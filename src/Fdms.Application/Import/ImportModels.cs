using Fdms.Application.Abstractions;
using Fdms.Domain.Enums;

namespace Fdms.Application.Import
{
    /// <summary>One invoice recovered from a spreadsheet or PDF, before it is turned into a receipt.</summary>
    public sealed class InvoiceDraft
    {
        public string? InvoiceNo { get; set; }
        public DateTime? InvoiceDate { get; set; }
        public string? Currency { get; set; }
        public ReceiptType ReceiptType { get; set; } = ReceiptType.FiscalInvoice;
        public string? Notes { get; set; }
        public string? OriginalInvoiceNo { get; set; }
        public BuyerRequest? Buyer { get; set; }
        public List<ReceiptLineRequest> Lines { get; set; } = new();
        public List<ReceiptPaymentRequest> Payments { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public sealed record ImportOptions(bool Submit, bool LinesTaxInclusive = true, decimal? DefaultTaxPercent = null,
        string? DefaultCurrency = null, string? InvoiceNoOverride = null, string? DefaultHsCode = null);

    public sealed record ImportItemResult(
        string? InvoiceNo, string Status, Guid? ReceiptId, decimal? Total, string? ErrorCode, string? Error,
        IReadOnlyList<string> Warnings, SubmitReceiptRequest? Request);

    public sealed record ImportResultDto(int Total, int Succeeded, int Failed, bool Submitted,
        IReadOnlyList<ImportItemResult> Items, IReadOnlyList<string> Errors);

    public sealed record CalculatedReceiptView(
        string Type, string Currency, bool LinesTaxInclusive, decimal Total,
        IReadOnlyList<CalculatedLineView> Lines, IReadOnlyList<CalculatedTaxView> Taxes, IReadOnlyList<CalculatedPaymentView> Payments);

    public sealed record CalculatedLineView(int LineNo, string Name, string? HsCode, decimal Price, decimal Quantity, decimal Total, int TaxId, decimal? TaxPercent);
    public sealed record CalculatedTaxView(int TaxId, decimal? TaxPercent, decimal TaxAmount, decimal SalesAmountWithTax);
    public sealed record CalculatedPaymentView(string MoneyType, decimal Amount);

    /// <summary>FiscalDayNo and HasErrors narrow to one fiscal day and/or to receipts ZIMRA flagged with validation errors.</summary>
    public sealed record ReceiptListFilter(Guid? DeviceId, string? Status, DateTime? From, DateTime? To, string? Search, int Page = 1, int PageSize = 50, int? FiscalDayNo = null, bool? HasErrors = null);

    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

    public interface IInvoiceImportService
    {
        Task<ImportResultDto> ImportExcelAsync(Guid clientId, Guid deviceId, Stream file, ImportOptions options, CancellationToken ct);
        Task<ImportResultDto> ImportPdfAsync(Guid clientId, Guid deviceId, Stream file, ImportOptions options, CancellationToken ct);
        /// <summary>A filled-in Word invoice (the template from <see cref="BuildWordTemplate"/>, or any similar .docx).</summary>
        Task<ImportResultDto> ImportWordAsync(Guid clientId, Guid deviceId, Stream file, ImportOptions options, CancellationToken ct);
        byte[] BuildExcelTemplate();
        /// <summary>The Word invoice template; with sample=true the same invoice filled in as an example.</summary>
        byte[] BuildWordTemplate(bool sample);
    }
}
