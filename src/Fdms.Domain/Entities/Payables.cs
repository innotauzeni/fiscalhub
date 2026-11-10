namespace Fdms.Domain.Entities
{
    /// <summary>Per-company books settings. Everything dated on or before LockedUntil is closed: nothing can be posted into it any more.</summary>
    public class AccountingSetting
    {
        public Guid ClientId { get; set; }
        public DateTime? LockedUntil { get; set; }
        /// <summary>Days after an invoice date that a customer's balance falls due (for the receivables ageing).</summary>
        public int CustomerTermsDays { get; set; } = 30;
        /// <summary>How account codes look: 9 = a digit, anything else is a separator kept as typed (9999, 99999, 999-999, 99.99.99).</summary>
        public string CodeFormat { get; set; } = "9999";
        /// <summary>The first digit(s) of each type, as JSON {"Asset":"1",...}. Null: 1 assets, 2 liabilities, 3 equity, 4 income, 5 expenses.</summary>
        public string? ClassStartsJson { get; set; }
        /// <summary>The gap between sibling codes suggested under a parent (1010, 1020, … with 10).</summary>
        public int CodeStep { get; set; } = 10;
        /// <summary>When on, every new or changed code must follow CodeFormat and start with its type's digits.</summary>
        public bool EnforceCodeFormat { get; set; }
        /// <summary>The month the financial year starts (1 = January): the balance sheet splits retained earnings from this year's profit there.</summary>
        public int FiscalYearStartMonth { get; set; } = 1;
        /// <summary>The currency all others are shown in when the reports combine them (null: USD).</summary>
        public string? BaseCurrency { get; set; }
        /// <summary>Printed at the foot of the invoices issued from the books (bank details, terms).</summary>
        public string? InvoiceFooter { get; set; }
        /// <summary>The accounting policies note of the financial statements (null: the standard wording).</summary>
        public string? PolicyNotes { get; set; }
        /// <summary>The day stock started being counted here: sales from then on take stock out (null: inventory not started).</summary>
        public DateTime? InventoryStartDate { get; set; }
        /// <summary>How far the fiscal receipts have been read for stock (their CreatedAt).</summary>
        public DateTime? StockSyncedAt { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }
    }

    /// <summary>Someone the company buys from.</summary>
    public class Supplier
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        public string? Tin { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Address { get; set; }
        /// <summary>Days after the bill date that payment falls due, when the bill does not say.</summary>
        public int TermsDays { get; set; } = 30;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A supplier's invoice the company owes. Posting it debits the expense (and VAT input) and credits accounts payable.</summary>
    public class Bill
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid SupplierId { get; set; }
        /// <summary>The supplier's own invoice number.</summary>
        public string Number { get; set; } = "";
        public DateTime Date { get; set; }
        public DateTime DueDate { get; set; }
        public string Currency { get; set; } = "USD";
        public string? Memo { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Vat { get; set; }
        public decimal Total { get; set; }
        public Guid ExpenseAccountId { get; set; }
        public Guid? CostCentreId { get; set; }
        public Guid? JournalEntryId { get; set; }
        public bool IsVoid { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Money paid towards a bill. Posting it debits accounts payable and credits the cash or bank account.</summary>
    public class BillPayment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid BillId { get; set; }
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public Guid AccountId { get; set; }
        public string? Reference { get; set; }
        public Guid? JournalEntryId { get; set; }
        public bool IsVoid { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
