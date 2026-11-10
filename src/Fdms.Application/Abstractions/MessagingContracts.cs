namespace Fdms.Application.Abstractions
{
    // =================================================================================================================
    // A company's own WhatsApp number, and SMS (the platform's provider account and each company's own).
    // =================================================================================================================

    // ---- a company's WhatsApp number ----
    public sealed record CompanyWhatsAppDto(bool Enabled, string? PhoneNumberId, string? BusinessAccountId, string? DisplayNumber, bool HasAccessToken, bool HasAppSecret,
        string VerifyToken, string WebhookUrl, string? AlertTemplate, string AlertTemplateLanguage, string ApiVersion, bool CustomersEnabled, string? CustomerGreeting,
        bool Configured, bool PlatformNumberAvailable, string? PlatformNumber);
    public sealed record SaveCompanyWhatsAppRequest(bool Enabled, string? PhoneNumberId, string? BusinessAccountId, string? DisplayNumber, string? AccessToken, string? AppSecret,
        string? AlertTemplate, string? AlertTemplateLanguage, string? ApiVersion, bool RotateVerifyToken, bool CustomersEnabled, string? CustomerGreeting);

    // ---- SMS ----
    /// <summary>What sending needs (secrets in clear, never returned to a browser).</summary>
    public sealed record SmsConfig(Guid AccountId, string Provider, string? ApiUrl, string? Username, string? ApiKey, string? ApiSecret, string? SenderId);
    /// <summary>Temporary: worth trying again later (the provider was unreachable or busy).</summary>
    public sealed record SmsSendResult(bool Ok, string? MessageId, string? Error, bool Temporary = false)
    {
        public static SmsSendResult Sent(string? id) => new(true, id, null);
        public static SmsSendResult Fail(string error, bool temporary = false) => new(false, null, error, temporary);
    }

    /// <summary>The SMS providers' HTTP APIs, kept small so tests can stand in for them.</summary>
    public interface ISmsApi
    {
        /// <summary>Sends one SMS. <paramref name="to"/> is digits only with the country code (263771234567).</summary>
        Task<SmsSendResult> SendAsync(SmsConfig c, string to, string text, CancellationToken ct);
    }

    /// <summary>A provider the settings page offers: which fields it needs and a line of help.</summary>
    public sealed record SmsProviderDto(string Key, string Label, IReadOnlyList<string> Fields, string? DefaultUrl, string Help);

    public sealed record SmsSettingsDto(bool Enabled, string Provider, string? ApiUrl, string? Username, bool HasApiKey, bool HasApiSecret, string? SenderId, bool AllowCompanies,
        bool Configured, IReadOnlyList<SmsProviderDto> Providers, bool PlatformAvailable);
    public sealed record SaveSmsSettingsRequest(bool Enabled, string Provider, string? ApiUrl, string? Username, string? ApiKey, string? ApiSecret, string? SenderId, bool AllowCompanies);
    public sealed record SmsTestRequest(string To);
    public sealed record SendInvoiceSmsRequest(string? Phone, string? Message);
    public sealed record SmsResultDto(bool Sent, bool Configured, string To, string Message);
    public sealed record SmsLogRow(Guid Id, string To, string Kind, string Status, string Body, string? Error, int Attempts, DateTime CreatedAt, DateTime? SentAt);

    /// <summary>A person's SMS alerts: Available = an SMS account can reach them; Phone is the number on their profile.</summary>
    public sealed record SmsPrefsDto(bool Available, string? Phone, bool Alerts);
    public sealed record SaveSmsPrefsRequest(bool Alerts);

    /// <summary>Sends one SMS straight away through the right account (the company's own, else the platform's when it allows companies) and logs it. Never throws.</summary>
    public interface ISmsDispatcher
    {
        /// <summary>True when an account would send for this company (null = the platform).</summary>
        Task<bool> AvailableAsync(Guid? clientId, CancellationToken ct);
        /// <summary>Sends now. <paramref name="logBody"/> is what the log keeps (e.g. a code masked). Returns the result, or null when no account is set up.</summary>
        Task<SmsSendResult?> SendNowAsync(Guid? clientId, string phone, string text, string kind, string? userId, string? logBody, CancellationToken ct);
    }

    public interface ISmsService
    {
        Task<SmsSettingsDto> PlatformAsync(CancellationToken ct);
        Task<SmsSettingsDto> SavePlatformAsync(SaveSmsSettingsRequest req, CancellationToken ct);
        Task<SmsSettingsDto> CompanyAsync(Guid clientId, CancellationToken ct);
        Task<SmsSettingsDto> SaveCompanyAsync(Guid clientId, SaveSmsSettingsRequest req, CancellationToken ct);
        /// <summary>Sends a test SMS through the company's account (or the platform's, for null).</summary>
        Task<SmsResultDto> TestAsync(Guid? clientId, string to, CancellationToken ct);
        Task<IReadOnlyList<SmsLogRow>> LogAsync(Guid? clientId, int take, CancellationToken ct);

        /// <summary>Texts the fiscal invoice (number, total and the link to view and verify it) to the customer: the given phone, else the buyer's phone on the invoice.</summary>
        Task<SmsResultDto> SendInvoiceAsync(Guid clientId, Guid receiptId, SendInvoiceSmsRequest req, CancellationToken ct);

        Task<SmsPrefsDto> MyPrefsAsync(string userId, CancellationToken ct);
        Task<SmsPrefsDto> SavePrefsAsync(string userId, SaveSmsPrefsRequest req, CancellationToken ct);

        /// <summary>Sends queued alerts (retried with back-off). Returns how many were tried.</summary>
        Task<int> ProcessPendingAsync(CancellationToken ct);
    }
}
