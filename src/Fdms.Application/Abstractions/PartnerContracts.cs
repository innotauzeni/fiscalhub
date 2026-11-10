namespace Fdms.Application.Abstractions
{
    // ---- operator side: managing partners -------------------------------------------------------------------------------

    public sealed record PartnerDto(Guid Id, string Name, string Code, string? ContactName, string? Email, string? Phone, decimal CommissionPercent, bool IsActive, string? Notes,
        int Clients, int Staff, IReadOnlyList<AmountDto> Earned, IReadOnlyList<AmountDto> Paid, IReadOnlyList<AmountDto> Owed, string Kind = "commission", decimal DiscountPercent = 0);
    /// <summary>Kind: commission (default) or reseller; DiscountPercent: a reseller's companies pay this much less than the list price.</summary>
    public sealed record SavePartnerRequest(Guid? Id, string Name, string? Code, string? ContactName, string? Email, string? Phone, decimal CommissionPercent, bool IsActive, string? Notes,
        string? Kind = null, decimal? DiscountPercent = null);
    public sealed record PartnerStaffDto(string Id, string Name, string Email, string Status, DateTime? LastLogin);
    public sealed record AddPartnerStaffRequest(string Email, string Name, string Surname, string Password);
    public sealed record PartnerClientLinkDto(Guid ClientId, string Name, Guid? PartnerId, string? PartnerName, decimal Percent);
    public sealed record SetClientPartnerRequest(Guid? PartnerId, decimal? Percent);
    public sealed record PartnerPayoutDto(Guid Id, DateTime Date, decimal Amount, string Currency, string? Reference, string? Notes, string? RecordedBy);
    public sealed record RecordPayoutRequest(DateTime Date, decimal Amount, string Currency, string? Reference, string? Notes);

    // ---- partner side: what the partner's own staff see ------------------------------------------------------------------

    public sealed record PartnerSummaryDto(string Name, string Code, decimal CommissionPercent, string ReferralPath, int Clients, int ActiveClients, int PendingReferrals,
        IReadOnlyList<AmountDto> Earned, IReadOnlyList<AmountDto> Paid, IReadOnlyList<AmountDto> Owed, string Kind = "commission", decimal DiscountPercent = 0);
    public sealed record PartnerClientDto(string Name, string Status, string? Plan, int Devices, int Receipts30Days, DateTime? LastReceiptAt, DateTime Since, decimal Percent, IReadOnlyList<AmountDto> Earned);
    public sealed record PartnerReferralDto(string CompanyName, string ContactName, string Status, DateTime CreatedAt);
    public sealed record CommissionLineDto(DateTime Date, string Company, string Invoice, string Currency, decimal InvoiceAmount, decimal Percent, decimal Commission);
    public sealed record PartnerStatementDto(string Partner, IReadOnlyList<CommissionLineDto> Lines, IReadOnlyList<PartnerPayoutDto> Payouts, IReadOnlyList<AmountDto> Earned, IReadOnlyList<AmountDto> Paid, IReadOnlyList<AmountDto> Owed);

    public interface IPartnerService
    {
        Task<IReadOnlyList<PartnerDto>> ListAsync(CancellationToken ct);
        Task<PartnerDto> SaveAsync(SavePartnerRequest req, CancellationToken ct);
        Task<IReadOnlyList<PartnerStaffDto>> StaffAsync(Guid partnerId, CancellationToken ct);
        Task<PartnerStaffDto> AddStaffAsync(Guid partnerId, AddPartnerStaffRequest req, CancellationToken ct);
        Task<IReadOnlyList<PartnerClientLinkDto>> ClientLinksAsync(CancellationToken ct);
        Task<PartnerClientLinkDto> SetClientPartnerAsync(Guid clientId, SetClientPartnerRequest req, CancellationToken ct);
        Task<PartnerPayoutDto> RecordPayoutAsync(Guid partnerId, RecordPayoutRequest req, CancellationToken ct);

        Task<PartnerSummaryDto> SummaryAsync(Guid partnerId, CancellationToken ct);
        Task<IReadOnlyList<PartnerClientDto>> ClientsAsync(Guid partnerId, CancellationToken ct);
        Task<IReadOnlyList<PartnerReferralDto>> ReferralsAsync(Guid partnerId, CancellationToken ct);
        Task<PartnerStatementDto> StatementAsync(Guid partnerId, CancellationToken ct);
    }

    // ---- documents ------------------------------------------------------------------------------------------------------

    public sealed record DocumentDto(Guid Id, string Title, string FileName, string ContentType, long SizeBytes, string Category, string LinkType, Guid? LinkId, string? LinkLabel, string? Notes,
        DateTime RetainUntil, bool CanDelete, string? UploadedBy, DateTime UploadedAt);
    public sealed record DocumentFilter(string? Q, string? Category, string? LinkType, Guid? LinkId, int Take = 200);
    public sealed record UploadDocumentRequest(string Title, string Category, string? LinkType, Guid? LinkId, string? Notes, DateTime? RetainUntil, string FileName, string? ContentType, byte[] Data);
    public sealed record UpdateDocumentRequest(string Title, string Category, string? Notes, DateTime? RetainUntil);
    public sealed record DocumentFile(string FileName, string ContentType, byte[] Data);
    public sealed record DocumentSummaryDto(int Files, long TotalBytes, long LimitBytes, int YearsKept);

    public interface IDocumentService
    {
        Task<IReadOnlyList<DocumentDto>> ListAsync(Guid clientId, DocumentFilter f, CancellationToken ct);
        Task<DocumentSummaryDto> SummaryAsync(Guid clientId, CancellationToken ct);
        Task<DocumentDto> UploadAsync(Guid clientId, UploadDocumentRequest req, CancellationToken ct);
        Task<DocumentDto> UpdateAsync(Guid clientId, Guid id, UpdateDocumentRequest req, CancellationToken ct);
        Task<DocumentFile> DownloadAsync(Guid clientId, Guid id, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
    }

    // ---- usage analytics (operator) -------------------------------------------------------------------------------------

    public sealed record UsageTotalsDto(int Companies, int ActiveCompanies, int OnTrial, int Locked, int AtRisk, int Devices, int Receipts, int Rejected, int Pending, decimal RejectedPercent, int ReceiptsPrevious);
    public sealed record UsageDayDto(DateTime Date, int Receipts, int Rejected);
    public sealed record UsageCompanyDto(Guid Id, string Name, string Status, string? Plan, string? Partner, int Devices, int Receipts, int ReceiptsPrevious, int Rejected, int Pending,
        DateTime? LastReceiptAt, DateTime? LastLoginAt, string Risk, IReadOnlyList<string> Reasons, DateTime CreatedAt);
    public sealed record UsagePlanDto(string Plan, int Companies, int Receipts);
    public sealed record PlatformUsageDto(int Days, DateTime From, DateTime To, UsageTotalsDto Totals, IReadOnlyList<UsageDayDto> Daily, IReadOnlyList<UsageCompanyDto> Companies, IReadOnlyList<UsagePlanDto> Plans);

    public interface IAnalyticsService
    {
        Task<PlatformUsageDto> UsageAsync(int days, CancellationToken ct);
    }
}
