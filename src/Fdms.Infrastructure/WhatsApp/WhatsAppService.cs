using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Billing;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.WhatsApp
{
    /// <summary>The platform's WhatsApp Cloud API settings, kept in Settings (token and app secret encrypted).</summary>
    public sealed class WhatsAppSettingsStore
    {
        public const string Key = "WhatsApp.Cloud";
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private readonly FdmsDbContext _db;
        private readonly IDataProtector _p;
        public WhatsAppSettingsStore(FdmsDbContext db, IDataProtectionProvider dp) { _db = db; _p = dp.CreateProtector("Fdms.WhatsApp.v1"); }

        public sealed record Stored(bool Enabled, string? PhoneNumberId, string? BusinessAccountId, string? DisplayNumber, string? AccessTokenProtected, string? AppSecretProtected,
            string VerifyToken, string? AlertTemplate, string AlertLanguage, string ApiVersion);

        public static Stored Empty() => new(false, null, null, null, null, null, NewVerifyToken(), null, "en", "v21.0");
        public static string NewVerifyToken() => "fh_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        public async Task<Stored> LoadAsync(CancellationToken ct)
        {
            var json = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).FirstOrDefaultAsync(ct);
            try { return (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Stored>(json, J)) ?? Empty(); } catch (JsonException) { return Empty(); }
        }

        public async Task SaveAsync(Stored s, string? by, CancellationToken ct)
        {
            var row = await _db.SystemSettings.FirstOrDefaultAsync(x => x.Key == Key, ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = Key });
            row.Value = JsonSerializer.Serialize(s, J); row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = by;
        }

        public string Protect(string plain) => _p.Protect(plain);
        public string? Unprotect(string? p) { if (string.IsNullOrEmpty(p)) return null; try { return _p.Unprotect(p); } catch (CryptographicException) { return null; } }

        /// <summary>What sending needs, or null when WhatsApp is off or not set up.</summary>
        public WhatsAppConfig? Config(Stored s)
        {
            var token = Unprotect(s.AccessTokenProtected);
            return s.Enabled && !string.IsNullOrWhiteSpace(s.PhoneNumberId) && token != null ? new WhatsAppConfig(s.PhoneNumberId!, token, s.ApiVersion) : null;
        }

        /// <summary>A company number's sending details, or null when it is off or not set up.</summary>
        public WhatsAppConfig? Config(WhatsAppAccount? a)
        {
            if (a is not { Enabled: true } || string.IsNullOrWhiteSpace(a.PhoneNumberId)) return null;
            var token = Unprotect(a.AccessTokenProtected);
            return token == null ? null : new WhatsAppConfig(a.PhoneNumberId!, token, a.ApiVersion);
        }
    }

    /// <summary>Puts alerts in the WhatsApp queue for the people who linked their number and want alerts there.</summary>
    public static class WhatsAppAlerts
    {
        public static async Task QueueAsync(FdmsDbContext db, Guid? clientId, string title, string? body, CancellationToken ct)
        {
            // a company with its own number sends its alerts from it; otherwise the platform's number does, when it is on
            Guid? accountId = null;
            if (clientId is { } owner)
                accountId = await db.WhatsAppAccounts.AsNoTracking().Where(a => a.ClientId == owner && a.Enabled && a.PhoneNumberId != null && a.AccessTokenProtected != null).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
            if (accountId == null)
            {
                var on = await db.SystemSettings.AsNoTracking().Where(s => s.Key == WhatsAppSettingsStore.Key).Select(s => s.Value).FirstOrDefaultAsync(ct);
                if (on == null || !on.Contains("\"enabled\":true")) return;
            }
            List<(string Id, string Phone, Guid? Client)> to;
            if (clientId is { } cid)
                to = (await db.Users.AsNoTracking().Where(u => u.ClientId == cid && u.Status == "ACTIVE" && u.WhatsAppNumber != null && u.WhatsAppAlerts)
                    .Select(u => new { u.Id, u.WhatsAppNumber, u.ClientId }).ToListAsync(ct)).Select(u => (u.Id, u.WhatsAppNumber!, u.ClientId)).ToList();
            else   // the platform's own alerts go to the platform administrators
                to = (await (from u in db.Users.AsNoTracking() join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id
                             where u.ClientId == null && u.Status == "ACTIVE" && r.Name == "SuperAdmin" && u.WhatsAppNumber != null && u.WhatsAppAlerts
                             select new { u.Id, u.WhatsAppNumber }).Distinct().ToListAsync(ct)).Select(u => (u.Id, u.WhatsAppNumber!, (Guid?)null)).ToList();
            if (to.Count == 0) return;
            var text = string.IsNullOrWhiteSpace(body) ? title : title + "\n" + body;
            foreach (var (id, phone, client) in to)
                db.WhatsAppMessages.Add(new WhatsAppMessage { Direction = "out", Kind = "alert", Phone = phone, UserId = id, ClientId = client, AccountId = accountId, Body = text.Length > 2000 ? text[..2000] : text, Status = "Pending" });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Wakes the WhatsApp worker as soon as a message arrives.</summary>
    public sealed class WhatsAppSignal
    {
        private readonly SemaphoreSlim _s = new(0, int.MaxValue);
        public void Poke() { if (_s.CurrentCount == 0) _s.Release(); }
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _s.WaitAsync(timeout, ct);
    }

    public sealed class WhatsAppService : IWhatsAppService
    {
        private static readonly Regex Digits = new(@"\D", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        private readonly WhatsAppSettingsStore _store;
        private readonly IWhatsAppApi _api;
        private readonly WhatsAppBot _bot;
        private readonly WhatsAppSignal _signal;
        private readonly PublicUrls _urls;
        private readonly ICallerScope _scope;
        private readonly ILogger<WhatsAppService> _log;

        public WhatsAppService(FdmsDbContext db, WhatsAppSettingsStore store, IWhatsAppApi api, WhatsAppBot bot, WhatsAppSignal signal, PublicUrls urls, ICallerScope scope, ILogger<WhatsAppService> log)
        { _db = db; _store = store; _api = api; _bot = bot; _signal = signal; _urls = urls; _scope = scope; _log = log; }

        /// <summary>Digits only with the country code; a Zimbabwe number written 07... becomes 2637....</summary>
        public static string? NormalizePhone(string? raw)
        {
            var d = Digits.Replace(raw ?? "", "");
            if (d.StartsWith("00")) d = d[2..];
            if (d.StartsWith("0") && d.Length == 10) d = "263" + d[1..];
            return d.Length is >= 8 and <= 15 ? d : null;
        }

        private static string Hash(string userId, string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId + ":" + code))).ToLowerInvariant();

        // =============================================================================================================
        // platform settings
        // =============================================================================================================

        private async Task<WhatsAppSettingsDto> ToDtoAsync(WhatsAppSettingsStore.Stored s, CancellationToken ct) =>
            new(s.Enabled, s.PhoneNumberId, s.BusinessAccountId, s.DisplayNumber, s.AccessTokenProtected != null, s.AppSecretProtected != null, s.VerifyToken,
                (await _urls.ApiAsync(ct)).TrimEnd('/') + "/api/v1/whatsapp/webhook", s.AlertTemplate, s.AlertLanguage, s.ApiVersion, _store.Config(s) != null && s.AppSecretProtected != null);

        public async Task<WhatsAppSettingsDto> SettingsAsync(CancellationToken ct)
        {
            var s = await _store.LoadAsync(ct);
            if (!await _db.SystemSettings.AnyAsync(x => x.Key == WhatsAppSettingsStore.Key, ct)) { await _store.SaveAsync(s, _scope.UserName, ct); await _db.SaveChangesAsync(ct); }   // keeps the verify token stable
            return await ToDtoAsync(s, ct);
        }

        public async Task<WhatsAppSettingsDto> SaveSettingsAsync(SaveWhatsAppSettingsRequest req, CancellationToken ct)
        {
            var old = await _store.LoadAsync(ct);
            static string? Clean(string? v, int max) { v = v?.Trim(); return string.IsNullOrEmpty(v) ? null : v.Length > max ? throw GatewayException.BadRequest("TOO_LONG", "A value is too long.") : v; }
            var phoneId = Clean(req.PhoneNumberId, 40);
            if (phoneId != null && !Regex.IsMatch(phoneId, @"^\d{6,30}$")) throw GatewayException.BadRequest("PHONE_ID_INVALID", "The phone number ID is the long number Meta shows under WhatsApp → API Setup (digits only), not the phone number itself.");
            var display = req.DisplayNumber == null ? old.DisplayNumber : NormalizePhone(req.DisplayNumber);
            if (!string.IsNullOrWhiteSpace(req.DisplayNumber) && display == null) throw GatewayException.BadRequest("NUMBER_INVALID", "Enter the WhatsApp number with its country code, e.g. +263 77 123 4567.");
            var template = Clean(req.AlertTemplate, 100);
            if (template != null && !Regex.IsMatch(template, "^[a-z0-9_]+$")) throw GatewayException.BadRequest("TEMPLATE_INVALID", "Template names use lower-case letters, digits and underscores, e.g. fiscalhub_alert.");
            var lang = Clean(req.AlertTemplateLanguage, 10) ?? old.AlertLanguage;
            var version = Clean(req.ApiVersion, 10) ?? old.ApiVersion;
            if (!Regex.IsMatch(version, @"^v\d{2}\.\d$")) throw GatewayException.BadRequest("VERSION_INVALID", "The Graph API version looks like v21.0.");
            var s = old with
            {
                Enabled = req.Enabled, PhoneNumberId = phoneId, BusinessAccountId = Clean(req.BusinessAccountId, 40), DisplayNumber = display,
                AccessTokenProtected = string.IsNullOrWhiteSpace(req.AccessToken) ? old.AccessTokenProtected : _store.Protect(req.AccessToken.Trim()),
                AppSecretProtected = string.IsNullOrWhiteSpace(req.AppSecret) ? old.AppSecretProtected : _store.Protect(req.AppSecret.Trim()),
                VerifyToken = req.RotateVerifyToken ? WhatsAppSettingsStore.NewVerifyToken() : old.VerifyToken, AlertTemplate = template, AlertLanguage = lang, ApiVersion = version,
            };
            if (s.Enabled && (s.PhoneNumberId == null || s.AccessTokenProtected == null || s.AppSecretProtected == null || s.DisplayNumber == null))
                throw GatewayException.BadRequest("WHATSAPP_INCOMPLETE", "To switch WhatsApp on, fill in the phone number ID, the WhatsApp number, the access token and the app secret.");
            await _store.SaveAsync(s, _scope.UserName, ct);
            _db.AuditLogs.Add(new AuditLog { Action = "settings.whatsapp", Entity = "Setting", EntityId = WhatsAppSettingsStore.Key,
                Detail = $"{(s.Enabled ? "on" : "off")}, number {s.DisplayNumber}{(req.AccessToken is { Length: > 0 } ? ", token changed" : "")}{(req.AppSecret is { Length: > 0 } ? ", secret changed" : "")}" });
            await _db.SaveChangesAsync(ct);
            return await ToDtoAsync(s, ct);
        }

        public async Task<string> TestAsync(string to, CancellationToken ct)
        {
            var phone = NormalizePhone(to) ?? throw GatewayException.BadRequest("NUMBER_INVALID", "Enter the number with its country code.");
            var s = await _store.LoadAsync(ct);
            var cfg = _store.Config(s) ?? throw GatewayException.Conflict("WHATSAPP_OFF", "Switch WhatsApp on and save the settings first.");
            try
            {
                // a first message to someone must be an approved template; Meta's sample "hello_world" exists on every new account
                if (s.AlertTemplate != null) await _api.SendTemplateAsync(cfg, phone, s.AlertTemplate, s.AlertLanguage, new[] { "FiscalHub test", "WhatsApp is connected." }, ct);
                else await _api.SendTemplateAsync(cfg, phone, "hello_world", "en_US", Array.Empty<string>(), ct);
                return $"Sent to +{phone}. Check that phone's WhatsApp.";
            }
            catch (WhatsAppException ex) { throw GatewayException.BadRequest("WHATSAPP_REFUSED", ex.Message); }
        }

        // =============================================================================================================
        // a person's link
        // =============================================================================================================

        /// <summary>The number a person's bot lives on: their company's own number when it has one that is on, otherwise the platform's (null when neither is set up).</summary>
        private async Task<string?> BotNumberAsync(Guid? clientId, CancellationToken ct)
        {
            if (clientId is { } cid)
            {
                var own = await _db.WhatsAppAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == cid, ct);
                if (_store.Config(own) != null && own!.DisplayNumber != null) return own.DisplayNumber;
            }
            var s = await _store.LoadAsync(ct);
            return _store.Config(s) != null ? s.DisplayNumber : null;
        }

        private async Task<WhatsAppLinkDto> LinkDtoAsync(ApplicationUser u, string? code, CancellationToken ct)
        {
            var number = await BotNumberAsync(u.ClientId, ct);
            return new WhatsAppLinkDto(number != null, number, u.WhatsAppNumber, u.WhatsAppLinkedAt, u.WhatsAppAlerts, u.WhatsAppDeviceId,
                code, code == null ? null : u.WhatsAppLinkExpiresAt, code == null || number == null ? null : $"https://wa.me/{number}?text={Uri.EscapeDataString("LINK " + code)}");
        }

        private async Task<ApplicationUser> UserAsync(string userId, CancellationToken ct) =>
            await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw GatewayException.NotFound("User");

        public async Task<WhatsAppLinkDto> MyLinkAsync(string userId, CancellationToken ct) => await LinkDtoAsync(await UserAsync(userId, ct), null, ct);

        public async Task<WhatsAppLinkDto> StartLinkAsync(string userId, CancellationToken ct)
        {
            var u = await UserAsync(userId, ct);
            if (await BotNumberAsync(u.ClientId, ct) == null) throw GatewayException.Conflict("WHATSAPP_OFF", "WhatsApp is not set up on this gateway yet.");
            var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            u.WhatsAppLinkCodeHash = Hash(u.Id, code); u.WhatsAppLinkExpiresAt = DateTime.UtcNow.AddMinutes(10);
            await _db.SaveChangesAsync(ct);
            return await LinkDtoAsync(u, code, ct);
        }

        public async Task<WhatsAppLinkDto> SavePrefsAsync(string userId, SaveWhatsAppPrefsRequest req, CancellationToken ct)
        {
            var u = await UserAsync(userId, ct);
            if (req.DeviceId is { } d && !await _db.Devices.AnyAsync(x => x.Id == d && x.ClientId == u.ClientId && x.IsActive, ct)) throw GatewayException.NotFound("Device");
            u.WhatsAppAlerts = req.Alerts; u.WhatsAppDeviceId = req.DeviceId;
            await _db.SaveChangesAsync(ct);
            return await LinkDtoAsync(u, null, ct);
        }

        public async Task<WhatsAppLinkDto> UnlinkAsync(string userId, CancellationToken ct)
        {
            var u = await UserAsync(userId, ct);
            if (u.WhatsAppNumber != null)
            {
                _db.WhatsAppSessions.RemoveRange(await _db.WhatsAppSessions.Where(x => x.Phone == u.WhatsAppNumber && (x.Line == "" || x.ClientId == u.ClientId)).ToListAsync(ct));
                _db.AuditLogs.Add(new AuditLog { ClientId = u.ClientId, Action = "whatsapp.unlink", Entity = "User", EntityId = u.Id, Detail = $"+{u.WhatsAppNumber}" });
            }
            u.WhatsAppNumber = null; u.WhatsAppLinkedAt = null; u.WhatsAppLinkCodeHash = null; u.WhatsAppLinkExpiresAt = null;
            await _db.SaveChangesAsync(ct);
            return await LinkDtoAsync(u, null, ct);
        }

        /// <summary>A "LINK 123456" message from a number: finds whose code it is. Null when no code matches.</summary>
        internal async Task<ApplicationUser?> CompleteLinkAsync(string phone, string code, Guid? onlyClientId, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            // on a company's own number only that company's staff can link
            var candidates = await _db.Users.Where(u => u.WhatsAppLinkExpiresAt > now && u.WhatsAppLinkCodeHash != null && u.Status == "ACTIVE" && (onlyClientId == null || u.ClientId == onlyClientId)).ToListAsync(ct);
            var u = candidates.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x.WhatsAppLinkCodeHash!), Encoding.ASCII.GetBytes(Hash(x.Id, code))));
            if (u == null) return null;
            // one number belongs to one person: whoever had it before loses it
            foreach (var other in await _db.Users.Where(x => x.WhatsAppNumber == phone && x.Id != u.Id).ToListAsync(ct)) { other.WhatsAppNumber = null; other.WhatsAppLinkedAt = null; }
            u.WhatsAppNumber = phone; u.WhatsAppLinkedAt = now; u.WhatsAppLinkCodeHash = null; u.WhatsAppLinkExpiresAt = null;
            _db.AuditLogs.Add(new AuditLog { ClientId = u.ClientId, Action = "whatsapp.link", Entity = "User", EntityId = u.Id, UserId = u.Id, UserName = u.Email, Detail = $"+{phone}" });
            await _db.SaveChangesAsync(ct);
            return u;
        }

        // =============================================================================================================
        // the webhook
        // =============================================================================================================

        public async Task<string?> VerifyWebhookAsync(string? mode, string? token, string? challenge, CancellationToken ct)
        {
            var s = await _store.LoadAsync(ct);
            return mode == "subscribe" && token != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(s.VerifyToken)) ? challenge : null;
        }

        private static bool SignatureOk(string? secret, string rawBody, string? signature)
        {
            if (secret == null || string.IsNullOrEmpty(signature) || !signature.StartsWith("sha256=", StringComparison.Ordinal)) return false;
            var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToLowerInvariant()));
        }

        public async Task<bool> ReceiveAsync(string rawBody, string? signature, CancellationToken ct)
        {
            var s = await _store.LoadAsync(ct);
            if (!SignatureOk(_store.Unprotect(s.AppSecretProtected), rawBody, signature)) return false;
            if (!s.Enabled) return true;   // genuine, but WhatsApp is switched off: nothing is answered
            await StoreAsync(rawBody, s.PhoneNumberId, null, ct);
            return true;
        }

        public async Task<string?> VerifyAccountWebhookAsync(Guid accountId, string? mode, string? token, string? challenge, CancellationToken ct)
        {
            var a = await _db.WhatsAppAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, ct);
            return a != null && mode == "subscribe" && token != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(a.VerifyToken)) ? challenge : null;
        }

        public async Task<bool> ReceiveAccountAsync(Guid accountId, string rawBody, string? signature, CancellationToken ct)
        {
            var a = await _db.WhatsAppAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (a == null || !SignatureOk(_store.Unprotect(a.AppSecretProtected), rawBody, signature)) return false;
            if (!a.Enabled) return true;
            await StoreAsync(rawBody, a.PhoneNumberId, a.Id, ct);
            return true;
        }

        /// <summary>Keeps the messages sent to <paramref name="phoneNumberId"/> (other numbers on the same Meta app are ignored); the worker answers them.</summary>
        private async Task StoreAsync(string rawBody, string? phoneNumberId, Guid? accountId, CancellationToken ct)
        {
            JsonElement root;
            try { root = JsonDocument.Parse(rawBody).RootElement; } catch (JsonException) { return; }
            var added = 0;
            foreach (var entry in Arr(root, "entry"))
                foreach (var change in Arr(entry, "changes"))
                {
                    if (!change.TryGetProperty("value", out var value)) continue;
                    var phoneId = value.TryGetProperty("metadata", out var md) && md.TryGetProperty("phone_number_id", out var pid) ? pid.GetString() : null;
                    if (phoneId == null || phoneId != phoneNumberId) continue;
                    foreach (var m in Arr(value, "messages"))
                    {
                        var waId = Str(m, "id");
                        var from = NormalizePhone(Str(m, "from"));
                        if (waId == null || from == null || await _db.WhatsAppMessages.AnyAsync(x => x.WaMessageId == waId, ct)) continue;
                        var type = Str(m, "type");
                        string? text = null, reply = null, kind = "text";
                        if (type == "text" && m.TryGetProperty("text", out var t)) text = Str(t, "body");
                        else if (type == "interactive" && m.TryGetProperty("interactive", out var i))
                        {
                            var r = i.TryGetProperty("button_reply", out var br) ? br : i.TryGetProperty("list_reply", out var lr) ? lr : default;
                            if (r.ValueKind == JsonValueKind.Object) { reply = Str(r, "id"); text = Str(r, "title"); kind = "reply"; }
                        }
                        else if (type == "button" && m.TryGetProperty("button", out var b)) { text = Str(b, "text"); reply = Str(b, "payload"); kind = "reply"; }
                        else kind = "other";
                        _db.WhatsAppMessages.Add(new WhatsAppMessage { Direction = "in", Phone = from, WaMessageId = waId, Kind = kind, AccountId = accountId,
                            Body = text is { Length: > 2000 } ? text[..2000] : text, ReplyId = reply is { Length: > 200 } ? reply[..200] : reply });
                        added++;
                    }
                }
            if (added > 0)
            {
                try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { /* Meta sent the same message twice at once */ }
                _signal.Poke();
            }
        }

        private static IEnumerable<JsonElement> Arr(JsonElement e, string name) => e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
        private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // =============================================================================================================
        // a company's own number
        // =============================================================================================================

        private async Task<CompanyWhatsAppDto> CompanyDtoAsync(Guid clientId, WhatsAppAccount? a, CancellationToken ct)
        {
            var platform = await _store.LoadAsync(ct);
            var platformOn = _store.Config(platform) != null && platform.DisplayNumber != null;
            var api = (await _urls.ApiAsync(ct)).TrimEnd('/');
            if (a == null)
                return new CompanyWhatsAppDto(false, null, null, null, false, false, "", "", null, "en", "v21.0", true, null, false, platformOn, platformOn ? platform.DisplayNumber : null);
            return new CompanyWhatsAppDto(a.Enabled, a.PhoneNumberId, a.BusinessAccountId, a.DisplayNumber, a.AccessTokenProtected != null, a.AppSecretProtected != null, a.VerifyToken,
                $"{api}/api/v1/whatsapp/webhook/{a.Id:N}", a.AlertTemplate, a.AlertLanguage, a.ApiVersion, a.CustomersEnabled, a.CustomerGreeting,
                _store.Config(a) != null && a.AppSecretProtected != null && a.DisplayNumber != null, platformOn, platformOn ? platform.DisplayNumber : null);
        }

        public async Task<CompanyWhatsAppDto> CompanyAsync(Guid clientId, CancellationToken ct)
        {
            var a = await _db.WhatsAppAccounts.FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            if (a == null)
            {
                // created on first look, so the webhook address and verify token stay the same while the company fills in Meta
                _db.WhatsAppAccounts.Add(a = new WhatsAppAccount { ClientId = clientId, VerifyToken = WhatsAppSettingsStore.NewVerifyToken(), UpdatedBy = _scope.UserName });
                await _db.SaveChangesAsync(ct);
            }
            return await CompanyDtoAsync(clientId, a, ct);
        }

        public async Task<CompanyWhatsAppDto> SaveCompanyAsync(Guid clientId, SaveCompanyWhatsAppRequest req, CancellationToken ct)
        {
            static string? Clean(string? v, int max) { v = v?.Trim(); return string.IsNullOrEmpty(v) ? null : v.Length > max ? throw GatewayException.BadRequest("TOO_LONG", "A value is too long.") : v; }
            var a = await _db.WhatsAppAccounts.FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            if (a == null) _db.WhatsAppAccounts.Add(a = new WhatsAppAccount { ClientId = clientId, VerifyToken = WhatsAppSettingsStore.NewVerifyToken() });
            var phoneId = Clean(req.PhoneNumberId, 40);
            if (phoneId != null && !Regex.IsMatch(phoneId, @"^\d{6,30}$")) throw GatewayException.BadRequest("PHONE_ID_INVALID", "The phone number ID is the long number Meta shows under WhatsApp → API Setup (digits only), not the phone number itself.");
            if (phoneId != null)
            {
                var platform = await _store.LoadAsync(ct);
                if (phoneId == platform.PhoneNumberId || await _db.WhatsAppAccounts.AnyAsync(x => x.PhoneNumberId == phoneId && x.ClientId != clientId, ct))
                    throw GatewayException.Conflict("NUMBER_TAKEN", "This WhatsApp number is already connected to FiscalHub elsewhere.");
            }
            var display = req.DisplayNumber == null ? a.DisplayNumber : NormalizePhone(req.DisplayNumber);
            if (!string.IsNullOrWhiteSpace(req.DisplayNumber) && display == null) throw GatewayException.BadRequest("NUMBER_INVALID", "Enter the WhatsApp number with its country code, e.g. +263 77 123 4567.");
            var template = Clean(req.AlertTemplate, 100);
            if (template != null && !Regex.IsMatch(template, "^[a-z0-9_]+$")) throw GatewayException.BadRequest("TEMPLATE_INVALID", "Template names use lower-case letters, digits and underscores, e.g. fiscalhub_alert.");
            var version = Clean(req.ApiVersion, 10) ?? a.ApiVersion;
            if (!Regex.IsMatch(version, @"^v\d{2}\.\d$")) throw GatewayException.BadRequest("VERSION_INVALID", "The Graph API version looks like v21.0.");

            a.Enabled = req.Enabled; a.PhoneNumberId = phoneId; a.BusinessAccountId = Clean(req.BusinessAccountId, 40); a.DisplayNumber = display;
            if (!string.IsNullOrWhiteSpace(req.AccessToken)) a.AccessTokenProtected = _store.Protect(req.AccessToken.Trim());
            if (!string.IsNullOrWhiteSpace(req.AppSecret)) a.AppSecretProtected = _store.Protect(req.AppSecret.Trim());
            if (req.RotateVerifyToken) a.VerifyToken = WhatsAppSettingsStore.NewVerifyToken();
            a.AlertTemplate = template; a.AlertLanguage = Clean(req.AlertTemplateLanguage, 10) ?? a.AlertLanguage; a.ApiVersion = version;
            a.CustomersEnabled = req.CustomersEnabled; a.CustomerGreeting = Clean(req.CustomerGreeting, 300);
            if (a.Enabled && (a.PhoneNumberId == null || a.AccessTokenProtected == null || a.AppSecretProtected == null || a.DisplayNumber == null))
                throw GatewayException.BadRequest("WHATSAPP_INCOMPLETE", "To switch your number on, fill in the phone number ID, the WhatsApp number, the access token and the app secret.");
            a.UpdatedAt = DateTime.UtcNow; a.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "settings.whatsapp", Entity = "WhatsAppAccount", EntityId = a.Id.ToString(),
                Detail = $"{(a.Enabled ? "on" : "off")}, number {a.DisplayNumber}, customers {(a.CustomersEnabled ? "on" : "off")}{(req.AccessToken is { Length: > 0 } ? ", token changed" : "")}{(req.AppSecret is { Length: > 0 } ? ", secret changed" : "")}" });
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { throw GatewayException.Conflict("NUMBER_TAKEN", "This WhatsApp number is already connected to FiscalHub elsewhere."); }
            return await CompanyDtoAsync(clientId, a, ct);
        }

        public async Task<CompanyWhatsAppDto> RemoveCompanyAsync(Guid clientId, CancellationToken ct)
        {
            var a = await _db.WhatsAppAccounts.FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            if (a != null)
            {
                var line = a.Id.ToString("N");
                _db.WhatsAppSessions.RemoveRange(await _db.WhatsAppSessions.Where(x => x.Line == line).ToListAsync(ct));
                _db.WhatsAppAccounts.Remove(a);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "settings.whatsapp_removed", Entity = "WhatsAppAccount", EntityId = a.Id.ToString(), Detail = $"number {a.DisplayNumber} removed" });
                await _db.SaveChangesAsync(ct);
            }
            return await CompanyDtoAsync(clientId, null, ct);
        }

        public async Task<string> TestCompanyAsync(Guid clientId, string to, CancellationToken ct)
        {
            var phone = NormalizePhone(to) ?? throw GatewayException.BadRequest("NUMBER_INVALID", "Enter the number with its country code.");
            var a = await _db.WhatsAppAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            var cfg = _store.Config(a) ?? throw GatewayException.Conflict("WHATSAPP_OFF", "Switch your number on and save the settings first.");
            try
            {
                if (a!.AlertTemplate != null) await _api.SendTemplateAsync(cfg, phone, a.AlertTemplate, a.AlertLanguage, new[] { "FiscalHub test", "Your WhatsApp number is connected." }, ct);
                else await _api.SendTemplateAsync(cfg, phone, "hello_world", "en_US", Array.Empty<string>(), ct);
                return $"Sent to +{phone}. Check that phone's WhatsApp.";
            }
            catch (WhatsAppException ex) { throw GatewayException.BadRequest("WHATSAPP_REFUSED", ex.Message); }
        }

        // =============================================================================================================
        // the worker's round
        // =============================================================================================================

        private static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6) };

        /// <summary>The number a message belongs to: its sending details and, for a company number, the account. Cached for one round.</summary>
        private sealed record Line(WhatsAppConfig? Cfg, WhatsAppAccount? Account, string? Template, string Language);

        public async Task<int> ProcessPendingAsync(CancellationToken ct)
        {
            var s = await _store.LoadAsync(ct);
            var platform = new Line(_store.Config(s), null, s.AlertTemplate, s.AlertLanguage);
            var lines = new Dictionary<Guid, Line>();
            async Task<Line> LineAsync(Guid? accountId)
            {
                if (accountId is not { } id) return platform;
                if (lines.TryGetValue(id, out var l)) return l;
                var a = await _db.WhatsAppAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                return lines[id] = new Line(_store.Config(a), a, a?.AlertTemplate, a?.AlertLanguage ?? "en");
            }

            var n = 0;
            var inbound = await _db.WhatsAppMessages.Where(m => m.Direction == "in" && m.Status == "Received").OrderBy(m => m.CreatedAt).Select(m => new { m.Id, m.AccountId }).Take(50).ToListAsync(ct);
            foreach (var item in inbound)
            {
                var line = await LineAsync(item.AccountId);
                var m = await _db.WhatsAppMessages.FirstAsync(x => x.Id == item.Id, ct);
                if (line.Cfg == null) { m.Status = "Ignored"; m.Error = "The number is switched off."; }
                else
                {
                    try { await _bot.HandleAsync(this, line.Cfg, m, line.Account, ct); m.Status = m.Status == "Received" ? "Processed" : m.Status; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, "WhatsApp message {Id} could not be answered", item.Id);
                        m.Status = "Failed"; m.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    }
                }
                m.ProcessedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
                n++;
            }

            var now = DateTime.UtcNow;
            var outbound = await _db.WhatsAppMessages.Where(m => m.Direction == "out" && m.Status == "Pending" && m.NextAttemptAt <= now).OrderBy(m => m.CreatedAt).Take(50).ToListAsync(ct);
            foreach (var m in outbound)
            {
                var line = await LineAsync(m.AccountId);
                if (line.Cfg == null) { m.Status = "Failed"; m.Error = "The WhatsApp number is switched off."; continue; }
                m.Attempts++;
                try
                {
                    var key = m.AccountId?.ToString("N") ?? "";
                    var session = await _db.WhatsAppSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Line == key && x.Phone == m.Phone, ct);
                    var open = session?.LastInboundAt > now.AddHours(-23.5);
                    // inside the 24-hour window anything may be written; outside it only an approved template
                    if (open) await _api.SendTextAsync(line.Cfg, m.Phone, "🔔 " + m.Body, ct);
                    else if (line.Template != null)
                    {
                        var parts = (m.Body ?? "").Split('\n', 2);
                        await _api.SendTemplateAsync(line.Cfg, m.Phone, line.Template, line.Language, new[] { parts[0], parts.Length > 1 ? parts[1] : " " }, ct);
                    }
                    else { m.Status = "Failed"; m.Error = "Outside WhatsApp's 24-hour window and no alert template is set."; continue; }
                    m.Status = "Sent"; m.ProcessedAt = DateTime.UtcNow; m.Error = null;
                }
                catch (WhatsAppException ex)
                {
                    m.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    if (m.Attempts > Backoff.Length) m.Status = "Failed"; else m.NextAttemptAt = DateTime.UtcNow + Backoff[m.Attempts - 1];
                }
                n++;
            }
            if (outbound.Count > 0) await _db.SaveChangesAsync(ct);
            // the log keeps 90 days
            var old = await _db.WhatsAppMessages.Where(m => m.CreatedAt < now.AddDays(-90)).OrderBy(m => m.CreatedAt).Take(500).ToListAsync(ct);
            if (old.Count > 0) { _db.WhatsAppMessages.RemoveRange(old); await _db.SaveChangesAsync(ct); }
            return n;
        }
    }

    /// <summary>Answers WhatsApp messages within seconds of their arrival and sends queued alerts.</summary>
    public sealed class WhatsAppWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly WhatsAppSignal _signal;
        private readonly ILogger<WhatsAppWorker> _log;
        public WhatsAppWorker(IServiceScopeFactory scopes, WhatsAppSignal signal, ILogger<WhatsAppWorker> log) { _scopes = scopes; _signal = signal; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IWhatsAppService>().ProcessPendingAsync(stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "WhatsApp round failed"); }
                try { await _signal.WaitAsync(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
