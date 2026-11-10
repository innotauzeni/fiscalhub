namespace Fdms.Application.Abstractions
{
    // ---- cost centres ----
    public sealed record CostCentreDto(Guid Id, string Code, string Name, string Kind, Guid? BranchId, string? BranchName, bool IsActive, bool InUse);
    public sealed record SaveCostCentreRequest(Guid? Id, string Code, string Name, string Kind, Guid? BranchId, bool IsActive);
    public sealed record CentreColumn(Guid? Id, string Code, string Name);
    /// <summary>One account across the cost centres (Amounts in the order of the columns; the last column is "not assigned").</summary>
    public sealed record CentreRow(string Section, Guid AccountId, string Code, string Name, IReadOnlyList<decimal> Amounts, decimal Total);
    public sealed record CentreSummary(string Label, IReadOnlyList<decimal> Amounts, decimal Total);
    public sealed record ProfitLossByCentreDto(string Currency, DateTime From, DateTime To, IReadOnlyList<CentreColumn> Columns, IReadOnlyList<CentreRow> Rows, IReadOnlyList<CentreSummary> Summaries);

    public interface ICostCentreService
    {
        Task<IReadOnlyList<CostCentreDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<CostCentreDto> SaveAsync(Guid clientId, SaveCostCentreRequest req, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
    }

    // ---- cash flow and the financial dashboard ----
    public sealed record CashFlowLine(string Label, Guid? AccountId, decimal Amount);
    public sealed record CashFlowSection(string Key, string Title, IReadOnlyList<CashFlowLine> Lines, decimal Total);
    /// <summary>Indirect method: net profit, adjusted for non-cash items and working capital, then investing and financing. Reconciles: the sections add up to the change in cash.</summary>
    public sealed record CashFlowDto(string Currency, DateTime From, DateTime To, decimal NetProfit, IReadOnlyList<CashFlowSection> Sections, decimal NetChange, decimal OpeningCash, decimal ClosingCash, bool Reconciles);
    /// <summary>Unit: x (times), % or days. Value null: cannot be worked out (nothing to divide by).</summary>
    public sealed record RatioDto(string Key, string Label, decimal? Value, string Unit, string Hint);
    public sealed record BooksTrendPoint(string Month, decimal Revenue, decimal Expenses, decimal NetProfit, decimal Cash);
    public sealed record FinancialDashboardDto(string Currency, DateTime From, DateTime To, decimal Revenue, decimal GrossProfit, decimal NetProfit, decimal Cash, decimal Receivables, decimal Payables,
        IReadOnlyList<RatioDto> Ratios, IReadOnlyList<BooksTrendPoint> Trend, IReadOnlyList<CashFlowLine> TopExpenses);

    // ---- recurring journals, opening balances, year end ----
    public sealed record RecurringJournalDto(Guid Id, string Name, string Currency, string Memo, string? Reference, IReadOnlyList<JournalLineInput> Lines, string Frequency, DateTime NextDate, DateTime? EndDate,
        bool AutoReverse, bool IsActive, DateTime? LastRunAt, string? LastEntryNumber, string? LastError, int RunCount, decimal Total);
    public sealed record SaveRecurringJournalRequest(Guid? Id, string Name, string Currency, string Memo, string? Reference, IReadOnlyList<JournalLineInput> Lines, string Frequency, DateTime NextDate,
        DateTime? EndDate, bool AutoReverse, bool IsActive);
    public sealed record OpeningBalanceLine(Guid AccountId, decimal Debit, decimal Credit);
    /// <summary>Balances on the day before the company starts using FiscalHub's books; any difference goes to "Opening balance equity".</summary>
    public sealed record OpeningBalancesRequest(DateTime Date, string Currency, IReadOnlyList<OpeningBalanceLine> Lines);
    public sealed record YearEndCheck(string Label, bool Ok, string? Detail);
    public sealed record YearEndDto(DateTime YearStart, DateTime YearEnd, bool Closed, DateTime? ClosedAt, string? ClosedBy, IReadOnlyList<AmountDto> NetProfit, IReadOnlyList<YearEndCheck> Checks,
        DateTime? LockedUntil, IReadOnlyList<YearEndDto> History);
    public sealed record CloseYearRequest(DateTime YearEnd, bool Depreciate);

    public interface IBooksAutomationService
    {
        Task<IReadOnlyList<RecurringJournalDto>> ListRecurringAsync(Guid clientId, CancellationToken ct);
        Task<RecurringJournalDto> SaveRecurringAsync(Guid clientId, SaveRecurringJournalRequest req, CancellationToken ct);
        Task DeleteRecurringAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Posts the next occurrence now and moves the schedule on.</summary>
        Task<JournalDto> RunRecurringAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>The background round: posts every occurrence that is due (all companies). Returns how many entries were posted.</summary>
        Task<int> RunDueAsync(DateTime today, CancellationToken ct);
        Task<JournalDto> OpeningBalancesAsync(Guid clientId, OpeningBalancesRequest req, CancellationToken ct);
        /// <summary>The financial year that ends in the given year (null: the last one that has ended).</summary>
        Task<YearEndDto> YearEndAsync(Guid clientId, int? year, CancellationToken ct);
        Task<YearEndDto> CloseYearAsync(Guid clientId, CloseYearRequest req, CancellationToken ct);
        Task<YearEndDto> ReopenYearAsync(Guid clientId, DateTime yearEnd, CancellationToken ct);
    }

    // ---- statements as PDF and by email ----
    public sealed record StatementAgeing(decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90);
    public sealed record EmailStatementRequest(string? Customer, Guid? SupplierId, string Currency, DateTime From, DateTime To, string? Email, string? Message);
    public sealed record EmailStatementResult(string SentTo);

    public interface IStatementService
    {
        Task<(byte[] Pdf, string FileName)> CustomerPdfAsync(Guid clientId, string customer, string currency, DateTime from, DateTime to, CancellationToken ct);
        Task<(byte[] Pdf, string FileName)> SupplierPdfAsync(Guid clientId, Guid supplierId, string currency, DateTime from, DateTime to, CancellationToken ct);
        Task<EmailStatementResult> EmailAsync(Guid clientId, EmailStatementRequest req, CancellationToken ct);
    }

    // ---- exporting any accounting table ----
    /// <summary>Kind: text, money or number (money and number are right-aligned and numeric in Excel).</summary>
    public sealed record ExportColumn(string Header, string Kind = "text");
    /// <summary>Style: normal, bold, total, section or muted. Indent: levels (sub-accounts).</summary>
    public sealed record ExportRow(IReadOnlyList<string?> Cells, string Style = "normal", int Indent = 0);
    /// <summary>Format: pdf, xlsx or csv. Landscape for wide tables.</summary>
    public sealed record ExportRequest(string Format, string Title, string? Subtitle, IReadOnlyList<ExportColumn> Columns, IReadOnlyList<ExportRow> Rows, bool Landscape = false, string? FileName = null);

    public interface IReportExporter
    {
        Task<(byte[] Bytes, string ContentType, string FileName)> ExportAsync(Guid clientId, ExportRequest req, CancellationToken ct);
    }
}

namespace Fdms.Application.Abstractions
{
    // ---- cashbook documents: spend money, receive money, bank transfers ----
    /// <summary>One line of a payment or receipt: the account, what it was, the amount with VAT included, the VAT rate (0 = none) and an optional cost centre.</summary>
    public sealed record CashLineInput(Guid AccountId, string? Description, decimal Amount, decimal VatRate = 0, Guid? CostCentreId = null);
    public sealed record CashLineView(Guid AccountId, string AccountCode, string AccountName, string? Description, decimal Amount, decimal VatRate, decimal Vat, Guid? CostCentreId, string? CostCentre);
    /// <summary>Kind: payment, receipt or transfer. Post: false keeps it as a draft for review (Post to ledger); true posts it at once.</summary>
    public sealed record SaveCashDocumentRequest(Guid? Id, string Kind, DateTime Date, string Currency, Guid AccountId, Guid? ToAccountId, string? Contact, string? Reference, string? Memo,
        IReadOnlyList<CashLineInput>? Lines, decimal Amount, decimal Fee, bool Post);
    public sealed record CashDocumentDto(Guid Id, string Kind, string Number, DateTime Date, string Currency, Guid AccountId, string AccountName, Guid? ToAccountId, string? ToAccountName,
        string? Contact, string? Reference, string? Memo, IReadOnlyList<CashLineView> Lines, decimal Subtotal, decimal Vat, decimal Total, decimal Fee, string Status,
        Guid? JournalEntryId, string? JournalNumber, string? CreatedBy, DateTime CreatedAt, string? PostedBy, DateTime? PostedAt);
    /// <summary>DateBy: transaction (the document's date) or created. Status: Draft, Posted, Void or empty for all.</summary>
    public sealed record CashDocumentFilter(string? Kind, string? Status, DateTime? From, DateTime? To, string? DateBy, string? Q, Guid? AccountId);
    public sealed record CashDocumentPage(IReadOnlyList<CashDocumentDto> Items, IReadOnlyList<AmountDto> Totals, int Count);
    public sealed record PostBatchRequest(IReadOnlyList<Guid> Ids);
    public sealed record PostBatchResult(int Posted, IReadOnlyList<string> Problems);

    public interface ICashDocumentService
    {
        Task<CashDocumentPage> ListAsync(Guid clientId, CashDocumentFilter f, CancellationToken ct);
        Task<CashDocumentDto> GetAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<CashDocumentDto> SaveAsync(Guid clientId, SaveCashDocumentRequest req, CancellationToken ct);
        Task<CashDocumentDto> PostAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<PostBatchResult> PostBatchAsync(Guid clientId, IReadOnlyList<Guid> ids, CancellationToken ct);
        Task<CashDocumentDto> VoidAsync(Guid clientId, Guid id, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
    }
}
