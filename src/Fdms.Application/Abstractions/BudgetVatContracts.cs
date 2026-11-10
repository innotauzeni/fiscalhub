namespace Fdms.Application.Abstractions
{
    // ---- VAT return from the books ----
    public sealed record VatReturnLine(string Label, decimal Amount, string? Note);
    public sealed record VatBillRow(string Supplier, string Number, DateTime Date, decimal Net, decimal Vat);
    public sealed record BooksVatReturnDto(string Currency, DateTime From, DateTime To, decimal TaxableSales, decimal SalesReturns, decimal NetSales, decimal OutputVat, decimal CreditNoteVat, decimal NetOutputVat,
        decimal Purchases, decimal InputVat, decimal VatPayable, decimal OutputBalance, decimal InputBalance, decimal SettlementDue, IReadOnlyList<VatReturnLine> Lines, IReadOnlyList<VatBillRow> Bills);
    public sealed record SettleVatRequest(DateTime Date, string Currency, Guid? AccountId);

    public interface IVatBooksService
    {
        Task<BooksVatReturnDto> ReturnAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct);
        /// <summary>Clears what is held for VAT (collected and recoverable) against the cash or bank account that paid or received the difference.</summary>
        Task<JournalDto> SettleAsync(Guid clientId, SettleVatRequest req, CancellationToken ct);
    }

    // ---- budgets ----
    public sealed record BudgetAccountDto(Guid AccountId, string Code, string Name, string Type, IReadOnlyList<decimal> Months);
    public sealed record BudgetDto(Guid Id, string Name, int Year, string Currency, decimal TotalIncome, decimal TotalExpense, IReadOnlyList<BudgetAccountDto> Accounts);
    public sealed record BudgetLineInput(Guid AccountId, IReadOnlyList<decimal> Months);
    public sealed record SaveBudgetRequest(Guid? Id, string Name, int Year, string Currency, IReadOnlyList<BudgetLineInput> Lines);
    public sealed record VarianceRow(Guid AccountId, string Code, string Name, decimal Budget, decimal Actual, decimal Variance, decimal? VariancePct);
    public sealed record VarianceSection(string Title, IReadOnlyList<VarianceRow> Rows, decimal Budget, decimal Actual, decimal Variance);
    public sealed record BudgetVsActualDto(Guid BudgetId, string Name, int Year, string Currency, int FromMonth, int ToMonth, IReadOnlyList<VarianceSection> Sections, decimal BudgetedProfit, decimal ActualProfit, decimal ProfitVariance);

    public interface IBudgetService
    {
        Task<IReadOnlyList<BudgetDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<BudgetDto> SaveAsync(Guid clientId, SaveBudgetRequest req, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<BudgetVsActualDto> VsActualAsync(Guid clientId, Guid id, int fromMonth, int toMonth, CancellationToken ct);
    }
}
