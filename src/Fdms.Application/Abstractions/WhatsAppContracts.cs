namespace Fdms.Application.Abstractions
{
    // =================================================================================================================
    // WhatsApp (Meta Cloud API): one FiscalHub number; company staff link their own WhatsApp number to their user and
    // then issue fiscal invoices, check the day and get PDFs by chatting. Alerts can also go out by WhatsApp.
    // =================================================================================================================

    /// <summary>What the gateway needs to talk to the Cloud API.</summary>
    public sealed record WhatsAppConfig(string PhoneNumberId, string AccessToken, string ApiVersion);

    public sealed record WhatsAppButton(string Id, string Title);
    public sealed record WhatsAppListRow(string Id, string Title, string? Description = null);

    /// <summary>Meta's Cloud API, kept small so tests can stand in for it.</summary>
    public interface IWhatsAppApi
    {
        Task<string?> SendTextAsync(WhatsAppConfig c, string to, string text, CancellationToken ct);
        /// <summary>Up to 3 reply buttons (titles up to 20 characters).</summary>
        Task<string?> SendButtonsAsync(WhatsAppConfig c, string to, string body, IReadOnlyList<WhatsAppButton> buttons, CancellationToken ct);
        /// <summary>A list of up to 10 rows (titles up to 24 characters, descriptions up to 72).</summary>
        Task<string?> SendListAsync(WhatsAppConfig c, string to, string body, string button, IReadOnlyList<WhatsAppListRow> rows, CancellationToken ct);
        Task<string?> SendDocumentAsync(WhatsAppConfig c, string to, byte[] file, string mimeType, string fileName, string? caption, CancellationToken ct);
        /// <summary>An approved message template (needed to write first, or more than 24 hours after the person's last message).</summary>
        Task<string?> SendTemplateAsync(WhatsAppConfig c, string to, string template, string language, IReadOnlyList<string> bodyParameters, CancellationToken ct);
        Task MarkReadAsync(WhatsAppConfig c, string messageId, CancellationToken ct);
    }

    public sealed class WhatsAppException : Exception
    {
        public WhatsAppException(string message, int status = 0) : base(message) => Status = status;
        public int Status { get; }
    }

    // ---- the platform's settings ----
    public sealed record WhatsAppSettingsDto(bool Enabled, string? PhoneNumberId, string? BusinessAccountId, string? DisplayNumber, bool HasAccessToken, bool HasAppSecret,
        string VerifyToken, string WebhookUrl, string? AlertTemplate, string AlertTemplateLanguage, string ApiVersion, bool Configured);
    public sealed record SaveWhatsAppSettingsRequest(bool Enabled, string? PhoneNumberId, string? BusinessAccountId, string? DisplayNumber, string? AccessToken, string? AppSecret,
        string? AlertTemplate, string? AlertTemplateLanguage, string? ApiVersion, bool RotateVerifyToken);
    public sealed record WhatsAppTestRequest(string To);

    // ---- a person's link ----
    /// <summary>WaLink opens WhatsApp with the code typed in, ready to send to the FiscalHub number.</summary>
    public sealed record WhatsAppLinkDto(bool Available, string? BotNumber, string? LinkedNumber, DateTime? LinkedAt, bool Alerts, Guid? DeviceId,
        string? Code, DateTime? CodeExpiresAt, string? WaLink);
    public sealed record SaveWhatsAppPrefsRequest(bool Alerts, Guid? DeviceId);

    public interface IWhatsAppService
    {
        Task<WhatsAppSettingsDto> SettingsAsync(CancellationToken ct);
        Task<WhatsAppSettingsDto> SaveSettingsAsync(SaveWhatsAppSettingsRequest req, CancellationToken ct);
        Task<string> TestAsync(string to, CancellationToken ct);

        Task<WhatsAppLinkDto> MyLinkAsync(string userId, CancellationToken ct);
        /// <summary>A fresh one-time code (10 minutes) the person sends to the bot from their WhatsApp.</summary>
        Task<WhatsAppLinkDto> StartLinkAsync(string userId, CancellationToken ct);
        Task<WhatsAppLinkDto> SavePrefsAsync(string userId, SaveWhatsAppPrefsRequest req, CancellationToken ct);
        Task<WhatsAppLinkDto> UnlinkAsync(string userId, CancellationToken ct);

        /// <summary>Meta's webhook check: the challenge to echo back, or null when the token is wrong.</summary>
        Task<string?> VerifyWebhookAsync(string? mode, string? token, string? challenge, CancellationToken ct);
        /// <summary>Stores the messages in a webhook call. False when the signature does not match (the call did not come from Meta).</summary>
        Task<bool> ReceiveAsync(string rawBody, string? signature, CancellationToken ct);
        /// <summary>Answers stored messages and sends waiting alerts. Returns how many were handled.</summary>
        Task<int> ProcessPendingAsync(CancellationToken ct);

        // ---- a company's own number ----
        Task<CompanyWhatsAppDto> CompanyAsync(Guid clientId, CancellationToken ct);
        Task<CompanyWhatsAppDto> SaveCompanyAsync(Guid clientId, SaveCompanyWhatsAppRequest req, CancellationToken ct);
        /// <summary>Forgets the company's number and credentials; its staff go back to the platform's number.</summary>
        Task<CompanyWhatsAppDto> RemoveCompanyAsync(Guid clientId, CancellationToken ct);
        Task<string> TestCompanyAsync(Guid clientId, string to, CancellationToken ct);
        /// <summary>Meta's check of a company number's webhook (its own verify token).</summary>
        Task<string?> VerifyAccountWebhookAsync(Guid accountId, string? mode, string? token, string? challenge, CancellationToken ct);
        /// <summary>Messages to a company's number, signed with that company's app secret.</summary>
        Task<bool> ReceiveAccountAsync(Guid accountId, string rawBody, string? signature, CancellationToken ct);
    }
}
