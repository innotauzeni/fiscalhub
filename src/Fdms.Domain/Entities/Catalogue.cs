namespace Fdms.Domain.Entities
{
    /// <summary>A buyer a company invoices often: kept once, so the buyer's name, TIN and VAT number are never mistyped on an invoice.</summary>
    public class Customer
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        public string? TradeName { get; set; }
        /// <summary>ZIMRA TIN: 10 digits.</summary>
        public string? Tin { get; set; }
        /// <summary>VAT registration number: 9 digits.</summary>
        public string? VatNumber { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Address { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>An item a company sells, with its HS code and tax kept once. ZIMRA rejects a line with a missing or wrong-length HS code, so picking from the catalogue avoids that.</summary>
    public class Product
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string? Code { get; set; }
        public string Name { get; set; } = "";
        /// <summary>4, 6 or 8 digits (ZIMRA tariff codes are 8).</summary>
        public string? HsCode { get; set; }
        public string? Unit { get; set; }
        public decimal UnitPrice { get; set; }
        public string Currency { get; set; } = "USD";
        /// <summary>The VAT rate to charge; null with IsExempt = exempt, null without = use the device's standard rate.</summary>
        public decimal? TaxPercent { get; set; }
        public bool IsExempt { get; set; }
        public bool IsActive { get; set; } = true;
        /// <summary>Its category or subcategory (inventory).</summary>
        public Guid? CategoryId { get; set; }
        /// <summary>Counted in stock: received on GRVs, taken out by sales, valued at average cost.</summary>
        public bool TrackStock { get; set; }
        /// <summary>The weighted average cost of one unit, in the base currency (changes with every receipt).</summary>
        public decimal AverageCost { get; set; }
        /// <summary>What it last cost on a GRV.</summary>
        public decimal LastCost { get; set; }
        /// <summary>At or below this quantity (all locations together) it shows on the reorder report; 0 = never.</summary>
        public decimal ReorderLevel { get; set; }
        public decimal ReorderQuantity { get; set; }
        public string? Barcode { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
