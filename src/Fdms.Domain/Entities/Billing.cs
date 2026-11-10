namespace Fdms.Domain.Entities
{
    /// <summary>A subscription plan the platform operator sells. Limits of 0 mean "unlimited". Prices are set in the console, never in code.</summary>
    public class Plan
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public decimal PriceMonthly { get; set; }
        public decimal PriceYearly { get; set; }
        public string Currency { get; set; } = "USD";
        public int MaxDevices { get; set; }
        public int MaxUsers { get; set; }
        public int MaxBranches { get; set; }
        /// <summary>Receipts included each month (0 = unlimited).</summary>
        public int MaxReceiptsPerMonth { get; set; }
        /// <summary>Charged for each receipt above the monthly allowance, on the next invoice (0 = never charged).</summary>
        public decimal PricePerExtraReceipt { get; set; }
        /// <summary>Usage pricing (developer / API plans): each active live (Production) device per month, charged for the period ahead. Test devices are free. 0 = none.</summary>
        public decimal PricePerDeviceMonthly { get; set; }
        /// <summary>The same per live device on a yearly subscription (0 = twelve times the monthly price).</summary>
        public decimal PricePerDeviceYearly { get; set; }
        /// <summary>
        /// How receipts are charged on top of the price: flat (included; the allowance and price per extra receipt above still apply), metered (per 1,000 receipts,
        /// with an optional monthly minimum and cap) or tiered (a monthly price set by the volume band). Bands and limits use the average receipts per month since the last invoice.
        /// </summary>
        public string ReceiptPricing { get; set; } = "flat";
        /// <summary>Metered: every receipt issued, per 1,000 (pro rata).</summary>
        public decimal PricePerThousandReceipts { get; set; }
        /// <summary>Metered: at least this much a month for receipts (0 = no minimum).</summary>
        public decimal ReceiptMinimumMonthly { get; set; }
        /// <summary>Metered: at most this much a month for receipts (0 = no cap).</summary>
        public decimal ReceiptCapMonthly { get; set; }
        /// <summary>Tiered: the bands, JSON [{"upTo":1000,"price":10},{"upTo":null,"price":60}] in rising order; the last may be open (null).</summary>
        public string? ReceiptTiersJson { get; set; }
        /// <summary>One feature per line, shown on the pricing cards.</summary>
        public string Features { get; set; } = "";
        public int TrialDays { get; set; }
        /// <summary>fiscal (the full product) or connector (Zoho Books connector only: no fiscal devices, only the menus that connector needs).</summary>
        public string Kind { get; set; } = "fiscal";
        /// <summary>The optional modules the plan includes (comma-separated keys, see PlanModules). Null = all of them (plans from before modules existed); "" = core only.</summary>
        public string? Modules { get; set; }
        public bool IsActive { get; set; } = true;
        /// <summary>Shown on the public pricing page and the registration form.</summary>
        public bool IsPublic { get; set; } = true;
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A company's request to join. No client and no user exist until the operator approves it; the password is stored hashed.</summary>
    public class ClientApplication
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string CompanyName { get; set; } = "";
        public string? Tin { get; set; }
        public string ContactName { get; set; } = "";
        public string ContactSurname { get; set; } = "";
        public string Email { get; set; } = "";
        public string? Phone { get; set; }
        public string PasswordHash { get; set; } = "";
        public int? PlanId { get; set; }
        public string BillingCycle { get; set; } = "monthly";
        /// <summary>Unverified (waiting for the emailed code), Pending (waiting for the operator), Approved or Rejected.</summary>
        public string Status { get; set; } = "Pending";
        /// <summary>The user name the applicant chose to sign in with (null: they sign in with the email).</summary>
        public string? UserName { get; set; }
        /// <summary>The emailed 6-digit code, hashed with the application id; it expires and allows a few wrong tries.</summary>
        public string? VerifyCodeHash { get; set; }
        public DateTime? VerifyExpiresAt { get; set; }
        public DateTime? VerifySentAt { get; set; }
        public int VerifyAttempts { get; set; }
        public string? Message { get; set; }
        public string? DecisionReason { get; set; }
        public string? DecidedBy { get; set; }
        public DateTime? DecidedAt { get; set; }
        public Guid? ClientId { get; set; }
        /// <summary>The partner whose referral link or code the applicant used.</summary>
        public Guid? PartnerId { get; set; }
        /// <summary>The company whose referral code the applicant used.</summary>
        public Guid? ReferrerClientId { get; set; }
        public string? IpAddress { get; set; }
        /// <summary>The company logo sent with the registration. On approval it becomes the logo of the company's default invoice template.</summary>
        public byte[]? Logo { get; set; }
        public string? LogoContentType { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>The plan a company is on and where it is in its billing period. Companies without one (created by hand) are not limited.</summary>
    public class Subscription
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public int PlanId { get; set; }
        /// <summary>Trial, Active, PastDue, Suspended or Cancelled.</summary>
        public string Status { get; set; } = "Trial";
        public string BillingCycle { get; set; } = "monthly";
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime CurrentPeriodStart { get; set; } = DateTime.UtcNow;
        public DateTime CurrentPeriodEnd { get; set; } = DateTime.UtcNow.AddMonths(1);
        public DateTime? TrialEndsAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>What the platform bills a company (not to be confused with the fiscal receipts a company issues to its own customers).</summary>
    public class BillingInvoice
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Number { get; set; } = "";
        public Guid ClientId { get; set; }
        public Guid? SubscriptionId { get; set; }
        public int? PlanId { get; set; }
        public string Description { get; set; } = "";
        public decimal Subtotal { get; set; }
        public decimal TaxPercent { get; set; }
        public decimal TaxAmount { get; set; }
        public decimal Total { get; set; }
        public string Currency { get; set; } = "USD";
        /// <summary>Open, Paid or Void.</summary>
        public string Status { get; set; } = "Open";
        public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
        public DateTime DueAt { get; set; } = DateTime.UtcNow.AddDays(7);
        public DateTime? PaidAt { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public string? PaymentMethod { get; set; }
        public string? PaymentReference { get; set; }
        /// <summary>Plan to move to once this invoice is paid (a plan change); null = renewal of the current plan.</summary>
        public int? ChangeToPlanId { get; set; }
        public string? ChangeToCycle { get; set; }
        /// <summary>subscription (the plan: renewals, plan changes) or integration (a one-off charge for the Zoho integration, which never touches the subscription).</summary>
        public string Kind { get; set; } = "subscription";
        /// <summary>An add-on invoice: what it adds when paid, e.g. {"devices":2,"users":0,"branches":1}.</summary>
        public string? AddonsJson { get; set; }
    }

    /// <summary>One attempt to pay an invoice: Paynow (cards, EcoCash, OneMoney, ZimSwitch...), a bank transfer awaiting confirmation, or a payment recorded by hand.</summary>
    public class Payment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid InvoiceId { get; set; }
        public Guid ClientId { get; set; }
        /// <summary>Paynow, BankTransfer, Cash or Other.</summary>
        public string Method { get; set; } = "";
        /// <summary>Pending, AwaitingConfirmation, Paid, Failed, Cancelled or Rejected.</summary>
        public string Status { get; set; } = "Pending";
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "USD";
        public string? Reference { get; set; }
        public string? ProviderReference { get; set; }
        public string? PollUrl { get; set; }
        public string? RedirectUrl { get; set; }
        public string? Instructions { get; set; }
        public string? PayerPhone { get; set; }
        public string? ProofName { get; set; }
        public string? ProofContentType { get; set; }
        public byte[]? Proof { get; set; }
        public string? Notes { get; set; }
        public string? ConfirmedBy { get; set; }
        public string? ProviderStatus { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ConfirmedAt { get; set; }
    }

    /// <summary>
    /// A one-time code the operator gives a company after a cash or bank payment, which the company types in to settle the invoice and switch everything on.
    /// Only a salted hash is stored; the code itself is shown to the operator once and emailed to the company.
    /// </summary>
    public class ActivationCode
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid InvoiceId { get; set; }
        public Guid? PaymentId { get; set; }
        public string Salt { get; set; } = "";
        public string CodeHash { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public DateTime? UsedAt { get; set; }
        public string? UsedBy { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? IssuedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A company asking to go live: ZIMRA FDMS has a demo (test) and a live environment, and live devices are switched on by the operator.</summary>
    public class GoLiveRequest
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>
        /// Pending (sent with its six ZIMRA test documents), SentToZimra (the operator emailed them to ZIMRA and waits for the live device), AwaitingPayment (approved with a
        /// go-live fee: live devices switch on when its invoice is paid), Approved or Rejected.
        /// </summary>
        public string Status { get; set; } = "Pending";
        /// <summary>The one-off go-live fee charged on approval (0 = none), and its invoice.</summary>
        public decimal Fee { get; set; }
        public Guid? InvoiceId { get; set; }
        public string ContactName { get; set; } = "";
        public string ContactPhone { get; set; } = "";
        public string? ZimraTin { get; set; }
        public string? Notes { get; set; }
        public string? DecisionReason { get; set; }
        public string? DecidedBy { get; set; }
        public DateTime? DecidedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>The six test documents ZIMRA asks for (USD and ZWG invoice, credit note and debit note from a test device), as a JSON list of receipt ids.</summary>
        public string? TestReceiptIdsJson { get; set; }
        /// <summary>When and to whom the operator sent the test documents to ZIMRA.</summary>
        public DateTime? SentToZimraAt { get; set; }
        public string? SentToZimraBy { get; set; }
        public string? ZimraEmail { get; set; }
        /// <summary>The live (Production) device ZIMRA issued for the company; the activation key is kept encrypted.</summary>
        public int? LiveDeviceId { get; set; }
        public string? LiveActivationKeyProtected { get; set; }
        public string? LiveSerialNo { get; set; }
    }
}
