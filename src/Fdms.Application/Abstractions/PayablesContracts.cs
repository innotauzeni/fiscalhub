namespace Fdms.Application.Abstractions
{
    public sealed record AmountDto(string Currency, decimal Amount);
    public sealed record SupplierDto(Guid Id, string Name, string? Tin, string? Email, string? Phone, string? Address, int TermsDays, bool IsActive, int OpenBills, IReadOnlyList<AmountDto> Outstanding);
    public sealed record SaveSupplierRequest(Guid? Id, string Name, string? Tin, string? Email, string? Phone, string? Address, int TermsDays, bool IsActive);

    public sealed record BillPaymentDto(Guid Id, DateTime Date, decimal Amount, Guid AccountId, string AccountName, string? Reference, bool Voided);
    public sealed record BillDto(Guid Id, Guid SupplierId, string SupplierName, string Number, DateTime Date, DateTime DueDate, string Currency, string? Memo, decimal Subtotal, decimal Vat, decimal Total,
        decimal Paid, decimal Outstanding, string Status, int DaysOverdue, Guid ExpenseAccountId, string ExpenseAccountName, string? JournalNumber, IReadOnlyList<BillPaymentDto> Payments);
    public sealed record CreateBillRequest(Guid SupplierId, string Number, DateTime Date, DateTime? DueDate, string Currency, string? Memo, decimal Subtotal, decimal Vat, Guid ExpenseAccountId, Guid? CostCentreId = null);
    public sealed record PayBillRequest(DateTime Date, decimal Amount, Guid AccountId, string? Reference);
    public sealed record BillFilter(string? Status, Guid? SupplierId, string? Q, string? Currency, int Take = 200);

    public sealed record AgeingRow(Guid SupplierId, string Supplier, decimal NotDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total);
    public sealed record AgeingDto(string Currency, DateTime AsAt, IReadOnlyList<AgeingRow> Rows, AgeingRow Totals);

    /// <summary>What the company owes one supplier over a period. Bills are credits and payments debits, so the balance is the amount owed.</summary>
    public sealed record SupplierStatementDto(string Supplier, string? Tin, string Currency, DateTime From, DateTime To, decimal Opening, IReadOnlyList<StatementLine> Lines, decimal Closing);

    public sealed record PeriodDto(DateTime? LockedUntil);
    public sealed record SetPeriodRequest(DateTime? LockedUntil);

    public interface IPayablesService
    {
        Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(Guid clientId, CancellationToken ct);
        Task<SupplierDto> SaveSupplierAsync(Guid clientId, SaveSupplierRequest req, CancellationToken ct);
        Task<IReadOnlyList<BillDto>> ListBillsAsync(Guid clientId, BillFilter f, CancellationToken ct);
        Task<BillDto> CreateBillAsync(Guid clientId, CreateBillRequest req, CancellationToken ct);
        Task<BillDto> VoidBillAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<BillDto> PayBillAsync(Guid clientId, Guid id, PayBillRequest req, CancellationToken ct);
        Task<BillDto> VoidPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct);
        Task<AgeingDto> AgeingAsync(Guid clientId, string currency, DateTime asAt, CancellationToken ct);
        Task<SupplierStatementDto> StatementAsync(Guid clientId, Guid supplierId, string currency, DateTime from, DateTime to, CancellationToken ct);
    }
}
