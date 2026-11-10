using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fdms.Application.Security;
using Fdms.Domain.Entities;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Fdms.Infrastructure.Auth
{
    public sealed class JwtSettings
    {
        public const string Section = "Jwt";
        /// <summary>HMAC signing key, at least 32 characters. Keep it secret and stable (changing it signs everyone out).</summary>
        public string Key { get; set; } = "";
        public string Issuer { get; set; } = "fdms-gateway";
        public string Audience { get; set; } = "fdms-gateway-users";
        public int AccessTokenMinutes { get; set; } = 60;
        public int RefreshTokenDays { get; set; } = 7;
    }

    /// <summary>
    /// Resolves the signing key from the FINAL configuration the first time it is needed (never from a value captured at start-up).
    /// Production refuses to start without a key; development makes a random one (everyone signs in again after a restart).
    /// </summary>
    public sealed class JwtKeyProvider
    {
        public string Key { get; }
        public string Issuer { get; }
        public string Audience { get; }

        public JwtKeyProvider(IConfiguration cfg, IHostEnvironment env, ILogger<JwtKeyProvider> log)
        {
            var key = cfg["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            {
                if (!env.IsDevelopment()) throw new InvalidOperationException("Jwt:Key must be set to a secret of at least 32 characters.");
                key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                log.LogWarning("Jwt:Key is not configured: using a random development key. Everyone must sign in again after a restart.");
            }
            Key = key;
            Issuer = cfg["Jwt:Issuer"] ?? "fdms-gateway";
            Audience = cfg["Jwt:Audience"] ?? "fdms-gateway-users";
        }
    }

    public static class ClaimNames
    {
        public const string ClientId = "fdms_client_id";
        public const string KeyId = "fdms_key_id";
        public const string PartnerId = "fdms_partner_id";
        public const string DeviceId = "fdms_device_id";
        /// <summary>"1" when the caller is limited to the DeviceId claims (an API token, or a branch-limited user).</summary>
        public const string Restricted = "fdms_restricted";
        /// <summary>"1" while the person must still turn on two-step sign-in (or use a passkey) because their company requires it.</summary>
        public const string MfaSetupRequired = "fdms_mfa_setup";
    }

    /// <summary>Issues access JWTs carrying role and permission claims (same idea as dara's JwtService) and opaque refresh tokens.</summary>
    public sealed class JwtService
    {
        private readonly JwtSettings _s;
        private readonly JwtKeyProvider _k;
        public JwtService(IOptions<JwtSettings> s, JwtKeyProvider k) { _s = s.Value; _k = k; }

        public DateTime AccessExpiry() => DateTime.UtcNow.AddMinutes(_s.AccessTokenMinutes);
        public DateTime RefreshExpiry() => DateTime.UtcNow.AddDays(_s.RefreshTokenDays);

        public string CreateAccessToken(ApplicationUser user, IEnumerable<string> roles, IEnumerable<string> permissions, DateTime expires, bool mfaSetupRequired = false)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Name, $"{user.Name} {user.Surname}".Trim()),
                new(ClaimTypes.Email, user.Email ?? ""),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            };
            if (user.ClientId.HasValue) claims.Add(new Claim(ClaimNames.ClientId, user.ClientId.Value.ToString()));
            if (user.PartnerId.HasValue) claims.Add(new Claim(ClaimNames.PartnerId, user.PartnerId.Value.ToString()));
            if (mfaSetupRequired) claims.Add(new Claim(ClaimNames.MfaSetupRequired, "1"));
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            claims.AddRange(permissions.Select(p => new Claim(Perm.ClaimType, p)));

            var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_k.Key)), SecurityAlgorithms.HmacSha256);
            var jwt = new JwtSecurityToken(_k.Issuer, _k.Audience, claims, expires: expires, signingCredentials: creds);
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }

        public static string NewRefreshToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}
