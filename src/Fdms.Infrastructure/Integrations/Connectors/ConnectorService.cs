using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Billing;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Integrations.Connectors
{
    /// <summary>
    /// QuickBooks Online, Xero, Sage Accounting and Odoo ⇄ ZIMRA, on one engine. Each product is sold on its own (apply, approve, set-up fee, monthly fee and documents,
    /// exactly like Zoho Books) and has a small adapter that speaks its API. A company connects (signs in, or for Odoo gives its address and an API key), picks the
    /// company/organisation in the product, the fiscal device and its defaults; invoices and credit notes are pulled, fiscalised (automatically or on a click) and the
    /// fiscal number, verification code, link and PDF are written back. A document becomes one fiscal receipt only: its external reference is the product's id.
    /// </summary>
    public sealed class ConnectorService : IConnectorService
    {
        private const int MaxPagesPerSync = 20;
        private const int MaxFiscalisePerSync = 50;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly string[] MoneyTypes = Enum.GetNames<MoneyType>();

        private readonly FdmsDbContext _db;
        private readonly IReadOnlyDictionary<string, IAccountingConnector> _connectors;
        private readonly IntegrationSettings _settings;
        private readonly IReceiptService _receipts;
        private readonly INotificationService _notify;
        private readonly IBillingService _billing;
        private readonly PublicUrls _urls;
        private readonly DeviceLocks _locks;
        private readonly ICallerScope _scope;
        private readonly IConfiguration _cfg;
        private readonly ILogger<ConnectorService> _log;
        private readonly IDataProtector _tokens;
        private readonly ITimeLimitedDataProtector _state;

        public ConnectorService(FdmsDbContext db, IEnumerable<IAccountingConnector> connectors, IntegrationSettings settings, IReceiptService receipts, INotificationService notify,
            IBillingService billing, PublicUrls urls, DeviceLocks locks, ICallerScope scope, IConfiguration cfg, IDataProtectionProvider dp, ILogger<ConnectorService> log)
        {
            _db = db; _settings = settings; _receipts = receipts; _notify = notify; _billing = billing; _urls = urls; _locks = locks; _scope = scope; _cfg = cfg; _log = log;
            _connectors = connectors.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
            _tokens = dp.CreateProtector("Fdms.Connectors.v1");
            _state = dp.CreateProtector("Fdms.Connectors.State.v1").ToTimeLimitedDataProtector();
        }

        // =============================================================================================================
        // helpers
        // =============================================================================================================

        private sealed record Extra(string? Url, string? Database, string? Username, string? UserId);

        private static string Cut(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot manage the accounting connection.");
        }

        private static IntegrationProductDto Product(string product)
        {
            var p = IntegrationProducts.Get(product);
            if (p.Key == IntegrationProducts.Zoho) throw GatewayException.BadRequest("PRODUCT_INVALID", "Zoho Books has its own page.");
            return p;
        }

        private IAccountingConnector Adapter(IntegrationProductDto p) =>
            _connectors.TryGetValue(p.Key, out var a) ? a : throw GatewayException.Conflict("CONNECTOR_UNAVAILABLE", $"{p.Label} is not available on this gateway.");

        private Task<IntegrationConnection?> ConnectionAsync(Guid clientId, IntegrationProductDto p, CancellationToken ct) =>
            _db.IntegrationConnections.FirstOrDefaultAsync(c => c.ClientId == clientId && c.Provider == p.ConnectionProvider, ct);

        private async Task<IntegrationConnection> LiveConnectionAsync(Guid clientId, IntegrationProductDto p, CancellationToken ct)
        {
            var c = await ConnectionAsync(clientId, p, ct);
            if (c == null || c.Status == "Disconnected") throw GatewayException.Conflict("CONNECTOR_NOT_CONNECTED", $"Connect {p.Label} first.");
            if (c.Status == "ChooseOrganization") throw GatewayException.Conflict("CONNECTOR_CHOOSE_ORGANIZATION", $"Choose which {p.Label} company to use.");
            if (c.Status == "NeedsReconnect") throw GatewayException.Conflict("CONNECTOR_RECONNECT", $"{p.Label} no longer accepts this connection. Connect again.");
            return c;
        }

        private static Extra ExtraOf(IntegrationConnection c)
        {
            try { return (string.IsNullOrWhiteSpace(c.ExtraJson) ? null : JsonSerializer.Deserialize<Extra>(c.ExtraJson, Json)) ?? new Extra(null, null, null, null); }
            catch (JsonException) { return new Extra(null, null, null, null); }
        }

        private string? Unprotect(string? v) { if (string.IsNullOrEmpty(v)) return null; try { return _tokens.Unprotect(v); } catch (CryptographicException) { return null; } }

        private async Task<ConnectorApp?> AppAsync(IntegrationProductDto p, CancellationToken ct)
        {
            if (p.Auth != "oauth") return null;
            var (id, secret, sandbox) = await _settings.ConnectorAppAsync(p.Key, ct);
            return string.IsNullOrWhiteSpace(id) || string.IsNullOrEmpty(secret) ? null : new ConnectorApp(id, secret, sandbox);
        }

        private async Task<string> RedirectUriAsync(IntegrationProductDto p, CancellationToken ct) =>
            (await _urls.ApiAsync(ct)).TrimEnd('/') + $"/api/v1/integrations/connectors/{p.Key}/callback";

        /// <summary>What a call to the product needs, with a fresh access token (renewed a little before it runs out).</summary>
        private async Task<ConnectorSession> SessionAsync(IntegrationConnection c, IntegrationProductDto p, CancellationToken ct)
        {
            var x = ExtraOf(c);
            var token = Unprotect(c.AccessTokenProtected);
            if (p.Auth == "oauth" && (token == null || c.AccessTokenExpiresAt == null || c.AccessTokenExpiresAt < DateTime.UtcNow.AddMinutes(2)))
            {
                var app = await AppAsync(p, ct) ?? throw GatewayException.Conflict("CONNECTOR_NOT_CONFIGURED", $"{p.Label} is not set up on this gateway yet.");
                var refresh = Unprotect(c.RefreshTokenProtected);
                if (refresh == null) { await NeedsReconnectAsync(c, p, "the sign-in was lost", ct); }
                try
                {
                    var t = await Adapter(p).RefreshAsync(app, refresh!, ct);
                    token = t.AccessToken;
                    c.AccessTokenProtected = _tokens.Protect(t.AccessToken);
                    if (!string.IsNullOrEmpty(t.RefreshToken)) c.RefreshTokenProtected = _tokens.Protect(t.RefreshToken);   // Xero and QuickBooks rotate it
                    c.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, t.ExpiresInSeconds));
                    c.LastTokenRefreshAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                }
                catch (ConnectorException ex) when (ex.Unauthorized) { await NeedsReconnectAsync(c, p, ex.Message, ct); }
            }
            if (token == null) await NeedsReconnectAsync(c, p, "the sign-in was lost", ct);
            return new ConnectorSession(p.Key, token!, c.OrganizationId, x.Url, x.Database, x.Username, x.UserId, (await _settings.ConnectorAppAsync(p.Key, ct)).Sandbox);
        }

        private async Task NeedsReconnectAsync(IntegrationConnection c, IntegrationProductDto p, string why, CancellationToken ct)
        {
            c.Status = "NeedsReconnect"; c.LastError = Cut($"{p.Label} refused the connection: {why}", 1000); c.LastErrorAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _notify.RaiseAsync(c.ClientId, "integration", "bad", $"connector:reconnect:{c.Id}:{DateTime.UtcNow:yyyyMMdd}", $"Connect {p.Label} again",
                $"{p.Label} no longer accepts FiscalHub's connection, so nothing is fiscalised from it. Open {p.Label} under Integration and connect again.", p.Page, ct);
            throw GatewayException.Conflict("CONNECTOR_RECONNECT", $"{p.Label} no longer accepts this connection. Connect again.");
        }

        private void Count(IntegrationConnection c)
        {
            var today = DateTime.UtcNow.Date;
            if (c.ApiCallsDay?.Date != today) { c.ApiCallsDay = today; c.ApiCallsToday = 0; }
            c.ApiCallsToday++; c.ApiCallsTotal++;
        }

        private async Task FailAsync(IntegrationConnection c, string message, CancellationToken ct)
        {
            c.LastError = Cut(message, 1000); c.LastErrorAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        private IQueryable<IntegrationDocument> DocsOf(Guid clientId, IntegrationProductDto p)
        {
            var ids = _db.IntegrationConnections.Where(x => x.ClientId == clientId && x.Provider == p.ConnectionProvider).Select(x => x.Id);
            return _db.IntegrationDocuments.Where(d => d.ClientId == clientId && ids.Contains(d.ConnectionId));
        }

        // =============================================================================================================
        // what the company sees
        // =============================================================================================================

        private async Task<IntegrationCountsDto> CountsAsync(Guid clientId, IntegrationProductDto p, CancellationToken ct)
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var q = DocsOf(clientId, p).AsNoTracking();
            var by = await q.GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            int N(string s) => by.FirstOrDefault(x => x.Key == s)?.N ?? 0;
            return new IntegrationCountsDto(N("Pending"), N("Fiscalised"), N("Failed"), N("Skipped"),
                await q.CountAsync(d => d.Status == "Fiscalised" && d.FiscalisedAt >= monthStart, ct),
                await q.CountAsync(d => d.Status == "Fiscalised" && d.PostedBackAt == null, ct));
        }

        private async Task<ConnectorStatusDto> DtoAsync(Guid clientId, IntegrationProductDto p, CancellationToken ct)
        {
            var c = await _db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == p.ConnectionProvider, ct);
            var access = await _db.IntegrationAccess.AsNoTracking().Where(a => a.ClientId == clientId && a.Provider == p.Key).Select(a => a.Status).FirstOrDefaultAsync(ct) ?? "None";
            var available = p.Auth != "oauth" || await AppAsync(p, ct) != null;
            var pricing = await _settings.PricingAsync(p.Key, ct);
            var status = c == null || c.Status == "Disconnected" ? "NotConnected" : c.Status;
            var orgs = c?.OrganizationsJson is { Length: > 0 } oj ? (JsonSerializer.Deserialize<List<ConnectorOrganization>>(oj, Json) ?? new()) : new List<ConnectorOrganization>();
            var x = c == null ? new Extra(null, null, null, null) : ExtraOf(c);
            var settings = new ConnectorSettingsDto(c?.AutoFiscalise ?? false, c?.DeviceId, c?.DefaultHsCode, c?.DefaultMoneyType ?? "Credit", c?.PostBack ?? true, c?.StartDate);
            return new ConnectorStatusDto(p.Key, p.Label, p.Auth, access, available, available ? null : $"{p.Label} is not set up on this gateway yet. Ask the platform operator.",
                c?.Id, status, c?.OrganizationId, c?.OrganizationName, orgs, c?.ConnectedBy, c?.ConnectedAt, c?.LastSyncAt, c?.LastError, c?.LastErrorAt,
                x.Url, x.Database, x.Username, settings, await CountsAsync(clientId, p, ct), pricing);
        }

        public async Task<IReadOnlyList<ConnectorStatusDto>> ListAsync(Guid clientId, CancellationToken ct)
        {
            var list = new List<ConnectorStatusDto>();
            foreach (var p in IntegrationProducts.Connectors.Where(p => _connectors.ContainsKey(p.Key))) list.Add(await DtoAsync(clientId, p, ct));
            return list;
        }

        public Task<ConnectorStatusDto> GetAsync(Guid clientId, string product, CancellationToken ct) => DtoAsync(clientId, Product(product), ct);

        // =============================================================================================================
        // connecting
        // =============================================================================================================

        public async Task<StartConnectorResult> StartAsync(Guid clientId, string product, CancellationToken ct)
        {
            RequireWholeCompany();
            var p = Product(product);
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct, p.Key);
            if (p.Auth != "oauth") throw GatewayException.BadRequest("CONNECTOR_KEY", $"{p.Label} connects with an address and an API key, not a sign-in.");
            var app = await AppAsync(p, ct) ?? throw GatewayException.Conflict("CONNECTOR_NOT_CONFIGURED", $"{p.Label} is not set up on this gateway yet. Ask the platform operator.");
            var state = _state.Protect($"{clientId:N}|{p.Key}|{Cut(_scope.UserName, 150)}", TimeSpan.FromMinutes(15));
            return new StartConnectorResult(Adapter(p).AuthorizeUrl(app, await RedirectUriAsync(p, ct), state));
        }

        public async Task<ConnectorCallbackResult> CallbackAsync(string product, string? code, string? state, string? error, IReadOnlyDictionary<string, string?> query, CancellationToken ct)
        {
            var p = IntegrationProducts.Find(product);
            var back = (await _urls.AppAsync(ct)).TrimEnd('/') + "/connectors?product=" + (p?.Key ?? "");
            ConnectorCallbackResult Fail(string why) => new(false, back + "&connector=failed&reason=" + Uri.EscapeDataString(why));
            if (p == null || p.Key == IntegrationProducts.Zoho) return Fail("product");
            string payload;
            try { payload = _state.Unprotect(state ?? ""); } catch (CryptographicException) { return Fail("expired"); }
            var parts = payload.Split('|', 3);
            if (parts.Length != 3 || !Guid.TryParse(parts[0], out var clientId) || parts[1] != p.Key) return Fail("state");
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return new(false, back + "&connector=denied");
            if (!await _db.Clients.AnyAsync(x => x.Id == clientId && x.IsActive, ct)) return Fail("company");
            if (!await IntegrationAccessService.IsApprovedAsync(_db, clientId, ct, p.Key)) return Fail("not-approved");
            var app = await AppAsync(p, ct);
            if (app == null) return Fail("not-configured");
            try
            {
                var t = await Adapter(p).ExchangeCodeAsync(app, await RedirectUriAsync(p, ct), code, query, ct);
                var c = await ConnectionAsync(clientId, p, ct);
                if (c == null) { c = new IntegrationConnection { ClientId = clientId, Provider = p.ConnectionProvider }; _db.IntegrationConnections.Add(c); }
                var org = c.OrganizationId;
                if (t.OrganizationId != null && t.OrganizationId != org) { c.OrganizationId = t.OrganizationId; c.SyncCursor = null; }   // QuickBooks: the company chosen at sign-in
                c.Status = "ChooseOrganization"; c.AccessTokenProtected = _tokens.Protect(t.AccessToken);
                c.RefreshTokenProtected = t.RefreshToken == null ? null : _tokens.Protect(t.RefreshToken);
                c.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, t.ExpiresInSeconds)); c.LastTokenRefreshAt = DateTime.UtcNow;
                c.ConnectedBy = Cut(parts[2], 200); c.ConnectedAt = DateTime.UtcNow; c.LastError = null; c.LastErrorAt = null;
                c.WebhookKey = string.IsNullOrEmpty(c.WebhookKey) ? Guid.NewGuid().ToString("N") : c.WebhookKey;
                await _db.SaveChangesAsync(ct);
                await LoadOrganizationsAsync(c, p, t.OrganizationId ?? org, ct);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.connect", Entity = "IntegrationConnection", EntityId = c.Id.ToString(), Detail = p.Label });
                await _db.SaveChangesAsync(ct);
                return new(true, back + (c.Status == "Connected" ? "&connector=connected" : "&connector=choose"));
            }
            catch (ConnectorException ex) { _log.LogWarning("{Product} sign-in failed: {Message}", p.Label, ex.Message); return Fail(ex.Message); }
            catch (HttpRequestException ex) { return Fail($"{p.Label} could not be reached: {ex.Message}"); }
        }

        /// <summary>Lists the companies the sign-in reaches; with exactly one (or the one named at sign-in) it is chosen at once.</summary>
        private async Task LoadOrganizationsAsync(IntegrationConnection c, IntegrationProductDto p, string? preferred, CancellationToken ct)
        {
            var s = await SessionAsync(c, p, ct);
            Count(c);
            var orgs = await Adapter(p).OrganizationsAsync(s, ct);
            c.OrganizationsJson = JsonSerializer.Serialize(orgs, Json);
            var pick = orgs.FirstOrDefault(o => o.Id == preferred) ?? (orgs.Count == 1 ? orgs[0] : null);
            if (pick != null) { c.OrganizationId = pick.Id; c.OrganizationName = Cut(pick.Name, 200); c.Status = "Connected"; c.SyncCursor = null; c.SyncRequestedAt = DateTime.UtcNow; }
            await _db.SaveChangesAsync(ct);
        }

        public async Task<ConnectorStatusDto> ConnectOdooAsync(Guid clientId, ConnectOdooRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var p = Product(IntegrationProducts.Odoo);
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct, p.Key);
            var url = (req.Url ?? "").Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || url.Length > 200)
                throw GatewayException.BadRequest("URL_INVALID", "Enter your Odoo address starting with https://, e.g. https://mycompany.odoo.com");
            var database = (req.Database ?? "").Trim(); var user = (req.Username ?? "").Trim(); var key = (req.ApiKey ?? "").Trim();
            if (database.Length is < 1 or > 100 || user.Length is < 1 or > 200) throw GatewayException.BadRequest("ODOO_INVALID", "Enter your Odoo database and the email you sign in with.");
            var c = await ConnectionAsync(clientId, p, ct);
            if (key.Length == 0 && c?.AccessTokenProtected != null) key = Unprotect(c.AccessTokenProtected) ?? "";   // keep the saved key
            if (key.Length is < 8 or > 200) throw GatewayException.BadRequest("ODOO_KEY", "Paste an Odoo API key (Odoo: your profile → Account Security → New API Key).");
            try
            {
                var t = await Adapter(p).SignInAsync(new ConnectorSession(p.Key, key, null, url, database, user, null, false), ct);
                if (c == null) { c = new IntegrationConnection { ClientId = clientId, Provider = p.ConnectionProvider }; _db.IntegrationConnections.Add(c); }
                c.AccessTokenProtected = _tokens.Protect(key); c.RefreshTokenProtected = null; c.AccessTokenExpiresAt = null;
                c.ExtraJson = JsonSerializer.Serialize(new Extra(url, database, user, t.AccessToken), Json);
                c.ApiDomain = Cut(url, 120); c.Status = "ChooseOrganization"; c.ConnectedBy = Cut(_scope.UserName, 200); c.ConnectedAt = DateTime.UtcNow; c.LastError = null; c.LastErrorAt = null;
                c.WebhookKey = string.IsNullOrEmpty(c.WebhookKey) ? Guid.NewGuid().ToString("N") : c.WebhookKey;
                await _db.SaveChangesAsync(ct);
                await LoadOrganizationsAsync(c, p, c.OrganizationId, ct);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.connect", Entity = "IntegrationConnection", EntityId = c.Id.ToString(), Detail = $"Odoo {u.Host} / {database}" });
                await _db.SaveChangesAsync(ct);
            }
            catch (ConnectorException ex) { throw GatewayException.BadRequest("ODOO_REFUSED", "Odoo refused the connection: " + ex.Message); }
            catch (HttpRequestException ex) { throw GatewayException.BadRequest("ODOO_UNREACHABLE", "Odoo could not be reached: " + ex.Message); }
            return await DtoAsync(clientId, p, ct);
        }

        public async Task<ConnectorStatusDto> ChooseOrganizationAsync(Guid clientId, string product, string organizationId, CancellationToken ct)
        {
            RequireWholeCompany();
            var p = Product(product);
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct, p.Key);
            var c = await ConnectionAsync(clientId, p, ct);
            if (c == null || c.Status is "Disconnected" or "NeedsReconnect") throw GatewayException.Conflict("CONNECTOR_NOT_CONNECTED", $"Connect {p.Label} first.");
            var orgs = c.OrganizationsJson is { Length: > 0 } oj ? JsonSerializer.Deserialize<List<ConnectorOrganization>>(oj, Json) ?? new() : new();
            var org = orgs.FirstOrDefault(o => o.Id == organizationId) ?? throw GatewayException.BadRequest("ORGANIZATION_INVALID", "Choose one of the companies listed.");
            if (c.OrganizationId != org.Id) { c.SyncCursor = null; }
            c.OrganizationId = org.Id; c.OrganizationName = Cut(org.Name, 200); c.Status = "Connected"; c.SyncRequestedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.organization", Entity = "IntegrationConnection", EntityId = c.Id.ToString(), Detail = $"{p.Label}: {org.Name}" });
            await _db.SaveChangesAsync(ct);
            return await DtoAsync(clientId, p, ct);
        }

        public async Task<ConnectorStatusDto> SaveSettingsAsync(Guid clientId, string product, ZohoSettingsRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var p = Product(product);
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct, p.Key);
            var c = await ConnectionAsync(clientId, p, ct);
            if (c == null || c.Status == "Disconnected") throw GatewayException.Conflict("CONNECTOR_NOT_CONNECTED", $"Connect {p.Label} first.");
            if (req.DeviceId is { } dev && !await _db.Devices.AnyAsync(d => d.Id == dev && d.ClientId == clientId && d.IsActive, ct)) throw GatewayException.NotFound("Device");
            if (req.AutoFiscalise && req.DeviceId == null) throw GatewayException.BadRequest("DEVICE_REQUIRED", $"Choose the fiscal device the {p.Label} invoices are issued on.");
            var hs = string.IsNullOrWhiteSpace(req.DefaultHsCode) ? null : req.DefaultHsCode.Trim();
            if (hs != null && !Regex.IsMatch(hs, @"^\d{4,10}$")) throw GatewayException.BadRequest("HS_INVALID", "An HS code has 4 to 10 digits (ZIMRA uses 8).");
            var mt = MoneyTypes.FirstOrDefault(m => m.Equals(req.DefaultMoneyType?.Trim() ?? "Credit", StringComparison.OrdinalIgnoreCase))
                     ?? throw GatewayException.BadRequest("PAYMENT_INVALID", "Choose how the sales are paid (Credit = on account).");
            if (req.StartDate is { } sd && sd.Date > DateTime.UtcNow.Date.AddDays(31)) throw GatewayException.BadRequest("START_INVALID", "The start date is too far ahead.");
            var startChanged = c.StartDate?.Date != req.StartDate?.Date;
            c.AutoFiscalise = req.AutoFiscalise; c.DeviceId = req.DeviceId; c.DefaultHsCode = hs; c.DefaultMoneyType = mt; c.PostBack = req.PostBack; c.StartDate = req.StartDate?.Date;
            if (startChanged) { c.SyncCursor = null; c.SyncRequestedAt = DateTime.UtcNow; }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.settings", Entity = "IntegrationConnection", EntityId = c.Id.ToString(),
                Detail = $"{p.Label}: auto {(c.AutoFiscalise ? "on" : "off")}, device {c.DeviceId}, HS {hs}, {mt}, write back {(c.PostBack ? "on" : "off")}, from {c.StartDate:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return await DtoAsync(clientId, p, ct);
        }

        public async Task DisconnectAsync(Guid clientId, string product, CancellationToken ct)
        {
            RequireWholeCompany();
            var p = Product(product);
            var c = await ConnectionAsync(clientId, p, ct) ?? throw GatewayException.NotFound("Connection");
            c.Status = "Disconnected"; c.AccessTokenProtected = null; c.RefreshTokenProtected = null; c.AccessTokenExpiresAt = null; c.AutoFiscalise = false; c.OrganizationsJson = null;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.disconnect", Entity = "IntegrationConnection", EntityId = c.Id.ToString(), Detail = p.Label });
            await _db.SaveChangesAsync(ct);
        }

        // =============================================================================================================
        // sync
        // =============================================================================================================

        public async Task<ZohoSyncResultDto> SyncNowAsync(Guid clientId, string product, CancellationToken ct)
        {
            RequireWholeCompany();
            var p = Product(product);
            await IntegrationAccessService.EnsureActiveAsync(_db, clientId, ct, p.Key);
            var c = await LiveConnectionAsync(clientId, p, ct);
            await using var _ = await _locks.AcquireAsync(c.Id, ct);
            await _db.Entry(c).ReloadAsync(ct);
            return await SyncAsync(c, p, ct);
        }

        private async Task<ZohoSyncResultDto> SyncAsync(IntegrationConnection c, IntegrationProductDto p, CancellationToken ct)
        {
            int added = 0, updated = 0, fiscalised = 0, failed = 0, posted = 0;
            string? error = null;
            try
            {
                var s = await SessionAsync(c, p, ct);
                var adapter = Adapter(p);
                var start = c.StartDate?.Date;
                var since = c.SyncCursor?.AddMinutes(-2) ?? start;
                var newest = c.SyncCursor;
                foreach (var kind in new[] { "invoice", "creditnote" })
                {
                    for (var page = 1; page <= MaxPagesPerSync; page++)
                    {
                        Count(c);
                        var pg = await adapter.ListAsync(s, kind, since, page, ct);
                        var ids = pg.Items.Select(i => i.Id).ToList();
                        var known = await _db.IntegrationDocuments.Where(d => d.ConnectionId == c.Id && d.Kind == kind && ids.Contains(d.ExternalId)).ToDictionaryAsync(d => d.ExternalId, ct);
                        foreach (var h in pg.Items)
                        {
                            if (h.LastModified > newest) newest = h.LastModified;
                            var status = h.Status.ToLowerInvariant();
                            known.TryGetValue(h.Id, out var d);
                            if (d == null)
                            {
                                if (status is "draft" or "void") continue;
                                if (start != null && h.Date != null && h.Date.Value.Date < start) continue;
                                d = new IntegrationDocument { ClientId = c.ClientId, ConnectionId = c.Id, Kind = kind, ExternalId = Cut(h.Id, 100) };
                                _db.IntegrationDocuments.Add(d);
                                added++;
                            }
                            else if (d.Status == "Fiscalised")
                            {
                                if ((h.Total != d.Total || status == "void") && h.LastModified != d.ExternalModifiedAt) d.ChangedAfterFiscalised = true;
                                d.ExternalStatus = Cut(h.Status, 40); d.ExternalModifiedAt = h.LastModified;
                                updated++;
                                continue;
                            }
                            else
                            {
                                updated++;
                                if (status == "void") { d.Status = "Skipped"; d.Error = $"Voided in {p.Label}."; }
                                else if (d.Status == "Failed" && h.LastModified != d.ExternalModifiedAt) { d.Status = "Pending"; d.Error = null; d.Attempts = 0; }
                            }
                            d.ExternalNumber = Cut(h.Number, 100); d.CustomerName = h.CustomerName == null ? null : Cut(h.CustomerName, 200); d.DocumentDate = h.Date;
                            d.Total = h.Total; d.Currency = Cut(h.Currency.ToUpperInvariant(), 10); d.ExternalStatus = Cut(h.Status, 40); d.ExternalModifiedAt = h.LastModified;
                        }
                        await _db.SaveChangesAsync(ct);
                        if (!pg.HasMore) break;
                    }
                }
                c.SyncCursor = newest ?? DateTime.UtcNow;
                c.LastSyncAt = DateTime.UtcNow; c.SyncRequestedAt = null; c.LastError = null; c.LastErrorAt = null;
                await _db.SaveChangesAsync(ct);

                if (c.AutoFiscalise && c.DeviceId != null)
                {
                    var due = await _db.IntegrationDocuments.Where(d => d.ConnectionId == c.Id && d.Status == "Pending")
                        .OrderBy(d => d.Kind == "creditnote").ThenBy(d => d.DocumentDate).ThenBy(d => d.CreatedAt).Take(MaxFiscalisePerSync).ToListAsync(ct);
                    foreach (var d in due)
                    {
                        if (await FiscaliseOneAsync(c, p, d, ct)) fiscalised++; else failed++;
                        if (d.PostedBackAt != null) posted++;
                    }
                }
            }
            catch (GatewayException ex) when (ex.Code is "CONNECTOR_RECONNECT" or "CONNECTOR_NOT_CONFIGURED") { error = ex.Message; }
            catch (ConnectorException ex) { error = ex.Message; await FailAsync(c, ex.Message, ct); }
            catch (HttpRequestException ex) { error = $"{p.Label} could not be reached: {ex.Message}"; await FailAsync(c, error, ct); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { error = $"{p.Label} did not answer in time."; await FailAsync(c, error, ct); }
            return new ZohoSyncResultDto(added, updated, fiscalised, failed, posted, error);
        }

        // =============================================================================================================
        // fiscalising
        // =============================================================================================================

        /// <summary>The document's lines as the gateway fiscalises them (all VAT included). A problem comes back in words when it cannot be done exactly.</summary>
        public static (List<ReceiptLineRequest> Lines, string? Problem) MapLines(ConnectorDocument doc, string? defaultHs)
        {
            var lines = new List<ReceiptLineRequest>();
            foreach (var l in doc.Lines)
            {
                if (l.GrossTotal == 0) continue;   // a free line carries nothing for ZIMRA
                if (l.Quantity <= 0 || l.GrossTotal < 0 && !l.Discount) return (lines, $"The line “{l.Name}” has a quantity or amount of zero or below. Fix it in the accounting system.");
                var hs = l.HsCode is { Length: > 0 } h && Regex.IsMatch(h, @"^\d{4,10}$") ? h : defaultHs;
                var gross = Math.Abs(l.GrossTotal);
                string name = Cut(string.IsNullOrWhiteSpace(l.Name) ? "Item" : l.Name, 200);
                decimal qty = l.Quantity, price;
                if (decimal.Round(decimal.Round(gross / qty, 2) * qty, 2) == gross) price = decimal.Round(gross / qty, 2);
                else { name = Cut($"{name} ({qty.ToString("0.####", CultureInfo.InvariantCulture)} units)", 200); qty = 1; price = gross; }
                lines.Add(l.Exempt ? new ReceiptLineRequest(name, hs, qty, price, IsExempt: true, IsDiscount: l.Discount)
                                   : new ReceiptLineRequest(name, hs, qty, price, TaxPercent: l.TaxPercent ?? 0, IsDiscount: l.Discount));
            }
            if (lines.Count == 0 || lines.All(x => x.IsDiscount)) return (lines, "The document has no lines with an amount.");
            return (lines, null);
        }

        private static string Ref(IntegrationProductDto p, IntegrationConnection c, IntegrationDocument d) => Cut($"{p.Key}:{c.OrganizationId}:{d.Kind}:{d.ExternalId}", 100);

        private async Task<bool> FiscaliseOneAsync(IntegrationConnection c, IntegrationProductDto p, IntegrationDocument d, CancellationToken ct)
        {
            ConnectorDocument? doc = null;
            try
            {
                if (c.DeviceId is not { } deviceId) throw GatewayException.BadRequest("DEVICE_REQUIRED", "Choose the fiscal device under the connection settings first.");
                if ((await _billing.GetAccessAsync(c.ClientId, ct)).Locked) throw new GatewayException(402, "PAYMENT_REQUIRED", "Your subscription needs paying before more invoices are fiscalised.");
                var s = await SessionAsync(c, p, ct);
                Count(c);
                doc = await Adapter(p).GetAsync(s, d.Kind, d.ExternalId, ct);
                var status = doc.Status.ToLowerInvariant();
                if (status == "void") { d.Status = "Skipped"; d.Error = $"Voided in {p.Label}."; await _db.SaveChangesAsync(ct); return false; }
                if (status == "draft") throw GatewayException.Conflict("CONNECTOR_DRAFT", $"The document is still a draft in {p.Label}. Approve or send it there first.");
                if (string.IsNullOrWhiteSpace(doc.Currency) || doc.Currency.Length != 3) throw GatewayException.Unprocessable("CURRENCY_INVALID", "The document has no currency.");
                var (lines, problem) = MapLines(doc, c.DefaultHsCode);
                if (problem != null) throw GatewayException.Unprocessable("CONNECTOR_UNSUPPORTED", problem);

                BuyerRequest? buyer = null;
                var tin = doc.Buyer?.Tin?.Replace(" ", "").Replace("-", "");
                if (doc.Buyer != null && tin is { Length: 10 } && tin.All(char.IsDigit))   // ZIMRA names a buyer only with a valid TIN
                    buyer = new BuyerRequest(Cut(doc.Buyer.Name, 200), Cut(doc.Buyer.Name, 200), tin, string.IsNullOrWhiteSpace(doc.Buyer.VatNumber) ? null : doc.Buyer.VatNumber.Trim(), doc.Buyer.Email, doc.Buyer.Phone);

                Guid? original = null;
                var type = ReceiptType.FiscalInvoice;
                if (d.Kind == "creditnote")
                {
                    type = ReceiptType.CreditNote;
                    original = doc.ReferenceInvoiceId == null ? null : await _db.IntegrationDocuments.AsNoTracking()
                        .Where(x => x.ConnectionId == c.Id && x.Kind == "invoice" && x.ExternalId == doc.ReferenceInvoiceId && x.ReceiptId != null).Select(x => x.ReceiptId).FirstOrDefaultAsync(ct);
                    if (original == null) throw GatewayException.Conflict("ORIGINAL_NOT_FISCALISED", "The invoice this credit note is for has not been fiscalised through the gateway (or the credit note is not linked to an invoice), so it cannot reference it. Fiscalise the invoice first.");
                }
                var notes = doc.Notes is { Length: > 0 } n && n.Trim().Length > 0 ? Cut(n.Trim(), 500) : null;
                if (type == ReceiptType.CreditNote && notes == null)
                {
                    var invoiceNo = await _db.IntegrationDocuments.AsNoTracking().Where(x => x.ConnectionId == c.Id && x.Kind == "invoice" && x.ExternalId == doc.ReferenceInvoiceId).Select(x => x.ExternalNumber).FirstOrDefaultAsync(ct);
                    notes = Cut($"Credit note {doc.Number} against invoice {invoiceNo} ({p.Label})", 500);
                }
                var req = new SubmitReceiptRequest(Ref(p, c, d), Cut(doc.Number, 50), type, doc.Currency.ToUpperInvariant(), true, lines, null, buyer, notes, original, p.Label);
                var view = await _receipts.PreviewAsync(c.ClientId, deviceId, req, ct);
                if (Math.Abs(Math.Abs(view.Total) - Math.Abs(doc.Total)) > 0.05m)
                    throw GatewayException.Unprocessable("TOTAL_MISMATCH", $"The fiscal total would be {Math.Abs(view.Total):0.00} but {p.Label} shows {Math.Abs(doc.Total):0.00} {doc.Currency}. Check the VAT rates on the lines.");
                var money = Enum.TryParse<MoneyType>(c.DefaultMoneyType, true, out var m) ? m : MoneyType.Credit;
                req = req with { Payments = new[] { new ReceiptPaymentRequest(money, Math.Abs(view.Total)) } };
                var receipt = await _receipts.SubmitAsync(c.ClientId, deviceId, req, ct);
                if (receipt.Status == "Rejected") throw GatewayException.Unprocessable(receipt.ErrorCode ?? "RECEIPT_REJECTED", receipt.ErrorMessage ?? "ZIMRA rejected the document.");
                d.Status = "Fiscalised"; d.ReceiptId = receipt.Id; d.FiscalisedAt = DateTime.UtcNow; d.Error = null; d.Attempts++;
                d.Total = doc.Total; d.ExternalNumber = Cut(doc.Number, 100);
                _db.AuditLogs.Add(new AuditLog { ClientId = c.ClientId, Action = "integration.fiscalise", Entity = "Receipt", EntityId = receipt.Id.ToString(), Detail = $"{p.Label} {d.Kind} {doc.Number}: {receipt.Total:0.00} {receipt.Currency}" });
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is GatewayException { Code: not "CONNECTOR_RECONNECT" } or FdmsException or ConnectorException { Unauthorized: false } or DbUpdateException)
            {
                if (ex is ConnectorException { RateLimited: true }) throw;
                d.Status = "Failed"; d.Error = Cut(ex.Message, 1000); d.Attempts++;
                await _db.SaveChangesAsync(ct);
                await _notify.RaiseAsync(c.ClientId, "integration", "bad", $"{p.Key}:failed:{d.Id}:{d.Attempts}", $"{p.Label} {(d.Kind == "creditnote" ? "credit note" : "invoice")} {d.ExternalNumber} was not fiscalised",
                    d.Error, p.Page, ct, email: d.Attempts == 1);
                return false;
            }
            if (c.PostBack) await PostBackOneAsync(c, p, d, ct);
            return true;
        }

        /// <summary>Writes the fiscal details back to the document (a note, and the fiscal PDF on invoices). A failure is recorded, never fatal.</summary>
        private async Task PostBackOneAsync(IntegrationConnection c, IntegrationProductDto p, IntegrationDocument d, CancellationToken ct)
        {
            if (d.ReceiptId is not { } receiptId) return;
            try
            {
                var s = await SessionAsync(c, p, ct);
                var r = await _receipts.GetAsync(c.ClientId, receiptId, ct);
                var zimra = await _db.Devices.AsNoTracking().Where(x => x.Id == r.DeviceId).Select(x => x.ZimraDeviceId).FirstOrDefaultAsync(ct);
                var fiscalNo = $"{zimra}/{r.ReceiptGlobalNo}";
                var code = IntegrationService.VerificationCode(r.QrUrl);
                var title = r.Type == "CreditNote" ? "FISCAL TAX CREDIT NOTE" : "FISCAL TAX INVOICE";
                var text = $"{title} fiscalised with ZIMRA. Fiscal number {fiscalNo} (device {zimra}, fiscal day {r.FiscalDayNo}, receipt {r.ReceiptCounter})"
                           + (code != null ? $", verification code {code}" : "") + (r.QrUrl != null ? $". Verify: {r.QrUrl}" : ".");
                byte[]? pdf = null;
                if (d.Kind == "invoice") try { pdf = await _receipts.GetPdfAsync(c.ClientId, receiptId, true, ct); } catch (GatewayException) { }
                Count(c);
                await Adapter(p).WriteBackAsync(s, d.Kind, d.ExternalId, new ConnectorWriteBack(text, fiscalNo, code, r.QrUrl, pdf, $"fiscal-{Regex.Replace(d.ExternalNumber, "[^A-Za-z0-9_-]", "_")}.pdf"), ct);
                d.PostedBackAt = DateTime.UtcNow; d.PostBackError = null;
            }
            catch (Exception ex) when (ex is GatewayException or ConnectorException or FdmsException or HttpRequestException) { d.PostBackError = Cut(ex.Message, 1000); }
            await _db.SaveChangesAsync(ct);
        }

        // =============================================================================================================
        // documents
        // =============================================================================================================

        private async Task<List<IntegrationDocumentDto>> DtosAsync(List<IntegrationDocument> docs, CancellationToken ct)
        {
            var ids = docs.Where(d => d.ReceiptId != null).Select(d => d.ReceiptId!.Value).ToList();
            var receipts = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.ReceiptGlobalNo, r.DeviceId }).ToListAsync(ct);
            var deviceIds = receipts.Select(r => r.DeviceId).Distinct().ToList();
            var zimra = await _db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ZimraDeviceId, ct);
            return docs.Select(d =>
            {
                var r = receipts.FirstOrDefault(x => x.Id == d.ReceiptId);
                return new IntegrationDocumentDto(d.Id, d.Kind, d.ExternalId, d.ExternalNumber, d.CustomerName, d.DocumentDate, d.Total, d.Currency, d.ExternalStatus, d.Status, d.ReceiptId,
                    r == null ? null : $"{(zimra.TryGetValue(r.DeviceId, out var z) ? z : 0)}/{r.ReceiptGlobalNo}", d.Error, d.Attempts, d.PostedBackAt, d.PostBackError, d.ChangedAfterFiscalised, d.CreatedAt, d.FiscalisedAt, null);
            }).ToList();
        }

        public async Task<IntegrationDocumentPage> DocumentsAsync(Guid clientId, string product, string? status, string? search, int page, int pageSize, CancellationToken ct)
        {
            RequireWholeCompany();
            var p = Product(product);
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 200);
            var q = DocsOf(clientId, p).AsNoTracking();
            q = status switch
            {
                "Pending" or "Fiscalised" or "Failed" or "Skipped" => q.Where(d => d.Status == status),
                "Changed" => q.Where(d => d.ChangedAfterFiscalised),
                "NotPostedBack" => q.Where(d => d.Status == "Fiscalised" && (d.PostedBackAt == null || d.PostBackError != null)),
                _ => q,
            };
            if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); q = q.Where(d => d.ExternalNumber.Contains(s) || (d.CustomerName != null && d.CustomerName.Contains(s))); }
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(d => d.DocumentDate).ThenByDescending(d => d.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return new IntegrationDocumentPage(await DtosAsync(items, ct), total, page, pageSize);
        }

        private async Task<(IntegrationConnection C, IntegrationDocument D, IntegrationProductDto P)> DocumentAsync(Guid clientId, string product, Guid id, bool active, CancellationToken ct)
        {
            RequireWholeCompany();
            var p = Product(product);
            if (active) await IntegrationAccessService.EnsureActiveAsync(_db, clientId, ct, p.Key);
            var c = await LiveConnectionAsync(clientId, p, ct);
            var d = await _db.IntegrationDocuments.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId && x.ConnectionId == c.Id, ct) ?? throw GatewayException.NotFound("Document");
            return (c, d, p);
        }

        public async Task<IntegrationDocumentDto> FiscaliseAsync(Guid clientId, string product, Guid documentId, CancellationToken ct)
        {
            var (c, d, p) = await DocumentAsync(clientId, product, documentId, true, ct);
            if (c.DeviceId == null) throw GatewayException.BadRequest("DEVICE_REQUIRED", "Choose the fiscal device under the connection settings first.");
            await using var _ = await _locks.AcquireAsync(c.Id, ct);
            await _db.Entry(c).ReloadAsync(ct); await _db.Entry(d).ReloadAsync(ct);
            if (d.Status == "Fiscalised") throw GatewayException.Conflict("ALREADY_FISCALISED", $"This document is fiscalised already. To change it, issue a credit note in {p.Label}.");
            await FiscaliseOneAsync(c, p, d, ct);
            return (await DtosAsync(new List<IntegrationDocument> { d }, ct))[0];
        }

        public async Task<IntegrationDocumentDto> SkipAsync(Guid clientId, string product, Guid documentId, CancellationToken ct)
        {
            var (_, d, _) = await DocumentAsync(clientId, product, documentId, false, ct);
            if (d.Status == "Fiscalised") throw GatewayException.Conflict("ALREADY_FISCALISED", "A fiscalised document cannot be skipped.");
            d.Status = d.Status == "Skipped" ? "Pending" : "Skipped"; d.Error = d.Status == "Skipped" ? "Skipped by hand." : null;
            await _db.SaveChangesAsync(ct);
            return (await DtosAsync(new List<IntegrationDocument> { d }, ct))[0];
        }

        public async Task<IntegrationDocumentDto> PostBackAsync(Guid clientId, string product, Guid documentId, CancellationToken ct)
        {
            var (c, d, p) = await DocumentAsync(clientId, product, documentId, true, ct);
            if (d.Status != "Fiscalised") throw GatewayException.Conflict("NOT_FISCALISED", "Only a fiscalised document can be written back.");
            await PostBackOneAsync(c, p, d, ct);
            return (await DtosAsync(new List<IntegrationDocument> { d }, ct))[0];
        }

        // =============================================================================================================
        // the background round
        // =============================================================================================================

        public async Task<int> RunScheduledAsync(DateTime now, CancellationToken ct)
        {
            var minutes = Math.Max(1, _cfg.GetValue("Integrations:SyncMinutes", 5));
            var providers = IntegrationProducts.Connectors.Select(p => p.ConnectionProvider).ToList();
            var active = _db.IntegrationAccess.Where(a => a.Status == "Active");
            var due = await _db.IntegrationConnections.AsNoTracking()
                .Where(c => providers.Contains(c.Provider) && c.Status == "Connected" && c.OrganizationId != null
                            && active.Any(a => a.ClientId == c.ClientId && a.Provider == c.Provider)
                            && (c.SyncRequestedAt != null || c.LastSyncAt == null || c.LastSyncAt < now.AddMinutes(-minutes)))
                .Select(c => new { c.Id, c.Provider }).Take(50).ToListAsync(ct);
            var n = 0;
            foreach (var x in due)
            {
                var p = IntegrationProducts.Connectors.First(q => q.ConnectionProvider == x.Provider);
                if (!_connectors.ContainsKey(p.Key)) continue;
                try
                {
                    await using var _ = await _locks.AcquireAsync(x.Id, ct);
                    var c = await _db.IntegrationConnections.FirstAsync(q => q.Id == x.Id, ct);
                    await SyncAsync(c, p, ct);
                    n++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "{Product} sync of {Connection} failed", p.Label, x.Id); }
                _db.ChangeTracker.Clear();
            }
            return n;
        }

        // =============================================================================================================
        // the platform owner
        // =============================================================================================================

        public async Task<IReadOnlyList<ConnectorAppDto>> AppsAsync(CancellationToken ct)
        {
            var list = new List<ConnectorAppDto>();
            foreach (var p in IntegrationProducts.Connectors) list.Add(await AppDtoAsync(p, ct));
            return list;
        }

        private async Task<ConnectorAppDto> AppDtoAsync(IntegrationProductDto p, CancellationToken ct)
        {
            var (id, secret, sandbox) = await _settings.ConnectorAppAsync(p.Key, ct);
            return new ConnectorAppDto(p.Key, p.Label, p.Auth, id, secret != null, sandbox, await RedirectUriAsync(p, ct), p.Auth != "oauth" || (!string.IsNullOrWhiteSpace(id) && secret != null));
        }

        public async Task<ConnectorAppDto> SaveAppAsync(string product, SaveConnectorAppRequest req, CancellationToken ct)
        {
            var p = Product(product);
            if (p.Auth != "oauth") throw GatewayException.BadRequest("CONNECTOR_KEY", $"{p.Label} needs no app: each company connects with its own address and API key.");
            var id = string.IsNullOrWhiteSpace(req.ClientId) ? null : req.ClientId.Trim();
            if (id is { Length: > 200 } || req.ClientSecret is { Length: > 500 }) throw GatewayException.BadRequest("TOO_LONG", "That value is too long.");
            await _settings.SaveConnectorAppAsync(p.Key, id, req.ClientSecret?.Trim(), req.ClearSecret, req.Sandbox, _scope.UserName, ct);
            _db.AuditLogs.Add(new AuditLog { Action = "settings.connector-app", Entity = "Setting", EntityId = "Integrations.App." + p.Key, Detail = $"{p.Label}{(req.ClientSecret is { Length: > 0 } ? ", secret changed" : "")}{(req.Sandbox ? ", sandbox" : "")}" });
            await _db.SaveChangesAsync(ct);
            return await AppDtoAsync(p, ct);
        }

        public Task<IntegrationPricingDto> PricingAsync(string product, CancellationToken ct) => _settings.PricingAsync(Product(product).Key, ct);

        public async Task<IntegrationPricingDto> SavePricingAsync(string product, IntegrationPricingDto req, CancellationToken ct)
        {
            var p = Product(product);
            if (req.MonthlyFee < 0 || req.PricePerExtraDocument < 0 || req.IncludedDocuments < 0 || req.SetupFee < 0) throw GatewayException.BadRequest("PRICE_INVALID", "Prices and the included documents cannot be negative.");
            if (req.MonthlyFee > 100000 || req.SetupFee > 1_000_000 || req.PricePerExtraDocument > 1000 || req.IncludedDocuments > 10_000_000) throw GatewayException.BadRequest("PRICE_INVALID", "That price is too high.");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!Regex.IsMatch(cur, "^[A-Z]{3}$")) throw GatewayException.BadRequest("CURRENCY_INVALID", "The currency is a 3-letter code such as USD.");
            var saved = new IntegrationPricingDto(req.Charge, decimal.Round(req.MonthlyFee, 2), cur, req.IncludedDocuments, decimal.Round(req.PricePerExtraDocument, 4), 0, decimal.Round(req.SetupFee, 2));
            await _settings.SavePricingAsync(p.Key, saved, _scope.UserName, ct);
            _db.AuditLogs.Add(new AuditLog { Action = "settings.integration-pricing", Entity = "Setting", EntityId = p.Key,
                Detail = $"{p.Label} {(saved.Charge ? "on" : "off")}: set-up {saved.SetupFee}, {saved.MonthlyFee} {saved.Currency}/month, {saved.IncludedDocuments} included, {saved.PricePerExtraDocument} per extra" });
            await _db.SaveChangesAsync(ct);
            return saved;
        }
    }

    /// <summary>The connectors' monthly fee and extra documents, added to the subscription invoice like Zoho's (same currency only, while the product is on).</summary>
    public static class ConnectorBilling
    {
        public static async Task<(decimal Amount, List<string> Parts)> ChargeAsync(FdmsDbContext db, IntegrationSettings settings, Guid clientId, DateTime periodStart, DateTime periodEnd, string currency, CancellationToken ct)
        {
            decimal total = 0; var parts = new List<string>();
            var months = Math.Max(1, (int)Math.Round((periodEnd - periodStart).TotalDays / 30.4));
            var from = periodStart - (periodEnd - periodStart);
            foreach (var p in IntegrationProducts.Connectors)
            {
                var price = await settings.PricingAsync(p.Key, ct);
                if (!price.Charge || !string.Equals(price.Currency, currency, StringComparison.OrdinalIgnoreCase)) continue;
                if (!await IntegrationAccessService.IsActiveAsync(db, clientId, ct, p.Key)) continue;
                var c = await db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == p.ConnectionProvider, ct);
                if (c == null) continue;
                var used = await db.IntegrationDocuments.AsNoTracking().CountAsync(d => d.ConnectionId == c.Id && d.Status == "Fiscalised" && d.FiscalisedAt >= from && d.FiscalisedAt < periodStart, ct);
                var fee = c.Status != "Disconnected" ? price.MonthlyFee * months : 0;
                var extra = Math.Max(0, used - price.IncludedDocuments * months);
                var amount = decimal.Round(fee + extra * price.PricePerExtraDocument, 2);
                if (amount <= 0) continue;
                total += amount;
                parts.Add(string.Join(", ", new[] { fee > 0 ? $"{p.Label} connection x{months} month(s)" : null, extra > 0 ? $"{extra} extra {p.Label} document(s)" : null }.Where(x => x != null)));
            }
            return (total, parts);
        }
    }
}
