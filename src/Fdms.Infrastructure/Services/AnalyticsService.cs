using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// How the platform is being used, for the operator: receipts per day and per company, rejections, and which companies look like they are slipping away. The "at risk" verdict is a
    /// plain list of rules (no receipts after earlier activity, a falling trend, many rejections, receipts stuck pending, an unpaid or expiring subscription, nobody signing in) and every
    /// company shows the reasons, so the operator knows whom to phone and why.
    /// </summary>
    public sealed class AnalyticsService : IAnalyticsService
    {
        private readonly FdmsDbContext _db;
        private readonly IBillingService _billing;
        public AnalyticsService(FdmsDbContext db, IBillingService billing) { _db = db; _billing = billing; }

        public async Task<PlatformUsageDto> UsageAsync(int days, CancellationToken ct)
        {
            days = Math.Clamp(days, 7, 180);
            var to = DateTime.UtcNow.Date.AddDays(1);
            var from = to.AddDays(-days);
            var prevFrom = from.AddDays(-days);

            var cur = await _db.Receipts.AsNoTracking().Where(r => r.CreatedAt >= from && r.CreatedAt < to && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held)
                .GroupBy(r => r.ClientId).Select(g => new { Id = g.Key, N = g.Count(), Rejected = g.Count(x => x.Status == ReceiptStatus.Rejected), Pending = g.Count(x => x.Status == ReceiptStatus.Pending) }).ToDictionaryAsync(x => x.Id, ct);
            var prev = await _db.Receipts.AsNoTracking().Where(r => r.CreatedAt >= prevFrom && r.CreatedAt < from && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held)
                .GroupBy(r => r.ClientId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
            var last = await _db.Receipts.AsNoTracking().GroupBy(r => r.ClientId).Select(g => new { Id = g.Key, At = g.Max(x => x.CreatedAt) }).ToDictionaryAsync(x => x.Id, x => x.At, ct);
            var daily = await _db.Receipts.AsNoTracking().Where(r => r.CreatedAt >= from && r.CreatedAt < to && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held)
                .GroupBy(r => r.CreatedAt.Date).Select(g => new { Day = g.Key, N = g.Count(), Rejected = g.Count(x => x.Status == ReceiptStatus.Rejected) }).ToDictionaryAsync(x => x.Day, ct);
            var logins = await _db.Users.AsNoTracking().Where(u => u.ClientId != null && u.LastLoginDate != null).GroupBy(u => u.ClientId!.Value).Select(g => new { Id = g.Key, At = g.Max(x => x.LastLoginDate) }).ToDictionaryAsync(x => x.Id, x => x.At, ct);
            var devices = (await _db.Devices.AsNoTracking().Select(d => d.ClientId).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var clients = await _db.Clients.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
            var subs = (await _db.Subscriptions.AsNoTracking().ToListAsync(ct)).GroupBy(s => s.ClientId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedAt).First());
            var plans = await _db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var partners = await _db.Partners.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);

            var now = DateTime.UtcNow;
            var rows = new List<UsageCompanyDto>();
            foreach (var c in clients)
            {
                subs.TryGetValue(c.Id, out var sub);
                cur.TryGetValue(c.Id, out var cu);
                var n = cu?.N ?? 0; var rej = cu?.Rejected ?? 0; var pend = cu?.Pending ?? 0; var before = prev.GetValueOrDefault(c.Id);
                var dev = devices.GetValueOrDefault(c.Id);
                var access = await _billing.GetAccessAsync(c.Id, ct);
                var status = !c.IsActive ? "Suspended" : access.Locked ? "Locked" : sub?.Status ?? "No plan";
                var high = new List<string>(); var medium = new List<string>();
                if (c.IsActive)
                {
                    if (access.Locked) high.Add(access.Reason == "EXPIRED" ? "Subscription expired" : access.Reason == "TRIAL_EXPIRED" ? "Trial ended without a plan" : "First invoice not paid");
                    else if (sub?.Status == "PastDue") high.Add("Subscription payment is overdue");
                    if (n == 0 && before > 0) high.Add($"No receipts in the last {days} days (had {before} before)");
                    else if (before >= 10 && n < before / 2) medium.Add($"Receipts fell from {before} to {n}");
                    if (n >= 10 && rej * 10 >= n) medium.Add($"{rej} of {n} receipts were rejected");
                    if (pend >= 5) medium.Add($"{pend} receipts are stuck pending");
                    if (sub?.Status == "Trial" && sub.TrialEndsAt is { } te && te > now && te < now.AddDays(7)) medium.Add($"Trial ends in {Math.Max(1, (int)Math.Ceiling((te - now).TotalDays))} day(s)");
                    if (dev == 0 && c.CreatedAt < now.AddDays(-7)) medium.Add("No device registered yet");
                    if (c.CreatedAt < now.AddDays(-30) && (!logins.TryGetValue(c.Id, out var li) || li < now.AddDays(-30))) medium.Add("Nobody has signed in for 30 days");
                }
                var risk = high.Count > 0 ? "High" : medium.Count > 0 ? "Medium" : "Low";
                rows.Add(new UsageCompanyDto(c.Id, c.Name, status, sub == null ? null : plans.GetValueOrDefault(sub.PlanId), c.PartnerId is { } pid ? partners.GetValueOrDefault(pid) : null, dev, n, before, rej, pend,
                    last.TryGetValue(c.Id, out var l) ? l : null, logins.TryGetValue(c.Id, out var lg) ? lg : null, risk, high.Concat(medium).ToList(), c.CreatedAt));
            }
            rows = rows.OrderByDescending(r => r.Risk == "High" ? 2 : r.Risk == "Medium" ? 1 : 0).ThenByDescending(r => r.Receipts).ThenBy(r => r.Name).ToList();

            var series = new List<UsageDayDto>();
            for (var d = from; d < to; d = d.AddDays(1)) series.Add(daily.TryGetValue(d, out var x) ? new UsageDayDto(d, x.N, x.Rejected) : new UsageDayDto(d, 0, 0));
            var total = rows.Sum(r => r.Receipts); var rejected = rows.Sum(r => r.Rejected);
            var active = rows.Where(r => r.Status != "Suspended").ToList();
            var totals = new UsageTotalsDto(rows.Count, active.Count, rows.Count(r => r.Status == "Trial"), rows.Count(r => r.Status == "Locked"), rows.Count(r => r.Status != "Suspended" && r.Risk != "Low"),
                rows.Sum(r => r.Devices), total, rejected, rows.Sum(r => r.Pending), total == 0 ? 0 : Math.Round(rejected * 100m / total, 1), rows.Sum(r => r.ReceiptsPrevious));
            var byPlan = rows.GroupBy(r => r.Plan ?? "No plan").Select(g => new UsagePlanDto(g.Key, g.Count(), g.Sum(x => x.Receipts))).OrderByDescending(p => p.Companies).ToList();
            return new PlatformUsageDto(days, from, to.AddDays(-1), totals, series, rows, byPlan);
        }
    }
}
