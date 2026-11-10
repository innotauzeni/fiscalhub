using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Fiscal;
using Fdms.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Fdms.Tests
{
    /// <summary>Timezone safety, the "who did this" audit trail, and the system error log.</summary>
    public class LogsAuditTimeTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 18000;
        public LogsAuditTimeTests(AccessFactory f) => _f = f;

        private static object Invoice(string extRef) => new
        {
            externalRef = extRef, invoiceNo = "INV-" + extRef, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
            lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } },
        };

        // ================================================================================================ time

        [Fact]
        public void Zimbabwe_time_comes_from_the_UTC_clock_and_carries_no_zone()
        {
            var cat = FdmsClock.CatNow();
            Assert.Equal(DateTimeKind.Unspecified, cat.Kind);                                     // a wall-clock reading, never mistaken for UTC
            var expected = DateTime.UtcNow.AddHours(2);
            Assert.InRange((cat - expected).TotalSeconds, -5, 5);                                 // UTC+2 whatever the server's own timezone is
        }

        [Fact]
        public void No_code_reads_the_servers_local_clock()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Fdms.Application"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var forbidden = new[] { "DateTime.Now", "DateTime.Today", "DateTimeOffset.Now", ".ToLocalTime(", "TimeZoneInfo.Local" };
            var offenders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains("Migrations"))
                .SelectMany(p => File.ReadLines(p).Select((line, i) => (p, i, line)).Where(x => !x.line.TrimStart().StartsWith("//") && forbidden.Any(f => x.line.Contains(f))))
                .Select(x => $"{Path.GetFileName(x.p)}:{x.i + 1}").ToList();
            Assert.True(offenders.Count == 0, "Server-local time would make fiscal timestamps depend on the host timezone: " + string.Join(", ", offenders));
        }

        [Fact]
        public async Task Utc_moments_are_sent_with_a_Z_and_Zimbabwe_wall_clock_dates_are_not()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Time Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var token = await TestKit.NewTokenAsync(_f, co.Admin, dev, name: "till");
            var created = await token.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("TZ1"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var createdBody = await created.Json();
            var receiptId = createdBody.GetProperty("id").GetGuid();

            static string Raw(JsonElement e, string name) => e.GetProperty(name).GetString()!;

            // UTC: browsers convert these to the viewer's own timezone
            var tokens = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/tokens", J);
            Assert.EndsWith("Z", Raw(tokens[0], "createdAt"));
            Assert.EndsWith("Z", Raw(tokens[0], "lastUsedAt"));
            var users = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users", J);
            Assert.EndsWith("Z", Raw(users.EnumerateArray().First(u => u.GetProperty("email").GetString() == co.AdminEmail), "lastLoginDate"));
            var audit = await TestKit.AuditAsync(_f, co.ClientId, "");
            Assert.EndsWith("Z", Raw(audit.GetProperty("items")[0], "at"));

            // Zimbabwe wall-clock: shown as printed on the receipt, in any viewer's timezone
            var listed = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{receiptId}", J));
            Assert.DoesNotContain("Z", Raw(listed, "receiptDate"));
            Assert.DoesNotContain("+", Raw(listed, "receiptDate"));
            Assert.DoesNotContain("Z", Raw(createdBody, "receiptDate"));                         // the response of the very request that created it, too
            var day = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{dev}/day", J);
            Assert.DoesNotContain("Z", Raw(day, "openedAt"));

            // and the wall-clock really is Zimbabwe time: within a minute of UTC+2
            var printed = DateTime.Parse(Raw(listed, "receiptDate"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None);
            Assert.InRange((printed - DateTime.UtcNow.AddHours(2)).TotalMinutes, -2, 2);
        }

        // ================================================================================================ audit

        /// <summary>The API leaves out empty properties, so a row without a record has no entityId at all.</summary>
        private static string? Eid(JsonElement r) => r.TryGetProperty("entityId", out var v) ? v.GetString() : null;

        [Fact]
        public async Task The_audit_trail_names_who_did_what_to_which_record()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Who Co");
            var other = await TestKit.NewCompanyAsync(_f, "Other Who Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var token = await TestKit.NewTokenAsync(_f, co.Admin, dev, name: "POS till 7");

            // a system integrating with a token, and a person at the console
            var byToken = await token.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("W1"));
            var tokenReceipt = (await byToken.Json()).GetProperty("id").GetString();
            var byUser = await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("W2"));
            var userReceipt = (await byUser.Json()).GetProperty("id").GetString();

            var rows = (await TestKit.AuditAsync(_f, co.ClientId, "action=receipt&pageSize=100")).GetProperty("items").EnumerateArray().ToList();
            var t = rows.First(r => Eid(r) == tokenReceipt);
            Assert.Equal("receipt.submit", t.GetProperty("action").GetString());
            Assert.Equal("token", t.GetProperty("actorType").GetString());
            Assert.Contains("POS till 7", t.GetProperty("userName").GetString());                    // which integration did it
            Assert.Equal("Receipt", t.GetProperty("entity").GetString());
            Assert.Contains("INV-W1", t.GetProperty("detail").GetString());
            var u = rows.First(r => Eid(r) == userReceipt);
            Assert.Equal("user", u.GetProperty("actorType").GetString());
            Assert.Equal(co.AdminEmail, u.GetProperty("userName").GetString());                      // which person did it

            // sign-ins: success and failure both leave a trail with the person named
            await TestKit.LoginAsync(_f, co.AdminEmail, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = co.AdminEmail, password = "Wrong-Password-1" })).StatusCode);
            await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = "nobody@" + Guid.NewGuid().ToString("N")[..6] + ".test", password = "x" });
            var auth = (await TestKit.AuditAsync(_f, co.ClientId, "action=auth&pageSize=100")).GetProperty("items").EnumerateArray().ToList();
            Assert.Contains(auth, r => r.GetProperty("action").GetString() == "auth.login" && r.GetProperty("userName").GetString() == co.AdminEmail);
            Assert.Contains(auth, r => r.GetProperty("action").GetString() == "auth.login-failed" && r.GetProperty("userName").GetString() == co.AdminEmail);

            // filters: actor, actor type, free text, date range, paging, the list of actions
            var tokenOnly = await TestKit.AuditAsync(_f, co.ClientId, "actorType=token&pageSize=100");
            Assert.All(tokenOnly.GetProperty("items").EnumerateArray(), r => Assert.Equal("token", r.GetProperty("actorType").GetString()));
            Assert.NotEmpty(tokenOnly.GetProperty("items").EnumerateArray());
            Assert.Contains((await TestKit.AuditAsync(_f, co.ClientId, "actor=till%207")).GetProperty("items").EnumerateArray(), r => Eid(r) == tokenReceipt);
            var future = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
            Assert.Empty((await TestKit.AuditAsync(_f, co.ClientId, $"from={future}")).GetProperty("items").EnumerateArray());
            var paged = await TestKit.AuditAsync(_f, co.ClientId, "pageSize=2&page=2");
            Assert.Equal(2, paged.GetProperty("page").GetInt32());
            Assert.True(paged.GetProperty("total").GetInt32() > 2 && paged.GetProperty("items").GetArrayLength() <= 2);
            Assert.Contains("receipt.submit", paged.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));

            // a company never sees another company's trail
            var theirs = await TestKit.AuditAsync(_f, other.ClientId, "pageSize=200");
            Assert.DoesNotContain(theirs.GetProperty("items").EnumerateArray(), r => Eid(r) == tokenReceipt);
        }

        // ================================================================================================ system log

        private async Task<JsonElement> LogsAsync(HttpClient op, string query = "")
        {
            await _f.Services.GetRequiredService<ErrorLogFlusher>().FlushAsync(CancellationToken.None);   // the background writer is off in tests
            return await op.GetFromJsonAsync<JsonElement>("/api/v1/system-logs?pageSize=200" + query, J);
        }

        [Fact]
        public async Task Errors_land_in_the_system_log_with_details_and_repeats_are_counted_once()
        {
            var op = await TestKit.AdminAsync(_f);
            var log = _f.Services.GetRequiredService<Serilog.ILogger>().ForContext("SourceContext", "Fdms.Tests.Boom");
            var tag = "tag" + Guid.NewGuid().ToString("N")[..8];

            try { throw new InvalidOperationException("kaboom " + tag); }
            catch (Exception ex) { log.Error(ex, "Receipt delivery failed for device {Device} ({Tag})", 12345, tag); }
            try { throw new InvalidOperationException("kaboom " + tag); }
            catch (Exception ex) { log.Error(ex, "Receipt delivery failed for device {Device} ({Tag})", 67890, tag); }   // same problem, different number
            log.Fatal("Out of disk space {Tag}", tag);
            log.Warning("Only a warning {Tag}", tag);                                                                     // below Error: not stored

            var all = await LogsAsync(op, "&search=" + tag);
            var items = all.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            var err = items.First(i => i.GetProperty("severity").GetString() == "Error");
            Assert.Equal(2, err.GetProperty("occurrences").GetInt32());
            Assert.Equal("System.InvalidOperationException", err.GetProperty("exceptionType").GetString());
            Assert.Contains("kaboom", err.GetProperty("stackTrace").GetString());
            Assert.Equal("Fdms.Tests.Boom", err.GetProperty("source").GetString());
            Assert.False(err.GetProperty("isResolved").GetBoolean());
            Assert.EndsWith("Z", err.GetProperty("createdAt").GetString());
            Assert.Equal("Critical", items.First(i => i.GetProperty("message").GetString()!.StartsWith("Out of disk")).GetProperty("severity").GetString());
            Assert.True(all.GetProperty("stats").GetProperty("unresolved").GetInt32() >= 2);
            Assert.True(all.GetProperty("stats").GetProperty("criticalUnresolved").GetInt32() >= 1);
        }

        [Fact]
        public async Task Log_entries_can_be_filtered_resolved_deleted_and_purged_by_the_platform_operator_only()
        {
            var op = await TestKit.AdminAsync(_f);
            var log = _f.Services.GetRequiredService<Serilog.ILogger>().ForContext("SourceContext", "Fdms.Tests.Workflow");
            var tag = "wf" + Guid.NewGuid().ToString("N")[..8];
            log.Error(new TimeoutException("slow " + tag), "ZIMRA call timed out {Tag}", tag);
            log.Error(new ArgumentException("bad " + tag), "Bad argument {Tag}", tag);

            var items = (await LogsAsync(op, "&search=" + tag)).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            var first = items.First(i => i.GetProperty("exceptionType").GetString()!.Contains("Timeout"));
            var id = first.GetProperty("id").GetInt64();
            var uuid = first.GetProperty("uuid").GetString();

            // search by the short reference, filter by severity and state
            Assert.Single((await LogsAsync(op, "&search=" + uuid)).GetProperty("items").EnumerateArray());
            Assert.Equal(2, (await LogsAsync(op, $"&search={tag}&severity=Error&resolved=false")).GetProperty("items").GetArrayLength());
            Assert.Empty((await LogsAsync(op, $"&search={tag}&severity=Warning")).GetProperty("items").EnumerateArray());

            // resolve one with a note: who and when are recorded
            var resolved = await (await op.PutAsJsonAsync($"/api/v1/system-logs/{id}/resolve", new { resolution = "ZIMRA was down; retried by the queue." })).Json();
            Assert.True(resolved.GetProperty("isResolved").GetBoolean());
            Assert.Equal("ZIMRA was down; retried by the queue.", resolved.GetProperty("resolution").GetString());
            Assert.Equal(TestKit.AdminEmail, resolved.GetProperty("resolvedBy").GetString());
            Assert.Single((await LogsAsync(op, $"&search={tag}&resolved=true")).GetProperty("items").EnumerateArray());

            // a recurrence of a RESOLVED error is a new open entry (it came back)
            log.Error(new TimeoutException("slow " + tag), "ZIMRA call timed out {Tag}", tag);
            Assert.Equal(2, (await LogsAsync(op, $"&search={tag}&severity=Error&resolved=false")).GetProperty("items").GetArrayLength());

            // delete one, resolve the rest, purge the resolved
            Assert.Equal(HttpStatusCode.NoContent, (await op.DeleteAsync($"/api/v1/system-logs/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/system-logs/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await op.PostAsync("/api/v1/system-logs/resolve-all", null)).StatusCode);
            Assert.Empty((await LogsAsync(op, "&resolved=false")).GetProperty("items").EnumerateArray());
            var purged = await (await op.DeleteAsync("/api/v1/system-logs/resolved")).Json();
            Assert.True(purged.GetProperty("deleted").GetInt32() >= 2);
            Assert.Empty((await LogsAsync(op, "&search=" + tag)).GetProperty("items").EnumerateArray());

            // the actions themselves are audited
            var trail = (await op.GetFromJsonAsync<JsonElement>("/api/v1/audit/search?action=systemlog&pageSize=50", J)).GetProperty("items").EnumerateArray().Select(r => r.GetProperty("action").GetString()).ToList();
            Assert.Contains("systemlog.resolve", trail); Assert.Contains("systemlog.delete", trail); Assert.Contains("systemlog.purge", trail);

            // company administrators have no access to the platform's error log
            var co = await TestKit.NewCompanyAsync(_f, "No Logs Co");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/system-logs")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsync("/api/v1/system-logs/resolve-all", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.DeleteAsync("/api/v1/system-logs/resolved")).StatusCode);
        }
    }
}
