namespace Fdms.Application.Abstractions
{
    /// <summary>
    /// The mail server, as the operator sets it in Settings (the password is never sent back). Source: database (set in Settings), config (appsettings.json) or none.
    /// NotifyEmails: where the platform's own alerts go (new registrations, payments to confirm, go-live requests).
    /// </summary>
    public sealed record EmailSettingsDto(string Source, bool Enabled, string? Host, int Port, bool EnableSsl, string? User, bool HasPassword, string? FromAddress, string? FromName, string? NotifyEmails, bool Configured);
    public sealed record SaveEmailSettingsRequest(bool Enabled, string? Host, int Port, bool EnableSsl, string? User, string? Password, bool ClearPassword, string? FromAddress, string? FromName, string? NotifyEmails);
    public sealed record TestEmailRequest(string To);
    public sealed record TestEmailResult(bool Ok, string Message);

    public interface IEmailSettingsService
    {
        Task<EmailSettingsDto> GetAsync(CancellationToken ct);
        Task<EmailSettingsDto> SaveAsync(SaveEmailSettingsRequest req, CancellationToken ct);
        Task<TestEmailResult> TestAsync(string to, CancellationToken ct);
        /// <summary>The addresses that get the platform's own alerts: the ones set in Settings, else Email:AdminNotify, else every active platform administrator.</summary>
        Task<IReadOnlyList<string>> OperatorEmailsAsync(CancellationToken ct);
    }
}
