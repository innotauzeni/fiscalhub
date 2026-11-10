using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Application.Import;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Receipt pipeline: calculate → number → sign → persist (Pending) → send to FDMS → finalize.
    /// Numbers (receiptCounter, receiptGlobalNo, previous-hash chain) are consumed under the device lock and persisted
    /// BEFORE the FDMS call, so a timeout can be retried with the identical receipt (FDMS answers duplicates with the
    /// original result). A permanent FDMS rejection rolls the numbers back so the sequence stays gapless.
    /// </summary>
    public sealed class ReceiptService : IReceiptService
    {
        private readonly FdmsDbContext _db;
        private readonly IFdmsGateway _gw;
        private readonly IDeviceSigner _signer;
        private readonly DeviceAccessor _devices;
        private readonly DeviceLocks _locks;
        private readonly FiscalDayService _days;
        private readonly WebhookQueue _hooks;
        private readonly ILogger<ReceiptService> _log;
        private readonly ICallerScope _scope;
        private readonly InvoiceTemplateService _templates;

        public ReceiptService(FdmsDbContext db, IFdmsGateway gw, IDeviceSigner signer, DeviceAccessor devices, DeviceLocks locks,
            FiscalDayService days, WebhookQueue hooks, ICallerScope scope, InvoiceTemplateService templates, ILogger<ReceiptService> log)
        {
            _scope = scope; _templates = templates;
            _db = db; _gw = gw; _signer = signer; _devices = devices; _locks = locks; _days = days; _hooks = hooks; _log = log;
        }

        // ---- submit ----------------------------------------------------------------------------------------------

        public const int MaxHoldMinutes = 60;
        private static readonly JsonSerializerOptions HoldJson = new(FdmsJson.Options) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

        public async Task<ReceiptDto> SubmitAsync(Guid clientId, Guid deviceId, SubmitReceiptRequest req, CancellationToken ct)
        {
            ValidateRequest(req);
            if (req.HoldMinutes is < 0 or > MaxHoldMinutes)
                throw GatewayException.BadRequest("HOLD_INVALID", $"holdMinutes must be between 0 and {MaxHoldMinutes}.");
            var device = await _devices.GetAsync(clientId, deviceId, ct);
            if (!device.IsActive) throw GatewayException.Conflict("DEVICE_INACTIVE", "This device is disabled.");

            await using var gate = await _locks.AcquireAsync(device.Id, ct);

            // Idempotency: same (client, device, externalRef) returns the original receipt.
            var existing = await _db.Receipts.FirstOrDefaultAsync(
                r => r.ClientId == clientId && r.DeviceId == device.Id && r.ExternalRef == req.ExternalRef, ct);
            if (existing != null)
            {
                if (existing.Status == ReceiptStatus.Pending) await DeliverAsync(device, existing, ct);
                return existing.ToDto();
            }

            if (req.HoldMinutes > 0)
            {
                // Validate everything now so mistakes surface immediately, but consume no number and send nothing.
                var calc = CalculateFor(device, req, FdmsClock.CatNow());
                await ResolveOriginalAsync(device, clientId, req, calc, ct);
                var held = new Receipt
                {
                    ClientId = clientId, DeviceId = device.Id, ExternalRef = req.ExternalRef, InvoiceNo = req.InvoiceNo,
                    Type = calc.Type, Currency = calc.Currency, Total = calc.Total, OriginalReceiptId = req.OriginalReceiptId,
                    Status = ReceiptStatus.Held, HoldUntil = DateTime.UtcNow.AddMinutes(req.HoldMinutes),
                    ReceiptDate = FdmsClock.CatNow(),
                    RequestJson = JsonSerializer.Serialize(req with { HoldMinutes = 0 }, HoldJson),
                    Hash = "", Signature = "", PayloadJson = "",
                };
                _db.Receipts.Add(held);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "receipt.hold", Entity = "Receipt", EntityId = held.Id.ToString(), Detail = $"{held.InvoiceNo} {held.Currency} {held.Total:0.00} held {req.HoldMinutes} min on device {device.ZimraDeviceId}" });
                await _db.SaveChangesAsync(ct);
                return held.ToDto();
            }

            return await IssueAsync(device, clientId, req, null, ct);
        }

        /// <summary>
        /// Calculate, number, sign, persist and deliver. Caller holds the device lock. When <paramref name="held"/> is given its row is
        /// filled in (and kept, marked Rejected, if FDMS refuses it) instead of a new row being created.
        /// </summary>
        private async Task<ReceiptDto> IssueAsync(Device device, Guid clientId, SubmitReceiptRequest req, Receipt? held, CancellationToken ct)
        {
            var offline = device.OperatingMode == DeviceOperatingMode.Offline;

            // Anything left over from an FDMS outage goes first, in order.
            await DeliverPendingAsync(device, ct);

            var day = await _days.CurrentDayAsync(device.Id, ct);
            if (day == null)
            {
                if (!req.AutoOpenDay) throw GatewayException.Conflict("FISCAL_DAY_CLOSED", "No fiscal day is open for this device.");
                day = await _days.OpenUnlockedAsync(device, ct);
            }
            if (day.Status == FiscalDayStatus.FiscalDayCloseInitiated)
                throw GatewayException.Conflict("FISC_CLOSE_IN_PROGRESS", "The fiscal day is being closed.");

            // ---- calculate ----
            var date = NextReceiptDate(day);
            var calc = CalculateFor(device, req, date);
            var (originalReceipt, noteRef) = await ResolveOriginalAsync(device, clientId, req, calc, ct);

            // ---- number + sign ----
            var before = Snapshot(device, day);
            var counter = day.LastReceiptCounter + 1;
            var globalNo = device.LastReceiptGlobalNo + 1;
            var prevHash = counter > 1 ? day.LastReceiptHash : null;

            var toSign = SignatureStringBuilder.BuildReceiptString(new ReceiptSigningInput(
                device.ZimraDeviceId, calc.Type, calc.Currency, globalNo, date, calc.Total, calc.Taxes, prevHash));
            var sig = _signer.Sign(toSign, _devices.PrivateKey(device), device.CertificatePem);
            _log.LogInformation("Receipt sign string device={Device} counter={Counter}: {S}", device.ZimraDeviceId, counter, toSign);

            var payload = BuildPayload(req, calc, counter, globalNo, date, sig, noteRef);
            var receipt = held ?? new Receipt { ClientId = clientId, DeviceId = device.Id, ExternalRef = req.ExternalRef };
            receipt.FiscalDayId = day.Id;
            receipt.FiscalDayNo = day.FiscalDayNo;
            receipt.InvoiceNo = req.InvoiceNo;
            receipt.Type = calc.Type;
            receipt.Currency = calc.Currency;
            receipt.ReceiptCounter = counter;
            receipt.ReceiptGlobalNo = globalNo;
            receipt.ReceiptDate = date;
            receipt.Total = calc.Total;
            receipt.OriginalReceiptId = originalReceipt?.Id;
            receipt.Status = offline ? ReceiptStatus.OfflineIssued : ReceiptStatus.Pending;
            receipt.HoldUntil = null;
            receipt.RequestJson = null;
            receipt.Hash = sig.Hash;
            receipt.Signature = sig.Signature;
            receipt.PayloadJson = JsonSerializer.Serialize(payload, FdmsJson.Options);
            receipt.QrUrl = string.IsNullOrEmpty(device.QrUrl) ? null : QrCodeBuilder.Build(device.QrUrl, device.ZimraDeviceId, date, globalNo, sig.Signature);

            day.LastReceiptCounter = counter;
            day.LastReceiptHash = sig.Hash;
            day.LastReceiptDate = date;
            device.LastReceiptGlobalNo = globalNo;
            ApplyCounters(day, FiscalCounterCalculator.Merge(day.Counters.Select(c => c.ToCounter()), FiscalCounterCalculator.Deltas(calc)));
            if (held == null) _db.Receipts.Add(receipt);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = held != null ? "receipt.issue" : "receipt.submit", Entity = "Receipt", EntityId = receipt.Id.ToString(), Detail = $"{receipt.InvoiceNo} {receipt.Currency} {receipt.Total:0.00} #{globalNo} on device {device.ZimraDeviceId}" });
            await _db.SaveChangesAsync(ct);   // numbers are now committed

            if (offline) return receipt.ToDto();   // goes to FDMS inside the day's file (OfflineFileService)

            // ---- deliver ----
            try { await DeliverAsync(device, receipt, ct); }
            catch (FdmsException ex) when (!ex.IsTransient)
            {
                await RollBackAsync(device.Id, day.Id, receipt, before, ex, held != null, ct);
                throw GatewayException.Unprocessable(ex.ErrorCode ?? "FDMS_REJECTED", ex.Message);
            }
            return receipt.ToDto();
        }

        // ---- hold window -----------------------------------------------------------------------------------------

        public async Task<ReceiptDto> ReleaseAsync(Guid clientId, Guid receiptId, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, receiptId, ct);
            if (r.Status != ReceiptStatus.Held) return r.ToDto();
            var device = await _devices.GetAsync(clientId, r.DeviceId, ct);
            await using var gate = await _locks.AcquireAsync(device.Id, ct);
            await _db.Entry(r).ReloadAsync(ct);
            if (r.Status != ReceiptStatus.Held) return r.ToDto();
            return await IssueHeldAsync(device, r, ct);
        }

        public async Task<ReceiptDto> CancelAsync(Guid clientId, Guid receiptId, string? reason, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, receiptId, ct);
            var device = await _devices.GetAsync(clientId, r.DeviceId, ct);
            await using var gate = await _locks.AcquireAsync(device.Id, ct);
            await _db.Entry(r).ReloadAsync(ct);
            if (r.Status == ReceiptStatus.Cancelled) return r.ToDto();
            if (r.Status != ReceiptStatus.Held)
                throw GatewayException.Conflict("ALREADY_ISSUED", "This receipt has already been issued to ZIMRA and can only be corrected with a credit note.");

            r.Status = ReceiptStatus.Cancelled;
            r.HoldUntil = null;
            r.ErrorMessage = string.IsNullOrWhiteSpace(reason) ? "Cancelled before fiscalisation." : Truncate("Cancelled before fiscalisation: " + reason, 1000);
            // free the externalRef/invoice number for a corrected resubmission
            r.ExternalRef = Truncate(r.ExternalRef + "#cancelled-" + r.Id.ToString("N")[..8], 100);
            await _hooks.EnqueueAsync(r.ClientId, WebhookEvents.ReceiptCancelled, r.ToDto(), ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = r.ClientId, Action = "receipt.cancel", Entity = "Receipt", EntityId = r.Id.ToString(), Detail = r.InvoiceNo });
            await _db.SaveChangesAsync(ct);
            return r.ToDto();
        }

        public async Task<int> ReleaseDueHeldAsync(CancellationToken ct)
        {
            var due = await _db.Receipts.AsNoTracking().Where(r => r.Status == ReceiptStatus.Held && r.HoldUntil <= DateTime.UtcNow)
                .OrderBy(r => r.CreatedAt).Select(r => new { r.Id, r.DeviceId }).ToListAsync(ct);
            var n = 0;
            foreach (var d in due)
            {
                var gate = await _locks.TryAcquireAsync(d.DeviceId, ct);
                if (gate == null) continue;   // busy; next tick
                try
                {
                    var r = await _db.Receipts.FirstAsync(x => x.Id == d.Id, ct);
                    if (r.Status != ReceiptStatus.Held) continue;
                    await IssueHeldAsync(await _devices.GetByIdAsync(r.DeviceId, ct), r, ct);
                    n++;
                }
                catch (Exception ex) { _log.LogError(ex, "Releasing held receipt {Id} failed", d.Id); }
                finally { await gate.DisposeAsync(); }
            }
            return n;
        }

        /// <summary>Issues a held receipt. A business or FDMS rejection marks it Rejected (with the reason) and tells the client's webhook.</summary>
        private async Task<ReceiptDto> IssueHeldAsync(Device device, Receipt held, CancellationToken ct)
        {
            var req = JsonSerializer.Deserialize<SubmitReceiptRequest>(held.RequestJson!, HoldJson)!;
            try { return await IssueAsync(device, held.ClientId, req, held, ct); }
            catch (GatewayException ge)
            {
                _db.ChangeTracker.Clear();
                var r = await _db.Receipts.FirstAsync(x => x.Id == held.Id, ct);
                if (r.Status == ReceiptStatus.Rejected) return r.ToDto();   // FDMS refusal: already recorded and announced by the rollback
                r.Status = ReceiptStatus.Rejected;
                r.HoldUntil = null;
                r.ErrorCode = ge.Code;
                r.ErrorMessage = Truncate(ge.Message, 1000);
                await _hooks.EnqueueAsync(r.ClientId, WebhookEvents.ReceiptFailed, r.ToDto(), ct);
                await _db.SaveChangesAsync(ct);
                return r.ToDto();
            }
        }

        // ---- delivery --------------------------------------------------------------------------------------------

        /// <summary>Sends one Pending receipt. Transient failures leave it Pending; permanent ones throw. Caller holds the device lock.</summary>
        private async Task DeliverAsync(Device device, Receipt receipt, CancellationToken ct)
        {
            if (receipt.Status != ReceiptStatus.Pending || device.OperatingMode == DeviceOperatingMode.Offline) return;
            var payload = JsonSerializer.Deserialize<ReceiptPayload>(receipt.PayloadJson, FdmsJson.Options)!;
            try
            {
                var res = await _gw.SubmitReceiptAsync(_devices.Credentials(device), payload, ct);
                receipt.Status = ReceiptStatus.Submitted;
                receipt.FdmsReceiptId = res.ReceiptId;
                receipt.ServerDate = res.ServerDate;
                receipt.ServerSignatureJson = res.ReceiptServerSignature.ValueKind == JsonValueKind.Undefined ? null : res.ReceiptServerSignature.GetRawText();
                receipt.ValidationErrorsJson = res.ValidationErrors is { Count: > 0 }
                    ? JsonSerializer.Serialize(res.ValidationErrors.Select(v => new ValidationErrorView(v.ValidationErrorCode, v.ValidationErrorColor)), FdmsJson.Options)
                    : null;
                receipt.ErrorCode = null;
                receipt.ErrorMessage = null;
                if (res.ValidationErrors is { Count: > 0 })
                    _log.LogWarning("Receipt {Id} accepted with validation errors: {Errors}", receipt.Id, receipt.ValidationErrorsJson);
                await _hooks.EnqueueAsync(receipt.ClientId, WebhookEvents.ReceiptFiscalised, receipt.ToDto(), ct);
                await _db.SaveChangesAsync(ct);
            }
            catch (FdmsException ex) when (ex.IsTransient)
            {
                receipt.ErrorCode = ex.ErrorCode ?? "FDMS_UNAVAILABLE";
                receipt.ErrorMessage = Truncate(ex.Message, 1000);
                await _db.SaveChangesAsync(ct);
                _log.LogWarning("Receipt {Id} left Pending: {Msg}", receipt.Id, ex.Message);
            }
        }

        private async Task DeliverPendingAsync(Device device, CancellationToken ct)
        {
            if (device.OperatingMode == DeviceOperatingMode.Offline) return;   // offline receipts travel in files
            var pending = await _db.Receipts.Where(r => r.DeviceId == device.Id && r.Status == ReceiptStatus.Pending)
                .OrderBy(r => r.ReceiptGlobalNo).ToListAsync(ct);
            foreach (var p in pending)
            {
                try { await DeliverAsync(device, p, ct); }
                catch (FdmsException ex)
                {
                    // Permanent rejection of an already-numbered receipt cannot be rolled back (later numbers may exist).
                    p.Status = ReceiptStatus.Rejected;
                    p.ErrorCode = ex.ErrorCode ?? "FDMS_REJECTED";
                    p.ErrorMessage = Truncate(ex.Message, 1000);
                    await _hooks.EnqueueAsync(p.ClientId, WebhookEvents.ReceiptFailed, p.ToDto(), ct);
                    await _db.SaveChangesAsync(ct);
                    _log.LogError(ex, "Pending receipt {Id} was rejected by FDMS; device sequence needs attention.", p.Id);
                    throw GatewayException.Conflict("SEQUENCE_BROKEN", $"Earlier receipt {p.InvoiceNo} was rejected by FDMS ({p.ErrorCode}). Contact support before issuing more receipts.");
                }
                if (p.Status == ReceiptStatus.Pending)
                    throw GatewayException.Conflict("FDMS_UNAVAILABLE", "FDMS is unreachable and earlier receipts are still queued; try again shortly.");
            }
        }

        public async Task<int> RetryPendingAsync(CancellationToken ct)
        {
            var deviceIds = await _db.Receipts.Where(r => r.Status == ReceiptStatus.Pending).Select(r => r.DeviceId).Distinct().ToListAsync(ct);
            var delivered = 0;
            foreach (var id in deviceIds)
            {
                var gate = await _locks.TryAcquireAsync(id, ct);
                if (gate == null) continue;   // busy with a live request; next tick
                try
                {
                    var device = await _devices.GetByIdAsync(id, ct);
                    var pending = await _db.Receipts.Where(r => r.DeviceId == id && r.Status == ReceiptStatus.Pending)
                        .OrderBy(r => r.ReceiptGlobalNo).ToListAsync(ct);
                    foreach (var p in pending)
                    {
                        try { await DeliverAsync(device, p, ct); }
                        catch (FdmsException ex) { p.Status = ReceiptStatus.Rejected; p.ErrorCode = ex.ErrorCode; p.ErrorMessage = Truncate(ex.Message, 1000); await _hooks.EnqueueAsync(p.ClientId, WebhookEvents.ReceiptFailed, p.ToDto(), ct); await _db.SaveChangesAsync(ct); break; }
                        if (p.Status == ReceiptStatus.Pending) break;   // still down; keep order
                        delivered++;
                    }
                }
                catch (Exception ex) { _log.LogError(ex, "Retrying pending receipts for device {Id} failed", id); }
                finally { await gate.DisposeAsync(); }
            }
            return delivered;
        }

        public async Task<ReceiptDto> RetryAsync(Guid clientId, Guid receiptId, CancellationToken ct)
        {
            var receipt = await LoadAsync(clientId, receiptId, ct);
            var device = await _devices.GetAsync(clientId, receipt.DeviceId, ct);
            var gate = await _locks.AcquireAsync(device.Id, ct);
            try
            {
                await _db.Entry(receipt).ReloadAsync(ct);
                if (receipt.Status == ReceiptStatus.Rejected)
                    throw GatewayException.Conflict("RECEIPT_REJECTED", "This receipt was rejected by FDMS and cannot be retried.");
                await DeliverPendingAsync(device, ct);
                await _db.Entry(receipt).ReloadAsync(ct);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "receipt.retry", Entity = "Receipt", EntityId = receipt.Id.ToString(), Detail = $"{receipt.InvoiceNo} now {receipt.Status}" });
                await _db.SaveChangesAsync(ct);
                return receipt.ToDto();
            }
            finally { await gate.DisposeAsync(); }
        }

        // ---- queries ---------------------------------------------------------------------------------------------

        public async Task<ReceiptDto> GetAsync(Guid clientId, Guid receiptId, CancellationToken ct) =>
            (await LoadAsync(clientId, receiptId, ct)).ToDto();

        public async Task<byte[]> GetQrPngAsync(Guid clientId, Guid receiptId, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, receiptId, ct);
            if (string.IsNullOrEmpty(r.QrUrl)) throw GatewayException.Conflict("NO_QR", "This receipt has no QR URL (device config missing).");
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(r.QrUrl, QRCodeGenerator.ECCLevel.M);
            return new PngByteQRCode(data).GetGraphic(8);
        }

        private async Task<Receipt> LoadAsync(Guid clientId, Guid receiptId, CancellationToken ct) =>
            await _db.Receipts.InScope(_scope).FirstOrDefaultAsync(r => r.Id == receiptId && r.ClientId == clientId, ct)
            ?? throw GatewayException.NotFound("Receipt");

        // ---- preview / list / pdf --------------------------------------------------------------------------------

        /// <summary>Runs exactly the calculation and validation a real submission would, without numbering or sending anything.</summary>
        public async Task<CalculatedReceiptView> PreviewAsync(Guid clientId, Guid deviceId, SubmitReceiptRequest req, CancellationToken ct)
        {
            ValidateRequest(req);
            var device = await _devices.GetAsync(clientId, deviceId, ct, track: false);
            var calc = CalculateFor(device, req, FdmsClock.CatNow());
            return new CalculatedReceiptView(calc.Type.ToString(), calc.Currency, calc.LinesTaxInclusive, calc.Total,
                calc.Lines.Select(l => new CalculatedLineView(l.LineNo, l.Name, l.HsCode, l.Price, l.Quantity, l.Total, l.TaxId, l.TaxPercent)).ToList(),
                calc.Taxes.Select(t => new CalculatedTaxView(t.TaxId, t.TaxPercent, t.TaxAmount, t.SalesAmountWithTax)).ToList(),
                calc.Payments.Select(p => new CalculatedPaymentView(p.MoneyType.ToString(), p.Amount)).ToList());
        }

        public async Task<PagedResult<ReceiptDto>> ListAsync(Guid clientId, ReceiptListFilter f, CancellationToken ct)
        {
            var q = _db.Receipts.AsNoTracking().InScope(_scope).Where(r => r.ClientId == clientId);
            if (f.DeviceId.HasValue) q = q.Where(r => r.DeviceId == f.DeviceId.Value);
            if (!string.IsNullOrWhiteSpace(f.Status) && Enum.TryParse<ReceiptStatus>(f.Status, true, out var st)) q = q.Where(r => r.Status == st);
            if (f.FiscalDayNo.HasValue) q = q.Where(r => r.FiscalDayNo == f.FiscalDayNo.Value);
            if (f.HasErrors == true) q = q.Where(r => r.ValidationErrorsJson != null && r.ValidationErrorsJson != "" && r.ValidationErrorsJson != "[]");
            if (f.From.HasValue) q = q.Where(r => r.ReceiptDate >= f.From.Value);
            if (f.To.HasValue) q = q.Where(r => r.ReceiptDate < f.To.Value.Date.AddDays(1));
            if (!string.IsNullOrWhiteSpace(f.Search))
            {
                var s = f.Search.Trim();
                q = q.Where(r => r.InvoiceNo.Contains(s) || r.ExternalRef.Contains(s));
            }
            var page = Math.Max(1, f.Page);
            var size = Math.Clamp(f.PageSize, 1, 200);
            var total = await q.CountAsync(ct);
            var items = await q.OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.ReceiptGlobalNo).Skip((page - 1) * size).Take(size).ToListAsync(ct);
            return new PagedResult<ReceiptDto>(items.Select(r => r.ToDto()).ToList(), page, size, total);
        }

        // ---- review and correct --------------------------------------------------------------------------------------

        public async Task<ReceiptDetailDto> GetDetailAsync(Guid clientId, Guid receiptId, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, receiptId, ct);
            if (string.IsNullOrEmpty(r.PayloadJson)) throw GatewayException.Conflict("RECEIPT_NOT_ISSUED", "This receipt has not been issued yet, so there is nothing sent to ZIMRA to show.");
            var p = JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options)!;
            var creditId = await _db.Receipts.AsNoTracking().Where(x => x.OriginalReceiptId == r.Id && x.Type == ReceiptType.CreditNote && x.Status != ReceiptStatus.Rejected)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            return new ReceiptDetailDto(r.ToDto(), p.ReceiptLines, p.ReceiptPayments, p.ReceiptLinesTaxInclusive, p.ReceiptNotes, p.BuyerData, creditId);
        }

        public async Task<CorrectionResult> CorrectAsync(Guid clientId, Guid receiptId, CorrectReceiptRequest req, CancellationToken ct)
        {
            var original = await LoadAsync(clientId, receiptId, ct);
            if (original.Type != ReceiptType.FiscalInvoice) throw GatewayException.Conflict("CORRECT_INVOICE_ONLY", "Only a fiscal invoice can be corrected this way.");
            if (original.Status != ReceiptStatus.Submitted) throw GatewayException.Conflict("RECEIPT_NOT_ACCEPTED", "Only an invoice already accepted by ZIMRA can be corrected with a credit note.");
            var reason = req.Reason?.Trim() ?? "";
            if (reason.Length < 3) throw GatewayException.BadRequest("REASON_REQUIRED", "Say why the invoice is being corrected (it is printed on the credit note).");
            if (reason.Length > 200) throw GatewayException.BadRequest("REASON_TOO_LONG", "The reason may be at most 200 characters.");

            var p = JsonSerializer.Deserialize<ReceiptPayload>(original.PayloadJson, FdmsJson.Options)!;
            var fixes = (req.Fixes ?? Array.Empty<LineFix>()).Where(f => !string.IsNullOrWhiteSpace(f.HsCode)).ToDictionary(f => f.LineNo, f => f.HsCode!.Trim());
            if (fixes.Keys.Any(n => p.ReceiptLines.All(l => l.ReceiptLineNo != n))) throw GatewayException.BadRequest("LINE_UNKNOWN", "One of the corrections names a line that is not on the invoice.");

            // the same lines, payments and buyer as the original, with only the listed HS codes replaced
            var lines = p.ReceiptLines.Select(l => new ReceiptLineRequest(l.ReceiptLineName, fixes.TryGetValue(l.ReceiptLineNo, out var hs) ? hs : l.ReceiptLineHSCode,
                l.ReceiptLineQuantity, Math.Abs(l.ReceiptLinePrice), TaxId: l.TaxId, IsDiscount: l.ReceiptLineType == "Discount")).ToList();
            var payments = p.ReceiptPayments.Select(x => new ReceiptPaymentRequest(Enum.Parse<MoneyType>(x.MoneyTypeCode, true), Math.Abs(x.PaymentAmount))).ToList();
            var buyer = p.BuyerData == null ? null : new BuyerRequest(p.BuyerData.BuyerRegisterName, p.BuyerData.BuyerTradeName, p.BuyerData.BuyerTin, p.BuyerData.VatNumber,
                p.BuyerData.BuyerContacts?.Email, p.BuyerData.BuyerContacts?.PhoneNo);

            var creditRef = $"fix-{original.Id:N}";
            var existing = await _db.Receipts.AsNoTracking().FirstOrDefaultAsync(x => x.OriginalReceiptId == original.Id && x.Type == ReceiptType.CreditNote && x.Status != ReceiptStatus.Rejected, ct);
            if (existing != null && existing.ExternalRef != creditRef) throw GatewayException.Conflict("ALREADY_CORRECTED", "A credit note already exists for this invoice.");

            ReceiptDto credit;
            if (existing != null) credit = existing.ToDto();   // a repeat after the replacement failed: do not credit twice
            else
            {
                var creditNo = ("CN-" + original.InvoiceNo); if (creditNo.Length > 50) creditNo = creditNo[..50];
                credit = await SubmitAsync(clientId, original.DeviceId, new SubmitReceiptRequest(creditRef, creditNo, ReceiptType.CreditNote, original.Currency, p.ReceiptLinesTaxInclusive, lines,
                    payments, buyer, "Correction: " + reason, original.Id, AutoOpenDay: true), ct);
            }

            ReceiptDto? replacement = null;
            if (req.IssueReplacement)
            {
                var newNo = string.IsNullOrWhiteSpace(req.ReplacementInvoiceNo) ? original.InvoiceNo + "-R" : req.ReplacementInvoiceNo.Trim();
                if (newNo.Length > 50) throw GatewayException.BadRequest("INVOICE_NO_TOO_LONG", "invoiceNo must be at most 50 characters.");
                replacement = await SubmitAsync(clientId, original.DeviceId, new SubmitReceiptRequest($"{creditRef}-new", newNo, ReceiptType.FiscalInvoice, original.Currency, p.ReceiptLinesTaxInclusive, lines,
                    payments, buyer, "Replaces " + original.InvoiceNo, null, AutoOpenDay: true), ct);
            }

            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "receipt.correct", Entity = "Receipt", EntityId = original.Id.ToString(),
                Detail = $"{original.InvoiceNo}: credit note {credit.InvoiceNo}" + (replacement != null ? $", replacement {replacement.InvoiceNo}" : "") + $" - {reason}" });
            await _db.SaveChangesAsync(ct);
            return new CorrectionResult(credit, replacement);
        }

        public async Task<ReceiptDto> IssueDebitNoteAsync(Guid clientId, Guid receiptId, DebitNoteRequest req, CancellationToken ct)
        {
            var original = await LoadAsync(clientId, receiptId, ct);
            if (original.Type != ReceiptType.FiscalInvoice) throw GatewayException.Conflict("DEBIT_INVOICE_ONLY", "A debit note can only be raised against a fiscal invoice.");
            if (original.Status != ReceiptStatus.Submitted) throw GatewayException.Conflict("RECEIPT_NOT_ACCEPTED", "Only an invoice already accepted by ZIMRA can be given a debit note.");
            var reason = req.Reason?.Trim() ?? "";
            if (reason.Length < 3) throw GatewayException.BadRequest("REASON_REQUIRED", "Say why the debit note is raised (it is printed on it).");
            if (reason.Length > 200) throw GatewayException.BadRequest("REASON_TOO_LONG", "The reason may be at most 200 characters.");
            if (req.Lines == null || req.Lines.Count == 0) throw GatewayException.Unprocessable("RCPT016", "Add at least one line: what is being added to the invoice.");
            if (req.Lines.Any(l => l.IsDiscount || l.Quantity <= 0 || l.UnitPrice <= 0)) throw GatewayException.BadRequest("LINE_INVALID", "Debit note lines must have a positive quantity and price.");

            var p = JsonSerializer.Deserialize<ReceiptPayload>(original.PayloadJson, FdmsJson.Options)!;
            var buyer = p.BuyerData == null ? null : new BuyerRequest(p.BuyerData.BuyerRegisterName, p.BuyerData.BuyerTradeName, p.BuyerData.BuyerTin, p.BuyerData.VatNumber,
                p.BuyerData.BuyerContacts?.Email, p.BuyerData.BuyerContacts?.PhoneNo);

            // the same note sent twice must not add the charge twice: the reference is derived from the invoice and the content
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(reason + "|" + JsonSerializer.Serialize(req.Lines) + "|" + req.MoneyType))).ToLowerInvariant()[..10];
            var extRef = $"debit-{original.Id:N}-{fingerprint}";
            var existing = await _db.Receipts.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId && x.ExternalRef == extRef && x.Status != ReceiptStatus.Rejected, ct);
            if (existing != null) return existing.ToDto();

            var count = await _db.Receipts.AsNoTracking().CountAsync(x => x.OriginalReceiptId == original.Id && x.Type == ReceiptType.DebitNote, ct);
            var no = string.IsNullOrWhiteSpace(req.InvoiceNo) ? "DN-" + original.InvoiceNo + (count > 0 ? "-" + (count + 1) : "") : req.InvoiceNo.Trim();
            if (no.Length > 50) throw GatewayException.BadRequest("INVOICE_NO_TOO_LONG", "invoiceNo must be at most 50 characters.");

            // the total is worked out first so the payment can be stated for exactly that amount
            var draft = new SubmitReceiptRequest(extRef, no, ReceiptType.DebitNote, original.Currency, p.ReceiptLinesTaxInclusive, req.Lines, null, buyer, "Debit note: " + reason, original.Id, AutoOpenDay: true);
            IReadOnlyList<ReceiptPaymentRequest>? payments = null;
            if (req.MoneyType != null && req.MoneyType != MoneyType.Cash)
            {
                var view = await PreviewAsync(clientId, original.DeviceId, draft, ct);
                payments = new[] { new ReceiptPaymentRequest(req.MoneyType.Value, view.Total) };
            }

            var note = await SubmitAsync(clientId, original.DeviceId, draft with { Payments = payments }, ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "receipt.debit", Entity = "Receipt", EntityId = original.Id.ToString(), Detail = $"{original.InvoiceNo}: debit note {note.InvoiceNo} {note.Total:0.00} {note.Currency} - {reason}" });
            await _db.SaveChangesAsync(ct);
            return note;
        }

        public async Task<byte[]> GetPdfAsync(Guid clientId, Guid receiptId, bool a4, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, receiptId, ct);
            var device = await _devices.GetAsync(clientId, r.DeviceId, ct, track: false);
            var (settings, logo) = await _templates.ResolveAsync(clientId, device.BranchId, ct);   // the device's branch template, else the company default
            return ReceiptPdfRenderer.Render(r, device, a4 ? ReceiptPdfFormat.InvoiceA4 : ReceiptPdfFormat.Receipt48, settings, logo);
        }

        public async Task<byte[]> GetDocxAsync(Guid clientId, Guid receiptId, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, receiptId, ct);
            var device = await _devices.GetAsync(clientId, r.DeviceId, ct, track: false);
            var (settings, logo) = await _templates.ResolveAsync(clientId, device.BranchId, ct);
            var doc = Templates.InvoiceDocumentFactory.Build(r, device, settings);
            return Templates.CanvasDocx.Build(doc, settings.Canvas, logo, settings.Assets, Templates.CanvasPdfRenderer.Qr(r.QrUrl));
        }

        public async Task<InvoiceDocumentDto> GetDocumentAsync(Guid clientId, Guid receiptId, CancellationToken ct)
        {
            var r = await LoadAsync(clientId, receiptId, ct);
            var device = await _devices.GetAsync(clientId, r.DeviceId, ct, track: false);
            var (settings, _) = await _templates.ResolveAsync(clientId, device.BranchId, ct);
            return Templates.InvoiceDocumentFactory.Build(r, device, settings);
        }

        /// <summary>The single place that turns a request into FDMS-valid lines, taxes and total (used by preview and submit).</summary>
        private CalculatedReceipt CalculateFor(Device device, SubmitReceiptRequest req, DateTime date)
        {
            var taxes = Mappers.ParseTaxes(device.TaxesJson)
                .Where(t => t.TaxValidFrom <= date && (t.TaxValidTill == null || t.TaxValidTill >= date))
                .ToDictionary(t => t.TaxId, t => new TaxDefinition(t.TaxId, null, t.TaxPercent));
            if (taxes.Count == 0)
                throw GatewayException.Conflict("NO_TAXES", "Device has no tax configuration; call refresh-config first.");

            if (!string.IsNullOrEmpty(device.VatNumber) && req.Lines.Any(l => string.IsNullOrWhiteSpace(l.HsCode)))
                throw GatewayException.Unprocessable("RCPT047", "HS code is required on every line for a VAT-registered taxpayer.");

            try
            {
                return ReceiptCalculator.Calculate(req.ReceiptType, req.Currency, req.LinesTaxInclusive,
                    req.Lines.Select((l, i) => new LineInput(l.Name, l.HsCode, l.Quantity, l.UnitPrice, ResolveTaxId(l, i + 1, taxes),
                        l.IsDiscount ? ReceiptLineType.Discount : ReceiptLineType.Sale)).ToList(),
                    req.Payments?.Select(p => new PaymentInput(p.MoneyType, p.Amount)).ToList(), taxes);
            }
            catch (ReceiptCalculationException ex) { throw GatewayException.Unprocessable(ex.Code, ex.Message); }
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        /// <summary>Maps a caller line to a device tax: explicit taxId, else exempt, else by percentage.</summary>
        private static int ResolveTaxId(ReceiptLineRequest l, int lineNo, IReadOnlyDictionary<int, TaxDefinition> taxes)
        {
            if (l.TaxId.HasValue) return l.TaxId.Value;
            if (l.IsExempt)
                return taxes.Values.Where(t => t.Percent == null).OrderBy(t => t.TaxId).Select(t => (int?)t.TaxId).FirstOrDefault()
                       ?? throw GatewayException.Unprocessable("RCPT025", $"Line {lineNo}: this device has no exempt tax configured.");
            if (l.TaxPercent.HasValue)
                return taxes.Values.Where(t => t.Percent == l.TaxPercent.Value).OrderBy(t => t.TaxId).Select(t => (int?)t.TaxId).FirstOrDefault()
                       ?? throw GatewayException.Unprocessable("RCPT025", $"Line {lineNo}: no tax of {l.TaxPercent.Value}% is configured for this device (valid: {string.Join(", ", taxes.Values.Select(t => t.Percent?.ToString("0.##") ?? "exempt"))}).");
            throw GatewayException.BadRequest("TAX_REQUIRED", $"Line {lineNo}: give taxId, taxPercent or isExempt.");
        }

        private static void ValidateRequest(SubmitReceiptRequest r)
        {
            if (string.IsNullOrWhiteSpace(r.ExternalRef)) throw GatewayException.BadRequest("EXTERNAL_REF_REQUIRED", "externalRef is required (it makes retries safe).");
            if (r.ExternalRef.Length > 100) throw GatewayException.BadRequest("EXTERNAL_REF_TOO_LONG", "externalRef must be at most 100 characters.");
            if (string.IsNullOrWhiteSpace(r.InvoiceNo)) throw GatewayException.BadRequest("INVOICE_NO_REQUIRED", "invoiceNo is required.");
            if (r.InvoiceNo.Length > 50) throw GatewayException.BadRequest("INVOICE_NO_TOO_LONG", "invoiceNo must be at most 50 characters.");
            if (string.IsNullOrWhiteSpace(r.Currency) || r.Currency.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "currency must be a 3-letter ISO 4217 code.");
            if (r.Lines == null || r.Lines.Count == 0) throw GatewayException.Unprocessable("RCPT016", "At least one line is required.");
            if (r.Lines.Any(l => string.IsNullOrWhiteSpace(l.Name) || l.Name.Length > 200)) throw GatewayException.BadRequest("LINE_NAME_INVALID", "Each line needs a name of at most 200 characters.");
            // ZIMRA answers a wrong-length HS code with RCPT048 only AFTER the receipt has been numbered, so stop it here instead.
            for (var i = 0; i < r.Lines.Count; i++)
            {
                var hs = r.Lines[i].HsCode?.Trim();
                if (!string.IsNullOrEmpty(hs) && !Regex.IsMatch(hs, @"^(\d{4}|\d{6}|\d{8})$"))
                    throw GatewayException.Unprocessable("RCPT048", $"Line {i + 1}: HS code \"{hs}\" has the wrong length - use 4, 6 or 8 digits (ZIMRA tariff codes are 8).");
            }
            if (r.ReceiptType != ReceiptType.FiscalInvoice)
            {
                if (r.OriginalReceiptId == null) throw GatewayException.Unprocessable("RCPT015", "originalReceiptId is required for credit and debit notes.");
                if (string.IsNullOrWhiteSpace(r.Notes)) throw GatewayException.Unprocessable("RCPT034", "notes (the reason) are required for credit and debit notes.");
            }
            else if (r.OriginalReceiptId != null)
                throw GatewayException.Unprocessable("RCPT029", "originalReceiptId must only be sent for credit and debit notes.");
        }

        /// <summary>RCPT030/RCPT014: dates must increase; the clock is Zimbabwe time regardless of the server.</summary>
        private static DateTime NextReceiptDate(FiscalDay day)
        {
            var now = FdmsClock.CatNow();
            now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
            var floor = (day.LastReceiptDate ?? day.OpenedAt).AddSeconds(1);
            return now < floor ? floor : now;
        }

        private async Task<(Receipt? Original, CreditDebitNoteDto? Ref)> ResolveOriginalAsync(Device device, Guid clientId, SubmitReceiptRequest req, CalculatedReceipt calc, CancellationToken ct)
        {
            if (req.ReceiptType == ReceiptType.FiscalInvoice) return (null, null);

            var original = await _db.Receipts.AsNoTracking().InScope(_scope).FirstOrDefaultAsync(r => r.Id == req.OriginalReceiptId && r.ClientId == clientId, ct)
                           ?? throw GatewayException.Unprocessable("RCPT032", "The original receipt does not exist.");
            if (original.Type != ReceiptType.FiscalInvoice || original.Status != ReceiptStatus.Submitted)
                throw GatewayException.Unprocessable("RCPT032", "The original must be a fiscal invoice already accepted by FDMS (an offline invoice counts once its file has been processed).");
            if (!string.Equals(original.Currency, calc.Currency, StringComparison.OrdinalIgnoreCase))
                throw GatewayException.Unprocessable("RCPT043", "Credit/debit notes must use the original invoice's currency.");
            if (original.ReceiptDate < FdmsClock.CatNow().AddMonths(-12))
                throw GatewayException.Unprocessable("RCPT033", "The original invoice was issued more than 12 months ago.");

            var originalPayload = JsonSerializer.Deserialize<ReceiptPayload>(original.PayloadJson, FdmsJson.Options)!;
            var allowed = originalPayload.ReceiptTaxes.Select(t => t.TaxId).ToHashSet();
            if (calc.Taxes.Any(t => !allowed.Contains(t.TaxId)))
                throw GatewayException.Unprocessable("RCPT036", "The note uses a tax that was not on the original invoice.");

            if (req.ReceiptType == ReceiptType.CreditNote)
            {
                var notes = await _db.Receipts.AsNoTracking()
                    .Where(r => r.OriginalReceiptId == original.Id && r.Status != ReceiptStatus.Rejected).Select(r => r.Total).ToListAsync(ct);
                var remaining = original.Total + notes.Sum();   // credits are negative, debits positive
                if (remaining + calc.Total < 0)
                    throw GatewayException.Unprocessable("RCPT035", $"Credit exceeds what remains of the original invoice ({remaining:0.00}).");
            }
            // receiptID when FDMS gave us one; offline-issued invoices are referenced by device + global no + fiscal day instead
            var originalDevice = original.DeviceId == device.Id ? device : await _devices.GetByIdAsync(original.DeviceId, ct);
            return (original, new CreditDebitNoteDto(original.FdmsReceiptId, original.FdmsReceiptId == null ? originalDevice.ZimraDeviceId : null,
                original.ReceiptGlobalNo, original.FiscalDayNo, original.ReceiptDate.ToString("yyyy-MM-ddTHH:mm:ss")));
        }

        private static ReceiptPayload BuildPayload(SubmitReceiptRequest req, CalculatedReceipt calc, int counter, long globalNo,
            DateTime date, SignatureResult sig, CreditDebitNoteDto? note)
        {
            BuyerDto? buyer = req.Buyer == null ? null : new BuyerDto(req.Buyer.RegisterName, req.Buyer.TradeName, req.Buyer.Tin, req.Buyer.VatNumber,
                string.IsNullOrWhiteSpace(req.Buyer.Email) && string.IsNullOrWhiteSpace(req.Buyer.Phone) ? null : new FdmsContacts(req.Buyer.Phone, req.Buyer.Email), null);

            return new ReceiptPayload(
                calc.Type.ToString(), calc.Currency, counter, globalNo, req.InvoiceNo, buyer, req.Notes,
                date.ToString("yyyy-MM-ddTHH:mm:ss"), note, calc.LinesTaxInclusive,
                calc.Lines.Select(l => new ReceiptLineDto(l.LineType.ToString(), l.LineNo, l.HsCode, l.Name, l.Price, l.Quantity, l.Total, l.TaxCode, l.TaxPercent, l.TaxId)).ToList(),
                calc.Taxes.OrderBy(t => t.TaxId).Select(t => new ReceiptTaxDto(t.TaxCode, t.TaxPercent, t.TaxId, t.TaxAmount, t.SalesAmountWithTax)).ToList(),
                calc.Payments.Select(p => new ReceiptPaymentDto(p.MoneyType.ToString(), p.Amount)).ToList(),
                calc.Total, ReceiptPrintForm.Receipt48.ToString(), new SignatureDto(sig.Hash, sig.Signature), req.Username, null);
        }

        private static void ApplyCounters(FiscalDay day, IReadOnlyList<FiscalCounter> counters)
        {
            day.Counters.Clear();
            foreach (var c in counters)
                day.Counters.Add(new FiscalDayCounterRow { Type = c.Type, Currency = c.Currency, TaxId = c.TaxId, TaxPercent = c.TaxPercent, MoneyType = c.MoneyType, Value = c.Value });
        }

        private sealed record Snap(int Counter, string? Hash, DateTime? Date, long GlobalNo, IReadOnlyList<FiscalCounter> Counters);

        private static Snap Snapshot(Device d, FiscalDay day) =>
            new(day.LastReceiptCounter, day.LastReceiptHash, day.LastReceiptDate, d.LastReceiptGlobalNo, day.Counters.Select(c => c.ToCounter()).ToList());

        /// <summary>FDMS permanently refused the receipt: give its numbers back and drop the row (the sequence must stay gapless).</summary>
        private async Task RollBackAsync(Guid deviceId, int dayId, Receipt receipt, Snap before, FdmsException ex, bool keepRow, CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var device = await _db.Devices.FirstAsync(d => d.Id == deviceId, ct);
            var day = await _db.FiscalDays.Include(d => d.Counters).FirstAsync(d => d.Id == dayId, ct);
            day.LastReceiptCounter = before.Counter;
            day.LastReceiptHash = before.Hash;
            day.LastReceiptDate = before.Date;
            device.LastReceiptGlobalNo = before.GlobalNo;
            ApplyCounters(day, before.Counters);
            var row = await _db.Receipts.FirstAsync(r => r.Id == receipt.Id, ct);
            if (keepRow)
            {
                // a held receipt keeps its row so the client can see why it failed; its numbers are given back
                row.Status = ReceiptStatus.Rejected; row.ErrorCode = ex.ErrorCode ?? "FDMS_REJECTED"; row.ErrorMessage = Truncate(ex.Message, 1000);
                row.ReceiptCounter = 0; row.ReceiptGlobalNo = 0; row.Hash = ""; row.Signature = ""; row.PayloadJson = ""; row.QrUrl = null;
                await _hooks.EnqueueAsync(row.ClientId, WebhookEvents.ReceiptFailed, row.ToDto(), ct);
            }
            else _db.Receipts.Remove(row);
            _db.AuditLogs.Add(new AuditLog { ClientId = receipt.ClientId, Action = "receipt.rejected",
                Detail = Truncate($"extRef={receipt.ExternalRef} invoice={receipt.InvoiceNo} {ex.ErrorCode} {ex.Message}", 2000) });
            await _db.SaveChangesAsync(ct);
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
