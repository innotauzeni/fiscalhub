using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    /// <summary>Re-delivers receipts left Pending by FDMS outages (oldest first, per device) every 30 seconds.</summary>
    public sealed class PendingReceiptRetryJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<PendingReceiptRetryJob> _log;
        public PendingReceiptRetryJob(IServiceScopeFactory scopes, ILogger<PendingReceiptRetryJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stop);
                    using var scope = _scopes.CreateScope();
                    var receipts = scope.ServiceProvider.GetRequiredService<IReceiptService>();
                    var held = await receipts.ReleaseDueHeldAsync(stop);
                    if (held > 0) _log.LogInformation("Issued {N} receipt(s) whose hold period ended.", held);
                    var n = await receipts.RetryPendingAsync(stop);
                    if (n > 0) _log.LogInformation("Delivered {N} queued receipt(s) to FDMS.", n);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogError(ex, "Pending receipt retry failed"); }
            }
        }
    }

    /// <summary>
    /// Closes every fiscal day that is still open at the end of the Zimbabwe day (23:59 by default, setting FiscalDay:AutoCloseTime; leave it empty to switch this off), so a
    /// forgotten day never runs on into the next one. It looks every minute, and a day that could not be closed on time (the gateway was down, receipts were still pending, ZIMRA refused)
    /// is retried every 10 minutes, up to 6 tries per night. A day opened after the cut-off belongs to the next night's close. An offline device's day is closed the same way (that needs no internet): the receipts and Z report are packed into the day's file, which is then uploaded when ZIMRA can be reached.
    /// </summary>
    public sealed class FiscalDayAutoCloseJob : BackgroundService
    {
        public const string DefaultTime = "23:59";
        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _cfg;
        private readonly ILogger<FiscalDayAutoCloseJob> _log;
        // per device: the cut-off being worked on, how many tries so far, and when the last one was
        private readonly Dictionary<Guid, (DateTime Cutoff, int Tries, DateTime Last)> _tries = new();
        public FiscalDayAutoCloseJob(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<FiscalDayAutoCloseJob> log) { _scopes = scopes; _cfg = cfg; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), stop); await RunOnceAsync(FdmsClock.CatNow(), stop); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Fiscal day auto-close run failed"); }
            }
        }

        /// <summary>The most recent cut-off at or before <paramref name="now"/>: today's if it has passed, otherwise yesterday's.</summary>
        public static DateTime LastCutoff(DateTime now, TimeSpan at) => now.TimeOfDay >= at ? now.Date + at : now.Date.AddDays(-1) + at;

        /// <summary>One pass. <paramref name="now"/> is Zimbabwe time. Returns how many days were closed.</summary>
        public async Task<int> RunOnceAsync(DateTime now, CancellationToken ct)
        {
            var setting = _cfg["FiscalDay:AutoCloseTime"] ?? DefaultTime;
            if (string.IsNullOrWhiteSpace(setting)) return 0;
            if (!TimeSpan.TryParse(setting, out var at) || at < TimeSpan.Zero || at >= TimeSpan.FromDays(1)) { _log.LogWarning("FiscalDay:AutoCloseTime '{T}' is not a time of day (use HH:mm); auto-close is off", setting); return 0; }
            var cutoff = LastCutoff(now, at);

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var days = scope.ServiceProvider.GetRequiredService<IFiscalDayService>();

            var due = await db.FiscalDays.AsNoTracking()
                .Where(f => f.Status == FiscalDayStatus.FiscalDayOpened && f.OpenedAt < cutoff)
                .Join(db.Devices.Where(d => d.IsActive), f => f.DeviceId, d => d.Id, (f, d) => new { f.FiscalDayNo, d.Id, d.ClientId, d.ZimraDeviceId })
                .ToListAsync(ct);

            var closed = 0;
            foreach (var x in due)
            {
                if (_tries.TryGetValue(x.Id, out var t) && t.Cutoff == cutoff && (t.Tries >= 6 || now - t.Last < TimeSpan.FromMinutes(10))) continue;
                var tries = _tries.TryGetValue(x.Id, out var prev) && prev.Cutoff == cutoff ? prev.Tries + 1 : 1;
                _tries[x.Id] = (cutoff, tries, now);
                try
                {
                    _log.LogInformation("Auto-closing fiscal day {No} of device {Id} at the end of the day (try {N})", x.FiscalDayNo, x.ZimraDeviceId, tries);
                    var res = await days.CloseAsync(x.ClientId, x.Id, ct);
                    db.AuditLogs.Add(new AuditLog { ClientId = x.ClientId, ActorType = "system", Action = "fiscalday.autoclose", Entity = "Device", EntityId = x.Id.ToString(), Detail = $"Fiscal day {x.FiscalDayNo} of device {x.ZimraDeviceId} closed automatically at the end of the day: {res.Status}" });
                    await db.SaveChangesAsync(ct);
                    closed++;
                }
                catch (Exception ex) when (ex is FdmsException or GatewayException)
                {
                    _log.LogWarning("Auto-close of device {Id} failed (try {N}): {Msg}", x.ZimraDeviceId, tries, ex.Message);
                    db.AuditLogs.Add(new AuditLog { ClientId = x.ClientId, ActorType = "system", Action = "fiscalday.autoclose_failed", Entity = "Device", EntityId = x.Id.ToString(), Detail = $"Fiscal day {x.FiscalDayNo} of device {x.ZimraDeviceId} could not be closed automatically (try {tries}): {ex.Message}" });
                    await db.SaveChangesAsync(ct);
                }
            }
            // forget devices whose cut-off has moved on
            foreach (var k in _tries.Where(p => p.Value.Cutoff < cutoff).Select(p => p.Key).ToList()) _tries.Remove(k);
            return closed;
        }
    }

    /// <summary>
    /// Closes fiscal days that are about to exceed taxPayerDayMaxHrs (FDMS rejects receipts after that, RCPT041) and
    /// renews certificates that expire within 30 days (spec 4.3 recommends one month ahead). Runs hourly.
    /// </summary>
    public sealed class DeviceMaintenanceJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<DeviceMaintenanceJob> _log;
        public DeviceMaintenanceJob(IServiceScopeFactory scopes, ILogger<DeviceMaintenanceJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stop);
                    await RunOnceAsync(stop);
                    await Task.Delay(TimeSpan.FromMinutes(55), stop);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogError(ex, "Device maintenance failed"); }
            }
        }

        private async Task RunOnceAsync(CancellationToken ct)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var days = scope.ServiceProvider.GetRequiredService<IFiscalDayService>();
            var devices = scope.ServiceProvider.GetRequiredService<IDeviceService>();
            var cat = FdmsClock.CatNow();

            foreach (var d in await db.Devices.AsNoTracking().Where(x => x.IsActive && x.OperatingMode == DeviceOperatingMode.Online).ToListAsync(ct))
            {
                try
                {
                    if (d.CertificateValidTill is { } till && till < DateTime.UtcNow.AddDays(30))
                    {
                        _log.LogInformation("Renewing certificate for device {Id} (expires {Till})", d.ZimraDeviceId, till);
                        await devices.RenewCertificateAsync(d.ClientId, d.Id, ct);
                    }

                    var open = await db.FiscalDays.AsNoTracking().Where(f => f.DeviceId == d.Id && f.Status == FiscalDayStatus.FiscalDayOpened)
                        .OrderByDescending(f => f.FiscalDayNo).FirstOrDefaultAsync(ct);
                    // Close with a one-hour safety margin before the maximum fiscal day length.
                    if (open != null && open.OpenedAt.AddHours(d.DayMaxHrs - 1) < cat)
                    {
                        _log.LogInformation("Auto-closing fiscal day {No} of device {Id}", open.FiscalDayNo, d.ZimraDeviceId);
                        await days.CloseAsync(d.ClientId, d.Id, ct);
                    }
                }
                catch (Exception ex) when (ex is FdmsException or GatewayException)
                {
                    _log.LogWarning("Maintenance for device {Id} skipped: {Msg}", d.ZimraDeviceId, ex.Message);
                }
            }
        }

    }
}
