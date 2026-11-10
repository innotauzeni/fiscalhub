namespace Fdms.Application.Abstractions
{
    public sealed record RevenueMonthDto(string Month, IReadOnlyList<AmountDto> Collected, int Invoices);
    public sealed record RevenuePlanDto(int PlanId, string Plan, decimal PriceMonthly, string Currency, int Paying, int Trial, decimal Mrr);
    public sealed record RevenueClientDto(Guid ClientId, string Company, string? Plan, string Status, IReadOnlyList<AmountDto> PaidAllTime, IReadOnlyList<AmountDto> Outstanding, DateTime? LastPaidAt);
    public sealed record RevenueMethodDto(string Method, IReadOnlyList<AmountDto> Collected, int Payments);
    public sealed record RevenueCountsDto(int Companies, int Active, int Paying, int Trial, int PastDue, int Suspended, int Cancelled, int NewThisMonth, int TrialsEndingIn7Days, int AwaitingConfirmation);

    /// <summary>
    /// The platform owner's money view. Mrr: recurring revenue per month from paying subscriptions (yearly plans counted as 1/12), per currency. Collected: money actually received
    /// (paid invoices, coins excluded). Outstanding / Overdue: open invoices. CoinsLiability: coins companies hold, valued at the coin rate (invoices they may pay with coins instead of money).
    /// </summary>
    public sealed record RevenueDto(
        IReadOnlyList<AmountDto> Mrr, IReadOnlyList<AmountDto> Arr, IReadOnlyList<AmountDto> ThisMonth, IReadOnlyList<AmountDto> LastMonth, IReadOnlyList<AmountDto> Last12Months, IReadOnlyList<AmountDto> AllTime,
        IReadOnlyList<AmountDto> Outstanding, IReadOnlyList<AmountDto> Overdue, IReadOnlyList<AmountDto> ArpuMonthly, RevenueCountsDto Counts, decimal TrialConversionPercent,
        IReadOnlyList<RevenueMonthDto> Months, IReadOnlyList<RevenuePlanDto> Plans, IReadOnlyList<RevenueMethodDto> Methods, IReadOnlyList<RevenueClientDto> TopClients,
        IReadOnlyList<RevenueClientDto> Owing, int CoinsOutstanding, IReadOnlyList<AmountDto> CoinsLiability, IReadOnlyList<AmountDto> ReferralMoneyOwed, IReadOnlyList<AmountDto> PartnerCommissionOwed, DateTime GeneratedAt);

    public interface IRevenueService
    {
        Task<RevenueDto> GetAsync(CancellationToken ct);
    }
}
