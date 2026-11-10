namespace Fdms.Application.Abstractions
{
    // ---- referral program ------------------------------------------------------------------------------------------------

    /// <summary>A milestone: refer this many paying companies and earn the perk (and free months added to your own subscription).</summary>
    public sealed record ReferralTier(string Key, string Label, int Referrals, int FreeMonths, string? Perk, int BonusCoins = 0);

    /// <summary>
    /// Something a company can buy with coins. Kind: free_days (Value days added to the subscription), extra_device / extra_user / extra_branch (Value more on top of the plan),
    /// pay_invoice (pays an open subscription invoice; the price is the invoice total times the coin rate, Cost is ignored).
    /// </summary>
    public sealed record CoinShopItem(string Key, string Title, string? Description, int Cost, string Kind, int Value, bool Enabled = true);

    /// <summary>RewardType: coins (rewards are credited as coins to spend in the gateway) or money (rewards are paid out by the operator). CoinsPerUnit: coins per 1.00 of the invoice currency.</summary>
    public sealed record ReferralProgramDto(bool Enabled, decimal Percent, int InvoicesRewarded, string? Description, IReadOnlyList<ReferralTier> Tiers, string RewardType = "coins", int CoinsPerUnit = 10, IReadOnlyList<CoinShopItem>? Shop = null);
    public sealed record SaveReferralProgramRequest(bool Enabled, decimal Percent, int InvoicesRewarded, string? Description, IReadOnlyList<ReferralTier>? Tiers, string? RewardType = null, int? CoinsPerUnit = null, IReadOnlyList<CoinShopItem>? Shop = null);

    public sealed record CoinEntryDto(Guid Id, int Amount, string Kind, string Description, DateTime CreatedAt);
    public sealed record BonusLimitsDto(int Devices, int Users, int Branches);
    public sealed record CoinInvoiceDto(Guid Id, string Number, decimal Total, string Currency, int Coins);
    public sealed record RedeemCoinsRequest(string Key, Guid? InvoiceId);
    public sealed record RedeemResultDto(string Message, int Balance);
    public sealed record AdjustCoinsRequest(Guid ClientId, int Amount, string? Note);
    public sealed record CoinHolderDto(Guid ClientId, string Company, int Balance);

    public sealed record ReferredCompanyDto(string Name, string Status, bool Paying, DateTime Since);
    public sealed record RewardDto(Guid Id, string Company, decimal Amount, string Currency, decimal Percent, string Status, DateTime CreatedAt, DateTime? PaidAt, string? Reference, int Coins = 0);
    public sealed record TierProgressDto(string Key, string Label, int Referrals, int FreeMonths, string? Perk, bool Reached, DateTime? GrantedAt, int BonusCoins = 0);
    public sealed record ReferralOverviewDto(bool Enabled, string Code, string ReferralPath, decimal Percent, int InvoicesRewarded, string? Description, int Referred, int Pending, int Paying,
        IReadOnlyList<AmountDto> Earned, IReadOnlyList<AmountDto> Paid, IReadOnlyList<AmountDto> Owed, IReadOnlyList<TierProgressDto> Tiers, IReadOnlyList<ReferredCompanyDto> Companies, IReadOnlyList<RewardDto> Rewards,
        string RewardType = "coins", int CoinsPerUnit = 10, int Coins = 0, IReadOnlyList<CoinShopItem>? Shop = null, IReadOnlyList<CoinEntryDto>? CoinHistory = null, BonusLimitsDto? Bonus = null, IReadOnlyList<CoinInvoiceDto>? PayableInvoices = null, string? ShareLink = null);

    public sealed record AdminRewardDto(Guid Id, string Referrer, string Referred, decimal Amount, string Currency, string Status, DateTime CreatedAt, DateTime? PaidAt, string? Reference, int Coins = 0);
    public sealed record ReferralProgramAdminDto(ReferralProgramDto Program, int Referrers, int ReferredCompanies, IReadOnlyList<AmountDto> Earned, IReadOnlyList<AmountDto> Paid, IReadOnlyList<AmountDto> Owed, IReadOnlyList<AdminRewardDto> Rewards,
        int CoinsIssued = 0, int CoinsSpent = 0, IReadOnlyList<CoinHolderDto>? Holders = null);
    public sealed record PayRewardRequest(string? Reference);

    public interface IReferralService
    {
        Task<ReferralOverviewDto> OverviewAsync(Guid clientId, CancellationToken ct);
        Task<ReferralProgramAdminDto> AdminAsync(string? status, CancellationToken ct);
        Task<ReferralProgramAdminDto> SaveProgramAsync(SaveReferralProgramRequest req, CancellationToken ct);
        Task<AdminRewardDto> PayAsync(Guid rewardId, string? reference, CancellationToken ct);
        /// <summary>Spends coins on a shop item (or on paying an open invoice).</summary>
        Task<RedeemResultDto> RedeemAsync(Guid clientId, RedeemCoinsRequest req, CancellationToken ct);
        /// <summary>The operator gives or takes coins (a goodwill gesture, a correction).</summary>
        Task<RedeemResultDto> AdjustCoinsAsync(AdjustCoinsRequest req, CancellationToken ct);
        Task<int> BalanceAsync(Guid clientId, CancellationToken ct);
        /// <summary>The company a referral code belongs to (null for an unknown code or a switched-off program).</summary>
        Task<Guid?> ReferrerForCodeAsync(string? code, CancellationToken ct);
    }

    // ---- terms, privacy and developer details ------------------------------------------------------------------------------

    public sealed record LegalDocDto(string Title, string Body, DateTime UpdatedAt);
    public sealed record DeveloperDto(string Name, string? About, string? Email, string? Phone, string? WhatsApp, string? Website, string? Address);
    public sealed record LegalDto(LegalDocDto Terms, LegalDocDto Privacy, DeveloperDto Developer);
    public sealed record SaveLegalRequest(string? Terms, string? Privacy, DeveloperDto? Developer);

    /// <summary>Where to download the mobile app. A null store is not shown on the website.</summary>
    public sealed record AppLinksDto(string? ApkPure, string? PlayStore, string? AppStore, string? Version);

    public interface IAppLinksService
    {
        Task<AppLinksDto> GetAsync(CancellationToken ct);
        Task<AppLinksDto> SaveAsync(AppLinksDto req, CancellationToken ct);
    }

    public interface ILegalService
    {
        Task<LegalDto> GetAsync(CancellationToken ct);
        Task<LegalDto> SaveAsync(SaveLegalRequest req, CancellationToken ct);
    }
}
