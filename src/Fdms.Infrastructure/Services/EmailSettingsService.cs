using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fdms.Infrastructure.Services
{
    /// <summary>The mail server set in Settings, kept in memory so the (singleton) email sender can use it without a database call per message.</summary>
    public sealed record StoredEmailSettings(bool Enabled, string? Host, int Port, bool EnableSsl, string? User, string? PasswordProtected, string? FromAddress, string? FromName, string? NotifyEmails);

    public sealed class EmailSettingsStore
    {
        public const string Key = "Email.Smtp";
        private const string Purpose = "Fdms.Email.v1";
        private readonly IDataProtector _protector;
        public EmailSettingsStore(IDataProtectionProvider dp) => _protector = dp.CreateProtector(Purpose);

        /// <summary>What Settings holds (null = nothing saved: the configuration file applies).</summary>
        public StoredEmailSettings? Current { get; private set; }
        public void Set(StoredEmailSettings? s) => Current = s;

        public string? Password => Current?.PasswordProtected is { Length: > 0 } p ? TryUnprotect(p) : null;
        public string Protect(string plain) => _protector.Protect(plain);
        private string? TryUnprotect(string p) { try { return _protector.Unprotect(p); } catch (System.Security.Cryptography.CryptographicException) { return null; } }

        public async Task LoadAsync(FdmsDbContext db, CancellationToken ct)
        {
            var json = await db.SystemSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).FirstOrDefaultAsync(ct);
            try { Current = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<StoredEmailSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { Current = null; }
        }
    }

    /// <summary>Loads the stored mail settings when the gateway starts.</summary>
    public sealed class EmailSettingsLoader : IHostedService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly EmailSettingsStore _store;
        public EmailSettingsLoader(IServiceScopeFactory scopes, EmailSettingsStore store) { _scopes = scopes; _store = store; }
        public async Task StartAsync(CancellationToken ct)
        {
            try { using var scope = _scopes.CreateScope(); await _store.LoadAsync(scope.ServiceProvider.GetRequiredService<FdmsDbContext>(), ct); }
            catch (Exception) { /* no database yet (first start): the configuration file applies */ }
        }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class EmailSettingsService : IEmailSettingsService
    {
        private static readonly Regex Email = new(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        private readonly EmailSettingsStore _store;
        private readonly IEmailSender _sender;
        private readonly IConfiguration _cfg;
        private readonly ICallerScope _scope;
        public EmailSettingsService(FdmsDbContext db, EmailSettingsStore store, IEmailSender sender, IConfiguration cfg, ICallerScope scope) { _db = db; _store = store; _sender = sender; _cfg = cfg; _scope = scope; }

        private string? Cfg(string email, string smtp) => _cfg["Email:" + email] is { Length: > 0 } v ? v : _cfg["SmtpClientSetting:" + smtp] is { Length: > 0 } w ? w : null;

        public async Task<EmailSettingsDto> GetAsync(CancellationToken ct)
        {
            await _store.LoadAsync(_db, ct);
            var s = _store.Current;
            if (s != null) return new EmailSettingsDto("database", s.Enabled, s.Host, s.Port, s.EnableSsl, s.User, !string.IsNullOrEmpty(s.PasswordProtected), s.FromAddress, s.FromName, s.NotifyEmails, _sender.Configured);
            var host = Cfg("Host", "Host");
            return new EmailSettingsDto(host == null ? "none" : "config", host != null, host, int.TryParse(Cfg("Port", "Port"), out var p) ? p : 587, !bool.TryParse(Cfg("EnableSsl", "EnableSsl"), out var e) || e,
                Cfg("User", "fromUsername"), Cfg("Password", "fromPassword") != null, Cfg("From", "FromAddress"), _cfg["SmtpClientSetting:FromName"], _cfg["Email:AdminNotify"], _sender.Configured);
        }

        private static List<string> SplitEmails(string? s) => (s ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public async Task<EmailSettingsDto> SaveAsync(SaveEmailSettingsRequest req, CancellationToken ct)
        {
            var host = req.Host?.Trim();
            if (req.Enabled && string.IsNullOrEmpty(host)) throw GatewayException.BadRequest("HOST_REQUIRED", "Enter the mail server (for example mail.yourcompany.com).");
            if (host is { Length: > 200 } || (host != null && !Regex.IsMatch(host, @"^[A-Za-z0-9.-]+$"))) throw GatewayException.BadRequest("HOST_INVALID", "The mail server is a host name such as mail.yourcompany.com.");
            if (req.Port is < 1 or > 65535) throw GatewayException.BadRequest("PORT_INVALID", "The port is usually 587 (STARTTLS) or 465 (SSL).");
            var from = req.FromAddress?.Trim();
            if (!string.IsNullOrEmpty(from) && !Email.IsMatch(from)) throw GatewayException.BadRequest("FROM_INVALID", "The sender address is not a valid email address.");
            var notify = SplitEmails(req.NotifyEmails);
            var bad = notify.FirstOrDefault(e => !Email.IsMatch(e));
            if (bad != null) throw GatewayException.BadRequest("NOTIFY_INVALID", $"\"{bad}\" is not a valid email address.");
            if (notify.Count > 10) throw GatewayException.BadRequest("NOTIFY_TOO_MANY", "Up to 10 addresses get the platform alerts.");

            await _store.LoadAsync(_db, ct);
            var old = _store.Current;
            var password = req.ClearPassword ? null : !string.IsNullOrEmpty(req.Password) ? _store.Protect(req.Password) : old?.PasswordProtected;
            var s = new StoredEmailSettings(req.Enabled, host, req.Port, req.EnableSsl, string.IsNullOrWhiteSpace(req.User) ? null : req.User.Trim(), password, string.IsNullOrEmpty(from) ? null : from,
                string.IsNullOrWhiteSpace(req.FromName) ? null : req.FromName.Trim(), notify.Count == 0 ? null : string.Join(", ", notify));
            var row = await _db.SystemSettings.FirstOrDefaultAsync(x => x.Key == EmailSettingsStore.Key, ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = EmailSettingsStore.Key });
            row.Value = JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web)); row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { Action = "settings.email", Entity = "Setting", EntityId = EmailSettingsStore.Key, Detail = $"{(req.Enabled ? "on" : "off")}, {host}:{req.Port}, from {from}, alerts to {notify.Count} address(es){(req.Password is { Length: > 0 } ? ", password changed" : "")}" });
            await _db.SaveChangesAsync(ct);
            _store.Set(s);
            return await GetAsync(ct);
        }

        public async Task<TestEmailResult> TestAsync(string to, CancellationToken ct)
        {
            if (!Email.IsMatch(to?.Trim() ?? "")) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter the address to send the test to.");
            await _store.LoadAsync(_db, ct);
            var problem = await _sender.TrySendAsync(to!.Trim(), "FDMS Gateway test email", "<p>This is a test from FDMS Gateway. Email is working.</p>", Array.Empty<EmailAttachment>(), ct);
            return problem == null ? new TestEmailResult(true, $"Sent to {to.Trim()}. Check the inbox (and the spam folder).") : new TestEmailResult(false, problem);
        }

        public async Task<IReadOnlyList<string>> OperatorEmailsAsync(CancellationToken ct)
        {
            if (_store.Current == null) await _store.LoadAsync(_db, ct);
            var set = SplitEmails(_store.Current?.NotifyEmails);
            if (set.Count == 0) set = SplitEmails(_cfg["Email:AdminNotify"]);
            if (set.Count == 0)
                set = await (from u in _db.Users.AsNoTracking() join ur in _db.UserRoles on u.Id equals ur.UserId join r in _db.Roles on ur.RoleId equals r.Id
                             where u.ClientId == null && u.Status == "ACTIVE" && r.Name == "SuperAdmin" && u.Email != null select u.Email!).Distinct().ToListAsync(ct);
            return set;
        }
    }
}
