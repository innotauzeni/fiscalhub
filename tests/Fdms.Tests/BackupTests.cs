using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace Fdms.Tests
{
    /// <summary>Database backups: operator only, a real dump on MySQL (and proof it restores), a clear refusal elsewhere.</summary>
    public class BackupTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        public BackupTests(AccessFactory f) => _f = f;

        [Fact]
        public async Task Backups_are_for_the_platform_operator_only()
        {
            var co = await TestKit.NewCompanyAsync(_f, "No Backup Co");
            foreach (var path in new[] { "/api/v1/backups" })
                Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsync("/api/v1/backups", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync($"/api/v1/backups/{Guid.NewGuid()}/download")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.DeleteAsync($"/api/v1/backups/{Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/v1/backups")).StatusCode);

            var op = await TestKit.AdminAsync(_f);
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/v1/backups")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/backups/{Guid.NewGuid()}/download")).StatusCode);
        }

        [Fact]
        public async Task A_backup_is_a_complete_sql_dump_that_restores_and_can_be_downloaded_and_deleted()
        {
            var op = await TestKit.AdminAsync(_f);
            var server = Environment.GetEnvironmentVariable("FDMS_TEST_MYSQL");
            if (string.IsNullOrWhiteSpace(server))
            {
                // the in-memory test database cannot be dumped: the answer says so plainly
                var res = await op.PostAsync("/api/v1/backups", null);
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
                Assert.Contains("BACKUP_UNSUPPORTED", await res.Content.ReadAsStringAsync());
                Assert.False((await op.GetFromJsonAsync<JsonElement>("/api/v1/backups", J)).GetProperty("info").GetProperty("supported").GetBoolean());
                Assert.Equal(HttpStatusCode.BadRequest, (await op.GetAsync("/api/v1/backups/schema")).StatusCode);
                return;
            }

            await TestKit.NewCompanyAsync(_f, "Backed Up Co");
            var made = await op.PostAsync("/api/v1/backups", null);
            Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
            var b = await made.Json();
            Assert.Equal("Ok", b.GetProperty("status").GetString());
            Assert.True(b.GetProperty("tables").GetInt32() > 20);
            Assert.True(b.GetProperty("rows").GetInt64() > 0);
            Assert.Equal(64, b.GetProperty("sha256").GetString()!.Length);
            Assert.True(b.GetProperty("fileExists").GetBoolean());
            var id = b.GetProperty("id").GetGuid();

            // the structure script for a new server, and the dump as a plain .sql file (no gunzip needed)
            var schema = await op.GetAsync("/api/v1/backups/schema");
            Assert.True(schema.IsSuccessStatusCode, await schema.Content.ReadAsStringAsync());
            var script = await schema.Content.ReadAsStringAsync();
            Assert.Contains("CREATE TABLE", script); Assert.Contains("fdms_receipts", script); Assert.Contains("__EFMigrationsHistory", script);
            var plain = await op.GetAsync($"/api/v1/backups/{id}/download?plain=true");
            Assert.True(plain.IsSuccessStatusCode);
            Assert.StartsWith("--", (await plain.Content.ReadAsStringAsync()).TrimStart());
            Assert.EndsWith(".sql", plain.Content.Headers.ContentDisposition!.FileName!.Trim('"'));

            Assert.Equal(HttpStatusCode.Forbidden, (await (await TestKit.NewCompanyAsync(_f, "No Schema Co")).Admin.GetAsync("/api/v1/backups/schema")).StatusCode);

            var list = await op.GetFromJsonAsync<JsonElement>("/api/v1/backups", J);
            Assert.True(list.GetProperty("info").GetProperty("supported").GetBoolean());
            Assert.Contains(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);

            // download, verify the checksum, and read the SQL
            var dl = await op.GetAsync($"/api/v1/backups/{id}/download");
            Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
            var bytes = await dl.Content.ReadAsByteArrayAsync();
            Assert.Equal(b.GetProperty("sha256").GetString(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
            string sql;
            using (var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)) using (var rd = new StreamReader(gz, Encoding.UTF8)) sql = await rd.ReadToEndAsync();
            Assert.Contains("CREATE TABLE", sql);
            Assert.Contains("INSERT INTO", sql);
            Assert.Contains(TestKit.AdminEmail, sql);                 // the seeded administrator's row is in it

            // it really restores: load it into an empty scratch database and compare the table count
            var scratch = "fdms_restore_" + Guid.NewGuid().ToString("N")[..8];
            await using (var admin = new MySqlConnection(server.TrimEnd(';') + ";SslMode=None;"))
            {
                await admin.OpenAsync();
                await new MySqlCommand($"CREATE DATABASE `{scratch}`", admin).ExecuteNonQueryAsync();
                try
                {
                    await using var conn = new MySqlConnection(server.TrimEnd(';') + $";Database={scratch};SslMode=None;AllowUserVariables=true;");
                    await conn.OpenAsync();
                    await new MySqlCommand(sql, conn) { CommandTimeout = 300 }.ExecuteNonQueryAsync();
                    var restored = Convert.ToInt32(await new MySqlCommand("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'", conn).ExecuteScalarAsync());
                    Assert.Equal(b.GetProperty("tables").GetInt32(), restored);
                    var users = Convert.ToInt32(await new MySqlCommand("SELECT COUNT(*) FROM fdms_users", conn).ExecuteScalarAsync());
                    Assert.True(users >= 1);
                }
                finally { await new MySqlCommand($"DROP DATABASE IF EXISTS `{scratch}`", admin).ExecuteNonQueryAsync(); }
            }

            // delete removes the record and the file
            Assert.Equal(HttpStatusCode.NoContent, (await op.DeleteAsync($"/api/v1/backups/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/backups/{id}/download")).StatusCode);
            Assert.DoesNotContain((await op.GetFromJsonAsync<JsonElement>("/api/v1/backups", J)).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
        }
    }
}
