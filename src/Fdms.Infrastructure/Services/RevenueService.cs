using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>The platform owner's money: recurring revenue, what came in, what is owed, who pays, which plans sell, and what the platform owes in rewards.</summary>
    public sealed class RevenueService : IRevenueService
    {
        private readonly FdmsDbContext _db;
        private readonly IPartnerService _partners;
        public RevenueService(FdmsDbContext db, IPartnerService partners) { _db = db; _partners = partners; }

        private static List<AmountDto> Sum(IEnumerable<(string Currency, decimal Amount)> rows) =>
            rows.GroupBy(r => r.Currency).Select(g => new AmountDto(g.Key, Math.Round(g.Sum(x => x.Amount), 2))).Where(a => a.Amount != 0).OrderBy(a => a.Currency).ToList();

        public async Task<RevenueDto> GetAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var lastMonthStart = monthStart.AddMonths(-1);
            var yearAgo = monthStart.AddMonths(-11);

            var clients = await _db.Clients.AsNoTracking().Select(c => new { c.Id, c.Name, c.IsActive, c.CreatedAt }).ToListAsync(ct);
            var subs = await _db.Subscriptions.AsNoTracking().ToListAsync(ct);
            var plans = await _db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
            // money actually received: paid invoices, not the ones settled with coins
            var paid = await _db.BillingInvoices.AsNoTracking().Where(i => i.Status == "Paid" && i.PaymentMethod != ReferralService.CoinsMethod)
                .Select(i => new { i.ClientId, i.Total, i.Currency, i.PaidAt, i.IssuedAt, i.PaymentMethod }).ToListAsync(ct);
            var open = await _db.BillingInvoices.AsNoTracking().Where(i => i.Status == "Open").Select(i => new { i.ClientId, i.Total, i.Currency, i.DueAt }).ToListAsync(ct);
            var awaiting = await _db.Payments.AsNoTracking().CountAsync(p => p.Status == "AwaitingConfirmation", ct);
            var payments = await _db.Payments.AsNoTracking().Where(p => p.Status == "Paid" && p.ConfirmedAt >= yearAgo && p.Method != ReferralService.CoinsMethod)
                .Select(p => new { p.Method, p.Amount, p.Currency }).ToListAsync(ct);

            DateTime When(DateTime? paidAt, DateTime issued) => paidAt ?? issued;
            static decimal Monthly(Domain.Entities.Plan p, string cycle) => cycle == "yearly" ? (p.PriceYearly > 0 ? p.PriceYearly / 12m : p.PriceMonthly) : p.PriceMonthly;

            var payingSubs = subs.Where(s => s.Status == "Active" && plans.ContainsKey(s.PlanId) && Monthly(plans[s.PlanId], s.BillingCycle) > 0).ToList();
            var mrr = Sum(payingSubs.Select(s => (plans[s.PlanId].Currency, Monthly(plans[s.PlanId], s.BillingCycle))));
            var counts = new RevenueCountsDto(
                clients.Count, clients.Count(c => c.IsActive), payingSubs.Select(s => s.ClientId).Distinct().Count(),
                subs.Count(s => s.Status == "Trial"), subs.Count(s => s.Status == "PastDue"), subs.Count(s => s.Status == "Suspended") + clients.Count(c => !c.IsActive && subs.All(s => s.ClientId != c.Id || s.Status != "Suspended")),
                subs.Count(s => s.Status == "Cancelled"), clients.Count(c => c.CreatedAt >= monthStart),
                subs.Count(s => s.Status == "Trial" && s.TrialEndsAt != null && s.TrialEndsAt <= now.AddDays(7)), awaiting);

            // of the companies that had a trial, how many now pay
            var hadTrial = subs.Where(s => s.TrialEndsAt != null).ToList();
            var conversion = hadTrial.Count == 0 ? 0 : Math.Round(100m * hadTrial.Count(s => s.Status == "Active") / hadTrial.Count, 1);

            var months = Enumerable.Range(0, 12).Select(i => yearAgo.AddMonths(i)).Select(m =>
            {
                var inMonth = paid.Where(p => When(p.PaidAt, p.IssuedAt) >= m && When(p.PaidAt, p.IssuedAt) < m.AddMonths(1)).ToList();
                return new RevenueMonthDto(m.ToString("yyyy-MM"), Sum(inMonth.Select(p => (p.Currency, p.Total))), inMonth.Count);
            }).ToList();

            var planRows = plans.Values.OrderBy(p => p.PriceMonthly).Select(p =>
            {
                var onPlan = subs.Where(s => s.PlanId == p.Id).ToList();
                var payingOnPlan = onPlan.Where(s => s.Status == "Active").ToList();
                return new RevenuePlanDto(p.Id, p.Name, p.PriceMonthly, p.Currency, payingOnPlan.Count, onPlan.Count(s => s.Status == "Trial"), Math.Round(payingOnPlan.Sum(s => Monthly(p, s.BillingCycle)), 2));
            }).ToList();

            var names = clients.ToDictionary(c => c.Id, c => c.Name);
            RevenueClientDto Row(Guid id)
            {
                var s = subs.FirstOrDefault(x => x.ClientId == id);
                var mine = paid.Where(p => p.ClientId == id).ToList();
                return new RevenueClientDto(id, names.GetValueOrDefault(id, "?"), s != null && plans.TryGetValue(s.PlanId, out var pl) ? pl.Name : null, s?.Status ?? "No plan",
                    Sum(mine.Select(p => (p.Currency, p.Total))), Sum(open.Where(o => o.ClientId == id).Select(o => (o.Currency, o.Total))), mine.Max(p => p.PaidAt));
            }
            var top = paid.GroupBy(p => p.ClientId).OrderByDescending(g => g.Sum(x => x.Total)).Take(10).Select(g => Row(g.Key)).ToList();
            var owing = open.GroupBy(o => o.ClientId).OrderByDescending(g => g.Sum(x => x.Total)).Take(15).Select(g => Row(g.Key)).ToList();

            // what the platform owes: coins companies can still spend, money rewards not paid out yet, partner commission not paid out yet
            var coinsOut = await _db.CoinTransactions.SumAsync(t => (int?)t.Amount, ct) ?? 0;
            var program = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == "Referral.Program").Select(s => s.Value).FirstOrDefaultAsync(ct);
            var perUnit = 10;
            try { if (!string.IsNullOrWhiteSpace(program)) perUnit = JsonSerializer.Deserialize<ReferralProgramDto>(program, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.CoinsPerUnit ?? 10; } catch (JsonException) { }
            var baseCurrency = plans.Values.Select(p => p.Currency).FirstOrDefault() ?? "USD";
            var referralOwed = Sum((await _db.ReferralRewards.AsNoTracking().Where(r => r.Status == "Earned").Select(r => new { r.Currency, r.Amount }).ToListAsync(ct)).Select(r => (r.Currency, r.Amount)));
            var partnerOwed = Sum((await _partners.ListAsync(ct)).SelectMany(p => p.Owed).Select(a => (a.Currency, a.Amount)));

            var paidIn = (DateTime from, DateTime to) => Sum(paid.Where(p => When(p.PaidAt, p.IssuedAt) >= from && When(p.PaidAt, p.IssuedAt) < to).Select(p => (p.Currency, p.Total)));
            return new RevenueDto(
                mrr, mrr.Select(a => new AmountDto(a.Currency, a.Amount * 12)).ToList(),
                paidIn(monthStart, monthStart.AddMonths(1)), paidIn(lastMonthStart, monthStart), paidIn(yearAgo, monthStart.AddMonths(1)), Sum(paid.Select(p => (p.Currency, p.Total))),
                Sum(open.Select(o => (o.Currency, o.Total))), Sum(open.Where(o => o.DueAt < now).Select(o => (o.Currency, o.Total))),
                counts.Paying == 0 ? new List<AmountDto>() : mrr.Select(a => new AmountDto(a.Currency, Math.Round(a.Amount / counts.Paying, 2))).ToList(),
                counts, conversion, months, planRows,
                payments.GroupBy(p => p.Method).Select(g => new RevenueMethodDto(Billing.PaymentService.Label(g.Key), Sum(g.Select(x => (x.Currency, x.Amount))), g.Count())).OrderByDescending(m => m.Payments).ToList(),
                top, owing, Math.Max(0, coinsOut), coinsOut > 0 ? new List<AmountDto> { new(baseCurrency, Math.Round(coinsOut / (decimal)perUnit, 2)) } : new List<AmountDto>(),
                referralOwed, partnerOwed, now);
        }
    }
}
