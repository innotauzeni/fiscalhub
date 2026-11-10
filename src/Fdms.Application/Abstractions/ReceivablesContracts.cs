namespace Fdms.Application.Abstractions
{
    // ---- customers who owe the company ----
    public sealed record ArPaymentDto(Guid Id, DateTime Date, decimal Amount, string AccountName, string? Reference, bool Voided);
    public sealed record OpenInvoiceDto(Guid ReceiptId, string InvoiceNo, DateTime Date, DateTime DueDate, string CustomerKey, string Customer, string? Tin, string Currency, decimal Total, decimal OnAccount,
        decimal Credited, decimal Received, decimal Outstanding, int DaysOverdue, IReadOnlyList<ArPaymentDto> Payments, string Source = "fiscal");
    public sealed record ReceiveRequest(DateTime Date, decimal Amount, Guid AccountId, string? Reference);
    public sealed record OpenInvoiceFilter(string? Status, string? Customer, string? Q, string? Currency, int Take = 300);
    public sealed record ReceivableAgeingRow(string CustomerKey, string Customer, decimal NotDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total);
    public sealed record ReceivableAgeingDto(string Currency, DateTime AsAt, IReadOnlyList<ReceivableAgeingRow> Rows, ReceivableAgeingRow Totals);
    public sealed record StatementLine(DateTime Date, string Kind, string Reference, string Description, decimal Debit, decimal Credit, decimal Balance);
    public sealed record CustomerStatementDto(string Customer, string? Tin, string Currency, DateTime From, DateTime To, decimal Opening, IReadOnlyList<StatementLine> Lines, decimal Closing);
    public sealed record ReceivablesSettingsDto(int TermsDays);
    public sealed record SetTermsRequest(int TermsDays);

    public interface IReceivablesService
    {
        Task<IReadOnlyList<OpenInvoiceDto>> ListAsync(Guid clientId, OpenInvoiceFilter f, CancellationToken ct);
        Task<OpenInvoiceDto> ReceiveAsync(Guid clientId, Guid receiptId, ReceiveRequest req, CancellationToken ct);
        Task<OpenInvoiceDto> VoidPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct);
        Task<ReceivableAgeingDto> AgeingAsync(Guid clientId, string currency, DateTime asAt, CancellationToken ct);
        Task<CustomerStatementDto> StatementAsync(Guid clientId, string customerKey, string currency, DateTime from, DateTime to, CancellationToken ct);
        Task<ReceivablesSettingsDto> GetSettingsAsync(Guid clientId, CancellationToken ct);
        Task<ReceivablesSettingsDto> SetTermsAsync(Guid clientId, int termsDays, CancellationToken ct);
    }

    // ---- fixed assets ----
    public sealed record FixedAssetDto(Guid Id, string Name, string? Code, DateTime PurchaseDate, string Currency, decimal Cost, decimal ResidualValue, int UsefulLifeMonths, string Method, decimal RatePercent,
        string AssetAccount, string AccumulatedAccount, string ExpenseAccount, string Status, DateTime? DisposedOn, decimal? DisposalProceeds, string? Notes, decimal AccumulatedDepreciation, decimal NetBookValue, DateTime? FullyDepreciatedBy);
    public sealed record CreateAssetRequest(string Name, string? Code, DateTime PurchaseDate, string Currency, decimal Cost, decimal ResidualValue, int UsefulLifeMonths, string Method, decimal RatePercent,
        Guid AssetAccountId, Guid AccumulatedAccountId, Guid ExpenseAccountId, string? Notes, Guid? PaidFromAccountId);
    public sealed record DisposeAssetRequest(DateTime Date, decimal Proceeds, Guid? ReceivedIntoAccountId);
    public sealed record DepreciateRequest(DateTime UpTo);
    public sealed record DepreciationResultDto(int Charges, decimal Amount, IReadOnlyList<string> Months);
    public sealed record AssetRegisterDto(DateTime AsAt, IReadOnlyList<FixedAssetDto> Assets, IReadOnlyList<AmountDto> Cost, IReadOnlyList<AmountDto> Accumulated, IReadOnlyList<AmountDto> NetBook);

    public interface IFixedAssetService
    {
        Task<AssetRegisterDto> RegisterAsync(Guid clientId, DateTime asAt, CancellationToken ct);
        Task<FixedAssetDto> CreateAsync(Guid clientId, CreateAssetRequest req, CancellationToken ct);
        Task<DepreciationResultDto> DepreciateAsync(Guid clientId, DateTime upTo, CancellationToken ct);
        Task<FixedAssetDto> DisposeAsync(Guid clientId, Guid id, DisposeAssetRequest req, CancellationToken ct);
    }
}
