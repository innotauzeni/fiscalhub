using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Fiscal day lifecycle. ZIMRA is the source of truth: every operation first reads getStatus and reconciles the
    /// local row, so a day opened/closed elsewhere can never deadlock this gateway.
    /// </summary>
    public sealed class FiscalDayService : IFiscalDayService
    {
        private readonly FdmsDbContext _db;
        private readonly IFdmsGateway _gw;
        private readonly IDeviceSigner _signer;
        private readonly DeviceAccessor _devices;
        private readonly DeviceLocks _locks;
        private readonly WebhookQueue _hooks;
        private readonly OfflineFileService _offline;
        private readonly ILogger<FiscalDayService> _log;

        public FiscalDayService(FdmsDbContext db, IFdmsGateway gw, IDeviceSigner signer, DeviceAccessor devices, DeviceLocks locks,
            WebhookQueue hooks, OfflineFileService offline, ILogger<FiscalDayService> log)
        {
            _db = db; _gw = gw; _signer = signer; _devices = devices; _locks = locks; _hooks = hooks; _offline = offline; _log = log;
        }

        private static bool IsActive(FiscalDayStatus s) => s != FiscalDayStatus.FiscalDayClosed;

        public Task<FiscalDay?> CurrentDayAsync(Guid deviceId, CancellationToken ct) =>
            _db.FiscalDays.Include(d => d.Counters)
               .Where(d => d.DeviceId == deviceId && d.Status != FiscalDayStatus.FiscalDayClosed)
               .OrderByDescending(d => d.FiscalDayNo)
               .FirstOrDefaultAsync(ct);

        public async Task<FiscalDayDto> GetStatusAsync(Guid clientId, Guid deviceId, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct);
            var gate = await _locks.AcquireAsync(device.Id, ct);
            try
            {
                // offline devices cannot call getStatus (DEV01): the local record is the truth
                var day = device.OperatingMode == DeviceOperatingMode.Offline ? await CurrentDayAsync(device.Id, ct) : (await SyncAsync(device, ct)).Day;
                return day?.ToDto() ?? await LastClosedDtoAsync(device, ct);
            }
            finally { await gate.DisposeAsync(); }
        }

        public async Task<FiscalDayDto> OpenAsync(Guid clientId, Guid deviceId, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct);
            var gate = await _locks.AcquireAsync(device.Id, ct);
            try { return (await OpenUnlockedAsync(device, ct)).ToDto(); }
            finally { await gate.DisposeAsync(); }
        }

        /// <summary>Caller must hold the device lock.</summary>
        public async Task<FiscalDay> OpenUnlockedAsync(Device device, CancellationToken ct)
        {
            if (device.OperatingMode == DeviceOperatingMode.Offline)
            {
                // Offline devices open days locally (openDay is refused with DEV01); the day is announced to FDMS inside its file.
                var local = await CurrentDayAsync(device.Id, ct);
                if (local != null) return local;
                var lastNo = await _db.FiscalDays.Where(d => d.DeviceId == device.Id).MaxAsync(d => (int?)d.FiscalDayNo, ct) ?? 0;
                var created = new FiscalDay { DeviceId = device.Id, FiscalDayNo = lastNo + 1, Status = FiscalDayStatus.FiscalDayOpened, OpenedAt = FdmsClock.CatNow(), IsOffline = true };
                _db.FiscalDays.Add(created);
                _db.AuditLogs.Add(new AuditLog { ClientId = device.ClientId, Action = "fiscalday.open", Detail = $"device={device.ZimraDeviceId} day={created.FiscalDayNo} offline" });
                await _db.SaveChangesAsync(ct);
                return created;
            }

            var (status, day) = await SyncAsync(device, ct);
            if (day != null && IsActive(day.Status))
            {
                if (day.Status == FiscalDayStatus.FiscalDayCloseInitiated)
                    throw GatewayException.Conflict("FISC_CLOSE_IN_PROGRESS", "The fiscal day is being closed; wait for FDMS to finish.");
                return day;   // already open (or CloseFailed, which still accepts receipts) — idempotent
            }

            var dayNo = (status.LastFiscalDayNo ?? 0) + 1;
            var openedAt = FdmsClock.CatNow();
            var res = await _gw.OpenDayAsync(_devices.Credentials(device), openedAt, dayNo, ct);

            var entity = new FiscalDay
            {
                DeviceId = device.Id,
                FiscalDayNo = res.FiscalDayNo,
                Status = FiscalDayStatus.FiscalDayOpened,
                OpenedAt = openedAt,
            };
            _db.FiscalDays.Add(entity);
            _db.AuditLogs.Add(new AuditLog { ClientId = device.ClientId, Action = "fiscalday.open", Detail = $"device={device.ZimraDeviceId} day={res.FiscalDayNo}" });
            await _db.SaveChangesAsync(ct);
            return entity;
        }

        public async Task<FiscalDayDto> CloseAsync(Guid clientId, Guid deviceId, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct);
            var gate = await _locks.AcquireAsync(device.Id, ct);
            try
            {
                if (device.OperatingMode == DeviceOperatingMode.Offline) return (await CloseOfflineAsync(device, ct)).ToDto();

                var (status, day) = await SyncAsync(device, ct);
                if (day == null || status.FiscalDayStatus is not ("FiscalDayOpened" or "FiscalDayCloseFailed"))
                    throw GatewayException.Unprocessable("FISC04", "There is no open fiscal day to close.");

                if (await _db.Receipts.AnyAsync(r => r.DeviceId == device.Id && r.FiscalDayId == day.Id && r.Status == ReceiptStatus.Pending, ct))
                    throw GatewayException.Conflict("RECEIPTS_PENDING",
                        "Some receipts are still waiting to be delivered to FDMS. Retry them first (POST /receipts/{id}/retry).");

                var req = BuildCloseRequest(device, day);

                await _gw.CloseDayAsync(_devices.Credentials(device), req, ct);
                day.Status = FiscalDayStatus.FiscalDayCloseInitiated;
                await _db.SaveChangesAsync(ct);

                // FDMS validates the report asynchronously: poll briefly for the outcome.
                for (var i = 0; i < 8; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    var st = await _gw.GetStatusAsync(_devices.Credentials(device), ct);
                    if (st.FiscalDayStatus == "FiscalDayClosed")
                    {
                        day.Status = FiscalDayStatus.FiscalDayClosed;
                        day.ClosedAt = st.FiscalDayClosed ?? FdmsClock.CatNow();
                        day.ClosingErrorCode = null;
                        break;
                    }
                    if (st.FiscalDayStatus == "FiscalDayCloseFailed")
                    {
                        day.Status = FiscalDayStatus.FiscalDayCloseFailed;
                        day.ClosingErrorCode = st.FiscalDayClosingErrorCode;
                        break;
                    }
                }
                _db.AuditLogs.Add(new AuditLog { ClientId = device.ClientId, Action = "fiscalday.close", Detail = $"device={device.ZimraDeviceId} day={day.FiscalDayNo} status={day.Status} err={day.ClosingErrorCode}" });
                if (day.Status is FiscalDayStatus.FiscalDayClosed or FiscalDayStatus.FiscalDayCloseFailed)
                    await _hooks.EnqueueAsync(device.ClientId,
                        day.Status == FiscalDayStatus.FiscalDayClosed ? WebhookEvents.FiscalDayClosed : WebhookEvents.FiscalDayCloseFailed,
                        new { deviceId = device.Id, zimraDeviceId = device.ZimraDeviceId, fiscalDayNo = day.FiscalDayNo, status = day.Status.ToString(), closingErrorCode = day.ClosingErrorCode }, ct);
                await _db.SaveChangesAsync(ct);
                return day.ToDto();
            }
            finally { await gate.DisposeAsync(); }
        }

        /// <summary>The Z report: non-zero counters, their signature (spec 13.3.1) and the last receipt counter.</summary>
        private CloseDayRequest BuildCloseRequest(Device device, FiscalDay day)
        {
            var counters = day.Counters.Select(c => c.ToCounter()).Where(c => c.Value != 0m).ToList();
            var toSign = SignatureStringBuilder.BuildFiscalDayString(device.ZimraDeviceId, day.FiscalDayNo, day.OpenedAt.Date, counters);
            var sig = _signer.Sign(toSign, _devices.PrivateKey(device), device.CertificatePem);
            _log.LogInformation("CloseDay sign string device={Device}: {S}", device.ZimraDeviceId, toSign);

            return new CloseDayRequest(
                day.FiscalDayNo,
                counters.OrderBy(c => (int)c.Type).Select(c => new FiscalCounterDto(
                    c.Type.ToString(), c.Currency,
                    c.Type == FiscalCounterType.BalanceByMoneyType ? null : c.TaxPercent,
                    c.Type == FiscalCounterType.BalanceByMoneyType ? null : c.TaxId,
                    c.MoneyType?.ToString(), Math.Round(c.Value, 2))).ToList(),
                new SignatureDto(sig.Hash, sig.Signature),
                day.LastReceiptCounter);
        }

        /// <summary>Offline close: sign the Z report locally, pack the day into files (footer in the last) and try to upload. Caller holds the lock.</summary>
        private async Task<FiscalDay> CloseOfflineAsync(Device device, CancellationToken ct)
        {
            var day = await CurrentDayAsync(device.Id, ct)
                      ?? throw GatewayException.Unprocessable("FISC04", "There is no open fiscal day to close.");
            var footer = BuildCloseRequest(device, day);
            var closedAt = FdmsClock.CatNow();

            day.Status = FiscalDayStatus.FiscalDayClosed;
            day.ClosedAt = closedAt;
            await _offline.BuildFilesAsync(device, day, footer, closedAt, ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = device.ClientId, Action = "fiscalday.close", Detail = $"device={device.ZimraDeviceId} day={day.FiscalDayNo} offline" });
            await _hooks.EnqueueAsync(device.ClientId, WebhookEvents.FiscalDayClosed,
                new { deviceId = device.Id, zimraDeviceId = device.ZimraDeviceId, fiscalDayNo = day.FiscalDayNo, status = "FiscalDayClosed", offline = true }, ct);
            await _db.SaveChangesAsync(ct);

            try { await _offline.ProcessDeviceAsync(device, ct); }
            catch (Exception ex) when (ex is FdmsException or GatewayException) { _log.LogWarning("Offline upload deferred: {Msg}", ex.Message); }   // the background job retries
            return day;
        }

        /// <summary>Reads FDMS status and aligns the local day / receipt sequence with it. Caller must hold the device lock.</summary>
        private async Task<(FdmsStatus Status, FiscalDay? Day)> SyncAsync(Device device, CancellationToken ct)
        {
            var st = await _gw.GetStatusAsync(_devices.Credentials(device), ct);
            var local = await CurrentDayAsync(device.Id, ct);
            var parsed = Enum.TryParse<FiscalDayStatus>(st.FiscalDayStatus, out var remote) ? remote : FiscalDayStatus.FiscalDayClosed;

            if (parsed == FiscalDayStatus.FiscalDayClosed)
            {
                if (local != null)
                {
                    local.Status = FiscalDayStatus.FiscalDayClosed;
                    local.ClosedAt = st.FiscalDayClosed ?? FdmsClock.CatNow();
                    local = null;
                }
            }
            else if (local == null || local.FiscalDayNo != st.LastFiscalDayNo)
            {
                // A day is open at ZIMRA that we did not open (e.g. another system on the same device). Track it,
                // but its earlier receipts/counters are unknown to us — closing it from here would be wrong.
                _log.LogWarning("Device {Id}: FDMS has fiscal day {No} open that this gateway did not open.", device.ZimraDeviceId, st.LastFiscalDayNo);
                if (local != null) local.Status = FiscalDayStatus.FiscalDayClosed;
                local = new FiscalDay
                {
                    DeviceId = device.Id,
                    FiscalDayNo = st.LastFiscalDayNo ?? 1,
                    Status = parsed,
                    OpenedAt = FdmsClock.CatNow(),
                };
                _db.FiscalDays.Add(local);
            }
            else
            {
                local.Status = parsed;
                local.ClosingErrorCode = st.FiscalDayClosingErrorCode;
            }

            if (st.LastReceiptGlobalNo.HasValue && st.LastReceiptGlobalNo.Value > device.LastReceiptGlobalNo)
                device.LastReceiptGlobalNo = st.LastReceiptGlobalNo.Value;   // receipts issued elsewhere: continue after them

            await _db.SaveChangesAsync(ct);
            return (st, local);
        }

        private async Task<FiscalDayDto> LastClosedDtoAsync(Device device, CancellationToken ct)
        {
            var last = await _db.FiscalDays.Include(d => d.Counters).AsNoTracking()
                .Where(d => d.DeviceId == device.Id).OrderByDescending(d => d.FiscalDayNo).FirstOrDefaultAsync(ct);
            return last?.ToDto() ?? new FiscalDayDto(device.Id, 0, FiscalDayStatus.FiscalDayClosed.ToString(), default, null, 0, null, Array.Empty<FiscalCounterView>());
        }
    }
}
