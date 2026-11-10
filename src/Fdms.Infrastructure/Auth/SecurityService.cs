using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using QRCoder;

namespace Fdms.Infrastructure.Auth
{
    /// <summary>
    /// The second ways to prove who you are: an authenticator app (time-based 6-digit codes, with one-time recovery codes) and passkeys (WebAuthn).
    /// The authenticator secret and recovery codes are kept by ASP.NET Identity; passkeys are stored in fdms_passkeys. A passkey is always checked with
    /// the device's own fingerprint/face/PIN ("user verification"), so signing in with one counts as both factors and skips the code.
    /// </summary>
    public sealed class SecurityService : ISecurityService
    {
        private readonly UserManager<ApplicationUser> _users;
        private readonly FdmsDbContext _db;
        private readonly IMemoryCache _cache;
        public SecurityService(UserManager<ApplicationUser> users, FdmsDbContext db, IMemoryCache cache) { _users = users; _db = db; _cache = cache; }

        private async Task<ApplicationUser> UserAsync(string userId) => await _users.FindByIdAsync(userId) ?? throw GatewayException.NotFound("User");

        private static PasskeyDto ToDto(Passkey p) => new(p.Id, p.Name, p.CreatedAt, p.LastUsedAt);

        private async Task AuditAsync(ApplicationUser u, string action, string detail, CancellationToken ct)
        {
            _db.AuditLogs.Add(new AuditLog { ClientId = u.ClientId, UserId = u.Id, UserName = u.Email, ActorType = "user", Action = action, Detail = detail, Entity = "User", EntityId = u.Id });
            await _db.SaveChangesAsync(ct);
        }

        private async Task RequirePasswordAsync(ApplicationUser u, string password)
        {
            if (string.IsNullOrEmpty(password) || !await _users.CheckPasswordAsync(u, password))
                throw GatewayException.BadRequest("PASSWORD_WRONG", "That password is not correct.");
        }

        public async Task<SecurityStatusDto> StatusAsync(string userId, CancellationToken ct)
        {
            var u = await UserAsync(userId);
            var left = u.TwoFactorEnabled ? await _users.CountRecoveryCodesAsync(u) : 0;
            var keys = await _db.Passkeys.AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.CreatedAt).ToListAsync(ct);
            return new SecurityStatusDto(u.TwoFactorEnabled, left, keys.Select(ToDto).ToList());
        }

        // ---- company policy ----------------------------------------------------------------------------------------------

        public async Task<SecurityPolicyDto> GetPolicyAsync(Guid clientId, CancellationToken ct)
        {
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Company");
            var people = await _db.Users.AsNoTracking().Where(u => u.ClientId == clientId && u.Status == "ACTIVE").OrderBy(u => u.Name).ThenBy(u => u.Surname).ToListAsync(ct);
            var ids = people.Select(p => p.Id).ToList();
            var keys = (await _db.Passkeys.AsNoTracking().Where(p => ids.Contains(p.UserId)).Select(p => p.UserId).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            return new SecurityPolicyDto(client.RequireMfa, people.Select(u => new PolicyUserDto(u.Id, (u.Name + " " + u.Surname).Trim(), u.Email ?? u.UserName ?? "", u.TwoFactorEnabled, keys.GetValueOrDefault(u.Id), u.TwoFactorEnabled || keys.ContainsKey(u.Id))).ToList());
        }

        public async Task<SecurityPolicyDto> SetPolicyAsync(Guid clientId, bool requireMfa, CancellationToken ct)
        {
            var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Company");
            if (client.RequireMfa != requireMfa)
            {
                client.RequireMfa = requireMfa;
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = requireMfa ? "company.mfa-required" : "company.mfa-optional", Detail = requireMfa ? "everyone must use two-step sign-in or a passkey" : "extra protection is optional again" });
                await _db.SaveChangesAsync(ct);
            }
            return await GetPolicyAsync(clientId, ct);
        }

        // ---- authenticator app ----------------------------------------------------------------------------------------------

        public async Task<MfaSetupDto> BeginMfaAsync(string userId, CancellationToken ct)
        {
            var u = await UserAsync(userId);
            if (u.TwoFactorEnabled) throw GatewayException.Conflict("MFA_ALREADY_ON", "Two-step sign-in is already switched on. Switch it off first to set it up again.");
            await _users.ResetAuthenticatorKeyAsync(u);
            var key = await _users.GetAuthenticatorKeyAsync(u) ?? throw new InvalidOperationException("No authenticator key.");
            var uri = $"otpauth://totp/{Uri.EscapeDataString("FDMS Gateway")}:{Uri.EscapeDataString(u.Email ?? u.UserName ?? "user")}?secret={key}&issuer={Uri.EscapeDataString("FDMS Gateway")}&digits=6";
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
            var png = "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8));
            return new MfaSetupDto(Spaced(key), uri, png);
        }

        private static string Spaced(string key) => string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));

        public async Task<MfaEnabledDto> EnableMfaAsync(string userId, string code, CancellationToken ct)
        {
            var u = await UserAsync(userId);
            if (u.TwoFactorEnabled) throw GatewayException.Conflict("MFA_ALREADY_ON", "Two-step sign-in is already switched on.");
            if (await _users.GetAuthenticatorKeyAsync(u) == null) throw GatewayException.BadRequest("MFA_NOT_STARTED", "Start the set-up first, then enter the code your app shows.");
            if (!await VerifyAsync(u, code)) throw GatewayException.BadRequest("MFA_CODE_WRONG", "That code is not right. Check the 6 digits in your app (they change every 30 seconds) and try again.");
            await _users.SetTwoFactorEnabledAsync(u, true);
            var codes = (await _users.GenerateNewTwoFactorRecoveryCodesAsync(u, 10))!.ToList();
            await AuditAsync(u, "auth.mfa-enabled", "switched on two-step sign-in", ct);
            return new MfaEnabledDto(codes);
        }

        public async Task DisableMfaAsync(string userId, string password, CancellationToken ct)
        {
            var u = await UserAsync(userId);
            await RequirePasswordAsync(u, password);
            await _users.SetTwoFactorEnabledAsync(u, false);
            await _users.ResetAuthenticatorKeyAsync(u);
            await AuditAsync(u, "auth.mfa-disabled", "switched off two-step sign-in", ct);
        }

        public async Task<MfaEnabledDto> NewRecoveryCodesAsync(string userId, string password, CancellationToken ct)
        {
            var u = await UserAsync(userId);
            if (!u.TwoFactorEnabled) throw GatewayException.Conflict("MFA_OFF", "Switch on two-step sign-in first.");
            await RequirePasswordAsync(u, password);
            var codes = (await _users.GenerateNewTwoFactorRecoveryCodesAsync(u, 10))!.ToList();
            await AuditAsync(u, "auth.mfa-recovery-codes", "made new recovery codes", ct);
            return new MfaEnabledDto(codes);
        }

        /// <summary>True for a valid app code (6 digits, spaces allowed).</summary>
        public async Task<bool> VerifyAsync(ApplicationUser u, string? code)
        {
            var c = (code ?? "").Replace(" ", "").Replace("-", "");
            if (c.Length != 6 || !c.All(char.IsDigit)) return false;
            return await _users.VerifyTwoFactorTokenAsync(u, _users.Options.Tokens.AuthenticatorTokenProvider, c);
        }

        public async Task ResetForUserAsync(Guid? callerClientId, string userId, CancellationToken ct)
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId && (callerClientId == null || x.ClientId == callerClientId), ct) ?? throw GatewayException.NotFound("User");
            await _users.SetTwoFactorEnabledAsync(u, false);
            await _users.ResetAuthenticatorKeyAsync(u);
            _db.Passkeys.RemoveRange(_db.Passkeys.Where(p => p.UserId == u.Id));
            foreach (var t in await _db.RefreshTokens.Where(r => r.UserId == u.Id && !r.IsRevoked).ToListAsync(ct)) t.IsRevoked = true;
            _db.AuditLogs.Add(new AuditLog { ClientId = u.ClientId, Action = "user.reset-mfa", Detail = u.Email, Entity = "User", EntityId = u.Id });
            await _db.SaveChangesAsync(ct);
        }

        // ---- passkeys -------------------------------------------------------------------------------------------------------

        private static IFido2 Fido(PasskeyOrigin where) => new Fido2(new Fido2Configuration
        {
            ServerDomain = where.RpId, ServerName = "FDMS Gateway", Origins = new HashSet<string> { where.Origin },
        });

        private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static byte[] FromB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }

        private void Put(string key, object value) => _cache.Set(key, value, TimeSpan.FromMinutes(5));

        private T Take<T>(string key) where T : class
        {
            if (!_cache.TryGetValue(key, out var v) || v is not T t) throw GatewayException.BadRequest("PASSKEY_EXPIRED", "That took too long. Start again.");
            _cache.Remove(key);
            return t;
        }

        private static JsonElement Json(string raw) { using var d = JsonDocument.Parse(raw); return d.RootElement.Clone(); }

        public async Task<PasskeyBeginDto> BeginPasskeyRegistrationAsync(string userId, PasskeyOrigin where, CancellationToken ct)
        {
            var u = await UserAsync(userId);
            if (await _db.Passkeys.CountAsync(p => p.UserId == userId, ct) >= 10) throw GatewayException.Conflict("PASSKEY_LIMIT", "You can have up to 10 passkeys. Remove one first.");
            var existing = await _db.Passkeys.AsNoTracking().Where(p => p.UserId == userId).Select(p => p.CredentialId).ToListAsync(ct);
            var options = Fido(where).RequestNewCredential(new RequestNewCredentialParams
            {
                User = new Fido2User { Id = Encoding.UTF8.GetBytes(u.Id), Name = u.Email ?? u.UserName ?? u.Id, DisplayName = (u.Name + " " + u.Surname).Trim() },
                ExcludeCredentials = existing.Select(c => new PublicKeyCredentialDescriptor(FromB64(c))).ToList(),
                AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Required },
                AttestationPreference = AttestationConveyancePreference.None,
            });
            var session = Guid.NewGuid().ToString("N");
            Put("pk-reg:" + userId + ":" + session, options);
            return new PasskeyBeginDto(session, Json(options.ToJson()));
        }

        public async Task<PasskeyDto> FinishPasskeyRegistrationAsync(string userId, string sessionId, PasskeyFinishRequest req, PasskeyOrigin where, CancellationToken ct)
        {
            var u = await UserAsync(userId);
            var options = Take<CredentialCreateOptions>("pk-reg:" + userId + ":" + sessionId);
            AuthenticatorAttestationRawResponse? raw;
            try { raw = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(req.Credential.GetRawText()); }
            catch (JsonException) { throw GatewayException.BadRequest("PASSKEY_INVALID", "The passkey could not be read."); }
            if (raw == null) throw GatewayException.BadRequest("PASSKEY_INVALID", "The passkey could not be read.");
            RegisteredPublicKeyCredential cred;
            try
            {
                cred = await Fido(where).MakeNewCredentialAsync(new MakeNewCredentialParams
                {
                    AttestationResponse = raw, OriginalOptions = options,
                    IsCredentialIdUniqueToUserCallback = async (a, c) => { var id = B64(a.CredentialId); return !await _db.Passkeys.AnyAsync(p => p.CredentialId == id, c); },
                }, ct);
            }
            catch (Fido2VerificationException e) { throw GatewayException.BadRequest("PASSKEY_REJECTED", "The passkey was not accepted: " + e.Message); }
            var name = string.IsNullOrWhiteSpace(req.Name) ? "Passkey" : req.Name.Trim();
            if (name.Length > 80) name = name[..80];
            var row = new Passkey { UserId = userId, CredentialId = B64(cred.Id), PublicKey = cred.PublicKey, SignCount = cred.SignCount, Name = name };
            _db.Passkeys.Add(row);
            await _db.SaveChangesAsync(ct);
            await AuditAsync(u, "auth.passkey-added", name, ct);
            return ToDto(row);
        }

        public async Task DeletePasskeyAsync(string userId, Guid id, string password, CancellationToken ct)
        {
            var u = await UserAsync(userId);
            await RequirePasswordAsync(u, password);
            var p = await _db.Passkeys.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct) ?? throw GatewayException.NotFound("Passkey");
            _db.Passkeys.Remove(p);
            await _db.SaveChangesAsync(ct);
            await AuditAsync(u, "auth.passkey-removed", p.Name, ct);
        }

        public async Task<PasskeyDto> RenamePasskeyAsync(string userId, Guid id, string name, CancellationToken ct)
        {
            name = (name ?? "").Trim();
            if (name.Length is 0 or > 80) throw GatewayException.BadRequest("NAME_INVALID", "Give the passkey a name of up to 80 characters.");
            var p = await _db.Passkeys.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct) ?? throw GatewayException.NotFound("Passkey");
            p.Name = name; await _db.SaveChangesAsync(ct);
            return ToDto(p);
        }

        public Task<PasskeyBeginDto> BeginPasskeyLoginAsync(PasskeyOrigin where, CancellationToken ct)
        {
            // no username is asked for: the device offers the passkeys it holds for this site
            var options = Fido(where).GetAssertionOptions(new GetAssertionOptionsParams { AllowedCredentials = new List<PublicKeyCredentialDescriptor>(), UserVerification = UserVerificationRequirement.Required });
            var session = Guid.NewGuid().ToString("N");
            Put("pk-login:" + session, options);
            return Task.FromResult(new PasskeyBeginDto(session, Json(options.ToJson())));
        }

        public async Task<string> FinishPasskeyLoginAsync(PasskeyLoginRequest req, PasskeyOrigin where, CancellationToken ct)
        {
            var options = Take<AssertionOptions>("pk-login:" + req.SessionId);
            AuthenticatorAssertionRawResponse? raw;
            try { raw = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(req.Credential.GetRawText()); }
            catch (JsonException) { throw new GatewayException(401, "INVALID_PASSKEY", "That passkey was not accepted."); }
            if (raw?.RawId == null) throw new GatewayException(401, "INVALID_PASSKEY", "That passkey was not accepted.");
            var credId = B64(raw.RawId);
            var stored = await _db.Passkeys.FirstOrDefaultAsync(p => p.CredentialId == credId, ct) ?? throw new GatewayException(401, "INVALID_PASSKEY", "That passkey is not registered here.");
            try
            {
                var res = await Fido(where).MakeAssertionAsync(new MakeAssertionParams
                {
                    AssertionResponse = raw, OriginalOptions = options, StoredPublicKey = stored.PublicKey, StoredSignatureCounter = (uint)stored.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (a, c) => Task.FromResult(a.UserHandle != null && Encoding.UTF8.GetString(a.UserHandle) == stored.UserId),
                }, ct);
                stored.SignCount = res.SignCount; stored.LastUsedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            catch (Fido2VerificationException) { throw new GatewayException(401, "INVALID_PASSKEY", "That passkey was not accepted."); }
            return stored.UserId;
        }
    }
}
