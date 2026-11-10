namespace Fdms.Application.Abstractions
{
    // =================================================================================================================
    // Zoho Books: what the gateway needs from Zoho's API (kept small, so a fake can stand in for Zoho in tests)
    // =================================================================================================================

    public sealed record ZohoTokens(string AccessToken, string? RefreshToken, int ExpiresInSeconds, string? ApiDomain);
    public sealed record ZohoAccess(string ApiDomain, string AccessToken, string OrganizationId);
    public sealed record ZohoOrganization(string Id, string Name, string? CurrencyCode);
    /// <summary>One line of a document list.</summary>
    public sealed record ZohoDocumentHead(string Id, string Number, string Status, DateTime? Date, decimal Total, string Currency, string? CustomerName, DateTime? LastModified);
    public sealed record ZohoPage(IReadOnlyList<ZohoDocumentHead> Items, bool HasMore);
    public sealed record ZohoCustomField(string Id, string Label, string? Value);
    public sealed record ZohoLine(string Name, string? Description, decimal Quantity, decimal Rate, decimal ItemTotal, decimal? TaxPercent, string? TaxName, string? HsCode);
    /// <summary>An invoice or credit note in full. ReferenceInvoiceId: the invoice a credit note credits.</summary>
    public sealed record ZohoDocument(string Id, string Number, string Status, DateTime? Date, string Currency, string? CustomerId, string? CustomerName, bool TaxInclusive,
        decimal Total, decimal EntityDiscount, decimal ShippingCharge, decimal? ShippingTaxPercent, decimal Adjustment, IReadOnlyList<ZohoLine> Lines,
        IReadOnlyList<ZohoCustomField> CustomFields, string? ReferenceInvoiceId, string? Notes);
    public sealed record ZohoRawResponse(int Status, string Body);
    public sealed record ZohoContact(string Name, string? CompanyName, string? Tin, string? VatNumber, string? Email, string? Phone);

    /// <summary>A refusal from Zoho. Unauthorized: the access is gone (sign in again). RateLimited: too many calls for now.</summary>
    public sealed class ZohoException : Exception
    {
        public ZohoException(int status, string message, bool unauthorized = false, bool rateLimited = false) : base(message) { Status = status; Unauthorized = unauthorized; RateLimited = rateLimited; }
        public int Status { get; }
        public bool Unauthorized { get; }
        public bool RateLimited { get; }
    }

    public interface IZohoApi
    {
        /// <summary>redirectUri null: a Self Client code (made in Zoho's API console, no redirect).</summary>
        Task<ZohoTokens> ExchangeCodeAsync(string accountsServer, string clientId, string clientSecret, string? redirectUri, string code, CancellationToken ct);
        Task<ZohoTokens> RefreshAsync(string accountsServer, string clientId, string clientSecret, string refreshToken, CancellationToken ct);
        Task RevokeAsync(string accountsServer, string refreshToken, CancellationToken ct);
        Task<IReadOnlyList<ZohoOrganization>> OrganizationsAsync(string apiDomain, string accessToken, CancellationToken ct);
        /// <summary>kind: invoice or creditnote. Newest changes first are not required; the caller pages until HasMore is false.</summary>
        Task<ZohoPage> ListAsync(ZohoAccess a, string kind, DateTime? modifiedSince, int page, CancellationToken ct);
        Task<ZohoDocument> GetAsync(ZohoAccess a, string kind, string id, CancellationToken ct);
        Task<ZohoContact?> ContactAsync(ZohoAccess a, string contactId, CancellationToken ct);
        Task CommentAsync(ZohoAccess a, string kind, string id, string text, CancellationToken ct);
        Task AttachAsync(ZohoAccess a, string kind, string id, byte[] pdf, string fileName, CancellationToken ct);
        Task SetCustomFieldsAsync(ZohoAccess a, string kind, string id, IReadOnlyList<(string FieldId, string Value)> values, CancellationToken ct);
        /// <summary>Any Books API call, passed through: path below /books/v3/ (organization_id is added), the query without it, a JSON body. Zoho's answer comes back as it is.</summary>
        Task<ZohoRawResponse> RawAsync(ZohoAccess a, string method, string path, string? query, string? jsonBody, CancellationToken ct);
    }

    // =================================================================================================================
    // What the console and the API work with
    // =================================================================================================================

    public sealed record IntegrationCountsDto(int Pending, int Fiscalised, int Failed, int Skipped, int FiscalisedThisMonth, int NotPostedBack);
    public sealed record ZohoRegionDto(string Key, string Label);

    /// <summary>The company's Zoho Books connection, its health and its settings. Never carries a token.</summary>
    public sealed record ZohoConnectionDto(
        bool Available, string? UnavailableReason, IReadOnlyList<ZohoRegionDto> Regions,
        Guid? Id, string Status, string? OrganizationId, string? OrganizationName, IReadOnlyList<ZohoOrganization> Organizations,
        string? AccountsServer, string? ConnectedBy, DateTime? ConnectedAt, DateTime? LastTokenRefreshAt, DateTime? AccessTokenExpiresAt,
        DateTime? LastSyncAt, string? LastError, DateTime? LastErrorAt, int ApiCallsToday, long ApiCallsTotal,
        bool AutoFiscalise, Guid? DeviceId, string? DefaultHsCode, string DefaultMoneyType, bool PostBack, DateTime? StartDate,
        string? WebhookUrl, IntegrationCountsDto Counts, IntegrationPricingDto Pricing,
        bool PlatformApp = true, string? OwnClientId = null, string? RedirectUri = null, string AccessStatus = "Active");

    /// <summary>The company's own Zoho app. With a GrantCode (Zoho API console, Self Client) it connects at once; without, the answer has the sign-in address (a server-based app needing the redirect address).</summary>
    public sealed record ConnectOwnZohoRequest(string? Region, string? ClientId, string? ClientSecret, string? GrantCode);
    public sealed record ConnectOwnZohoResult(string? AuthorizeUrl, ZohoConnectionDto? Connection);

    public sealed record StartZohoRequest(string? Region);
    public sealed record StartZohoResult(string AuthorizeUrl);
    public sealed record ChooseOrganizationRequest(string OrganizationId);
    public sealed record ZohoSettingsRequest(bool AutoFiscalise, Guid? DeviceId, string? DefaultHsCode, string? DefaultMoneyType, bool PostBack, DateTime? StartDate);

    public sealed record IntegrationDocumentDto(Guid Id, string Kind, string ExternalId, string ExternalNumber, string? CustomerName, DateTime? DocumentDate, decimal Total, string Currency,
        string? ExternalStatus, string Status, Guid? ReceiptId, string? FiscalInvoiceNo, string? Error, int Attempts, DateTime? PostedBackAt, string? PostBackError, bool ChangedAfterFiscalised,
        DateTime CreatedAt, DateTime? FiscalisedAt, string? ExternalUrl);
    public sealed record IntegrationDocumentPage(IReadOnlyList<IntegrationDocumentDto> Items, int Total, int Page, int PageSize);
    public sealed record ZohoSyncResultDto(int New, int Updated, int Fiscalised, int Failed, int PostedBack, string? Error);
    /// <summary>The answer to Zoho's sign-in: where to send the person's browser next.</summary>
    public sealed record ZohoCallbackResult(bool Ok, string RedirectTo);

    // ---- the platform owner ----
    public sealed record ZohoAppSettingsDto(string? ClientId, bool HasSecret, string RedirectUri, bool Configured);
    public sealed record SaveZohoAppRequest(string? ClientId, string? ClientSecret, bool ClearSecret);
    /// <summary>What a connection costs: a monthly fee per connected company, a number of documents included, and a price per document above that.</summary>
    public sealed record IntegrationPricingDto(bool Charge, decimal MonthlyFee, string Currency, int IncludedDocuments, decimal PricePerExtraDocument, decimal BridgeMonthlyFee = 0, decimal SetupFee = 0);

    // ---- applying for the integration ----
    /// <summary>Status: None (never applied), Requested, AwaitingPayment, Active, Rejected or Suspended.</summary>
    public sealed record IntegrationAccessDto(Guid? Id, Guid ClientId, string Company, string Status, IReadOnlyList<string> Uses, string? Note, string? RequestedBy, DateTime? RequestedAt,
        string? DecidedBy, DateTime? DecidedAt, string? DecisionNote, decimal SetupFee, string Currency, Guid? InvoiceId, string? InvoiceNumber, string? InvoiceStatus, DateTime? ActivatedAt, DateTime? SuspendedAt, string Provider = "zoho", string ProviderLabel = "Zoho Books");
    public sealed record ApplyIntegrationRequest(IReadOnlyList<string>? Uses, string? Note, string? Provider = null);
    public sealed record ApproveIntegrationRequest(decimal? SetupFee, string? Note);
    public sealed record IntegrationDecisionRequest(string? Reason);
    /// <summary>A live bridge key as the owner sees it: whose it is (a partner or a company) and when it was last used.</summary>
    public sealed record OwnerBridgeKeyDto(Guid Id, string Name, string Prefix, string Owner, string OwnerKind, Guid? ClientId, DateTime CreatedAt, DateTime? LastUsedAt);

    public interface IIntegrationAccessService
    {
        Task<IntegrationAccessDto> MineAsync(Guid clientId, CancellationToken ct, string provider = IntegrationProducts.Zoho);
        Task<IntegrationAccessDto> ApplyAsync(Guid clientId, ApplyIntegrationRequest req, CancellationToken ct);
        Task<IReadOnlyList<IntegrationAccessDto>> ListAsync(string? status, CancellationToken ct);
        /// <summary>With a set-up fee an invoice is raised and the integration switches on when it is paid; without one it switches on at once.</summary>
        Task<IntegrationAccessDto> ApproveAsync(Guid id, ApproveIntegrationRequest req, CancellationToken ct);
        Task<IntegrationAccessDto> RejectAsync(Guid id, IntegrationDecisionRequest req, CancellationToken ct);
        /// <summary>Switches a company's integration off at once: no sync, no fiscalising from Zoho, no bridge calls, no partner access.</summary>
        Task<IntegrationAccessDto> SuspendAsync(Guid clientId, IntegrationDecisionRequest req, CancellationToken ct, string provider = IntegrationProducts.Zoho);
        Task<IntegrationAccessDto> ReactivateAsync(Guid clientId, CancellationToken ct, string provider = IntegrationProducts.Zoho);
        /// <summary>The owner revokes any bridge key (a partner's or a company's).</summary>
        Task RevokeKeyAsync(Guid keyId, string? reason, CancellationToken ct);
        Task<IReadOnlyList<OwnerBridgeKeyDto>> KeysAsync(CancellationToken ct);
    }
    public sealed record IntegrationMonitorRow(Guid ConnectionId, Guid ClientId, string Company, string Provider, string Status, string? OrganizationName, DateTime? LastTokenRefreshAt,
        DateTime? AccessTokenExpiresAt, DateTime? LastSyncAt, string? LastError, int ApiCallsToday, int FiscalisedThisMonth, int Failed, int Pending, bool AutoFiscalise);
    public sealed record IntegrationMonitorDto(IReadOnlyList<IntegrationMonitorRow> Connections, int Connected, int NeedsAttention, int FiscalisedThisMonth, IReadOnlyList<AmountDto> MonthlyRevenue);

    public interface IIntegrationService
    {
        Task<ZohoConnectionDto> ZohoAsync(Guid clientId, CancellationToken ct);
        Task<StartZohoResult> StartZohoAsync(Guid clientId, StartZohoRequest req, CancellationToken ct);
        /// <summary>Zoho sends the browser back here after sign-in (no session: the state proves who started it).</summary>
        Task<ZohoCallbackResult> CompleteZohoAsync(string? code, string? state, string? accountsServer, string? error, CancellationToken ct);
        Task<ZohoConnectionDto> ChooseOrganizationAsync(Guid clientId, string organizationId, CancellationToken ct);
        Task<ZohoConnectionDto> SaveZohoSettingsAsync(Guid clientId, ZohoSettingsRequest req, CancellationToken ct);
        Task DisconnectZohoAsync(Guid clientId, CancellationToken ct);
        Task<ConnectOwnZohoResult> ConnectOwnAsync(Guid clientId, ConnectOwnZohoRequest req, CancellationToken ct);
        /// <summary>Forgets the company's own Zoho app (the platform's is used from the next sign-in).</summary>
        Task<ZohoConnectionDto> ForgetOwnAppAsync(Guid clientId, CancellationToken ct);
        Task<ZohoSyncResultDto> SyncNowAsync(Guid clientId, CancellationToken ct);
        Task<IntegrationDocumentPage> DocumentsAsync(Guid clientId, string? status, string? search, int page, int pageSize, CancellationToken ct);
        Task<IntegrationDocumentDto> FiscaliseAsync(Guid clientId, Guid documentId, CancellationToken ct);
        Task<IntegrationDocumentDto> SkipAsync(Guid clientId, Guid documentId, CancellationToken ct);
        Task<IntegrationDocumentDto> PostBackAsync(Guid clientId, Guid documentId, CancellationToken ct);
        /// <summary>Zoho's workflow webhook: asks for a sync soon. The body is not trusted; the documents are fetched from Zoho.</summary>
        Task<bool> WebhookAsync(Guid connectionId, string key, CancellationToken ct);
        /// <summary>The background round: syncs connections that are due or asked for. Returns how many were synced.</summary>
        Task<int> RunScheduledAsync(DateTime now, CancellationToken ct);

        Task<ZohoAppSettingsDto> ZohoAppAsync(CancellationToken ct);
        Task<ZohoAppSettingsDto> SaveZohoAppAsync(SaveZohoAppRequest req, CancellationToken ct);
        Task<IntegrationPricingDto> PricingAsync(CancellationToken ct);
        Task<IntegrationPricingDto> SavePricingAsync(IntegrationPricingDto req, CancellationToken ct);
        Task<IntegrationMonitorDto> MonitorAsync(CancellationToken ct);
    }

    /// <summary>What a company's connections add to its next subscription invoice (null = nothing).</summary>
    public interface IIntegrationBilling
    {
        Task<(decimal Amount, string Description)?> ChargeAsync(Guid clientId, DateTime periodStart, DateTime periodEnd, string currency, CancellationToken ct);
    }
}
