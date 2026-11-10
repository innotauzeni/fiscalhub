using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Billing;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Integrations
{
    /// <summary>Delivers bridge events over HTTPS with the same protections as company webhooks (no redirects, no internal addresses).</summary>
    public sealed class HttpBridgeSender : IBridgeSender
    {
        private readonly HttpClient _http;
        public HttpBridgeSender(IConfiguration cfg) => _http = WebhookDeliveryJob.SafeClient(cfg);

        public async Task<string?> SendAsync(string url, string eventType, long deliveryId, string timestamp, string signature, string body, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("X-FiscalHub-Event", eventType);
                req.Headers.TryAddWithoutValidation("X-FiscalHub-Delivery", deliveryId.ToString(CultureInfo.InvariantCulture));
                req.Headers.TryAddWithoutValidation("X-FiscalHub-Timestamp", timestamp);
                req.Headers.TryAddWithoutValidation("X-FiscalHub-Signature", "sha256=" + signature);
                using var res = await _http.SendAsync(req, ct);
                return res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return ex.Message.Length > 400 ? ex.Message[..400] : ex.Message; }
        }
    }

    /// <summary>
    /// The Zoho bridge. A company lets a partner reach chosen Zoho Books modules (<see cref="BridgeGrant"/>); the partner's system calls the gateway with a bridge
    /// key and the call is passed to that company's Zoho with the gateway's stored sign-in, in Zoho's own format. Changes in watched modules are found every few
    /// minutes (or at once when Zoho's workflow webhook calls) and sent to the partner's webhook, signed and retried, and kept in a feed it can read.
    /// The partner never holds a Zoho token, and sees only the companies and modules it was given.
    /// </summary>
    public sealed class BridgeService : IBridgeService
    {
        private sealed record Module(string Key, string Label, string Path, bool ReadOnly, bool Watchable, string Root, string IdField);

        private static readonly Module[] Catalog =
        {
            new("contacts", "Contacts (customers and vendors)", "contacts", false, true, "contacts", "contact_id"),
            new("items", "Items", "items", false, true, "items", "item_id"),
            new("invoices", "Invoices", "invoices", false, true, "invoices", "invoice_id"),
            new("creditnotes", "Credit notes", "creditnotes", false, true, "creditnotes", "creditnote_id"),
            new("estimates", "Quotes (estimates)", "estimates", false, true, "estimates", "estimate_id"),
            new("salesorders", "Sales orders", "salesorders", false, true, "salesorders", "salesorder_id"),
            new("customerpayments", "Customer payments", "customerpayments", false, true, "customerpayments", "payment_id"),
            new("purchaseorders", "Purchase orders", "purchaseorders", false, true, "purchaseorders", "purchaseorder_id"),
            new("bills", "Bills", "bills", false, true, "bills", "bill_id"),
            new("vendorpayments", "Vendor payments", "vendorpayments", false, true, "vendorpayments", "payment_id"),
            new("expenses", "Expenses", "expenses", false, true, "expenses", "expense_id"),
            new("recurringinvoices", "Recurring invoices", "recurringinvoices", false, false, "recurring_invoices", "recurring_invoice_id"),
            new("projects", "Projects", "projects", false, false, "projects", "project_id"),
            new("journals", "Manual journals", "journals", false, false, "journals", "journal_id"),
            new("chartofaccounts", "Chart of accounts (read only)", "chartofaccounts", true, false, "chartofaccounts", "account_id"),
            new("bankaccounts", "Bank accounts (read only)", "bankaccounts", true, false, "bankaccounts", "account_id"),
            new("taxes", "Tax rates (read only)", "settings/taxes", true, false, "taxes", "tax_id"),
            new("currencies", "Currencies (read only)", "settings/currencies", true, false, "currencies", "currency_id"),
        };

        private static readonly Regex Segment = new(@"^[A-Za-z0-9_.\-]{1,80}$", RegexOptions.Compiled);
        private static readonly Regex ZohoTime = new(@"([+-]\d{2})(\d{2})$", RegexOptions.Compiled);
        private static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24) };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly object CountLock = new();
        private const int MaxBodyBytes = 1_000_000;

        private readonly FdmsDbContext _db;
        private readonly IZohoApi _zoho;
        private readonly IntegrationService _integration;
        private readonly IntegrationSettings _settings;
        private readonly IBridgeSender _sender;
        private readonly PublicUrls _urls;
        private readonly DeviceLocks _locks;
        private readonly ICallerScope _scope;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _cfg;
        private readonly ILogger<BridgeService> _log;
        private readonly IDataProtector _secrets;

        public BridgeService(FdmsDbContext db, IZohoApi zoho, IntegrationService integration, IntegrationSettings settings, IBridgeSender sender, PublicUrls urls, DeviceLocks locks,
            ICallerScope scope, IMemoryCache cache, IConfiguration cfg, IDataProtectionProvider dp, ILogger<BridgeService> log)
        {
            _db = db; _zoho = zoho; _integration = integration; _settings = settings; _sender = sender; _urls = urls; _locks = locks; _scope = scope; _cache = cache; _cfg = cfg; _log = log;
            _secrets = dp.CreateProtector("Fdms.Bridge.Webhook.v1");
        }

        public IReadOnlyList<BridgeModuleDto> Modules { get; } = Catalog.Select(m => new BridgeModuleDto(m.Key, m.Label, m.Path, m.ReadOnly, m.Watchable)).ToList();

        private static List<string> Split(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        private static string Cut(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];
        private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

        private static List<string> CleanModules(IEnumerable<string>? keys, bool watchableOnly)
        {
            var list = (keys ?? Array.Empty<string>()).Select(k => k?.Trim().ToLowerInvariant() ?? "").Where(k => k.Length > 0).Distinct().ToList();
            var bad = list.FirstOrDefault(k => Catalog.All(m => m.Key != k || (watchableOnly && !m.Watchable)));
            if (bad != null) throw GatewayException.BadRequest("MODULE_INVALID", watchableOnly ? $"Changes in \"{bad}\" cannot be watched." : $"\"{bad}\" is not a Zoho Books module the bridge offers.");
            return Catalog.Where(m => list.Contains(m.Key)).Select(m => m.Key).ToList();   // catalogue order
        }

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot share the company's Zoho Books.");
        }

        private async Task<IntegrationConnection?> ConnectionAsync(Guid clientId, CancellationToken ct) =>
            await _db.IntegrationConnections.FirstOrDefaultAsync(c => c.ClientId == clientId && c.Provider == IntegrationService.Provider, ct);

        private static (bool Ready, string? Reason) Readiness(IntegrationConnection? c) => c switch
        {
            null => (false, "The company has not connected Zoho Books."),
            { Status: "Disconnected" } => (false, "The company has disconnected Zoho Books."),
            { Status: "NeedsReconnect" } => (false, "The company's Zoho sign-in expired; it must reconnect."),
            { Status: "ChooseOrganization" } => (false, "The company has not chosen its Zoho organisation yet."),
            { FullAccess: false } => (false, "The company must reconnect Zoho Books once to allow the bridge (the earlier connection only reached invoices and contacts)."),
            _ => (true, null),
        };

        // =============================================================================================================
        // the company
        // =============================================================================================================

        public async Task<BridgeGrantsDto> GrantsAsync(Guid clientId, CancellationToken ct)
        {
            var grants = await _db.BridgeGrants.AsNoTracking().Where(g => g.ClientId == clientId && g.IsActive).OrderBy(g => g.CreatedAt).ToListAsync(ct);
            var ids = grants.Select(g => g.PartnerId).ToList();
            var partners = await _db.Partners.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var c = await _db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == IntegrationService.Provider, ct);
            var p = await _settings.PricingAsync(ct);
            var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return new BridgeGrantsDto(grants.Select(g => ToDto(g, partners.GetValueOrDefault(g.PartnerId), month)).ToList(), Modules, c?.FullAccess == true,
                p.BridgeMonthlyFee, p.Currency, p.Charge && p.BridgeMonthlyFee > 0);
        }

        private static BridgeGrantDto ToDto(BridgeGrant g, Partner? p, DateTime month) =>
            new(g.Id, g.PartnerId, p?.Name ?? "?", p?.Code ?? "", Split(g.Modules), g.CanWrite, g.IsActive, g.CreatedBy, g.CreatedAt, g.CallsMonth >= month ? g.CallsThisMonth : 0);

        public async Task<BridgeGrantDto> SaveGrantAsync(Guid clientId, SaveBridgeGrantRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct);
            var code = (req.PartnerCode ?? "").Trim();
            if (code.Length == 0) throw GatewayException.BadRequest("PARTNER_REQUIRED", "Enter the partner's code (they find it on their bridge page).");
            var partner = await _db.Partners.FirstOrDefaultAsync(p => p.Code == code && p.IsActive, ct)
                          ?? await _db.Partners.FirstOrDefaultAsync(p => p.Code.ToUpper() == code.ToUpper() && p.IsActive, ct)
                          ?? throw new GatewayException(404, "PARTNER_NOT_FOUND", "No active partner has that code.");
            var modules = CleanModules(req.Modules, false);
            if (modules.Count == 0) throw GatewayException.BadRequest("MODULES_REQUIRED", "Choose at least one Zoho Books module the partner may use.");
            var g = await _db.BridgeGrants.FirstOrDefaultAsync(x => x.ClientId == clientId && x.PartnerId == partner.Id, ct);
            if (g == null) { g = new BridgeGrant { ClientId = clientId, PartnerId = partner.Id }; _db.BridgeGrants.Add(g); }
            var isNew = !g.IsActive || g.CreatedBy == null;
            g.Modules = string.Join(",", modules); g.CanWrite = req.CanWrite; g.IsActive = true; g.RevokedAt = null;
            if (isNew) { g.CreatedAt = DateTime.UtcNow; g.CreatedBy = Cut(_scope.UserName, 200); }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bridge.grant", Entity = "BridgeGrant", EntityId = g.Id.ToString(),
                Detail = $"{partner.Name} ({partner.Code}): {g.Modules}, {(g.CanWrite ? "read and write" : "read only")}" });
            await _db.SaveChangesAsync(ct);
            return ToDto(g, partner, new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        public async Task RevokeGrantAsync(Guid clientId, Guid grantId, CancellationToken ct)
        {
            RequireWholeCompany();
            var g = await _db.BridgeGrants.FirstOrDefaultAsync(x => x.Id == grantId && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Partner access");
            g.IsActive = false; g.RevokedAt = DateTime.UtcNow;
            // changes not yet delivered are not sent after access is withdrawn
            foreach (var e in await _db.BridgeEvents.Where(e => e.ClientId == clientId && e.PartnerId == g.PartnerId && (e.Status == "Pending" || e.Status == "Stored")).ToListAsync(ct))
            { e.Status = "Cancelled"; e.LastError = "The company withdrew the partner's access."; }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bridge.revoke", Entity = "BridgeGrant", EntityId = g.Id.ToString(), Detail = g.PartnerId.ToString() });
            await _db.SaveChangesAsync(ct);
        }

        // =============================================================================================================
        // the partner's portal
        // =============================================================================================================

        private async Task<Partner> PartnerAsync(Guid partnerId, CancellationToken ct) =>
            await _db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partnerId && p.IsActive, ct) ?? throw new GatewayException(403, "PARTNER_INACTIVE", "This partner account is not active.");

        private static BridgeKeyDto ToDto(BridgeKey k) =>
            new(k.Id, k.Name, k.Prefix, k.CreatedBy, k.CreatedAt, k.LastUsedAt, k.RevokedAt, k.CallsDay?.Date == DateTime.UtcNow.Date ? k.CallsToday : 0, k.CallsTotal,
                k.ClientId != null ? Split(k.Modules) : null, k.CanWrite);

        private static BridgeEndpointDto ToDto(BridgeEndpoint? e) => new(e?.WebhookUrl, Split(e?.WatchModules), !string.IsNullOrEmpty(e?.SecretProtected));

        public async Task<BridgePortalDto> PortalAsync(Guid partnerId, CancellationToken ct)
        {
            var partner = await PartnerAsync(partnerId, ct);
            var keys = await _db.BridgeKeys.AsNoTracking().Where(k => k.PartnerId == partnerId).OrderByDescending(k => k.RevokedAt == null).ThenByDescending(k => k.CreatedAt).Take(50).ToListAsync(ct);
            var endpoint = await _db.BridgeEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.PartnerId == partnerId, ct);
            var events = await _db.BridgeEvents.AsNoTracking().Where(e => e.PartnerId == partnerId).OrderByDescending(e => e.Id).Take(50).ToListAsync(ct);
            return new BridgePortalDto(keys.Select(ToDto).ToList(), ToDto(endpoint), await PartnerCompaniesAsync(partnerId, ct),
                events.Select(e => new BridgeEventDto(e.Id, e.ClientId, e.Module, e.RecordId, e.EventType, e.Status, e.Attempts, e.CreatedAt, e.DeliveredAt, e.LastError)).ToList(),
                Modules, (await _urls.ApiAsync(ct)).TrimEnd('/') + "/api/v1/bridge", partner.Code);
        }

        public async Task<CreatedBridgeKeyDto> CreateKeyAsync(Guid partnerId, CreateBridgeKeyRequest req, CancellationToken ct)
        {
            await PartnerAsync(partnerId, ct);
            var name = (req.Name ?? "").Trim();
            if (name.Length < 2 || name.Length > 80) throw GatewayException.BadRequest("NAME_REQUIRED", "Give the key a name (for example \"Production server\").");
            if (await _db.BridgeKeys.CountAsync(k => k.PartnerId == partnerId && k.RevokedAt == null, ct) >= 10) throw GatewayException.Conflict("KEYS_FULL", "You have 10 keys in use. Revoke one you no longer need.");
            var secret = "fhb_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var k = new BridgeKey { PartnerId = partnerId, Name = name, Prefix = secret[..12], KeyHash = Hash(secret), CreatedBy = Cut(_scope.UserName, 200) };
            _db.BridgeKeys.Add(k);
            _db.AuditLogs.Add(new AuditLog { Action = "bridge.key.create", Entity = "BridgeKey", EntityId = k.Id.ToString(), Detail = $"{name} ({k.Prefix}…) for partner {partnerId}" });
            await _db.SaveChangesAsync(ct);
            return new CreatedBridgeKeyDto(ToDto(k), secret);
        }

        public async Task RevokeKeyAsync(Guid partnerId, Guid keyId, CancellationToken ct)
        {
            var k = await _db.BridgeKeys.FirstOrDefaultAsync(x => x.Id == keyId && x.PartnerId == partnerId, ct) ?? throw GatewayException.NotFound("Key");
            if (k.RevokedAt != null) return;
            k.RevokedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { Action = "bridge.key.revoke", Entity = "BridgeKey", EntityId = k.Id.ToString(), Detail = $"{k.Name} ({k.Prefix}…)" });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<SavedBridgeEndpointDto> SaveEndpointAsync(Guid partnerId, SaveBridgeEndpointRequest req, CancellationToken ct)
        {
            await PartnerAsync(partnerId, ct);
            return await SaveEndpointCoreAsync(partnerId, null, req, ct);
        }

        private async Task<SavedBridgeEndpointDto> SaveEndpointCoreAsync(Guid? partnerId, Guid? clientId, SaveBridgeEndpointRequest req, CancellationToken ct)
        {
            var e = partnerId != null ? await _db.BridgeEndpoints.FirstOrDefaultAsync(x => x.PartnerId == partnerId, ct) : await _db.BridgeEndpoints.FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            if (e == null) { e = new BridgeEndpoint { PartnerId = partnerId, ClientId = clientId }; _db.BridgeEndpoints.Add(e); }
            string? secret = null;
            if (string.IsNullOrWhiteSpace(req.WebhookUrl)) { e.WebhookUrl = null; e.SecretProtected = null; }
            else
            {
                var uri = WebhookService.ValidateUrl(req.WebhookUrl.Trim(), _cfg.GetValue<bool>("Webhooks:AllowInsecureTargets"));
                if (uri.ToString().Length > 500) throw GatewayException.BadRequest("WEBHOOK_URL_INVALID", "The address is too long.");
                e.WebhookUrl = uri.ToString();
                if (req.RotateSecret || e.SecretProtected == null)
                {
                    secret = "fhbsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                    e.SecretProtected = _secrets.Protect(secret);
                }
            }
            if (req.WatchModules != null) e.WatchModules = string.Join(",", CleanModules(req.WatchModules, true));
            e.UpdatedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bridge.endpoint", Entity = "BridgeEndpoint", EntityId = e.Id.ToString(), Detail = $"{(e.WebhookUrl == null ? "no webhook" : new Uri(e.WebhookUrl).Host)}; watching {e.WatchModules}{(secret != null ? "; new secret" : "")}" });
            await _db.SaveChangesAsync(ct);
            return new SavedBridgeEndpointDto(ToDto(e), secret);
        }

        // =============================================================================================================
        // the company's own system
        // =============================================================================================================

        public async Task<OwnApiDto> OwnAsync(Guid clientId, CancellationToken ct)
        {
            var keys = await _db.BridgeKeys.AsNoTracking().Where(k => k.ClientId == clientId).OrderByDescending(k => k.RevokedAt == null).ThenByDescending(k => k.CreatedAt).Take(50).ToListAsync(ct);
            var endpoint = await _db.BridgeEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.ClientId == clientId, ct);
            var events = await _db.BridgeEvents.AsNoTracking().Where(e => e.ClientId == clientId && e.PartnerId == null).OrderByDescending(e => e.Id).Take(50).ToListAsync(ct);
            var (ready, why) = Readiness(await _db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId && c.Provider == IntegrationService.Provider, ct));
            if (!await IntegrationAccessService.IsActiveAsync(_db, clientId, ct)) (ready, why) = (false, await IntegrationAccessService.IsApprovedAsync(_db, clientId, ct)
                ? "Your keys are created but stay inactive until the Zoho Books set-up invoice is paid under Billing."
                : "The Zoho Books integration is not switched on for your company (apply for it above).");
            var p = await _settings.PricingAsync(ct);
            var connector = await IntegrationBilling.ConnectorPlanAsync(_db, clientId, ct);
            return new OwnApiDto(clientId, keys.Select(ToDto).ToList(), ToDto(endpoint),
                events.Select(e => new BridgeEventDto(e.Id, e.ClientId, e.Module, e.RecordId, e.EventType, e.Status, e.Attempts, e.CreatedAt, e.DeliveredAt, e.LastError)).ToList(),
                Modules, (await _urls.ApiAsync(ct)).TrimEnd('/') + "/api/v1/bridge", ready, why, p.Charge && p.BridgeMonthlyFee > 0 && !connector, p.BridgeMonthlyFee, p.Currency, connector);
        }

        public async Task<CreatedBridgeKeyDto> CreateOwnKeyAsync(Guid clientId, CreateOwnKeyRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct);
            var name = (req.Name ?? "").Trim();
            if (name.Length < 2 || name.Length > 80) throw GatewayException.BadRequest("NAME_REQUIRED", "Give the key a name, e.g. the name of your system.");
            var modules = CleanModules(req.Modules, false);
            if (modules.Count == 0) throw GatewayException.BadRequest("MODULES_REQUIRED", "Choose at least one Zoho Books module the key may use.");
            if (await _db.BridgeKeys.CountAsync(k => k.ClientId == clientId && k.RevokedAt == null, ct) >= 10) throw GatewayException.Conflict("KEYS_FULL", "You have 10 keys in use. Revoke one you no longer need.");
            var secret = "fhb_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var k = new BridgeKey { ClientId = clientId, Name = name, Prefix = secret[..12], KeyHash = Hash(secret), Modules = string.Join(",", modules), CanWrite = req.CanWrite, CreatedBy = Cut(_scope.UserName, 200) };
            _db.BridgeKeys.Add(k);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bridge.key.create", Entity = "BridgeKey", EntityId = k.Id.ToString(), Detail = $"{name} ({k.Prefix}…): {k.Modules}, {(k.CanWrite ? "read and write" : "read only")}" });
            await _db.SaveChangesAsync(ct);
            return new CreatedBridgeKeyDto(ToDto(k), secret);
        }

        public async Task RevokeOwnKeyAsync(Guid clientId, Guid keyId, CancellationToken ct)
        {
            RequireWholeCompany();
            var k = await _db.BridgeKeys.FirstOrDefaultAsync(x => x.Id == keyId && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Key");
            if (k.RevokedAt != null) return;
            k.RevokedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bridge.key.revoke", Entity = "BridgeKey", EntityId = k.Id.ToString(), Detail = $"{k.Name} ({k.Prefix}…)" });
            await _db.SaveChangesAsync(ct);
        }

        public Task<SavedBridgeEndpointDto> SaveOwnEndpointAsync(Guid clientId, SaveBridgeEndpointRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            return SaveEndpointCoreAsync(null, clientId, req, ct);
        }

        // =============================================================================================================
        // the partner's system
        // =============================================================================================================

        public async Task<BridgeCaller?> ValidateKeyAsync(string key, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith("fhb_", StringComparison.Ordinal) || key.Length > 100) return null;
            var hash = Hash(key);
            var k = await _db.BridgeKeys.AsNoTracking().FirstOrDefaultAsync(x => x.KeyHash == hash && x.RevokedAt == null, ct);
            if (k == null) return null;
            if (k.ClientId is { } clientId)
                return await _db.Clients.AnyAsync(c => c.Id == clientId && c.IsActive, ct) ? new BridgeCaller(null, clientId, k.Id, k.Name, Split(k.Modules), k.CanWrite) : null;
            if (k.PartnerId is not { } partnerId || !await _db.Partners.AnyAsync(p => p.Id == partnerId && p.IsActive, ct)) return null;
            return new BridgeCaller(partnerId, null, k.Id, k.Name, Array.Empty<string>(), false);
        }

        public async Task<IReadOnlyList<BridgeCompanyDto>> CompaniesAsync(BridgeCaller caller, CancellationToken ct)
        {
            if (caller.ClientId is not { } own) return await PartnerCompaniesAsync(caller.PartnerId!.Value, ct);
            var name = await _db.Clients.AsNoTracking().Where(c => c.Id == own).Select(c => c.Name).FirstAsync(ct);
            var conn = await _db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == own && c.Provider == IntegrationService.Provider, ct);
            var (ready, why) = Readiness(conn);
            var created = await _db.BridgeKeys.AsNoTracking().Where(k => k.Id == caller.KeyId).Select(k => k.CreatedAt).FirstAsync(ct);
            return new[] { new BridgeCompanyDto(own, name, conn?.OrganizationId, conn?.OrganizationName, conn?.Status ?? "NotConnected", ready, why, caller.Modules, caller.CanWrite, created) };
        }

        private async Task<IReadOnlyList<BridgeCompanyDto>> PartnerCompaniesAsync(Guid partnerId, CancellationToken ct)
        {
            var grants = await _db.BridgeGrants.AsNoTracking().Where(g => g.PartnerId == partnerId && g.IsActive).ToListAsync(ct);
            var ids = grants.Select(g => g.ClientId).ToList();
            var clients = await _db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id) && c.IsActive).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var conns = await _db.IntegrationConnections.AsNoTracking().Where(c => ids.Contains(c.ClientId) && c.Provider == IntegrationService.Provider).ToDictionaryAsync(c => c.ClientId, ct);
            var active = (await _db.IntegrationAccess.AsNoTracking().Where(a => ids.Contains(a.ClientId) && a.Status == "Active").Select(a => a.ClientId).ToListAsync(ct)).ToHashSet();
            return grants.Where(g => clients.ContainsKey(g.ClientId)).OrderBy(g => clients[g.ClientId]).Select(g =>
            {
                var c = conns.GetValueOrDefault(g.ClientId);
                var (ready, why) = active.Contains(g.ClientId) ? Readiness(c) : (false, "The company's Zoho Books integration is not switched on.");
                return new BridgeCompanyDto(g.ClientId, clients[g.ClientId], c?.OrganizationId, c?.OrganizationName, c?.Status ?? "NotConnected", ready, why, Split(g.Modules), g.CanWrite, g.CreatedAt);
            }).ToList();
        }

        /// <summary>Which module a path belongs to (the longest catalogue path it starts with), after checking every segment.</summary>
        public static (string Path, string? ModuleKey) ParsePath(string? path)
        {
            var segs = (path ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0 || segs.Length > 6 || segs.Any(s => !Segment.IsMatch(s) || s is "." or "..")) return ("", null);
            var clean = string.Join("/", segs);
            var m = Catalog.Where(x => clean.Equals(x.Path, StringComparison.OrdinalIgnoreCase) || clean.StartsWith(x.Path + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Path.Length).FirstOrDefault();
            return (clean, m?.Key);
        }

        /// <summary>The partner's query string without anything that would choose another organisation or carry a token.</summary>
        public static string CleanQuery(string? query)
        {
            if (string.IsNullOrEmpty(query)) return "";
            var parts = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => { var name = Uri.UnescapeDataString(p.Split('=')[0]).ToLowerInvariant(); return name is not ("organization_id" or "authtoken" or "access_token"); });
            var q = string.Join("&", parts);
            if (q.Length > 2000) throw GatewayException.BadRequest("QUERY_TOO_LONG", "The query string is too long.");
            return q;
        }

        private void Throttle(Guid keyId)
        {
            var limit = Math.Clamp(_cfg.GetValue("Integrations:BridgePerMinute", 60), 1, 10000);
            var slot = $"bridge:{keyId:N}:{DateTime.UtcNow:yyyyMMddHHmm}";
            lock (CountLock)
            {
                var n = _cache.GetOrCreate(slot, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2); return new int[1]; })!;
                if (++n[0] > limit) throw new GatewayException(429, "RATE_LIMITED", $"At most {limit} calls a minute per key. Slow down and retry.");
            }
        }

        public async Task<ZohoRawResponse> ProxyAsync(BridgeCaller caller, Guid companyId, string method, string path, string? query, string? body, CancellationToken ct)
        {
            method = method.ToUpperInvariant();
            if (method is not ("GET" or "POST" or "PUT")) throw new GatewayException(405, "METHOD_NOT_ALLOWED", "The bridge reads (GET) and writes (POST, PUT). Deleting in Zoho is not offered.");
            var (clean, moduleKey) = ParsePath(path);
            if (moduleKey == null) throw GatewayException.BadRequest("MODULE_INVALID", "The path must start with a Zoho Books module the bridge offers (see GET /api/v1/bridge/modules).");
            var module = Catalog.First(m => m.Key == moduleKey);
            BridgeGrant? grant = null;
            IReadOnlyList<string> modules; bool canWrite;
            if (caller.ClientId is { } own)
            {
                // a company's own key reaches only that company
                if (own != companyId) throw new GatewayException(404, "COMPANY_NOT_SHARED", "This key belongs to another company.");
                modules = caller.Modules; canWrite = caller.CanWrite;
            }
            else
            {
                grant = await _db.BridgeGrants.FirstOrDefaultAsync(g => g.ClientId == companyId && g.PartnerId == caller.PartnerId && g.IsActive, ct)
                        ?? throw new GatewayException(404, "COMPANY_NOT_SHARED", "This company has not shared its Zoho Books with you.");
                modules = Split(grant.Modules); canWrite = grant.CanWrite;
            }
            if (!modules.Contains(module.Key)) throw new GatewayException(403, "MODULE_NOT_SHARED", caller.ClientId != null ? $"This key does not include \"{module.Label}\". Create a key that does under Integration → Zoho Books." : $"The company has not shared \"{module.Label}\" with you.");
            var write = method != "GET";
            if (write && module.ReadOnly) throw new GatewayException(403, "MODULE_READ_ONLY", $"\"{module.Label}\" can only be read through the bridge.");
            if (write && !canWrite) throw new GatewayException(403, "WRITE_NOT_SHARED", caller.ClientId != null ? "This key is read-only." : "The company shared its Zoho Books with you read-only.");
            if (body != null)
            {
                if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes) throw GatewayException.BadRequest("BODY_TOO_LARGE", "The body may be at most 1 MB.");
                try { using var _ = JsonDocument.Parse(body); } catch (JsonException) { throw GatewayException.BadRequest("BODY_INVALID", "The body must be JSON, as Zoho Books expects it."); }
            }
            await IntegrationAccessService.EnsureActiveAsync(_db, companyId, ct);
            var c = await ConnectionAsync(companyId, ct);
            var (ready, why) = Readiness(c);
            if (!ready) throw GatewayException.Conflict("ZOHO_NOT_READY", why!);
            Throttle(caller.KeyId);

            var access = await _integration.AccessAsync(c!, ct);   // renews the token if needed; 409 ZOHO_RECONNECT when Zoho refuses
            IntegrationService.Count(c!);
            var now = DateTime.UtcNow;
            var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            if (grant != null)
            {
                if (grant.CallsMonth != month) { grant.CallsMonth = month; grant.CallsThisMonth = 0; }
                grant.CallsThisMonth++;
            }
            var key = await _db.BridgeKeys.FirstAsync(k => k.Id == caller.KeyId, ct);
            if (key.CallsDay?.Date != now.Date) { key.CallsDay = now.Date; key.CallsToday = 0; }
            key.CallsToday++; key.CallsTotal++; key.LastUsedAt = now;

            ZohoRawResponse res;
            try { res = await _zoho.RawAsync(access, method, clean, CleanQuery(query), body, ct); }
            catch (ZohoException ex) when (ex.Unauthorized)
            {
                await _integration.NeedsReconnectAsync(c!, ex.Message, ct);
                throw GatewayException.Conflict("ZOHO_NOT_READY", "Zoho no longer accepts the company's connection; the company must reconnect Zoho Books.");
            }
            catch (ZohoException ex) when (ex.RateLimited) { await _db.SaveChangesAsync(ct); throw new GatewayException(429, "ZOHO_RATE_LIMITED", ex.Message); }
            catch (HttpRequestException ex) { await _db.SaveChangesAsync(ct); throw new GatewayException(502, "ZOHO_UNREACHABLE", "Zoho could not be reached: " + ex.Message); }
            if (write)
                _db.AuditLogs.Add(new AuditLog { ClientId = companyId, Action = "bridge.write", Entity = "Zoho", EntityId = Cut(clean, 100),
                    Detail = Cut($"{method} {clean} by {(caller.ClientId != null ? "own system" : "partner")} key \"{caller.KeyName}\": Zoho answered {res.Status}", 480) });
            await _db.SaveChangesAsync(ct);
            return res;
        }

        public async Task<BridgeFeedDto> FeedAsync(BridgeCaller caller, long after, int take, CancellationToken ct)
        {
            take = Math.Clamp(take, 1, 200);
            var q = _db.BridgeEvents.AsNoTracking().Where(e => e.Id > after && e.Status != "Cancelled");
            q = caller.ClientId is { } own ? q.Where(e => e.ClientId == own && e.PartnerId == null) : q.Where(e => e.PartnerId == caller.PartnerId);
            var rows = await q.OrderBy(e => e.Id).Take(take + 1).ToListAsync(ct);
            var more = rows.Count > take;
            var items = rows.Take(take).Select(Item).ToList();
            return new BridgeFeedDto(items, items.Count == 0 ? after : items[^1].Id, more);
        }

        private static BridgeFeedItem Item(BridgeEvent e)
        {
            JsonElement? data = null;
            try { data = JsonDocument.Parse(e.PayloadJson).RootElement.Clone(); } catch (JsonException) { }
            return new BridgeFeedItem(e.Id, e.EventType, e.ClientId, e.Module, e.RecordId, e.CreatedAt, data);
        }

        // =============================================================================================================
        // watching Zoho and delivering
        // =============================================================================================================

        /// <summary>Zoho writes times like 2026-10-11T09:30:00+0200.</summary>
        public static DateTime? ParseZohoTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var fixedZone = ZohoTime.Replace(s.Trim(), "$1:$2");
            return DateTimeOffset.TryParse(fixedZone, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
        }

        public async Task<int> RunAsync(DateTime now, CancellationToken ct)
        {
            var n = 0;
            try { n += await WatchAsync(now, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Bridge watch round failed"); }
            _db.ChangeTracker.Clear();
            n += await DeliverAsync(now, ct);
            // the feed keeps 30 days
            var old = await _db.BridgeEvents.Where(e => e.CreatedAt < now.AddDays(-30) && e.Status != "Pending").OrderBy(e => e.Id).Take(500).ToListAsync(ct);
            if (old.Count > 0) { _db.BridgeEvents.RemoveRange(old); await _db.SaveChangesAsync(ct); }
            return n;
        }

        private async Task<int> WatchAsync(DateTime now, CancellationToken ct)
        {
            var every = TimeSpan.FromMinutes(Math.Clamp(_cfg.GetValue("Integrations:BridgeWatchMinutes", 15), 1, 1440));
            var all = await _db.BridgeEndpoints.AsNoTracking().Where(e => e.WatchModules != "").ToListAsync(ct);
            if (all.Count == 0) return 0;
            var partnerEndpoints = all.Where(e => e.PartnerId != null).ToDictionary(e => e.PartnerId!.Value);
            var ownEndpoints = all.Where(e => e.ClientId != null).ToDictionary(e => e.ClientId!.Value);
            var partnerIds = partnerEndpoints.Keys.ToList();
            var grants = await _db.BridgeGrants.AsNoTracking().Where(g => g.IsActive && partnerIds.Contains(g.PartnerId)).ToListAsync(ct);
            var activeIds = await _db.IntegrationAccess.AsNoTracking().Where(a => a.Status == "Active").Select(a => a.ClientId).ToListAsync(ct);
            var clientIds = grants.Select(g => g.ClientId).Concat(ownEndpoints.Keys).Distinct().Where(activeIds.Contains).ToList();
            var created = 0;
            foreach (var clientId in clientIds)
            {
                // who watches what in this company: each partner's watch list within what the company shared, and the company's own system
                var watchers = grants.Where(g => g.ClientId == clientId)
                    .Select(g => ((Guid?)g.PartnerId, Modules: Split(g.Modules).Intersect(Split(partnerEndpoints[g.PartnerId].WatchModules)).ToList(), Url: partnerEndpoints[g.PartnerId].WebhookUrl))
                    .ToList();
                if (ownEndpoints.TryGetValue(clientId, out var own)) watchers.Add((null, Split(own.WatchModules), own.WebhookUrl));
                watchers = watchers.Where(w => w.Modules.Count > 0).ToList();
                if (watchers.Count == 0) continue;
                var c = await ConnectionAsync(clientId, ct);
                if (!Readiness(c).Ready) continue;
                if (!c!.BridgeWatchRequested && c.BridgeWatchAt != null && c.BridgeWatchAt > now - every) continue;
                await using var lk = await _locks.TryAcquireAsync(c.Id, ct);
                if (lk == null) continue;
                created += await WatchCompanyAsync(c, watchers, now, ct);
                _db.ChangeTracker.Clear();
            }
            return created;
        }

        private async Task<int> WatchCompanyAsync(IntegrationConnection c, List<(Guid? PartnerId, List<string> Modules, string? Url)> watchers, DateTime now, CancellationToken ct)
        {
            var cursors = string.IsNullOrEmpty(c.BridgeCursorsJson) ? new Dictionary<string, DateTime>() : JsonSerializer.Deserialize<Dictionary<string, DateTime>>(c.BridgeCursorsJson, Json) ?? new();
            var created = 0;
            var problems = new List<string>();
            ZohoAccess access;
            try { access = await _integration.AccessAsync(c, ct); }
            catch (GatewayException) { return 0; }   // needs reconnecting: the company was told
            foreach (var key in watchers.SelectMany(w => w.Modules).Distinct())
            {
                var m = Catalog.First(x => x.Key == key);
                if (!cursors.TryGetValue(key, out var cursor)) { cursors[key] = now; continue; }   // first look: start from now, history is not replayed
                var newest = cursor;
                var changed = new List<(JsonElement Item, string Id, DateTime Modified, bool Created)>();
                try
                {
                    for (var page = 1; page <= 5; page++)
                    {
                        IntegrationService.Count(c);
                        var res = await _zoho.RawAsync(access, "GET", m.Path, $"sort_column=last_modified_time&sort_order=D&per_page=200&page={page}", null, ct);
                        if (res.Status is < 200 or > 299) { problems.Add($"{m.Label}: Zoho answered {res.Status}"); break; }
                        using var doc = JsonDocument.Parse(res.Body);
                        if (!doc.RootElement.TryGetProperty(m.Root, out var arr) || arr.ValueKind != JsonValueKind.Array) break;
                        var reachedOld = false;
                        foreach (var it in arr.EnumerateArray())
                        {
                            var modified = it.TryGetProperty("last_modified_time", out var lm) ? ParseZohoTime(lm.GetString()) : null;
                            if (modified == null) continue;
                            if (modified <= cursor) { reachedOld = true; break; }
                            var id = it.TryGetProperty(m.IdField, out var idv) ? (idv.ValueKind == JsonValueKind.String ? idv.GetString() : idv.GetRawText()) : null;
                            if (string.IsNullOrEmpty(id)) continue;
                            var createdAt = it.TryGetProperty("created_time", out var cr) ? ParseZohoTime(cr.GetString()) : null;
                            changed.Add((it.Clone(), id, modified.Value, createdAt != null && createdAt > cursor));
                            if (modified > newest) newest = modified.Value;
                        }
                        var more = doc.RootElement.TryGetProperty("page_context", out var pc) && pc.TryGetProperty("has_more_page", out var hm) && hm.ValueKind == JsonValueKind.True;
                        if (reachedOld || !more) break;
                    }
                }
                catch (ZohoException ex) { problems.Add($"{m.Label}: {ex.Message}"); if (ex.RateLimited || ex.Unauthorized) break; continue; }
                catch (JsonException) { problems.Add($"{m.Label}: unreadable answer"); continue; }

                foreach (var (item, id, modified, isNew) in changed.OrderBy(x => x.Modified))
                    foreach (var w in watchers.Where(w => w.Modules.Contains(key)))
                    {
                        _db.BridgeEvents.Add(new BridgeEvent
                        {
                            PartnerId = w.PartnerId, ClientId = c.ClientId, Module = key, RecordId = Cut(id, 60), EventType = $"zoho.{key}.{(isNew ? "created" : "updated")}",
                            PayloadJson = item.GetRawText(), Status = w.Url == null ? "Stored" : "Pending", NextAttemptAt = now,
                        });
                        created++;
                    }
                cursors[key] = newest;
            }
            c.BridgeCursorsJson = JsonSerializer.Serialize(cursors, Json);
            c.BridgeWatchAt = now; c.BridgeWatchRequested = false;
            if (problems.Count > 0) { c.LastError = Cut("Bridge: " + string.Join("; ", problems), 1000); c.LastErrorAt = now; }
            await _db.SaveChangesAsync(ct);
            return created;
        }

        private async Task<int> DeliverAsync(DateTime now, CancellationToken ct)
        {
            var due = await _db.BridgeEvents.Where(e => e.Status == "Pending" && e.NextAttemptAt <= now).OrderBy(e => e.Id).Take(100).ToListAsync(ct);
            if (due.Count == 0) return 0;
            // an event belongs to a partner, or (no partner) to the company's own system
            static string Owner(Guid? partnerId, Guid? clientId) => partnerId != null ? "p:" + partnerId : "c:" + clientId;
            var partnerIds = due.Where(e => e.PartnerId != null).Select(e => e.PartnerId).Distinct().ToList();
            var ownIds = due.Where(e => e.PartnerId == null).Select(e => (Guid?)e.ClientId).Distinct().ToList();
            var endpoints = (await _db.BridgeEndpoints.AsNoTracking().Where(e => (e.PartnerId != null && partnerIds.Contains(e.PartnerId)) || (e.ClientId != null && ownIds.Contains(e.ClientId))).ToListAsync(ct))
                .ToDictionary(e => Owner(e.PartnerId, e.ClientId));
            var delivered = 0;
            var failing = new HashSet<string>();
            foreach (var e in due)
            {
                var owner = Owner(e.PartnerId, e.PartnerId == null ? e.ClientId : null);
                var ep = endpoints.GetValueOrDefault(owner);
                if (ep?.WebhookUrl == null || ep.SecretProtected == null) { e.Status = "Stored"; continue; }   // kept for the feed
                if (failing.Contains(owner)) { e.NextAttemptAt = now.AddMinutes(1); continue; }   // one failure per receiver per round: keep the order
                string secret;
                try { secret = _secrets.Unprotect(ep.SecretProtected); } catch (CryptographicException) { e.Status = "Failed"; e.LastError = "The webhook secret cannot be read; set the webhook again."; continue; }
                var body = JsonSerializer.Serialize(Item(e), Json);
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                e.Attempts++;
                var error = await _sender.SendAsync(ep.WebhookUrl, e.EventType, e.Id, ts, WebhookDeliveryJob.Sign(secret, ts, body), body, ct);
                if (error == null) { e.Status = "Delivered"; e.DeliveredAt = DateTime.UtcNow; e.LastError = null; delivered++; continue; }
                failing.Add(owner);
                e.LastError = Cut(error, 500);
                if (e.Attempts > Backoff.Length) e.Status = "Failed";
                else e.NextAttemptAt = DateTime.UtcNow + Backoff[e.Attempts - 1];
            }
            await _db.SaveChangesAsync(ct);
            return delivered;
        }

        // =============================================================================================================
        // the platform
        // =============================================================================================================

        public async Task<BridgeMonitorDto> MonitorAsync(CancellationToken ct)
        {
            var today = DateTime.UtcNow.Date;
            var keys = await _db.BridgeKeys.AsNoTracking().ToListAsync(ct);
            var grants = await _db.BridgeGrants.AsNoTracking().Where(g => g.IsActive).ToListAsync(ct);
            var endpoints = await _db.BridgeEndpoints.AsNoTracking().ToListAsync(ct);
            var events = await _db.BridgeEvents.AsNoTracking().Where(e => e.Status == "Pending" || e.Status == "Failed").GroupBy(e => new { e.PartnerId, e.ClientId, e.Status })
                .Select(g => new { g.Key.PartnerId, g.Key.ClientId, g.Key.Status, N = g.Count() }).ToListAsync(ct);

            var partnerIds = keys.Where(k => k.PartnerId != null).Select(k => k.PartnerId!.Value).Concat(grants.Select(g => g.PartnerId)).Distinct().ToList();
            var partners = await _db.Partners.AsNoTracking().Where(p => partnerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var rows = partnerIds.Select(id =>
            {
                var ks = keys.Where(k => k.PartnerId == id).ToList();
                var url = endpoints.FirstOrDefault(e => e.PartnerId == id)?.WebhookUrl;
                return new BridgeMonitorRow(id, partners.GetValueOrDefault(id) ?? "?", ks.Count(k => k.RevokedAt == null), grants.Count(g => g.PartnerId == id),
                    ks.Where(k => k.CallsDay?.Date == today).Sum(k => k.CallsToday), ks.Sum(k => k.CallsTotal),
                    events.Where(e => e.PartnerId == id && e.Status == "Pending").Sum(e => e.N), events.Where(e => e.PartnerId == id && e.Status == "Failed").Sum(e => e.N),
                    url == null ? null : new Uri(url).Host, ks.Max(k => k.LastUsedAt));
            }).ToList();

            // companies whose own system uses the bridge
            var ownIds = keys.Where(k => k.ClientId != null).Select(k => k.ClientId!.Value).Distinct().ToList();
            var companies = await _db.Clients.AsNoTracking().Where(c => ownIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            rows.AddRange(ownIds.Select(id =>
            {
                var ks = keys.Where(k => k.ClientId == id).ToList();
                var url = endpoints.FirstOrDefault(e => e.ClientId == id)?.WebhookUrl;
                return new BridgeMonitorRow(id, (companies.GetValueOrDefault(id) ?? "?") + " (own system)", ks.Count(k => k.RevokedAt == null), 1,
                    ks.Where(k => k.CallsDay?.Date == today).Sum(k => k.CallsToday), ks.Sum(k => k.CallsTotal),
                    events.Where(e => e.PartnerId == null && e.ClientId == id && e.Status == "Pending").Sum(e => e.N), events.Where(e => e.PartnerId == null && e.ClientId == id && e.Status == "Failed").Sum(e => e.N),
                    url == null ? null : new Uri(url).Host, ks.Max(k => k.LastUsedAt), OwnSystem: true);
            }));
            rows = rows.OrderByDescending(r => r.CallsToday).ThenBy(r => r.Partner).ToList();
            var shared = grants.Select(g => g.ClientId).Concat(keys.Where(k => k.ClientId != null && k.RevokedAt == null).Select(k => k.ClientId!.Value)).Distinct().Count();
            return new BridgeMonitorDto(rows, shared, rows.Sum(r => r.CallsToday));
        }
    }
}
