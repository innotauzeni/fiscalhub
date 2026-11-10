using Fdms.Infrastructure.Services;
using System.Security.Cryptography;
using System.Text;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Auth
{
    /// <summary>
    /// Integration tokens. A token belongs to ONE device and carries an explicit list of permissions (subset of Perm.TokenScopes), so a
    /// leaked token can only do those things on that device. Only a SHA-256 hash is stored; the secret is shown once.
    /// </summary>
    public sealed class ApiTokenService : IApiTokenService
    {
        private static readonly Dictionary<string, string> ScopeNames = Perm.All.ToDictionary(p => p.Name, p => p.DisplayName);
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        public ApiTokenService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

        public IReadOnlyList<ScopeDto> Scopes() => Perm.TokenScopes.Select(s => new ScopeDto(s, ScopeNames.GetValueOrDefault(s, s))).ToList();

        public async Task<CreatedApiToken> CreateAsync(Guid clientId, Guid deviceId, CreateTokenRequest req, string? userId, CancellationToken ct)
        {
            var device = await _db.Devices.AsNoTracking().InScope(_scope).FirstOrDefaultAsync(d => d.Id == deviceId && d.ClientId == clientId, ct)
                         ?? throw GatewayException.NotFound("Device");
            if (string.IsNullOrWhiteSpace(req.Name)) throw GatewayException.BadRequest("NAME_REQUIRED", "Give the token a name (e.g. the system that will use it).");

            var scopes = (req.Scopes ?? Array.Empty<string>()).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
            if (scopes.Count == 0) throw GatewayException.BadRequest("SCOPES_REQUIRED", "Choose at least one permission for the token.");
            var bad = scopes.Where(s => !Perm.TokenScopes.Contains(s)).ToList();
            if (bad.Count > 0) throw GatewayException.BadRequest("SCOPE_NOT_ALLOWED", "These permissions cannot be given to a token: " + string.Join(", ", bad));
            if (req.ExpiresAt.HasValue && (req.ExpiresAt <= DateTime.UtcNow || req.ExpiresAt > DateTime.UtcNow.AddYears(5)))
                throw GatewayException.BadRequest("EXPIRY_INVALID", "The expiry must be in the future and within 5 years.");

            // 192 bits of randomness; the token is only ever shown in this response.
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var token = "fdms_" + secret;
            var key = new ApiKey
            {
                ClientId = clientId, DeviceId = deviceId, Name = req.Name.Trim(), KeyPrefix = token[..12], KeyHash = Hash(token),
                Scopes = string.Join(",", scopes), CreatedByUserId = userId, ExpiresAt = req.ExpiresAt,
            };
            _db.ApiKeys.Add(key);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, ApiKeyId = key.Id, Action = "token.create", Detail = $"device={device.ZimraDeviceId} name={key.Name} scopes={key.Scopes}" });
            await _db.SaveChangesAsync(ct);
            return new CreatedApiToken(ToDto(key, device.ZimraDeviceId, null), token);
        }

        public async Task<IReadOnlyList<ApiTokenDto>> ListAsync(Guid clientId, Guid? deviceId, CancellationToken ct)
        {
            var q = from k in _db.ApiKeys.AsNoTracking()
                    join d in _db.Devices.AsNoTracking().InScope(_scope) on k.DeviceId equals d.Id
                    where k.ClientId == clientId && (deviceId == null || k.DeviceId == deviceId)
                    orderby k.CreatedAt descending
                    select new { Key = k, d.ZimraDeviceId };
            var rows = await q.ToListAsync(ct);
            var userIds = rows.Select(r => r.Key.CreatedByUserId).Where(x => x != null).Distinct().ToList();
            var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => (u.Name + " " + u.Surname).Trim(), ct);
            return rows.Select(r => ToDto(r.Key, r.ZimraDeviceId, r.Key.CreatedByUserId != null && names.TryGetValue(r.Key.CreatedByUserId, out var n) ? n : null)).ToList();
        }

        public async Task RevokeAsync(Guid clientId, Guid tokenId, CancellationToken ct)
        {
            var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.Id == tokenId && k.ClientId == clientId, ct) ?? throw GatewayException.NotFound("API token");
            key.RevokedAt ??= DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, ApiKeyId = key.Id, Action = "token.revoke", Detail = key.Name });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<TokenPrincipal?> ValidateAsync(string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("fdms_", StringComparison.Ordinal) || token.Length > 100) return null;
            var hash = Hash(token);
            var now = DateTime.UtcNow;
            var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
            if (key == null || key.RevokedAt != null || (key.ExpiresAt != null && key.ExpiresAt < now)) return null;
            // the company and the device must both still be active
            var ok = await _db.Clients.AnyAsync(c => c.Id == key.ClientId && c.IsActive, ct)
                     && await _db.Devices.AnyAsync(d => d.Id == key.DeviceId && d.ClientId == key.ClientId && d.IsActive, ct);
            if (!ok) return null;

            if (key.LastUsedAt == null || key.LastUsedAt < now.AddMinutes(-1)) { key.LastUsedAt = now; await _db.SaveChangesAsync(ct); }
            // scopes are re-checked against today's allowed list, so removing a scope from the catalogue takes effect at once
            var scopes = key.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(s => Perm.TokenScopes.Contains(s)).ToList();
            return new TokenPrincipal(key.ClientId, key.Id, key.DeviceId, scopes, key.Name);
        }

        private static ApiTokenDto ToDto(ApiKey k, int zimraId, string? createdBy) => new(k.Id, k.DeviceId, zimraId, k.Name, k.KeyPrefix,
            k.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries), k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.LastUsedAt, createdBy);
    }
}
