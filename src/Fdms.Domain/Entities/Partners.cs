namespace Fdms.Domain.Entities
{
    /// <summary>
    /// An accounting firm or reseller that brings companies onto the gateway. Companies that register through its referral code, or that the operator attaches to it, earn it
    /// a commission on the subscription invoices they pay. Its own staff sign in with the Partner role and see only the companies it brought.
    /// </summary>
    public class Partner
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        /// <summary>The word in a referral link: /register?ref=CODE. Upper-case letters and digits.</summary>
        public string Code { get; set; } = "";
        public string? ContactName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        /// <summary>The share of each paid subscription invoice (before tax) the partner earns. Copied to a company when it joins, so changing it later does not rewrite history.</summary>
        public decimal CommissionPercent { get; set; }
        /// <summary>commission (earns a share of what its companies pay) or reseller (its companies pay a reseller price: the plan price less DiscountPercent; it earns no commission from the platform).</summary>
        public string Kind { get; set; } = "commission";
        /// <summary>Reseller: how much less than the list price its companies pay.</summary>
        public decimal DiscountPercent { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Money the operator has paid out to a partner against the commission it has earned.</summary>
    public class PartnerPayout
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PartnerId { get; set; }
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string? Reference { get; set; }
        public string? Notes { get; set; }
        public string? RecordedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
