using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Billing;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Services
{
    // =================================================================================================================
    /// <summary>
    /// Sends a fiscal invoice to the customer. The link is a Data Protection token (signed, tamper-proof, expires after Share:Days, 90 by default) naming ONE receipt of ONE company, so
    /// it can be opened without signing in and grants nothing else.
    /// </summary>
    public sealed class InvoiceShareService : IInvoiceShareService
    {
        private readonly ITimeLimitedDataProtector _protector;
        private readonly FdmsDbContext _db;
        private readonly IReceiptService _receipts;
        private readonly IEmailSender _email;
        private readonly PublicUrls _urls;
        private readonly InvoiceTemplateService _templates;
        private readonly IConfiguration _cfg;
        private readonly IEmailOutbox _outbox;

        public InvoiceShareService(IDataProtectionProvider dp, FdmsDbContext db, IReceiptService receipts, IEmailSender email, PublicUrls urls, InvoiceTemplateService templates, IConfiguration cfg, IEmailOutbox outbox)
        {
            _protector = dp.CreateProtector("Fdms.InvoiceShare.v1").ToTimeLimitedDataProtector();
            _db = db; _receipts = receipts; _email = email; _urls = urls; _templates = templates; _cfg = cfg; _outbox = outbox;
        }

        private TimeSpan Lifetime => TimeSpan.FromDays(Math.Clamp(_cfg.GetValue("Share:Days", 90), 1, 730));

        public async Task<ShareLinkDto> CreateLinkAsync(Guid clientId, Guid receiptId, CancellationToken ct)
        {
            var r = await _receipts.GetAsync(clientId, receiptId, ct);   // also proves the receipt belongs to the caller
            var expires = DateTimeOffset.UtcNow.Add(Lifetime);
            var token = _protector.Protect($"{clientId:N}.{receiptId:N}", expires);
            var url = (await _urls.AppAsync(ct)).TrimEnd('/') + "/invoice/" + token;
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstAsync(ct);
            return new ShareLinkDto(url, token, expires.UtcDateTime,
                $"Hello, here is your invoice {r.InvoiceNo} for {r.Total.ToString("0.00", CultureInfo.InvariantCulture)} {r.Currency} from {company}. You can view it, download the PDF and verify it with ZIMRA here: {url}");
        }

        public async Task<SendResultDto> EmailAsync(Guid clientId, Guid receiptId, SendInvoiceRequest req, CancellationToken ct)
        {
            var r = await _receipts.GetAsync(clientId, receiptId, ct);
            var to = req.Email?.Trim();
            if (string.IsNullOrEmpty(to) && r.Status != "Held") { try { to = (await _receipts.GetDetailAsync(clientId, receiptId, ct)).Buyer?.BuyerContacts?.Email; } catch (GatewayException) { /* not issued yet */ } }
            if (string.IsNullOrWhiteSpace(to) || !Regex.IsMatch(to, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") || to.Length > 200) throw GatewayException.BadRequest("EMAIL_REQUIRED", "Enter the customer's email address.");
            if (r.Status is "Held" or "Cancelled") throw GatewayException.Conflict("NOT_ISSUED", "This invoice has not been issued, so there is nothing to send yet.");

            var link = await CreateLinkAsync(clientId, receiptId, ct);
            if (!_email.Configured) return new SendResultDto(false, false, to, "Email is not set up on this server. Copy the link or use WhatsApp instead.");

            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstAsync(ct);
            var note = string.IsNullOrWhiteSpace(req.Message) ? "" : $"<p>{WebUtility.HtmlEncode(req.Message.Trim())}</p>";
            var verify = string.IsNullOrEmpty(r.QrUrl) ? "" : $"<p>To check it with ZIMRA: <a href=\"{WebUtility.HtmlEncode(r.QrUrl)}\">verify this invoice</a>.</p>";
            var html = $"<p>Hello,</p>{note}<p><b>{WebUtility.HtmlEncode(company)}</b> has sent you invoice <b>{WebUtility.HtmlEncode(r.InvoiceNo)}</b> for <b>{r.Total:0.00} {WebUtility.HtmlEncode(r.Currency)}</b>. The PDF is attached.</p>" +
                $"<p><a href=\"{WebUtility.HtmlEncode(link.Url)}\">View the invoice online</a></p>{verify}";
            var files = new List<EmailAttachment>();
            if (r.ReceiptGlobalNo > 0) files.Add(new EmailAttachment($"{Safe(r.InvoiceNo)}.pdf", await _receipts.GetPdfAsync(clientId, receiptId, true, ct), "application/pdf"));
            var subject = $"Invoice {r.InvoiceNo} from {company}";
            var result = await _email.DeliverAsync(to, subject, html, files, ct);
            if (!result.Ok)
            {
                if (!result.Temporary) return new SendResultDto(false, true, to, result.Error + " You can still share the link or use WhatsApp.");
                // the mail server is busy or unreachable: keep the email and try again by itself
                await _outbox.EnqueueAsync(clientId, to, subject, html, files, result.Error, ct);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "receipt.email_queued", Entity = "Receipt", EntityId = receiptId.ToString(), Detail = $"{r.InvoiceNo} to {to}: {result.Error}" });
                await _db.SaveChangesAsync(ct);
                return new SendResultDto(false, true, to, "The mail server is busy right now, so the email is queued. It will be tried again automatically (and you will get an alert if it cannot be delivered). Meanwhile you can share the link or use WhatsApp.", Queued: true);
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "receipt.email", Entity = "Receipt", EntityId = receiptId.ToString(), Detail = $"{r.InvoiceNo} sent to {to}" });
            await _db.SaveChangesAsync(ct);
            return new SendResultDto(true, true, to, "Sent.");
        }

        private static string Safe(string s) => Regex.Replace(s, @"[^\w\-.]+", "_");

        private (Guid ClientId, Guid ReceiptId) Open(string token)
        {
            try
            {
                var parts = _protector.Unprotect(token, out var expiry).Split('.');
                if (expiry < DateTimeOffset.UtcNow) throw new GatewayException(410, "LINK_EXPIRED", "This link has expired. Ask the company to send it again.");
                return (Guid.ParseExact(parts[0], "N"), Guid.ParseExact(parts[1], "N"));
            }
            catch (CryptographicException) { throw new GatewayException(404, "LINK_INVALID", "This link is not valid."); }
            catch (FormatException) { throw new GatewayException(404, "LINK_INVALID", "This link is not valid."); }
            catch (IndexOutOfRangeException) { throw new GatewayException(404, "LINK_INVALID", "This link is not valid."); }
        }

        public async Task<PublicInvoiceDto> ResolveAsync(string token, CancellationToken ct)
        {
            var (clientId, id) = Open(token);
            var r = await _receipts.GetAsync(clientId, id, ct);
            var client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == clientId, ct);
            var branch = await _db.Devices.AsNoTracking().Where(d => d.Id == r.DeviceId).Select(d => d.BranchId).FirstOrDefaultAsync(ct);
            var (settings, logo) = await _templates.ResolveAsync(clientId, branch, ct);
            var text = r.Status switch
            {
                "Submitted" => "Fiscalised: ZIMRA has accepted this invoice.",
                "Pending" => "Issued and being delivered to ZIMRA.",
                "OfflineIssued" => "Issued. It reaches ZIMRA when the company's fiscal day is closed.",
                "Held" => "Not issued yet.",
                "Rejected" => "This invoice was rejected and is not valid.",
                "Cancelled" => "This invoice was cancelled.",
                _ => r.Status,
            };
            return new PublicInvoiceDto(client.Name, client.Slug, logo is { Length: > 0 }, settings.Accent ?? "#2563eb", r.InvoiceNo, r.Type, r.Currency, r.Total, r.ReceiptDate, r.Status,
                r.ReceiptGlobalNo > 0 ? r.ReceiptGlobalNo : null, r.ReceiptGlobalNo > 0 ? r.FiscalDayNo : null, r.QrUrl, text);
        }

        public async Task<byte[]> PublicPdfAsync(string token, bool a4, CancellationToken ct)
        {
            var (clientId, id) = Open(token);
            return await _receipts.GetPdfAsync(clientId, id, a4, ct);
        }

        public async Task<byte[]> PublicQrAsync(string token, CancellationToken ct)
        {
            var (clientId, id) = Open(token);
            return await _receipts.GetQrPngAsync(clientId, id, ct);
        }

        public async Task<TemplateLogo?> PublicLogoAsync(string token, CancellationToken ct)
        {
            var (clientId, id) = Open(token);
            var branch = await _db.Receipts.AsNoTracking().Where(r => r.Id == id && r.ClientId == clientId).Join(_db.Devices, r => r.DeviceId, d => d.Id, (r, d) => d.BranchId).FirstOrDefaultAsync(ct);
            var (_, logo) = await _templates.ResolveAsync(clientId, branch, ct);
            return logo is { Length: > 0 } ? new TemplateLogo(logo, logo.Length > 3 && logo[0] == 0xFF ? "image/jpeg" : "image/png") : null;
        }
    }

    // =================================================================================================================
    /// <summary>The company's own sign-in word (/login?company=acme) and the logo and colour that come from its default invoice template.</summary>
    public sealed class BrandService : IBrandService
    {
        private static readonly Regex SlugRx = new("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$", RegexOptions.Compiled);
        private static readonly HashSet<string> Reserved = new() { "app", "api", "admin", "login", "register", "docs", "www", "billing", "invoice", "pricing", "support", "mail", "static", "assets", "demo", "test" };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly FdmsDbContext _db;
        private readonly PublicUrls _urls;
        public BrandService(FdmsDbContext db, PublicUrls urls) { _db = db; _urls = urls; }

        public static string Slugify(string name)
        {
            var s = Regex.Replace(name.ToLowerInvariant().Normalize(NormalizationForm.FormD), @"[^a-z0-9]+", "-").Trim('-');
            if (s.Length > 30) s = s[..30].Trim('-');
            return s.Length < 3 ? "company" : s;
        }

        public Task<string> NewSlugAsync(string name, CancellationToken ct) => UniqueSlugAsync(_db, name, ct);

        public static async Task<string> UniqueSlugAsync(FdmsDbContext db, string name, CancellationToken ct)
        {
            var baseSlug = Slugify(name); var slug = baseSlug; var n = 1;
            while (Reserved.Contains(slug) || await db.Clients.AnyAsync(c => c.Slug == slug, ct)) slug = baseSlug + "-" + (++n);
            return slug;
        }

        private async Task<(string Accent, byte[]? Logo, string? Type)> DefaultLookAsync(Guid clientId, CancellationToken ct)
        {
            var t = await _db.InvoiceTemplates.AsNoTracking().Where(x => x.ClientId == clientId && x.BranchId == null).Select(x => new { x.SettingsJson, x.Logo, x.LogoContentType }).FirstOrDefaultAsync(ct);
            if (t == null) return ("#2563eb", null, null);
            string accent = "#2563eb";
            try { accent = JsonSerializer.Deserialize<InvoiceTemplateSettings>(t.SettingsJson, Json)?.Accent ?? accent; } catch (JsonException) { /* default colour */ }
            return (accent, t.Logo, t.LogoContentType);
        }

        public async Task<BrandDto?> PublicAsync(string slug, CancellationToken ct)
        {
            slug = (slug ?? "").Trim().ToLowerInvariant();
            var c = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug && x.IsActive, ct);
            if (c == null) return null;
            var look = await DefaultLookAsync(c.Id, ct);
            return new BrandDto(c.Name, c.Slug, look.Accent, look.Logo is { Length: > 0 });
        }

        public async Task<TemplateLogo?> PublicLogoAsync(string slug, CancellationToken ct)
        {
            slug = (slug ?? "").Trim().ToLowerInvariant();
            var c = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug && x.IsActive, ct);
            return c == null ? null : await LogoAsync(c.Id, ct);
        }

        public async Task<TemplateLogo?> LogoAsync(Guid clientId, CancellationToken ct)
        {
            var look = await DefaultLookAsync(clientId, ct);
            return look.Logo is { Length: > 0 } ? new TemplateLogo(look.Logo, look.Type ?? "image/png") : null;
        }

        public async Task<CompanyBrandDto> GetAsync(Guid clientId, CancellationToken ct)
        {
            var c = await _db.Clients.FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Company");
            if (string.IsNullOrEmpty(c.Slug)) { c.Slug = await NewSlugAsync(c.Name, ct); await _db.SaveChangesAsync(ct); }
            var look = await DefaultLookAsync(clientId, ct);
            return new CompanyBrandDto(c.Slug, look.Accent, look.Logo is { Length: > 0 }, (await _urls.AppAsync(ct)).TrimEnd('/') + "/login?company=" + c.Slug);
        }

        public async Task<CompanyBrandDto> SaveAsync(Guid clientId, SaveBrandRequest req, CancellationToken ct)
        {
            var slug = (req.Slug ?? "").Trim().ToLowerInvariant();
            if (!SlugRx.IsMatch(slug)) throw GatewayException.BadRequest("SLUG_INVALID", "Use 3 to 40 lower-case letters, digits or dashes (not starting or ending with a dash).");
            if (Reserved.Contains(slug)) throw GatewayException.BadRequest("SLUG_RESERVED", "That word is reserved. Choose another.");
            if (await _db.Clients.AnyAsync(x => x.Slug == slug && x.Id != clientId, ct)) throw GatewayException.Conflict("SLUG_TAKEN", "Another company already uses that word.");
            var c = await _db.Clients.FirstAsync(x => x.Id == clientId, ct);
            c.Slug = slug;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "company.brand", Detail = slug });
            await _db.SaveChangesAsync(ct);
            return await GetAsync(clientId, ct);
        }
    }

    // =================================================================================================================
    /// <summary>
    /// What the plan allows and how much of it is used. It only informs: fiscalising an invoice is a legal duty, so reaching the receipt limit never stops a sale. The company is told at 80% and
    /// at 100%, and can upgrade under Billing.
    /// </summary>
    public sealed class UsageService : IUsageService
    {
        private readonly FdmsDbContext _db;
        public UsageService(FdmsDbContext db) { _db = db; }

        public async Task<UsageMeterDto> GetAsync(Guid clientId, CancellationToken ct)
        {
            var now = DateTime.UtcNow; var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc); var end = start.AddMonths(1).AddDays(-1);
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            var plan = sub == null ? null : await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);

            var receipts = await _db.Receipts.CountAsync(r => r.ClientId == clientId && r.CreatedAt >= start && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held, ct);
            var devices = await _db.Devices.CountAsync(d => d.ClientId == clientId, ct);
            var users = await _db.Users.CountAsync(u => u.ClientId == clientId && u.Status == "ACTIVE", ct);
            var branches = await _db.Branches.CountAsync(b => b.ClientId == clientId, ct);

            static UsageItemDto Item(string key, string label, int used, int limit)
            {
                var pct = limit <= 0 ? 0 : (int)Math.Min(999, Math.Round(100.0 * used / limit));
                return new UsageItemDto(key, label, used, Math.Max(0, limit), pct, limit <= 0 ? "ok" : pct >= 100 ? "over" : pct >= 80 ? "warn" : "ok");
            }
            var items = new List<UsageItemDto>
            {
                Item("receipts", "Receipts this month", receipts, plan?.MaxReceiptsPerMonth ?? 0), Item("devices", "Devices", devices, plan?.MaxDevices ?? 0),
                Item("users", "Users", users, plan?.MaxUsers ?? 0), Item("branches", "Branches", branches, plan?.MaxBranches ?? 0),
            };
            return new UsageMeterDto(plan?.Name, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), plan != null, items);
        }
    }

    // =================================================================================================================
    /// <summary>Items to suggest while typing an invoice: the company's catalogue first, then lines it has issued before (name, HS code, price, tax), newest first.</summary>
    public sealed class SuggestService : ISuggestService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        public SuggestService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        public async Task<IReadOnlyList<ItemSuggestionDto>> ItemsAsync(Guid clientId, string? q, int take, CancellationToken ct)
        {
            take = Math.Clamp(take, 1, 30);
            q = q?.Trim();
            var result = new List<ItemSuggestionDto>();

            var products = _db.Products.AsNoTracking().Where(p => p.ClientId == clientId && p.IsActive);
            if (!string.IsNullOrEmpty(q)) products = products.Where(p => p.Name.Contains(q) || (p.Code != null && p.Code.Contains(q)) || (p.HsCode != null && p.HsCode.Contains(q)));
            foreach (var p in await products.OrderBy(p => p.Name).Take(take).ToListAsync(ct))
                result.Add(new ItemSuggestionDto(p.Name, p.HsCode, p.UnitPrice, p.Currency, p.IsExempt ? null : p.TaxPercent, p.IsExempt, "catalogue"));

            if (result.Count < take)
            {
                var recent = await _db.Receipts.AsNoTracking().InScope(_scope).Where(r => r.ClientId == clientId && r.Type == ReceiptType.FiscalInvoice && r.PayloadJson != "")
                    .OrderByDescending(r => r.CreatedAt).Select(r => new { r.PayloadJson, r.Currency }).Take(300).ToListAsync(ct);
                var seen = new HashSet<string>(result.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var r in recent)
                {
                    ReceiptPayload? p;
                    try { p = JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options); } catch (JsonException) { continue; }
                    if (p == null) continue;
                    foreach (var l in p.ReceiptLines.Where(l => l.ReceiptLineType != "Discount"))
                    {
                        var name = l.ReceiptLineName?.Trim();
                        if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                        if (!string.IsNullOrEmpty(q) && !name.Contains(q, StringComparison.OrdinalIgnoreCase) && !(l.ReceiptLineHSCode ?? "").Contains(q)) continue;
                        result.Add(new ItemSuggestionDto(name, l.ReceiptLineHSCode, Math.Abs(l.ReceiptLinePrice), r.Currency, l.TaxPercent, l.TaxPercent == null, "history"));
                        if (result.Count >= take) return result;
                    }
                }
            }
            return result;
        }
    }
}
