namespace Fdms.Domain.Entities
{
    /// <summary>A currency the platform knows (USD, ZWG...). The operator keeps the list; every currency choice in the console is made from it.</summary>
    public class Currency
    {
        /// <summary>ISO-style 3-letter code, upper case. The key.</summary>
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string Symbol { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
    }

    /// <summary>A bank a company deals with (NMB, CBZ, Stanbic...). Its accounts are listed under it.</summary>
    public class Bank
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        /// <summary>Short code or SWIFT/BIC (optional).</summary>
        public string? Code { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>An account at a bank, in one currency. Picking a bank offers its accounts, so payment details never have to be typed twice.</summary>
    public class BankAccount
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid BankId { get; set; }
        public string AccountName { get; set; } = "";
        public string AccountNumber { get; set; } = "";
        public string? BranchName { get; set; }
        public string? BranchCode { get; set; }
        public string CurrencyCode { get; set; } = "USD";
        /// <summary>The account offered first for its currency.</summary>
        public bool IsDefault { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
