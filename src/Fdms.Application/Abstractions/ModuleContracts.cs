namespace Fdms.Application.Abstractions
{
    // ---- customers (the buyer register) ------------------------------------------------------------------------------------

    public sealed record CustomerDto(Guid Id, string Name, string? TradeName, string? Tin, string? VatNumber, string? Email, string? Phone, string? Address, string? Notes, bool IsActive, DateTime CreatedAt);
    public sealed record SaveCustomerRequest(Guid? Id, string Name, string? TradeName, string? Tin, string? VatNumber, string? Email, string? Phone, string? Address, string? Notes, bool IsActive = true);

    public interface ICustomerService
    {
        Task<IReadOnlyList<CustomerDto>> ListAsync(Guid clientId, string? search, bool includeInactive, CancellationToken ct);
        Task<CustomerDto> SaveAsync(Guid clientId, SaveCustomerRequest req, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
    }

    // ---- products (the item catalogue: HS code and tax kept once, so an invoice cannot be sent with a wrong one) -----------

    public sealed record ProductDto(Guid Id, string? Code, string Name, string? HsCode, string? Unit, decimal UnitPrice, string Currency, decimal? TaxPercent, bool IsExempt, bool IsActive, DateTime CreatedAt);
    public sealed record SaveProductRequest(Guid? Id, string? Code, string Name, string? HsCode, string? Unit, decimal UnitPrice, string? Currency, decimal? TaxPercent, bool IsExempt, bool IsActive = true);

    public interface IProductService
    {
        Task<IReadOnlyList<ProductDto>> ListAsync(Guid clientId, string? search, bool includeInactive, CancellationToken ct);
        Task<ProductDto> SaveAsync(Guid clientId, SaveProductRequest req, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
    }

    // ---- reports -----------------------------------------------------------------------------------------------------------

    /// <summary>Sales for one tax rate: Sales includes the tax; Tax is the tax inside it.</summary>
    public sealed record TaxLineDto(string Label, decimal? TaxPercent, decimal Sales, decimal Tax);
    public sealed record PaymentLineDto(string MoneyType, decimal Amount);
    public sealed record CurrencyReportDto(string Currency, int Invoices, decimal InvoicesTotal, int CreditNotes, decimal CreditNotesTotal, int DebitNotes, decimal DebitNotesTotal,
        decimal NetSales, decimal NetTax, IReadOnlyList<TaxLineDto> Taxes, IReadOnlyList<PaymentLineDto> Payments);
    public sealed record DayPointDto(string Date, string Currency, int Documents, decimal Net);
    public sealed record ReportCountsDto(int Counted, int Pending, int OfflineIssued, int Rejected, int Cancelled, int Held);
    public sealed record SalesReportDto(string From, string To, bool Truncated, ReportCountsDto Counts, IReadOnlyList<CurrencyReportDto> Currencies, IReadOnlyList<DayPointDto> Days);

    public sealed record CounterTotalDto(string Currency, string Type, decimal Value);
    public sealed record FiscalDayReportRowDto(int DeviceZimraId, int FiscalDayNo, string Status, DateTime OpenedAt, DateTime? ClosedAt, int Receipts, bool Offline, IReadOnlyList<CounterTotalDto> Totals);

    // ---- activity: who and which systems did what -----------------------------------------------------------------------------

    public sealed record ActivityDto(long Id, Guid ClientId, string? ClientName, Guid? DeviceId, int? ZimraDeviceId, string ActorType, string? ActorName, string Method, string Path,
        int StatusCode, int DurationMs, string? ErrorCode, string? IpAddress, string? UserAgent, DateTime At);
    public sealed record ActivitySearch(Guid? ClientId = null, Guid? DeviceId = null, string? ActorType = null, string? Outcome = null, string? Search = null, DateTime? From = null, DateTime? To = null, int Page = 1, int PageSize = 50);
    public sealed record DeviceActivityDto(Guid DeviceId, int ZimraDeviceId, string? Name, string? Branch, bool IsActive, DateTime? LastSeenAt, DateTime? LastTokenCallAt, int Calls24h, int Errors24h, int Receipts24h, int TokenCount, string? LastActor);
    public sealed record ActivityPage(IReadOnlyList<ActivityDto> Items, int Total, int Page, int PageSize);

    public interface IActivityService
    {
        /// <summary>Records one call. Never throws: tracking must not break the request.</summary>
        Task RecordAsync(ApiActivityEntry entry, CancellationToken ct);
        /// <summary>callerClientId null = the platform operator, who sees every company.</summary>
        Task<ActivityPage> SearchAsync(Guid? callerClientId, ActivitySearch q, CancellationToken ct);
        Task<IReadOnlyList<DeviceActivityDto>> DevicesAsync(Guid? callerClientId, Guid? clientId, CancellationToken ct);
    }

    public sealed record ApiActivityEntry(Guid ClientId, Guid? DeviceId, Guid? ApiKeyId, string? UserId, string? ActorName, string ActorType, string Method, string Path, int StatusCode, int DurationMs, string? ErrorCode, string? IpAddress, string? UserAgent);

    // ---- database backups (platform operator) -----------------------------------------------------------------------------------

    public sealed record BackupDto(Guid Id, string FileName, long SizeBytes, string Kind, string Status, string? Error, int Tables, long Rows, string? Sha256, string? CreatedBy, DateTime CreatedAt, DateTime? FinishedAt, bool FileExists);
    public sealed record BackupInfoDto(bool Supported, string Provider, string Folder, bool ScheduleEnabled, int IntervalHours, int KeepLast, DateTime? LastOkAt, DateTime? NextDueAt);

    public interface IBackupService
    {
        Task<BackupInfoDto> InfoAsync(CancellationToken ct);
        Task<IReadOnlyList<BackupDto>> ListAsync(CancellationToken ct);
        /// <summary>Writes a consistent, compressed SQL dump of the whole database. Throws BACKUP_UNSUPPORTED when the database is not MySQL.</summary>
        Task<BackupDto> CreateAsync(string kind, CancellationToken ct);
        Task<(string Path, string FileName)> GetFileAsync(Guid id, CancellationToken ct);
        /// <summary>The SQL that builds the empty database structure (every table, index and migration record), safe to run again on a server that already has some of it. For setting up a new server; the data comes from a backup.</summary>
        Task<(string FileName, string Sql)> SchemaScriptAsync(CancellationToken ct);
        Task DeleteAsync(Guid id, CancellationToken ct);
        /// <summary>Called by the background job: makes a backup when the last good one is older than the interval, then trims old ones. Returns true when one was made.</summary>
        Task<bool> RunScheduledAsync(CancellationToken ct);
    }

    public interface IReportService
    {
        Task<SalesReportDto> SalesAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct);
        Task<string> SalesCsvAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct);
        Task<IReadOnlyList<FiscalDayReportRowDto>> FiscalDaysAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct);
        Task<string> FiscalDaysCsvAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct);
    }

    // ---- compliance centre -------------------------------------------------------------------------------------------------

    /// <summary>Status: ok, info, warn or critical. Route is the console page that fixes it.</summary>
    public sealed record ComplianceCheckDto(string Id, string Area, string Title, string Status, string Detail, string? Device, string? Route);
    public sealed record ComplianceDto(int Score, string Status, int Ok, int Warnings, int Critical, IReadOnlyList<ComplianceCheckDto> Checks, DateTime CheckedAt);

    public interface IComplianceService
    {
        Task<ComplianceDto> CheckAsync(Guid clientId, CancellationToken ct);
    }
}
