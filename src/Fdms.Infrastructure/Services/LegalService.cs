using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The terms and conditions, the privacy policy and the developer's contact details, kept in the platform settings so the operator edits them in the console and the web and mobile apps
    /// show the same words. Until the operator writes their own, sensible starting texts are shown. Text uses "# Heading" lines and blank-line separated paragraphs.
    /// </summary>
    public sealed class LegalService : ILegalService
    {
        private const string TermsKey = "Legal.Terms", PrivacyKey = "Legal.Privacy", DeveloperKey = "Legal.Developer";
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        public LegalService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

        public const string DefaultTerms = @"# 1. About these terms
These terms govern your use of the FDMS Gateway (the ""Service""), which connects your billing or point-of-sale system to ZIMRA's Fiscal Device Management System (FDMS) so that your invoices are fiscalised, signed and archived. By registering or signing in you agree to them.

# 2. Your account
You must give correct company and contact details and keep your sign-in details secret. You are responsible for everything done with your account, including by people you add and by API tokens you create. Tell us straight away if you think your account has been misused.

# 3. Fiscal responsibility
You remain the taxpayer. You are responsible for the accuracy of the invoices, prices, HS codes and tax rates you submit, for opening and closing your fiscal days, and for meeting your obligations under the laws of Zimbabwe and ZIMRA's rules. The Service passes your data to ZIMRA and shows you ZIMRA's answer; it does not replace your own checks.

# 4. Subscription and payment
The Service is provided on the plan you choose. Fees are invoiced in advance and are due by the date shown. If an invoice is not paid, access can be limited to Billing until it is. Fees already paid are not refundable except where the law requires.

# 5. Acceptable use
Do not misuse the Service: no attempts to break into it, to overload it, to submit false or fictitious invoices, or to use it for anything unlawful. We may suspend an account that does.

# 6. Your data
Your invoices and records stay yours. We keep them for the period the law requires (at least six years for tax records) and make backups. You can ask us for a copy at any time.

# 7. Availability
We work to keep the Service available but cannot promise it will never be interrupted. ZIMRA's systems are outside our control. If ZIMRA is unreachable, the Service can issue receipts offline and send them later where the rules allow.

# 8. Liability
To the extent the law allows, we are not liable for indirect or consequential losses, or for penalties arising from information you gave us or from ZIMRA's systems. Our total liability for any claim is limited to the fees you paid in the three months before it.

# 9. Referral program
Where a referral program is switched on, rewards are as described in the program and can be changed or ended by us. Rewards are earned only on real, paid subscriptions and not on accounts opened to refer yourself.

# 10. Changes and contact
We may update these terms; the date at the top of this page shows when. Questions: use the developer details in the app.";

        public const string DefaultPrivacy = @"# 1. What we collect
Account details (names, email, phone), your company's details (name, TIN, VAT number, address), your fiscal devices and certificates, the invoices and receipts you submit, and technical information such as IP address, browser and sign-in times that we need to keep the Service secure.

# 2. How we use it
To provide the Service: to sign you in, to send your receipts to ZIMRA and return the result, to produce your invoices and reports, to bill you, to send you alerts you asked for, and to keep the Service safe and working.

# 3. Who sees it
Your data is visible to the people in your company according to the roles you give them. It is sent to ZIMRA as the law requires. Payment providers receive only what is needed to take a payment. We do not sell your data.

# 4. Security
Passwords are stored hashed, device private keys and gateway credentials are encrypted, connections use HTTPS, and you can switch on two-step sign-in and passkeys. We keep an audit trail of important actions.

# 5. How long we keep it
Tax records are kept for the period the law requires. Account data is kept while your account is active and for a reasonable time after it closes.

# 6. Your rights
You can ask to see, correct or export your data, and to close your account (records we must keep by law are kept). Use the developer details in the app to contact us.

# 7. Cookies and storage
The web console keeps a sign-in session in your browser's session storage. We do not use advertising trackers.

# 8. Changes
If we change this policy we will update the date at the top of this page.";

        public static DeveloperDto DefaultDeveloper() => new("FDMS Gateway", "A fiscalisation gateway that connects any billing, POS or ERP system to ZIMRA's FDMS: one connection, every invoice fiscalised, signed and archived.", "support@daraerp.co.zw", null, null, "https://fiscalapi.daraerp.co.zw", "Harare, Zimbabwe");

        private async Task<(string? Value, DateTime At)> RowAsync(string key, CancellationToken ct)
        {
            var r = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => new { s.Value, s.UpdatedAt }).FirstOrDefaultAsync(ct);
            return (r?.Value, r?.UpdatedAt ?? new DateTime(2026, 1, 1));
        }

        public async Task<LegalDto> GetAsync(CancellationToken ct)
        {
            var (t, tAt) = await RowAsync(TermsKey, ct); var (p, pAt) = await RowAsync(PrivacyKey, ct); var (d, _) = await RowAsync(DeveloperKey, ct);
            DeveloperDto dev;
            try { dev = string.IsNullOrWhiteSpace(d) ? DefaultDeveloper() : JsonSerializer.Deserialize<DeveloperDto>(d, J) ?? DefaultDeveloper(); } catch (JsonException) { dev = DefaultDeveloper(); }
            return new LegalDto(new LegalDocDto("Terms and conditions", string.IsNullOrWhiteSpace(t) ? DefaultTerms : t, tAt), new LegalDocDto("Privacy policy", string.IsNullOrWhiteSpace(p) ? DefaultPrivacy : p, pAt), dev);
        }

        private async Task SetAsync(string key, string? value, CancellationToken ct)
        {
            var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
            if (string.IsNullOrWhiteSpace(value)) { if (row != null) _db.SystemSettings.Remove(row); return; }   // blank = back to the starting text
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = key });
            row.Value = value; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
        }

        public async Task<LegalDto> SaveAsync(SaveLegalRequest req, CancellationToken ct)
        {
            if ((req.Terms?.Length ?? 0) > 60_000 || (req.Privacy?.Length ?? 0) > 60_000) throw GatewayException.BadRequest("TOO_LONG", "Each document can be at most 60,000 characters.");
            if (req.Terms != null) await SetAsync(TermsKey, req.Terms.Trim(), ct);
            if (req.Privacy != null) await SetAsync(PrivacyKey, req.Privacy.Trim(), ct);
            if (req.Developer != null)
            {
                var d = req.Developer;
                static string? C(string? v, int max, string what) { v = v?.Trim(); if (string.IsNullOrEmpty(v)) return null; if (v.Length > max) throw GatewayException.BadRequest("TOO_LONG", $"{what} can be at most {max} characters."); return v; }
                var name = C(d.Name, 100, "The name") ?? throw GatewayException.BadRequest("NAME_REQUIRED", "Enter the developer or company name.");
                var email = C(d.Email, 200, "The email");
                if (email != null && !email.Contains('@')) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter a valid email address.");
                var site = C(d.Website, 200, "The website");
                if (site != null && !(Uri.TryCreate(site, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))) throw GatewayException.BadRequest("URL_INVALID", "The website must start with http:// or https://.");
                var dev = new DeveloperDto(name, C(d.About, 600, "The description"), email, C(d.Phone, 40, "The phone"), C(d.WhatsApp, 40, "The WhatsApp number"), site, C(d.Address, 300, "The address"));
                await SetAsync(DeveloperKey, JsonSerializer.Serialize(dev, J), ct);
            }
            _db.AuditLogs.Add(new AuditLog { Action = "settings.legal", Entity = "Setting", Detail = "terms, privacy or developer details changed" });
            await _db.SaveChangesAsync(ct);
            return await GetAsync(ct);
        }
    }
}
