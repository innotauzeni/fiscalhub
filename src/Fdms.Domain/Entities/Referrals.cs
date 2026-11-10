namespace Fdms.Domain.Entities
{
    /// <summary>
    /// Money a company earns for bringing another company: a share of what the referred company pays for its subscription. Earned automatically when the invoice is paid,
    /// then paid out by the operator. One reward per referred company and invoice, so it can never be earned twice.
    /// </summary>
    public class ReferralReward
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ReferrerClientId { get; set; }
        public Guid ReferredClientId { get; set; }
        public Guid InvoiceId { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public decimal Percent { get; set; }
        /// <summary>Coins credited instead of money (the program pays rewards in coins). 0 for a money reward.</summary>
        public int Coins { get; set; }
        /// <summary>Earned (money waiting to be paid out), Paid, or Credited (paid as coins).</summary>
        public string Status { get; set; } = "Earned";
        public string? Reference { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? PaidAt { get; set; }
        public string? PaidBy { get; set; }
    }

    /// <summary>
    /// One movement of a company's coins: earned from a referral or a level, spent in the coins shop, or adjusted by the operator. The balance is the sum.
    /// Coins only buy things inside the gateway (time, slots, paying a subscription invoice); they are never paid out as money.
    /// </summary>
    public class CoinTransaction
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>Positive = earned, negative = spent.</summary>
        public int Amount { get; set; }
        /// <summary>referral, level, redeem, adjust.</summary>
        public string Kind { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>What it is about (the reward, the level, the shop item); unique per company and kind, so nothing is credited twice.</summary>
        public string? Reference { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
    }

    /// <summary>A milestone perk a company unlocked by referring enough paying companies (for example a free month). Granted once per tier.</summary>
    public class ReferralPerk
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string TierKey { get; set; } = "";
        public string Label { get; set; } = "";
        public int FreeMonths { get; set; }
        public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    }
}
