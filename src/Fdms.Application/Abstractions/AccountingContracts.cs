namespace Fdms.Application.Abstractions
{
    // ---- chart of accounts and journals ----
    /// <summary>SubType is where the account appears in the statements (never null here: the default for its type when not chosen). Level: 0 for a top account.</summary>
    public sealed record LedgerAccountDto(Guid Id, string Code, string Name, string Type, string? Role, bool IsActive, bool IsSystem, Guid? BankAccountId, bool InUse,
        Guid? ParentId = null, bool IsGroup = false, string? SubType = null, string? Description = null, int Level = 0, bool HasChildren = false);
    public sealed record SaveLedgerAccountRequest(Guid? Id, string Code, string Name, string Type, bool IsActive, Guid? ParentId = null, bool IsGroup = false, string? SubType = null, string? Description = null);

    /// <summary>
    /// How a company numbers its accounts. CodeFormat: 9 is a digit, anything else a separator (9999, 999999, 99-999, 9.99.999). ClassStarts: the first digit(s) of each type.
    /// CodeStep: the gap between suggested sibling codes. EnforceCodeFormat: refuse codes that do not follow it. FiscalYearStartMonth: 1-12.
    /// </summary>
    public sealed record ChartSettingsDto(string CodeFormat, IReadOnlyDictionary<string, string> ClassStarts, int CodeStep, bool EnforceCodeFormat, int FiscalYearStartMonth);
    public sealed record SubTypeDto(string Key, string Label, string Type);
    public sealed record ChartSetupDto(ChartSettingsDto Settings, IReadOnlyList<SubTypeDto> SubTypes, string Example);
    public sealed record NextCodeDto(string? Code, string? Problem);
    public sealed record RenumberRow(Guid Id, string Name, string Type, string OldCode, string NewCode);
    public sealed record RenumberPreviewDto(IReadOnlyList<RenumberRow> Rows, IReadOnlyList<string> Problems, int Changed);
    public sealed record ImportChartRequest(string Csv);
    public sealed record ImportChartResult(int Created, int Updated, int Skipped, IReadOnlyList<string> Problems);

    public sealed record JournalLineInput(Guid AccountId, decimal Debit, decimal Credit, string? Description, Guid? CostCentreId = null);
    /// <summary>ReverseOn: an accrual or prepayment — the same entry is reversed on that date (for example the first day of next month).</summary>
    public sealed record CreateJournalRequest(DateTime Date, string Currency, string Memo, string? Reference, IReadOnlyList<JournalLineInput> Lines, DateTime? ReverseOn = null);
    public sealed record JournalLineDto(Guid Id, Guid AccountId, string AccountCode, string AccountName, decimal Debit, decimal Credit, string? Description, Guid? CostCentreId = null, string? CostCentre = null);
    public sealed record JournalDto(Guid Id, string Number, DateTime Date, string Currency, string Memo, string? Reference, string Source, bool Reversed, bool IsReversal, string? CreatedBy, decimal Total, IReadOnlyList<JournalLineDto> Lines);
    public sealed record JournalFilter(DateTime? From, DateTime? To, string? Source, string? Q, string? Currency, int Take = 100);
    public sealed record SyncResultDto(int Posted);

    public sealed record CashbookEntryRequest(DateTime Date, Guid AccountId, string Kind, Guid CounterAccountId, decimal Amount, string Currency, string Description, string? Reference);

    // ---- reports ----
    public sealed record AccountingMeta(IReadOnlyList<string> Currencies, string DefaultCurrency, IReadOnlyList<LedgerAccountDto> CashAccounts, DateTime FirstEntry);
    /// <summary>AccountCode/AccountName: set when the ledger includes sub-accounts. Source and SourceId point at the document the entry came from (sales: the fiscal receipt).</summary>
    public sealed record LedgerLineDto(Guid EntryId, DateTime Date, string Number, string Memo, string? Reference, decimal Debit, decimal Credit, decimal Balance,
        string? AccountCode = null, string? AccountName = null, string? Source = null, Guid? SourceId = null);
    public sealed record LedgerReportDto(LedgerAccountDto Account, string Currency, DateTime From, DateTime To, decimal Opening, IReadOnlyList<LedgerLineDto> Lines, decimal TotalDebit, decimal TotalCredit, decimal Closing,
        bool IncludesSubAccounts = false);

    /// <summary>
    /// One account of the trial balance. Debit/Credit: its own closing balance on its side. Opening (before From), PeriodDebit/PeriodCredit (From to AsAt) and Closing are
    /// debit-positive. The Subtotal fields add every sub-account below it (equal to its own figures when it has none).
    /// </summary>
    public sealed record TrialBalanceRow(Guid AccountId, string Code, string Name, string Type, decimal Debit, decimal Credit,
        string? SubType = null, Guid? ParentId = null, int Level = 0, bool HasChildren = false, bool IsGroup = false,
        decimal Opening = 0, decimal PeriodDebit = 0, decimal PeriodCredit = 0, decimal Closing = 0,
        decimal SubtotalOpening = 0, decimal SubtotalPeriodDebit = 0, decimal SubtotalPeriodCredit = 0, decimal SubtotalClosing = 0);
    public sealed record TrialBalanceDto(string Currency, DateTime AsAt, IReadOnlyList<TrialBalanceRow> Rows, decimal TotalDebit, decimal TotalCredit, bool Balanced,
        DateTime? From = null, decimal TotalOpeningDebit = 0, decimal TotalOpeningCredit = 0, decimal TotalPeriodDebit = 0, decimal TotalPeriodCredit = 0);

    /// <summary>Amount: the account's own figure (rows add up to the section total). Subtotal: with its sub-accounts, for an account that has some. Compare: the comparative period.</summary>
    public sealed record StatementRow(Guid AccountId, string Code, string Name, decimal Amount, decimal? Compare = null, int Level = 0, Guid? ParentId = null, bool HasChildren = false,
        decimal? Subtotal = null, decimal? CompareSubtotal = null);
    public sealed record StatementSection(string Title, IReadOnlyList<StatementRow> Rows, decimal Total, string? Key = null, decimal? CompareTotal = null);
    /// <summary>Revenue − cost of sales = gross profit; − operating expenses + other income = operating profit; − finance costs = profit before tax; − income tax = net profit.</summary>
    public sealed record ProfitLossDto(string Currency, DateTime From, DateTime To, IReadOnlyList<StatementSection> Sections, decimal GrossProfit, decimal NetProfit,
        decimal OperatingProfit = 0, decimal ProfitBeforeTax = 0, DateTime? CompareFrom = null, DateTime? CompareTo = null,
        decimal? CompareGrossProfit = null, decimal? CompareOperatingProfit = null, decimal? CompareProfitBeforeTax = null, decimal? CompareNetProfit = null);
    public sealed record BalanceSheetDto(string Currency, DateTime AsAt, IReadOnlyList<StatementSection> Sections, decimal TotalAssets, decimal TotalLiabilitiesAndEquity, bool Balanced,
        decimal TotalLiabilities = 0, decimal TotalEquity = 0, DateTime? CompareAsAt = null, decimal? CompareTotalAssets = null, decimal? CompareTotalLiabilities = null,
        decimal? CompareTotalEquity = null, decimal? CompareTotalLiabilitiesAndEquity = null);
    /// <summary>OtherAccounts: the other side(s) of the entry. Source: what posted it (sales, payment, receipt, transfer, bill …).</summary>
    public sealed record CashLineDto(Guid JournalLineId, Guid EntryId, DateTime Date, string Number, string Description, string? Reference, decimal Receipt, decimal Payment, decimal Balance, bool Reconciled,
        string? OtherAccounts = null, string? Source = null);
    public sealed record CashbookDto(LedgerAccountDto Account, string Currency, DateTime From, DateTime To, decimal Opening, IReadOnlyList<CashLineDto> Lines, decimal TotalReceipts, decimal TotalPayments, decimal Closing);

    // ---- bank reconciliation ----
    public sealed record StatementLineDto(Guid Id, DateTime Date, string Description, string? Reference, decimal Amount, bool Matched, Guid? JournalLineId);
    public sealed record ImportStatementRequest(Guid AccountId, string Csv);
    public sealed record ImportStatementResult(int Imported, int Duplicates, int Skipped, IReadOnlyList<string> Problems);
    public sealed record AddStatementLineRequest(Guid AccountId, DateTime Date, string Description, string? Reference, decimal Amount);
    public sealed record ReconLedgerLine(Guid JournalLineId, Guid EntryId, DateTime Date, string Number, string Description, string? Reference, decimal Amount, bool Matched);
    public sealed record MatchSuggestion(Guid StatementLineId, Guid JournalLineId);
    public sealed record ReconciliationDto(LedgerAccountDto Account, DateTime AsAt, decimal LedgerBalance, decimal? StatementBalance, decimal UnmatchedLedger, decimal UnmatchedStatement, decimal? Difference,
        IReadOnlyList<StatementLineDto> StatementLines, IReadOnlyList<ReconLedgerLine> LedgerLines, IReadOnlyList<MatchSuggestion> Suggestions);
    public sealed record MatchRequest(Guid StatementLineId, Guid JournalLineId);
    public sealed record CreateFromStatementRequest(Guid StatementLineId, Guid CounterAccountId, string? Memo);

    public interface IAccountingService
    {
        Task EnsureChartAsync(Guid clientId, CancellationToken ct);
        Task<IReadOnlyList<LedgerAccountDto>> ListAccountsAsync(Guid clientId, CancellationToken ct);
        Task<LedgerAccountDto> SaveAccountAsync(Guid clientId, SaveLedgerAccountRequest req, CancellationToken ct);
        Task DeleteAccountAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<IReadOnlyList<JournalDto>> ListJournalsAsync(Guid clientId, JournalFilter f, CancellationToken ct);
        Task<JournalDto> GetJournalAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<JournalDto> CreateJournalAsync(Guid clientId, CreateJournalRequest req, CancellationToken ct);
        Task<JournalDto> ReverseJournalAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Posts every confirmed fiscal receipt that is not in the books yet (invoices, credit and debit notes). Safe to repeat.</summary>
        Task<SyncResultDto> SyncSalesAsync(Guid clientId, CancellationToken ct);
        Task<JournalDto> CashbookEntryAsync(Guid clientId, CashbookEntryRequest req, CancellationToken ct);
        /// <summary>Everything dated on or before this date is closed (null = nothing is closed).</summary>
        Task<DateTime?> LockedUntilAsync(Guid clientId, CancellationToken ct);
        Task<PeriodDto> SetLockAsync(Guid clientId, DateTime? lockedUntil, CancellationToken ct);
        /// <summary>Posts one balanced entry on behalf of another module (bills, payments). Refuses a date in a closed period.</summary>
        Task<JournalDto> PostEntryAsync(Guid clientId, DateTime date, string currency, string memo, string? reference, string source, Guid? sourceId, IReadOnlyList<(Guid Account, decimal Debit, decimal Credit)> lines, CancellationToken ct,
            (Guid Account, Guid CostCentre)? tag = null);
        /// <summary>Reverses an entry on a given date (an accrual undone at the start of the next period).</summary>
        Task<JournalDto> ReverseAtAsync(Guid clientId, Guid id, DateTime? on, CancellationToken ct);
        Task CheckCentresAsync(Guid clientId, IEnumerable<Guid?> ids, CancellationToken ct);
        /// <summary>Posts an entry from another module with a description and cost centre on each line (cashbook documents). Refuses headings and closed periods.</summary>
        Task<JournalDto> PostLinesAsync(Guid clientId, DateTime date, string currency, string memo, string? reference, string source, Guid? sourceId, IReadOnlyList<JournalLineInput> lines, CancellationToken ct);
    }

    public interface IAccountingReports
    {
        Task<AccountingMeta> MetaAsync(Guid clientId, CancellationToken ct);
        Task<LedgerReportDto> LedgerAsync(Guid clientId, Guid accountId, string currency, DateTime from, DateTime to, CancellationToken ct, bool includeSubAccounts = false, Guid? costCentreId = null);
        /// <summary>From: the start of the period whose movements are shown (null: the first day of AsAt's financial year). IncludeZero: also accounts with nothing on them.</summary>
        Task<TrialBalanceDto> TrialBalanceAsync(Guid clientId, string currency, DateTime asAt, CancellationToken ct, DateTime? from = null, bool includeZero = false);
        /// <summary>Compare: none, previous (the same length just before) or year (the same dates a year earlier).</summary>
        Task<ProfitLossDto> ProfitLossAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct, string? compare = null, Guid? costCentreId = null);
        /// <summary>Income and expenses side by side for every cost centre, plus what was not assigned to one.</summary>
        Task<ProfitLossByCentreDto> ProfitLossByCentreAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct);
        Task<CashFlowDto> CashFlowAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct);
        Task<FinancialDashboardDto> DashboardAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct);
        /// <summary>Compare: none, year (a year earlier) or a date (yyyy-MM-dd).</summary>
        Task<BalanceSheetDto> BalanceSheetAsync(Guid clientId, string currency, DateTime asAt, CancellationToken ct, string? compare = null);
        Task<CashbookDto> CashbookAsync(Guid clientId, Guid accountId, string currency, DateTime from, DateTime to, CancellationToken ct);
        /// <summary>
        /// From now on (this request) the trial balance, profit and loss and balance sheet combine every currency into the base currency, which is returned: assets and
        /// liabilities at the rate on the report date, income, expenses and equity at the rate on each transaction's date, the difference shown as a translation reserve.
        /// </summary>
        Task<string> UseBaseCurrencyAsync(Guid clientId, CancellationToken ct);
    }

    /// <summary>The chart of accounts' numbering: the code format, suggested codes, renumbering the whole chart, and importing a chart from CSV.</summary>
    public interface IChartService
    {
        Task<ChartSetupDto> SetupAsync(Guid clientId, CancellationToken ct);
        Task<ChartSetupDto> SaveSettingsAsync(Guid clientId, ChartSettingsDto req, CancellationToken ct);
        Task<NextCodeDto> NextCodeAsync(Guid clientId, string type, Guid? parentId, CancellationToken ct);
        /// <summary>What every code becomes under the new format and class digits (nothing is changed).</summary>
        Task<RenumberPreviewDto> PreviewRenumberAsync(Guid clientId, ChartSettingsDto req, CancellationToken ct);
        /// <summary>Saves the new format and renumbers the chart in one go; refused while any problem remains.</summary>
        Task<RenumberPreviewDto> RenumberAsync(Guid clientId, ChartSettingsDto req, CancellationToken ct);
        Task<ImportChartResult> ImportAsync(Guid clientId, ImportChartRequest req, CancellationToken ct);
    }

    public interface IBankReconService
    {
        Task<ImportStatementResult> ImportAsync(Guid clientId, ImportStatementRequest req, CancellationToken ct);
        Task<StatementLineDto> AddLineAsync(Guid clientId, AddStatementLineRequest req, CancellationToken ct);
        Task DeleteLineAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<ReconciliationDto> ReconciliationAsync(Guid clientId, Guid accountId, DateTime asAt, decimal? statementBalance, CancellationToken ct);
        Task MatchAsync(Guid clientId, MatchRequest req, CancellationToken ct);
        Task UnmatchAsync(Guid clientId, Guid statementLineId, CancellationToken ct);
        Task<int> AutoMatchAsync(Guid clientId, Guid accountId, CancellationToken ct);
        Task<JournalDto> CreateEntryAsync(Guid clientId, CreateFromStatementRequest req, CancellationToken ct);
    }
}
