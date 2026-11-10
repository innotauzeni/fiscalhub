namespace Fdms.Application.Abstractions
{
    // ---- sending an invoice to the customer -------------------------------------------------------------------------------------

    public sealed record SendInvoiceRequest(string? Email, string? Message);
    public sealed record ShareLinkDto(string Url, string Token, DateTime ExpiresAt, string WhatsAppText);
    /// <summary>Queued = the mail server was busy, so the email waits in the outbox and is retried automatically.</summary>
    public sealed record SendResultDto(bool Sent, bool EmailConfigured, string To, string Message, bool Queued = false);

    public sealed record OutboxDto(Guid Id, string To, string Subject, string Status, int Attempts, DateTime NextAttemptAt, string? LastError, DateTime CreatedAt, DateTime? SentAt);

    public interface IEmailOutbox
    {
        Task EnqueueAsync(Guid? clientId, string to, string subject, string html, IReadOnlyList<EmailAttachment> files, string? firstError, CancellationToken ct);
        Task<IReadOnlyList<OutboxDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task RetryNowAsync(Guid clientId, Guid id, CancellationToken ct);
        Task CancelAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>One pass: tries every waiting email that is due. Returns how many were delivered.</summary>
        Task<int> RunDueAsync(DateTime utcNow, CancellationToken ct);
    }
    public sealed record PublicInvoiceDto(string CompanyName, string? Slug, bool HasLogo, string Accent, string InvoiceNo, string Type, string Currency, decimal Total, DateTime Date,
        string Status, long? GlobalNo, int? FiscalDayNo, string? QrUrl, string StatusText);

    public interface IInvoiceShareService
    {
        /// <summary>A link anyone can open without signing in (the invoice page with QR and PDF). Signed and time-limited; it grants nothing but that one invoice.</summary>
        Task<ShareLinkDto> CreateLinkAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        /// <summary>Emails the fiscal invoice PDF and the link. Says plainly when email is not set up on the server.</summary>
        Task<SendResultDto> EmailAsync(Guid clientId, Guid receiptId, SendInvoiceRequest req, CancellationToken ct);
        Task<PublicInvoiceDto> ResolveAsync(string token, CancellationToken ct);
        Task<byte[]> PublicPdfAsync(string token, bool a4, CancellationToken ct);
        Task<byte[]> PublicQrAsync(string token, CancellationToken ct);
        Task<TemplateLogo?> PublicLogoAsync(string token, CancellationToken ct);
    }

    // ---- notifications and alerts -----------------------------------------------------------------------------------------------

    public sealed record NotificationDto(Guid Id, string Kind, string Severity, string Title, string? Body, string? Link, DateTime CreatedAt, bool Read);
    public sealed record NotificationList(int Unread, IReadOnlyList<NotificationDto> Items);
    public sealed record AlertKindInfo(string Id, string Label, string Help);
    public sealed record AlertSettingsDto(string? Emails, IReadOnlyList<string> EnabledKinds, IReadOnlyList<AlertKindInfo> Kinds, bool EmailConfigured, string FallbackNote);
    public sealed record SaveAlertSettingsRequest(string? Emails, IReadOnlyList<string>? EnabledKinds);

    public interface INotificationService
    {
        Task<NotificationList> ListAsync(Guid clientId, bool unreadOnly, int take, CancellationToken ct);
        Task MarkReadAsync(Guid clientId, IReadOnlyList<Guid>? ids, CancellationToken ct);
        Task<AlertSettingsDto> GetSettingsAsync(Guid clientId, CancellationToken ct);
        Task<AlertSettingsDto> SaveSettingsAsync(Guid clientId, SaveAlertSettingsRequest req, CancellationToken ct);
        /// <summary>Records the alert once per key and emails it. Returns false when it was already raised, or the company switched that kind off.</summary>
        Task<bool> RaiseAsync(Guid clientId, string kind, string severity, string key, string title, string? body, string? link, CancellationToken ct, bool email = true);
        /// <summary>An alert for the platform operator (new registration, payment to confirm, go-live request ...): shown in the operator's bell and emailed to the operator's alert addresses. Once per key.</summary>
        Task<bool> RaisePlatformAsync(string kind, string severity, string key, string title, string? body, string? link, CancellationToken ct);
        /// <summary>Looks at every company for things that need attention and raises alerts. Returns how many new alerts were raised.</summary>
        Task<int> ScanAsync(DateTime utcNow, CancellationToken ct);
    }

    // ---- device health ------------------------------------------------------------------------------------------------------------

    /// <summary>Status: ok, warn or bad. FixAction is one of refresh-config, renew-certificate, ping, retry-pending, close-day, open-fiscal-days, offline-files (the console turns it into a button).</summary>
    public sealed record HealthCheckDto(string Id, string Title, string Status, string Detail, string? FixAction = null, string? FixLabel = null);
    public sealed record DeviceHealthDto(Guid DeviceId, int ZimraDeviceId, string? Name, string Status, IReadOnlyList<HealthCheckDto> Checks);

    public interface IHealthService
    {
        /// <summary>live = also ask ZIMRA whether it can be reached (a real call, so it is optional).</summary>
        Task<IReadOnlyList<DeviceHealthDto>> CheckAsync(Guid clientId, Guid? deviceId, bool live, CancellationToken ct);
    }

    // ---- plan usage -----------------------------------------------------------------------------------------------------------------

    /// <summary>Limit 0 = unlimited. Level: ok, warn (80% or more) or over (100% or more).</summary>
    public sealed record UsageItemDto(string Key, string Label, int Used, int Limit, int Percent, string Level);
    public sealed record UsageMeterDto(string? Plan, string PeriodStart, string PeriodEnd, bool Limited, IReadOnlyList<UsageItemDto> Items);

    public interface IUsageService
    {
        Task<UsageMeterDto> GetAsync(Guid clientId, CancellationToken ct);
    }

    // ---- quotes ---------------------------------------------------------------------------------------------------------------------

    public sealed record QuoteLineDto(string Name, string? HsCode, decimal Quantity, decimal UnitPrice, decimal? TaxPercent, bool IsExempt = false);
    public sealed record QuoteDto(Guid Id, string Number, string CustomerName, string? CustomerTin, string? CustomerVat, string? CustomerEmail, string? CustomerPhone, string Currency, bool TaxInclusive,
        DateTime? ValidUntil, string? Notes, string Status, IReadOnlyList<QuoteLineDto> Lines, decimal Total, Guid? InvoiceReceiptId, DateTime CreatedAt);
    public sealed record SaveQuoteRequest(Guid? Id, string? Number, string CustomerName, string? CustomerTin, string? CustomerVat, string? CustomerEmail, string? CustomerPhone, string Currency,
        bool TaxInclusive, DateTime? ValidUntil, string? Notes, IReadOnlyList<QuoteLineDto> Lines);
    public sealed record QuoteStatusRequest(string Status);
    public sealed record ConvertQuoteRequest(Guid DeviceId, string? InvoiceNo, string? PaymentMethod);

    public interface IQuoteService
    {
        Task<IReadOnlyList<QuoteDto>> ListAsync(Guid clientId, string? status, string? search, CancellationToken ct);
        Task<QuoteDto> GetAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<QuoteDto> SaveAsync(Guid clientId, SaveQuoteRequest req, CancellationToken ct);
        Task<QuoteDto> SetStatusAsync(Guid clientId, Guid id, string status, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Fiscalises the quote as an invoice on the chosen device (once) and marks the quote Invoiced.</summary>
        Task<ReceiptDto> ConvertAsync(Guid clientId, Guid id, ConvertQuoteRequest req, CancellationToken ct);
        Task<byte[]> PdfAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<SendResultDto> EmailAsync(Guid clientId, Guid id, string? to, string? message, CancellationToken ct);
    }

    // ---- recurring invoices ---------------------------------------------------------------------------------------------------------

    public sealed record RecurringRunDto(Guid Id, DateTime RunDate, Guid? ReceiptId, string? InvoiceNo, string Status, string? Error, DateTime CreatedAt);
    public sealed record RecurringDto(Guid Id, Guid DeviceId, int ZimraDeviceId, string Name, string CustomerName, string? CustomerTin, string? CustomerVat, string? CustomerEmail, string? CustomerPhone,
        string Currency, bool TaxInclusive, string PaymentMethod, string? Notes, IReadOnlyList<QuoteLineDto> Lines, string Frequency, DateTime NextRunDate, DateTime? EndDate, bool IsActive,
        string Prefix, int NextNumber, bool EmailCustomer, decimal Total, IReadOnlyList<RecurringRunDto> Recent);
    public sealed record SaveRecurringRequest(Guid? Id, Guid DeviceId, string Name, string CustomerName, string? CustomerTin, string? CustomerVat, string? CustomerEmail, string? CustomerPhone,
        string Currency, bool TaxInclusive, string PaymentMethod, string? Notes, IReadOnlyList<QuoteLineDto> Lines, string Frequency, DateTime NextRunDate, DateTime? EndDate, string? Prefix, bool EmailCustomer);

    public interface IRecurringService
    {
        Task<IReadOnlyList<RecurringDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<RecurringDto> SaveAsync(Guid clientId, SaveRecurringRequest req, CancellationToken ct);
        Task<RecurringDto> SetActiveAsync(Guid clientId, Guid id, bool active, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Issues the next due invoice now (for the schedule's next date) and moves the schedule on.</summary>
        Task<RecurringDto> RunNowAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>The scheduler's pass: issues everything due on or before <paramref name="catToday"/>. Returns how many invoices were issued.</summary>
        Task<int> RunDueAsync(DateTime catToday, CancellationToken ct);
    }

    // ---- VAT summary export ---------------------------------------------------------------------------------------------------------

    public sealed record VatLineDto(string Label, decimal? TaxPercent, decimal SalesInclusive, decimal Vat, decimal SalesExclusive);
    public sealed record VatCurrencyDto(string Currency, IReadOnlyList<VatLineDto> Lines, decimal TotalSalesInclusive, decimal TotalVat, decimal TotalSalesExclusive,
        int Invoices, decimal InvoicesTotal, int CreditNotes, decimal CreditNotesTotal, int DebitNotes, decimal DebitNotesTotal);
    public sealed record VatReturnDto(string TaxpayerName, string? Tin, string? VatNumber, string From, string To, string DeviceLabel, bool Truncated, int Pending, int OfflineIssued,
        IReadOnlyList<VatCurrencyDto> Currencies, DateTime GeneratedAt);

    public interface IVatReturnService
    {
        Task<VatReturnDto> BuildAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct);
        Task<byte[]> XlsxAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct);
        Task<byte[]> PdfAsync(Guid clientId, DateTime from, DateTime to, Guid? deviceId, CancellationToken ct);
    }

    // ---- suggestions while typing an invoice ----------------------------------------------------------------------------------------

    public sealed record ItemSuggestionDto(string Name, string? HsCode, decimal UnitPrice, string Currency, decimal? TaxPercent, bool IsExempt, string Source);

    public interface ISuggestService
    {
        Task<IReadOnlyList<ItemSuggestionDto>> ItemsAsync(Guid clientId, string? q, int take, CancellationToken ct);
    }

    // ---- company branding (sign-in page and sidebar) --------------------------------------------------------------------------------

    public sealed record BrandDto(string Name, string? Slug, string Accent, bool HasLogo);
    public sealed record CompanyBrandDto(string? Slug, string Accent, bool HasLogo, string? SignInUrl);
    public sealed record SaveBrandRequest(string? Slug);

    public interface IBrandService
    {
        Task<BrandDto?> PublicAsync(string slug, CancellationToken ct);
        Task<TemplateLogo?> PublicLogoAsync(string slug, CancellationToken ct);
        Task<CompanyBrandDto> GetAsync(Guid clientId, CancellationToken ct);
        Task<CompanyBrandDto> SaveAsync(Guid clientId, SaveBrandRequest req, CancellationToken ct);
        Task<TemplateLogo?> LogoAsync(Guid clientId, CancellationToken ct);
        /// <summary>A short unique word for a new company, from its name.</summary>
        Task<string> NewSlugAsync(string name, CancellationToken ct);
    }

    // ---- getting started and platform attention ------------------------------------------------------------------------------------

    public sealed record ChecklistStepDto(string Id, string Title, string Help, bool Done, string Route, string Action);
    public sealed record ChecklistDto(int Done, int Total, IReadOnlyList<ChecklistStepDto> Steps);
    public sealed record AttentionItemDto(string Kind, string Severity, Guid ClientId, string ClientName, string Title, string Detail, string? Route);
}
