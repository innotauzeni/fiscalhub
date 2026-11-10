using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The activity trail a company can read: every API call its people and integrating systems make, per device. Callers who are limited to some devices
    /// (branch staff) only ever see those devices, exactly as everywhere else.
    /// </summary>
    public sealed class ActivityService : IActivityService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        public ActivityService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        public async Task RecordAsync(ApiActivityEntry e, CancellationToken ct)
        {
            try
            {
                static string? Cut(string? s, int n) => s == null ? null : s.Length <= n ? s : s[..n];
                _db.ApiActivities.Add(new ApiActivity
                {
                    ClientId = e.ClientId, DeviceId = e.DeviceId, ApiKeyId = e.ApiKeyId, UserId = e.UserId, ActorName = Cut(e.ActorName, 200), ActorType = e.ActorType,
                    Method = Cut(e.Method, 10)!, Path = Cut(e.Path, 300)!, StatusCode = e.StatusCode, DurationMs = e.DurationMs, ErrorCode = Cut(e.ErrorCode, 60),
                    IpAddress = Cut(e.IpAddress, 64), UserAgent = Cut(e.UserAgent, 200),
                });
                await _db.SaveChangesAsync(ct);
            }
            catch { /* tracking must never break a request */ }
        }

        private IQueryable<ApiActivity> Scoped(Guid? callerClientId, Guid? clientFilter)
        {
            var q = _db.ApiActivities.AsNoTracking().AsQueryable();
            if (callerClientId != null) q = q.Where(a => a.ClientId == callerClientId);
            else if (clientFilter != null) q = q.Where(a => a.ClientId == clientFilter);
            if (_scope.AllowedDeviceIds is { } allowed) { var ids = allowed.ToList(); q = q.Where(a => a.DeviceId != null && ids.Contains(a.DeviceId.Value)); }
            return q;
        }

        public async Task<ActivityPage> SearchAsync(Guid? callerClientId, ActivitySearch s, CancellationToken ct)
        {
            var q = Scoped(callerClientId, s.ClientId);
            if (s.DeviceId != null) q = q.Where(a => a.DeviceId == s.DeviceId);
            if (!string.IsNullOrWhiteSpace(s.ActorType)) q = q.Where(a => a.ActorType == s.ActorType);
            if (s.Outcome == "errors") q = q.Where(a => a.StatusCode >= 400);
            else if (s.Outcome == "ok") q = q.Where(a => a.StatusCode < 400);
            if (s.From != null) q = q.Where(a => a.At >= s.From);
            if (s.To != null) q = q.Where(a => a.At < s.To.Value.AddDays(1));
            if (!string.IsNullOrWhiteSpace(s.Search)) { var t = s.Search.Trim(); q = q.Where(a => a.Path.Contains(t) || (a.ActorName != null && a.ActorName.Contains(t)) || (a.IpAddress != null && a.IpAddress.Contains(t))); }

            var page = Math.Max(1, s.Page); var size = Math.Clamp(s.PageSize, 1, 200);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);

            var deviceIds = rows.Where(r => r.DeviceId != null).Select(r => r.DeviceId!.Value).Distinct().ToList();
            var zimra = await _db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.ZimraDeviceId, ct);
            var clientIds = rows.Select(r => r.ClientId).Distinct().ToList();
            var names = callerClientId == null ? await _db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct) : new Dictionary<Guid, string>();
            return new ActivityPage(rows.Select(r => new ActivityDto(r.Id, r.ClientId, names.GetValueOrDefault(r.ClientId), r.DeviceId, r.DeviceId != null && zimra.TryGetValue(r.DeviceId.Value, out var z) ? z : null,
                r.ActorType, r.ActorName, r.Method, r.Path, r.StatusCode, r.DurationMs, r.ErrorCode, r.IpAddress, r.UserAgent, r.At)).ToList(), total, page, size);
        }

        public async Task<IReadOnlyList<DeviceActivityDto>> DevicesAsync(Guid? callerClientId, Guid? clientId, CancellationToken ct)
        {
            var devices = _db.Devices.AsNoTracking().AsQueryable();
            if (callerClientId != null) devices = devices.Where(d => d.ClientId == callerClientId);
            else if (clientId != null) devices = devices.Where(d => d.ClientId == clientId);
            if (_scope.AllowedDeviceIds is { } allowed) { var ids = allowed.ToList(); devices = devices.Where(d => ids.Contains(d.Id)); }
            var list = await devices.OrderBy(d => d.ZimraDeviceId).ToListAsync(ct);
            var devIds = list.Select(d => d.Id).ToList();
            var since = DateTime.UtcNow.AddHours(-24);

            var rows = await _db.ApiActivities.AsNoTracking().Where(a => a.DeviceId != null && devIds.Contains(a.DeviceId.Value) && a.At >= since)
                .Select(a => new { DeviceId = a.DeviceId!.Value, a.StatusCode, a.ActorType, a.ActorName, a.Method, a.Path, a.At }).ToListAsync(ct);
            var lastEver = await _db.ApiActivities.AsNoTracking().Where(a => a.DeviceId != null && devIds.Contains(a.DeviceId.Value))
                .GroupBy(a => a.DeviceId!.Value).Select(g => new { DeviceId = g.Key, Last = g.Max(x => x.At) }).ToListAsync(ct);
            var lastToken = await _db.ApiActivities.AsNoTracking().Where(a => a.DeviceId != null && devIds.Contains(a.DeviceId.Value) && a.ActorType == "token")
                .GroupBy(a => a.DeviceId!.Value).Select(g => new { DeviceId = g.Key, Last = g.Max(x => x.At) }).ToListAsync(ct);
            var tokens = await _db.ApiKeys.AsNoTracking().Where(k => devIds.Contains(k.DeviceId) && k.RevokedAt == null).GroupBy(k => k.DeviceId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            var branches = await _db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Name, ct);

            return list.Select(d =>
            {
                var mine = rows.Where(r => r.DeviceId == d.Id).ToList();
                var latest = mine.OrderByDescending(r => r.At).FirstOrDefault();
                return new DeviceActivityDto(d.Id, d.ZimraDeviceId, d.Name ?? d.BranchName ?? d.ModelName, d.BranchId != null ? branches.GetValueOrDefault(d.BranchId.Value) : null, d.IsActive,
                    lastEver.FirstOrDefault(x => x.DeviceId == d.Id)?.Last, lastToken.FirstOrDefault(x => x.DeviceId == d.Id)?.Last,
                    mine.Count, mine.Count(r => r.StatusCode >= 400), mine.Count(r => r.Method == "POST" && r.StatusCode < 300 && r.Path.EndsWith("/receipts", StringComparison.OrdinalIgnoreCase)),
                    tokens.FirstOrDefault(t => t.Key == d.Id)?.N ?? 0, latest?.ActorName);
            }).ToList();
        }
    }
}
