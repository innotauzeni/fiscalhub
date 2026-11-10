using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Where people download the mobile app (APKPure, Google Play, the App Store). Set by the platform operator; shown on the public website only once a link is filled in.
    /// Each link must point at that store's own website, so a mistyped or malicious address can never be offered as "the app".
    /// </summary>
    public sealed class AppLinksService : IAppLinksService
    {
        public const string Key = "App.Links";
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private static readonly string[] ApkPureHosts = { "apkpure.com", "apkpure.net", "m.apkpure.com", "www.apkpure.com", "www.apkpure.net" };
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        public AppLinksService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        public async Task<AppLinksDto> GetAsync(CancellationToken ct)
        {
            var json = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == Key).Select(s => s.Value).FirstOrDefaultAsync(ct);
            try { return (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AppLinksDto>(json, J)) ?? Empty; }
            catch (JsonException) { return Empty; }
        }

        private static readonly AppLinksDto Empty = new(null, null, null, null);

        /// <summary>Null when empty; otherwise an https address on one of the given hosts.</summary>
        public static string? Check(string? url, string store, params string[] hosts)
        {
            url = url?.Trim();
            if (string.IsNullOrEmpty(url)) return null;
            if (url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo))
                throw GatewayException.BadRequest("URL_INVALID", $"The {store} link must be a full https:// address.");
            if (!hosts.Contains(u.Host.ToLowerInvariant()))
                throw GatewayException.BadRequest("URL_NOT_STORE", $"The {store} link must point at {hosts[0]} (copy it from your app's page in that store).");
            return u.ToString();
        }

        public async Task<AppLinksDto> SaveAsync(AppLinksDto req, CancellationToken ct)
        {
            var version = req.Version?.Trim();
            if (version is { Length: > 30 }) throw GatewayException.BadRequest("VERSION_INVALID", "The version can be at most 30 characters.");
            var links = new AppLinksDto(
                Check(req.ApkPure, "APKPure", ApkPureHosts),
                Check(req.PlayStore, "Google Play", "play.google.com"),
                Check(req.AppStore, "App Store", "apps.apple.com", "itunes.apple.com"),
                string.IsNullOrEmpty(version) ? null : version);
            var row = await _db.SystemSettings.FirstOrDefaultAsync(x => x.Key == Key, ct);
            if (row == null) _db.SystemSettings.Add(row = new SystemSetting { Key = Key });
            row.Value = JsonSerializer.Serialize(links, J); row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { Action = "settings.app-links", Entity = "Setting", EntityId = Key,
                Detail = $"APKPure {(links.ApkPure != null ? "set" : "off")}, Google Play {(links.PlayStore != null ? "set" : "off")}, App Store {(links.AppStore != null ? "set" : "off")}" });
            await _db.SaveChangesAsync(ct);
            return links;
        }
    }
}
