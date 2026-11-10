using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Integrations
{
    /// <summary>
    /// The platform's Zoho app (client ID and secret from Zoho's API console) and what connections cost. Kept in Settings; the secret is encrypted.
    /// The configuration file (Zoho:ClientId, Zoho:ClientSecret) applies when nothing is saved.
    /// </summary>
    public sealed class IntegrationSettings
    {
        public const string AppKey = "Integrations.Zoho";
        public const string PricingKey = "Integrations.Pricing";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly FdmsDbContext _db;
        private readonly IConfiguration _cfg;
        private readonly IDataProtector _secret;
        public IntegrationSettings(FdmsDbContext db, IConfiguration cfg, IDataProtectionProvider dp) { _db = db; _cfg = cfg; _secret = dp.CreateProtector("Fdms.Integrations.App.v1"); }

        private sealed record StoredApp(string? ClientId, string? SecretProtected);

        private async Task<T?> ReadAsync<T>(string key, CancellationToken ct) where T : class
        {
            var json = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
            try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json); } catch (JsonException) { return null; }
        }

        private async Task WriteAsync(string key, object value, string? by, CancellationToken ct)
        {
            var row = await _db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = key });
            row.Value = JsonSerializer.Serialize(value, Json); row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = by;
        }

        /// <summary>The Zoho app in use (null secret = not set).</summary>
        public async Task<(string? ClientId, string? Secret, bool FromDatabase)> AppAsync(CancellationToken ct)
        {
            var s = await ReadAsync<StoredApp>(AppKey, ct);
            if (s != null)
            {
                string? secret = null;
                if (!string.IsNullOrEmpty(s.SecretProtected)) try { secret = _secret.Unprotect(s.SecretProtected); } catch (CryptographicException) { }
                return (s.ClientId, secret, true);
            }
            return (_cfg["Zoho:ClientId"] is { Length: > 0 } id ? id : null, _cfg["Zoho:ClientSecret"] is { Length: > 0 } sec ? sec : null, false);
        }

        public async Task SaveAppAsync(string? clientId, string? newSecret, bool clearSecret, string? by, CancellationToken ct)
        {
            var old = await ReadAsync<StoredApp>(AppKey, ct);
            // nothing saved yet: the secret from the configuration file carries over unless a new one is typed
            var kept = old != null ? old.SecretProtected : _cfg["Zoho:ClientSecret"] is { Length: > 0 } cfgSecret ? _secret.Protect(cfgSecret) : null;
            var secret = clearSecret ? null : !string.IsNullOrEmpty(newSecret) ? _secret.Protect(newSecret) : kept;
            await WriteAsync(AppKey, new StoredApp(clientId, secret), by, ct);
        }

        public static readonly IntegrationPricingDto DefaultPricing = new(false, 10m, "USD", 200, 0.05m);
        public async Task<IntegrationPricingDto> PricingAsync(CancellationToken ct) => await ReadAsync<IntegrationPricingDto>(PricingKey, ct) ?? DefaultPricing;
        public Task SavePricingAsync(IntegrationPricingDto p, string? by, CancellationToken ct) => WriteAsync(PricingKey, p, by, ct);

        // ---- each accounting product has its own prices and (OAuth products) its own app ----
        private static string PricingKeyFor(string product) => product == IntegrationProducts.Zoho ? PricingKey : PricingKey + "." + product;
        public async Task<IntegrationPricingDto> PricingAsync(string product, CancellationToken ct) => await ReadAsync<IntegrationPricingDto>(PricingKeyFor(product), ct) ?? DefaultPricing;
        public Task SavePricingAsync(string product, IntegrationPricingDto p, string? by, CancellationToken ct) => WriteAsync(PricingKeyFor(product), p, by, ct);

        private sealed record StoredConnectorApp(string? ClientId, string? SecretProtected, bool Sandbox);
        public async Task<(string? ClientId, string? Secret, bool Sandbox)> ConnectorAppAsync(string product, CancellationToken ct)
        {
            var s = await ReadAsync<StoredConnectorApp>("Integrations.App." + product, ct);
            string? secret = null;
            if (!string.IsNullOrEmpty(s?.SecretProtected)) try { secret = _secret.Unprotect(s.SecretProtected); } catch (CryptographicException) { }
            return (s?.ClientId, secret, s?.Sandbox ?? false);
        }
        public async Task SaveConnectorAppAsync(string product, string? clientId, string? newSecret, bool clearSecret, bool sandbox, string? by, CancellationToken ct)
        {
            var old = await ReadAsync<StoredConnectorApp>("Integrations.App." + product, ct);
            var secret = clearSecret ? null : !string.IsNullOrEmpty(newSecret) ? _secret.Protect(newSecret) : old?.SecretProtected;
            await WriteAsync("Integrations.App." + product, new StoredConnectorApp(clientId, secret, sandbox), by, ct);
        }
    }

    /// <summary>
    /// Zoho Books ⇄ ZIMRA. A company signs in to Zoho once (OAuth); the gateway keeps the refresh token encrypted and renews the short-lived access token itself.
    /// Invoices and credit notes changed in Zoho are pulled (every few minutes, or at once when Zoho's workflow webhook calls), fiscalised on the company's device
    /// (automatically or when someone clicks Fiscalise) and the fiscal number, verification code, link and PDF are written back to the Zoho document.
    /// A Zoho document becomes one fiscal receipt, never two: the receipt's external reference is the Zoho ID, and the gateway returns the existing receipt on a repeat.
    /// </summary>
    public sealed class IntegrationService : IIntegrationService
    {
        public const string Provider = "zoho-books";
        // full access: fiscalising needs invoices, credit notes and contacts; the bridge lets partners reach the other modules the company allows
        public const string Scopes = "ZohoBooks.fullaccess.all";
        private const int MaxPagesPerSync = 20;
        private const int MaxFiscalisePerSync = 50;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly string[] MoneyTypes = Enum.GetNames<MoneyType>();

        private readonly FdmsDbContext _db;
        private readonly IZohoApi _zoho;
        private readonly IntegrationSettings _settings;
        private readonly IReceiptService _receipts;
        private readonly INotificationService _notify;
        private readonly IBillingService _billing;
        private readonly PublicUrls _urls;
        private readonly DeviceLocks _locks;
        private readonly ICallerScope _scope;
        private readonly IConfiguration _cfg;
        private readonly ILogger<IntegrationService> _log;
        private readonly IDataProtector _tokens;
        private readonly ITimeLimitedDataProtector _state;

        public IntegrationService(FdmsDbContext db, IZohoApi zoho, IntegrationSettings settings, IReceiptService receipts, INotificationService notify, IBillingService billing,
            PublicUrls urls, DeviceLocks locks, ICallerScope scope, IConfiguration cfg, IDataProtectionProvider dp, ILogger<IntegrationService> log)
        {
            _db = db; _zoho = zoho; _settings = settings; _receipts = receipts; _notify = notify; _billing = billing; _urls = urls; _locks = locks; _scope = scope; _cfg = cfg; _log = log;
            _tokens = dp.CreateProtector("Fdms.Integrations.v1");
            _state = dp.CreateProtector("Fdms.Integrations.State.v1").ToTimeLimitedDataProtector();
        }

        // =============================================================================================================
        // helpers
        // =============================================================================================================

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot manage the accounting connection.");
        }

        private static string Cut(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];

        private async Task<string> RedirectUriAsync(CancellationToken ct) =>
            _cfg["Zoho:RedirectUri"] is { Length: > 0 } fixedUri ? fixedUri : (await _urls.ApiAsync(ct)).TrimEnd('/') + "/api/v1/integrations/zoho/callback";

        private async Task<IntegrationConnection?> ConnectionAsync(Guid clientId, CancellationToken ct) =>
            await _db.IntegrationConnections.FirstOrDefaultAsync(c => c.ClientId == clientId && c.Provider == Provider, ct);

        private async Task<IntegrationConnection> LiveConnectionAsync(Guid clientId, CancellationToken ct)
        {
            var c = await ConnectionAsync(clientId, ct);
            if (c == null || c.Status == "Disconnected") throw GatewayException.Conflict("ZOHO_NOT_CONNECTED", "Connect Zoho Books first.");
            if (c.Status == "ChooseOrganization") throw GatewayException.Conflict("ZOHO_CHOOSE_ORGANIZATION", "Choose which Zoho Books organisation to use.");
            if (c.Status == "NeedsReconnect") throw GatewayException.Conflict("ZOHO_RECONNECT", "Zoho no longer accepts this connection. Click Reconnect and sign in to Zoho again.");
            return c;
        }

        internal static void Count(IntegrationConnection c, int calls = 1)
        {
            var today = DateTime.UtcNow.Date;
            if (c.ApiCallsDay?.Date != today) { c.ApiCallsDay = today; c.ApiCallsToday = 0; }
            c.ApiCallsToday += calls; c.ApiCallsTotal += calls;
        }

        private string? Unprotect(string? p) { if (string.IsNullOrEmpty(p)) return null; try { return _tokens.Unprotect(p); } catch (CryptographicException) { return null; } }

        /// <summary>A working access token: the saved one while it has two minutes left, otherwise a fresh one from the refresh token.</summary>
        internal async Task<ZohoAccess> AccessAsync(IntegrationConnection c, CancellationToken ct)
        {
            var org = c.OrganizationId ?? throw GatewayException.Conflict("ZOHO_CHOOSE_ORGANIZATION", "Choose which Zoho Books organisation to use.");
            var token = c.AccessTokenExpiresAt > DateTime.UtcNow.AddMinutes(2) ? Unprotect(c.AccessTokenProtected) : null;
            if (token != null) return new ZohoAccess(c.ApiDomain, token, org);

            var refresh = Unprotect(c.RefreshTokenProtected);
            var app = await AppForAsync(c, ct);
            if (refresh == null || app.ClientId == null || app.Secret == null) { await NeedsReconnectAsync(c, "The saved Zoho sign-in is missing or unreadable.", ct); throw Reconnect(); }
            try
            {
                Count(c);
                var t = await _zoho.RefreshAsync(c.AccountsServer, app.ClientId, app.Secret, refresh, ct);
                c.AccessTokenProtected = _tokens.Protect(t.AccessToken);
                c.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, t.ExpiresInSeconds));
                c.LastTokenRefreshAt = DateTime.UtcNow;
                if (t.RefreshToken != null) c.RefreshTokenProtected = _tokens.Protect(t.RefreshToken);
                await _db.SaveChangesAsync(ct);
                return new ZohoAccess(c.ApiDomain, t.AccessToken, org);
            }
            catch (ZohoException ex) when (ex.Unauthorized)
            {
                await NeedsReconnectAsync(c, ex.Message, ct);
                throw Reconnect();
            }
        }

        private static GatewayException Reconnect() => GatewayException.Conflict("ZOHO_RECONNECT", "Zoho no longer accepts this connection. Click Reconnect and sign in to Zoho again.");

        internal async Task NeedsReconnectAsync(IntegrationConnection c, string why, CancellationToken ct)
        {
            c.Status = "NeedsReconnect"; c.AccessTokenProtected = null; c.AccessTokenExpiresAt = null;
            c.LastError = Cut(why, 1000); c.LastErrorAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = c.ClientId, Action = "integration.reconnect", Entity = "IntegrationConnection", EntityId = c.Id.ToString(), Detail = Cut(why, 400) });
            await _db.SaveChangesAsync(ct);
            await _notify.RaiseAsync(c.ClientId, "integration", "bad", $"zoho:reconnect:{c.Id}:{DateTime.UtcNow:yyyyMMdd}", "Zoho Books needs you to sign in again",
                "The connection to Zoho Books stopped working (the access was revoked or expired). Open Integrations and click Reconnect. Nothing is fiscalised until then.", "/integrations", ct);
        }

        private async Task FailAsync(IntegrationConnection c, Exception ex, CancellationToken ct)
        {
            c.LastError = Cut(ex.Message, 1000); c.LastErrorAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        // =============================================================================================================
        // connection
        // =============================================================================================================

        /// <summary>The Zoho app a connection signs in with: the company's own when it gave one, otherwise the platform's.</summary>
        private async Task<(string? ClientId, string? Secret, bool Own)> AppForAsync(IntegrationConnection? c, CancellationToken ct)
        {
            if (c?.OwnClientId != null) return (c.OwnClientId, Unprotect(c.OwnClientSecretProtected), true);
            var a = await _settings.AppAsync(ct);
            return (a.ClientId, a.Secret, false);
        }

        public async Task<ZohoConnectionDto> ZohoAsync(Guid clientId, CancellationToken ct)
        {
            var app = await _settings.AppAsync(ct);
            var platform = IsZohoClientId(app.ClientId) && app.Secret != null;
            var pricing = await _settings.PricingAsync(ct);
            var regions = ZohoApi.Regions.Select(r => new ZohoRegionDto(r.Key, r.Label)).ToList();
            var c = await _db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == Provider, ct);
            var own = c?.OwnClientId;
            // a company can always connect with its own Zoho app, even when the platform has none
            var access = await _db.IntegrationAccess.AsNoTracking().Where(a => a.ClientId == clientId).Select(a => a.Status).FirstOrDefaultAsync(ct) ?? "None";
            var unavailable = platform || own != null ? null : "The platform has no shared Zoho app yet. Connect with your own Zoho app (Self Client) below.";
            var redirect = await RedirectUriAsync(ct);
            if (c == null || c.Status == "Disconnected")
                return new ZohoConnectionDto(true, unavailable, regions, c?.Id, "NotConnected", null, null, Array.Empty<ZohoOrganization>(), null, null, null, null, null, c?.LastSyncAt,
                    null, null, 0, c?.ApiCallsTotal ?? 0, c?.AutoFiscalise ?? false, c?.DeviceId, c?.DefaultHsCode, c?.DefaultMoneyType ?? "Credit", c?.PostBack ?? true, c?.StartDate, null,
                    await CountsAsync(clientId, ct), pricing, platform, own, redirect, access);
            var orgs = c.Status == "ChooseOrganization" && c.OrganizationsJson != null ? JsonSerializer.Deserialize<List<ZohoOrganization>>(c.OrganizationsJson, Json) ?? new() : new List<ZohoOrganization>();
            var webhook = (await _urls.ApiAsync(ct)).TrimEnd('/') + $"/api/v1/integrations/zoho/hook/{c.Id:N}/{c.WebhookKey}";
            return new ZohoConnectionDto(true, unavailable, regions, c.Id, c.Status, c.OrganizationId, c.OrganizationName, orgs, c.AccountsServer, c.ConnectedBy, c.ConnectedAt,
                c.LastTokenRefreshAt, c.AccessTokenExpiresAt, c.LastSyncAt, c.LastError, c.LastErrorAt, c.ApiCallsDay?.Date == DateTime.UtcNow.Date ? c.ApiCallsToday : 0, c.ApiCallsTotal,
                c.AutoFiscalise, c.DeviceId, c.DefaultHsCode, c.DefaultMoneyType, c.PostBack, c.StartDate, webhook, await CountsAsync(clientId, ct), pricing, platform, own, redirect, access);
        }

        private async Task<IntegrationCountsDto> CountsAsync(Guid clientId, CancellationToken ct)
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var zoho = _db.IntegrationConnections.Where(x => x.ClientId == clientId && x.Provider == Provider).Select(x => x.Id);
            var q = _db.IntegrationDocuments.AsNoTracking().Where(d => d.ClientId == clientId && zoho.Contains(d.ConnectionId));
            var by = await q.GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            int N(string s) => by.FirstOrDefault(x => x.Key == s)?.N ?? 0;
            return new IntegrationCountsDto(N("Pending"), N("Fiscalised"), N("Failed"), N("Skipped"),
                await q.CountAsync(d => d.Status == "Fiscalised" && d.FiscalisedAt >= monthStart, ct),
                await q.CountAsync(d => d.Status == "Fiscalised" && d.PostedBackAt == null, ct));
        }

        private string SignInUrl(Guid clientId, (string Key, string Label, string Accounts, string Api) region, string appClientId, string redirect)
        {
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            var state = _state.Protect($"{clientId:N}|{_scope.UserId}|{(_scope.UserName ?? "").Replace("|", " ")}|{region.Key}|{nonce}", TimeSpan.FromMinutes(20));
            return $"{region.Accounts}/oauth/v2/auth?scope={Uri.EscapeDataString(Scopes)}&client_id={Uri.EscapeDataString(appClientId)}&response_type=code&access_type=offline&prompt=consent"
                   + $"&redirect_uri={Uri.EscapeDataString(redirect)}&state={Uri.EscapeDataString(state)}";
        }

        private static (string Key, string Label, string Accounts, string Api) Region(string? key)
        {
            var region = ZohoApi.Regions.FirstOrDefault(r => r.Key == (string.IsNullOrWhiteSpace(key) ? "com" : key));
            if (region.Key == null) throw GatewayException.BadRequest("REGION_INVALID", "Choose where your Zoho account is (the address you sign in at).");
            return region;
        }

        public async Task<StartZohoResult> StartZohoAsync(Guid clientId, StartZohoRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct);
            var app = await AppForAsync(await ConnectionAsync(clientId, ct), ct);
            if (!IsZohoClientId(app.ClientId) || app.Secret == null) throw GatewayException.Conflict("ZOHO_NOT_CONFIGURED", "The platform has no shared Zoho app yet. Connect with your own Zoho app (Self Client) instead.");
            return new StartZohoResult(SignInUrl(clientId, Region(req.Region), app.ClientId!, await RedirectUriAsync(ct)));
        }

        /// <summary>A Zoho client ID looks like 1000.XXXXXXXXXXXXXXXXXXXXXXXXXXXXXX.</summary>
        public static bool IsZohoClientId(string? id) => id != null && Regex.IsMatch(id, @"^1000\.[A-Za-z0-9]{8,80}$");

        public async Task<ConnectOwnZohoResult> ConnectOwnAsync(Guid clientId, ConnectOwnZohoRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct);
            var region = Region(req.Region);
            var id = (req.ClientId ?? "").Trim();
            if (!IsZohoClientId(id)) throw GatewayException.BadRequest("CLIENT_ID_INVALID", "The client ID starts with \"1000.\" — copy it from your app in Zoho's API console (api-console.zoho.com), with nothing before or after it.");
            var secret = req.ClientSecret?.Trim();
            if (secret is { Length: > 200 } || (secret != null && secret.Contains(' '))) throw GatewayException.BadRequest("SECRET_INVALID", "That client secret does not look right. Copy it again from Zoho's API console.");
            var code = req.GrantCode?.Trim();
            if (code is { Length: > 300 } || (code != null && code.Contains(' '))) throw GatewayException.BadRequest("CODE_INVALID", "That code does not look right. Generate a new one in Zoho's API console (Self Client).");

            var c = await ConnectionAsync(clientId, ct);
            if (c == null) { c = new IntegrationConnection { ClientId = clientId, Provider = Provider, Status = "Disconnected", StartDate = DateTime.UtcNow.Date }; _db.IntegrationConnections.Add(c); }
            if (string.IsNullOrEmpty(secret) && (c.OwnClientId != id || c.OwnClientSecretProtected == null))
                throw GatewayException.BadRequest("SECRET_REQUIRED", "Enter the client secret of your Zoho app.");
            c.OwnClientId = id;
            if (!string.IsNullOrEmpty(secret)) c.OwnClientSecretProtected = _tokens.Protect(secret);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.own-app", Entity = "IntegrationConnection", EntityId = c.Id.ToString(), Detail = $"own Zoho app {id[..Math.Min(12, id.Length)]}…{(secret != null ? ", secret changed" : "")}" });
            await _db.SaveChangesAsync(ct);

            // no code: the company's server-based app signs in through Zoho's page and comes back to our redirect address
            if (string.IsNullOrEmpty(code)) return new ConnectOwnZohoResult(SignInUrl(clientId, region, id, await RedirectUriAsync(ct)), null);

            ZohoTokens tokens;
            try { tokens = await _zoho.ExchangeCodeAsync(region.Accounts, id, Unprotect(c.OwnClientSecretProtected)!, null, code, ct); }
            catch (ZohoException ex)
            {
                var why = ex.Message.Contains("invalid_client")
                    ? "Zoho does not recognise this client ID and secret. Copy both from Self Client → Client Secret in Zoho's API console, and check the data centre."
                    : ex.Message.Contains("invalid_code")
                        ? "Zoho refused the code. A code works only with the client ID and secret of the client that made it (use the Self Client's own ID and secret, not another app's), only once, and only for the minutes chosen. Generate a new code and connect straight away."
                        : $"{ex.Message}. Generate a new code with scope {Scopes} and try again straight away; check the client ID, secret and data centre too.";
                throw GatewayException.BadRequest("ZOHO_CODE_REFUSED", why);
            }
            var problem = await FinishConnectAsync(c, clientId, region, region.Accounts, tokens, _scope.UserName, ct);
            if (problem != null) throw GatewayException.BadRequest("ZOHO_CONNECT_FAILED", problem);
            return new ConnectOwnZohoResult(null, await ZohoAsync(clientId, ct));
        }

        public async Task<ZohoConnectionDto> ForgetOwnAppAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            var c = await ConnectionAsync(clientId, ct);
            if (c?.OwnClientId != null)
            {
                c.OwnClientId = null; c.OwnClientSecretProtected = null;
                if (c.Status != "Disconnected") { c.Status = "NeedsReconnect"; c.RefreshTokenProtected = null; c.AccessTokenProtected = null; c.AccessTokenExpiresAt = null; }   // its tokens belonged to that app
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.own-app.remove", Entity = "IntegrationConnection", EntityId = c.Id.ToString() });
                await _db.SaveChangesAsync(ct);
            }
            return await ZohoAsync(clientId, ct);
        }

        /// <summary>After Zoho handed out tokens: keep them, look up the organisations, and pick one when there is no choice to make. Returns why it failed, or null.</summary>
        private async Task<string?> FinishConnectAsync(IntegrationConnection c, Guid clientId, (string Key, string Label, string Accounts, string Api) region, string accounts, ZohoTokens tokens, string? by, CancellationToken ct)
        {
            if (tokens.RefreshToken == null) return "Zoho gave no refresh token. For a Self Client, generate a new code; for a sign-in, try again.";
            var apiDomain = tokens.ApiDomain is { } ad && ZohoApi.IsApiDomain(ad) ? ad.TrimEnd('/') : ZohoApi.Regions.FirstOrDefault(r => r.Accounts == accounts).Api ?? region.Api;
            IReadOnlyList<ZohoOrganization> orgs;
            try { orgs = await _zoho.OrganizationsAsync(apiDomain, tokens.AccessToken, ct); }
            catch (ZohoException ex) { return "Zoho Books did not list your organisations: " + ex.Message + ". The code must be made with scope " + Scopes + "."; }
            if (orgs.Count == 0) return "This Zoho account has no Zoho Books organisation.";

            var previousOrg = c.OrganizationId;
            c.AccountsServer = accounts; c.ApiDomain = apiDomain;
            c.RefreshTokenProtected = _tokens.Protect(tokens.RefreshToken);
            c.AccessTokenProtected = _tokens.Protect(tokens.AccessToken);
            c.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(60, tokens.ExpiresInSeconds));
            c.LastTokenRefreshAt = DateTime.UtcNow;
            c.ConnectedAt = DateTime.UtcNow; c.ConnectedBy = string.IsNullOrWhiteSpace(by) ? null : Cut(by, 200);
            c.LastError = null; c.LastErrorAt = null;
            c.StartDate ??= DateTime.UtcNow.Date;
            c.FullAccess = Scopes.Contains("fullaccess");
            Count(c, 2);
            // the same organisation as before keeps its place; one organisation is chosen straight away
            var keep = previousOrg != null ? orgs.FirstOrDefault(o => o.Id == previousOrg) : null;
            var pick = keep ?? (orgs.Count == 1 ? orgs[0] : null);
            if (pick != null) UseOrganization(c, pick);
            else { c.Status = "ChooseOrganization"; c.OrganizationsJson = JsonSerializer.Serialize(orgs.Take(50), Json); }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.connect", Entity = "IntegrationConnection", EntityId = c.Id.ToString(),
                Detail = $"Zoho Books at {accounts}{(pick != null ? ", organisation " + pick.Name : "")} by {c.ConnectedBy}{(c.OwnClientId != null ? " (own Zoho app)" : "")}" });
            await _db.SaveChangesAsync(ct);
            return null;
        }

        public async Task<ZohoCallbackResult> CompleteZohoAsync(string? code, string? state, string? accountsServer, string? error, CancellationToken ct)
        {
            var appUrl = (await _urls.AppAsync(ct)).TrimEnd('/') + "/integrations";
            ZohoCallbackResult Fail(string why) => new(false, appUrl + "?zoho=error&reason=" + Uri.EscapeDataString(why));
            if (!string.IsNullOrEmpty(error)) return new(false, appUrl + "?zoho=" + (error == "access_denied" ? "denied" : "error&reason=" + Uri.EscapeDataString(Cut(error, 60))));
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return Fail("missing");

            string[] parts;
            try { parts = _state.Unprotect(state).Split('|'); } catch (CryptographicException) { return Fail("expired"); }
            if (parts.Length != 5 || !Guid.TryParseExact(parts[0], "N", out var clientId)) return Fail("expired");
            var region = ZohoApi.Regions.FirstOrDefault(r => r.Key == parts[3]);
            if (region.Key == null) return Fail("region");
            // Zoho names the data centre the person actually signed in at; only Zoho's own servers are accepted
            var accounts = !string.IsNullOrEmpty(accountsServer) ? accountsServer.TrimEnd('/') : region.Accounts;
            if (!ZohoApi.IsAccountsServer(accounts)) return Fail("server");
            if (!await _db.Clients.AnyAsync(x => x.Id == clientId && x.IsActive, ct)) return Fail("company");
            if (!await IntegrationAccessService.IsApprovedAsync(_db, clientId, ct)) return Fail("not-approved");

            var c = await ConnectionAsync(clientId, ct);
            var app = await AppForAsync(c, ct);
            if (app.ClientId == null || app.Secret == null) return Fail("not-configured");
            ZohoTokens tokens;
            try { tokens = await _zoho.ExchangeCodeAsync(accounts, app.ClientId, app.Secret, await RedirectUriAsync(ct), code, ct); }
            catch (ZohoException ex)
            {
                _log.LogWarning("Zoho sign-in failed: {Message}", ex.Message);
                return Fail("zoho");
            }
            if (c == null) { c = new IntegrationConnection { ClientId = clientId, Provider = Provider, StartDate = DateTime.UtcNow.Date }; _db.IntegrationConnections.Add(c); }
            var problem = await FinishConnectAsync(c, clientId, region, accounts, tokens, parts[2], ct);
            if (problem != null) { _log.LogWarning("Zoho sign-in failed: {Problem}", problem); return Fail(problem.Contains("organisation") ? "no-organization" : "zoho"); }
            return new(true, appUrl + "?zoho=" + (c.Status == "Connected" ? "connected" : "choose"));
        }

        private static void UseOrganization(IntegrationConnection c, ZohoOrganization o)
        {
            if (c.OrganizationId != o.Id) c.SyncCursor = null;   // another organisation: start from the start date
            c.OrganizationId = o.Id; c.OrganizationName = Cut(o.Name, 200); c.OrganizationsJson = null; c.Status = "Connected";
            c.SyncRequestedAt = DateTime.UtcNow;
        }

        public async Task<ZohoConnectionDto> ChooseOrganizationAsync(Guid clientId, string organizationId, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct);
            var c = await ConnectionAsync(clientId, ct);
            if (c == null || c.Status != "ChooseOrganization" || c.OrganizationsJson == null) throw GatewayException.Conflict("ZOHO_NOTHING_TO_CHOOSE", "Sign in to Zoho first.");
            var orgs = JsonSerializer.Deserialize<List<ZohoOrganization>>(c.OrganizationsJson, Json) ?? new();
            var o = orgs.FirstOrDefault(x => x.Id == organizationId) ?? throw GatewayException.BadRequest("ORGANIZATION_INVALID", "Choose one of the organisations listed.");
            UseOrganization(c, o);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.organization", Entity = "IntegrationConnection", EntityId = c.Id.ToString(), Detail = o.Name });
            await _db.SaveChangesAsync(ct);
            return await ZohoAsync(clientId, ct);
        }

        public async Task<ZohoConnectionDto> SaveZohoSettingsAsync(Guid clientId, ZohoSettingsRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureApprovedAsync(_db, clientId, ct);
            var c = await ConnectionAsync(clientId, ct);
            if (c == null || c.Status == "Disconnected") throw GatewayException.Conflict("ZOHO_NOT_CONNECTED", "Connect Zoho Books first.");
            if (req.DeviceId is { } dev && !await _db.Devices.AnyAsync(d => d.Id == dev && d.ClientId == clientId && d.IsActive, ct)) throw GatewayException.NotFound("Device");
            if (req.AutoFiscalise && req.DeviceId == null) throw GatewayException.BadRequest("DEVICE_REQUIRED", "Choose the fiscal device the Zoho invoices are issued on.");
            var hs = string.IsNullOrWhiteSpace(req.DefaultHsCode) ? null : req.DefaultHsCode.Trim();
            if (hs != null && !Regex.IsMatch(hs, @"^\d{4,10}$")) throw GatewayException.BadRequest("HS_INVALID", "An HS code has 4 to 10 digits (ZIMRA uses 8).");
            var money = req.DefaultMoneyType?.Trim() ?? "Credit";
            var mt = MoneyTypes.FirstOrDefault(m => m.Equals(money, StringComparison.OrdinalIgnoreCase)) ?? throw GatewayException.BadRequest("PAYMENT_INVALID", "Choose how the sales are paid (Credit = on account).");
            if (req.StartDate is { } sd && sd.Date > DateTime.UtcNow.Date.AddDays(31)) throw GatewayException.BadRequest("START_INVALID", "The start date is too far ahead.");
            var startChanged = c.StartDate?.Date != req.StartDate?.Date;
            c.AutoFiscalise = req.AutoFiscalise; c.DeviceId = req.DeviceId; c.DefaultHsCode = hs; c.DefaultMoneyType = mt; c.PostBack = req.PostBack; c.StartDate = req.StartDate?.Date;
            if (startChanged) { c.SyncCursor = null; c.SyncRequestedAt = DateTime.UtcNow; }   // an earlier start date reaches back on the next sync
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.settings", Entity = "IntegrationConnection", EntityId = c.Id.ToString(),
                Detail = $"auto {(c.AutoFiscalise ? "on" : "off")}, device {c.DeviceId}, HS {hs}, {mt}, post back {(c.PostBack ? "on" : "off")}, from {c.StartDate:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return await ZohoAsync(clientId, ct);
        }

        public async Task DisconnectZohoAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            var c = await ConnectionAsync(clientId, ct) ?? throw GatewayException.NotFound("Connection");
            var refresh = Unprotect(c.RefreshTokenProtected);
            if (refresh != null && ZohoApi.IsAccountsServer(c.AccountsServer))
                try { await _zoho.RevokeAsync(c.AccountsServer, refresh, ct); } catch (Exception ex) when (ex is ZohoException or HttpRequestException or TaskCanceledException) { _log.LogInformation("Zoho token revoke failed: {Message}", ex.Message); }
            c.Status = "Disconnected"; c.RefreshTokenProtected = null; c.AccessTokenProtected = null; c.AccessTokenExpiresAt = null; c.AutoFiscalise = false; c.OrganizationsJson = null;
            c.WebhookKey = Guid.NewGuid().ToString("N");   // the old webhook address stops working
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.disconnect", Entity = "IntegrationConnection", EntityId = c.Id.ToString(), Detail = "Zoho Books" });
            await _db.SaveChangesAsync(ct);
        }

        // =============================================================================================================
        // sync
        // =============================================================================================================

        public async Task<ZohoSyncResultDto> SyncNowAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureActiveAsync(_db, clientId, ct);
            var c = await LiveConnectionAsync(clientId, ct);
            await using var _ = await _locks.AcquireAsync(c.Id, ct);
            await _db.Entry(c).ReloadAsync(ct);
            return await SyncAsync(c, ct);
        }

        private async Task<ZohoSyncResultDto> SyncAsync(IntegrationConnection c, CancellationToken ct)
        {
            int added = 0, updated = 0, fiscalised = 0, failed = 0, posted = 0;
            string? error = null;
            try
            {
                var a = await AccessAsync(c, ct);
                var start = c.StartDate?.Date;
                var since = c.SyncCursor?.AddMinutes(-2) ?? start;
                var newest = c.SyncCursor;
                foreach (var kind in new[] { "invoice", "creditnote" })
                {
                    for (var page = 1; page <= MaxPagesPerSync; page++)
                    {
                        Count(c);
                        var p = await _zoho.ListAsync(a, kind, since, page, ct);
                        var ids = p.Items.Select(i => i.Id).ToList();
                        var known = await _db.IntegrationDocuments.Where(d => d.ConnectionId == c.Id && d.Kind == kind && ids.Contains(d.ExternalId)).ToDictionaryAsync(d => d.ExternalId, ct);
                        foreach (var h in p.Items)
                        {
                            if (h.LastModified > newest) newest = h.LastModified;
                            var status = h.Status.ToLowerInvariant();
                            known.TryGetValue(h.Id, out var d);
                            if (d == null)
                            {
                                if (status is "draft" or "void") continue;                       // nothing to fiscalise (yet)
                                if (start != null && h.Date != null && h.Date.Value.Date < start) continue;   // before the start date
                                d = new IntegrationDocument { ClientId = c.ClientId, ConnectionId = c.Id, Kind = kind, ExternalId = h.Id };
                                _db.IntegrationDocuments.Add(d);
                                added++;
                            }
                            else if (d.Status == "Fiscalised")
                            {
                                // a fiscal record never changes; flag the difference so someone issues a credit note
                                if ((h.Total != d.Total || status == "void") && h.LastModified != d.ExternalModifiedAt) d.ChangedAfterFiscalised = true;
                                d.ExternalStatus = Cut(h.Status, 40); d.ExternalModifiedAt = h.LastModified;
                                updated++;
                                continue;
                            }
                            else
                            {
                                updated++;
                                if (status == "void") { d.Status = "Skipped"; d.Error = "Voided in Zoho Books."; }
                                else if (d.Status == "Failed" && h.LastModified != d.ExternalModifiedAt) { d.Status = "Pending"; d.Error = null; d.Attempts = 0; }   // edited after failing: try again
                            }
                            d.ExternalNumber = Cut(h.Number, 100); d.CustomerName = h.CustomerName == null ? null : Cut(h.CustomerName, 200); d.DocumentDate = h.Date;
                            d.Total = h.Total; d.Currency = Cut(h.Currency.ToUpperInvariant(), 10); d.ExternalStatus = Cut(h.Status, 40); d.ExternalModifiedAt = h.LastModified;
                        }
                        await _db.SaveChangesAsync(ct);
                        if (!p.HasMore) break;
                    }
                }
                c.SyncCursor = newest ?? DateTime.UtcNow;
                c.LastSyncAt = DateTime.UtcNow; c.SyncRequestedAt = null; c.LastError = null; c.LastErrorAt = null;
                await _db.SaveChangesAsync(ct);

                if (c.AutoFiscalise && c.DeviceId != null)
                {
                    // invoices before credit notes (a credit note needs its invoice fiscalised), oldest first
                    var due = await _db.IntegrationDocuments.Where(d => d.ConnectionId == c.Id && d.Status == "Pending")
                        .OrderBy(d => d.Kind == "creditnote").ThenBy(d => d.DocumentDate).ThenBy(d => d.CreatedAt).Take(MaxFiscalisePerSync).ToListAsync(ct);
                    foreach (var d in due)
                    {
                        if (await FiscaliseOneAsync(c, d, ct)) fiscalised++; else failed++;
                        if (d.PostedBackAt != null) posted++;
                    }
                }
            }
            catch (GatewayException ex) when (ex.Code == "ZOHO_RECONNECT") { error = ex.Message; }
            catch (ZohoException ex) { error = ex.Message; await FailAsync(c, ex, ct); }
            catch (HttpRequestException ex) { error = "Zoho could not be reached: " + ex.Message; await FailAsync(c, new Exception(error), ct); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { error = "Zoho did not answer in time."; await FailAsync(c, new Exception(error), ct); }
            return new ZohoSyncResultDto(added, updated, fiscalised, failed, posted, error);
        }

        // =============================================================================================================
        // fiscalising a Zoho document
        // =============================================================================================================

        /// <summary>Turns the Zoho document into lines the gateway fiscalises. Throws with a clear reason when it cannot be done exactly.</summary>
        public static (List<ReceiptLineRequest> Lines, string? Problem) MapLines(ZohoDocument z, string? defaultHs)
        {
            var lines = new List<ReceiptLineRequest>();
            static bool Exempt(string? taxName) => taxName != null && taxName.Contains("exempt", StringComparison.OrdinalIgnoreCase);
            foreach (var l in z.Lines)
            {
                if (l.Quantity <= 0 || l.ItemTotal < 0) return (lines, $"The line “{l.Name}” has a quantity or amount of zero or below. Remove it or fix it in Zoho Books.");
                if (l.ItemTotal == 0) continue;   // a free line carries nothing for ZIMRA
                var hs = l.HsCode ?? defaultHs;
                string name = Cut(l.Name, 200);
                decimal qty = l.Quantity, price;
                if (decimal.Round(l.Rate * l.Quantity, 2) == l.ItemTotal) price = l.Rate;
                else if (decimal.Round(l.ItemTotal / l.Quantity, 2) * l.Quantity == l.ItemTotal) price = decimal.Round(l.ItemTotal / l.Quantity, 2);
                else { name = Cut($"{l.Name} ({l.Quantity.ToString("0.####", CultureInfo.InvariantCulture)} x {l.Rate.ToString("0.00##", CultureInfo.InvariantCulture)} less discount)", 200); qty = 1; price = l.ItemTotal; }
                lines.Add(Exempt(l.TaxName) ? new ReceiptLineRequest(name, hs, qty, price, IsExempt: true) : new ReceiptLineRequest(name, hs, qty, price, TaxPercent: l.TaxPercent ?? 0));
            }
            if (lines.Count == 0) return (lines, "The document has no lines with an amount.");
            if (z.ShippingCharge > 0)
                lines.Add(new ReceiptLineRequest("Shipping", defaultHs, 1, z.ShippingCharge, TaxPercent: z.ShippingTaxPercent ?? 0));
            if (z.EntityDiscount > 0)
            {
                // a discount on the whole document is fiscalised as a discount line; it can only carry one tax rate
                var rates = z.Lines.Where(l => l.ItemTotal > 0).Select(l => Exempt(l.TaxName) ? -1m : l.TaxPercent ?? 0).Distinct().ToList();
                if (rates.Count != 1) return (lines, "The discount on the whole invoice covers lines with different VAT rates, which ZIMRA cannot split exactly. Give the discount per line in Zoho Books instead.");
                lines.Add(rates[0] < 0 ? new ReceiptLineRequest("Discount", defaultHs, 1, z.EntityDiscount, IsExempt: true, IsDiscount: true)
                                       : new ReceiptLineRequest("Discount", defaultHs, 1, z.EntityDiscount, TaxPercent: rates[0], IsDiscount: true));
            }
            if (z.Adjustment > 0) lines.Add(new ReceiptLineRequest("Adjustment", defaultHs, 1, z.Adjustment, TaxPercent: 0));
            else if (z.Adjustment < 0) lines.Add(new ReceiptLineRequest("Adjustment", defaultHs, 1, -z.Adjustment, TaxPercent: 0, IsDiscount: true));
            return (lines, null);
        }

        private static string Ref(IntegrationConnection c, IntegrationDocument d) => $"zoho:{c.OrganizationId}:{d.Kind}:{d.ExternalId}";

        private async Task<bool> FiscaliseOneAsync(IntegrationConnection c, IntegrationDocument d, CancellationToken ct)
        {
            ZohoDocument? z = null;
            try
            {
                if (c.DeviceId is not { } deviceId) throw GatewayException.BadRequest("DEVICE_REQUIRED", "Choose the fiscal device under the connection settings first.");
                var access = await _billing.GetAccessAsync(c.ClientId, ct);
                if (access.Locked) throw new GatewayException(402, "PAYMENT_REQUIRED", "Your subscription needs paying before more invoices are fiscalised.");
                var a = await AccessAsync(c, ct);
                Count(c);
                z = await _zoho.GetAsync(a, d.Kind, d.ExternalId, ct);
                var status = z.Status.ToLowerInvariant();
                if (status == "void") { d.Status = "Skipped"; d.Error = "Voided in Zoho Books."; await _db.SaveChangesAsync(ct); return false; }
                if (status == "draft") throw GatewayException.Conflict("ZOHO_DRAFT", "The document is still a draft in Zoho Books. Send or approve it there first.");
                if (string.IsNullOrWhiteSpace(z.Currency) || z.Currency.Length != 3) throw GatewayException.Unprocessable("CURRENCY_INVALID", "The document has no currency.");

                var (lines, problem) = MapLines(z, c.DefaultHsCode);
                if (problem != null) throw GatewayException.Unprocessable("ZOHO_UNSUPPORTED", problem);

                BuyerRequest? buyer = null;
                if (z.CustomerId != null)
                {
                    Count(c);
                    var contact = await _zoho.ContactAsync(a, z.CustomerId, ct);
                    var tin = contact?.Tin?.Replace(" ", "").Replace("-", "");
                    // ZIMRA wants a buyer only with a valid TIN; otherwise the sale is to an unnamed customer
                    if (contact != null && tin is { Length: 10 } && tin.All(char.IsDigit))
                        buyer = new BuyerRequest(Cut(contact.CompanyName ?? contact.Name, 200), Cut(contact.Name, 200), tin,
                            string.IsNullOrWhiteSpace(contact.VatNumber) ? null : contact.VatNumber.Trim(), contact.Email, contact.Phone);
                }

                Guid? original = null;
                var type = ReceiptType.FiscalInvoice;
                if (d.Kind == "creditnote")
                {
                    type = ReceiptType.CreditNote;
                    original = z.ReferenceInvoiceId == null ? null : await _db.IntegrationDocuments.AsNoTracking()
                        .Where(x => x.ConnectionId == c.Id && x.Kind == "invoice" && x.ExternalId == z.ReferenceInvoiceId && x.ReceiptId != null).Select(x => x.ReceiptId).FirstOrDefaultAsync(ct);
                    if (original == null) throw GatewayException.Conflict("ORIGINAL_NOT_FISCALISED", "The invoice this credit note is for has not been fiscalised through the gateway, so the credit note cannot reference it. Fiscalise the invoice first.");
                }

                var notes = z.Notes is { Length: > 0 } n && n.Trim().Length > 0 ? Cut(n.Trim(), 500) : null;
                // ZIMRA wants the reason on a credit note; Zoho often has none
                if (type == ReceiptType.CreditNote && notes == null)
                {
                    var invoiceNo = await _db.IntegrationDocuments.AsNoTracking().Where(x => x.ConnectionId == c.Id && x.Kind == "invoice" && x.ExternalId == z.ReferenceInvoiceId).Select(x => x.ExternalNumber).FirstOrDefaultAsync(ct);
                    notes = Cut($"Credit note {z.Number} against invoice {invoiceNo} (Zoho Books)", 500);
                }
                var req = new SubmitReceiptRequest(Ref(c, d), Cut(z.Number, 50), type, z.Currency.ToUpperInvariant(), z.TaxInclusive, lines, null, buyer, notes, original, "Zoho Books");
                var view = await _receipts.PreviewAsync(c.ClientId, deviceId, req, ct);
                if (Math.Abs(Math.Abs(view.Total) - Math.Abs(z.Total)) > 0.05m)
                    throw GatewayException.Unprocessable("TOTAL_MISMATCH", $"The fiscal total would be {Math.Abs(view.Total):0.00} but Zoho shows {z.Total:0.00} {z.Currency}. Check the VAT rates on the lines (and that prices include or exclude VAT as set in Zoho).");
                var money = Enum.TryParse<MoneyType>(c.DefaultMoneyType, true, out var m) ? m : MoneyType.Credit;
                req = req with { Payments = new[] { new ReceiptPaymentRequest(money, Math.Abs(view.Total)) } };

                var receipt = await _receipts.SubmitAsync(c.ClientId, deviceId, req, ct);
                if (receipt.Status == "Rejected") throw GatewayException.Unprocessable(receipt.ErrorCode ?? "RECEIPT_REJECTED", receipt.ErrorMessage ?? "ZIMRA rejected the document.");
                d.Status = "Fiscalised"; d.ReceiptId = receipt.Id; d.FiscalisedAt = DateTime.UtcNow; d.Error = null; d.Attempts++;
                d.Total = z.Total; d.ExternalNumber = Cut(z.Number, 100);
                _db.AuditLogs.Add(new AuditLog { ClientId = c.ClientId, Action = "integration.fiscalise", Entity = "Receipt", EntityId = receipt.Id.ToString(), Detail = $"Zoho {d.Kind} {z.Number}: {receipt.Total:0.00} {receipt.Currency}" });
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is GatewayException { Code: not "ZOHO_RECONNECT" } or FdmsException or ZohoException { Unauthorized: false } or DbUpdateException)
            {
                if (ex is ZohoException { RateLimited: true }) throw;   // stop the round; nothing is wrong with the document
                d.Status = "Failed"; d.Error = Cut(ex.Message, 1000); d.Attempts++;
                await _db.SaveChangesAsync(ct);
                await _notify.RaiseAsync(c.ClientId, "integration", "bad", $"zoho:failed:{d.Id}:{d.Attempts}", $"Zoho {(d.Kind == "creditnote" ? "credit note" : "invoice")} {d.ExternalNumber} was not fiscalised",
                    d.Error, "/integrations", ct, email: d.Attempts == 1);
                return false;
            }
            if (c.PostBack) await PostBackOneAsync(c, d, z, ct);
            return true;
        }

        /// <summary>Writes the fiscal details back to the Zoho document: a comment, custom fields (when the company created them) and the fiscal PDF. A failure is recorded, never fatal.</summary>
        private async Task PostBackOneAsync(IntegrationConnection c, IntegrationDocument d, ZohoDocument? z, CancellationToken ct)
        {
            if (d.ReceiptId is not { } receiptId) return;
            var problems = new List<string>();
            try
            {
                var a = await AccessAsync(c, ct);
                var r = await _receipts.GetAsync(c.ClientId, receiptId, ct);
                var zimra = await _db.Devices.AsNoTracking().Where(x => x.Id == r.DeviceId).Select(x => x.ZimraDeviceId).FirstOrDefaultAsync(ct);
                var fiscalNo = $"{zimra}/{r.ReceiptGlobalNo}";
                var code = VerificationCode(r.QrUrl);
                var title = r.Type == "CreditNote" ? "FISCAL TAX CREDIT NOTE" : "FISCAL TAX INVOICE";
                var text = $"{title} fiscalised with ZIMRA. Device {zimra}, fiscal day {r.FiscalDayNo}, receipt {r.ReceiptCounter} (global no {r.ReceiptGlobalNo})"
                           + (code != null ? $", verification code {code}" : "") + (r.QrUrl != null ? $". Verify: {r.QrUrl}" : ".")
                           + (r.Status == "Pending" ? " ZIMRA has not confirmed it yet; the gateway keeps sending it." : "");
                try { Count(c); await _zoho.CommentAsync(a, d.Kind, d.ExternalId, text, ct); }
                catch (ZohoException ex) when (!ex.Unauthorized && !ex.RateLimited) { problems.Add("comment: " + ex.Message); }

                // custom fields the company made in Zoho Books, matched by their label
                if (z == null) { Count(c); z = await _zoho.GetAsync(a, d.Kind, d.ExternalId, ct); }
                static string Key(string label) => Regex.Replace(label.ToLowerInvariant(), "[^a-z]", "");
                var values = new List<(string, string)>();
                foreach (var f in z.CustomFields)
                {
                    var k = Key(f.Label);
                    if (k is "fiscalnumber" or "fiscalno" or "zimrafiscalnumber") values.Add((f.Id, fiscalNo));
                    else if (k is "verificationcode" or "zimraverificationcode" && code != null) values.Add((f.Id, code));
                    else if (k is "zimraqr" or "zimraverification" or "verificationlink" or "fiscalqr" && r.QrUrl != null) values.Add((f.Id, r.QrUrl));
                }
                if (values.Count > 0)
                    try { Count(c); await _zoho.SetCustomFieldsAsync(a, d.Kind, d.ExternalId, values, ct); }
                    catch (ZohoException ex) when (!ex.Unauthorized && !ex.RateLimited) { problems.Add("fields: " + ex.Message); }

                if (d.Kind == "invoice")   // Zoho takes attachments on invoices
                    try
                    {
                        var pdf = await _receipts.GetPdfAsync(c.ClientId, receiptId, true, ct);
                        Count(c);
                        await _zoho.AttachAsync(a, d.Kind, d.ExternalId, pdf, $"fiscal-{Regex.Replace(d.ExternalNumber, "[^A-Za-z0-9_-]", "_")}.pdf", ct);
                    }
                    catch (ZohoException ex) when (!ex.Unauthorized && !ex.RateLimited) { problems.Add("PDF: " + ex.Message); }
            }
            catch (Exception ex) when (ex is GatewayException or ZohoException or FdmsException or HttpRequestException) { problems.Add(ex.Message); }
            if (problems.Count == 0) { d.PostedBackAt = DateTime.UtcNow; d.PostBackError = null; }
            else { d.PostBackError = Cut(string.Join("; ", problems), 1000); if (problems.Count < 3 && !problems.Any(p => p.StartsWith("comment"))) d.PostedBackAt = DateTime.UtcNow; }
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>ZIMRA's verification code: the last 16 characters of the QR address, as xxxx-xxxx-xxxx-xxxx.</summary>
        public static string? VerificationCode(string? qrUrl)
        {
            if (string.IsNullOrEmpty(qrUrl) || qrUrl.Length < 16) return null;
            var h = qrUrl[^16..];
            return Regex.IsMatch(h, "^[0-9A-Fa-f]{16}$") ? $"{h[..4]}-{h[4..8]}-{h[8..12]}-{h[12..]}".ToUpperInvariant() : null;
        }

        // =============================================================================================================
        // documents
        // =============================================================================================================

        private async Task<IntegrationDocumentDto> ToDtoAsync(IntegrationDocument d, IntegrationConnection? c, CancellationToken ct)
        {
            var page = await DtosAsync(new List<IntegrationDocument> { d }, c, ct);
            return page[0];
        }

        private async Task<List<IntegrationDocumentDto>> DtosAsync(List<IntegrationDocument> docs, IntegrationConnection? c, CancellationToken ct)
        {
            var ids = docs.Where(d => d.ReceiptId != null).Select(d => d.ReceiptId!.Value).ToList();
            var receipts = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.ReceiptGlobalNo, r.DeviceId }).ToListAsync(ct);
            var deviceIds = receipts.Select(r => r.DeviceId).Distinct().ToList();
            var zimra = await _db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ZimraDeviceId, ct);
            var web = c != null && ZohoApi.IsAccountsServer(c.AccountsServer) && c.OrganizationId != null ? ZohoApi.BooksWeb(c.AccountsServer) + $"/app/{c.OrganizationId}#/" : null;
            return docs.Select(d =>
            {
                var r = receipts.FirstOrDefault(x => x.Id == d.ReceiptId);
                return new IntegrationDocumentDto(d.Id, d.Kind, d.ExternalId, d.ExternalNumber, d.CustomerName, d.DocumentDate, d.Total, d.Currency, d.ExternalStatus, d.Status, d.ReceiptId,
                    r == null ? null : $"{(zimra.TryGetValue(r.DeviceId, out var z) ? z : 0)}/{r.ReceiptGlobalNo}", d.Error, d.Attempts, d.PostedBackAt, d.PostBackError, d.ChangedAfterFiscalised, d.CreatedAt, d.FiscalisedAt,
                    web == null ? null : web + (d.Kind == "creditnote" ? "creditnotes/" : "invoices/") + d.ExternalId);
            }).ToList();
        }

        public async Task<IntegrationDocumentPage> DocumentsAsync(Guid clientId, string? status, string? search, int page, int pageSize, CancellationToken ct)
        {
            RequireWholeCompany();
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 5, 200);
            var zoho = _db.IntegrationConnections.Where(x => x.ClientId == clientId && x.Provider == Provider).Select(x => x.Id);
            var q = _db.IntegrationDocuments.AsNoTracking().Where(d => d.ClientId == clientId && zoho.Contains(d.ConnectionId));
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
            var c = await _db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == Provider, ct);
            return new IntegrationDocumentPage(await DtosAsync(items, c, ct), total, page, pageSize);
        }

        private async Task<IntegrationDocument> DocumentAsync(Guid clientId, Guid id, CancellationToken ct) =>
            await _db.IntegrationDocuments.FirstOrDefaultAsync(d => d.Id == id && d.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");

        public async Task<IntegrationDocumentDto> FiscaliseAsync(Guid clientId, Guid documentId, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureActiveAsync(_db, clientId, ct);
            var c = await LiveConnectionAsync(clientId, ct);
            if (c.DeviceId == null) throw GatewayException.BadRequest("DEVICE_REQUIRED", "Choose the fiscal device under the connection settings first.");
            await using var _ = await _locks.AcquireAsync(c.Id, ct);
            await _db.Entry(c).ReloadAsync(ct);
            var d = await DocumentAsync(clientId, documentId, ct);
            await _db.Entry(d).ReloadAsync(ct);
            if (d.Status == "Fiscalised") throw GatewayException.Conflict("ALREADY_FISCALISED", "This document is fiscalised already. To change it, issue a credit note in Zoho Books.");
            if (d.ConnectionId != c.Id) throw GatewayException.Conflict("OTHER_CONNECTION", "This document came through an earlier connection.");
            await FiscaliseOneAsync(c, d, ct);
            return await ToDtoAsync(d, c, ct);
        }

        public async Task<IntegrationDocumentDto> SkipAsync(Guid clientId, Guid documentId, CancellationToken ct)
        {
            RequireWholeCompany();
            var d = await DocumentAsync(clientId, documentId, ct);
            if (d.Status == "Fiscalised") throw GatewayException.Conflict("ALREADY_FISCALISED", "A fiscalised document cannot be skipped.");
            d.Status = d.Status == "Skipped" ? "Pending" : "Skipped";   // skipping again brings it back
            if (d.Status == "Pending") { d.Error = null; d.Attempts = 0; }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = d.Status == "Skipped" ? "integration.skip" : "integration.unskip", Entity = "IntegrationDocument", EntityId = d.Id.ToString(), Detail = $"Zoho {d.Kind} {d.ExternalNumber}" });
            await _db.SaveChangesAsync(ct);
            return await ToDtoAsync(d, await _db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d.ConnectionId, ct), ct);
        }

        public async Task<IntegrationDocumentDto> PostBackAsync(Guid clientId, Guid documentId, CancellationToken ct)
        {
            RequireWholeCompany();
            await IntegrationAccessService.EnsureActiveAsync(_db, clientId, ct);
            var c = await LiveConnectionAsync(clientId, ct);
            var d = await DocumentAsync(clientId, documentId, ct);
            if (d.Status != "Fiscalised") throw GatewayException.Conflict("NOT_FISCALISED", "Fiscalise the document first.");
            await PostBackOneAsync(c, d, null, ct);
            return await ToDtoAsync(d, c, ct);
        }

        // =============================================================================================================
        // webhook and the background round
        // =============================================================================================================

        public async Task<bool> WebhookAsync(Guid connectionId, string key, CancellationToken ct)
        {
            var c = await _db.IntegrationConnections.FirstOrDefaultAsync(x => x.Id == connectionId, ct);
            if (c == null || c.Status != "Connected" || !await IntegrationAccessService.IsActiveAsync(_db, c.ClientId, ct) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.WebhookKey), Encoding.ASCII.GetBytes(key ?? ""))) return false;
            c.SyncRequestedAt ??= DateTime.UtcNow;
            c.BridgeWatchRequested = true;
            await _db.SaveChangesAsync(ct);
            return true;
        }

        public async Task<int> RunScheduledAsync(DateTime now, CancellationToken ct)
        {
            var every = TimeSpan.FromMinutes(Math.Clamp(_cfg.GetValue("Integrations:SyncMinutes", 5), 1, 1440));
            var active = _db.IntegrationAccess.Where(a => a.Status == "Active").Select(a => a.ClientId);
            var due = await _db.IntegrationConnections.AsNoTracking().Where(c => c.Status == "Connected" && c.OrganizationId != null && active.Contains(c.ClientId))
                .Where(c => c.SyncRequestedAt != null || c.LastSyncAt == null || c.LastSyncAt < now - every)
                .OrderBy(c => c.SyncRequestedAt == null).ThenBy(c => c.LastSyncAt).Select(c => c.Id).Take(50).ToListAsync(ct);
            var n = 0;
            foreach (var id in due)
            {
                await using var lk = await _locks.TryAcquireAsync(id, ct);
                if (lk == null) continue;   // a person is syncing it right now
                var c = await _db.IntegrationConnections.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (c == null || c.Status != "Connected") continue;
                // a company that has not paid is not fiscalised for, but its documents are still listed
                try { await SyncAsync(c, ct); n++; }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Zoho sync failed for connection {Id}", id); }
                _db.ChangeTracker.Clear();
            }
            return n;
        }

        // =============================================================================================================
        // the platform owner
        // =============================================================================================================

        public async Task<ZohoAppSettingsDto> ZohoAppAsync(CancellationToken ct)
        {
            var a = await _settings.AppAsync(ct);
            return new ZohoAppSettingsDto(a.ClientId, a.Secret != null, await RedirectUriAsync(ct), IsZohoClientId(a.ClientId) && a.Secret != null);
        }

        public async Task<ZohoAppSettingsDto> SaveZohoAppAsync(SaveZohoAppRequest req, CancellationToken ct)
        {
            var id = string.IsNullOrWhiteSpace(req.ClientId) ? null : req.ClientId.Trim();
            if (id != null && !IsZohoClientId(id)) throw GatewayException.BadRequest("CLIENT_ID_INVALID", "The client ID starts with \"1000.\" — copy it from Zoho's API console with nothing before or after it.");
            if (req.ClientSecret is { Length: > 200 }) throw GatewayException.BadRequest("SECRET_INVALID", "That client secret is too long.");
            await _settings.SaveAppAsync(id, string.IsNullOrWhiteSpace(req.ClientSecret) ? null : req.ClientSecret.Trim(), req.ClearSecret, _scope.UserName, ct);
            _db.AuditLogs.Add(new AuditLog { Action = "settings.zoho", Entity = "Setting", EntityId = IntegrationSettings.AppKey, Detail = $"client {id}{(req.ClientSecret is { Length: > 0 } ? ", secret changed" : req.ClearSecret ? ", secret removed" : "")}" });
            await _db.SaveChangesAsync(ct);
            return await ZohoAppAsync(ct);
        }

        public Task<IntegrationPricingDto> PricingAsync(CancellationToken ct) => _settings.PricingAsync(ct);

        public async Task<IntegrationPricingDto> SavePricingAsync(IntegrationPricingDto req, CancellationToken ct)
        {
            if (req.MonthlyFee < 0 || req.PricePerExtraDocument < 0 || req.IncludedDocuments < 0 || req.BridgeMonthlyFee < 0 || req.SetupFee < 0) throw GatewayException.BadRequest("PRICE_INVALID", "Prices and the included documents cannot be negative.");
            if (req.MonthlyFee > 100000 || req.BridgeMonthlyFee > 100000 || req.PricePerExtraDocument > 1000 || req.IncludedDocuments > 10_000_000) throw GatewayException.BadRequest("PRICE_INVALID", "That price is too high.");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!Regex.IsMatch(cur, "^[A-Z]{3}$")) throw GatewayException.BadRequest("CURRENCY_INVALID", "The currency is a 3-letter code such as USD.");
            var p = new IntegrationPricingDto(req.Charge, decimal.Round(req.MonthlyFee, 2), cur, req.IncludedDocuments, decimal.Round(req.PricePerExtraDocument, 4), decimal.Round(req.BridgeMonthlyFee, 2), decimal.Round(req.SetupFee, 2));
            await _settings.SavePricingAsync(p, _scope.UserName, ct);
            _db.AuditLogs.Add(new AuditLog { Action = "settings.integration-pricing", Entity = "Setting", EntityId = IntegrationSettings.PricingKey,
                Detail = $"{(p.Charge ? "on" : "off")}: {p.MonthlyFee} {p.Currency}/month, {p.IncludedDocuments} included, {p.PricePerExtraDocument} per extra, bridge {p.BridgeMonthlyFee}/month" });
            await _db.SaveChangesAsync(ct);
            return p;
        }

        public async Task<IntegrationMonitorDto> MonitorAsync(CancellationToken ct)
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var conns = await _db.IntegrationConnections.AsNoTracking().Where(c => c.Status != "Disconnected").OrderBy(c => c.Status).ThenBy(c => c.LastSyncAt).ToListAsync(ct);
            var clientIds = conns.Select(c => c.ClientId).ToList();
            var names = await _db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var stats = await _db.IntegrationDocuments.AsNoTracking().Where(d => clientIds.Contains(d.ClientId))
                .GroupBy(d => d.ConnectionId)
                .Select(g => new { g.Key, Month = g.Count(d => d.Status == "Fiscalised" && d.FiscalisedAt >= monthStart), Failed = g.Count(d => d.Status == "Failed"), Pending = g.Count(d => d.Status == "Pending") })
                .ToListAsync(ct);
            var today = DateTime.UtcNow.Date;
            var rows = conns.Select(c =>
            {
                var s = stats.FirstOrDefault(x => x.Key == c.Id);
                return new IntegrationMonitorRow(c.Id, c.ClientId, names.TryGetValue(c.ClientId, out var n) ? n : "?", c.Provider, c.Status, c.OrganizationName, c.LastTokenRefreshAt, c.AccessTokenExpiresAt,
                    c.LastSyncAt, c.LastError, c.ApiCallsDay?.Date == today ? c.ApiCallsToday : 0, s?.Month ?? 0, s?.Failed ?? 0, s?.Pending ?? 0, c.AutoFiscalise);
            }).ToList();
            // what this month's connections bring in at the current prices (billed with each company's subscription)
            var p = await _settings.PricingAsync(ct);
            var revenue = new List<AmountDto>();
            if (p.Charge)
            {
                var connected = rows.Where(r => r.Status is "Connected" or "NeedsReconnect").ToList();
                var amount = connected.Count * p.MonthlyFee + connected.Sum(r => Math.Max(0, r.FiscalisedThisMonth - p.IncludedDocuments)) * p.PricePerExtraDocument;
                revenue.Add(new AmountDto(p.Currency, decimal.Round(amount, 2)));
            }
            return new IntegrationMonitorDto(rows, rows.Count(r => r.Status == "Connected"), rows.Count(r => r.Status != "Connected" || r.Failed > 0 || r.LastError != null),
                rows.Sum(r => r.FiscalisedThisMonth), revenue);
        }
    }

    /// <summary>
    /// What the accounting connection adds to a subscription invoice: the monthly fee for each month of the period while a connection is live, and the
    /// documents fiscalised through it in the period before, above the included number. Only when charging is switched on and the currency matches.
    /// </summary>
    public sealed class IntegrationBilling : IIntegrationBilling
    {
        private readonly FdmsDbContext _db;
        private readonly IntegrationSettings _settings;
        public IntegrationBilling(FdmsDbContext db, IntegrationSettings settings) { _db = db; _settings = settings; }

        /// <summary>The company is on the Zoho connector plan (its price already covers the bridge).</summary>
        public static Task<bool> ConnectorPlanAsync(FdmsDbContext db, Guid clientId, CancellationToken ct) =>
            (from s in db.Subscriptions.AsNoTracking() join p in db.Plans.AsNoTracking() on s.PlanId equals p.Id where s.ClientId == clientId && p.Kind == "connector" select p.Id).AnyAsync(ct);

        public async Task<(decimal Amount, string Description)?> ChargeAsync(Guid clientId, DateTime periodStart, DateTime periodEnd, string currency, CancellationToken ct)
        {
            var zoho = await ZohoChargeAsync(clientId, periodStart, periodEnd, currency, ct);
            var (more, moreParts) = await Connectors.ConnectorBilling.ChargeAsync(_db, _settings, clientId, periodStart, periodEnd, currency, ct);
            var total = (zoho?.Amount ?? 0) + more;
            if (total <= 0) return null;
            var parts = new List<string>();
            if (zoho != null) parts.Add(zoho.Value.Description);
            parts.AddRange(moreParts);
            return (decimal.Round(total, 2), string.Join(", ", parts));
        }

        private async Task<(decimal Amount, string Description)?> ZohoChargeAsync(Guid clientId, DateTime periodStart, DateTime periodEnd, string currency, CancellationToken ct)
        {
            var p = await _settings.PricingAsync(ct);
            if (!p.Charge || !string.Equals(p.Currency, currency, StringComparison.OrdinalIgnoreCase)) return null;
            if (!await IntegrationAccessService.IsActiveAsync(_db, clientId, ct)) return null;
            var c = await _db.IntegrationConnections.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == IntegrationService.Provider, ct);
            var bridged = p.BridgeMonthlyFee > 0 && !await ConnectorPlanAsync(_db, clientId, ct)
                          && (await _db.BridgeGrants.AsNoTracking().AnyAsync(g => g.ClientId == clientId && g.IsActive, ct) || await _db.BridgeKeys.AsNoTracking().AnyAsync(k => k.ClientId == clientId && k.RevokedAt == null, ct));
            if (c == null && !bridged) return null;
            var months = Math.Max(1, (int)Math.Round((periodEnd - periodStart).TotalDays / 30.4));
            var live = c != null && c.Status != "Disconnected";
            var span = periodEnd - periodStart;
            var from = periodStart - span;
            var zohoIds = _db.IntegrationConnections.Where(x => x.ClientId == clientId && x.Provider == IntegrationService.Provider).Select(x => x.Id);
            var used = await _db.IntegrationDocuments.AsNoTracking().CountAsync(d => d.ClientId == clientId && zohoIds.Contains(d.ConnectionId) && d.Status == "Fiscalised" && d.FiscalisedAt >= from && d.FiscalisedAt < periodStart, ct);
            var fee = live ? p.MonthlyFee * months : 0;
            var extra = Math.Max(0, used - p.IncludedDocuments * months);
            var bridgeFee = bridged ? p.BridgeMonthlyFee * months : 0;
            var amount = decimal.Round(fee + extra * p.PricePerExtraDocument + bridgeFee, 2);
            if (amount <= 0) return null;
            var parts = new List<string>();
            if (fee > 0) parts.Add($"Zoho Books connection x{months} month(s)");
            if (extra > 0) parts.Add($"{extra} extra Zoho document(s)");
            if (bridgeFee > 0) parts.Add($"Zoho bridge x{months} month(s)");
            return (amount, string.Join(", ", parts));
        }
    }

    /// <summary>Every minute: syncs the connections that are due (every few minutes) or that Zoho's webhook asked for.</summary>
    public sealed class IntegrationSyncJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<IntegrationSyncJob> _log;
        public IntegrationSyncJob(IServiceScopeFactory scopes, ILogger<IntegrationSyncJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(3), stop); } catch (OperationCanceledException) { return; }
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<IIntegrationService>().RunScheduledAsync(DateTime.UtcNow, stop);
                    if (n > 0) _log.LogDebug("Synced {N} accounting connection(s)", n);
                    using (var connectorScope = _scopes.CreateScope())
                    {
                        var m = await connectorScope.ServiceProvider.GetRequiredService<IConnectorService>().RunScheduledAsync(DateTime.UtcNow, stop);
                        if (m > 0) _log.LogDebug("Synced {N} QuickBooks/Xero/Sage/Odoo connection(s)", m);
                    }
                    using var bridgeScope = _scopes.CreateScope();
                    await bridgeScope.ServiceProvider.GetRequiredService<IBridgeService>().RunAsync(DateTime.UtcNow, stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Accounting sync round failed"); }
                try { await Task.Delay(TimeSpan.FromSeconds(60), stop); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
