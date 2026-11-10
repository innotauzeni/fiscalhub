using Fdms.Domain.Enums;

namespace Fdms.Domain.Entities
{
    /// <summary>A tenant: a company or external system that integrates with this gateway.</summary>
    public class Client
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Where receipt/fiscal-day events are POSTed (https). Null = webhooks off.</summary>
        public string? WebhookUrl { get; set; }
        /// <summary>HMAC secret protected with Data Protection; the plain value is shown once when the webhook is configured.</summary>
        public string? WebhookSecretProtected { get; set; }
        /// <summary>May register live (Production) devices. Demo (Test) devices need no approval. Set by the operator after a go-live request.</summary>
        public bool LiveApproved { get; set; }
        public DateTime? LiveApprovedAt { get; set; }
        /// <summary>The company's own sign-in address word: /login?company=acme shows its logo and colour. Lower-case letters, digits and dashes.</summary>
        public string? Slug { get; set; }
        /// <summary>Where alerts are emailed (comma separated). Empty = the company administrators' own addresses.</summary>
        public string? AlertEmails { get; set; }
        /// <summary>Comma-separated alert kinds switched ON. Null = all of them.</summary>
        public string? AlertKinds { get; set; }
        /// <summary>Everyone in the company must use the authenticator app or a passkey. Until they do, their session reaches only their own security settings.</summary>
        public bool RequireMfa { get; set; }
        /// <summary>The partner (accounting firm or reseller) that brought this company, and the share of its paid subscription invoices the partner earns. Null = direct.</summary>
        public Guid? PartnerId { get; set; }
        public decimal PartnerPercent { get; set; }
        /// <summary>The company's own referral code (refer-and-earn). Companies that register with it are linked to this one.</summary>
        public string? ReferralCode { get; set; }
        /// <summary>The company that referred this one (null = direct or through a partner).</summary>
        public Guid? ReferredByClientId { get; set; }
        /// <summary>Extra devices, users and branches on top of the plan, bought with coins (or given by the operator).</summary>
        public int BonusDevices { get; set; }
        public int BonusUsers { get; set; }
        public int BonusBranches { get; set; }
        /// <summary>Extra devices, users and branches the company pays for each period (add-ons), on top of the plan.</summary>
        public int PaidDevices { get; set; }
        public int PaidUsers { get; set; }
        public int PaidBranches { get; set; }
        /// <summary>Usage (extra receipts, SMS) up to this moment is already on an invoice; the next invoice charges what came after.</summary>
        public DateTime? UsageBilledUntil { get; set; }
        /// <summary>Never delete this company automatically, even when its demo ended unpaid (set by the operator).</summary>
        public bool KeepWhenUnpaid { get; set; }
    }

    /// <summary>API token. Only a SHA-256 hash is stored; the plain token is shown once at creation.</summary>
    public class ApiKey
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        public string KeyPrefix { get; set; } = "";
        public string KeyHash { get; set; } = "";
        /// <summary>The ONE device this token may operate.</summary>
        public Guid DeviceId { get; set; }
        /// <summary>Comma-separated permission names (subset of the integration permissions).</summary>
        public string Scopes { get; set; } = "";
        public string? CreatedByUserId { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>How a company's printed invoices look. BranchId null = the company default; one row per branch overrides it for that branch's devices.</summary>
    public class InvoiceTemplate
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid? BranchId { get; set; }
        public string Name { get; set; } = "";
        /// <summary>InvoiceTemplateSettings as JSON.</summary>
        public string SettingsJson { get; set; } = "{}";
        public byte[]? Logo { get; set; }
        public string? LogoContentType { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A picture the company uploaded for the invoice builder (a background, a signature, a stamp, a decoration). Shared by its templates.</summary>
    public class TemplateAsset
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string? Name { get; set; }
        public string ContentType { get; set; } = "image/png";
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class Device
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public int ZimraDeviceId { get; set; }
        /// <summary>The company's own name for this machine, e.g. "Till 2, front counter". A branch can have many machines; this is how people tell them apart.</summary>
        public string? Name { get; set; }
        /// <summary>The company branch this till belongs to (our own grouping, not ZIMRA's BranchName).</summary>
        public Guid? BranchId { get; set; }
        public int Environment { get; set; }                // FdmsEnvironment
        public string SerialNo { get; set; } = "";
        public string ModelName { get; set; } = "";
        public string ModelVersion { get; set; } = "";
        public string CertificatePem { get; set; } = "";
        /// <summary>PKCS#8 PEM protected with ASP.NET Data Protection; never returned by the API.</summary>
        public string PrivateKeyProtected { get; set; } = "";
        public DateTime? CertificateValidTill { get; set; }

        public DeviceOperatingMode OperatingMode { get; set; }
        public string? QrUrl { get; set; }
        public string? TaxPayerName { get; set; }
        public string? TaxPayerTin { get; set; }
        public string? VatNumber { get; set; }
        public string? BranchName { get; set; }
        /// <summary>The branch's address, phone and email as ZIMRA holds them (from getConfig): used to fill the placeholders in invoice wording.</summary>
        public string? BranchAddress { get; set; }
        public string? BranchPhone { get; set; }
        public string? BranchEmail { get; set; }
        public int DayMaxHrs { get; set; } = 24;
        /// <summary>applicableTaxes from getConfig as JSON.</summary>
        public string? TaxesJson { get; set; }
        public DateTime? ConfigRefreshedAt { get; set; }

        /// <summary>Last receiptGlobalNo issued by this device (gapless, spec RCPT012).</summary>
        public long LastReceiptGlobalNo { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class FiscalDay
    {
        public int Id { get; set; }
        public Guid DeviceId { get; set; }
        public int FiscalDayNo { get; set; }
        public FiscalDayStatus Status { get; set; }
        public DateTime OpenedAt { get; set; }               // CAT
        public DateTime? ClosedAt { get; set; }
        public int LastReceiptCounter { get; set; }
        public string? LastReceiptHash { get; set; }
        public DateTime? LastReceiptDate { get; set; }
        public string? ClosingErrorCode { get; set; }
        /// <summary>Day was run in FDMS Offline mode: receipts are issued locally and uploaded as files.</summary>
        public bool IsOffline { get; set; }
        public List<FiscalDayCounterRow> Counters { get; set; } = new();
    }

    public class FiscalDayCounterRow
    {
        public int Id { get; set; }
        public int FiscalDayId { get; set; }
        public FiscalCounterType Type { get; set; }
        public string Currency { get; set; } = "";
        public int? TaxId { get; set; }
        public decimal? TaxPercent { get; set; }
        public MoneyType? MoneyType { get; set; }
        public decimal Value { get; set; }
    }

    /// <summary>
    /// Pending = numbered, waiting for FDMS; Submitted = accepted; Rejected = refused after numbering;
    /// Held = in the grace period, NOT numbered yet (can be cancelled with no trace at ZIMRA); Cancelled = voided while held;
    /// OfflineIssued = issued in Offline mode, waiting for its file to be processed by FDMS.
    /// </summary>
    public enum ReceiptStatus { Pending = 0, Submitted = 1, Rejected = 2, Held = 3, Cancelled = 4, OfflineIssued = 5 }

    public class Receipt
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid DeviceId { get; set; }
        public int FiscalDayId { get; set; }
        public int FiscalDayNo { get; set; }
        /// <summary>Caller's own reference; (ClientId, DeviceId, ExternalRef) is unique and makes submission idempotent.</summary>
        public string ExternalRef { get; set; } = "";
        public string InvoiceNo { get; set; } = "";
        public ReceiptType Type { get; set; }
        public string Currency { get; set; } = "";
        public int ReceiptCounter { get; set; }
        public long ReceiptGlobalNo { get; set; }
        public DateTime ReceiptDate { get; set; }
        public decimal Total { get; set; }
        public Guid? OriginalReceiptId { get; set; }          // credit / debit note target
        /// <summary>Held receipts: the request to issue when the hold ends.</summary>
        public string? RequestJson { get; set; }
        public DateTime? HoldUntil { get; set; }
        public int? OfflineFileId { get; set; }
        public ReceiptStatus Status { get; set; }

        public string Hash { get; set; } = "";
        public string Signature { get; set; } = "";
        /// <summary>The exact ReceiptPayload JSON sent (or to be re-sent) to FDMS.</summary>
        public string PayloadJson { get; set; } = "";

        public long? FdmsReceiptId { get; set; }
        public DateTime? ServerDate { get; set; }
        public string? ServerSignatureJson { get; set; }
        public string? ValidationErrorsJson { get; set; }
        public string? QrUrl { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public enum OfflineFileStatus { Pending = 0, Uploaded = 1, Processed = 2, ProcessedWithErrors = 3 }

    /// <summary>A fiscal-day file (header + receipts [+ footer]) for FDMS submitFile, kept until FDMS has processed it.</summary>
    public class OfflineFile
    {
        public int Id { get; set; }
        public Guid ClientId { get; set; }
        public Guid DeviceId { get; set; }
        public int FiscalDayNo { get; set; }
        public int Sequence { get; set; }
        public int ReceiptCount { get; set; }
        public bool HasFooter { get; set; }
        public OfflineFileStatus Status { get; set; }
        /// <summary>The exact base64 text that is uploaded.</summary>
        public string ContentBase64 { get; set; } = "";
        public string? OperationId { get; set; }
        public string? ErrorsJson { get; set; }
        public string? LastError { get; set; }
        public int Attempts { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UploadedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }

    public enum WebhookStatus { Pending = 0, Delivered = 1, Failed = 2 }

    /// <summary>Outbox row: written in the same SaveChanges as the state change, delivered by a background job with retries.</summary>
    public class WebhookDelivery
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string EventType { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public WebhookStatus Status { get; set; }
        public int Attempts { get; set; }
        public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
        public DateTime? DeliveredAt { get; set; }
        public string? LastError { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class AuditLog
    {
        public long Id { get; set; }
        public Guid? ClientId { get; set; }
        public Guid? ApiKeyId { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string Action { get; set; } = "";
        public string? Detail { get; set; }
        public DateTime At { get; set; } = DateTime.UtcNow;
        /// <summary>user (a signed-in person), token (an integrating system) or system (background work).</summary>
        public string ActorType { get; set; } = "system";
        public string? IpAddress { get; set; }
        /// <summary>What was acted on, e.g. "Receipt", with its id, so the trail can link back.</summary>
        public string? Entity { get; set; }
        public string? EntityId { get; set; }
    }

    /// <summary>
    /// One call a company's people or integrating systems made to the API: who (person or token), which device, what, the outcome and how long it took.
    /// Unlike the audit log (operator only), a company can read its own activity, so it can see what its systems are doing on its devices.
    /// </summary>
    public class ApiActivity
    {
        public long Id { get; set; }
        public Guid ClientId { get; set; }
        public Guid? DeviceId { get; set; }
        public Guid? ApiKeyId { get; set; }
        public string? UserId { get; set; }
        public string? ActorName { get; set; }
        /// <summary>user (signed in to the console) or token (an integrating system).</summary>
        public string ActorType { get; set; } = "user";
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public int StatusCode { get; set; }
        public int DurationMs { get; set; }
        public string? ErrorCode { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public DateTime At { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A database backup file the gateway wrote (on demand or on schedule). The file lives outside the web root; only the platform operator can list, download or delete it.</summary>
    public class DatabaseBackup
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        /// <summary>manual or scheduled.</summary>
        public string Kind { get; set; } = "manual";
        /// <summary>Running, Ok or Failed.</summary>
        public string Status { get; set; } = "Running";
        public string? Error { get; set; }
        public int Tables { get; set; }
        public long Rows { get; set; }
        /// <summary>SHA-256 of the file, so a copy can be checked against the original.</summary>
        public string? Sha256 { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
    }

    /// <summary>A platform-wide setting the operator can change in the console (key/value), e.g. the public address of the API.</summary>
    public class SystemSetting
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }
    }

    /// <summary>An error the gateway hit (unhandled exception, ZIMRA unreachable, background job failure) with enough context to diagnose it.</summary>
    public class ErrorLog
    {
        public long Id { get; set; }
        public string Uuid { get; set; } = Guid.NewGuid().ToString("N")[..12];
        /// <summary>Critical, Error, Warning or Info.</summary>
        public string Severity { get; set; } = "Error";
        public string? Source { get; set; }
        public string Message { get; set; } = "";
        public string? ExceptionType { get; set; }
        public string? StackTrace { get; set; }
        public string? Path { get; set; }
        public string? Method { get; set; }
        public int? StatusCode { get; set; }
        public string? IpAddress { get; set; }
        public string? UserName { get; set; }
        public Guid? ClientId { get; set; }
        public string? TraceId { get; set; }
        public string? MachineName { get; set; }
        public string? Environment { get; set; }
        /// <summary>Same error repeating (same type, message shape and path) is counted on one row instead of flooding the list.</summary>
        public string Fingerprint { get; set; } = "";
        public int Occurrences { get; set; } = 1;
        public bool IsResolved { get; set; }
        public string? Resolution { get; set; }
        public string? ResolvedBy { get; set; }
        public DateTime? ResolvedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    }
}
