namespace Fdms.Domain.Entities
{
    /// <summary>Money received from a customer towards a fiscal invoice that was left on account. Posting it debits the cash or bank account and credits receivables.</summary>
    public class ArPayment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>The fiscal invoice (receipt) it settles.</summary>
        public Guid ReceiptId { get; set; }
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public Guid AccountId { get; set; }
        public string? Reference { get; set; }
        public Guid? JournalEntryId { get; set; }
        public bool IsVoid { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Something the company owns and uses for years (a vehicle, a computer, equipment): bought once, written off over its life as depreciation.</summary>
    public class FixedAsset
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        public string? Code { get; set; }
        public DateTime PurchaseDate { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal Cost { get; set; }
        public decimal ResidualValue { get; set; }
        public int UsefulLifeMonths { get; set; }
        /// <summary>straight_line (equal amounts every month) or reducing (a yearly percentage of what is left).</summary>
        public string Method { get; set; } = "straight_line";
        public decimal RatePercent { get; set; }
        public Guid AssetAccountId { get; set; }
        public Guid AccumulatedAccountId { get; set; }
        public Guid ExpenseAccountId { get; set; }
        /// <summary>Active or Disposed.</summary>
        public string Status { get; set; } = "Active";
        public DateTime? DisposedOn { get; set; }
        public decimal? DisposalProceeds { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>One month's depreciation of one asset. (AssetId, Period) is unique, so a month is never charged twice.</summary>
    public class DepreciationCharge
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid AssetId { get; set; }
        /// <summary>The first day of the month charged.</summary>
        public DateTime Period { get; set; }
        public decimal Amount { get; set; }
        public Guid? JournalEntryId { get; set; }
    }
}
