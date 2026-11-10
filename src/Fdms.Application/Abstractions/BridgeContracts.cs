namespace Fdms.Application.Abstractions
{
    // =================================================================================================================
    // The Zoho bridge: a partner's system reads and writes its client companies' Zoho Books through the gateway,
    // and hears about changes. The gateway holds the Zoho sign-in; the partner never sees it.
    // =================================================================================================================

    /// <summary>A Zoho Books module the bridge can reach. Path: below /books/v3/. ReadOnly: never written through the bridge. Watchable: changes can be sent.</summary>
    public sealed record BridgeModuleDto(string Key, string Label, string Path, bool ReadOnly, bool Watchable);

    // ---- the company ----
    public sealed record BridgeGrantDto(Guid Id, Guid PartnerId, string PartnerName, string PartnerCode, IReadOnlyList<string> Modules, bool CanWrite, bool IsActive, string? CreatedBy, DateTime CreatedAt,
        long CallsThisMonth);
    public sealed record BridgeGrantsDto(IReadOnlyList<BridgeGrantDto> Grants, IReadOnlyList<BridgeModuleDto> Modules, bool ZohoFullAccess, decimal MonthlyFee, string Currency, bool Charged);
    public sealed record SaveBridgeGrantRequest(string PartnerCode, IReadOnlyList<string> Modules, bool CanWrite);

    // ---- the partner (portal, signed in as a person) ----
    public sealed record BridgeKeyDto(Guid Id, string Name, string Prefix, string? CreatedBy, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt, int CallsToday, long CallsTotal,
        IReadOnlyList<string>? Modules = null, bool CanWrite = false);
    public sealed record CreateBridgeKeyRequest(string Name);
    /// <summary>The key itself appears only here, once.</summary>
    public sealed record CreatedBridgeKeyDto(BridgeKeyDto Key, string Secret);
    public sealed record BridgeEndpointDto(string? WebhookUrl, IReadOnlyList<string> WatchModules, bool HasSecret);
    public sealed record SaveBridgeEndpointRequest(string? WebhookUrl, IReadOnlyList<string>? WatchModules, bool RotateSecret);
    /// <summary>Secret: only when it was just created or rotated.</summary>
    public sealed record SavedBridgeEndpointDto(BridgeEndpointDto Endpoint, string? Secret);
    public sealed record BridgeCompanyDto(Guid CompanyId, string Name, string? OrganizationId, string? OrganizationName, string ConnectionStatus, bool Ready, string? NotReadyReason,
        IReadOnlyList<string> Modules, bool CanWrite, DateTime GrantedAt);
    public sealed record BridgeEventDto(long Id, Guid CompanyId, string Module, string RecordId, string Type, string Status, int Attempts, DateTime CreatedAt, DateTime? DeliveredAt, string? LastError);
    public sealed record BridgePortalDto(IReadOnlyList<BridgeKeyDto> Keys, BridgeEndpointDto Endpoint, IReadOnlyList<BridgeCompanyDto> Companies, IReadOnlyList<BridgeEventDto> RecentEvents,
        IReadOnlyList<BridgeModuleDto> Modules, string BaseUrl, string PartnerCode);

    // ---- the partner's system (signed in with a bridge key) ----
    /// <summary>Who calls the bridge: a partner (PartnerId), or a company's own system (ClientId, with the key's modules and write right).</summary>
    public sealed record BridgeCaller(Guid? PartnerId, Guid? ClientId, Guid KeyId, string KeyName, IReadOnlyList<string> Modules, bool CanWrite);

    // ---- a company's own system ----
    public sealed record CreateOwnKeyRequest(string Name, IReadOnlyList<string> Modules, bool CanWrite);
    public sealed record OwnApiDto(Guid CompanyId, IReadOnlyList<BridgeKeyDto> Keys, BridgeEndpointDto Endpoint, IReadOnlyList<BridgeEventDto> RecentEvents, IReadOnlyList<BridgeModuleDto> Modules,
        string BaseUrl, bool Ready, string? NotReadyReason, bool Charged, decimal MonthlyFee, string Currency, bool ConnectorPlan);
    /// <summary>One change, as the partner's system receives it (webhook body and feed item).</summary>
    public sealed record BridgeFeedItem(long Id, string Type, Guid CompanyId, string Module, string RecordId, DateTime CreatedAt, object? Data);
    public sealed record BridgeFeedDto(IReadOnlyList<BridgeFeedItem> Events, long LastId, bool More);

    // ---- the platform ----
    /// <summary>OwnSystem: a company's own keys (PartnerId is then the company's id).</summary>
    public sealed record BridgeMonitorRow(Guid PartnerId, string Partner, int ActiveKeys, int Companies, int CallsToday, long CallsTotal, int EventsPending, int EventsFailed, string? WebhookHost, DateTime? LastUsedAt, bool OwnSystem = false);
    public sealed record BridgeMonitorDto(IReadOnlyList<BridgeMonitorRow> Partners, int CompaniesShared, int CallsToday);

    public interface IBridgeService
    {
        IReadOnlyList<BridgeModuleDto> Modules { get; }

        // the company
        Task<BridgeGrantsDto> GrantsAsync(Guid clientId, CancellationToken ct);
        Task<BridgeGrantDto> SaveGrantAsync(Guid clientId, SaveBridgeGrantRequest req, CancellationToken ct);
        Task RevokeGrantAsync(Guid clientId, Guid grantId, CancellationToken ct);

        // the partner's portal
        Task<BridgePortalDto> PortalAsync(Guid partnerId, CancellationToken ct);
        Task<CreatedBridgeKeyDto> CreateKeyAsync(Guid partnerId, CreateBridgeKeyRequest req, CancellationToken ct);
        Task RevokeKeyAsync(Guid partnerId, Guid keyId, CancellationToken ct);
        Task<SavedBridgeEndpointDto> SaveEndpointAsync(Guid partnerId, SaveBridgeEndpointRequest req, CancellationToken ct);

        // the partner's system
        /// <summary>Checks a bridge key (and counts the call). Null when it is unknown or revoked.</summary>
        Task<BridgeCaller?> ValidateKeyAsync(string key, CancellationToken ct);
        Task<IReadOnlyList<BridgeCompanyDto>> CompaniesAsync(BridgeCaller caller, CancellationToken ct);
        /// <summary>Passes a call to the company's Zoho Books: method GET, POST or PUT; path below /books/v3/ starting with a module; query and JSON body as sent.</summary>
        Task<ZohoRawResponse> ProxyAsync(BridgeCaller caller, Guid companyId, string method, string path, string? query, string? body, CancellationToken ct);
        Task<BridgeFeedDto> FeedAsync(BridgeCaller caller, long after, int take, CancellationToken ct);

        // the company's own system
        Task<OwnApiDto> OwnAsync(Guid clientId, CancellationToken ct);
        Task<CreatedBridgeKeyDto> CreateOwnKeyAsync(Guid clientId, CreateOwnKeyRequest req, CancellationToken ct);
        Task RevokeOwnKeyAsync(Guid clientId, Guid keyId, CancellationToken ct);
        Task<SavedBridgeEndpointDto> SaveOwnEndpointAsync(Guid clientId, SaveBridgeEndpointRequest req, CancellationToken ct);

        /// <summary>The background round: looks for changes in watched modules and delivers due events. Returns how many events were created or delivered.</summary>
        Task<int> RunAsync(DateTime now, CancellationToken ct);

        Task<BridgeMonitorDto> MonitorAsync(CancellationToken ct);
    }

    /// <summary>Sends one signed event to a partner's webhook. Replaced in tests.</summary>
    public interface IBridgeSender
    {
        /// <summary>Null when the partner's system accepted it, otherwise why not.</summary>
        Task<string?> SendAsync(string url, string eventType, long deliveryId, string timestamp, string signature, string body, CancellationToken ct);
    }
}
