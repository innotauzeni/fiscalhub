namespace Fdms.Domain.Entities
{
    /// <summary>A plan for one year in one currency: how much income and expense is expected for each account in each month. Compared with what the books say actually happened.</summary>
    public class Budget
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        public int Year { get; set; }
        public string Currency { get; set; } = "USD";
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class BudgetLine
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid BudgetId { get; set; }
        public Guid ClientId { get; set; }
        public Guid AccountId { get; set; }
        /// <summary>1 to 12.</summary>
        public int Month { get; set; }
        public decimal Amount { get; set; }
    }
}
