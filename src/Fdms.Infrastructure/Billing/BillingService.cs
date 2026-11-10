using System.Net;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Billing
{
    internal static class BillingMap
    {
        public static PaymentDto ToDto(Payment p, string invoiceNumber, string? clientName) =>
            new(p.Id, p.InvoiceId, invoiceNumber, p.ClientId, clientName, p.Method, p.Status, p.Amount, p.Currency, p.Reference, p.RedirectUrl, p.Instructions, p.Proof != null, p.Notes, p.ConfirmedBy, p.CreatedAt, p.ConfirmedAt);

        public static BillingInvoiceDto ToDto(BillingInvoice i, string? clientName, IEnumerable<Payment> payments) =>
            new(i.Id, i.Number, i.ClientId, clientName, i.Description, i.Subtotal, i.TaxPercent, i.TaxAmount, i.Total, i.Currency, i.Status, i.IssuedAt, i.DueAt, i.PaidAt, i.PeriodStart, i.PeriodEnd, i.PaymentMethod,
                i.PaymentReference, payments.OrderByDescending(p => p.CreatedAt).Select(p => ToDto(p, i.Number, clientName)).ToList());

        public static DateTime AddCycle(DateTime d, string cycle) => cycle == "yearly" ? d.AddYears(1) : d.AddMonths(1);
        public static decimal PriceOf(Plan p, string cycle) => cycle == "yearly" ? p.PriceYearly : p.PriceMonthly;
        public static string Cycle(string? c) => string.Equals(c, "yearly", StringComparison.OrdinalIgnoreCase) ? "yearly" : "monthly";
    }

    /// <summary>
    /// Subscriptions and what the platform bills: starting a subscription, issuing and renewing invoices, plan changes, plan limits, and the
    /// periodic check that marks unpaid subscriptions overdue (and, only when Billing:EnforceSuspension is on, suspends them).
    /// </summary>
    public sealed class BillingService : IBillingService
    {
        private readonly FdmsDbContext _db;
        private readonly IPaynowGateway _paynow;
        private readonly IBillingProfileService _profiles;
        private readonly IEmailSender _email;
        private readonly IConfiguration _cfg;
        private readonly ICallerScope _scope;
        private readonly ILogger<BillingService> _log;
        private readonly IMemoryCache _cache;
        private readonly IPaymentGatewaySettings _gateways;

        private readonly INotificationService _notify;
        private readonly IIntegrationBilling? _integrations;
        private readonly IUsageBilling? _usage;

        public BillingService(FdmsDbContext db, IPaynowGateway paynow, IBillingProfileService profiles, IEmailSender email, IConfiguration cfg, ICallerScope scope, IMemoryCache cache, IPaymentGatewaySettings gateways, ILogger<BillingService> log, INotificationService notify, IIntegrationBilling? integrations = null, IUsageBilling? usage = null)
        { _usage = usage; _integrations = integrations; _notify = notify; _gateways = gateways; _cache = cache; _db = db; _paynow = paynow; _profiles = profiles; _email = email; _cfg = cfg; _scope = scope; _log = log; }

        // ---- starting and overview -----------------------------------------------------------------------------------

        public async Task<SubscriptionDto> StartSubscriptionAsync(Guid clientId, int planId, string? cycle, int? trialDays, CancellationToken ct)
        {
            var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, ct) ?? throw GatewayException.NotFound("Plan");
            if (await _db.Subscriptions.AnyAsync(s => s.ClientId == clientId, ct)) throw GatewayException.Conflict("SUBSCRIPTION_EXISTS", "This company already has a subscription.");
            var c = BillingMap.Cycle(cycle);
            var now = DateTime.UtcNow;
            var trial = Math.Clamp(trialDays ?? plan.TrialDays, 0, 90);

            var sub = new Subscription { ClientId = clientId, PlanId = plan.Id, BillingCycle = c, StartedAt = now, CurrentPeriodStart = now };
            if (trial > 0) { sub.Status = "Trial"; sub.TrialEndsAt = now.AddDays(trial); sub.CurrentPeriodEnd = sub.TrialEndsAt.Value; }
            else { sub.Status = "Active"; sub.CurrentPeriodEnd = BillingMap.AddCycle(now, c); }
            _db.Subscriptions.Add(sub);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "subscription.start", Entity = "Subscription", EntityId = sub.Id.ToString(), Detail = $"{plan.Name} {c}" + (trial > 0 ? $", {trial}-day trial" : "") });
            await _db.SaveChangesAsync(ct);
            if (trial == 0 && BillingMap.PriceOf(plan, c) > 0) await IssueInvoiceAsync(sub, plan, c, now, sub.CurrentPeriodEnd, now.AddDays(7), null, null, ct);
            return await ToSubscriptionDto(sub, ct);
        }

        public async Task<string?> IssueOpeningInvoiceAsync(Guid clientId, CancellationToken ct)
        {
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Subscription");
            var existing = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId == clientId && i.Status == "Open" && i.Kind == "subscription").Select(i => i.Number).FirstOrDefaultAsync(ct);
            if (existing != null) return existing;
            var plan = await _db.Plans.FirstAsync(p => p.Id == sub.PlanId, ct);
            if (BillingMap.PriceOf(plan, sub.BillingCycle) <= 0) return null;
            // the paid period starts when the trial ends, so paying early never shortens the free days
            var start = sub.Status == "Trial" && sub.TrialEndsAt is { } end && end > DateTime.UtcNow ? end : DateTime.UtcNow;
            var inv = await IssueInvoiceAsync(sub, plan, sub.BillingCycle, start, BillingMap.AddCycle(start, sub.BillingCycle), start, null, null, ct);
            return inv.Number;
        }

        public async Task<BillingOverviewDto> OverviewAsync(Guid clientId, CancellationToken ct)
        {
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            var plan = sub == null ? null : await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            var invoices = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId == clientId).OrderByDescending(i => i.IssuedAt).Take(24).ToListAsync(ct);
            var ids = invoices.Select(i => i.Id).ToList();
            var payments = await _db.Payments.AsNoTracking().Where(p => ids.Contains(p.InvoiceId)).ToListAsync(ct);
            var profile = await _profiles.LoadAsync(ct);
            var bank = !string.IsNullOrEmpty(profile.AccountNumber);
            var paynowOn = await _paynow.IsConfiguredAsync(ct);
            var methods = (await _gateways.MethodsAsync(ct)).ToList();
            if (bank) methods.Add(new PayMethodDto("bank", "Bank transfer", "manual", false, null));
            return new BillingOverviewDto(sub == null ? null : await ToSubscriptionDto(sub, ct), plan == null ? null : PlanService.ToDto(plan, 0), await UsageAsync(clientId, ct),
                invoices.Select(i => BillingMap.ToDto(i, client.Name, payments.Where(p => p.InvoiceId == i.Id))).ToList(),
                (await _db.Plans.AsNoTracking().Where(p => p.IsActive && p.IsPublic).OrderBy(p => p.SortOrder).ToListAsync(ct)).Select(p => PlanService.ToDto(p, 0)).ToList(),
                new PaymentOptionsDto(paynowOn, paynowOn, bank, bank ? new BankDetailsDto(profile.BankName, profile.AccountName, profile.AccountNumber, profile.BranchCode, profile.Swift, profile.PaymentInstructions) : null, plan?.Currency ?? "USD", methods),
                await GetAccessAsync(clientId, ct), (await ResellerOfAsync(clientId, ct))?.Name, (await ResellerOfAsync(clientId, ct))?.DiscountPercent ?? 0);
        }

        private async Task<UsageDto> UsageAsync(Guid clientId, CancellationToken ct)
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return new UsageDto(await _db.Devices.CountAsync(d => d.ClientId == clientId, ct), await _db.Users.CountAsync(u => u.ClientId == clientId && u.Status == "ACTIVE", ct),
                await _db.Branches.CountAsync(b => b.ClientId == clientId, ct),
                await _db.Receipts.CountAsync(r => r.ClientId == clientId && r.CreatedAt >= monthStart && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held, ct));
        }

        /// <summary>The company's partner when it is a reseller with a discount (its companies pay less than the list price), else null.</summary>
        private Task<Partner?> ResellerOfAsync(Guid clientId, CancellationToken ct) =>
            (from c in _db.Clients.AsNoTracking() join p in _db.Partners.AsNoTracking() on c.PartnerId equals p.Id
             where c.Id == clientId && p.Kind == "reseller" && p.IsActive && p.DiscountPercent > 0 select p).FirstOrDefaultAsync(ct);

        private async Task<SubscriptionDto> ToSubscriptionDto(Subscription s, CancellationToken ct)
        {
            var plan = await _db.Plans.AsNoTracking().FirstAsync(p => p.Id == s.PlanId, ct);
            var name = await _db.Clients.AsNoTracking().Where(c => c.Id == s.ClientId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            var owing = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId == s.ClientId && i.Status == "Open").SumAsync(i => (decimal?)i.Total, ct) ?? 0;
            return new SubscriptionDto(s.Id, s.ClientId, name, s.PlanId, plan.Name, s.Status, s.BillingCycle, s.StartedAt, s.CurrentPeriodStart, s.CurrentPeriodEnd, s.TrialEndsAt, owing);
        }

        // ---- invoices ---------------------------------------------------------------------------------------------------

        private async Task<BillingInvoice> IssueInvoiceAsync(Subscription sub, Plan plan, string cycle, DateTime start, DateTime end, DateTime due, int? changeToPlan, string? changeToCycle, CancellationToken ct)
        {
            var profile = await _profiles.LoadAsync(ct);
            var subtotal = BillingMap.PriceOf(plan, cycle);
            var description = $"{plan.Name} plan, {cycle} ({start:d MMM yyyy} - {end:d MMM yyyy})";
            // a reseller's company pays the reseller price: the plan less the reseller's discount
            if (await ResellerOfAsync(sub.ClientId, ct) is { } reseller && subtotal > 0)
            {
                subtotal = decimal.Round(subtotal * (100m - reseller.DiscountPercent) / 100m, 2);
                description += $" at the reseller price through {reseller.Name} ({reseller.DiscountPercent:0.##}% off)";
            }
            // the accounting connection (Zoho Books) is billed with the subscription
            if (_integrations != null && changeToPlan == null && await _integrations.ChargeAsync(sub.ClientId, start, end, plan.Currency, ct) is { } extra)
            { subtotal += extra.Amount; description += "; " + extra.Description; }
            // usage (extra receipts, SMS), the company's own WhatsApp number and paid add-ons
            if (_usage != null && changeToPlan == null && await _usage.ChargeAsync(sub.ClientId, plan.Id, start, end, ct) is { } used)
            { subtotal += used.Amount; description += "; " + used.Description; }
            var tax = decimal.Round(subtotal * profile.TaxPercent / 100m, 2);
            var inv = new BillingInvoice
            {
                ClientId = sub.ClientId, SubscriptionId = sub.Id, PlanId = plan.Id, Description = description, Subtotal = subtotal, TaxPercent = profile.TaxPercent,
                TaxAmount = tax, Total = subtotal + tax, Currency = plan.Currency, Status = "Open", IssuedAt = DateTime.UtcNow, DueAt = due, PeriodStart = start, PeriodEnd = end, ChangeToPlanId = changeToPlan, ChangeToCycle = changeToCycle,
            };
            for (var attempt = 0; ; attempt++)
            {
                inv.Number = await NextNumberAsync(profile.InvoicePrefix, ct);
                _db.BillingInvoices.Add(inv);
                try { await _db.SaveChangesAsync(ct); break; }
                catch (DbUpdateException) when (attempt < 4) { _db.Entry(inv).State = EntityState.Detached; }   // two invoices raced for the same number: take the next one
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = sub.ClientId, Action = "invoice.issue", Entity = "BillingInvoice", EntityId = inv.Number, Detail = $"{inv.Description}: {inv.Total:0.00} {inv.Currency}" });
            await _db.SaveChangesAsync(ct);
            await NotifyAsync(sub.ClientId, $"Invoice {inv.Number} from {profile.CompanyName}", $"<p>Your invoice <b>{inv.Number}</b> for <b>{inv.Total:0.00} {inv.Currency}</b> is ready ({WebUtility.HtmlEncode(inv.Description)}). It is due {inv.DueAt:d MMM yyyy}.</p><p>Sign in and open <b>Billing</b> to pay by card, ZimSwitch, EcoCash, OneMoney or bank transfer.</p>", ct);
            // the bell and the dashboard (the email above already went out)
            await _notify.RaiseAsync(sub.ClientId, "billing", "warn", $"invoice:{inv.Id}", $"Invoice {inv.Number} is ready to pay",
                $"{inv.Total:0.00} {inv.Currency} for {inv.Description}, due {inv.DueAt:d MMM yyyy}. Open Billing to pay.", "/billing", ct, email: false);
            return inv;
        }

        private async Task<string> NextNumberAsync(string prefix, CancellationToken ct)
        {
            var p = $"{prefix}-{DateTime.UtcNow.Year}-";
            var last = await _db.BillingInvoices.Where(i => i.Number.StartsWith(p)).OrderByDescending(i => i.Number).Select(i => i.Number).FirstOrDefaultAsync(ct);
            return p + ((last != null && int.TryParse(last[p.Length..], out var n) ? n : 0) + 1).ToString("000000");
        }

        private async Task NotifyAsync(Guid clientId, string subject, string html, CancellationToken ct)
        {
            var to = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Email).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(to)) await _email.SendAsync(to, subject, html, ct);
        }

        public async Task<BillingInvoiceDto> GetInvoiceAsync(Guid? clientId, Guid invoiceId, CancellationToken ct)
        {
            var inv = await _db.BillingInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && (clientId == null || i.ClientId == clientId), ct) ?? throw GatewayException.NotFound("Invoice");
            return BillingMap.ToDto(inv, await _db.Clients.AsNoTracking().Where(c => c.Id == inv.ClientId).Select(c => c.Name).FirstOrDefaultAsync(ct), await _db.Payments.AsNoTracking().Where(p => p.InvoiceId == inv.Id).ToListAsync(ct));
        }

        public async Task<IReadOnlyList<BillingInvoiceDto>> ListInvoicesAsync(string? status, CancellationToken ct)
        {
            var q = _db.BillingInvoices.AsNoTracking().AsQueryable();
            if (status is "Open" or "Paid" or "Void") q = q.Where(i => i.Status == status);
            var rows = await q.OrderByDescending(i => i.IssuedAt).Take(300).ToListAsync(ct);
            var names = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var ids = rows.Select(r => r.Id).ToList();
            var payments = await _db.Payments.AsNoTracking().Where(p => ids.Contains(p.InvoiceId)).ToListAsync(ct);
            return rows.Select(i => BillingMap.ToDto(i, names.GetValueOrDefault(i.ClientId), payments.Where(p => p.InvoiceId == i.Id))).ToList();
        }

        public async Task<byte[]> InvoicePdfAsync(Guid? clientId, Guid invoiceId, CancellationToken ct)
        {
            var inv = await _db.BillingInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && (clientId == null || i.ClientId == clientId), ct) ?? throw GatewayException.NotFound("Invoice");
            var client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == inv.ClientId, ct);
            return BillingInvoicePdf.Render(inv, client.Name, client.Email, await _profiles.LoadAsync(ct), (await _profiles.GetLogoAsync(ct))?.Bytes);
        }

        public async Task<BillingInvoiceDto> CreateInvoiceAsync(Guid clientId, CancellationToken ct)
        {
            if (await _db.BillingInvoices.AnyAsync(i => i.ClientId == clientId && i.Status == "Open" && i.ChangeToPlanId == null && i.Kind == "subscription", ct)) throw GatewayException.Conflict("INVOICE_OPEN", "This company already has an unpaid invoice.");
            return await RenewInternalAsync(clientId, ct);
        }

        private async Task<BillingInvoiceDto> RenewInternalAsync(Guid clientId, CancellationToken ct)
        {
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Subscription");
            var plan = await _db.Plans.FirstAsync(p => p.Id == sub.PlanId, ct);
            if (await _db.BillingInvoices.AnyAsync(i => i.ClientId == clientId && i.Status == "Open" && i.ChangeToPlanId == null && i.Kind == "subscription", ct)) throw GatewayException.Conflict("INVOICE_OPEN", "This company already has an unpaid invoice.");
            var start = PeriodStart(sub);
            var inv = await IssueInvoiceAsync(sub, plan, sub.BillingCycle, start, BillingMap.AddCycle(start, sub.BillingCycle), start, null, null, ct);
            return await GetInvoiceAsync(null, inv.Id, ct);
        }

        // ---- changing plan ------------------------------------------------------------------------------------------------

        private static List<string> Misfits(Plan plan, UsageDto u)
        {
            var bad = new List<string>();
            if (plan.MaxDevices > 0 && u.Devices > plan.MaxDevices) bad.Add($"{u.Devices} devices (limit {plan.MaxDevices})");
            if (plan.MaxUsers > 0 && u.Users > plan.MaxUsers) bad.Add($"{u.Users} users (limit {plan.MaxUsers})");
            if (plan.MaxBranches > 0 && u.Branches > plan.MaxBranches) bad.Add($"{u.Branches} branches (limit {plan.MaxBranches})");
            return bad;
        }

        public async Task<BillingInvoiceDto> ChangePlanAsync(Guid clientId, ChangePlanRequest req, CancellationToken ct)
        {
            var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == req.PlanId && p.IsActive && p.IsPublic, ct) ?? throw GatewayException.NotFound("Plan");
            var cycle = BillingMap.Cycle(req.BillingCycle);
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (sub == null)
            {
                await StartSubscriptionAsync(clientId, plan.Id, cycle, 0, ct);   // a company created by hand picks its plan: no trial, pay to start
                var first = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId == clientId).OrderByDescending(i => i.IssuedAt).FirstOrDefaultAsync(ct);
                return first == null ? throw GatewayException.Conflict("PLAN_FREE", "That plan is free: it is active now.") : await GetInvoiceAsync(clientId, first.Id, ct);
            }
            if (sub.PlanId == plan.Id && sub.BillingCycle == cycle) throw GatewayException.BadRequest("SAME_PLAN", "You are already on that plan and billing cycle.");
            var misfit = Misfits(plan, await UsageAsync(clientId, ct));
            if (misfit.Count > 0) throw GatewayException.Conflict("PLAN_TOO_SMALL", $"You use {string.Join(", ", misfit)}, which {plan.Name} does not allow. Remove some first, or choose a bigger plan.");

            foreach (var old in await _db.BillingInvoices.Where(i => i.ClientId == clientId && i.Status == "Open" && i.ChangeToPlanId != null).ToListAsync(ct)) old.Status = "Void";   // only the newest change is payable
            var now = DateTime.UtcNow;
            var price = BillingMap.PriceOf(plan, cycle);
            if (price <= 0)
            {
                sub.PlanId = plan.Id; PlanModuleAccess.Changed(); sub.BillingCycle = cycle; sub.Status = "Active"; sub.CurrentPeriodStart = now; sub.CurrentPeriodEnd = BillingMap.AddCycle(now, cycle);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "subscription.change", Entity = "Subscription", EntityId = sub.Id.ToString(), Detail = $"{plan.Name} {cycle} (free)" });
                await _db.SaveChangesAsync(ct);
                throw GatewayException.Conflict("PLAN_FREE", "That plan is free: it is active now.");
            }
            var inv = await IssueInvoiceAsync(sub, plan, cycle, now, BillingMap.AddCycle(now, cycle), now.AddDays(7), plan.Id, cycle, ct);
            return await GetInvoiceAsync(clientId, inv.Id, ct);
        }

        // ---- operator ---------------------------------------------------------------------------------------------------------

        public async Task<IReadOnlyList<SubscriptionDto>> ListSubscriptionsAsync(CancellationToken ct)
        {
            var subs = await _db.Subscriptions.AsNoTracking().OrderBy(s => s.CurrentPeriodEnd).ToListAsync(ct);
            var plans = await _db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var names = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var owing = (await _db.BillingInvoices.AsNoTracking().Where(i => i.Status == "Open").ToListAsync(ct)).GroupBy(i => i.ClientId).ToDictionary(g => g.Key, g => g.Sum(i => i.Total));
            return subs.Select(s => new SubscriptionDto(s.Id, s.ClientId, names.GetValueOrDefault(s.ClientId), s.PlanId, plans.GetValueOrDefault(s.PlanId) ?? "?", s.Status, s.BillingCycle, s.StartedAt, s.CurrentPeriodStart,
                s.CurrentPeriodEnd, s.TrialEndsAt, owing.GetValueOrDefault(s.ClientId))).ToList();
        }

        public async Task<SubscriptionDto> AdminUpdateAsync(Guid clientId, SubscriptionAdminRequest req, CancellationToken ct)
        {
            var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            var now = DateTime.UtcNow;
            if (sub == null)
            {
                var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == req.PlanId, ct) ?? throw GatewayException.BadRequest("PLAN_REQUIRED", "Choose a plan to start a subscription.");
                sub = new Subscription { ClientId = clientId, PlanId = plan.Id, Status = "Active", BillingCycle = BillingMap.Cycle(req.BillingCycle), StartedAt = now, CurrentPeriodStart = now, CurrentPeriodEnd = BillingMap.AddCycle(now, BillingMap.Cycle(req.BillingCycle)) };
                _db.Subscriptions.Add(sub); PlanModuleAccess.Changed();
            }
            else if (req.PlanId.HasValue && req.PlanId != sub.PlanId)
            {
                if (!await _db.Plans.AnyAsync(p => p.Id == req.PlanId, ct)) throw GatewayException.NotFound("Plan");
                sub.PlanId = req.PlanId.Value; PlanModuleAccess.Changed();
            }
            if (!string.IsNullOrEmpty(req.BillingCycle)) sub.BillingCycle = BillingMap.Cycle(req.BillingCycle);
            if (req.PeriodEnd.HasValue) { if (req.PeriodEnd <= sub.CurrentPeriodStart) throw GatewayException.BadRequest("PERIOD_INVALID", "The period must end after it starts."); sub.CurrentPeriodEnd = req.PeriodEnd.Value; }
            if (!string.IsNullOrEmpty(req.Status))
            {
                if (req.Status is not ("Trial" or "Active" or "PastDue" or "Suspended" or "Cancelled")) throw GatewayException.BadRequest("STATUS_INVALID", "Status must be Trial, Active, PastDue, Suspended or Cancelled.");
                sub.Status = req.Status;
                client.IsActive = req.Status is not ("Suspended" or "Cancelled");   // the existing suspension switch: users and tokens stop working
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "subscription.admin-update", Entity = "Subscription", EntityId = sub.Id.ToString(), Detail = $"plan {sub.PlanId}, {sub.Status}, until {sub.CurrentPeriodEnd:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return await ToSubscriptionDto(sub, ct);
        }

        // ---- paid ---------------------------------------------------------------------------------------------------------------

        public async Task<BillingInvoiceDto> IssueOneOffAsync(Guid clientId, string kind, string description, decimal subtotal, string currency, CancellationToken ct)
        {
            var profile = await _profiles.LoadAsync(ct);
            var tax = decimal.Round(subtotal * profile.TaxPercent / 100m, 2);
            var now = DateTime.UtcNow;
            var inv = new BillingInvoice { ClientId = clientId, Kind = kind, Description = description.Length > 300 ? description[..300] : description, Subtotal = subtotal, TaxPercent = profile.TaxPercent,
                TaxAmount = tax, Total = subtotal + tax, Currency = currency, Status = "Open", IssuedAt = now, DueAt = now.AddDays(7), PeriodStart = now, PeriodEnd = now };
            for (var attempt = 0; ; attempt++)
            {
                inv.Number = await NextNumberAsync(profile.InvoicePrefix, ct);
                _db.BillingInvoices.Add(inv);
                try { await _db.SaveChangesAsync(ct); break; }
                catch (DbUpdateException) when (attempt < 4) { _db.Entry(inv).State = EntityState.Detached; }
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "invoice.issue", Entity = "BillingInvoice", EntityId = inv.Number, Detail = $"{inv.Description}: {inv.Total:0.00} {inv.Currency}" });
            await _db.SaveChangesAsync(ct);
            await NotifyAsync(clientId, $"Invoice {inv.Number} from {profile.CompanyName}", $"<p>Your invoice <b>{inv.Number}</b> for <b>{inv.Total:0.00} {inv.Currency}</b> is ready ({WebUtility.HtmlEncode(inv.Description)}). It is due {inv.DueAt:d MMM yyyy}.</p><p>Sign in and open <b>Billing</b> to pay.</p>", ct);
            await _notify.RaiseAsync(clientId, "billing", "warn", $"invoice:{inv.Id}", $"Invoice {inv.Number} is ready to pay", $"{inv.Total:0.00} {inv.Currency} for {inv.Description}. Open Billing to pay.", "/billing", ct, email: false);
            var name = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            return BillingMap.ToDto(inv, name, Array.Empty<Payment>());
        }

        public async Task ApplyInvoicePaidAsync(Guid invoiceId, string method, string? reference, CancellationToken ct)
        {
            var inv = await _db.BillingInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw GatewayException.NotFound("Invoice");
            if (inv.Status == "Paid") return;
            if (inv.Status == "Void") throw GatewayException.Conflict("INVOICE_VOID", "That invoice was cancelled.");
            inv.Status = "Paid"; inv.PaidAt = DateTime.UtcNow; inv.PaymentMethod = method; inv.PaymentReference = reference;
            _cache.Remove(AccessKey(inv.ClientId));

            // paid add-ons (extra devices, users, branches) switch on and are billed with each renewal from now on
            if (inv.Kind == "addon")
            {
                var client = await _db.Clients.FirstAsync(c => c.Id == inv.ClientId, ct);
                var add = AddonService.Parse(inv.AddonsJson);
                client.PaidDevices += add.Devices; client.PaidUsers += add.Users; client.PaidBranches += add.Branches;
                _db.AuditLogs.Add(new AuditLog { ClientId = inv.ClientId, Action = "invoice.paid", Entity = "BillingInvoice", EntityId = inv.Number, Detail = $"{inv.Total:0.00} {inv.Currency} via {method} (add-ons: {AddonService.Summary(add)})" + (reference != null ? $" ({reference})" : "") });
                await _db.SaveChangesAsync(ct);
                await _notify.RaiseAsync(inv.ClientId, "billing", "info", $"addon-active:{inv.Id}", "Your add-ons are active", $"{AddonService.Summary(add)} added to your plan. They are billed with each renewal.", "/billing", ct);
                return;
            }

            // the go-live fee: live (Production) devices switch on
            if (inv.Kind == "golive")
            {
                var req = await _db.GoLiveRequests.FirstOrDefaultAsync(r => r.InvoiceId == inv.Id, ct);
                var client = await _db.Clients.FirstAsync(c => c.Id == inv.ClientId, ct);
                if (req is { Status: "AwaitingPayment" }) { req.Status = "Approved"; client.LiveApproved = true; client.LiveApprovedAt = DateTime.UtcNow; }
                _db.AuditLogs.Add(new AuditLog { ClientId = inv.ClientId, Action = "invoice.paid", Entity = "BillingInvoice", EntityId = inv.Number, Detail = $"{inv.Total:0.00} {inv.Currency} via {method} (go-live: live devices enabled)" + (reference != null ? $" ({reference})" : "") });
                _db.Notifications.RemoveRange(await _db.Notifications.Where(n => n.ClientId == inv.ClientId && (n.Key == $"invoice:{inv.Id}" || (req != null && n.Key == $"golive-approved:{req.Id}"))).ToListAsync(ct));
                await _db.SaveChangesAsync(ct);
                ForgetAccess(inv.ClientId);
                await _notify.RaiseAsync(inv.ClientId, "billing", "info", $"golive-live:{inv.Id}", "Live devices are switched on", "You can now register live (Production) ZIMRA FDMS devices under Devices.", "/devices", ct);
                return;
            }

            // a one-off integration charge switches the integration on and leaves the subscription alone
            if (inv.Kind == "integration")
            {
                var access = await _db.IntegrationAccess.FirstOrDefaultAsync(a => a.ClientId == inv.ClientId && a.InvoiceId == inv.Id, ct);
                if (access is { Status: "AwaitingPayment" }) { access.Status = "Active"; access.ActivatedAt = DateTime.UtcNow; }
                if (access != null) await Integrations.IntegrationAccessService.ClearInvoiceNoticesAsync(_db, inv.ClientId, access.Id, inv.Id, ct);
                _db.AuditLogs.Add(new AuditLog { ClientId = inv.ClientId, Action = "invoice.paid", Entity = "BillingInvoice", EntityId = inv.Number, Detail = $"{inv.Total:0.00} {inv.Currency} via {method} (integration)" + (reference != null ? $" ({reference})" : "") });
                await _db.SaveChangesAsync(ct);
                await _notify.RaiseAsync(inv.ClientId, "integration", "info", $"integration-active:{inv.Id}", "The Zoho Books integration is switched on",
                    $"We received {inv.Total:0.00} {inv.Currency} for {inv.Number}. Open Integration, Zoho Books to connect.", "/integrations", ct);
                var who = await _db.Clients.AsNoTracking().Where(c => c.Id == inv.ClientId).Select(c => c.Name).FirstOrDefaultAsync(ct);
                await _notify.RaisePlatformAsync("payment", "info", $"invoice-paid:{inv.Id}", $"Paid: {inv.Total:0.00} {inv.Currency} from {who} (integration)", $"{who} paid {inv.Number}; their Zoho integration is now on.", "/admin/integrations", ct);
                return;
            }

            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ClientId == inv.ClientId, ct);
            if (sub != null)
            {
                if (inv.ChangeToPlanId.HasValue) { sub.PlanId = inv.ChangeToPlanId.Value; PlanModuleAccess.Changed(); sub.BillingCycle = inv.ChangeToCycle ?? sub.BillingCycle; }
                sub.CurrentPeriodStart = inv.PeriodStart; sub.CurrentPeriodEnd = inv.PeriodEnd;
                var wasSuspended = sub.Status == "Suspended";
                sub.Status = "Active";
                if (wasSuspended)
                {
                    var client = await _db.Clients.FirstAsync(c => c.Id == inv.ClientId, ct);
                    client.IsActive = true;   // paid up: switched back on
                }
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = inv.ClientId, Action = "invoice.paid", Entity = "BillingInvoice", EntityId = inv.Number, Detail = $"{inv.Total:0.00} {inv.Currency} via {method}" + (reference != null ? $" ({reference})" : "") });
            await _db.SaveChangesAsync(ct);
            await NotifyAsync(inv.ClientId, $"Payment received for {inv.Number}", $"<p>Thank you. We received <b>{inv.Total:0.00} {inv.Currency}</b> for invoice <b>{inv.Number}</b>. Your subscription is active until <b>{inv.PeriodEnd:d MMM yyyy}</b>.</p>", ct);
            await _notify.RaiseAsync(inv.ClientId, "billing", "info", $"invoice-paid:{inv.Id}", $"Payment for {inv.Number} approved",
                $"We received {inv.Total:0.00} {inv.Currency} by {method}. Your subscription is active until {inv.PeriodEnd:d MMM yyyy}.", "/billing", ct, email: false);
            if (method != Services.ReferralService.CoinsMethod)
            {
                var company = await _db.Clients.AsNoTracking().Where(c => c.Id == inv.ClientId).Select(c => c.Name).FirstOrDefaultAsync(ct);
                await _notify.RaisePlatformAsync("payment", "info", $"invoice-paid:{inv.Id}", $"Paid: {inv.Total:0.00} {inv.Currency} from {company}",
                    $"{company} paid invoice {inv.Number} by {method}" + (reference != null ? $" (reference {reference})." : "."), "/admin/revenue", ct);
            }
        }

        // ---- limits and the periodic check ---------------------------------------------------------------------------------------------

        private static string AccessKey(Guid clientId) => "billing-access:" + clientId;

        public async Task<AccessStateDto> GetAccessAsync(Guid clientId, CancellationToken ct)
        {
            if (_cache.TryGetValue(AccessKey(clientId), out AccessStateDto? hit) && hit != null) return hit;
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            var live = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.LiveApproved).FirstOrDefaultAsync(ct);
            var state = new AccessStateDto(false, false, null, null, live);
            if (sub != null)
            {
                var now = DateTime.UtcNow;
                var grace = Math.Clamp(_cfg.GetValue("Billing:LockGraceDays", 3), 0, 60);
                var plan = await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
                var free = plan != null && BillingMap.PriceOf(plan, sub.BillingCycle) <= 0;
                if (sub.Status == "Trial")
                {
                    // a trial is demo-only, and when it ends everything but Billing closes until a plan is paid
                    var over = sub.TrialEndsAt.HasValue && sub.TrialEndsAt.Value < now;
                    state = over ? new AccessStateDto(true, false, "TRIAL_EXPIRED", sub.TrialEndsAt, live) : new AccessStateDto(false, true, null, null, live);
                }
                else if (!await _db.BillingInvoices.AnyAsync(i => i.ClientId == clientId && i.Status == "Paid", ct) && await _db.BillingInvoices.AnyAsync(i => i.ClientId == clientId && i.Status == "Open" && i.Kind == "subscription", ct))
                    state = new AccessStateDto(true, false, "NEW_ACCOUNT", null, live);   // a brand-new account owes its first invoice before anything else opens up
                else if (!free && sub.CurrentPeriodEnd.AddDays(grace) < now)
                    state = new AccessStateDto(true, false, "EXPIRED", sub.CurrentPeriodEnd, live);   // the paid period ran out
            }
            _cache.Set(AccessKey(clientId), state, TimeSpan.FromSeconds(20));
            return state;
        }

        public void ForgetAccess(Guid clientId) => _cache.Remove(AccessKey(clientId));

        public async Task EnsureLiveAllowedAsync(Guid clientId, CancellationToken ct)
        {
            var a = await GetAccessAsync(clientId, ct);
            if (a.DemoOnly) throw new GatewayException(403, "TRIAL_DEMO_ONLY", "Your trial is for demo (Test) devices only. Pay for your plan under Billing to register live (Production) devices.");
            if (!a.LiveApproved) throw new GatewayException(403, "LIVE_NOT_APPROVED", "Live (Production) devices need approval. Send a go-live request under Billing and we will switch it on.");
        }

        public async Task<BillingInvoiceDto> RenewAsync(Guid clientId, CancellationToken ct)
        {
            var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Subscription");
            var existing = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId == clientId && i.Status == "Open" && i.ChangeToPlanId == null && i.Kind == "subscription").OrderByDescending(i => i.IssuedAt).FirstOrDefaultAsync(ct);
            if (existing != null) return await GetInvoiceAsync(clientId, existing.Id, ct);
            var plan = await _db.Plans.FirstAsync(p => p.Id == sub.PlanId, ct);
            if (BillingMap.PriceOf(plan, sub.BillingCycle) <= 0) throw GatewayException.Conflict("PLAN_FREE", "Your plan is free: there is nothing to pay.");
            var start = PeriodStart(sub);
            var inv = await IssueInvoiceAsync(sub, plan, sub.BillingCycle, start, BillingMap.AddCycle(start, sub.BillingCycle), DateTime.UtcNow.AddDays(7), null, null, ct);
            return await GetInvoiceAsync(clientId, inv.Id, ct);
        }

        /// <summary>The next period starts where the last one ended, or today when it ended long ago (otherwise paying would still leave the account expired).</summary>
        private static DateTime PeriodStart(Subscription sub) => sub.CurrentPeriodEnd < DateTime.UtcNow ? DateTime.UtcNow : sub.CurrentPeriodEnd;

        public async Task EnsureWithinPlanAsync(Guid clientId, string resource, CancellationToken ct)
        {
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (sub == null) return;   // created by hand, no plan: not limited
            if (sub.Status == "Cancelled") throw new GatewayException(403, "SUBSCRIPTION_CANCELLED", "This subscription was cancelled. Contact support to reactivate it.");
            var plan = await _db.Plans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);
            if (plan.Kind == "connector" && resource is "devices" or "branches")
                throw new GatewayException(403, "PLAN_CONNECTOR", $"Your {plan.Name} plan covers the Zoho Books connector only. To fiscalise, change to a fiscal plan under Billing.");
            if (plan.Kind == PlanKinds.Accounting && resource == "devices")
                throw new GatewayException(403, "PLAN_ACCOUNTING", $"Your {plan.Name} plan is for the books only. To fiscalise, change to a fiscal plan under Billing.");
            var usage = await UsageAsync(clientId, ct);
            var bonus = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => new { Devices = c.BonusDevices + c.PaidDevices, Users = c.BonusUsers + c.PaidUsers, Branches = c.BonusBranches + c.PaidBranches }).FirstOrDefaultAsync(ct);
            var (limit, used, extra) = resource switch { "devices" => (plan.MaxDevices, usage.Devices, bonus?.Devices ?? 0), "users" => (plan.MaxUsers, usage.Users, bonus?.Users ?? 0), "branches" => (plan.MaxBranches, usage.Branches, bonus?.Branches ?? 0), _ => (0, 0, 0) };
            // extra slots bought with coins (or given by the operator) sit on top of the plan; 0 still means unlimited
            if (limit > 0 && used >= limit + extra) throw new GatewayException(403, "PLAN_LIMIT", $"Your {plan.Name} plan allows {limit + extra} {resource}{(extra > 0 ? $" ({limit} + {extra} extra)" : "")}. Add more under Billing (add-ons), upgrade your plan, or get more with coins under Refer and earn.");
        }

        public async Task<int> RunMaintenanceAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var renewDays = _cfg.GetValue("Billing:RenewalInvoiceDaysBefore", 7);
            var graceDays = _cfg.GetValue("Billing:GraceDays", 7);
            var enforce = _cfg.GetValue("Billing:EnforceSuspension", false);
            var changes = 0;

            foreach (var sub in await _db.Subscriptions.Where(s => s.Status != "Cancelled").ToListAsync(ct))
            {
                var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
                if (plan == null) continue;
                var open = await _db.BillingInvoices.AnyAsync(i => i.ClientId == sub.ClientId && i.Status == "Open" && i.ChangeToPlanId == null && i.Kind == "subscription", ct);

                // 1. the next invoice goes out a week before the period ends
                if (!open && sub.Status != "Suspended" && sub.CurrentPeriodEnd <= now.AddDays(renewDays))
                {
                    var price = BillingMap.PriceOf(plan, sub.BillingCycle);
                    var start = PeriodStart(sub); var end = BillingMap.AddCycle(start, sub.BillingCycle);
                    if (price > 0) { await IssueInvoiceAsync(sub, plan, sub.BillingCycle, start, end, start, null, null, ct); open = true; }
                    else { sub.CurrentPeriodStart = start; sub.CurrentPeriodEnd = end; sub.Status = "Active"; }   // a free plan simply rolls on
                    changes++;
                }
                // 2. period over and the invoice is still unpaid
                if (open && sub.CurrentPeriodEnd < now && sub.Status is "Trial" or "Active")
                {
                    sub.Status = "PastDue"; changes++;
                    _db.AuditLogs.Add(new AuditLog { ClientId = sub.ClientId, Action = "subscription.past-due", Entity = "Subscription", EntityId = sub.Id.ToString(), Detail = "unpaid after the period ended" });
                }
                // 3. a week of grace, then suspended (the company is only switched off when Billing:EnforceSuspension is on)
                if (sub.Status == "PastDue" && sub.CurrentPeriodEnd.AddDays(graceDays) < now)
                {
                    sub.Status = "Suspended"; changes++;
                    if (enforce) { var client = await _db.Clients.FirstAsync(c => c.Id == sub.ClientId, ct); client.IsActive = false; }
                    _db.AuditLogs.Add(new AuditLog { ClientId = sub.ClientId, Action = "subscription.suspend", Entity = "Subscription", EntityId = sub.Id.ToString(), Detail = enforce ? "unpaid: company switched off" : "unpaid (company not switched off: enforcement is off)" });
                }
            }
            if (changes > 0) await _db.SaveChangesAsync(ct);
            return changes;
        }
    }
}
