using Fdms.Api.Security;
using Fdms.Application.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Features;
using Fdms.Application.Import;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>Register and manage fiscal devices. All routes need a client API key (X-Api-Key).</summary>
    [ApiController]
    [Authorize]
    [Route("api/v1/devices")]
    public class DevicesController : ControllerBase
    {
        private readonly IMediator _m;
        public DevicesController(IMediator m) => _m = m;

        /// <summary>Check the taxpayer behind an activation key before registering (nothing is changed).</summary>
        [HasPermission(Perm.DevicesManage)]
        [HttpPost("verify")]
        public async Task<IActionResult> Verify(DeviceIdentityRequest body, CancellationToken ct) =>
            Ok(await _m.Send(new VerifyDeviceCommand(body), ct));

        /// <summary>Generate keys, register the device with ZIMRA and load its configuration.</summary>
        [HasPermission(Perm.DevicesManage)]
        [HttpPost("register")]
        public async Task<IActionResult> Register(DeviceIdentityRequest body, CancellationToken ct)
        {
            var d = await _m.Send(new RegisterDeviceCommand(User.ClientId(), body), ct);
            return CreatedAtAction(nameof(Get), new { id = d.Id }, d);
        }

        [HasPermission(Perm.DevicesView)]
        [HttpGet]
        public async Task<IActionResult> List(CancellationToken ct) => Ok(await _m.Send(new ListDevicesQuery(User.ClientId()), ct));

        [HasPermission(Perm.DevicesView)]
        [HttpGet("{id:guid}")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await _m.Send(new GetDeviceQuery(User.ClientId(), id), ct));

        public sealed record RenameBody(string? Name);

        /// <summary>The company's own name for a machine ("Till 2, front counter"), so the machines of one branch can be told apart.</summary>
        [HasPermission(Perm.DevicesManage)]
        [HttpPut("{id:guid}/name")]
        public async Task<IActionResult> Rename(Guid id, RenameBody body, [FromServices] IDeviceService devices, CancellationToken ct) => Ok(await devices.RenameAsync(User.ClientId(), id, body.Name, ct));

        /// <summary>Re-read taxes, QR url and certificate expiry from FDMS (getConfig).</summary>
        [HasPermission(Perm.DevicesManage)]
        [HttpPost("{id:guid}/refresh-config")]
        public async Task<IActionResult> RefreshConfig(Guid id, CancellationToken ct) =>
            Ok(await _m.Send(new RefreshDeviceConfigCommand(User.ClientId(), id), ct));

        /// <summary>Issue a new device certificate (recommended a month before expiry; done automatically by the gateway too).</summary>
        [HasPermission(Perm.DevicesManage)]
        [HttpPost("{id:guid}/renew-certificate")]
        public async Task<IActionResult> Renew(Guid id, CancellationToken ct) =>
            Ok(await _m.Send(new RenewCertificateCommand(User.ClientId(), id), ct));

        /// <summary>Fiscal day status as reported by FDMS (the local record is reconciled to it).</summary>
        [HasPermission(Perm.DayView)]
        [HttpGet("{id:guid}/day")]
        public async Task<IActionResult> DayStatus(Guid id, CancellationToken ct) =>
            Ok(await _m.Send(new GetFiscalDayStatusQuery(User.ClientId(), id), ct));

        [HasPermission(Perm.DayManage)]
        [HttpPost("{id:guid}/day/open")]
        public async Task<IActionResult> OpenDay(Guid id, CancellationToken ct) =>
            Ok(await _m.Send(new OpenFiscalDayCommand(User.ClientId(), id), ct));

        /// <summary>Close the fiscal day (Z report). Returns the outcome FDMS reported after validating the counters.</summary>
        [HasPermission(Perm.DayManage)]
        [HttpPost("{id:guid}/day/close")]
        public async Task<IActionResult> CloseDay(Guid id, CancellationToken ct) =>
            Ok(await _m.Send(new CloseFiscalDayCommand(User.ClientId(), id), ct));

        /// <summary>
        /// Fiscalize an invoice, credit note or debit note. Safe to retry with the same externalRef.
        /// 201 = accepted by FDMS; 202 = numbered and queued because FDMS is unreachable (it will be delivered automatically).
        /// </summary>
        [HasPermission(Perm.ReceiptsSubmit)]
        [HttpPost("{id:guid}/receipts")]
        public async Task<IActionResult> Submit(Guid id, SubmitReceiptRequest body, CancellationToken ct)
        {
            var r = await _m.Send(new SubmitReceiptCommand(User.ClientId(), id, body), ct);
            return r.Status is "Pending" or "Held" ? Accepted(r) : CreatedAtAction("Get", "Receipts", new { id = r.Id }, r);
        }

        /// <summary>FDMS connectivity check for this device.</summary>
        [HasPermission(Perm.DevicesView)]
        [HttpPost("{id:guid}/ping")]
        public async Task<IActionResult> Ping(Guid id, CancellationToken ct) => Ok(await _m.Send(new PingDeviceQuery(User.ClientId(), id), ct));

        /// <summary>Goods FDMS holds in stock for this device's store.</summary>
        [HasPermission(Perm.StockView)]
        [HttpGet("{id:guid}/stock")]
        public async Task<IActionResult> Stock(Guid id, [FromQuery] string? hsCode, [FromQuery] string? goodName, [FromQuery] int offset = 0, [FromQuery] int limit = 50, CancellationToken ct = default) =>
            Ok(await _m.Send(new GetStockQuery(User.ClientId(), id, hsCode, goodName, offset, limit), ct));

        /// <summary>The FDMS signing certificate chain (for verifying FDMS receipt/fiscal-day signatures).</summary>
        [HasPermission(Perm.DevicesView)]
        [HttpGet("{id:guid}/server-certificate")]
        public async Task<IActionResult> ServerCertificate(Guid id, [FromQuery] string? thumbprint, CancellationToken ct) =>
            Ok(await _m.Send(new GetServerCertificateQuery(User.ClientId(), id, thumbprint), ct));

        /// <summary>Offline mode: the fiscal-day files and how FDMS processed them.</summary>
        [HasPermission(Perm.DayView)]
        [HttpGet("{id:guid}/offline-files")]
        public async Task<IActionResult> OfflineFiles(Guid id, CancellationToken ct) => Ok(await _m.Send(new ListOfflineFilesQuery(User.ClientId(), id), ct));

        /// <summary>Offline mode: file up what has been issued so far, upload to FDMS and check processing now (the gateway also does this every minute).</summary>
        [HasPermission(Perm.OfflineManage)]
        [HttpPost("{id:guid}/offline-files/sync")]
        public async Task<IActionResult> SyncOffline(Guid id, CancellationToken ct) => Ok(await _m.Send(new SyncOfflineFilesCommand(User.ClientId(), id), ct));

        /// <summary>Calculate taxes and totals exactly as a real submission would, without consuming a receipt number.</summary>
        [HasPermission(Perm.ReceiptsSubmit)]
        [HttpPost("{id:guid}/receipts/preview")]
        public async Task<IActionResult> Preview(Guid id, SubmitReceiptRequest body, CancellationToken ct) =>
            Ok(await _m.Send(new PreviewReceiptCommand(User.ClientId(), id, body), ct));

        /// <summary>
        /// Upload an .xlsx of invoices (download the template from /api/v1/import/excel-template). With submit=false (default)
        /// nothing is fiscalised: you get the calculated result per invoice to check first.
        /// </summary>
        [HasPermission(Perm.ReceiptsImport)]
        [HttpPost("{id:guid}/import/excel")]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> ImportExcel(Guid id, IFormFile file, [FromQuery] bool submit = false, [FromQuery] bool taxInclusive = true,
            [FromQuery] decimal? defaultTaxPercent = null, [FromQuery] string? defaultCurrency = null, [FromQuery] string? defaultHsCode = null, CancellationToken ct = default)
        {
            var bytes = await ReadUploadAsync(file, ".xlsx", new byte[] { 0x50, 0x4B }, ct);
            return Ok(await _m.Send(new ImportInvoicesCommand(User.ClientId(), id, ImportKind.Excel, bytes,
                new ImportOptions(submit, taxInclusive, defaultTaxPercent, defaultCurrency, null, defaultHsCode)), ct));
        }

        /// <summary>
        /// Upload a digital (text) PDF invoice. The reader is best effort, so submit=false is the default: review the returned
        /// draft (it is a ready-to-send receipt request), fix anything, then POST it to /receipts - or repeat with submit=true.
        /// </summary>
        [HasPermission(Perm.ReceiptsImport)]
        [HttpPost("{id:guid}/import/pdf")]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> ImportPdf(Guid id, IFormFile file, [FromQuery] bool submit = false, [FromQuery] bool taxInclusive = true,
            [FromQuery] decimal? defaultTaxPercent = null, [FromQuery] string? defaultCurrency = null, [FromQuery] string? invoiceNo = null, [FromQuery] string? defaultHsCode = null, CancellationToken ct = default)
        {
            var bytes = await ReadUploadAsync(file, ".pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 }, ct);
            return Ok(await _m.Send(new ImportInvoicesCommand(User.ClientId(), id, ImportKind.Pdf, bytes,
                new ImportOptions(submit, taxInclusive, defaultTaxPercent, defaultCurrency, invoiceNo, defaultHsCode)), ct));
        }

        /// <summary>
        /// Upload a filled-in Word invoice (.docx): download the template from /api/v1/import/word-template. Like the PDF reader it only builds a draft by default
        /// (submit=false): check it, then repeat with submit=true.
        /// </summary>
        [HasPermission(Perm.ReceiptsImport)]
        [HttpPost("{id:guid}/import/word")]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> ImportWord(Guid id, IFormFile file, [FromQuery] bool submit = false, [FromQuery] bool taxInclusive = true,
            [FromQuery] decimal? defaultTaxPercent = null, [FromQuery] string? defaultCurrency = null, [FromQuery] string? invoiceNo = null, [FromQuery] string? defaultHsCode = null, CancellationToken ct = default)
        {
            var bytes = await ReadUploadAsync(file, ".docx", new byte[] { 0x50, 0x4B }, ct);
            return Ok(await _m.Send(new ImportInvoicesCommand(User.ClientId(), id, ImportKind.Word, bytes,
                new ImportOptions(submit, taxInclusive, defaultTaxPercent, defaultCurrency, invoiceNo, defaultHsCode)), ct));
        }

        private static async Task<byte[]> ReadUploadAsync(IFormFile? file, string extension, byte[] magic, CancellationToken ct)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Upload a file in the form field \"file\".");
            if (!Path.GetExtension(file.FileName).Equals(extension, StringComparison.OrdinalIgnoreCase))
                throw GatewayException.BadRequest("FILE_TYPE", $"Only {extension} files are accepted.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            if (bytes.Length < magic.Length || !bytes.Take(magic.Length).SequenceEqual(magic))
                throw GatewayException.BadRequest("FILE_TYPE", $"The file is not a valid {extension} document.");
            return bytes;
        }
    }

    [ApiController]
    [Authorize]
    [Route("api/v1/receipts")]
    public class ReceiptsController : ControllerBase
    {
        private readonly IMediator _m;
        public ReceiptsController(IMediator m) => _m = m;

        /// <summary>Search your receipts. status = Pending | Submitted | Rejected; from/to filter on the receipt date.</summary>
        [HasPermission(Perm.ReceiptsView)]
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] Guid? deviceId, [FromQuery] string? status, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] int? fiscalDayNo = null, [FromQuery] bool? hasErrors = null, CancellationToken ct = default) =>
            Ok(await _m.Send(new ListReceiptsQuery(User.ClientId(), new ReceiptListFilter(deviceId, status, from, to, search, page, pageSize, fiscalDayNo, hasErrors)), ct));

        [HasPermission(Perm.ReceiptsView)]
        [HttpGet("{id:guid}")]
        public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await _m.Send(new GetReceiptQuery(User.ClientId(), id), ct));

        /// <summary>The receipt with its lines, payments and buyer exactly as sent to ZIMRA.</summary>
        [HasPermission(Perm.ReceiptsView)]
        [HttpGet("{id:guid}/detail")]
        public async Task<IActionResult> Detail(Guid id, CancellationToken ct) => Ok(await _m.Send(new GetReceiptDetailQuery(User.ClientId(), id), ct));

        /// <summary>
        /// Correct a fiscalised invoice (for example one ZIMRA flagged with a red validation error that blocks closing the day): issues a credit note that reverses it
        /// and, when issueReplacement is true, a corrected invoice with the fixed HS codes. Safe to repeat.
        /// </summary>
        [HasPermission(Perm.ReceiptsSubmit)]
        [HttpPost("{id:guid}/correct")]
        public async Task<IActionResult> Correct(Guid id, CorrectReceiptRequest body, [FromServices] IApprovalService approvals, CancellationToken ct)
        {
            // the approval matrix may hold it for approval (202) instead of carrying it out
            var (total, currency, number) = await approvals.ReceiptAsync(User.ClientId(), id, ct);
            var held = await approvals.GateAsync(User.ClientId(), "credit_note", total, currency, $"Credit note for {number}: {body.Reason}", id.ToString(), body, ct);
            if (held != null) return Accepted(held);
            return Ok(await _m.Send(new CorrectReceiptCommand(User.ClientId(), id, body), ct));
        }

        /// <summary>
        /// Add to an invoice ZIMRA already accepted (an undercharge, extra charges) with a debit note. The lines are the additional charges; tax and currency follow the invoice.
        /// </summary>
        [HasPermission(Perm.ReceiptsSubmit)]
        [HttpPost("{id:guid}/debit-note")]
        public async Task<IActionResult> DebitNote(Guid id, DebitNoteRequest body, [FromServices] IApprovalService approvals, CancellationToken ct)
        {
            var (_, currency, number) = await approvals.ReceiptAsync(User.ClientId(), id, ct);
            var extra = (body.Lines ?? Array.Empty<ReceiptLineRequest>()).Sum(l => l.Quantity * l.UnitPrice);
            var held = await approvals.GateAsync(User.ClientId(), "debit_note", extra, currency, $"Debit note on {number}: {body.Reason}", id.ToString(), body, ct);
            if (held != null) return Accepted(held);
            return Ok(await _m.Send(new IssueDebitNoteCommand(User.ClientId(), id, body), ct));
        }

        /// <summary>Printable receipt: format=receipt (80 mm roll, default) or a4.</summary>
        [HasPermission(Perm.ReceiptsView)]
        [HttpGet("{id:guid}/pdf")]
        public async Task<IActionResult> Pdf(Guid id, [FromQuery] string format = "receipt", CancellationToken ct = default) =>
            File(await _m.Send(new GetReceiptPdfQuery(User.ClientId(), id, format.Equals("a4", StringComparison.OrdinalIgnoreCase)), ct), "application/pdf");

        /// <summary>The invoice as a Word document, laid out like the A4 PDF (the company's built design when it has one).</summary>
        [HasPermission(Perm.ReceiptsView)]
        [HttpGet("{id:guid}/docx")]
        public async Task<IActionResult> Docx(Guid id, [FromServices] IReceiptService receipts, CancellationToken ct) =>
            File(await receipts.GetDocxAsync(User.ClientId(), id, ct), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"invoice-{id:N}.docx");

        /// <summary>The invoice as structured JSON for other systems: every printed value, the lines, taxes, totals, fiscal identifiers and the layout.</summary>
        [HasPermission(Perm.ReceiptsView)]
        [HttpGet("{id:guid}/document")]
        public async Task<IActionResult> Document(Guid id, [FromServices] IReceiptService receipts, CancellationToken ct) => Ok(await receipts.GetDocumentAsync(User.ClientId(), id, ct));

        /// <summary>QR code image (PNG) to print on the receipt.</summary>
        [HasPermission(Perm.ReceiptsView)]
        [HttpGet("{id:guid}/qr")]
        public async Task<IActionResult> Qr(Guid id, CancellationToken ct) =>
            File(await _m.Send(new GetReceiptQrQuery(User.ClientId(), id), ct), "image/png");

        public sealed record CancelBody(string? Reason);

        /// <summary>End the hold now: the receipt is numbered, signed and sent to ZIMRA.</summary>
        [HasPermission(Perm.ReceiptsSubmit)]
        [HttpPost("{id:guid}/release")]
        public async Task<IActionResult> Release(Guid id, CancellationToken ct) => Ok(await _m.Send(new ReleaseReceiptCommand(User.ClientId(), id), ct));

        /// <summary>Void a receipt that is still Held. Nothing was sent to ZIMRA; once issued only a credit note can correct it.</summary>
        [HasPermission(Perm.ReceiptsSubmit)]
        [HttpPost("{id:guid}/cancel")]
        public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelBody? body, [FromServices] IApprovalService approvals, CancellationToken ct)
        {
            var (total, currency, number) = await approvals.ReceiptAsync(User.ClientId(), id, ct);
            var held = await approvals.GateAsync(User.ClientId(), "receipt_cancel", total, currency, $"Cancel {number}{(string.IsNullOrWhiteSpace(body?.Reason) ? "" : ": " + body!.Reason)}", id.ToString(), new ReceiptsCancelPayload(body?.Reason), ct);
            if (held != null) return Accepted(held);
            return Ok(await _m.Send(new CancelReceiptCommand(User.ClientId(), id, body?.Reason), ct));
        }

        /// <summary>Re-deliver a receipt that is still Pending.</summary>
        [HasPermission(Perm.ReceiptsSubmit)]
        [HttpPost("{id:guid}/retry")]
        public async Task<IActionResult> Retry(Guid id, CancellationToken ct) => Ok(await _m.Send(new RetryReceiptCommand(User.ClientId(), id), ct));
    }

    /// <summary>Spreadsheet template for the Excel import.</summary>
    [ApiController]
    [Authorize]
    [Route("api/v1/import")]
    public class ImportController : ControllerBase
    {
        private readonly IInvoiceImportService _import;
        public ImportController(IInvoiceImportService import) => _import = import;

        [HasPermission(Perm.ReceiptsImport)]
        [HttpGet("excel-template")]
        public IActionResult Template() =>
            File(_import.BuildExcelTemplate(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "invoice-import-template.xlsx");

        /// <summary>The Word tax-invoice template (sample=true: the same invoice filled in as an example).</summary>
        [HasPermission(Perm.ReceiptsImport)]
        [HttpGet("word-template")]
        public IActionResult WordTemplate([FromQuery] bool sample = false) =>
            File(_import.BuildWordTemplate(sample), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", sample ? "tax-invoice-example.docx" : "tax-invoice-template.docx");
    }

    /// <summary>
    /// Get notified when a receipt is fiscalised/rejected or a fiscal day closes. Each POST carries X-Fdms-Event, X-Fdms-Delivery,
    /// X-Fdms-Timestamp and X-Fdms-Signature = "sha256=" + hex(HMAC-SHA256(secret, timestamp + "." + body)).
    /// Events: receipt.fiscalised, receipt.failed, fiscalday.closed, fiscalday.close_failed. Failed deliveries are retried for ~2 days.
    /// </summary>
    [ApiController]
    [Authorize]
    [HasPermission(Perm.WebhooksManage)]
    [Route("api/v1/webhook")]
    public class WebhookController : ControllerBase
    {
        private readonly IMediator _m;
        public WebhookController(IMediator m) => _m = m;

        public sealed record SetWebhookBody(string Url);

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _m.Send(new GetWebhookQuery(User.ClientId()), ct));

        /// <summary>Sets the https URL and returns a NEW signing secret - shown only in this response.</summary>
        [HttpPut] public async Task<IActionResult> Set(SetWebhookBody body, CancellationToken ct) => Ok(await _m.Send(new SetWebhookCommand(User.ClientId(), body.Url), ct));

        [HttpDelete]
        public async Task<IActionResult> Clear(CancellationToken ct) { await _m.Send(new ClearWebhookCommand(User.ClientId()), ct); return NoContent(); }

        [HttpGet("deliveries")]
        public async Task<IActionResult> Deliveries([FromQuery] int take = 50, CancellationToken ct = default) =>
            Ok(await _m.Send(new ListWebhookDeliveriesQuery(User.ClientId(), take), ct));
    }
}
