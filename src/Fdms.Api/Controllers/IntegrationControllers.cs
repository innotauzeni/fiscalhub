using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Features;
using Fdms.Application.Import;
using Fdms.Application.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>What the company's people and systems did on its devices, so an integration can be watched and a misbehaving one found. Company users see their own company; the operator sees all.</summary>
    [ApiController, Authorize, HasPermission(Perm.ActivityView), Route("api/v1/activity")]
    public class ActivityController : ControllerBase
    {
        private readonly IActivityService _s;
        public ActivityController(IActivityService s) => _s = s;

        /// <summary>Filterable, paged list of API calls (outcome = ok | errors; actorType = user | token).</summary>
        [HttpGet] public async Task<IActionResult> Search([FromQuery] ActivitySearch q, CancellationToken ct) => Ok(await _s.SearchAsync(User.ClientIdOrNull(), q, ct));

        /// <summary>One row per device: last seen, calls and errors in the last 24 hours, receipts issued, live tokens.</summary>
        [HttpGet("devices")] public async Task<IActionResult> Devices([FromQuery] Guid? clientId, CancellationToken ct) => Ok(await _s.DevicesAsync(User.ClientIdOrNull(), clientId, ct));
    }

    /// <summary>
    /// For systems that never send their users to this console: hand over an invoice file (or call the JSON API), get it fiscalised, and download the fiscal result and
    /// printable invoice into your own system. Everything runs with a per-device API token.
    /// </summary>
    [ApiController, Authorize, Route("api/v1")]
    public class IntegrationController : ControllerBase
    {
        private readonly IMediator _m;
        public IntegrationController(IMediator m) => _m = m;

        private object Links(Guid id)
        {
            var b = $"{Request.Scheme}://{Request.Host}/api/v1/receipts/{id}";
            return new { self = b, fiscal = b + "/fiscal", pdf = b + "/pdf", pdfA4 = b + "/pdf?format=a4", qr = b + "/qr", detail = b + "/detail" };
        }

        private object FiscalBlock(ReceiptDto r) => new
        {
            receiptId = r.Id, deviceId = r.DeviceId, externalRef = r.ExternalRef, invoiceNo = r.InvoiceNo, type = r.Type, currency = r.Currency, total = r.Total, status = r.Status,
            fiscalDayNo = r.FiscalDayNo, receiptCounter = r.ReceiptCounter, receiptGlobalNo = r.ReceiptGlobalNo, receiptDate = r.ReceiptDate, fdmsReceiptId = r.FdmsReceiptId, serverDate = r.ServerDate,
            qrUrl = r.QrUrl, deviceSignatureHash = r.DeviceSignatureHash, errorCode = r.ErrorCode, errorMessage = r.ErrorMessage, validationErrors = r.ValidationErrors, links = Links(r.Id),
            printBlock = PrintBlock(r),
        };

        /// <summary>
        /// For a system that keeps printing its own invoice: the lines ZIMRA expects on it, ready to place at the foot of the page, and the QR to draw (links.qr is the PNG, qrUrl is what it encodes).
        /// Also print your own device ID. A receipt that is not Submitted yet must say so instead of showing the QR.
        /// </summary>
        private static object PrintBlock(ReceiptDto r) => new
        {
            ready = r.Status == "Submitted" && !string.IsNullOrEmpty(r.QrUrl),
            lines = new[]
            {
                $"Invoice No: {r.InvoiceNo}", $"Fiscal day {r.FiscalDayNo}  Receipt #{r.ReceiptCounter}  Global #{r.ReceiptGlobalNo}",
                r.FdmsReceiptId.HasValue ? $"ZIMRA receipt ID: {r.FdmsReceiptId}" : null, !string.IsNullOrEmpty(r.QrUrl) ? "Verify this receipt: " + r.QrUrl : null, $"Device signature: {r.DeviceSignatureHash}",
            }.Where(l => l != null),
            qrUrl = r.QrUrl,
        };

        /// <summary>
        /// Upload an invoice document (.xlsx, .docx or a digital .pdf; the type is taken from the file name) and have it fiscalised in one call (submit=true by default).
        /// The answer carries, for every invoice, the fiscal result (QR, global number, signature hash, status) and links to download the printable invoice.
        /// Safe to repeat: the invoice number is the idempotency key, so the same document never fiscalises twice. Send submit=false for a dry run.
        /// </summary>
        [HasPermission(Perm.ReceiptsImport)]
        [HttpPost("devices/{id:guid}/fiscalise")]
        [RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> Fiscalise(Guid id, IFormFile file, [FromQuery] bool submit = true, [FromQuery] bool taxInclusive = true, [FromQuery] decimal? defaultTaxPercent = null,
            [FromQuery] string? defaultCurrency = null, [FromQuery] string? invoiceNo = null, [FromQuery] string? defaultHsCode = null, CancellationToken ct = default)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Upload a file in the form field \"file\".");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var kind = ext switch { ".xlsx" => ImportKind.Excel, ".docx" => ImportKind.Word, ".pdf" => ImportKind.Pdf, _ => throw GatewayException.BadRequest("FILE_TYPE", "Only .xlsx, .docx and .pdf invoice documents are accepted.") };
            using var ms = new MemoryStream(); await file.CopyToAsync(ms, ct); var bytes = ms.ToArray();
            var magic = kind == ImportKind.Pdf ? new byte[] { 0x25, 0x50, 0x44, 0x46 } : new byte[] { 0x50, 0x4B };
            if (bytes.Length < magic.Length || !bytes.Take(magic.Length).SequenceEqual(magic)) throw GatewayException.BadRequest("FILE_TYPE", $"The file is not a valid {ext} document.");

            var r = await _m.Send(new ImportInvoicesCommand(User.ClientId(), id, kind, bytes, new ImportOptions(submit, taxInclusive, defaultTaxPercent, defaultCurrency, invoiceNo, defaultHsCode)), ct);
            var items = new List<object>();
            foreach (var i in r.Items)
            {
                ReceiptDto? rec = i.ReceiptId is { } rid ? await _m.Send(new GetReceiptQuery(User.ClientId(), rid), ct) : null;
                items.Add(new { i.InvoiceNo, i.Status, i.ReceiptId, i.Total, i.ErrorCode, i.Error, i.Warnings, fiscal = rec == null ? null : FiscalBlock(rec), links = i.ReceiptId is { } g ? Links(g) : null, draft = rec == null ? i.Request : null });
            }
            return Ok(new { r.Total, r.Succeeded, r.Failed, submitted = r.Submitted, items, errors = r.Errors });
        }

        /// <summary>Find a receipt by your own reference (externalRef) or invoice number, e.g. to fetch the fiscal result again after a timeout.</summary>
        [HasPermission(Perm.ReceiptsView)]
        [HttpGet("receipts/lookup")]
        public async Task<IActionResult> Lookup([FromQuery] string? externalRef, [FromQuery] string? invoiceNo, [FromQuery] Guid? deviceId, CancellationToken ct)
        {
            var key = externalRef ?? invoiceNo;
            if (string.IsNullOrWhiteSpace(key)) throw GatewayException.BadRequest("KEY_REQUIRED", "Give externalRef or invoiceNo.");
            var page = await _m.Send(new ListReceiptsQuery(User.ClientId(), new ReceiptListFilter(deviceId, null, null, null, key.Trim(), 1, 50, null, null)), ct);
            var hits = page.Items.Where(r => externalRef != null ? r.ExternalRef == externalRef : r.InvoiceNo == invoiceNo).ToList();
            return Ok(new { count = hits.Count, items = hits.Select(FiscalBlock) });
        }

        /// <summary>The fiscal result of one receipt as compact JSON, with the links to its printable forms.</summary>
        [HasPermission(Perm.ReceiptsView)]
        [HttpGet("receipts/{id:guid}/fiscal")]
        public async Task<IActionResult> Fiscal(Guid id, CancellationToken ct) => Ok(FiscalBlock(await _m.Send(new GetReceiptQuery(User.ClientId(), id), ct)));
    }
}
