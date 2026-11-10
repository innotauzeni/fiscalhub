using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>Plans and prices. Everything about a plan (name, limits, price, trial) is edited in the console; nothing is hard-coded.</summary>
    public sealed class PlanService : IPlanService
    {
        private static readonly Regex Code = new("^[a-z0-9][a-z0-9-]{1,19}$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        public PlanService(FdmsDbContext db) => _db = db;

        public async Task<IReadOnlyList<PlanDto>> ListAsync(bool publicOnly, CancellationToken ct)
        {
            var subs = await _db.Subscriptions.AsNoTracking().GroupBy(s => s.PlanId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var q = _db.Plans.AsNoTracking().AsQueryable();
            if (publicOnly) q = q.Where(p => p.IsActive && p.IsPublic);
            return (await q.OrderBy(p => p.SortOrder).ThenBy(p => p.PriceMonthly).ToListAsync(ct)).Select(p => ToDto(p, subs.GetValueOrDefault(p.Id))).ToList();
        }

        public async Task<PlanDto> SaveAsync(SavePlanRequest r, CancellationToken ct)
        {
            var code = (r.Code ?? "").Trim().ToLowerInvariant();
            if (!Code.IsMatch(code)) throw GatewayException.BadRequest("CODE_INVALID", "The plan code is 2-20 lowercase letters, digits or - (e.g. starter).");
            var name = r.Name?.Trim() ?? "";
            if (name.Length < 2 || name.Length > 80) throw GatewayException.BadRequest("NAME_INVALID", "Give the plan a name (2-80 characters).");
            if (r.PriceMonthly < 0 || r.PriceYearly < 0 || r.PriceMonthly > 1_000_000 || r.PriceYearly > 10_000_000) throw GatewayException.BadRequest("PRICE_INVALID", "Prices must be zero or more.");
            var currency = (r.Currency ?? "USD").Trim().ToUpperInvariant();
            if (!Regex.IsMatch(currency, "^[A-Z]{3}$")) throw GatewayException.BadRequest("CURRENCY_INVALID", "Currency must be a 3-letter code such as USD.");
            if (new[] { r.MaxDevices, r.MaxUsers, r.MaxBranches, r.MaxReceiptsPerMonth }.Any(v => v < 0)) throw GatewayException.BadRequest("LIMIT_INVALID", "Limits must be zero (unlimited) or more.");
            if (r.TrialDays is < 0 or > 90) throw GatewayException.BadRequest("TRIAL_INVALID", "A trial is 0-90 days.");
            var features = (r.Features ?? Array.Empty<string>()).Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            if (features.Count > 12 || features.Any(f => f.Length > 100)) throw GatewayException.BadRequest("FEATURES_INVALID", "At most 12 features of up to 100 characters.");
            if (await _db.Plans.AnyAsync(p => p.Code == code && p.Id != r.Id, ct)) throw GatewayException.Conflict("PLAN_CODE_EXISTS", "Another plan already uses that code.");

            var p = r.Id == 0 ? new Plan() : await _db.Plans.FirstOrDefaultAsync(x => x.Id == r.Id, ct) ?? throw GatewayException.NotFound("Plan");
            p.Code = code; p.Name = name; p.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(); p.PriceMonthly = decimal.Round(r.PriceMonthly, 2); p.PriceYearly = decimal.Round(r.PriceYearly, 2);
            p.Currency = currency; p.MaxDevices = r.MaxDevices; p.MaxUsers = r.MaxUsers; p.MaxBranches = r.MaxBranches; p.MaxReceiptsPerMonth = r.MaxReceiptsPerMonth;
            if (r.Modules is { } mods)
            {
                var bad = mods.Where(m => PlanModules.Find(m) == null).ToList();
                if (bad.Count > 0) throw GatewayException.BadRequest("MODULE_INVALID", "Unknown module: " + string.Join(", ", bad));
                p.Modules = string.Join(",", PlanModules.All.Select(m => m.Key).Where(mods.Contains));
            }
            if (p.Kind == "connector" && PlanModules.Parse(p.Modules) is { } inc && !inc.Contains("zoho"))
                p.Modules = string.Join(",", inc.Append("zoho"));   // the connector plan is the Zoho connector
            if (p.Kind == PlanKinds.Accounting && PlanModules.Parse(p.Modules) is { } books && !books.Contains("accounting"))
                p.Modules = string.Join(",", books.Append("accounting"));   // the accounting plan is the books
            PlanModuleAccess.Changed();
            if (r.PricePerDeviceMonthly is { } perDevice) p.PricePerDeviceMonthly = perDevice is >= 0 and <= 100_000 ? decimal.Round(perDevice, 2) : throw GatewayException.BadRequest("PRICE_INVALID", "The price per device must be zero or more.");
            if (r.PricePerThousandReceipts is { } perThousand) p.PricePerThousandReceipts = perThousand is >= 0 and <= 100_000 ? decimal.Round(perThousand, 4) : throw GatewayException.BadRequest("PRICE_INVALID", "The price per 1,000 receipts must be zero or more.");
            static decimal Money(decimal v, string what) => v is >= 0 and <= 1_000_000 ? decimal.Round(v, 4) : throw GatewayException.BadRequest("PRICE_INVALID", $"The {what} must be zero or more.");
            if (r.PricePerDeviceYearly is { } perDeviceYear) p.PricePerDeviceYearly = Money(perDeviceYear, "yearly price per device");
            if (r.ReceiptMinimumMonthly is { } min) p.ReceiptMinimumMonthly = Money(min, "monthly minimum");
            if (r.ReceiptCapMonthly is { } cap) p.ReceiptCapMonthly = Money(cap, "monthly cap");
            if (p.ReceiptCapMonthly > 0 && p.ReceiptCapMonthly < p.ReceiptMinimumMonthly) throw GatewayException.BadRequest("PRICE_INVALID", "The monthly cap cannot be below the monthly minimum.");
            if (r.ReceiptTiers is { } tiers)
            {
                if (tiers.Count > 10) throw GatewayException.BadRequest("TIERS_INVALID", "Use at most 10 volume bands.");
                int? last = 0;
                for (var i = 0; i < tiers.Count; i++)
                {
                    var t = tiers[i];
                    if (t.Price < 0) throw GatewayException.BadRequest("TIERS_INVALID", "Band prices must be zero or more.");
                    if (t.UpTo == null && i != tiers.Count - 1) throw GatewayException.BadRequest("TIERS_INVALID", "Only the last band can be open-ended.");
                    if (t.UpTo is { } up && (up <= 0 || last == null || up <= last)) throw GatewayException.BadRequest("TIERS_INVALID", "Bands must go up, e.g. up to 1,000, then up to 5,000.");
                    last = t.UpTo;
                }
                p.ReceiptTiersJson = tiers.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(tiers.Select(t => new { upTo = t.UpTo, price = decimal.Round(t.Price, 2) }));
            }
            if (r.ReceiptPricing != null)
            {
                if (r.ReceiptPricing is not ("flat" or "metered" or "tiered")) throw GatewayException.BadRequest("PRICING_INVALID", "Receipt pricing is flat, metered (per 1,000) or tiered (volume bands).");
                p.ReceiptPricing = r.ReceiptPricing;
            }
            if (p.ReceiptPricing == "tiered" && UsageBilling.Tiers(p.ReceiptTiersJson).Count == 0) throw GatewayException.BadRequest("TIERS_INVALID", "Add at least one volume band.");
            if (p.ReceiptPricing == "metered" && p.PricePerThousandReceipts <= 0 && p.ReceiptMinimumMonthly <= 0 && r.PricePerThousandReceipts is not > 0)
                throw GatewayException.BadRequest("PRICE_INVALID", "Set the price per 1,000 receipts (or a monthly minimum).");
            if (r.PricePerExtraReceipt is { } extra) p.PricePerExtraReceipt = extra is >= 0 and <= 1000 ? decimal.Round(extra, 4) : throw GatewayException.BadRequest("PRICE_INVALID", "The price per extra receipt must be zero or more.");
            p.Features = string.Join("\n", features); p.TrialDays = r.TrialDays; p.IsActive = r.IsActive; p.IsPublic = r.IsPublic; p.SortOrder = r.SortOrder;
            if (r.Kind != null) p.Kind = PlanKinds.All.Contains(r.Kind) ? r.Kind : throw GatewayException.BadRequest("KIND_INVALID", "A plan is fiscal (the full product), connector (Zoho Books connector only) or accounting (the books only, no fiscal devices).");
            if (p.Kind == PlanKinds.Accounting && PlanModules.Parse(p.Modules) is { } withBooks && !withBooks.Contains("accounting")) p.Modules = string.Join(",", withBooks.Append("accounting"));
            if (r.Id == 0) _db.Plans.Add(p);
            _db.AuditLogs.Add(new AuditLog { Action = r.Id == 0 ? "plan.create" : "plan.update", Entity = "Plan", EntityId = code, Detail = $"{name}: {p.PriceMonthly:0.00}/{p.PriceYearly:0.00} {currency}" });
            await _db.SaveChangesAsync(ct);
            return ToDto(p, await _db.Subscriptions.CountAsync(s => s.PlanId == p.Id, ct));
        }

        public async Task DeleteAsync(int id, CancellationToken ct)
        {
            var p = await _db.Plans.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Plan");
            if (await _db.Subscriptions.AnyAsync(s => s.PlanId == id, ct) || await _db.BillingInvoices.AnyAsync(i => i.PlanId == id || i.ChangeToPlanId == id, ct))
                throw GatewayException.Conflict("PLAN_IN_USE", "Companies or invoices still use this plan. Deactivate it instead, so it stops being offered.");
            _db.Plans.Remove(p);
            _db.AuditLogs.Add(new AuditLog { Action = "plan.delete", Entity = "Plan", EntityId = p.Code, Detail = p.Name });
            await _db.SaveChangesAsync(ct);
        }

        public static PlanDto ToDto(Plan p, int subscribers) => new(p.Id, p.Code, p.Name, p.Description, p.PriceMonthly, p.PriceYearly, p.Currency, p.MaxDevices, p.MaxUsers, p.MaxBranches, p.MaxReceiptsPerMonth,
            p.Features.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), p.TrialDays, p.IsActive, p.IsPublic, p.SortOrder, subscribers, p.Kind, p.PricePerExtraReceipt, PlanModules.Parse(p.Modules)?.ToList() ?? PlanModules.All.Select(m => m.Key).ToList(), p.PricePerDeviceMonthly, p.PricePerThousandReceipts,
            p.PricePerDeviceYearly, p.ReceiptPricing, p.ReceiptMinimumMonthly, p.ReceiptCapMonthly, UsageBilling.Tiers(p.ReceiptTiersJson));
    }

    /// <summary>
    /// The operator's identity on the invoices it sends (name, address, tax number, logo, colour, wording) and the bank details shown to people who pay by
    /// transfer. Kept in the settings table; the PDF is previewed live in the console like the clients' own invoice templates.
    /// </summary>
    public sealed class BillingProfileService : IBillingProfileService
    {
        private const string ProfileKey = "BillingProfile", LogoKey = "BillingLogo", CacheKey = "setting:billing-profile";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly Regex Colour = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        private readonly IPaynowGateway _paynow;
        private readonly IConfiguration _cfg;
        private readonly IMemoryCache _cache;
        public BillingProfileService(FdmsDbContext db, IPaynowGateway paynow, IConfiguration cfg, IMemoryCache cache) { _db = db; _paynow = paynow; _cfg = cfg; _cache = cache; }

        public async Task<BillingProfile> LoadAsync(CancellationToken ct) =>
            await _cache.GetOrCreateAsync(CacheKey, async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                var json = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == ProfileKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
                try { return json == null ? new BillingProfile { CompanyName = "FDMS Gateway" } : JsonSerializer.Deserialize<BillingProfile>(json, Json) ?? new BillingProfile(); }
                catch (JsonException) { return new BillingProfile { CompanyName = "FDMS Gateway" }; }
            }) ?? new BillingProfile();

        public async Task<BillingProfileDto> GetAsync(CancellationToken ct)
        {
            var on = await _paynow.IsConfiguredAsync(ct);
            return new(await LoadAsync(ct), await _db.SystemSettings.AnyAsync(s => s.Key == LogoKey, ct), on, on ? "live" : "off");
        }

        private static string? Clean(string? s, int max, string field)
        {
            var v = s?.Replace("\r\n", "\n").Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (v.Length > max) throw GatewayException.BadRequest("PROFILE_TOO_LONG", $"{field} may be at most {max} characters.");
            if (v.Any(c => char.IsControl(c) && c != '\n')) throw GatewayException.BadRequest("PROFILE_INVALID", $"{field} contains characters that cannot be printed.");
            return v;
        }

        private static BillingProfile Validate(BillingProfile p)
        {
            if (p == null) throw GatewayException.BadRequest("PROFILE_REQUIRED", "Details are required.");
            if (!Colour.IsMatch(p.Accent ?? "")) throw GatewayException.BadRequest("COLOR_INVALID", "The colour must look like #2563eb.");
            var prefix = (p.InvoicePrefix ?? "").Trim().ToUpperInvariant();
            if (!Regex.IsMatch(prefix, "^[A-Z0-9]{2,8}$")) throw GatewayException.BadRequest("PREFIX_INVALID", "The invoice prefix is 2-8 letters or digits (e.g. INV).");
            if (p.TaxPercent is < 0 or > 100) throw GatewayException.BadRequest("TAX_INVALID", "Tax must be between 0 and 100 percent.");
            var name = Clean(p.CompanyName, 120, "Company name") ?? throw GatewayException.BadRequest("NAME_REQUIRED", "The company name is required.");
            return new BillingProfile
            {
                CompanyName = name, Address = Clean(p.Address, 300, "Address"), TaxNumber = Clean(p.TaxNumber, 40, "Tax number"), Email = Clean(p.Email, 150, "Email"), Phone = Clean(p.Phone, 40, "Phone"),
                Website = Clean(p.Website, 150, "Website"), Accent = p.Accent!, InvoicePrefix = prefix, TaxPercent = decimal.Round(p.TaxPercent, 2),
                BankName = Clean(p.BankName, 100, "Bank name"), AccountName = Clean(p.AccountName, 100, "Account name"), AccountNumber = Clean(p.AccountNumber, 50, "Account number"),
                BranchCode = Clean(p.BranchCode, 30, "Branch code"), Swift = Clean(p.Swift, 20, "SWIFT code"), PaymentInstructions = Clean(p.PaymentInstructions, 600, "Payment instructions"),
                Terms = Clean(p.Terms, 1000, "Terms"), Footer = Clean(p.Footer, 300, "Footer"),
            };
        }

        public async Task<BillingProfileDto> SaveAsync(BillingProfile profile, CancellationToken ct)
        {
            var clean = Validate(profile);
            var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == ProfileKey, ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = ProfileKey });
            row.Value = JsonSerializer.Serialize(clean, Json); row.UpdatedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { Action = "settings.billing", Entity = "Setting", EntityId = ProfileKey, Detail = "billing details updated" });
            await _db.SaveChangesAsync(ct);
            _cache.Remove(CacheKey);
            return await GetAsync(ct);
        }

        public async Task SetLogoAsync(byte[] bytes, CancellationToken ct)
        {
            if (bytes.Length == 0 || bytes.Length > InvoiceTemplateService.MaxLogoBytes) throw GatewayException.BadRequest("LOGO_SIZE", $"The logo must be an image of at most {InvoiceTemplateService.MaxLogoBytes / 1000} KB.");
            if (!(bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50) && !(bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)) throw GatewayException.BadRequest("LOGO_TYPE", "The logo must be a PNG or JPEG image.");
            if (!Services.ReceiptPdfRenderer.CanDecode(bytes)) throw GatewayException.BadRequest("LOGO_UNREADABLE", "That image file is damaged or unsupported - export it again as PNG or JPEG.");
            var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == LogoKey, ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = LogoKey });
            row.Value = Convert.ToBase64String(bytes); row.UpdatedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { Action = "settings.billing-logo", Entity = "Setting", EntityId = LogoKey, Detail = "billing logo changed" });
            await _db.SaveChangesAsync(ct);
            _cache.Remove("setting:billing-logo");
        }

        public async Task RemoveLogoAsync(CancellationToken ct)
        {
            var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == LogoKey, ct);
            if (row != null) { _db.SystemSettings.Remove(row); await _db.SaveChangesAsync(ct); }
            _cache.Remove("setting:billing-logo");
        }

        public async Task<TemplateLogo?> GetLogoAsync(CancellationToken ct)
        {
            var b64 = await _cache.GetOrCreateAsync("setting:billing-logo", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                return await _db.SystemSettings.AsNoTracking().Where(s => s.Key == LogoKey).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? "";
            });
            if (string.IsNullOrEmpty(b64)) return null;
            var bytes = Convert.FromBase64String(b64);
            return new TemplateLogo(bytes, bytes[0] == 0x89 ? "image/png" : "image/jpeg");
        }

        public async Task<byte[]> PreviewAsync(BillingProfile profile, CancellationToken ct)
        {
            var p = Validate(profile);
            var now = DateTime.UtcNow;
            var inv = new BillingInvoice
            {
                Number = p.InvoicePrefix + "-" + now.Year + "-000123", Description = "Business plan, monthly", Subtotal = 45m, TaxPercent = p.TaxPercent, TaxAmount = decimal.Round(45m * p.TaxPercent / 100, 2),
                Total = decimal.Round(45m + 45m * p.TaxPercent / 100, 2), Currency = "USD", Status = "Open", IssuedAt = now, DueAt = now.AddDays(7), PeriodStart = now, PeriodEnd = now.AddMonths(1),
            };
            return BillingInvoicePdf.RenderPng(inv, "Sample Trading (Pvt) Ltd", "accounts@sample.example", p, (await GetLogoAsync(ct))?.Bytes);
        }
    }
}
