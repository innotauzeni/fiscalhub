namespace Fdms.Domain.Entities
{
    /// <summary>An account in a company's chart of accounts. Type decides which side increases it and which report it appears on.</summary>
    public class LedgerAccount
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Asset, Liability, Equity, Income or Expense.</summary>
        public string Type { get; set; } = "Asset";
        /// <summary>What the system posts to it automatically: cash, bank, mobile, card, receivable, other, vat_output, sales, sales_returns, retained, bank_charges. Empty for ordinary accounts.</summary>
        public string? Role { get; set; }
        public bool IsActive { get; set; } = true;
        /// <summary>Part of the standard chart: can be renamed but not deleted.</summary>
        public bool IsSystem { get; set; }
        /// <summary>When set, this account is the ledger side of one of the company's bank accounts (see Banks).</summary>
        public Guid? BankAccountId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>The account this one sits under (same type). Reports roll children up into their parent.</summary>
        public Guid? ParentId { get; set; }
        /// <summary>A heading that only groups other accounts: nothing can be posted to it.</summary>
        public bool IsGroup { get; set; }
        /// <summary>Where it appears in the statements: cash, current_asset, fixed_asset, non_current_asset, current_liability, non_current_liability, equity,
        /// revenue, other_income, cost_of_sales, operating_expense, finance_cost, income_tax. Null: the default for its type.</summary>
        public string? SubType { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>One balanced posting (debits equal credits) in one currency. Posted entries are never edited: a mistake is reversed by a new entry.</summary>
    public class JournalEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>Running number per company; Number is how it is shown (JV-000012).</summary>
        public int Seq { get; set; }
        public string Number { get; set; } = "";
        public DateTime Date { get; set; }
        public string Currency { get; set; } = "USD";
        public string Memo { get; set; } = "";
        public string? Reference { get; set; }
        /// <summary>manual, sales, cashbook, bank or reversal.</summary>
        public string Source { get; set; } = "manual";
        /// <summary>The fiscal receipt a sales entry was posted from. Unique per company, so a receipt is never posted twice.</summary>
        public Guid? SourceId { get; set; }
        public Guid? ReversesId { get; set; }
        public Guid? ReversedById { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<JournalLine> Lines { get; set; } = new();
    }

    public class JournalLine
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid EntryId { get; set; }
        public Guid ClientId { get; set; }
        public Guid AccountId { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public string? Description { get; set; }
        /// <summary>The department, project or branch the amount belongs to (optional).</summary>
        public Guid? CostCentreId { get; set; }
        /// <summary>Copied from the entry so reports and reconciliation need no join.</summary>
        public DateTime Date { get; set; }
        public string Currency { get; set; } = "USD";
    }

    /// <summary>A line of a bank statement the company imported, to be matched with what its books say. Positive = money in, negative = money out.</summary>
    public class BankStatementLine
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>The ledger account of the bank account this statement is for.</summary>
        public Guid AccountId { get; set; }
        public DateTime Date { get; set; }
        public string Description { get; set; } = "";
        public string? Reference { get; set; }
        public decimal Amount { get; set; }
        public Guid? JournalLineId { get; set; }
        public DateTime? MatchedAt { get; set; }
        /// <summary>Date, amount, description and reference together: importing the same statement twice adds nothing.</summary>
        public string Fingerprint { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
