using System.Net;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Billing;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    // =================================================================================================================
    /// <summary>
    /// The bell and the alert emails. An alert is raised ONCE per condition (its key), so a certificate that stays close to expiry does not email every five minutes. Each company chooses
    /// which kinds it wants and where they are emailed; with no address given they go to the company administrators.
    /// </summary>
    public sealed class NotificationService : INotificationService
    {
        public static readonly IReadOnlyList<AlertKindInfo> Kinds = new AlertKindInfo[]
        {
            new("certificate", "Device certificate expiring", "A device certificate expires within 30 days, or already has."),
            new("dayclose", "Fiscal day could not close", "ZIMRA refused to close a fiscal day, or the automatic close at the end of the day failed."),
            new("daylong", "Fiscal day open too long", "A day is close to the maximum length ZIMRA allows."),
            new("rejected", "Receipt rejected by ZIMRA", "ZIMRA refused a receipt. It needs correcting."),
            new("queued", "Receipts stuck waiting for ZIMRA", "Receipts have been queued for more than an hour."),
            new("usage", "Plan limit nearly reached", "You have used 80% or more of what your plan allows."),
            new("recurring", "Recurring invoice failed", "A scheduled invoice could not be issued."),
            new("billing", "Invoices and payments", "A subscription invoice is ready to pay, a payment was received, or a payment was not accepted. Always on."),
            new("approval", "Approvals", "Something waits for approval, or a request you made was approved or rejected."),
            new("email", "Email could not be delivered", "An email (an invoice, a quote, an alert) was refused by the mail server again and again and has been given up on."),
            new("integration", "Accounting connection", "Zoho Books needs signing in again, or a document from it could not be fiscalised."),
        };

        private static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        private readonly IEmailSender _email;
        private readonly PublicUrls _urls;
        private readonly IUsageService _usage;
        private readonly ILogger<NotificationService> _log;
        private readonly IEmailSettingsService _emailSettings;
        /// <summary>The company id under which the platform operator's own notifications are kept.</summary>
        public static readonly Guid Platform = Guid.Empty;
        public NotificationService(FdmsDbContext db, IEmailSender email, PublicUrls urls, IUsageService usage, ILogger<NotificationService> log, IEmailSettingsService emailSettings) { _db = db; _email = email; _urls = urls; _usage = usage; _log = log; _emailSettings = emailSettings; }

        public async Task<bool> RaisePlatformAsync(string kind, string severity, string key, string title, string? body, string? link, CancellationToken ct)
        {
            if (await _db.Notifications.AnyAsync(n => n.ClientId == Platform && n.Key == key, ct)) return false;
            var n = new Notification { ClientId = Platform, Kind = kind, Severity = severity, Key = key, Title = title.Length > 200 ? title[..200] : title, Body = body is { Length: > 1000 } ? body[..1000] : body, Link = link };
            _db.Notifications.Add(n);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { _db.Entry(n).State = EntityState.Detached; return false; }
            try { await global::Fdms.Infrastructure.WhatsApp.WhatsAppAlerts.QueueAsync(_db, null, n.Title, n.Body, ct); } catch (Exception ex) { _log.LogWarning(ex, "WhatsApp alert for {Key} not queued", key); }
            try { await global::Fdms.Infrastructure.Messaging.SmsAlerts.QueueAsync(_db, null, n.Title, n.Body, ct); } catch (Exception ex) { _log.LogWarning(ex, "SMS alert for {Key} not queued", key); }
            try
            {
                var to = await _emailSettings.OperatorEmailsAsync(ct);
                if (to.Count > 0 && _email.Configured)
                {
                    var app = (await _urls.AppAsync(ct)).TrimEnd('/');
                    var html = $"<p><b>{WebUtility.HtmlEncode(n.Title)}</b></p>" + (string.IsNullOrEmpty(n.Body) ? "" : $"<p>{WebUtility.HtmlEncode(n.Body)}</p>") +
                               (string.IsNullOrEmpty(n.Link) ? "" : $"<p><a href=\"{app}{n.Link}\">Open it in the console</a></p>") + "<p style=\"color:#64748b;font-size:12px\">FDMS Gateway platform alert. Change who gets these under Settings, Email.</p>";
                    foreach (var addr in to) await _email.SendAsync(addr, "[FDMS Gateway] " + n.Title, html, ct);
                    n.EmailedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Platform alert email for {Key} failed", key); }   // the bell still shows it
            return true;
        }

        public async Task<NotificationList> ListAsync(Guid clientId, bool unreadOnly, int take, CancellationToken ct)
        {
            var q = _db.Notifications.AsNoTracking().Where(n => n.ClientId == clientId);
            var unread = await q.CountAsync(n => n.ReadAt == null, ct);
            if (unreadOnly) q = q.Where(n => n.ReadAt == null);
            var rows = await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
            return new NotificationList(unread, rows.Select(n => new NotificationDto(n.Id, n.Kind, n.Severity, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt != null)).ToList());
        }

        public async Task MarkReadAsync(Guid clientId, IReadOnlyList<Guid>? ids, CancellationToken ct)
        {
            var q = _db.Notifications.Where(n => n.ClientId == clientId && n.ReadAt == null);
            if (ids is { Count: > 0 }) { var list = ids.ToList(); q = q.Where(n => list.Contains(n.Id)); }
            foreach (var n in await q.ToListAsync(ct)) n.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        private AlertSettingsDto ToSettings(Client c) => new(c.AlertEmails,
            c.AlertKinds == null ? Kinds.Select(k => k.Id).ToList() : c.AlertKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => Kinds.Any(x => x.Id == k)).ToList(),
            Kinds, _email.Configured, "When no address is given, alerts go to the email addresses of your company administrators.");

        public async Task<AlertSettingsDto> GetSettingsAsync(Guid clientId, CancellationToken ct) =>
            ToSettings(await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Company"));

        public async Task<AlertSettingsDto> SaveSettingsAsync(Guid clientId, SaveAlertSettingsRequest req, CancellationToken ct)
        {
            var c = await _db.Clients.FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Company");
            var emails = (req.Emails ?? "").Split(new[] { ',', ';', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (emails.Count > 5) throw GatewayException.BadRequest("TOO_MANY_EMAILS", "Give at most 5 email addresses.");
            if (emails.Any(e => e.Length > 150 || !EmailRx.IsMatch(e))) throw GatewayException.BadRequest("EMAIL_INVALID", "One of the email addresses is not valid.");
            c.AlertEmails = emails.Count == 0 ? null : string.Join(", ", emails);
            if (req.EnabledKinds != null)
            {
                var chosen = req.EnabledKinds.Where(k => Kinds.Any(x => x.Id == k)).Distinct().ToList();
                c.AlertKinds = chosen.Count == Kinds.Count ? null : string.Join(",", chosen);
            }
            await _db.SaveChangesAsync(ct);
            return ToSettings(c);
        }

        public async Task<bool> RaiseAsync(Guid clientId, string kind, string severity, string key, string title, string? body, string? link, CancellationToken ct, bool email = true)
        {
            var c = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId, ct);
            if (c == null) return false;
            // invoices and payments are always on: a company must always hear that it has something to pay
            var enabled = kind == "billing" || c.AlertKinds == null || c.AlertKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(kind);
            if (!enabled || await _db.Notifications.AnyAsync(n => n.ClientId == clientId && n.Key == key, ct)) return false;

            var n = new Notification { ClientId = clientId, Kind = kind, Severity = severity, Key = key, Title = title.Length > 200 ? title[..200] : title, Body = body is { Length: > 1000 } ? body[..1000] : body, Link = link };
            _db.Notifications.Add(n);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { _db.Entry(n).State = EntityState.Detached; return false; }   // raised at the same moment by another run
            try { await global::Fdms.Infrastructure.WhatsApp.WhatsAppAlerts.QueueAsync(_db, clientId, n.Title, n.Body, ct); } catch (Exception ex) { _log.LogWarning(ex, "WhatsApp alert for {Key} not queued", key); }
            try { await global::Fdms.Infrastructure.Messaging.SmsAlerts.QueueAsync(_db, clientId, n.Title, n.Body, ct); } catch (Exception ex) { _log.LogWarning(ex, "SMS alert for {Key} not queued", key); }

            try
            {
                var to = (c.AlertEmails ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (to.Count == 0)
                    to = await (from u in _db.Users.AsNoTracking() join ur in _db.UserRoles on u.Id equals ur.UserId join r in _db.Roles on ur.RoleId equals r.Id
                                where u.ClientId == clientId && u.Status == "ACTIVE" && r.Name == "ClientAdmin" && u.Email != null select u.Email!).Distinct().ToListAsync(ct);
                if (email && to.Count > 0 && _email.Configured)
                {
                    var app = (await _urls.AppAsync(ct)).TrimEnd('/');
                    var html = $"<p><b>{WebUtility.HtmlEncode(n.Title)}</b></p>" + (string.IsNullOrEmpty(n.Body) ? "" : $"<p>{WebUtility.HtmlEncode(n.Body)}</p>") +
                               (string.IsNullOrEmpty(n.Link) ? "" : $"<p><a href=\"{app}{n.Link}\">Open it in the console</a></p>") + $"<p style=\"color:#64748b;font-size:12px\">{WebUtility.HtmlEncode(c.Name)}. You can change which alerts you get under Alerts in the console.</p>";
                    foreach (var addr in to) await _email.SendAsync(addr, "[FDMS Gateway] " + n.Title, html, ct);
                    n.EmailedAt = DateTime.UtcNow; await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "Alert email for {Key} failed", key); }   // the bell still shows it
            return true;
        }

        public async Task<int> ScanAsync(DateTime utcNow, CancellationToken ct)
        {
            var raised = 0;
            var cat = utcNow.AddHours(2);   // Zimbabwe time as a wall clock, like FdmsClock
            foreach (var c in await _db.Clients.AsNoTracking().Where(x => x.IsActive).Select(x => new { x.Id, x.Name }).ToListAsync(ct))
            {
                async Task R(string kind, string sev, string key, string title, string? body, string? link) { if (await RaiseAsync(c.Id, kind, sev, key, title, body, link, ct)) raised++; }
                var devices = await _db.Devices.AsNoTracking().Where(d => d.ClientId == c.Id && d.IsActive).ToListAsync(ct);
                var ids = devices.Select(d => d.Id).ToList();

                foreach (var d in devices.Where(d => d.CertificateValidTill is { } t && t < utcNow.AddDays(30)))
                {
                    var expired = d.CertificateValidTill!.Value < utcNow;
                    await R("certificate", expired ? "bad" : "warn", $"cert:{d.Id}:{utcNow:yyyyMM}", expired ? $"Device {d.ZimraDeviceId}: the certificate has expired" : $"Device {d.ZimraDeviceId}: the certificate expires on {d.CertificateValidTill:d MMM yyyy}",
                        "Renew it on the Devices page (or press Fix on the Health page) so invoicing does not stop.", "/health");
                }

                var days = (await _db.FiscalDays.AsNoTracking().Where(f => ids.Contains(f.DeviceId) && f.Status != FiscalDayStatus.FiscalDayClosed).ToListAsync(ct));
                foreach (var f in days)
                {
                    var d = devices.First(x => x.Id == f.DeviceId);
                    if (f.Status == FiscalDayStatus.FiscalDayCloseFailed)
                        await R("dayclose", "bad", $"dayclose:{d.Id}:{f.FiscalDayNo}", $"Device {d.ZimraDeviceId}: fiscal day {f.FiscalDayNo} could not be closed", $"ZIMRA refused the close{(f.ClosingErrorCode != null ? " (" + f.ClosingErrorCode + ")" : "")}. Fix the receipts it names, then close the day again.", "/days");
                    else if (f.Status == FiscalDayStatus.FiscalDayOpened && f.OpenedAt.AddHours(Math.Max(1, d.DayMaxHrs - 3)) < cat)
                        await R("daylong", "warn", $"daylong:{d.Id}:{f.FiscalDayNo}", $"Device {d.ZimraDeviceId}: fiscal day {f.FiscalDayNo} has been open for a long time", $"ZIMRA refuses receipts after {d.DayMaxHrs} hours. Close the day soon.", "/days");
                }

                foreach (var r in await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId) && r.Status == ReceiptStatus.Rejected && r.CreatedAt > utcNow.AddDays(-3)).OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct))
                    await R("rejected", "bad", $"rejected:{r.Id}", $"Invoice {r.InvoiceNo} was rejected by ZIMRA", r.ErrorMessage ?? r.ErrorCode ?? "Open it to see why and correct it.", "/receipts");

                foreach (var g in await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId) && r.Status == ReceiptStatus.Pending && r.CreatedAt < utcNow.AddMinutes(-60))
                    .GroupBy(r => r.DeviceId).Select(g => new { DeviceId = g.Key, N = g.Count() }).ToListAsync(ct))
                {
                    var d = devices.First(x => x.Id == g.DeviceId);
                    await R("queued", "warn", $"queued:{d.Id}:{utcNow:yyyyMMdd}", $"Device {d.ZimraDeviceId}: {g.N} receipt(s) have been waiting for ZIMRA for over an hour", "Check the connection, then press Retry on the Health page.", "/health");
                }

                var u = await _usage.GetAsync(c.Id, ct);
                foreach (var item in u.Items.Where(i => i.Level != "ok"))
                    await R("usage", item.Level == "over" ? "bad" : "warn", $"usage:{item.Key}:{utcNow:yyyyMM}:{item.Level}",
                        item.Level == "over" ? $"{item.Label}: you are over your plan ({item.Used} of {item.Limit})" : $"{item.Label}: {item.Percent}% of your plan used ({item.Used} of {item.Limit})",
                        item.Key == "receipts" ? "Invoices keep being fiscalised, but consider upgrading under Billing." : "Upgrade under Billing to add more.", "/billing");
            }
            return raised;
        }
    }

    /// <summary>Looks for things that need attention every five minutes and raises alerts (see <see cref="NotificationService"/>).</summary>
    public sealed class AlertJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<AlertJob> _log;
        public AlertJob(IServiceScopeFactory scopes, ILogger<AlertJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(3), stop); } catch (OperationCanceledException) { return; }
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<INotificationService>().ScanAsync(DateTime.UtcNow, stop);
                    if (n > 0) _log.LogInformation("Raised {N} alert(s)", n);
                    await Task.Delay(TimeSpan.FromMinutes(5), stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Alert scan failed"); try { await Task.Delay(TimeSpan.FromMinutes(10), stop); } catch (OperationCanceledException) { break; } }
            }
        }
    }

    // =================================================================================================================
    /// <summary>One page that answers "is my device healthy?" with a traffic light per check and, where there is one, the single action that fixes it.</summary>
    public sealed class HealthService : IHealthService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IDeviceService _devices;
        public HealthService(FdmsDbContext db, ICallerScope scope, IDeviceService devices) { _db = db; _scope = scope; _devices = devices; }

        private static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now" : t.TotalMinutes < 90 ? $"{(int)t.TotalMinutes} min ago" : t.TotalHours < 48 ? $"{(int)t.TotalHours} h ago" : $"{(int)t.TotalDays} days ago";

        public async Task<IReadOnlyList<DeviceHealthDto>> CheckAsync(Guid clientId, Guid? deviceId, bool live, CancellationToken ct)
        {
            var q = _db.Devices.AsNoTracking().InScope(_scope).Where(d => d.ClientId == clientId);
            if (deviceId != null) q = q.Where(d => d.Id == deviceId);
            var devices = await q.OrderBy(d => d.ZimraDeviceId).ToListAsync(ct);
            if (deviceId != null && devices.Count == 0) throw GatewayException.NotFound("Device");
            var ids = devices.Select(d => d.Id).ToList();
            var utcNow = DateTime.UtcNow; var cat = FdmsClock.CatNow();

            var days = (await _db.FiscalDays.AsNoTracking().Where(f => ids.Contains(f.DeviceId)).OrderByDescending(f => f.FiscalDayNo).ToListAsync(ct)).GroupBy(f => f.DeviceId).ToDictionary(g => g.Key, g => g.First());
            var byStatus = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId) && (r.Status == ReceiptStatus.Pending || r.Status == ReceiptStatus.OfflineIssued || r.Status == ReceiptStatus.Held
                    || (r.Status == ReceiptStatus.Rejected && r.CreatedAt > utcNow.AddDays(-7))))
                .GroupBy(r => new { r.DeviceId, r.Status }).Select(g => new { g.Key.DeviceId, g.Key.Status, N = g.Count(), Oldest = g.Min(x => x.CreatedAt) }).ToListAsync(ct);
            var last = (await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.DeviceId)).GroupBy(r => r.DeviceId).Select(g => new { DeviceId = g.Key, At = g.Max(x => x.CreatedAt) }).ToListAsync(ct)).ToDictionary(x => x.DeviceId, x => x.At);

            var result = new List<DeviceHealthDto>();
            foreach (var d in devices)
            {
                var checks = new List<HealthCheckDto>();

                // ZIMRA reachable (a real call, only when asked)
                if (live)
                {
                    try { var p = await _devices.PingAsync(clientId, d.Id, ct); checks.Add(new("connection", "ZIMRA connection", "ok", $"ZIMRA answered just now.", "ping", "Test again")); }
                    catch (Exception ex) when (ex is FdmsException or GatewayException or HttpRequestException or TaskCanceledException)
                    { checks.Add(new("connection", "ZIMRA connection", "bad", $"ZIMRA could not be reached: {ex.Message}", "ping", "Test again")); }
                }
                else checks.Add(new("connection", "ZIMRA connection", "info", "Not tested yet. Press “Test connection” to ask ZIMRA now.", "ping", "Test connection"));

                // certificate
                if (d.CertificateValidTill is { } till)
                {
                    var left = (till - utcNow).TotalDays;
                    checks.Add(left < 0 ? new("certificate", "Device certificate", "bad", $"Expired on {till:d MMM yyyy}. Invoices cannot be signed.", "renew-certificate", "Renew certificate")
                        : left <= 30 ? new("certificate", "Device certificate", "warn", $"Expires in {(int)left} days ({till:d MMM yyyy}).", "renew-certificate", "Renew certificate")
                        : new("certificate", "Device certificate", "ok", $"Valid until {till:d MMM yyyy}."));
                }
                else checks.Add(new("certificate", "Device certificate", "warn", "No expiry date is known.", "refresh-config", "Refresh from ZIMRA"));

                // settings from ZIMRA
                var taxes = Mappers.ParseTaxes(d.TaxesJson);
                if (taxes.Count == 0) checks.Add(new("taxes", "Taxes from ZIMRA", "bad", "No taxes are configured, so invoices cannot be calculated.", "refresh-config", "Refresh from ZIMRA"));
                else if (d.ConfigRefreshedAt is null || utcNow - d.ConfigRefreshedAt.Value > TimeSpan.FromDays(2))
                    checks.Add(new("taxes", "Taxes from ZIMRA", "warn", d.ConfigRefreshedAt is null ? "Settings were never refreshed." : $"Settings were last refreshed {Ago(utcNow - d.ConfigRefreshedAt.Value)}.", "refresh-config", "Refresh from ZIMRA"));
                else checks.Add(new("taxes", "Taxes from ZIMRA", "ok", $"{taxes.Count} tax(es) configured, refreshed {Ago(utcNow - d.ConfigRefreshedAt.Value)}."));

                // fiscal day
                days.TryGetValue(d.Id, out var day);
                if (day == null || day.Status == FiscalDayStatus.FiscalDayClosed) checks.Add(new("day", "Fiscal day", "ok", "No day is open. The next receipt opens one by itself."));
                else if (day.Status == FiscalDayStatus.FiscalDayCloseFailed) checks.Add(new("day", "Fiscal day", "bad", $"Day {day.FiscalDayNo}: ZIMRA refused to close it{(day.ClosingErrorCode != null ? " (" + day.ClosingErrorCode + ")" : "")}.", "open-fiscal-days", "See what to fix"));
                else if (day.Status == FiscalDayStatus.FiscalDayCloseInitiated) checks.Add(new("day", "Fiscal day", "warn", $"Day {day.FiscalDayNo}: ZIMRA is still checking the close.", "open-fiscal-days", "Open Fiscal days"));
                else if (day.OpenedAt.AddHours(Math.Max(1, d.DayMaxHrs - 3)) < cat) checks.Add(new("day", "Fiscal day", "warn", $"Day {day.FiscalDayNo} has been open for {(int)(cat - day.OpenedAt).TotalHours} h; ZIMRA stops accepting receipts after {d.DayMaxHrs} h.", "close-day", "Close the day"));
                else checks.Add(new("day", "Fiscal day", "ok", $"Day {day.FiscalDayNo} is open since {day.OpenedAt:HH:mm} ({(int)(cat - day.OpenedAt).TotalHours} h)."));

                // receipts waiting for ZIMRA
                var pending = byStatus.FirstOrDefault(x => x.DeviceId == d.Id && x.Status == ReceiptStatus.Pending);
                if (pending == null) checks.Add(new("queue", "Receipts waiting for ZIMRA", "ok", "Nothing is waiting."));
                else
                {
                    var age = utcNow - pending.Oldest;
                    checks.Add(new("queue", "Receipts waiting for ZIMRA", age > TimeSpan.FromMinutes(60) ? "bad" : "warn", $"{pending.N} receipt(s) are queued; the oldest has waited {Ago(age).Replace(" ago", "")}.", "retry-pending", "Send them now"));
                }
                var held = byStatus.FirstOrDefault(x => x.DeviceId == d.Id && x.Status == ReceiptStatus.Held);
                if (held != null) checks.Add(new("scheduled", "Scheduled receipts", "info", $"{held.N} scheduled receipt(s) will be sent at their set time."));

                if (d.OperatingMode == DeviceOperatingMode.Offline)
                {
                    var off = byStatus.FirstOrDefault(x => x.DeviceId == d.Id && x.Status == ReceiptStatus.OfflineIssued);
                    checks.Add(off == null ? new("offline", "Offline files", "ok", "Offline mode. Nothing is waiting to be uploaded.")
                        : new("offline", "Offline files", "info", $"Offline mode. {off.N} receipt(s) are waiting for the day's file to be uploaded.", "offline-files", "Open Offline files"));
                }

                var rej = byStatus.FirstOrDefault(x => x.DeviceId == d.Id && x.Status == ReceiptStatus.Rejected);
                checks.Add(rej == null ? new("rejected", "Rejected receipts", "ok", "None in the last 7 days.") : new("rejected", "Rejected receipts", "bad", $"{rej.N} receipt(s) were rejected by ZIMRA in the last 7 days.", "view-rejected", "Open them"));

                if (last.TryGetValue(d.Id, out var lastAt) && utcNow - lastAt > TimeSpan.FromDays(3)) checks.Add(new("activity", "Recent use", "info", $"The last receipt was issued {Ago(utcNow - lastAt)}."));

                var overall = checks.Any(c => c.Status == "bad") ? "bad" : checks.Any(c => c.Status == "warn") ? "warn" : "ok";
                result.Add(new DeviceHealthDto(d.Id, d.ZimraDeviceId, d.Name ?? d.BranchName ?? d.ModelName, overall, checks));
            }
            return result;
        }
    }
}
