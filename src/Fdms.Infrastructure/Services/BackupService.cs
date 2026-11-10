using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Database backups: a consistent (single snapshot) SQL dump of every table, gzip-compressed, written outside the web root. It uses the database driver only, so no
    /// mysqldump program is needed on the server. Restoring is deliberately NOT an API call: stop the gateway and load the file with the mysql client (see the docs).
    /// File names are generated here and looked up by id, so no caller-supplied path is ever touched.
    /// </summary>
    public sealed class BackupService : IBackupService
    {
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IConfiguration _cfg;
        private readonly ILogger<BackupService> _log;
        private readonly INotificationService _notify;
        /// <summary>The last line of every complete dump: a file without it was cut short (disk full, crash) and is not trusted.</summary>
        public const string EndMarker = "-- FDMS backup complete";
        public BackupService(FdmsDbContext db, ICallerScope scope, IConfiguration cfg, ILogger<BackupService> log, INotificationService notify) { _db = db; _scope = scope; _cfg = cfg; _log = log; _notify = notify; }

        private string Folder => Path.GetFullPath(_cfg["Backup:Path"] is { Length: > 0 } p ? p : "backups");
        private int IntervalHours => Math.Clamp(_cfg.GetValue("Backup:IntervalHours", 24), 1, 24 * 30);
        private int KeepLast => Math.Clamp(_cfg.GetValue("Backup:KeepLast", 14), 1, 365);
        private bool ScheduleEnabled => _cfg.GetValue("Backup:Enabled", true);
        /// <summary>A second place (another disk, a network share, a synced cloud folder) every good backup is copied to, so one failed disk does not take the backups with it.</summary>
        private string? MirrorFolder => _cfg["Backup:MirrorPath"] is { Length: > 0 } m ? Path.GetFullPath(m) : null;
        private bool Supported => _db.Database.IsMySql();

        public async Task<BackupInfoDto> InfoAsync(CancellationToken ct)
        {
            var last = await _db.DatabaseBackups.AsNoTracking().Where(b => b.Status == "Ok").OrderByDescending(b => b.CreatedAt).Select(b => (DateTime?)b.CreatedAt).FirstOrDefaultAsync(ct);
            return new BackupInfoDto(Supported, Supported ? "MySQL" : "Other", Folder, ScheduleEnabled && Supported, IntervalHours, KeepLast, last, ScheduleEnabled && Supported ? (last ?? DateTime.UtcNow).AddHours(IntervalHours) : null);
        }

        private BackupDto ToDto(DatabaseBackup b) => new(b.Id, b.FileName, b.SizeBytes, b.Kind, b.Status, b.Error, b.Tables, b.Rows, b.Sha256, b.CreatedBy, b.CreatedAt, b.FinishedAt, File.Exists(Path.Combine(Folder, b.FileName)));

        public async Task<IReadOnlyList<BackupDto>> ListAsync(CancellationToken ct) =>
            (await _db.DatabaseBackups.AsNoTracking().OrderByDescending(b => b.CreatedAt).Take(200).ToListAsync(ct)).Select(ToDto).ToList();

        public async Task<BackupDto> CreateAsync(string kind, CancellationToken ct)
        {
            if (!Supported) throw GatewayException.BadRequest("BACKUP_UNSUPPORTED", "Backups need a MySQL database.");
            if (!await OneAtATime.WaitAsync(0, ct)) throw GatewayException.Conflict("BACKUP_RUNNING", "A backup is already running.");
            var rec = new DatabaseBackup { Kind = kind == "scheduled" ? "scheduled" : "manual", CreatedBy = kind == "scheduled" ? "System" : _scope.UserName ?? "Operator", FileName = $"fdms-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.sql.gz" };
            try
            {
                Directory.CreateDirectory(Folder);
                _db.DatabaseBackups.Add(rec);
                await _db.SaveChangesAsync(ct);

                var path = Path.Combine(Folder, rec.FileName);
                var temp = path + ".part";
                try
                {
                    (rec.Tables, rec.Rows) = await DumpAsync(temp, ct);
                    File.Move(temp, path, true);
                    var info = new FileInfo(path);
                    rec.SizeBytes = info.Length;
                    using (var fs = File.OpenRead(path)) rec.Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                    await VerifyAsync(path, ct);   // read it back: it must decompress and end with the marker
                    rec.Status = "Ok";
                    var mirrorError = Mirror(path);
                    if (mirrorError != null) rec.Error = "Saved, but the copy to the mirror folder failed: " + mirrorError;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Database backup failed");
                    rec.Status = "Failed"; rec.Error = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
                    try { if (File.Exists(temp)) File.Delete(temp); if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
                }
                rec.FinishedAt = DateTime.UtcNow;
                _db.AuditLogs.Add(new AuditLog { Action = rec.Status == "Ok" ? "backup.create" : "backup.failed", Entity = "DatabaseBackup", EntityId = rec.Id.ToString(), Detail = $"{rec.FileName} {rec.Kind} {rec.Status} {rec.SizeBytes / 1024} KB, {rec.Tables} tables, {rec.Rows} rows{(rec.Error != null ? ": " + rec.Error : "")}" });
                await _db.SaveChangesAsync(CancellationToken.None);
                if (rec.Status != "Ok" || rec.Error != null)
                {
                    try
                    {
                        await _notify.RaisePlatformAsync("backup", "bad", $"backup-failed:{rec.Id}", rec.Status == "Ok" ? "Backup mirror copy failed" : "Database backup failed",
                            (rec.Error ?? "") + " Check the disk space and the backup folder, then run a backup by hand under Backups.", "/admin/backups", CancellationToken.None);
                    }
                    catch (Exception ex) { _log.LogError(ex, "Could not raise the backup alert"); }
                }
                return ToDto(rec);
            }
            finally { OneAtATime.Release(); }
        }

        /// <summary>Streams every base table as DROP/CREATE/INSERT statements inside one consistent-snapshot transaction.</summary>
        private async Task<(int Tables, long Rows)> DumpAsync(string target, CancellationToken ct)
        {
            await using var conn = new MySqlConnection(_db.Database.GetConnectionString());
            await conn.OpenAsync(ct);
            await using (var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct))
            {
                await using var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var gz = new GZipStream(fs, CompressionLevel.Optimal);
                await using var w = new StreamWriter(gz, new UTF8Encoding(false), 1 << 16);
                await w.WriteAsync($"-- FDMS gateway backup, {DateTime.UtcNow:O}\n-- Restore with the gateway stopped:  gunzip -c file.sql.gz | mysql -u USER -p DATABASE\nSET NAMES utf8mb4;\nSET FOREIGN_KEY_CHECKS=0;\nSET UNIQUE_CHECKS=0;\nSET SQL_MODE='NO_AUTO_VALUE_ON_ZERO';\n\n");

                var tables = new List<string>();
                await using (var cmd = new MySqlCommand("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME", conn, tx))
                await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) tables.Add(r.GetString(0));

                long rows = 0;
                foreach (var t in tables)
                {
                    var q = "`" + t.Replace("`", "``") + "`";
                    await w.WriteAsync($"-- {t}\nDROP TABLE IF EXISTS {q};\n");
                    await using (var cmd = new MySqlCommand("SHOW CREATE TABLE " + q, conn, tx))
                    await using (var r = await cmd.ExecuteReaderAsync(ct)) { if (await r.ReadAsync(ct)) await w.WriteAsync(r.GetString(1) + ";\n"); }

                    await using var sel = new MySqlCommand("SELECT * FROM " + q, conn, tx) { CommandTimeout = 600 };
                    await using var rd = await sel.ExecuteReaderAsync(ct);
                    var n = 0;
                    var sb = new StringBuilder();
                    while (await rd.ReadAsync(ct))
                    {
                        if (n % 200 == 0) { if (n > 0) await w.WriteAsync(";\n"); await w.WriteAsync($"INSERT INTO {q} VALUES\n"); } else await w.WriteAsync(",\n");
                        sb.Clear(); sb.Append('(');
                        for (var i = 0; i < rd.FieldCount; i++) { if (i > 0) sb.Append(','); sb.Append(Literal(rd, i)); }
                        sb.Append(')');
                        await w.WriteAsync(sb.ToString());
                        n++; rows++;
                    }
                    if (n > 0) await w.WriteAsync(";\n");
                    await w.WriteAsync("\n");
                }
                await w.WriteAsync("SET FOREIGN_KEY_CHECKS=1;\nSET UNIQUE_CHECKS=1;\n" + EndMarker + "\n");
                await w.FlushAsync();
                return (tables.Count, rows);
            }
        }

        /// <summary>Reads the whole file back through gzip and checks it ends with the marker, so a truncated or corrupt dump is reported as failed instead of trusted.</summary>
        public static async Task VerifyAsync(string path, CancellationToken ct)
        {
            await using var fs = File.OpenRead(path);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new StreamReader(gz, Encoding.UTF8);
            string? line, last = null;
            while ((line = await r.ReadLineAsync(ct)) != null) if (line.Length > 0) last = line;
            if (last != EndMarker) throw new InvalidDataException("The backup file is incomplete (it does not end with the completion marker).");
        }

        /// <summary>Copies a good backup to the mirror folder and keeps the newest KeepLast there. Returns why it failed, or null.</summary>
        private string? Mirror(string path)
        {
            var mirror = MirrorFolder;
            if (mirror == null) return null;
            try
            {
                Directory.CreateDirectory(mirror);
                var target = Path.Combine(mirror, Path.GetFileName(path));
                File.Copy(path, target + ".part", true);
                File.Move(target + ".part", target, true);
                foreach (var old in new DirectoryInfo(mirror).GetFiles("fdms-*.sql.gz").OrderByDescending(f => f.Name).Skip(KeepLast))
                    try { old.Delete(); } catch (IOException) { /* in use: next time */ }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogError(ex, "Copying the backup to {Mirror} failed", mirror);
                return ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            }
        }

        private static string Literal(System.Data.Common.DbDataReader r, int i)
        {
            if (r.IsDBNull(i)) return "NULL";
            var v = r.GetValue(i);
            return v switch
            {
                bool b => b ? "1" : "0",
                byte[] bytes => bytes.Length == 0 ? "''" : "0x" + Convert.ToHexString(bytes),
                DateTime d => "'" + d.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "'",
                MySqlDateTime d => "'" + (d.IsValidDateTime ? d.GetDateTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) : "0000-00-00 00:00:00") + "'",
                TimeSpan t => "'" + t.ToString(@"hh\:mm\:ss\.ffffff", CultureInfo.InvariantCulture) + "'",
                decimal m => m.ToString(CultureInfo.InvariantCulture),
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                float f => f.ToString("R", CultureInfo.InvariantCulture),
                sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(v, CultureInfo.InvariantCulture)!,
                Guid g => "'" + g.ToString() + "'",
                _ => "'" + MySqlHelper.EscapeString(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "") + "'",
            };
        }

        public async Task<(string Path, string FileName)> GetFileAsync(Guid id, CancellationToken ct)
        {
            var b = await _db.DatabaseBackups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Backup");
            var path = Path.Combine(Folder, Path.GetFileName(b.FileName));
            if (b.Status != "Ok" || !File.Exists(path)) throw GatewayException.NotFound("Backup file");
            _db.AuditLogs.Add(new AuditLog { Action = "backup.download", Entity = "DatabaseBackup", EntityId = b.Id.ToString(), Detail = b.FileName });
            await _db.SaveChangesAsync(ct);
            return (path, b.FileName);
        }

        public async Task<(string FileName, string Sql)> SchemaScriptAsync(CancellationToken ct)
        {
            if (!Supported) throw GatewayException.BadRequest("BACKUP_UNSUPPORTED", "The structure script needs a MySQL database.");
            var sql = _db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
            _db.AuditLogs.Add(new AuditLog { Action = "backup.schema", Entity = "Database", Detail = "downloaded the database structure script" });
            await _db.SaveChangesAsync(ct);
            return ($"fdms-structure-{DateTime.UtcNow:yyyyMMdd}.sql", sql);
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            var b = await _db.DatabaseBackups.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Backup");
            if (b.Status == "Running" && DateTime.UtcNow - b.CreatedAt < TimeSpan.FromHours(2)) throw GatewayException.Conflict("BACKUP_RUNNING", "That backup is still running.");
            try { var path = Path.Combine(Folder, Path.GetFileName(b.FileName)); if (File.Exists(path)) File.Delete(path); } catch (IOException ex) { throw GatewayException.Conflict("FILE_IN_USE", ex.Message); }
            _db.DatabaseBackups.Remove(b);
            _db.AuditLogs.Add(new AuditLog { Action = "backup.delete", Entity = "DatabaseBackup", EntityId = b.Id.ToString(), Detail = b.FileName });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<bool> RunScheduledAsync(CancellationToken ct)
        {
            if (!ScheduleEnabled || !Supported) return false;
            var last = await _db.DatabaseBackups.AsNoTracking().Where(b => b.Status == "Ok").OrderByDescending(b => b.CreatedAt).Select(b => (DateTime?)b.CreatedAt).FirstOrDefaultAsync(ct);
            var lastTry = await _db.DatabaseBackups.AsNoTracking().OrderByDescending(b => b.CreatedAt).Select(b => (DateTime?)b.CreatedAt).FirstOrDefaultAsync(ct);
            // after a failure wait an hour before trying again instead of failing every minute
            if ((last != null && DateTime.UtcNow - last < TimeSpan.FromHours(IntervalHours)) || (lastTry != null && DateTime.UtcNow - lastTry < TimeSpan.FromHours(1))) { await TrimAsync(ct); return false; }
            var made = await CreateAsync("scheduled", ct);
            await TrimAsync(ct);
            return made.Status == "Ok";
        }

        /// <summary>Keeps the newest KeepLast good backups (manual ones count) and removes older ones and their files; failed attempts older than a week go too.</summary>
        private async Task TrimAsync(CancellationToken ct)
        {
            var ok = await _db.DatabaseBackups.Where(b => b.Status == "Ok").OrderByDescending(b => b.CreatedAt).Skip(KeepLast).ToListAsync(ct);
            var failed = await _db.DatabaseBackups.Where(b => b.Status != "Ok" && b.CreatedAt < DateTime.UtcNow.AddDays(-7)).ToListAsync(ct);
            foreach (var b in ok.Concat(failed))
            {
                try { var p = Path.Combine(Folder, Path.GetFileName(b.FileName)); if (File.Exists(p)) File.Delete(p); } catch { /* the record is still removed; a locked file is left for next time */ }
                _db.DatabaseBackups.Remove(b);
            }
            if (ok.Count + failed.Count > 0) await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Makes the scheduled database backup. Checks every 15 minutes whether one is due.</summary>
    public sealed class BackupJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<BackupJob> _log;
        public BackupJob(IServiceScopeFactory scopes, ILogger<BackupJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    if (await scope.ServiceProvider.GetRequiredService<IBackupService>().RunScheduledAsync(stop)) _log.LogInformation("Scheduled database backup written");
                    await Task.Delay(TimeSpan.FromMinutes(15), stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Scheduled backup run failed"); try { await Task.Delay(TimeSpan.FromMinutes(30), stop); } catch (OperationCanceledException) { break; } }
            }
        }
    }
}
