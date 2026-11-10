using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.WhatsApp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Messaging
{
    /// <summary>Which SMS account sends for a company, and its secrets (encrypted at rest).</summary>
    public sealed class SmsAccountStore
    {
        private readonly FdmsDbContext _db;
        private readonly IDataProtector _p;
        public SmsAccountStore(FdmsDbContext db, IDataProtectionProvider dp) { _db = db; _p = dp.CreateProtector("Fdms.Sms.v1"); }

        public string Protect(string plain) => _p.Protect(plain);
        public string? Unprotect(string? v) { if (string.IsNullOrEmpty(v)) return null; try { return _p.Unprotect(v); } catch (CryptographicException) { return null; } }

        public Task<SmsAccount?> PlatformAsync(CancellationToken ct) => _db.SmsAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == null, ct);
        public Task<SmsAccount?> CompanyAsync(Guid clientId, CancellationToken ct) => _db.SmsAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == clientId, ct);

        /// <summary>The company's own account when it is on; otherwise the platform's, when it is on and lets companies use it. Null = SMS cannot be sent.</summary>
        public static async Task<SmsAccount?> ResolveAsync(FdmsDbContext db, Guid? clientId, CancellationToken ct)
        {
            if (clientId is { } cid)
            {
                var own = await db.SmsAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == cid && a.Enabled, ct);
                if (own != null) return own;
            }
            var platform = await db.SmsAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == null && a.Enabled, ct);
            return platform != null && (clientId == null || platform.AllowCompanies) ? platform : null;
        }

        public SmsConfig Config(SmsAccount a) => new(a.Id, a.Provider, a.ApiUrl, a.Username, Unprotect(a.ApiKeyProtected), Unprotect(a.ApiSecretProtected), a.SenderId);

        /// <summary>The fields the provider needs are all there (secrets included).</summary>
        public static bool Complete(SmsAccount a)
        {
            var p = SmsProviders.Find(a.Provider);
            if (p == null) return false;
            foreach (var f in p.Fields.Where(f => !f.EndsWith('?')))
                if (f switch { "apiKey" => a.ApiKeyProtected, "apiSecret" => a.ApiSecretProtected, "username" => a.Username, "senderId" => a.SenderId, "apiUrl" => a.ApiUrl, _ => "x" } is null or "") return false;
            return true;
        }
    }

    /// <summary>Puts alerts in the SMS queue for the people who switched SMS alerts on and have a phone number on their profile.</summary>
    public static class SmsAlerts
    {
        /// <summary>At most this many alert SMS per person in 24 hours, so a burst of alerts cannot run up a bill.</summary>
        public const int PerPersonPerDay = 10;

        public static async Task QueueAsync(FdmsDbContext db, Guid? clientId, string title, string? body, CancellationToken ct)
        {
            var account = await SmsAccountStore.ResolveAsync(db, clientId, ct);
            if (account == null) return;
            List<(string Id, string? Phone)> people;
            if (clientId is { } cid)
                people = (await db.Users.AsNoTracking().Where(u => u.ClientId == cid && u.Status == "ACTIVE" && u.SmsAlerts && u.PhoneNumber != null)
                    .Select(u => new { u.Id, u.PhoneNumber }).ToListAsync(ct)).Select(u => (u.Id, u.PhoneNumber)).ToList();
            else
                people = (await (from u in db.Users.AsNoTracking() join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id
                                 where u.ClientId == null && u.Status == "ACTIVE" && r.Name == "SuperAdmin" && u.SmsAlerts && u.PhoneNumber != null
                                 select new { u.Id, u.PhoneNumber }).Distinct().ToListAsync(ct)).Select(u => (u.Id, u.PhoneNumber)).ToList();
            if (people.Count == 0) return;
            var text = SmsText.Fit("FiscalHub: " + title + (string.IsNullOrWhiteSpace(body) ? "" : ". " + body));
            var since = DateTime.UtcNow.AddDays(-1);
            foreach (var (id, raw) in people)
            {
                var phone = WhatsAppService.NormalizePhone(raw);
                if (phone == null) continue;
                if (await db.SmsMessages.CountAsync(m => m.UserId == id && m.Kind == "alert" && m.CreatedAt > since, ct) >= PerPersonPerDay) continue;
                db.SmsMessages.Add(new SmsMessage { ClientId = clientId, AccountId = account.Id, UserId = id, To = phone, Body = text, Kind = "alert", Status = "Pending" });
            }
            await db.SaveChangesAsync(ct);
        }
    }

    public static class SmsText
    {
        /// <summary>Two SMS parts at most (306 characters of plain text).</summary>
        public static string Fit(string s) { s = Regex.Replace(s.Trim(), @"\s+", " "); return s.Length <= 306 ? s : s[..303] + "..."; }
    }

    public sealed class SmsDispatcher : ISmsDispatcher
    {
        private readonly FdmsDbContext _db;
        private readonly SmsAccountStore _store;
        private readonly ISmsApi _api;
        private readonly ILogger<SmsDispatcher> _log;
        public SmsDispatcher(FdmsDbContext db, SmsAccountStore store, ISmsApi api, ILogger<SmsDispatcher> log) { _db = db; _store = store; _api = api; _log = log; }

        public async Task<bool> AvailableAsync(Guid? clientId, CancellationToken ct) => await SmsAccountStore.ResolveAsync(_db, clientId, ct) != null;

        public async Task<SmsSendResult?> SendNowAsync(Guid? clientId, string phone, string text, string kind, string? userId, string? logBody, CancellationToken ct)
        {
            try
            {
                var account = await SmsAccountStore.ResolveAsync(_db, clientId, ct);
                var to = WhatsAppService.NormalizePhone(phone);
                if (account == null || to == null) return null;
                return await SendWithAsync(account, clientId, to, text, kind, userId, logBody, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "SMS ({Kind}) could not be sent", kind);
                return SmsSendResult.Fail("The SMS could not be sent.");
            }
        }

        internal async Task<SmsSendResult> SendWithAsync(SmsAccount account, Guid? clientId, string to, string text, string kind, string? userId, string? logBody, CancellationToken ct)
        {
            text = SmsText.Fit(text);
            var r = await _api.SendAsync(_store.Config(account), to, text, ct);
            _db.SmsMessages.Add(new SmsMessage { ClientId = clientId, AccountId = account.Id, UserId = userId, To = to, Body = logBody ?? text, Kind = kind, Attempts = 1,
                Status = r.Ok ? "Sent" : "Failed", ProviderMessageId = r.MessageId is { Length: > 200 } id ? id[..200] : r.MessageId, Error = Cut(r.Error, 500), SentAt = r.Ok ? DateTime.UtcNow : null });
            await _db.SaveChangesAsync(ct);
            return r;
        }

        private static string? Cut(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];
    }

    public sealed class SmsService : ISmsService
    {
        private static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2) };
        private readonly FdmsDbContext _db;
        private readonly SmsAccountStore _store;
        private readonly SmsDispatcher _send;
        private readonly ISmsApi _api;
        private readonly IReceiptService _receipts;
        private readonly IInvoiceShareService _share;
        private readonly ICallerScope _scope;

        public SmsService(FdmsDbContext db, SmsAccountStore store, SmsDispatcher send, ISmsApi api, IReceiptService receipts, IInvoiceShareService share, ICallerScope scope)
        { _db = db; _store = store; _send = send; _api = api; _receipts = receipts; _share = share; _scope = scope; }

        // =============================================================================================================
        // settings
        // =============================================================================================================

        private async Task<SmsSettingsDto> DtoAsync(SmsAccount? a, Guid? clientId, CancellationToken ct)
        {
            var platform = await _store.PlatformAsync(ct);
            var platformAvailable = platform is { Enabled: true } && (clientId == null || platform.AllowCompanies) && SmsAccountStore.Complete(platform);
            return a == null
                ? new SmsSettingsDto(false, SmsProviders.Nhume, null, null, false, false, null, true, false, SmsProviders.All, platformAvailable)
                : new SmsSettingsDto(a.Enabled, a.Provider, a.ApiUrl, a.Username, a.ApiKeyProtected != null, a.ApiSecretProtected != null, a.SenderId, a.AllowCompanies,
                    a.Enabled && SmsAccountStore.Complete(a), SmsProviders.All, platformAvailable);
        }

        public async Task<SmsSettingsDto> PlatformAsync(CancellationToken ct) => await DtoAsync(await _store.PlatformAsync(ct), null, ct);
        public async Task<SmsSettingsDto> CompanyAsync(Guid clientId, CancellationToken ct) => await DtoAsync(await _store.CompanyAsync(clientId, ct), clientId, ct);
        public Task<SmsSettingsDto> SavePlatformAsync(SaveSmsSettingsRequest req, CancellationToken ct) => SaveAsync(null, req, ct);
        public Task<SmsSettingsDto> SaveCompanyAsync(Guid clientId, SaveSmsSettingsRequest req, CancellationToken ct) => SaveAsync(clientId, req, ct);

        private async Task<SmsSettingsDto> SaveAsync(Guid? clientId, SaveSmsSettingsRequest req, CancellationToken ct)
        {
            var provider = SmsProviders.Find(req.Provider) ?? throw GatewayException.BadRequest("PROVIDER_INVALID", "Choose one of the SMS providers on the list.");
            static string? Clean(string? v, int max) { v = v?.Trim(); return string.IsNullOrEmpty(v) ? null : v.Length > max ? throw GatewayException.BadRequest("TOO_LONG", "A value is too long.") : v; }
            var url = Clean(req.ApiUrl, 300);
            if (url != null && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)))
                throw GatewayException.BadRequest("URL_INVALID", "The provider address must be a full https:// address.");
            var sender = Clean(req.SenderId, 20);
            if (sender != null && !Regex.IsMatch(sender, @"^(\+?\d{3,15}|[A-Za-z0-9 ._-]{1,11})$"))
                throw GatewayException.BadRequest("SENDER_INVALID", "A sender ID is up to 11 letters or digits, or a phone number.");

            var a = await _db.SmsAccounts.FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            if (a == null) _db.SmsAccounts.Add(a = new SmsAccount { ClientId = clientId });
            var changedProvider = !string.Equals(a.Provider, provider.Key, StringComparison.Ordinal);
            a.Provider = provider.Key; a.ApiUrl = url; a.Username = Clean(req.Username, 200); a.SenderId = sender;
            // secrets are write-only: empty keeps what is saved, unless the provider changed (an old provider's key means nothing to a new one)
            if (!string.IsNullOrWhiteSpace(req.ApiKey)) a.ApiKeyProtected = _store.Protect(req.ApiKey.Trim()); else if (changedProvider) a.ApiKeyProtected = null;
            if (!string.IsNullOrWhiteSpace(req.ApiSecret)) a.ApiSecretProtected = _store.Protect(req.ApiSecret.Trim()); else if (changedProvider) a.ApiSecretProtected = null;
            a.AllowCompanies = clientId == null ? req.AllowCompanies : true;
            a.Enabled = req.Enabled;
            if (a.Enabled && !SmsAccountStore.Complete(a))
                throw GatewayException.BadRequest("SMS_INCOMPLETE", $"To switch SMS on, fill in everything {provider.Label} needs: {string.Join(", ", provider.Fields.Where(f => !f.EndsWith('?')).Select(FieldLabel))}.");
            a.UpdatedAt = DateTime.UtcNow; a.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "settings.sms", Entity = "SmsAccount", EntityId = a.Id.ToString(),
                Detail = $"{provider.Label}, {(a.Enabled ? "on" : "off")}{(req.ApiKey is { Length: > 0 } || req.ApiSecret is { Length: > 0 } ? ", credentials changed" : "")}" });
            await _db.SaveChangesAsync(ct);
            return await DtoAsync(a, clientId, ct);
        }

        private static string FieldLabel(string f) => f switch { "apiKey" => "API key", "apiSecret" => "secret / password", "username" => "user name / account ID", "senderId" => "sender ID", "apiUrl" => "API address", _ => f };

        public async Task<SmsResultDto> TestAsync(Guid? clientId, string to, CancellationToken ct)
        {
            var phone = WhatsAppService.NormalizePhone(to) ?? throw GatewayException.BadRequest("NUMBER_INVALID", "Enter the phone number with its country code, e.g. +263 77 123 4567.");
            var a = clientId is { } cid ? await _store.CompanyAsync(cid, ct) : await _store.PlatformAsync(ct);
            if (a == null || !a.Enabled || !SmsAccountStore.Complete(a)) throw GatewayException.Conflict("SMS_OFF", "Switch SMS on and save the settings first.");
            var r = await _send.SendWithAsync(a, clientId, phone, "FiscalHub test: SMS is connected.", "test", _scope.UserId, null, ct);
            return new SmsResultDto(r.Ok, true, "+" + phone, r.Ok ? $"Sent to +{phone}. Check that phone." : "The provider refused it: " + r.Error);
        }

        public async Task<IReadOnlyList<SmsLogRow>> LogAsync(Guid? clientId, int take, CancellationToken ct) =>
            (await _db.SmsMessages.AsNoTracking().Where(m => m.ClientId == clientId).OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct))
                .Select(m => new SmsLogRow(m.Id, "+" + m.To, m.Kind, m.Status, m.Body, m.Error, m.Attempts, m.CreatedAt, m.SentAt)).ToList();

        // =============================================================================================================
        // an invoice to the customer
        // =============================================================================================================

        public async Task<SmsResultDto> SendInvoiceAsync(Guid clientId, Guid receiptId, SendInvoiceSmsRequest req, CancellationToken ct)
        {
            var r = await _receipts.GetAsync(clientId, receiptId, ct);
            if (r.Status is "Held" or "Cancelled") throw GatewayException.Conflict("NOT_ISSUED", "This invoice has not been issued, so there is nothing to send yet.");
            var raw = req.Phone?.Trim();
            if (string.IsNullOrEmpty(raw)) { try { raw = (await _receipts.GetDetailAsync(clientId, receiptId, ct)).Buyer?.BuyerContacts?.PhoneNo; } catch (GatewayException) { } }
            var phone = WhatsAppService.NormalizePhone(raw) ?? throw GatewayException.BadRequest("PHONE_REQUIRED", "Enter the customer's phone number with its country code, e.g. +263 77 123 4567.");
            var account = await SmsAccountStore.ResolveAsync(_db, clientId, ct);
            if (account == null) return new SmsResultDto(false, false, "+" + phone, "SMS is not set up. Add your SMS provider under Integration → Messaging, or share the link instead.");

            var link = await _share.CreateLinkAsync(clientId, receiptId, ct);
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstAsync(ct);
            var title = r.Type == "CreditNote" ? "FISCAL TAX CREDIT NOTE" : r.Type == "DebitNote" ? "FISCAL TAX DEBIT NOTE" : "FISCAL TAX INVOICE";
            var note = string.IsNullOrWhiteSpace(req.Message) ? "" : req.Message.Trim() + " ";
            var text = $"{note}{company}: {title} {r.InvoiceNo}, {r.Total:0.00} {r.Currency}. View and verify: {link.Url}";
            var result = await _send.SendWithAsync(account, clientId, phone, text, "invoice", _scope.UserId, null, ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = result.Ok ? "receipt.sms" : "receipt.sms_failed", Entity = "Receipt", EntityId = receiptId.ToString(),
                Detail = $"{r.InvoiceNo} to +{phone}{(result.Ok ? "" : ": " + result.Error)}" });
            await _db.SaveChangesAsync(ct);
            return new SmsResultDto(result.Ok, true, "+" + phone, result.Ok ? $"Sent to +{phone}." : "The SMS provider refused it: " + result.Error);
        }

        // =============================================================================================================
        // a person's alerts
        // =============================================================================================================

        public async Task<SmsPrefsDto> MyPrefsAsync(string userId, CancellationToken ct)
        {
            var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct) ?? throw GatewayException.NotFound("User");
            return new SmsPrefsDto(await _send.AvailableAsync(u.ClientId, ct), u.PhoneNumber, u.SmsAlerts);
        }

        public async Task<SmsPrefsDto> SavePrefsAsync(string userId, SaveSmsPrefsRequest req, CancellationToken ct)
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct) ?? throw GatewayException.NotFound("User");
            if (req.Alerts && WhatsAppService.NormalizePhone(u.PhoneNumber) == null)
                throw GatewayException.BadRequest("PHONE_REQUIRED", "Add your phone number (with its country code) to your profile first.");
            u.SmsAlerts = req.Alerts;
            await _db.SaveChangesAsync(ct);
            return new SmsPrefsDto(await _send.AvailableAsync(u.ClientId, ct), u.PhoneNumber, u.SmsAlerts);
        }

        // =============================================================================================================
        // the worker's round
        // =============================================================================================================

        public async Task<int> ProcessPendingAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var due = await _db.SmsMessages.Where(m => m.Status == "Pending" && m.NextAttemptAt <= now).OrderBy(m => m.CreatedAt).Take(50).ToListAsync(ct);
            var accounts = new Dictionary<Guid, SmsAccount?>();
            foreach (var m in due)
            {
                if (m.AccountId is not { } aid) { m.Status = "Failed"; m.Error = "No SMS account."; continue; }
                if (!accounts.TryGetValue(aid, out var a)) accounts[aid] = a = await _db.SmsAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == aid && x.Enabled, ct);
                if (a == null) { m.Status = "Failed"; m.Error = "The SMS account was switched off."; continue; }
                m.Attempts++;
                var r = await _api.SendAsync(_store.Config(a), m.To, m.Body, ct);
                if (r.Ok) { m.Status = "Sent"; m.SentAt = DateTime.UtcNow; m.ProviderMessageId = r.MessageId is { Length: > 200 } id ? id[..200] : r.MessageId; m.Error = null; }
                else
                {
                    m.Error = r.Error is { Length: > 500 } e ? e[..500] : r.Error;
                    if (!r.Temporary || m.Attempts > Backoff.Length) m.Status = "Failed"; else m.NextAttemptAt = DateTime.UtcNow + Backoff[m.Attempts - 1];
                }
            }
            if (due.Count > 0) await _db.SaveChangesAsync(ct);

            // the log keeps 90 days
            var old = await _db.SmsMessages.Where(m => m.CreatedAt < now.AddDays(-90)).OrderBy(m => m.CreatedAt).Take(500).ToListAsync(ct);
            if (old.Count > 0) { _db.SmsMessages.RemoveRange(old); await _db.SaveChangesAsync(ct); }
            return due.Count;
        }
    }

    /// <summary>Sends queued SMS alerts every half minute.</summary>
    public sealed class SmsWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<SmsWorker> _log;
        public SmsWorker(IServiceScopeFactory scopes, ILogger<SmsWorker> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try { using var scope = _scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<ISmsService>().ProcessPendingAsync(stop); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "SMS round failed"); }
                try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
