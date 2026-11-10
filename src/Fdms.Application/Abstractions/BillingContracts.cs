namespace Fdms.Application.Abstractions
{
    // ---- plans -------------------------------------------------------------------------------------------------------

    public sealed record PlanDto(int Id, string Code, string Name, string? Description, decimal PriceMonthly, decimal PriceYearly, string Currency, int MaxDevices, int MaxUsers, int MaxBranches,
        int MaxReceiptsPerMonth, IReadOnlyList<string> Features, int TrialDays, bool IsActive, bool IsPublic, int SortOrder, int Subscribers, string Kind = "fiscal", decimal PricePerExtraReceipt = 0, IReadOnlyList<string>? Modules = null, decimal PricePerDeviceMonthly = 0, decimal PricePerThousandReceipts = 0,
        decimal PricePerDeviceYearly = 0, string ReceiptPricing = "flat", decimal ReceiptMinimumMonthly = 0, decimal ReceiptCapMonthly = 0, IReadOnlyList<ReceiptTier>? ReceiptTiers = null);

    /// <summary>A volume band: up to UpTo receipts a month (null = any number) costs Price a month.</summary>
    public sealed record ReceiptTier(int? UpTo, decimal Price);

    public sealed record SavePlanRequest(int Id, string Code, string Name, string? Description, decimal PriceMonthly, decimal PriceYearly, string Currency, int MaxDevices, int MaxUsers,
        int MaxBranches, int MaxReceiptsPerMonth, IReadOnlyList<string>? Features, int TrialDays, bool IsActive, bool IsPublic, int SortOrder, string? Kind = null, decimal? PricePerExtraReceipt = null, IReadOnlyList<string>? Modules = null, decimal? PricePerDeviceMonthly = null, decimal? PricePerThousandReceipts = null,
        decimal? PricePerDeviceYearly = null, string? ReceiptPricing = null, decimal? ReceiptMinimumMonthly = null, decimal? ReceiptCapMonthly = null, IReadOnlyList<ReceiptTier>? ReceiptTiers = null);

    public interface IPlanService
    {
        Task<IReadOnlyList<PlanDto>> ListAsync(bool publicOnly, CancellationToken ct);
        Task<PlanDto> SaveAsync(SavePlanRequest req, CancellationToken ct);
        Task DeleteAsync(int id, CancellationToken ct);
    }

    // ---- onboarding --------------------------------------------------------------------------------------------------

    /// <summary>Website is a hidden field real people leave empty; a filled one means a bot and the request is quietly dropped.</summary>
    public sealed record RegisterRequest(string CompanyName, string? Tin, string ContactName, string ContactSurname, string Email, string? Phone, string Password, int? PlanId, string? BillingCycle,
        string? Message, string? Website, string? LogoData = null, string? PartnerCode = null, string? UserName = null);

    /// <summary>
    /// What happens next after registering. Status "Unverified": a 6-digit code was emailed (enter it to open the account at once). "Pending": the operator reviews it first.
    /// The same answer whether or not the address already has an account.
    /// </summary>
    public sealed record RegisterResultDto(string Status, bool VerificationRequired, string Email, string Message);
    public sealed record VerifyRegistrationRequest(string Email, string Code);
    public sealed record ResendCodeRequest(string Email);
    /// <summary>Status "Active": the company exists, the trial runs until TrialEndsAt and its first invoice is ready (pay any time). "Pending": waiting for the operator.</summary>
    public sealed record VerifyRegistrationResultDto(string Status, string Message, string? SignInName, DateTime? TrialEndsAt, string? InvoiceNumber);

    public sealed record ApplicationDto(Guid Id, string CompanyName, string? Tin, string ContactName, string ContactSurname, string Email, string? Phone, int? PlanId, string? PlanName, string BillingCycle,
        string Status, string? Message, string? DecisionReason, string? DecidedBy, DateTime? DecidedAt, Guid? ClientId, DateTime CreatedAt, string? IpAddress, bool HasLogo = false);

    public sealed record ApproveApplicationRequest(int? PlanId, string? BillingCycle, int? TrialDays, string? Note);
    public sealed record RejectApplicationRequest(string Reason);

    public interface IOnboardingService
    {
        /// <summary>Stores the application (nothing else exists yet) and tells the operator. Always answers the same, so addresses cannot be probed.</summary>
        Task<RegisterResultDto> RegisterAsync(RegisterRequest req, string? ip, CancellationToken ct);
        /// <summary>Checks the emailed code. With self-service sign-up the company, its administrator, the trial and the first invoice are created at once.</summary>
        Task<VerifyRegistrationResultDto> VerifyAsync(VerifyRegistrationRequest req, string? ip, CancellationToken ct);
        /// <summary>Emails a new code (at most one a minute). Always answers the same.</summary>
        Task<RegisterResultDto> ResendAsync(ResendCodeRequest req, CancellationToken ct);
        Task<IReadOnlyList<ApplicationDto>> ListAsync(string? status, CancellationToken ct);
        Task<ApplicationDto> ApproveAsync(Guid id, ApproveApplicationRequest req, CancellationToken ct);
        Task<ApplicationDto> RejectAsync(Guid id, string reason, CancellationToken ct);
        /// <summary>The logo that came with an application, for the operator to see while reviewing.</summary>
        Task<TemplateLogo?> GetLogoAsync(Guid id, CancellationToken ct);
        /// <summary>For a person who tries to sign in before approval: the application's state, when the password matches. Null when none.</summary>
        Task<ApplicationDto?> FindForSignInAsync(string identifier, string password, CancellationToken ct);
    }

    // ---- subscriptions, invoices, payments ---------------------------------------------------------------------------

    public sealed record SubscriptionDto(Guid Id, Guid ClientId, string? ClientName, int PlanId, string PlanName, string Status, string BillingCycle, DateTime StartedAt,
        DateTime CurrentPeriodStart, DateTime CurrentPeriodEnd, DateTime? TrialEndsAt, decimal Outstanding);

    public sealed record UsageDto(int Devices, int Users, int Branches, int ReceiptsThisMonth);

    public sealed record BankDetailsDto(string? BankName, string? AccountName, string? AccountNumber, string? BranchCode, string? Swift, string? Instructions);

    /// <summary>What a company may use to pay. Paynow covers cards (Visa/Mastercard), EcoCash, OneMoney, ZimSwitch and more; bank transfer is confirmed by the operator.</summary>
    public sealed record PaymentOptionsDto(bool Paynow, bool PaynowMobile, bool BankTransfer, BankDetailsDto? Bank, string Currency, IReadOnlyList<PayMethodDto> Methods);

    public sealed record PaymentDto(Guid Id, Guid InvoiceId, string InvoiceNumber, Guid ClientId, string? ClientName, string Method, string Status, decimal Amount, string Currency, string? Reference,
        string? RedirectUrl, string? Instructions, bool HasProof, string? Notes, string? ConfirmedBy, DateTime CreatedAt, DateTime? ConfirmedAt);

    public sealed record BillingInvoiceDto(Guid Id, string Number, Guid ClientId, string? ClientName, string Description, decimal Subtotal, decimal TaxPercent, decimal TaxAmount, decimal Total,
        string Currency, string Status, DateTime IssuedAt, DateTime DueAt, DateTime? PaidAt, DateTime PeriodStart, DateTime PeriodEnd, string? PaymentMethod, string? PaymentReference,
        IReadOnlyList<PaymentDto> Payments);

    public sealed record BillingOverviewDto(SubscriptionDto? Subscription, PlanDto? Plan, UsageDto Usage, IReadOnlyList<BillingInvoiceDto> Invoices, IReadOnlyList<PlanDto> Plans, PaymentOptionsDto Options, AccessStateDto? Access = null,
        string? ResellerName = null, decimal ResellerDiscountPercent = 0);

    public sealed record ChangePlanRequest(int PlanId, string? BillingCycle);
    /// <summary>Method: web (Paynow checkout page: cards, ZimSwitch, wallets), ecocash or onemoney (a prompt on the phone).</summary>
    public sealed record PayPaynowRequest(string Method, string? Phone);
    public sealed record PayEcoCashRequest(string Phone);
    public sealed record SubscriptionAdminRequest(int? PlanId, string? Status, DateTime? PeriodEnd, string? BillingCycle);
    /// <summary>SendCode: do not activate yet; issue a one-time activation code the company types in (cash and bank payments).</summary>
    public sealed record ConfirmPaymentRequest(string? Reference, bool SendCode = false);
    public sealed record IssuedCodeDto(string Code, DateTime ExpiresAt, bool Emailed);
    public sealed record ConfirmResultDto(PaymentDto Payment, IssuedCodeDto? Code);
    public sealed record MarkPaidResultDto(BillingInvoiceDto Invoice, IssuedCodeDto? Code);
    public sealed record RedeemCodeRequest(string Code);
    public sealed record RejectPaymentRequest(string Reason);
    public sealed record MarkPaidRequest(string Method, string? Reference, string? Notes, bool SendCode = false);

    /// <summary>Locked: a new account that has not yet paid its first invoice can only use Billing. DemoOnly: a trial may use test (demo) devices only; live devices need a paid plan.</summary>
    /// <summary>Reason: NEW_ACCOUNT (first invoice unpaid), TRIAL_EXPIRED or EXPIRED (subscription ran out). LiveApproved: may register live (Production) devices.</summary>
    public sealed record AccessStateDto(bool Locked, bool DemoOnly, string? Reason = null, DateTime? ExpiredAt = null, bool LiveApproved = true);

    public interface IBillingService
    {
        Task<AccessStateDto> GetAccessAsync(Guid clientId, CancellationToken ct);
        void ForgetAccess(Guid clientId);
        /// <summary>Issues the next invoice for the current plan when none is open (an expired subscription starts a fresh period from today).</summary>
        Task<BillingInvoiceDto> RenewAsync(Guid clientId, CancellationToken ct);
        /// <summary>Throws TRIAL_DEMO_ONLY when a company on a trial tries to add a live (production) device.</summary>
        Task EnsureLiveAllowedAsync(Guid clientId, CancellationToken ct);
        Task<BillingOverviewDto> OverviewAsync(Guid clientId, CancellationToken ct);
        /// <summary>Puts a company on a plan: a trial if the plan has one, otherwise active with its first invoice.</summary>
        Task<SubscriptionDto> StartSubscriptionAsync(Guid clientId, int planId, string? cycle, int? trialDays, CancellationToken ct);
        /// <summary>Marks an invoice paid and applies it (extends the period, switches plan, reactivates a suspended company).</summary>
        Task ApplyInvoicePaidAsync(Guid invoiceId, string method, string? reference, CancellationToken ct);
        /// <summary>A one-off invoice that is not part of the subscription (kind "integration": paying it switches the Zoho integration on).</summary>
        Task<BillingInvoiceDto> IssueOneOffAsync(Guid clientId, string kind, string description, decimal subtotal, string currency, CancellationToken ct);
        /// <summary>The first invoice of a new trial company, raised at once so it can pay whenever it is ready: it covers the first period after the trial and is due when the trial ends. Null for a free plan.</summary>
        Task<string?> IssueOpeningInvoiceAsync(Guid clientId, CancellationToken ct);
        Task<BillingInvoiceDto> ChangePlanAsync(Guid clientId, ChangePlanRequest req, CancellationToken ct);
        Task<IReadOnlyList<SubscriptionDto>> ListSubscriptionsAsync(CancellationToken ct);
        Task<SubscriptionDto> AdminUpdateAsync(Guid clientId, SubscriptionAdminRequest req, CancellationToken ct);
        Task<BillingInvoiceDto> GetInvoiceAsync(Guid? clientId, Guid invoiceId, CancellationToken ct);
        Task<IReadOnlyList<BillingInvoiceDto>> ListInvoicesAsync(string? status, CancellationToken ct);
        Task<BillingInvoiceDto> CreateInvoiceAsync(Guid clientId, CancellationToken ct);
        Task<byte[]> InvoicePdfAsync(Guid? clientId, Guid invoiceId, CancellationToken ct);
        /// <summary>Creates renewal invoices, marks overdue subscriptions and (when enabled) suspends companies that did not pay. Returns what changed.</summary>
        Task<int> RunMaintenanceAsync(CancellationToken ct);
        /// <summary>Throws PLAN_LIMIT when adding one more of <paramref name="resource"/> (devices, users, branches) would exceed the company's plan.</summary>
        Task EnsureWithinPlanAsync(Guid clientId, string resource, CancellationToken ct);
    }

    public interface IPaymentService
    {
        Task<PaymentDto> PayWithPaynowAsync(Guid clientId, Guid invoiceId, PayPaynowRequest req, CancellationToken ct);
        Task<PaymentDto> PayWithEcoCashAsync(Guid clientId, Guid invoiceId, PayEcoCashRequest req, CancellationToken ct);
        Task<PaymentDto> PayWithStripeAsync(Guid clientId, Guid invoiceId, CancellationToken ct);
        /// <summary>Asks the gateways about payments still pending (in case the customer never came back and the webhook was lost). Returns how many became paid.</summary>
        Task<int> RefreshPendingAsync(CancellationToken ct);
        /// <summary>Pay outside any gateway, then tell us: provider is bank, ecocash-manual or paypal. The operator confirms it.</summary>
        Task<PaymentDto> PayManualAsync(Guid clientId, Guid invoiceId, string provider, string? reference, string? notes, byte[]? proof, string? proofName, string? proofContentType, CancellationToken ct);
        /// <summary>Stripe calling back (verified by its signature). Returns false when the message is not genuine.</summary>
        Task<bool> HandleStripeWebhookAsync(string payload, string signature, CancellationToken ct);
        /// <summary>EcoCash saying a charge changed. The body is never trusted: the payment is re-checked with EcoCash itself.</summary>
        Task<bool> HandleEcoCashNotifyAsync(string? reference, CancellationToken ct);
        /// <summary>The company checking on a payment: asks Paynow when it is still pending.</summary>
        Task<PaymentDto> RefreshAsync(Guid? clientId, Guid paymentId, CancellationToken ct);
        /// <summary>Paynow calling back with the outcome (verified by its hash). Returns false when the message is not genuine.</summary>
        Task<bool> HandlePaynowResultAsync(IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken ct);
        Task<IReadOnlyList<PaymentDto>> ListAsync(string? status, CancellationToken ct);
        Task<ConfirmResultDto> ConfirmAsync(Guid paymentId, string? reference, bool sendCode, CancellationToken ct);
        Task<PaymentDto> RejectAsync(Guid paymentId, string reason, CancellationToken ct);
        Task<MarkPaidResultDto> MarkInvoicePaidAsync(Guid invoiceId, MarkPaidRequest req, CancellationToken ct);
        Task<(byte[] Bytes, string ContentType, string Name)?> GetProofAsync(Guid? clientId, Guid paymentId, CancellationToken ct);
    }

    // ---- platform billing profile (the operator's own details on invoices) ------------------------------------------

    /// <summary>The operator's identity on the invoices it sends and the bank details shown to people who pay by transfer.</summary>
    public sealed class BillingProfile
    {
        public string CompanyName { get; set; } = "";
        public string? Address { get; set; }
        public string? TaxNumber { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Website { get; set; }
        public string Accent { get; set; } = "#2563eb";
        public string InvoicePrefix { get; set; } = "INV";
        public decimal TaxPercent { get; set; }
        public string? BankName { get; set; }
        public string? AccountName { get; set; }
        public string? AccountNumber { get; set; }
        public string? BranchCode { get; set; }
        public string? Swift { get; set; }
        public string? PaymentInstructions { get; set; }
        public string? Terms { get; set; }
        public string? Footer { get; set; }
    }

    public sealed record BillingProfileDto(BillingProfile Profile, bool HasLogo, bool PaynowConfigured, string PaynowMode);

    public interface IBillingProfileService
    {
        Task<BillingProfileDto> GetAsync(CancellationToken ct);
        Task<BillingProfileDto> SaveAsync(BillingProfile profile, CancellationToken ct);
        Task SetLogoAsync(byte[] bytes, CancellationToken ct);
        Task RemoveLogoAsync(CancellationToken ct);
        Task<TemplateLogo?> GetLogoAsync(CancellationToken ct);
        /// <summary>A sample invoice with the profile being edited (page 1 as PNG), like the invoice-template preview.</summary>
        Task<byte[]> PreviewAsync(BillingProfile profile, CancellationToken ct);
        /// <summary>Public pieces only (name, support email, bank details when transfers are on).</summary>
        Task<BillingProfile> LoadAsync(CancellationToken ct);
    }

    // ---- email -------------------------------------------------------------------------------------------------------

    public interface IEmailSender
    {
        /// <summary>Sends a message. Does nothing (and says so in the log) when email is not configured; never throws into the caller.</summary>
        Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct);
        /// <summary>False when no mail server is set up, so a screen can say so instead of pretending a message was sent.</summary>
        bool Configured => true;
        /// <summary>Like SendAsync with files attached (an implementation that cannot attach sends the message without them).</summary>
        Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct) => SendAsync(to, subject, htmlBody, ct);
        /// <summary>Sends and tells the truth: null when the mail server accepted the message, otherwise why it did not. For screens where a person is waiting to know (SendAsync stays silent for background alerts).</summary>
        /// <summary>Like TrySendAsync but also says whether a refusal is temporary ("try later", unreachable: worth retrying) or final (bad login, no such mailbox: not worth retrying).</summary>
        async Task<EmailResult> DeliverAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct) { var p = await TrySendAsync(to, subject, htmlBody, attachments, ct); return new EmailResult(p == null, false, p); }
        async Task<string?> TrySendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct) { await SendAsync(to, subject, htmlBody, attachments, ct); return null; }
    }

    public sealed record EmailResult(bool Ok, bool Temporary, string? Error);

    public sealed record EmailAttachment(string FileName, byte[] Bytes, string ContentType);

    // ---- activation codes and going live ---------------------------------------------------------------------------------

    public interface IActivationService
    {
        /// <summary>Makes a one-time code for an unpaid invoice (older codes for it stop working), emails it to the company and returns it once.</summary>
        Task<IssuedCodeDto> IssueAsync(Guid invoiceId, Guid? paymentId, CancellationToken ct);
        Task RevokeForPaymentAsync(Guid paymentId, CancellationToken ct);
        /// <summary>The company typing a code in: settles the invoice and switches the account on. Wrong guesses are counted and lock the attempt after five.</summary>
        Task RedeemAsync(Guid clientId, string? code, string? ip, CancellationToken ct);
    }

    /// <summary>TestReceiptIds: the six ZIMRA test documents; left out, the latest qualifying ones are taken.</summary>
    public sealed record SubmitGoLiveRequest(string ContactName, string ContactPhone, string? ZimraTin, string? Notes, IReadOnlyList<Guid>? TestReceiptIds = null);
    /// <summary>
    /// One of the six documents ZIMRA wants before a taxpayer goes live (Slot: "USD invoice" … "ZWG debit note"), fiscalised on a test device and accepted.
    /// ReceiptId null: not issued yet (Problem says what is missing).
    /// </summary>
    public sealed record GoLiveTestDocDto(string Slot, string Currency, string Type, Guid? ReceiptId, string? InvoiceNo, DateTime? Date, decimal? Total, long? ReceiptGlobalNo, int? FiscalDayNo,
        int? ZimraDeviceId, string? Original, string? Problem);
    public sealed record GoLiveDeviceDto(int DeviceId, string? ActivationKey, string? SerialNo);
    public sealed record GoLiveRequestDto(Guid Id, Guid ClientId, string? ClientName, string Status, string ContactName, string ContactPhone, string? ZimraTin, string? Notes, string? DecisionReason, string? DecidedBy, DateTime? DecidedAt, DateTime CreatedAt, decimal Fee = 0, Guid? InvoiceId = null, string? InvoiceNumber = null,
        IReadOnlyList<GoLiveTestDocDto>? TestDocuments = null, DateTime? SentToZimraAt = null, string? SentToZimraBy = null, string? ZimraEmail = null, GoLiveDeviceDto? LiveDevice = null);
    /// <summary>TestDocuments: what the company has issued so far for each of the six slots; TestSetComplete: all six are there.</summary>
    public sealed record GoLiveStatusDto(bool Approved, DateTime? ApprovedAt, GoLiveRequestDto? Latest, bool CanRequest, string? Blocked,
        IReadOnlyList<GoLiveTestDocDto>? TestDocuments = null, bool TestSetComplete = false);
    /// <summary>A company allowed to use live devices. ViaZimra: it went through the ZIMRA go-live process (test documents and a live device from ZIMRA); false: approved before that existed.</summary>
    public sealed record LiveCompanyDto(Guid ClientId, string Name, DateTime? ApprovedAt, bool ViaZimra, int? ZimraLiveDeviceId, int LiveDevices, int TestDevices);
    public sealed record ZimraEmailDto(string To, string? Cc, string Subject, string Body, IReadOnlyList<string> Attachments);
    public sealed record SendToZimraRequest(string To, string? Cc, string Subject, string Body);
    /// <summary>Fee: the one-off go-live fee for this approval; null = the price set under Plans → Usage and add-on prices (0 = none).</summary>
/// <summary>LiveDeviceId/ActivationKey/SerialNo: the live device ZIMRA issued, passed on to the company to register.</summary>
    public sealed record GoLiveDecisionRequest(string? Note, decimal? Fee = null, int? LiveDeviceId = null, string? ActivationKey = null, string? SerialNo = null);

    public interface IGoLiveService
    {
        Task<GoLiveStatusDto> StatusAsync(Guid clientId, CancellationToken ct);
        Task<GoLiveRequestDto> SubmitAsync(Guid clientId, SubmitGoLiveRequest req, CancellationToken ct);
        Task<IReadOnlyList<GoLiveRequestDto>> ListAsync(string? status, CancellationToken ct);
        Task<GoLiveRequestDto> ApproveAsync(Guid id, string? note, CancellationToken ct, decimal? fee = null, int? liveDeviceId = null, string? activationKey = null, string? serialNo = null);
        /// <summary>The email to ZIMRA, written out with the company's details and its six test documents (the operator can change it before sending).</summary>
        Task<ZimraEmailDto> ZimraEmailAsync(Guid id, CancellationToken ct);
        /// <summary>Emails ZIMRA the request with the six fiscal PDFs attached; the request becomes SentToZimra.</summary>
        Task<GoLiveRequestDto> SendToZimraAsync(Guid id, SendToZimraRequest req, CancellationToken ct);
        /// <summary>The six PDFs and a summary in one zip, to send by other means.</summary>
        Task<(byte[] Zip, string FileName)> PackAsync(Guid id, CancellationToken ct);
        Task<(byte[] Pdf, string FileName)> DocumentPdfAsync(Guid id, Guid receiptId, CancellationToken ct);
        /// <summary>Every company that may use live devices, and whether it got there through ZIMRA.</summary>
        Task<IReadOnlyList<LiveCompanyDto>> LiveCompaniesAsync(CancellationToken ct);
        Task<GoLiveRequestDto> RejectAsync(Guid id, string reason, CancellationToken ct);
        Task RevokeAsync(Guid clientId, CancellationToken ct);
    }
}

namespace Fdms.Application.Abstractions
{
    /// <summary>A demo (trial) company that never paid: when it will be deleted if it still does not pay, and whether the operator chose to keep it.</summary>
    public sealed record DemoAccountDto(Guid ClientId, string Company, string? Email, string? Plan, string Status, DateTime DemoEndsAt, DateTime DeleteOn, bool Keep, int Devices, int Receipts, bool WillBeDeleted);
    public sealed record KeepDemoRequest(bool Keep);

    public interface IDemoCleanupService
    {
        Task<IReadOnlyList<DemoAccountDto>> ListAsync(CancellationToken ct);
        Task SetKeepAsync(Guid clientId, bool keep, CancellationToken ct);
        /// <summary>Warns, then deletes, demo accounts that ended unpaid. Returns how many were deleted.</summary>
        Task<int> RunAsync(DateTime now, CancellationToken ct);
    }
}
