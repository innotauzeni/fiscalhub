using System.Text.Json;

namespace Fdms.Application.Abstractions
{
    public sealed record PasskeyDto(Guid Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt);
    public sealed record SecurityStatusDto(bool MfaEnabled, int RecoveryCodesLeft, IReadOnlyList<PasskeyDto> Passkeys);

    /// <summary>What an authenticator app needs: the secret to type in, the otpauth link and a ready QR picture (a PNG data address).</summary>
    public sealed record MfaSetupDto(string Secret, string Uri, string QrImage);
    public sealed record MfaCodeRequest(string Code);
    public sealed record MfaEnabledDto(IReadOnlyList<string> RecoveryCodes);
    public sealed record PasswordRequest(string Password);
    public sealed record MfaLoginRequest(string MfaToken, string Code);

    public sealed record PasskeyNameRequest(string Name);
    public sealed record PasskeyFinishRequest(string? Name, JsonElement Credential);
    public sealed record PasskeyBeginDto(string SessionId, JsonElement Options);
    public sealed record PasskeyLoginRequest(string SessionId, JsonElement Credential);

    /// <summary>Where the browser says the page is. A passkey is bound to this host, so it must come from the request, not from the client body.</summary>
    public sealed record PasskeyOrigin(string RpId, string Origin);

    public sealed record PolicyUserDto(string Id, string Name, string Email, bool MfaEnabled, int Passkeys, bool Compliant);
    public sealed record SecurityPolicyDto(bool RequireMfa, IReadOnlyList<PolicyUserDto> Users);
    public sealed record SetPolicyRequest(bool RequireMfa);

    public interface ISecurityService
    {
        Task<SecurityPolicyDto> GetPolicyAsync(Guid clientId, CancellationToken ct);
        Task<SecurityPolicyDto> SetPolicyAsync(Guid clientId, bool requireMfa, CancellationToken ct);
        Task<SecurityStatusDto> StatusAsync(string userId, CancellationToken ct);
        Task<MfaSetupDto> BeginMfaAsync(string userId, CancellationToken ct);
        Task<MfaEnabledDto> EnableMfaAsync(string userId, string code, CancellationToken ct);
        Task DisableMfaAsync(string userId, string password, CancellationToken ct);
        Task<MfaEnabledDto> NewRecoveryCodesAsync(string userId, string password, CancellationToken ct);
        /// <summary>For a company administrator whose colleague lost their phone: switches MFA off, drops passkeys and ends their sessions.</summary>
        Task ResetForUserAsync(Guid? callerClientId, string userId, CancellationToken ct);

        Task<PasskeyBeginDto> BeginPasskeyRegistrationAsync(string userId, PasskeyOrigin where, CancellationToken ct);
        Task<PasskeyDto> FinishPasskeyRegistrationAsync(string userId, string sessionId, PasskeyFinishRequest req, PasskeyOrigin where, CancellationToken ct);
        Task DeletePasskeyAsync(string userId, Guid id, string password, CancellationToken ct);
        Task<PasskeyDto> RenamePasskeyAsync(string userId, Guid id, string name, CancellationToken ct);

        Task<PasskeyBeginDto> BeginPasskeyLoginAsync(PasskeyOrigin where, CancellationToken ct);
        /// <summary>Checks the signed challenge and returns the user id it belongs to.</summary>
        Task<string> FinishPasskeyLoginAsync(PasskeyLoginRequest req, PasskeyOrigin where, CancellationToken ct);
    }
}
