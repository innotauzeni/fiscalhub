using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>The owner's prices for usage and add-ons, kept in Settings so they can change at any time (from the next invoice).</summary>
    public sealed class UsagePricingStore
    {
        public const string Key = "Billing.UsagePricing";
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private readonly FdmsDbContext _db;
        public UsagePricingStore(FdmsDbContext db) => _db = db;

        public static UsagePricingDto Default() => new(false, "USD", 0.05m, 10m, 5m, 2m, 5m, 50m, 0m);

        public async Task<UsagePricingDto> LoadAsync(CancellationToken ct)
        {
            var json = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).FirstOrDefaultAsync(ct);
            try { return (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<UsagePricingDto>(json, J)) ?? Default(); } catch (JsonException) { return Default(); }
        }

        public async Task SaveAsync(UsagePricingDto p, string? by, CancellationToken ct)
        {
            var row = await _db.SystemSettings.FirstOrDefaultAsync(x => x.Key == Key, ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = Key });
            row.Value = JsonSerializer.Serialize(p, J); row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = by;
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// What a subscription invoice adds on top of the plan:
    /// usage since the last invoice (receipts above the plan's monthly allowance, SMS sent through the platform's account), billed exactly once
    /// (Client.UsageBilledUntil moves forward with each invoice); and, for the period ahead, the company's own WhatsApp number and its paid add-ons.
    /// </summary>
    public sealed class UsageBilling : IUsageBilling
    {
        private readonly FdmsDbContext _db;
        private readonly UsagePricingStore _prices;
        public UsageBilling(FdmsDbContext db, UsagePricingStore prices) { _db = db; _prices = prices; }

        public static int Months(DateTime from, DateTime to) => Math.Max(1, (int)Math.Round((to - from).TotalDays / 30.4));

        public static IReadOnlyList<ReceiptTier> Tiers(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ReceiptTier>();
            try { return JsonSerializer.Deserialize<List<ReceiptTier>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new List<ReceiptTier>(); }
            catch (JsonException) { return Array.Empty<ReceiptTier>(); }
        }

        /// <summary>
        /// The receipt charge for a stretch of usage: metered = per 1,000 within the monthly minimum and cap; tiered = the band the monthly volume falls in.
        /// Both use the average receipts per month, so where an invoice date falls does not change the price.
        /// </summary>
        public static (decimal Amount, string Text)? ReceiptCharge(Plan plan, int receipts, int months)
        {
            var perMonth = (decimal)receipts / months;
            switch (plan.ReceiptPricing)
            {
                case "metered":
                    var monthly = perMonth * plan.PricePerThousandReceipts / 1000m;
                    var note = "";
                    if (plan.ReceiptMinimumMonthly > 0 && monthly < plan.ReceiptMinimumMonthly) { monthly = plan.ReceiptMinimumMonthly; note = ", monthly minimum"; }
                    if (plan.ReceiptCapMonthly > 0 && monthly > plan.ReceiptCapMonthly) { monthly = plan.ReceiptCapMonthly; note = ", monthly cap"; }
                    return (monthly * months, $"{receipts} receipt(s) at {plan.PricePerThousandReceipts:0.##} per 1,000{note} x{months} month(s)");
                case "tiered":
                    var tiers = Tiers(plan.ReceiptTiersJson);
                    if (tiers.Count == 0) return null;
                    var band = tiers.FirstOrDefault(t => t.UpTo == null || perMonth <= t.UpTo) ?? tiers[^1];
                    return (band.Price * months, $"{receipts} receipt(s) (~{Math.Ceiling(perMonth):0}/month, band {(band.UpTo is { } up ? "up to " + up.ToString("N0") : "unlimited")}) x{months} month(s)");
                default:
                    return null;
            }
        }

        public async Task<(decimal Amount, string Description)?> ChargeAsync(Guid clientId, int planId, DateTime periodStart, DateTime periodEnd, CancellationToken ct)
        {
            var plan = await _db.Plans.AsNoTracking().FirstAsync(p => p.Id == planId, ct);
            var client = await _db.Clients.FirstAsync(c => c.Id == clientId, ct);
            var p = await _prices.LoadAsync(ct);
            var now = DateTime.UtcNow;
            // usage is counted from where the last invoice stopped (first time: one period back)
            var from = client.UsageBilledUntil ?? now.AddDays(-Math.Max(28, (periodEnd - periodStart).TotalDays));
            var ahead = Months(periodStart, periodEnd);
            var parts = new List<string>();
            decimal total = 0;
            void Add(decimal amount, string text) { amount = decimal.Round(amount, 2); if (amount > 0) { total += amount; parts.Add(text); } }

            // 1. receipts above the plan's allowance (the plan's own price, in the plan's currency)
            if (plan.MaxReceiptsPerMonth > 0 && plan.PricePerExtraReceipt > 0)
            {
                var used = await _db.Receipts.AsNoTracking().CountAsync(r => r.ClientId == clientId && r.CreatedAt >= from && r.CreatedAt < now &&
                    r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held && r.Status != ReceiptStatus.Rejected, ct);
                var extra = Math.Max(0, used - plan.MaxReceiptsPerMonth * Months(from, now));
                Add(extra * plan.PricePerExtraReceipt, $"{extra} extra receipt(s) x {plan.PricePerExtraReceipt:0.####}");
            }

            // 2. usage plans (developer / API): every live device for the period ahead, and every receipt per 1,000
            if (plan.PricePerDeviceMonthly > 0 || plan.PricePerDeviceYearly > 0)
            {
                var live = await _db.Devices.AsNoTracking().CountAsync(d => d.ClientId == clientId && d.IsActive && d.Environment == (int)global::Fdms.Application.Fdms.FdmsEnvironment.Production, ct);
                // a yearly period uses the yearly device price when there is one
                if (ahead >= 12 && plan.PricePerDeviceYearly > 0) Add(live * plan.PricePerDeviceYearly * ahead / 12m, $"{live} live device(s) x {plan.PricePerDeviceYearly:0.##} a year");
                else Add(live * plan.PricePerDeviceMonthly * ahead, $"{live} live device(s) x {plan.PricePerDeviceMonthly:0.##} x{ahead} month(s)");
            }
            if (plan.ReceiptPricing is "metered" or "tiered")
            {
                var issued = await _db.Receipts.AsNoTracking().CountAsync(r => r.ClientId == clientId && r.CreatedAt >= from && r.CreatedAt < now &&
                    r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held && r.Status != ReceiptStatus.Rejected, ct);
                if (ReceiptCharge(plan, issued, Months(from, now)) is { } charge) Add(charge.Amount, charge.Text);
            }

            // 3. the owner's usage prices, for companies billed in their currency
            if (p.Charge && string.Equals(p.Currency, plan.Currency, StringComparison.OrdinalIgnoreCase))
            {
                if (p.SmsPrice > 0)
                {
                    var platformAccounts = _db.SmsAccounts.Where(a => a.ClientId == null).Select(a => (Guid?)a.Id);
                    var sms = await _db.SmsMessages.AsNoTracking().CountAsync(m => m.ClientId == clientId && m.Status == "Sent" && m.Kind != "test" && m.CreatedAt >= from && m.CreatedAt < now
                        && platformAccounts.Contains(m.AccountId), ct);
                    Add(sms * p.SmsPrice, $"{sms} SMS x {p.SmsPrice:0.####}");
                }
                if (p.WhatsAppNumberMonthly > 0 && await _db.WhatsAppAccounts.AsNoTracking().AnyAsync(a => a.ClientId == clientId && a.Enabled, ct))
                    Add(p.WhatsAppNumberMonthly * ahead, $"own WhatsApp number x{ahead} month(s)");
                if (p.LiveDeviceMonthly > 0)
                {
                    var live = await _db.Devices.AsNoTracking().CountAsync(d => d.ClientId == clientId && d.IsActive && d.Environment == (int)global::Fdms.Application.Fdms.FdmsEnvironment.Production, ct);
                    if (live > 0) Add(live * p.LiveDeviceMonthly * ahead, $"{live} live device(s) x{ahead} month(s)");
                }
                if (client.PaidDevices > 0) Add(client.PaidDevices * p.ExtraDeviceMonthly * ahead, $"{client.PaidDevices} extra device(s) x{ahead} month(s)");
                if (client.PaidUsers > 0) Add(client.PaidUsers * p.ExtraUserMonthly * ahead, $"{client.PaidUsers} extra user(s) x{ahead} month(s)");
                if (client.PaidBranches > 0) Add(client.PaidBranches * p.ExtraBranchMonthly * ahead, $"{client.PaidBranches} extra branch(es) x{ahead} month(s)");
            }

            client.UsageBilledUntil = now;   // saved with the invoice
            return total > 0 ? (total, string.Join(", ", parts)) : null;
        }
    }

    public sealed class AddonService : IAddonService
    {
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private const int MaxEach = 100;
        private readonly FdmsDbContext _db;
        private readonly UsagePricingStore _prices;
        private readonly IBillingService _billing;
        private readonly ICallerScope _scope;
        public AddonService(FdmsDbContext db, UsagePricingStore prices, IBillingService billing, ICallerScope scope) { _db = db; _prices = prices; _billing = billing; _scope = scope; }

        public sealed record Addons(int Devices, int Users, int Branches);
        public static Addons Parse(string? json) { try { return (json == null ? null : JsonSerializer.Deserialize<Addons>(json, J)) ?? new Addons(0, 0, 0); } catch (JsonException) { return new Addons(0, 0, 0); } }
        public static string Summary(Addons a) => string.Join(", ", new[] { (a.Devices, "device"), (a.Users, "user"), (a.Branches, "branch") }
            .Where(x => x.Item1 > 0).Select(x => $"{x.Item1} extra {x.Item2}{(x.Item1 == 1 ? "" : x.Item2 == "branch" ? "es" : "s")}"));

        // ---- the owner's prices ----

        public Task<UsagePricingDto> PricingAsync(CancellationToken ct) => _prices.LoadAsync(ct);

        public async Task<UsagePricingDto> SavePricingAsync(UsagePricingDto req, CancellationToken ct)
        {
            var currency = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (currency.Length != 3 || !currency.All(char.IsLetter)) throw GatewayException.BadRequest("CURRENCY_INVALID", "The currency is a 3-letter code, e.g. USD.");
            var prices = new[] { req.SmsPrice, req.WhatsAppNumberMonthly, req.ExtraDeviceMonthly, req.ExtraUserMonthly, req.ExtraBranchMonthly, req.GoLiveFee, req.LiveDeviceMonthly };
            if (prices.Any(v => v < 0 || v > 100_000)) throw GatewayException.BadRequest("PRICE_INVALID", "Prices must be zero or more.");
            var p = new UsagePricingDto(req.Charge, currency, decimal.Round(req.SmsPrice, 4), decimal.Round(req.WhatsAppNumberMonthly, 2), decimal.Round(req.ExtraDeviceMonthly, 2),
                decimal.Round(req.ExtraUserMonthly, 2), decimal.Round(req.ExtraBranchMonthly, 2), decimal.Round(req.GoLiveFee, 2), decimal.Round(req.LiveDeviceMonthly, 2));
            _db.AuditLogs.Add(new AuditLog { Action = "settings.usage_pricing", Entity = "Setting", EntityId = UsagePricingStore.Key,
                Detail = $"{(p.Charge ? "on" : "off")}, {p.Currency}: SMS {p.SmsPrice}, WhatsApp {p.WhatsAppNumberMonthly}/mo, device {p.ExtraDeviceMonthly}/mo, user {p.ExtraUserMonthly}/mo, branch {p.ExtraBranchMonthly}/mo, go-live {p.GoLiveFee}, live device {p.LiveDeviceMonthly}/mo" });
            await _prices.SaveAsync(p, _scope.UserName, ct);
            return p;
        }

        // ---- a company's add-ons ----

        private async Task<(Subscription? Sub, Plan? Plan)> PlanOfAsync(Guid clientId, CancellationToken ct)
        {
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            var plan = sub == null ? null : await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
            return (sub, plan);
        }

        private static string? Unavailable(UsagePricingDto p, Subscription? sub, Plan? plan) =>
            sub == null || plan == null ? "Your company is not on a plan."
            : sub.Status == "Cancelled" ? "Your subscription is cancelled."
            : !p.Charge || !string.Equals(p.Currency, plan.Currency, StringComparison.OrdinalIgnoreCase) ? "Add-ons are not offered on your plan. Upgrade your plan for more room."
            : null;

        public async Task<AddonsDto> MineAsync(Guid clientId, CancellationToken ct)
        {
            var c = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            var p = await _prices.LoadAsync(ct);
            var (sub, plan) = await PlanOfAsync(clientId, ct);
            var pending = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId == clientId && i.Kind == "addon" && i.Status == "Open").OrderByDescending(i => i.IssuedAt).FirstOrDefaultAsync(ct);
            return new AddonsDto(c.PaidDevices, c.PaidUsers, c.PaidBranches, p.ExtraDeviceMonthly, p.ExtraUserMonthly, p.ExtraBranchMonthly, p.Currency, Unavailable(p, sub, plan) == null, Unavailable(p, sub, plan),
                pending?.Id, pending?.Number, pending?.Total, pending == null ? null : Summary(Parse(pending.AddonsJson)), sub?.CurrentPeriodEnd);
        }

        private static void Check(ChangeAddonsRequest r)
        {
            if (new[] { r.Devices, r.Users, r.Branches }.Any(v => v < 0 || v > MaxEach)) throw GatewayException.BadRequest("QUANTITY_INVALID", $"Choose between 0 and {MaxEach} of each.");
            if (r.Devices + r.Users + r.Branches == 0) throw GatewayException.BadRequest("QUANTITY_REQUIRED", "Choose at least one add-on.");
        }

        public async Task<AddonsDto> BuyAsync(Guid clientId, ChangeAddonsRequest req, CancellationToken ct)
        {
            Check(req);
            var p = await _prices.LoadAsync(ct);
            var (sub, plan) = await PlanOfAsync(clientId, ct);
            if (Unavailable(p, sub, plan) is { } why) throw GatewayException.Conflict("ADDONS_UNAVAILABLE", why);
            if (plan!.Kind == "connector" && (req.Devices > 0 || req.Branches > 0)) throw GatewayException.Conflict("PLAN_CONNECTOR", "Your plan covers the Zoho Books connector only, so devices and branches cannot be added.");
            if (plan.Kind == PlanKinds.Accounting && req.Devices > 0) throw GatewayException.Conflict("PLAN_ACCOUNTING", "Your plan is for the books only, so fiscal devices cannot be added. Change to a fiscal plan to fiscalise.");
            foreach (var (qty, price, name) in new[] { (req.Devices, p.ExtraDeviceMonthly, "devices"), (req.Users, p.ExtraUserMonthly, "users"), (req.Branches, p.ExtraBranchMonthly, "branches") })
                if (qty > 0 && price <= 0) throw GatewayException.Conflict("ADDONS_UNAVAILABLE", $"Extra {name} are not offered as add-ons. Upgrade your plan instead.");
            if (await _db.BillingInvoices.AnyAsync(i => i.ClientId == clientId && i.Kind == "addon" && i.Status == "Open", ct))
                throw GatewayException.Conflict("ADDON_PENDING", "You already have an add-on invoice waiting. Pay it, or cancel it, first.");

            // the rest of the current period now; from the next renewal they are on the subscription invoice
            var now = DateTime.UtcNow;
            var months = sub!.CurrentPeriodEnd > now ? UsageBilling.Months(now, sub.CurrentPeriodEnd) : 1;   // whole months left, at least one
            var add = new Addons(req.Devices, req.Users, req.Branches);
            var subtotal = (req.Devices * p.ExtraDeviceMonthly + req.Users * p.ExtraUserMonthly + req.Branches * p.ExtraBranchMonthly) * months;
            var inv = await _billing.IssueOneOffAsync(clientId, "addon", $"Add-ons: {Summary(add)} x{months} month(s), to {sub.CurrentPeriodEnd:d MMM yyyy}", subtotal, plan.Currency, ct);
            var row = await _db.BillingInvoices.FirstAsync(i => i.Id == inv.Id, ct);
            row.AddonsJson = JsonSerializer.Serialize(add, J);
            await _db.SaveChangesAsync(ct);
            return await MineAsync(clientId, ct);
        }

        public async Task<AddonsDto> RemoveAsync(Guid clientId, ChangeAddonsRequest req, CancellationToken ct)
        {
            Check(req);
            var c = await _db.Clients.FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            if (req.Devices > c.PaidDevices || req.Users > c.PaidUsers || req.Branches > c.PaidBranches) throw GatewayException.BadRequest("QUANTITY_INVALID", "You cannot remove more add-ons than you have.");
            var (_, plan) = await PlanOfAsync(clientId, ct);
            if (plan != null)
            {
                // still in use: the room would no longer be there
                var devices = await _db.Devices.CountAsync(d => d.ClientId == clientId, ct);
                var users = await _db.Users.CountAsync(u => u.ClientId == clientId && u.Status == "ACTIVE", ct);
                var branches = await _db.Branches.CountAsync(b => b.ClientId == clientId, ct);
                if (plan.MaxDevices > 0 && devices > plan.MaxDevices + c.BonusDevices + c.PaidDevices - req.Devices) throw GatewayException.Conflict("ADDON_IN_USE", "Those extra devices are in use. Remove a device first.");
                if (plan.MaxUsers > 0 && users > plan.MaxUsers + c.BonusUsers + c.PaidUsers - req.Users) throw GatewayException.Conflict("ADDON_IN_USE", "Those extra users are in use. Deactivate a user first.");
                if (plan.MaxBranches > 0 && branches > plan.MaxBranches + c.BonusBranches + c.PaidBranches - req.Branches) throw GatewayException.Conflict("ADDON_IN_USE", "Those extra branches are in use. Remove a branch first.");
            }
            c.PaidDevices -= req.Devices; c.PaidUsers -= req.Users; c.PaidBranches -= req.Branches;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "billing.addons_removed", Entity = "Client", EntityId = clientId.ToString(), Detail = Summary(new Addons(req.Devices, req.Users, req.Branches)) });
            await _db.SaveChangesAsync(ct);
            return await MineAsync(clientId, ct);
        }

        public async Task<AddonsDto> CancelPendingAsync(Guid clientId, CancellationToken ct)
        {
            foreach (var inv in await _db.BillingInvoices.Where(i => i.ClientId == clientId && i.Kind == "addon" && i.Status == "Open").ToListAsync(ct))
            {
                inv.Status = "Void";
                _db.Notifications.RemoveRange(await _db.Notifications.Where(n => n.ClientId == clientId && n.Key == $"invoice:{inv.Id}").ToListAsync(ct));
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "invoice.void", Entity = "BillingInvoice", EntityId = inv.Number, Detail = "add-on invoice cancelled by the company" });
            }
            await _db.SaveChangesAsync(ct);
            return await MineAsync(clientId, ct);
        }
    }
}
