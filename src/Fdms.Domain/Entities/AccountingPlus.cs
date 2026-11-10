namespace Fdms.Domain.Entities
{
    /// <summary>
    /// A department, project or branch that income and costs can be tagged with, so profit can be read per cost centre. A branch cost centre (BranchId set)
    /// receives the sales of that branch's devices automatically.
    /// </summary>
    public class CostCentre
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>department, project or branch.</summary>
        public string Kind { get; set; } = "department";
        public Guid? BranchId { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A journal posted again and again on a schedule (rent, salaries accruals, loan interest). Lines are kept as JSON (account, debit, credit, description, cost centre).
    /// AutoReverse: each posting is reversed on the first day of the next period (an accrual).
    /// </summary>
    public class RecurringJournal
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        public string Currency { get; set; } = "USD";
        public string Memo { get; set; } = "";
        public string? Reference { get; set; }
        public string LinesJson { get; set; } = "[]";
        /// <summary>weekly, monthly, quarterly or yearly.</summary>
        public string Frequency { get; set; } = "monthly";
        public DateTime NextDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool AutoReverse { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime? LastRunAt { get; set; }
        public string? LastEntryNumber { get; set; }
        public string? LastError { get; set; }
        public int RunCount { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A financial year that was closed: its period is locked and its result stays in retained earnings.</summary>
    public class YearClose
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public DateTime YearStart { get; set; }
        public DateTime YearEnd { get; set; }
        /// <summary>The year's net profit per currency, as JSON [{currency, amount}].</summary>
        public string ResultJson { get; set; } = "[]";
        public DateTime ClosedAt { get; set; } = DateTime.UtcNow;
        public string? ClosedBy { get; set; }
        public DateTime? ReopenedAt { get; set; }
        public string? ReopenedBy { get; set; }
    }
}

namespace Fdms.Domain.Entities
{
    /// <summary>
    /// A cashbook document: money spent from or received into a cash or bank account (with one or more lines, VAT included), or a transfer between two of them.
    /// Kind: payment, receipt or transfer. Status: Draft (not in the books yet), Posted (JournalEntryId set) or Void (the journal was reversed).
    /// </summary>
    public class CashDocument
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Kind { get; set; } = "payment";
        public int Seq { get; set; }
        public string Number { get; set; } = "";
        public DateTime Date { get; set; }
        public string Currency { get; set; } = "USD";
        /// <summary>The cash or bank ledger account the money leaves (payment, transfer) or comes into (receipt).</summary>
        public Guid AccountId { get; set; }
        /// <summary>Transfers: the account the money goes to.</summary>
        public Guid? ToAccountId { get; set; }
        /// <summary>Who was paid or who paid.</summary>
        public string? Contact { get; set; }
        public string? Reference { get; set; }
        public string? Memo { get; set; }
        /// <summary>The lines as JSON: [{accountId, description, amount (VAT included), vatRate, costCentreId}].</summary>
        public string LinesJson { get; set; } = "[]";
        public decimal Subtotal { get; set; }
        public decimal Vat { get; set; }
        public decimal Total { get; set; }
        /// <summary>Transfers: a bank charge taken from the paying account.</summary>
        public decimal Fee { get; set; }
        public string Status { get; set; } = "Draft";
        public Guid? JournalEntryId { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? PostedBy { get; set; }
        public DateTime? PostedAt { get; set; }
        public string? VoidedBy { get; set; }
        public DateTime? VoidedAt { get; set; }
    }
}
