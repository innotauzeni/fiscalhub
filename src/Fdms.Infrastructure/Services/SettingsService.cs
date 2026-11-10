using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Platform settings kept in the database so the operator changes them in the console. The public address of the API is what the developer
    /// documentation shows in its examples and links, so it must be the API's own address, never the address of whatever serves the web page.
    /// </summary>
    public sealed class SettingsService : ISettingsService
    {
        public const string PublicApiUrlKey = "PublicApiUrl", PublicAppUrlKey = "PublicAppUrl";
        private const string CacheKey = "setting:" + PublicApiUrlKey;
        private readonly FdmsDbContext _db;
        private readonly IConfiguration _cfg;
        private readonly IMemoryCache _cache;
        private readonly ICallerScope _scope;
        public SettingsService(FdmsDbContext db, IConfiguration cfg, IMemoryCache cache, ICallerScope scope) { _db = db; _cfg = cfg; _cache = cache; _scope = scope; }

        /// <summary>An absolute http(s) address, no query or fragment, no trailing slash; null when it is not acceptable.</summary>
        public static string? Normalize(string? value)
        {
            var v = value?.Trim();
            if (string.IsNullOrEmpty(v) || v.Length > 200) return null;
            if (!Uri.TryCreate(v, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || !string.IsNullOrEmpty(u.UserInfo)) return null;
            return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        private async Task<string?> StoredAsync(CancellationToken ct) =>
            await _cache.GetOrCreateAsync(CacheKey, async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                return (await _db.SystemSettings.AsNoTracking().Where(s => s.Key == PublicApiUrlKey).Select(s => s.Value).FirstOrDefaultAsync(ct)) ?? "";
            }) is { Length: > 0 } v ? v : null;

        public async Task<string> EffectiveApiUrlAsync(string requestBase, CancellationToken ct) => (await ResolveAsync(requestBase, ct)).Url;

        private async Task<string?> StoredAppUrlAsync(CancellationToken ct) =>
            await _cache.GetOrCreateAsync("setting:" + PublicAppUrlKey, async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                return (await _db.SystemSettings.AsNoTracking().Where(s => s.Key == PublicAppUrlKey).Select(s => s.Value).FirstOrDefaultAsync(ct)) ?? "";
            }) is { Length: > 0 } v ? v : null;

        /// <summary>Where the web console is: Settings, then Hosting:AppUrl, then the console site allowed in Cors:Origins (it has its own site), then the API's own /app.</summary>
        public async Task<string> EffectiveAppUrlAsync(string requestBase, CancellationToken ct) =>
            await StoredAppUrlAsync(ct) ?? Normalize(_cfg["Hosting:AppUrl"]) ?? FirstCorsOrigin() ?? (await EffectiveApiUrlAsync(requestBase, ct)) + "/app";

        private string? FirstCorsOrigin()
        {
            var list = _cfg.GetSection("Cors:Origins").Get<string[]>();
            var first = list is { Length: > 0 } ? list[0] : (_cfg["Cors:Origins"] ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return Normalize(first?.Trim());
        }

        private async Task<(string Url, string Source, string? Stored)> ResolveAsync(string requestBase, CancellationToken ct)
        {
            var stored = await StoredAsync(ct);
            if (stored != null) return (stored, "database", stored);
            var configured = Normalize(_cfg["Hosting:PublicUrl"]);
            return configured != null ? (configured, "config", null) : (requestBase.TrimEnd('/'), "request", null);
        }

        public async Task<PlatformSettingsDto> GetAsync(string requestBase, CancellationToken ct)
        {
            var (url, source, stored) = await ResolveAsync(requestBase, ct);
            var app = await StoredAppUrlAsync(ct);
            return new PlatformSettingsDto(stored, url, source, url + "/swagger/index.html", url + "/swagger/v1/swagger.json", url + "/api/v1/docs/integration", app, await EffectiveAppUrlAsync(requestBase, ct));
        }

        public async Task<PlatformSettingsDto> SaveAsync(string requestBase, SavePlatformSettingsRequest req, CancellationToken ct)
        {
            var raw = req.PublicApiUrl?.Trim();
            var row = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == PublicApiUrlKey, ct);
            if (string.IsNullOrEmpty(raw))
            {
                if (row != null) _db.SystemSettings.Remove(row);   // back to Hosting:PublicUrl, else the address of the request
            }
            else
            {
                var url = Normalize(raw) ?? throw GatewayException.BadRequest("URL_INVALID", "Enter the full address, for example https://localhost:7167 or https://api.yourcompany.com (http or https, no query).");
                if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = PublicApiUrlKey });
                row.Value = url; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            }
            _db.AuditLogs.Add(new AuditLog { Action = "settings.update", Entity = "Setting", EntityId = PublicApiUrlKey, Detail = string.IsNullOrEmpty(raw) ? "public API address cleared" : "public API address set to " + raw });

            // the web console address: where customers come back to after paying and where emailed links lead
            var appRaw = req.PublicAppUrl?.Trim();
            var appRow = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == PublicAppUrlKey, ct);
            if (string.IsNullOrEmpty(appRaw)) { if (appRow != null) _db.SystemSettings.Remove(appRow); }
            else
            {
                var appUrl = Normalize(appRaw) ?? throw GatewayException.BadRequest("URL_INVALID", "Enter the full address of the web console, for example http://localhost:4200/app or https://app.yourcompany.com/app.");
                if (appRow == null) _db.SystemSettings.Add(appRow = new SystemSetting { Key = PublicAppUrlKey });
                appRow.Value = appUrl; appRow.UpdatedAt = DateTime.UtcNow; appRow.UpdatedBy = _scope.UserName;
            }
            _db.AuditLogs.Add(new AuditLog { Action = "settings.update", Entity = "Setting", EntityId = PublicAppUrlKey, Detail = string.IsNullOrEmpty(appRaw) ? "web console address cleared" : "web console address set to " + appRaw });
            await _db.SaveChangesAsync(ct);
            _cache.Remove(CacheKey); _cache.Remove("setting:" + PublicAppUrlKey);
            return await GetAsync(requestBase, ct);
        }

        public async Task<bool> EnsureDefaultAsync(string candidate, CancellationToken ct)
        {
            var url = Normalize(candidate);
            if (url == null || await _db.SystemSettings.AnyAsync(s => s.Key == PublicApiUrlKey, ct)) return false;
            _db.SystemSettings.Add(new SystemSetting { Key = PublicApiUrlKey, Value = url, UpdatedBy = "startup" });
            _db.AuditLogs.Add(new AuditLog { Action = "settings.default", Entity = "Setting", EntityId = PublicApiUrlKey, Detail = "public API address defaulted to " + url });
            await _db.SaveChangesAsync(ct);
            _cache.Remove(CacheKey);
            return true;
        }
    }
}
