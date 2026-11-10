namespace Fdms.Domain.Entities
{
    /// <summary>A category of stock items; a category with a parent is a subcategory. It can post its items to its own inventory and cost of sales accounts.</summary>
    public class StockCategory
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid? ParentId { get; set; }
        public string Name { get; set; } = "";
        public string? Code { get; set; }
        /// <summary>Where its items' value is held (null: the parent's, then the default inventory account).</summary>
        public Guid? InventoryAccountId { get; set; }
        /// <summary>Where the cost of its items sold goes (null: the parent's, then the default cost of sales account).</summary>
        public Guid? CogsAccountId { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A place stock is kept: a shop, a warehouse, a van. It can belong to a branch, so that branch's fiscal sales take stock from it.</summary>
    public class StockLocation
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public Guid? BranchId { get; set; }
        /// <summary>Where sales from tills without a location of their own take stock from.</summary>
        public bool IsDefault { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>How many of an item are at a location now. Kept up to date with every movement.</summary>
    public class StockLevel
    {
        public Guid ClientId { get; set; }
        public Guid ProductId { get; set; }
        public Guid LocationId { get; set; }
        public decimal Quantity { get; set; }
    }

    /// <summary>
    /// A stock document: a goods received voucher (grv), a transfer between locations, an adjustment or stock take, opening stock, or the stock side of a sale or a return
    /// (sale / return, from a fiscal receipt or an invoice from the books). Posting it moves the stock and, where value changes hands, posts to the books.
    /// </summary>
    public class StockDocument
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>grv, transfer, adjustment, opening, sale or return.</summary>
        public string Kind { get; set; } = "grv";
        public string? Number { get; set; }
        public int Seq { get; set; }
        public DateTime Date { get; set; }
        /// <summary>Draft, Posted or Void.</summary>
        public string Status { get; set; } = "Draft";
        public Guid LocationId { get; set; }
        /// <summary>A transfer's destination.</summary>
        public Guid? ToLocationId { get; set; }
        public Guid? SupplierId { get; set; }
        /// <summary>The supplier's invoice or delivery note number (GRV).</summary>
        public string? SupplierReference { get; set; }
        /// <summary>adjust (quantities up or down) or count (a stock take: what was counted).</summary>
        public string? Mode { get; set; }
        public string? Reason { get; set; }
        public string? Notes { get; set; }
        public decimal Subtotal { get; set; }
        public decimal Vat { get; set; }
        public decimal Total { get; set; }
        /// <summary>The receipt or invoice a sale or return came from.</summary>
        public Guid? SourceId { get; set; }
        public Guid? BillId { get; set; }
        public Guid? JournalEntryId { get; set; }
        /// <summary>For opening stock: the account it is posted against (null: opening balance equity).</summary>
        public Guid? OffsetAccountId { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? PostedAt { get; set; }
        public DateTime? VoidedAt { get; set; }
        public List<StockDocumentLine> Lines { get; set; } = new();
    }

    public class StockDocumentLine
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid DocumentId { get; set; }
        public Guid ClientId { get; set; }
        public int Sort { get; set; }
        public Guid ProductId { get; set; }
        /// <summary>Received, sent, sold, or the change (+/-) of an adjustment. For a stock take, worked out when posted.</summary>
        public decimal Quantity { get; set; }
        /// <summary>A stock take's count.</summary>
        public decimal? Counted { get; set; }
        /// <summary>What the system held when a stock take was posted.</summary>
        public decimal? Expected { get; set; }
        /// <summary>Cost per unit before VAT (GRV, opening stock, adjustments in); the average cost for everything else, set when posted.</summary>
        public decimal UnitCost { get; set; }
        public decimal VatPercent { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>One item in or out of one location: the stock card. Quantity is signed; Value = Quantity x UnitCost, in the base currency.</summary>
    public class StockMovement
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid DocumentId { get; set; }
        public Guid ProductId { get; set; }
        public Guid LocationId { get; set; }
        public DateTime Date { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitCost { get; set; }
        public decimal Value { get; set; }
        /// <summary>What was on hand at the location after it, and the item's average cost after it.</summary>
        public decimal BalanceAfter { get; set; }
        public decimal AverageCostAfter { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
