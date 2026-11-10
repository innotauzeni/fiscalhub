namespace Fdms.Application.Abstractions
{
    // =================================================================================================================
    // Accounting connectors (QuickBooks Online, Xero, Sage Accounting, Odoo): one engine, one small adapter per product.
    // Each adapter turns its product's invoices and credit notes into the shape below; the engine fiscalises and writes the
    // result back. Kept small so a fake adapter stands in for the real products in tests.
    // =================================================================================================================

    /// <summary>The platform's app at the product (OAuth). Sandbox: the product's test environment (QuickBooks).</summary>
    public sealed record ConnectorApp(string ClientId, string ClientSecret, bool Sandbox);
    /// <summary>OrganizationId: when the sign-in names the company at once (QuickBooks: the realm).</summary>
    public sealed record ConnectorTokens(string AccessToken, string? RefreshToken, int ExpiresInSeconds, string? OrganizationId = null, string? OrganizationName = null);
    /// <summary>What every call needs. OrganizationId: QuickBooks realm, Xero tenant, Sage business, Odoo company. BaseUrl/Database/Username/UserId: Odoo.</summary>
    public sealed record ConnectorSession(string Provider, string AccessToken, string? OrganizationId, string? BaseUrl, string? Database, string? Username, string? UserId, bool Sandbox);
    public sealed record ConnectorOrganization(string Id, string Name, string? Currency);
    public sealed record ConnectorDocumentHead(string Id, string Number, string Status, DateTime? Date, decimal Total, string Currency, string? CustomerName, DateTime? LastModified);
    public sealed record ConnectorPage(IReadOnlyList<ConnectorDocumentHead> Items, bool HasMore);
    /// <summary>One line, VAT included in GrossTotal. Exempt: the line carries no VAT by law (not zero-rated).</summary>
    public sealed record ConnectorLine(string Name, string? HsCode, decimal Quantity, decimal GrossTotal, decimal? TaxPercent, bool Exempt, bool Discount = false);
    public sealed record ConnectorBuyer(string Name, string? Tin, string? VatNumber, string? Email, string? Phone);
    /// <summary>
    /// An invoice or credit note in full, every amount VAT included. Status: draft, open, paid or void (each adapter maps its own words).
    /// ReferenceInvoiceId: the invoice a credit note credits.
    /// </summary>
    public sealed record ConnectorDocument(string Id, string Number, string Status, DateTime? Date, string Currency, decimal Total, IReadOnlyList<ConnectorLine> Lines,
        ConnectorBuyer? Buyer, string? ReferenceInvoiceId, string? Notes);
    /// <summary>What goes back to the product: a note with the fiscal details, and the fiscal PDF (invoices).</summary>
    public sealed record ConnectorWriteBack(string Text, string FiscalNumber, string? VerificationCode, string? VerifyUrl, byte[]? Pdf, string PdfName);

    /// <summary>A refusal from the product. Unauthorized: the sign-in is gone. RateLimited: too many calls for now.</summary>
    public sealed class ConnectorException : Exception
    {
        public ConnectorException(int status, string message, bool unauthorized = false, bool rateLimited = false) : base(message) { Status = status; Unauthorized = unauthorized; RateLimited = rateLimited; }
        public int Status { get; }
        public bool Unauthorized { get; }
        public bool RateLimited { get; }
    }

    /// <summary>One accounting product's API. Kind is invoice or creditnote.</summary>
    public interface IAccountingConnector
    {
        string Key { get; }
        /// <summary>OAuth products: where to send the person to sign in.</summary>
        string AuthorizeUrl(ConnectorApp app, string redirectUri, string state);
        /// <summary>OAuth products: the code from the sign-in. Callback carries the other values the product sends back (QuickBooks: realmId).</summary>
        Task<ConnectorTokens> ExchangeCodeAsync(ConnectorApp app, string redirectUri, string code, IReadOnlyDictionary<string, string?> callback, CancellationToken ct);
        Task<ConnectorTokens> RefreshAsync(ConnectorApp app, string refreshToken, CancellationToken ct);
        /// <summary>Key-based products (Odoo): checks the address, database, user and key; AccessToken comes back as the user id.</summary>
        Task<ConnectorTokens> SignInAsync(ConnectorSession s, CancellationToken ct);
        Task<IReadOnlyList<ConnectorOrganization>> OrganizationsAsync(ConnectorSession s, CancellationToken ct);
        /// <summary>Documents changed since (null: from the start), page 1 upwards; the caller pages until HasMore is false.</summary>
        Task<ConnectorPage> ListAsync(ConnectorSession s, string kind, DateTime? since, int page, CancellationToken ct);
        Task<ConnectorDocument> GetAsync(ConnectorSession s, string kind, string id, CancellationToken ct);
        Task WriteBackAsync(ConnectorSession s, string kind, string id, ConnectorWriteBack wb, CancellationToken ct);
    }

    // ---- what the console works with ----

    public sealed record ConnectorSettingsDto(bool AutoFiscalise, Guid? DeviceId, string? DefaultHsCode, string DefaultMoneyType, bool PostBack, DateTime? StartDate);
    /// <summary>
    /// A company's view of one product: whether it may use it (AccessStatus), whether the platform set it up (Available), its connection and settings. Never a token.
    /// Status: NotConnected, ChooseOrganization, Connected or NeedsReconnect.
    /// </summary>
    public sealed record ConnectorStatusDto(string Product, string Label, string Auth, string AccessStatus, bool Available, string? UnavailableReason,
        Guid? ConnectionId, string Status, string? OrganizationId, string? OrganizationName, IReadOnlyList<ConnectorOrganization> Organizations,
        string? ConnectedBy, DateTime? ConnectedAt, DateTime? LastSyncAt, string? LastError, DateTime? LastErrorAt,
        string? BaseUrl, string? Database, string? Username, ConnectorSettingsDto Settings, IntegrationCountsDto Counts, IntegrationPricingDto Pricing);
    public sealed record StartConnectorResult(string AuthorizeUrl);
    /// <summary>Odoo: the company's Odoo address, database, sign-in name and an API key (Odoo: Preferences → Account Security → New API Key).</summary>
    public sealed record ConnectOdooRequest(string Url, string Database, string Username, string ApiKey);
    public sealed record ConnectorCallbackResult(bool Ok, string RedirectTo);

    // ---- the platform owner ----
    public sealed record ConnectorAppDto(string Product, string Label, string Auth, string? ClientId, bool HasSecret, bool Sandbox, string RedirectUri, bool Configured);
    public sealed record SaveConnectorAppRequest(string? ClientId, string? ClientSecret, bool ClearSecret, bool Sandbox);

    public interface IConnectorService
    {
        Task<IReadOnlyList<ConnectorStatusDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<ConnectorStatusDto> GetAsync(Guid clientId, string product, CancellationToken ct);
        Task<StartConnectorResult> StartAsync(Guid clientId, string product, CancellationToken ct);
        Task<ConnectorCallbackResult> CallbackAsync(string product, string? code, string? state, string? error, IReadOnlyDictionary<string, string?> query, CancellationToken ct);
        Task<ConnectorStatusDto> ConnectOdooAsync(Guid clientId, ConnectOdooRequest req, CancellationToken ct);
        Task<ConnectorStatusDto> ChooseOrganizationAsync(Guid clientId, string product, string organizationId, CancellationToken ct);
        Task<ConnectorStatusDto> SaveSettingsAsync(Guid clientId, string product, ZohoSettingsRequest req, CancellationToken ct);
        Task DisconnectAsync(Guid clientId, string product, CancellationToken ct);
        Task<ZohoSyncResultDto> SyncNowAsync(Guid clientId, string product, CancellationToken ct);
        Task<IntegrationDocumentPage> DocumentsAsync(Guid clientId, string product, string? status, string? search, int page, int pageSize, CancellationToken ct);
        Task<IntegrationDocumentDto> FiscaliseAsync(Guid clientId, string product, Guid documentId, CancellationToken ct);
        Task<IntegrationDocumentDto> SkipAsync(Guid clientId, string product, Guid documentId, CancellationToken ct);
        Task<IntegrationDocumentDto> PostBackAsync(Guid clientId, string product, Guid documentId, CancellationToken ct);
        /// <summary>The background round: syncs the connections that are due. Returns how many were synced.</summary>
        Task<int> RunScheduledAsync(DateTime now, CancellationToken ct);

        Task<IReadOnlyList<ConnectorAppDto>> AppsAsync(CancellationToken ct);
        Task<ConnectorAppDto> SaveAppAsync(string product, SaveConnectorAppRequest req, CancellationToken ct);
        Task<IntegrationPricingDto> PricingAsync(string product, CancellationToken ct);
        Task<IntegrationPricingDto> SavePricingAsync(string product, IntegrationPricingDto req, CancellationToken ct);
    }
}
