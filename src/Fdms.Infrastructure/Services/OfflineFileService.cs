using Fdms.Application.Fiscal;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// FDMS Offline mode (spec 2.4 / 4.9): receipts are issued locally (signed, numbered, QR-ready) and handed to FDMS in
    /// per-fiscal-day files. Files must stay under 3 MB, are sent in sequence order, and only the last file of a day carries the
    /// Z-report footer. Files are kept until FDMS reports them processed.
    /// </summary>
    public sealed class OfflineFileService : IOfflineFileService
    {
        /// <summary>Raw JSON budget per file; base64 adds a third, which stays under the 3 MB limit.</summary>
        public const int DefaultRawBytesPerFile = 1_900_000;
        public const int MaxUploadAttempts = 10;

        private readonly FdmsDbContext _db;
        private readonly IFdmsGateway _gw;
        private readonly DeviceAccessor _devices;
        private readonly DeviceLocks _locks;
        private readonly WebhookQueue _hooks;
        private readonly ILogger<OfflineFileService> _log;
        private readonly int _rawBytes;

        public OfflineFileService(FdmsDbContext db, IFdmsGateway gw, DeviceAccessor devices, DeviceLocks locks, WebhookQueue hooks, Microsoft.Extensions.Configuration.IConfiguration cfg, ILogger<OfflineFileService> log)
        {
            _db = db; _gw = gw; _devices = devices; _locks = locks; _hooks = hooks; _log = log;
            _rawBytes = Math.Clamp(cfg.GetValue("Fdms:OfflineFileBytes", DefaultRawBytesPerFile), 2000, DefaultRawBytesPerFile);
        }

        // ---- building ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Packs this day's not-yet-filed offline receipts into one or more files (footer in the last one when the day is closed).
        /// Caller holds the device lock.
        /// </summary>
        public async Task BuildFilesAsync(Device device, FiscalDay day, CloseDayRequest? footer, DateTime? closedAt, CancellationToken ct)
        {
            var receipts = await _db.Receipts
                .Where(r => r.DeviceId == device.Id && r.FiscalDayId == day.Id && r.Status == ReceiptStatus.OfflineIssued && r.OfflineFileId == null)
                .OrderBy(r => r.ReceiptCounter).ToListAsync(ct);
            if (receipts.Count == 0 && footer == null) return;

            var seq = await _db.OfflineFiles.Where(f => f.DeviceId == device.Id && f.FiscalDayNo == day.FiscalDayNo).MaxAsync(f => (int?)f.Sequence, ct) ?? 0;

            var chunks = new List<List<Receipt>>();
            var cur = new List<Receipt>(); var size = 0;
            foreach (var r in receipts)
            {
                if (cur.Count > 0 && size + r.PayloadJson.Length > _rawBytes) { chunks.Add(cur); cur = new(); size = 0; }
                cur.Add(r); size += r.PayloadJson.Length;
            }
            if (cur.Count > 0 || chunks.Count == 0) chunks.Add(cur);

            var created = new List<(OfflineFile File, List<Receipt> Receipts)>();
            for (var i = 0; i < chunks.Count; i++)
            {
                var last = i == chunks.Count - 1;
                var dto = new FiscalDayFileDto(
                    new FileHeaderDto(device.ZimraDeviceId, day.FiscalDayNo, day.OpenedAt.ToString("yyyy-MM-ddTHH:mm:ss"), ++seq),
                    chunks[i].Count == 0 ? null : new FileContentDto(chunks[i].Select(r => JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options)!).ToList()),
                    last && footer != null
                        ? new FileFooterDto(footer.FiscalDayCounters, footer.FiscalDayDeviceSignature, footer.ReceiptCounter,
                            (closedAt ?? FdmsClock_Now()).ToString("yyyy-MM-ddTHH:mm:ss"))
                        : null);
                var json = JsonSerializer.Serialize(dto, FdmsJson.Options);
                var file = new OfflineFile
                {
                    ClientId = device.ClientId, DeviceId = device.Id, FiscalDayNo = day.FiscalDayNo, Sequence = seq,
                    ReceiptCount = chunks[i].Count, HasFooter = last && footer != null,
                    ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)),
                };
                _db.OfflineFiles.Add(file);
                created.Add((file, chunks[i]));
            }
            await _db.SaveChangesAsync(ct);   // ids assigned
            foreach (var (file, rs) in created) foreach (var r in rs) r.OfflineFileId = file.Id;
            await _db.SaveChangesAsync(ct);
        }

        private static DateTime FdmsClock_Now() => Application.Fiscal.FdmsClock.CatNow();

        // ---- operator-facing ---------------------------------------------------------------------------------------

        public async Task<IReadOnlyList<OfflineFileDto>> ListAsync(Guid clientId, Guid deviceId, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct, track: false);
            return (await _db.OfflineFiles.AsNoTracking().Where(f => f.DeviceId == device.Id).OrderByDescending(f => f.FiscalDayNo).ThenByDescending(f => f.Sequence).Take(200).ToListAsync(ct))
                .Select(ToDto).ToList();
        }

        /// <summary>Files up everything issued so far (content-only files for an open day), resets retry counters and uploads/polls now.</summary>
        public async Task<IReadOnlyList<OfflineFileDto>> SyncAsync(Guid clientId, Guid deviceId, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct);
            if (device.OperatingMode != DeviceOperatingMode.Offline)
                throw GatewayException.Conflict("NOT_OFFLINE", "This device is in Online mode; receipts are sent to FDMS directly.");

            await using (await _locks.AcquireAsync(device.Id, ct))
            {
                var open = await _db.FiscalDays.Include(d => d.Counters).Where(d => d.DeviceId == device.Id && d.IsOffline && d.Status == FiscalDayStatus.FiscalDayOpened)
                    .OrderByDescending(d => d.FiscalDayNo).FirstOrDefaultAsync(ct);
                if (open != null) await BuildFilesAsync(device, open, null, null, ct);
            }
            foreach (var f in await _db.OfflineFiles.Where(f => f.DeviceId == device.Id && f.Status == OfflineFileStatus.Pending).ToListAsync(ct)) f.Attempts = 0;
            await _db.SaveChangesAsync(ct);
            await ProcessDeviceAsync(device, ct);
            return await ListAsync(clientId, deviceId, ct);
        }

        // ---- upload + status polling -------------------------------------------------------------------------------

        /// <summary>Background entry point: advances every device that still has unfinished files.</summary>
        public async Task<int> RunAllAsync(CancellationToken ct)
        {
            var ids = await _db.OfflineFiles.Where(f => f.Status == OfflineFileStatus.Pending || f.Status == OfflineFileStatus.Uploaded).Select(f => f.DeviceId).Distinct().ToListAsync(ct);
            var n = 0;
            foreach (var id in ids)
            {
                try { n += await ProcessDeviceAsync(await _devices.GetByIdAsync(id, ct), ct); }
                catch (Exception ex) { _log.LogError(ex, "Offline file processing failed for device {Id}", id); }
            }
            return n;
        }

        /// <summary>Uploads Pending files in (day, sequence) order and checks Uploaded files. Returns files that reached a final state.</summary>
        public async Task<int> ProcessDeviceAsync(Device device, CancellationToken ct)
        {
            var creds = _devices.Credentials(device);
            var finished = 0;

            // 1) upload in order; stop at the first failure so FDMS never sees a gap
            var pending = await _db.OfflineFiles.Where(f => f.DeviceId == device.Id && f.Status == OfflineFileStatus.Pending)
                .OrderBy(f => f.FiscalDayNo).ThenBy(f => f.Sequence).ToListAsync(ct);
            foreach (var f in pending)
            {
                if (f.Attempts >= MaxUploadAttempts) break;
                f.Attempts++;
                try
                {
                    var res = await _gw.SubmitFileAsync(creds, $"{device.ZimraDeviceId}_{f.FiscalDayNo}_{f.Sequence}.json", f.ContentBase64, ct);
                    f.OperationId = res.OperationId; f.Status = OfflineFileStatus.Uploaded; f.UploadedAt = DateTime.UtcNow; f.LastError = null;
                    await _db.SaveChangesAsync(ct);
                }
                catch (FdmsException ex)
                {
                    f.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    await _db.SaveChangesAsync(ct);
                    _log.LogWarning("Offline file {Day}/{Seq} upload failed: {Msg}", f.FiscalDayNo, f.Sequence, ex.Message);
                    break;
                }
            }

            // 2) ask FDMS how the uploaded files went
            var uploaded = await _db.OfflineFiles.Where(f => f.DeviceId == device.Id && f.Status == OfflineFileStatus.Uploaded)
                .OrderBy(f => f.FiscalDayNo).ThenBy(f => f.Sequence).ToListAsync(ct);
            foreach (var f in uploaded)
            {
                FileStatusResponse st;
                try
                {
                    var from = (f.UploadedAt ?? DateTime.UtcNow).AddHours(2).Date.AddDays(-1);
                    st = await _gw.GetFileStatusAsync(creds, from, Application.Fiscal.FdmsClock.CatNow().Date.AddDays(1), f.OperationId, ct);
                }
                catch (FdmsException ex) { f.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message; await _db.SaveChangesAsync(ct); break; }

                var item = st.FileStatus?.FirstOrDefault(i => i.OperationId == f.OperationId) ?? st.FileStatus?.FirstOrDefault();
                if (item == null) continue;
                if (item.FileProcessingStatus == "FileProcessingIsSuccessful")
                {
                    f.Status = OfflineFileStatus.Processed; f.ProcessedAt = DateTime.UtcNow; f.LastError = null;
                    foreach (var r in await _db.Receipts.Where(r => r.OfflineFileId == f.Id && r.Status == ReceiptStatus.OfflineIssued).ToListAsync(ct))
                    {
                        r.Status = ReceiptStatus.Submitted; r.ServerDate = FdmsClock.CatNow();
                        await _hooks.EnqueueAsync(r.ClientId, WebhookEvents.ReceiptFiscalised, r.ToDto(), ct);
                    }
                    finished++;
                }
                else if (item.FileProcessingStatus == "FileProcessingWithErrors")
                {
                    f.Status = OfflineFileStatus.ProcessedWithErrors; f.ProcessedAt = DateTime.UtcNow;
                    f.ErrorsJson = JsonSerializer.Serialize(item.FileProcessingErrorCode ?? new List<string>(), FdmsJson.Options);
                    await _hooks.EnqueueAsync(f.ClientId, WebhookEvents.OfflineFileFailed,
                        new { deviceId = f.DeviceId, fiscalDayNo = f.FiscalDayNo, sequence = f.Sequence, errors = item.FileProcessingErrorCode }, ct);
                    _log.LogError("Offline file {Day}/{Seq} of device {Id} processed with errors: {Errors}", f.FiscalDayNo, f.Sequence, device.ZimraDeviceId, f.ErrorsJson);
                    finished++;
                }
                // FileProcessingInProgress / WaitingForPreviousFile: look again next cycle
                await _db.SaveChangesAsync(ct);
            }
            return finished;
        }

        private static OfflineFileDto ToDto(OfflineFile f) => new(f.Id, f.FiscalDayNo, f.Sequence, f.ReceiptCount, f.HasFooter, f.Status.ToString(),
            f.OperationId, string.IsNullOrEmpty(f.ErrorsJson) ? Array.Empty<string>() : JsonSerializer.Deserialize<List<string>>(f.ErrorsJson, FdmsJson.Options)!,
            f.LastError, f.Attempts, f.UploadedAt, f.ProcessedAt);
    }

    /// <summary>Uploads queued offline files and checks their processing status (every minute).</summary>
    public sealed class OfflineFileJob : Microsoft.Extensions.Hosting.BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<OfflineFileJob> _log;
        public OfflineFileJob(IServiceScopeFactory scopes, ILogger<OfflineFileJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(60), stop);
                    using var scope = _scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<OfflineFileService>().RunAllAsync(stop);
                    if (n > 0) _log.LogInformation("{N} offline file(s) reached a final state.", n);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogError(ex, "Offline file cycle failed"); }
            }
        }
    }
}
