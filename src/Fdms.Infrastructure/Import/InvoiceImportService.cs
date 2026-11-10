using Fdms.Application.Abstractions;
using Fdms.Application.Import;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Import
{
    /// <summary>
    /// Turns spreadsheets / PDFs into receipts through the SAME pipeline as the JSON API (calculation, numbering, signing,
    /// FDMS), so an imported invoice is fiscalised exactly like any other. Each invoice is idempotent: externalRef is
    /// "import:&lt;invoice no&gt;", so re-uploading a file never fiscalises an invoice twice.
    /// </summary>
    public sealed class InvoiceImportService : IInvoiceImportService
    {
        public const int MaxSubmitPerRequest = 200;

        private readonly IReceiptService _receipts;
        private readonly DeviceAccessor _devices;
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;

        public InvoiceImportService(IReceiptService receipts, DeviceAccessor devices, FdmsDbContext db, ICallerScope scope)
        {
            _scope = scope;
            _receipts = receipts; _devices = devices; _db = db;
        }

        public byte[] BuildExcelTemplate() => ExcelInvoiceParser.BuildTemplate();
        public byte[] BuildWordTemplate(bool sample) => WordInvoiceTemplate.Build(sample);

        public async Task<ImportResultDto> ImportExcelAsync(Guid clientId, Guid deviceId, Stream file, ImportOptions options, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct, track: false);
            List<InvoiceDraft> drafts; List<string> errors;
            try { (drafts, errors) = ExcelInvoiceParser.Parse(file); }
            catch (Exception ex) when (ex is not GatewayException)
            { throw GatewayException.BadRequest("FILE_UNREADABLE", "The file could not be read as an .xlsx workbook."); }
            return await ProcessAsync(clientId, device, drafts, errors, options, ct);
        }

        public async Task<ImportResultDto> ImportPdfAsync(Guid clientId, Guid deviceId, Stream file, ImportOptions options, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct, track: false);
            string text;
            try { text = PdfTextExtractor.Extract(file); }
            catch (Exception ex) when (ex is not GatewayException)
            { throw GatewayException.BadRequest("FILE_UNREADABLE", "The file could not be read as a PDF."); }

            var draft = InvoiceTextParser.Parse(text);
            if (!string.IsNullOrWhiteSpace(options.InvoiceNoOverride)) draft.InvoiceNo = options.InvoiceNoOverride.Trim();
            return await ProcessAsync(clientId, device, new List<InvoiceDraft> { draft }, new List<string>(), options, ct);
        }

        public async Task<ImportResultDto> ImportWordAsync(Guid clientId, Guid deviceId, Stream file, ImportOptions options, CancellationToken ct)
        {
            var device = await _devices.GetAsync(clientId, deviceId, ct, track: false);
            string text;
            try { text = DocxTextExtractor.Extract(file); }
            catch (Exception ex) when (ex is not GatewayException)
            { throw GatewayException.BadRequest("FILE_UNREADABLE", "The file could not be read as a Word (.docx) document."); }

            var draft = InvoiceTextParser.Parse(text);
            for (var i = 0; i < draft.Warnings.Count; i++) draft.Warnings[i] = draft.Warnings[i].Replace("The PDF", "The document").Replace("the PDF", "the document").Replace("scanned image", "empty file");
            if (!string.IsNullOrWhiteSpace(options.InvoiceNoOverride)) draft.InvoiceNo = options.InvoiceNoOverride.Trim();
            return await ProcessAsync(clientId, device, new List<InvoiceDraft> { draft }, new List<string>(), options, ct);
        }

        private async Task<ImportResultDto> ProcessAsync(Guid clientId, Device device, List<InvoiceDraft> drafts,
            List<string> parseErrors, ImportOptions options, CancellationToken ct)
        {
            if (options.Submit && parseErrors.Count > 0)
                // A dropped row would silently change an invoice's total, so nothing is fiscalised until the file is clean.
                return new ImportResultDto(drafts.Count, 0, drafts.Count, false, Array.Empty<ImportItemResult>(),
                    parseErrors.Prepend("Nothing was submitted because some rows could not be read. Fix them and upload again.").ToList());
            if (options.Submit && drafts.Count > MaxSubmitPerRequest)
                throw GatewayException.BadRequest("TOO_MANY_INVOICES", $"At most {MaxSubmitPerRequest} invoices can be fiscalised per upload; split the file (or preview it first).");

            var taxes = Services.Mappers.ParseTaxes(device.TaxesJson);
            var positive = taxes.Where(t => t.TaxPercent is > 0).Select(t => t.TaxPercent!.Value).Distinct().ToList();
            var defaultPercent = options.DefaultTaxPercent ?? (positive.Count == 1 ? positive[0] : (decimal?)null);

            var items = new List<ImportItemResult>();
            foreach (var draft in drafts)
            {
                SubmitReceiptRequest? req = null;
                try
                {
                    req = await BuildRequestAsync(clientId, draft, options, defaultPercent, ct);
                    // Payments are validated against the total, so first ask for the total alone, then align the payment to it.
                    var preview = await _receipts.PreviewAsync(clientId, device.Id, req with { Payments = null }, ct);
                    req = AlignPayments(req, preview);

                    if (!options.Submit)
                    {
                        items.Add(new ImportItemResult(req.InvoiceNo, "Preview", null, preview.Total, null, null, draft.Warnings, req));
                        continue;
                    }
                    var r = await _receipts.SubmitAsync(clientId, device.Id, req, ct);
                    items.Add(new ImportItemResult(req.InvoiceNo, r.Status, r.Id, r.Total, null, null, draft.Warnings, null));
                }
                catch (GatewayException ge)
                {
                    items.Add(new ImportItemResult(draft.InvoiceNo, "Failed", null, null, ge.Code, ge.Message, draft.Warnings, options.Submit ? null : req));
                }
            }
            var failed = items.Count(i => i.Status == "Failed");
            return new ImportResultDto(items.Count, items.Count - failed, failed, options.Submit, items, parseErrors);
        }

        private async Task<SubmitReceiptRequest> BuildRequestAsync(Guid clientId, InvoiceDraft d, ImportOptions o, decimal? defaultPercent, CancellationToken ct)
        {
            var invoiceNo = (d.InvoiceNo ?? "").Trim();
            if (invoiceNo.Length == 0) throw GatewayException.BadRequest("INVOICE_NO_REQUIRED", "The invoice has no invoice number (for PDFs pass invoiceNo).");
            if (invoiceNo.Length > 50) throw GatewayException.BadRequest("INVOICE_NO_TOO_LONG", "Invoice numbers are limited to 50 characters.");

            var currency = d.Currency ?? o.DefaultCurrency;
            if (string.IsNullOrWhiteSpace(currency))
            {
                currency = "USD";
                d.Warnings.Add("No currency found; USD was assumed.");
            }

            if (!string.IsNullOrWhiteSpace(o.DefaultHsCode) && !System.Text.RegularExpressions.Regex.IsMatch(o.DefaultHsCode.Trim(), @"^\d{4}(\d{4})?$"))
                throw GatewayException.BadRequest("HS_CODE_INVALID", "defaultHsCode must be 4 or 8 digits.");
            var hsApplied = 0;

            var lines = new List<ReceiptLineRequest>();
            foreach (var l0 in d.Lines)
            {
                var l = l0;
                if (string.IsNullOrWhiteSpace(l.HsCode) && !string.IsNullOrWhiteSpace(o.DefaultHsCode)) { l = l with { HsCode = o.DefaultHsCode.Trim() }; hsApplied++; }
                if (l.TaxId == null && l.TaxPercent == null && !l.IsExempt)
                {
                    if (defaultPercent == null)
                        throw GatewayException.BadRequest("TAX_REQUIRED", $"\"{l.Name}\" has no tax rate and the device has several rates; set Tax Percent or pass defaultTaxPercent.");
                    lines.Add(l with { TaxPercent = defaultPercent });
                }
                else lines.Add(l);
            }

            if (hsApplied > 0) d.Warnings.Add($"HS code {o.DefaultHsCode!.Trim()} was applied to {hsApplied} line(s) that had none - make sure it is right for these goods.");

            Guid? original = null;
            if (d.ReceiptType != ReceiptType.FiscalInvoice)
            {
                if (string.IsNullOrWhiteSpace(d.OriginalInvoiceNo))
                    throw GatewayException.BadRequest("ORIGINAL_REQUIRED", "Credit and debit notes need \"Original Invoice No\".");
                var no = d.OriginalInvoiceNo.Trim();
                original = await _db.Receipts.AsNoTracking().InScope(_scope)
                    .Where(r => r.ClientId == clientId && r.InvoiceNo == no && r.Type == ReceiptType.FiscalInvoice && r.Status == ReceiptStatus.Submitted)
                    .Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct)
                    ?? throw GatewayException.Unprocessable("RCPT032", $"Original invoice \"{no}\" has not been fiscalised through this gateway.");
            }

            return new SubmitReceiptRequest(
                ExternalRef: ("import:" + invoiceNo).Length <= 100 ? "import:" + invoiceNo : ("import:" + invoiceNo)[..100],
                InvoiceNo: invoiceNo,
                ReceiptType: d.ReceiptType,
                Currency: currency.Trim().ToUpperInvariant(),
                LinesTaxInclusive: o.LinesTaxInclusive,
                Lines: lines,
                Payments: d.Payments.Count == 0 ? null : d.Payments.Select(p => new ReceiptPaymentRequest(p.MoneyType, Math.Abs(p.Amount))).ToList(),
                Buyer: d.Buyer,
                Notes: d.Notes,
                OriginalReceiptId: original,
                AutoOpenDay: true);
        }

        /// <summary>A single payment line means "paid in full by this method": its amount is set to the calculated total.</summary>
        private static SubmitReceiptRequest AlignPayments(SubmitReceiptRequest req, CalculatedReceiptView preview)
        {
            if (req.Payments == null || req.Payments.Count == 0) return req;
            if (req.Payments.Count == 1)
                return req with { Payments = new[] { req.Payments[0] with { Amount = Math.Abs(preview.Total) } } };
            if (req.Payments.Any(p => p.Amount == 0))
                throw GatewayException.BadRequest("PAYMENT_AMOUNT_REQUIRED", "With several payment methods every payment needs an amount.");
            return req;
        }
    }
}
