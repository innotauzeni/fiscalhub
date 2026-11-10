namespace Fdms.Domain.Entities
{
    /// <summary>
    /// A company's link to an outside accounting system (Zoho Books first). Tokens are kept encrypted; the access token is short-lived and renewed
    /// from the refresh token. One connection per company and provider.
    /// </summary>
    public class IntegrationConnection
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>zoho-books.</summary>
        public string Provider { get; set; } = "zoho-books";
        /// <summary>ChooseOrganization (signed in, organisation not picked yet), Connected, NeedsReconnect (access was revoked or expired), Disconnected.</summary>
        public string Status { get; set; } = "Connected";
        /// <summary>The provider's sign-in server for this account's region, e.g. https://accounts.zoho.com.</summary>
        public string AccountsServer { get; set; } = "";
        /// <summary>The provider's API server for this account's region, e.g. https://www.zohoapis.com.</summary>
        public string ApiDomain { get; set; } = "";
        public string? OrganizationId { get; set; }
        public string? OrganizationName { get; set; }
        /// <summary>The organisations the account can reach, while one is being chosen (JSON).</summary>
        public string? OrganizationsJson { get; set; }
        /// <summary>The company's own Zoho app (Self Client or server-based), used instead of the platform's when set.</summary>
        public string? OwnClientId { get; set; }
        public string? OwnClientSecretProtected { get; set; }
        public string? RefreshTokenProtected { get; set; }
        public string? AccessTokenProtected { get; set; }
        public DateTime? AccessTokenExpiresAt { get; set; }
        public DateTime? LastTokenRefreshAt { get; set; }
        public string? ConnectedBy { get; set; }
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastSyncAt { get; set; }
        public DateTime? SyncRequestedAt { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastErrorAt { get; set; }
        /// <summary>Documents changed after this moment are fetched on the next sync.</summary>
        public DateTime? SyncCursor { get; set; }

        // ---- what the company chose ----
        /// <summary>Fiscalise new documents straight away (otherwise they wait for someone to click Fiscalise).</summary>
        public bool AutoFiscalise { get; set; }
        /// <summary>The fiscal device the documents are issued on.</summary>
        public Guid? DeviceId { get; set; }
        /// <summary>HS code for lines that carry none in the provider.</summary>
        public string? DefaultHsCode { get; set; }
        /// <summary>How the sale is recorded for ZIMRA when the provider does not say (Credit = on account).</summary>
        public string DefaultMoneyType { get; set; } = "Credit";
        /// <summary>Write the fiscal number, verification link and PDF back to the provider.</summary>
        public bool PostBack { get; set; } = true;
        /// <summary>Only documents dated on or after this day are taken (so years of history are not fiscalised by mistake).</summary>
        public DateTime? StartDate { get; set; }
        /// <summary>Connectors: what the product needs besides a token, e.g. Odoo {"url","database","username","userId"}, or the QuickBooks environment.</summary>
        public string? ExtraJson { get; set; }
        /// <summary>The secret in the webhook address the provider calls when something changes.</summary>
        public string WebhookKey { get; set; } = Guid.NewGuid().ToString("N");

        // ---- usage, for the health view and billing ----
        public DateTime? ApiCallsDay { get; set; }
        public int ApiCallsToday { get; set; }
        public long ApiCallsTotal { get; set; }

        // ---- the Zoho bridge ----
        /// <summary>Signed in with full Zoho Books access (needed for the bridge; older connections asked only for invoices and contacts).</summary>
        public bool FullAccess { get; set; }
        /// <summary>Per module, the last change already sent to partners (JSON {module: time}).</summary>
        public string? BridgeCursorsJson { get; set; }
        public DateTime? BridgeWatchAt { get; set; }
        public bool BridgeWatchRequested { get; set; }
    }

    /// <summary>One document from the outside system (an invoice or a credit note) and where it stands here.</summary>
    public class IntegrationDocument
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid ConnectionId { get; set; }
        /// <summary>invoice or creditnote.</summary>
        public string Kind { get; set; } = "invoice";
        public string ExternalId { get; set; } = "";
        public string ExternalNumber { get; set; } = "";
        public string? CustomerName { get; set; }
        public DateTime? DocumentDate { get; set; }
        public decimal Total { get; set; }
        public string Currency { get; set; } = "";
        public string? ExternalStatus { get; set; }
        public DateTime? ExternalModifiedAt { get; set; }
        /// <summary>Pending, Fiscalised, Failed or Skipped.</summary>
        public string Status { get; set; } = "Pending";
        public Guid? ReceiptId { get; set; }
        public string? Error { get; set; }
        public int Attempts { get; set; }
        /// <summary>When the fiscal details were written back to the provider (null = not yet).</summary>
        public DateTime? PostedBackAt { get; set; }
        public string? PostBackError { get; set; }
        /// <summary>The document changed in the provider after it was fiscalised (the fiscal record cannot change; correct it with a credit note).</summary>
        public bool ChangedAfterFiscalised { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FiscalisedAt { get; set; }
    }
}

namespace Fdms.Domain.Entities
{
    /// <summary>
    /// A key for the Zoho bridge. Only its SHA-256 is kept; the key is shown once. Either a partner's (it reaches the companies that gave the partner access,
    /// <see cref="BridgeGrant"/>) or a company's own (its own system reaches its own Zoho Books, with the modules and write right set on the key).
    /// </summary>
    public class BridgeKey
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? PartnerId { get; set; }
        /// <summary>Set for a company's own key.</summary>
        public Guid? ClientId { get; set; }
        /// <summary>A company key: the modules it reaches (comma-separated) and whether it may write.</summary>
        public string Modules { get; set; } = "";
        public bool CanWrite { get; set; }
        public string Name { get; set; } = "";
        /// <summary>The first characters, to recognise the key in lists.</summary>
        public string Prefix { get; set; } = "";
        public string KeyHash { get; set; } = "";
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastUsedAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public DateTime? CallsDay { get; set; }
        public int CallsToday { get; set; }
        public long CallsTotal { get; set; }
    }

    /// <summary>A company lets a partner use its Zoho Books through the gateway: which modules, and whether it may write.</summary>
    public class BridgeGrant
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid PartnerId { get; set; }
        /// <summary>Comma-separated module keys (contacts, items, invoices ...).</summary>
        public string Modules { get; set; } = "";
        public bool CanWrite { get; set; }
        public bool IsActive { get; set; } = true;
        /// <summary>Calls through the bridge in the current month (for the company and the platform).</summary>
        public DateTime? CallsMonth { get; set; }
        public long CallsThisMonth { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? RevokedAt { get; set; }
    }

    /// <summary>Where a partner's system (or a company's own system) wants to hear about changes, and which modules it watches. One per partner and one per company.</summary>
    public class BridgeEndpoint
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? PartnerId { get; set; }
        public Guid? ClientId { get; set; }
        public string? WebhookUrl { get; set; }
        public string? SecretProtected { get; set; }
        /// <summary>Comma-separated module keys whose changes are sent.</summary>
        public string WatchModules { get; set; } = "";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A change in a company's Zoho Books, for one partner: delivered to its webhook (signed, retried) and kept in its event feed.</summary>
    public class BridgeEvent
    {
        public long Id { get; set; }
        /// <summary>The partner it is for; null = the company's own system.</summary>
        public Guid? PartnerId { get; set; }
        public Guid ClientId { get; set; }
        public string Module { get; set; } = "";
        public string RecordId { get; set; } = "";
        public string EventType { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Pending, Delivered, Failed, or Stored (no webhook: the partner reads the feed).</summary>
        public string Status { get; set; } = "Pending";
        public int Attempts { get; set; }
        public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
        public DateTime? DeliveredAt { get; set; }
        public string? LastError { get; set; }
    }
}

namespace Fdms.Domain.Entities
{
    /// <summary>
    /// Whether a company may use the Zoho Books integration. A company applies, the platform owner approves (raising an invoice for the set-up fee when there is one)
    /// and the integration switches on when that invoice is paid. The owner can suspend it at any time.
    /// </summary>
    public class IntegrationAccess
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>Which product: zoho (Zoho Books), quickbooks, xero, sage or odoo. A company applies for each separately.</summary>
        public string Provider { get; set; } = "zoho";
        /// <summary>Requested, AwaitingPayment, Active, Rejected or Suspended.</summary>
        public string Status { get; set; } = "Requested";
        /// <summary>What the company wants it for (comma-separated): fiscalise, own_api, partners.</summary>
        public string Uses { get; set; } = "";
        public string? Note { get; set; }
        public string? RequestedBy { get; set; }
        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
        public string? DecidedBy { get; set; }
        public DateTime? DecidedAt { get; set; }
        /// <summary>The owner's reason for rejecting or suspending, or a note on approval.</summary>
        public string? DecisionNote { get; set; }
        public Guid? InvoiceId { get; set; }
        public decimal SetupFee { get; set; }
        public DateTime? ActivatedAt { get; set; }
        public DateTime? SuspendedAt { get; set; }
    }
}
