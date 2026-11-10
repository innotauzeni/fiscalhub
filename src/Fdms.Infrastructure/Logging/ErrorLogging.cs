using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Logging
{
    /// <summary>
    /// Hand-over point between the logger (which must never block or fail a fiscal request) and the database. The logging sink drops errors
    /// here and returns at once; <see cref="ErrorLogFlusher"/> stores them a moment later. If the queue is full the newest are dropped.
    /// </summary>
    public sealed class ErrorLogQueue
    {
        // one queue per application host (a singleton), so two hosts in one process never swap each other's errors
        private readonly Channel<ErrorLog> _chan = Channel.CreateBounded<ErrorLog>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = false });
        public bool TryWrite(ErrorLog e) => _chan.Writer.TryWrite(e);
        public bool TryRead(out ErrorLog? e) => _chan.Reader.TryRead(out e);

        private static readonly Regex Variable = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\d+", RegexOptions.Compiled);

        /// <summary>Same type, same message shape (numbers and ids ignored) and same path = the same problem, counted once.</summary>
        public static string Fingerprint(string? exceptionType, string message, string? path) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{exceptionType}|{Variable.Replace(message, "#")}|{path}"))).ToLowerInvariant()[..40];
    }

    /// <summary>Stores queued errors, merging repeats of an unresolved error into one row with a count.</summary>
    public sealed class ErrorLogFlusher : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ErrorLogQueue _queue;
        private readonly ILogger<ErrorLogFlusher> _log;
        public ErrorLogFlusher(IServiceScopeFactory scopes, ErrorLogQueue queue, ILogger<ErrorLogFlusher> log) { _scopes = scopes; _queue = queue; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), stop); await FlushAsync(stop); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "Could not store queued error logs"); }   // Warning on purpose: an Error here would feed itself
            }
            try { await FlushAsync(CancellationToken.None); } catch { /* shutting down */ }
        }

        public async Task<int> FlushAsync(CancellationToken ct)
        {
            var batch = new List<ErrorLog>();
            while (batch.Count < 500 && _queue.TryRead(out var e)) batch.Add(e!);
            if (batch.Count == 0) return 0;

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var group in batch.GroupBy(x => x.Fingerprint))
            {
                var first = group.First();
                var existing = await db.ErrorLogs.FirstOrDefaultAsync(x => x.Fingerprint == group.Key && !x.IsResolved && x.LastSeenAt >= cutoff, ct);
                if (existing != null) { existing.Occurrences += group.Count(); existing.LastSeenAt = group.Max(x => x.CreatedAt); continue; }
                first.Occurrences = group.Count();
                first.LastSeenAt = group.Max(x => x.CreatedAt);
                db.ErrorLogs.Add(first);
            }
            await db.SaveChangesAsync(ct);
            return batch.Count;
        }
    }

    /// <summary>Keeps the error log from growing forever: deletes entries older than Logs:ErrorRetentionDays (default 90).</summary>
    public sealed class LogRetentionService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _cfg;
        private readonly ILogger<LogRetentionService> _log;
        public LogRetentionService(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<LogRetentionService> log) { _scopes = scopes; _cfg = cfg; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try { await PurgeAsync(stop); await Task.Delay(TimeSpan.FromHours(12), stop); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "Error-log retention run failed"); await Task.Delay(TimeSpan.FromMinutes(30), stop); }
            }
        }

        public async Task<int> PurgeAsync(CancellationToken ct)
        {
            var days = Math.Max(1, _cfg.GetValue("Logs:ErrorRetentionDays", 90));
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var old = await db.ErrorLogs.Where(x => x.LastSeenAt < cutoff).Take(5000).ToListAsync(ct);
            db.ErrorLogs.RemoveRange(old);
            var activityCutoff = DateTime.UtcNow.AddDays(-Math.Max(7, _cfg.GetValue("Logs:ActivityRetentionDays", 90)));
            db.ApiActivities.RemoveRange(await db.ApiActivities.Where(x => x.At < activityCutoff).Take(5000).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            return old.Count;
        }
    }

    public sealed class ErrorLogService : IErrorLogService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        public ErrorLogService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        public async Task<ErrorLogPageDto> SearchAsync(ErrorLogSearch f, CancellationToken ct)
        {
            var q = _db.ErrorLogs.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(f.Search))
            {
                var s = f.Search.Trim();
                q = q.Where(x => x.Message.Contains(s) || (x.Source != null && x.Source.Contains(s)) || (x.Path != null && x.Path.Contains(s)) || (x.ExceptionType != null && x.ExceptionType.Contains(s)) || x.Uuid == s);
            }
            if (f.Severity is "Critical" or "Error" or "Warning" or "Info") q = q.Where(x => x.Severity == f.Severity);
            if (f.Resolved.HasValue) q = q.Where(x => x.IsResolved == f.Resolved.Value);
            if (f.From.HasValue) q = q.Where(x => x.LastSeenAt >= f.From.Value);
            if (f.To.HasValue) q = q.Where(x => x.LastSeenAt < f.To.Value);

            var size = Math.Clamp(f.PageSize, 1, 200);
            var page = Math.Max(1, f.Page);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(x => x.LastSeenAt).ThenByDescending(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
            return new ErrorLogPageDto(await ToDtos(rows, ct), page, size, total, await StatsAsync(ct));
        }

        private async Task<ErrorLogStats> StatsAsync(CancellationToken ct)
        {
            var today = DateTime.UtcNow.Date;
            var all = _db.ErrorLogs.AsNoTracking();
            return new ErrorLogStats(await all.CountAsync(x => !x.IsResolved, ct), await all.CountAsync(x => !x.IsResolved && x.Severity == "Critical", ct),
                await all.CountAsync(x => x.LastSeenAt >= today, ct), await all.CountAsync(ct));
        }

        public async Task<ErrorLogDto> GetAsync(long id, CancellationToken ct)
        {
            var row = await _db.ErrorLogs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Log entry");
            return (await ToDtos(new[] { row }, ct))[0];
        }

        public async Task<ErrorLogDto> ResolveAsync(long id, string? resolution, CancellationToken ct)
        {
            var row = await _db.ErrorLogs.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Log entry");
            var note = resolution?.Trim();
            if (note is { Length: > 1000 }) throw GatewayException.BadRequest("NOTE_TOO_LONG", "The note may be at most 1000 characters.");
            row.IsResolved = true; row.Resolution = string.IsNullOrEmpty(note) ? null : note; row.ResolvedAt = DateTime.UtcNow; row.ResolvedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { Action = "systemlog.resolve", Entity = "ErrorLog", EntityId = row.Uuid, Detail = Short(row.Message) });
            await _db.SaveChangesAsync(ct);
            return await GetAsync(id, ct);
        }

        public async Task<int> ResolveAllAsync(CancellationToken ct)
        {
            var open = await _db.ErrorLogs.Where(x => !x.IsResolved).ToListAsync(ct);
            foreach (var x in open) { x.IsResolved = true; x.ResolvedAt = DateTime.UtcNow; x.ResolvedBy = _scope.UserName; x.Resolution ??= "Resolved in bulk"; }
            if (open.Count > 0) _db.AuditLogs.Add(new AuditLog { Action = "systemlog.resolve-all", Detail = $"{open.Count} entries" });
            await _db.SaveChangesAsync(ct);
            return open.Count;
        }

        public async Task DeleteAsync(long id, CancellationToken ct)
        {
            var row = await _db.ErrorLogs.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Log entry");
            _db.ErrorLogs.Remove(row);
            _db.AuditLogs.Add(new AuditLog { Action = "systemlog.delete", Entity = "ErrorLog", EntityId = row.Uuid, Detail = Short(row.Message) });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<int> PurgeResolvedAsync(CancellationToken ct)
        {
            var done = await _db.ErrorLogs.Where(x => x.IsResolved).ToListAsync(ct);
            _db.ErrorLogs.RemoveRange(done);
            if (done.Count > 0) _db.AuditLogs.Add(new AuditLog { Action = "systemlog.purge", Detail = $"{done.Count} resolved entries deleted" });
            await _db.SaveChangesAsync(ct);
            return done.Count;
        }

        private static string Short(string s) => s.Length <= 200 ? s : s[..200];

        private async Task<IReadOnlyList<ErrorLogDto>> ToDtos(IEnumerable<ErrorLog> rows, CancellationToken ct)
        {
            var clients = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            return rows.Select(x => new ErrorLogDto(x.Id, x.Uuid, x.Severity, x.Source, x.Message, x.ExceptionType, x.StackTrace, x.Path, x.Method, x.StatusCode, x.IpAddress, x.UserName,
                x.ClientId, x.ClientId.HasValue ? clients.GetValueOrDefault(x.ClientId.Value) : null, x.TraceId, x.MachineName, x.Environment, x.Occurrences, x.IsResolved, x.Resolution,
                x.ResolvedBy, x.ResolvedAt, x.CreatedAt, x.LastSeenAt)).ToList();
        }
    }
}
