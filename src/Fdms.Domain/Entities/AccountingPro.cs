namespace Fdms.Domain.Entities
{
    /// <summary>
    /// An invoice or credit note issued from the books, not through a fiscal device: for companies on an accounting-only plan (no VAT is charged on them), and the customers'
    /// unpaid invoices brought in when a company starts its books here (Kind "opening"). Issuing it posts to receivables; payments are received against it like a fiscal invoice.
    /// </summary>
    public class SalesInvoice
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>invoice, credit_note or opening (an unpaid invoice from before the company's books started here).</summary>
        public string Kind { get; set; } = "invoice";
        /// <summary>INV-000001 / CN-000001, given when issued (a draft has none yet); an opening invoice keeps the number it had.</summary>
        public string? Number { get; set; }
        public int Seq { get; set; }
        public Guid CustomerId { get; set; }
        /// <summary>The customer as it was when issued, so a later change to the customer never changes an issued document.</summary>
        public string CustomerName { get; set; } = "";
        public string? CustomerTin { get; set; }
        public string? CustomerEmail { get; set; }
        public string? CustomerAddress { get; set; }
        public DateTime Date { get; set; }
        public DateTime DueDate { get; set; }
        public string Currency { get; set; } = "USD";
        public string? Reference { get; set; }
        public string? Notes { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Discount { get; set; }
        public decimal Total { get; set; }
        /// <summary>Draft, Issued or Void.</summary>
        public string Status { get; set; } = "Draft";
        /// <summary>The invoice a credit note reduces.</summary>
        public Guid? OriginalId { get; set; }
        public Guid? QuoteId { get; set; }
        public Guid? JournalEntryId { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? IssuedAt { get; set; }
        public DateTime? VoidedAt { get; set; }
        public List<SalesInvoiceLine> Lines { get; set; } = new();
    }

    public class SalesInvoiceLine
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid InvoiceId { get; set; }
        public Guid ClientId { get; set; }
        public int Sort { get; set; }
        public Guid? ProductId { get; set; }
        public string Description { get; set; } = "";
        public decimal Quantity { get; set; } = 1;
        public decimal UnitPrice { get; set; }
        public decimal DiscountPercent { get; set; }
        /// <summary>Quantity x price less the discount, rounded to cents.</summary>
        public decimal Amount { get; set; }
        /// <summary>The income account the line is credited to.</summary>
        public Guid AccountId { get; set; }
        public Guid? CostCentreId { get; set; }
    }

    /// <summary>What one unit of a currency was worth in the company's base currency from a date on (until the next rate). Used to show all currencies together in one.</summary>
    public class ExchangeRate
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Currency { get; set; } = "";
        public DateTime Date { get; set; }
        public decimal Rate { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// An amount paid or received in advance and released to profit or loss month by month: a prepayment (insurance, rent, a licence paid for a year) is an asset released to an
    /// expense; deferred income (a subscription or service paid for in advance) is a liability released to income.
    /// </summary>
    public class AmortisationSchedule
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        /// <summary>prepayment or deferred_income.</summary>
        public string Kind { get; set; } = "prepayment";
        public string Currency { get; set; } = "USD";
        public decimal Amount { get; set; }
        /// <summary>The first month released (its first day).</summary>
        public DateTime StartMonth { get; set; }
        public int Months { get; set; }
        /// <summary>The balance sheet account it sits in (prepayments, or deferred income).</summary>
        public Guid BalanceAccountId { get; set; }
        /// <summary>The expense (or income) account each month's share goes to.</summary>
        public Guid PnlAccountId { get; set; }
        public Guid? CostCentreId { get; set; }
        /// <summary>Active, Finished or Cancelled.</summary>
        public string Status { get; set; } = "Active";
        public string? Notes { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>One month released from a schedule. (ScheduleId, Period) is unique, so a month is never released twice.</summary>
    public class AmortisationCharge
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid ScheduleId { get; set; }
        public DateTime Period { get; set; }
        public decimal Amount { get; set; }
        public Guid? JournalEntryId { get; set; }
    }
}
