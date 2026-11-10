namespace Fdms.Application.Abstractions
{
    /// <summary>What a company's plan is for: fiscal (the full product), connector (the Zoho Books connector only) or accounting (the books only, no fiscal devices).</summary>
    public static class PlanKinds
    {
        public const string Fiscal = "fiscal", Connector = "connector", Accounting = "accounting";
        public static readonly string[] All = { Fiscal, Connector, Accounting };
    }

    // ---- sales invoices from the books (accounting-only companies; no VAT) ----
    public sealed record SalesInvoiceLineInput(Guid? ProductId, string Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent = 0, Guid? AccountId = null, Guid? CostCentreId = null);
    /// <summary>Kind: invoice or credit_note (a credit note names the invoice it reduces in OriginalId).</summary>
    public sealed record SaveSalesInvoiceRequest(Guid? Id, Guid CustomerId, DateTime Date, DateTime? DueDate, string Currency, string? Reference, string? Notes, IReadOnlyList<SalesInvoiceLineInput> Lines,
        string Kind = "invoice", Guid? OriginalId = null);
    public sealed record SalesInvoiceLineDto(Guid Id, Guid? ProductId, string Description, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal Amount, Guid AccountId, string Account, Guid? CostCentreId);
    /// <summary>Credited: credit notes issued against it. Paid: payments received. Outstanding: what the customer still owes on it.</summary>
    public sealed record SalesInvoiceDto(Guid Id, string Kind, string? Number, string Status, Guid CustomerId, string Customer, string? CustomerTin, string? CustomerEmail, DateTime Date, DateTime DueDate,
        string Currency, string? Reference, string? Notes, decimal Subtotal, decimal Discount, decimal Total, decimal Credited, decimal Paid, decimal Outstanding, int DaysOverdue,
        Guid? OriginalId, string? OriginalNumber, string? JournalNumber, string? CreatedBy, DateTime CreatedAt, IReadOnlyList<SalesInvoiceLineDto> Lines);
    /// <summary>Status: draft, open (issued, still owing), overdue, paid, void, or empty for all.</summary>
    public sealed record SalesInvoiceFilter(string? Status, string? Kind, Guid? CustomerId, string? Q, string? Currency, DateTime? From, DateTime? To, int Take = 200);
    /// <summary>Allowed: whether this company issues invoices from the books (accounting-only plans); Reason says why not.</summary>
    public sealed record SalesSetupDto(bool Allowed, string? Reason, Guid? DefaultIncomeAccountId, int TermsDays, string? Footer);
    public sealed record SaveInvoiceFooterRequest(string? Footer);
    public sealed record EmailInvoiceRequest(string? Email, string? Message);
    public sealed record EmailInvoiceResult(string SentTo);

    public interface ISalesInvoiceService
    {
        Task<SalesSetupDto> SetupAsync(Guid clientId, CancellationToken ct);
        Task<SalesSetupDto> SaveFooterAsync(Guid clientId, SaveInvoiceFooterRequest req, CancellationToken ct);
        Task<IReadOnlyList<SalesInvoiceDto>> ListAsync(Guid clientId, SalesInvoiceFilter f, CancellationToken ct);
        Task<SalesInvoiceDto> GetAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Creates or changes a draft.</summary>
        Task<SalesInvoiceDto> SaveAsync(Guid clientId, SaveSalesInvoiceRequest req, CancellationToken ct);
        Task DeleteDraftAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Gives the draft its number and posts it: receivables against income (a credit note the other way).</summary>
        Task<SalesInvoiceDto> IssueAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Takes an issued document back out of the books; refused while payments or credit notes stand against it.</summary>
        Task<SalesInvoiceDto> VoidAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>A draft invoice with the quote's customer and lines.</summary>
        Task<SalesInvoiceDto> FromQuoteAsync(Guid clientId, Guid quoteId, CancellationToken ct);
        Task<(byte[] Pdf, string FileName)> PdfAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<EmailInvoiceResult> EmailAsync(Guid clientId, Guid id, EmailInvoiceRequest req, CancellationToken ct);
    }

    // ---- starting the books here: balances, customers' unpaid invoices, suppliers' unpaid bills ----
    /// <summary>One account of the trial balance being brought in. Name and Type are only needed for an account that is not in the chart yet.</summary>
    public sealed record ConversionBalanceRow(string Code, string? Name, string? Type, decimal Debit, decimal Credit, string? SubType = null);
    /// <summary>One unpaid customer invoice or supplier bill at the conversion date: who, its number, date, due date and the amount still owing.</summary>
    public sealed record ConversionPartyRow(string Name, string Number, DateTime Date, DateTime? DueDate, decimal Amount, string? Email = null, string? Tin = null);
    /// <summary>
    /// Date: the conversion date (balances as at the end of that day). PostDifferenceToEquity: a trial balance that does not balance is still posted, the difference going to
    /// opening balance equity (to be investigated).
    /// </summary>
    public sealed record ConversionRequest(DateTime Date, string Currency, IReadOnlyList<ConversionBalanceRow>? Balances, IReadOnlyList<ConversionPartyRow>? Customers,
        IReadOnlyList<ConversionPartyRow>? Suppliers, bool PostDifferenceToEquity = false);
    /// <summary>SubType: where the account shows in the statements (for a new account: as given, or like the account just before it).</summary>
    public sealed record ConversionLineDto(int Line, string Code, string Name, string Type, bool IsNew, decimal Debit, decimal Credit, string? Note, string? SubType = null);
    /// <summary>What would be posted, with every check: the trial balance balances, receivables and payables agree with the customers' and suppliers' detail.</summary>
    public sealed record ConversionCheckDto(IReadOnlyList<ConversionLineDto> Lines, int NewAccounts, decimal TotalDebit, decimal TotalCredit, decimal Difference,
        decimal ReceivablesInBalances, decimal CustomerInvoices, decimal PayablesInBalances, decimal SupplierBills, int NewCustomers, int NewSuppliers,
        IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, bool CanPost);
    public sealed record ConversionResultDto(string? JournalNumber, int AccountsCreated, int CustomerInvoices, int SupplierBills, int CustomersCreated, int SuppliersCreated);
    /// <summary>A file read into rows (nothing is posted). Kind: balances, customers or suppliers.</summary>
    public sealed record ConversionFileDto(string Kind, IReadOnlyList<ConversionBalanceRow> Balances, IReadOnlyList<ConversionPartyRow> Parties, IReadOnlyList<string> Problems);
    public sealed record ConversionDoneDto(string Currency, DateTime Date, string Number, Guid EntryId, int CustomerInvoices, int SupplierBills);

    public interface IConversionService
    {
        Task<IReadOnlyList<ConversionDoneDto>> StatusAsync(Guid clientId, CancellationToken ct);
        (byte[] Bytes, string FileName) Template(string kind);
        Task<ConversionFileDto> ReadAsync(Guid clientId, string kind, string fileName, Stream file, CancellationToken ct);
        Task<ConversionCheckDto> CheckAsync(Guid clientId, ConversionRequest req, CancellationToken ct);
        Task<ConversionResultDto> PostAsync(Guid clientId, ConversionRequest req, CancellationToken ct);
    }

    // ---- exchange rates and the base currency ----
    public sealed record ExchangeRateDto(Guid Id, string Currency, DateTime Date, decimal Rate);
    /// <summary>Rate: what one unit of Currency is worth in the base currency from Date on.</summary>
    public sealed record SaveExchangeRateRequest(string Currency, DateTime Date, decimal Rate);
    public sealed record RatesDto(string BaseCurrency, IReadOnlyList<string> Currencies, IReadOnlyList<ExchangeRateDto> Rates);
    public sealed record SetBaseCurrencyRequest(string BaseCurrency);

    public interface IExchangeRateService
    {
        Task<RatesDto> ListAsync(Guid clientId, CancellationToken ct);
        Task<RatesDto> SaveAsync(Guid clientId, SaveExchangeRateRequest req, CancellationToken ct);
        Task<RatesDto> DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<RatesDto> SetBaseAsync(Guid clientId, SetBaseCurrencyRequest req, CancellationToken ct);
    }

    // ---- prepayments and deferred income ----
    public sealed record ScheduleMonthDto(DateTime Period, decimal Amount, bool Posted, string? JournalNumber);
    public sealed record ScheduleDto(Guid Id, string Name, string Kind, string Currency, decimal Amount, DateTime StartMonth, int Months, Guid BalanceAccountId, string BalanceAccount,
        Guid PnlAccountId, string PnlAccount, Guid? CostCentreId, string Status, string? Notes, decimal Released, decimal Remaining, int MonthsReleased, IReadOnlyList<ScheduleMonthDto> Plan);
    /// <summary>Kind: prepayment (asset released to an expense) or deferred_income (liability released to income). StartMonth: any day of the first month.</summary>
    public sealed record SaveScheduleRequest(Guid? Id, string Name, string Kind, string Currency, decimal Amount, DateTime StartMonth, int Months, Guid BalanceAccountId, Guid PnlAccountId,
        Guid? CostCentreId, string? Notes);
    public sealed record RunSchedulesResult(int Posted);

    public interface IScheduleService
    {
        Task<IReadOnlyList<ScheduleDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<ScheduleDto> SaveAsync(Guid clientId, SaveScheduleRequest req, CancellationToken ct);
        /// <summary>Stops releasing what is left (the months already released stay).</summary>
        Task<ScheduleDto> CancelAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Releases every month that has ended and is not released yet, for one company.</summary>
        Task<RunSchedulesResult> RunAsync(Guid clientId, CancellationToken ct);
        /// <summary>The same for every company (the hourly job).</summary>
        Task<int> RunAllDueAsync(DateTime today, CancellationToken ct);
    }

    // ---- statements, notes and the audit trail ----
    /// <summary>One component of equity: what it was at the start, the profit for the period it took, other movements (capital in, drawings, dividends), and the end.</summary>
    public sealed record EquityMovementRow(string Component, Guid? AccountId, decimal Opening, decimal Profit, decimal Other, decimal Closing);
    public sealed record ChangesInEquityDto(string Currency, DateTime From, DateTime To, IReadOnlyList<EquityMovementRow> Rows, EquityMovementRow Total);
    public sealed record PolicyNotesDto(string Text, bool IsStandard);
    public sealed record SavePolicyNotesRequest(string? Text);
    public sealed record BooksAuditRow(long Id, DateTime At, string? User, string Action, string What, string? Detail, string? IpAddress);
    public sealed record BooksAuditFilter(DateTime? From, DateTime? To, string? Q, string? User, int Take = 300);

    public interface IFinancialStatementsService
    {
        Task<ChangesInEquityDto> ChangesInEquityAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct);
        Task<PolicyNotesDto> PolicyNotesAsync(Guid clientId, CancellationToken ct);
        Task<PolicyNotesDto> SavePolicyNotesAsync(Guid clientId, SavePolicyNotesRequest req, CancellationToken ct);
        /// <summary>The annual financial statements as one PDF: financial position, profit or loss, changes in equity, cash flows and notes, with the year before alongside.</summary>
        Task<(byte[] Pdf, string FileName)> PackAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct);
        Task<IReadOnlyList<BooksAuditRow>> AuditAsync(Guid clientId, BooksAuditFilter f, CancellationToken ct);
    }
}
