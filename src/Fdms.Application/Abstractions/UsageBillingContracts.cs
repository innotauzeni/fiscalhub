namespace Fdms.Application.Abstractions
{
    // =================================================================================================================
    // Usage and add-ons: what a company pays on top of its plan. Every price is set by the platform owner and can change
    // at any time; a change applies from the next invoice.
    // =================================================================================================================

    /// <summary>
    /// Charge: switches the usage charges below on or off (the per-plan extra-receipt price is set on each plan). Only companies whose plan is billed in
    /// <see cref="Currency"/> are charged these. Monthly prices are multiplied by the months an invoice covers.
    /// </summary>
    /// <remarks>GoLiveFee: one-off, invoiced when a go-live request is approved (live devices switch on when it is paid). LiveDeviceMonthly: per live (Production) device, each period.</remarks>
    public sealed record UsagePricingDto(bool Charge, string Currency, decimal SmsPrice, decimal WhatsAppNumberMonthly, decimal ExtraDeviceMonthly, decimal ExtraUserMonthly, decimal ExtraBranchMonthly,
        decimal GoLiveFee = 0, decimal LiveDeviceMonthly = 0);

    /// <summary>A company's add-ons: what it has, what each costs a month, and an add-on invoice waiting to be paid.</summary>
    public sealed record AddonsDto(int Devices, int Users, int Branches, decimal DevicePrice, decimal UserPrice, decimal BranchPrice, string Currency, bool Available, string? UnavailableReason,
        Guid? PendingInvoiceId, string? PendingInvoiceNumber, decimal? PendingTotal, string? PendingSummary, DateTime? PeriodEnd);
    public sealed record ChangeAddonsRequest(int Devices, int Users, int Branches);

    /// <summary>Adds the usage charges and add-ons to a subscription invoice.</summary>
    public interface IUsageBilling
    {
        /// <summary>The charges for an invoice covering periodStart..periodEnd. Marks the usage as billed (saved with the invoice). Null when there is nothing to charge.</summary>
        Task<(decimal Amount, string Description)?> ChargeAsync(Guid clientId, int planId, DateTime periodStart, DateTime periodEnd, CancellationToken ct);
    }

    public interface IAddonService
    {
        Task<UsagePricingDto> PricingAsync(CancellationToken ct);
        Task<UsagePricingDto> SavePricingAsync(UsagePricingDto req, CancellationToken ct);

        Task<AddonsDto> MineAsync(Guid clientId, CancellationToken ct);
        /// <summary>Raises an invoice for the rest of the current period; the add-ons switch on when it is paid and are then billed with each renewal.</summary>
        Task<AddonsDto> BuyAsync(Guid clientId, ChangeAddonsRequest req, CancellationToken ct);
        /// <summary>Stops paying for some add-ons from now on (no refund). Refused while they are in use.</summary>
        Task<AddonsDto> RemoveAsync(Guid clientId, ChangeAddonsRequest req, CancellationToken ct);
        Task<AddonsDto> CancelPendingAsync(Guid clientId, CancellationToken ct);
    }
}
