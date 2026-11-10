using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    public static class WebhookEvents
    {
        public const string ReceiptFiscalised = "receipt.fiscalised";
        public const string ReceiptFailed = "receipt.failed";
        public const string ReceiptCancelled = "receipt.cancelled";
        public const string OfflineFileFailed = "offlinefile.failed";
        public const string FiscalDayClosed = "fiscalday.closed";
        public const string FiscalDayCloseFailed = "fiscalday.close_failed";
    }

    /// <summary>Adds outbox rows to the current DbContext; they are saved together with the state change that caused them.</summary>
    public sealed class WebhookQueue
    {
        private readonly FdmsDbContext _db;
        public WebhookQueue(FdmsDbContext db) => _db = db;

        public async Task EnqueueAsync(Guid clientId, string eventType, object data, CancellationToken ct)
        {
            // Only clients that configured a webhook get rows.
            var has = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId && c.WebhookUrl != null && c.IsActive, ct);
            if (!has) return;
            var body = JsonSerializer.Serialize(new { id = Guid.NewGuid(), type = eventType, createdAt = DateTime.UtcNow, data }, FdmsJson.Options);
            _db.WebhookDeliveries.Add(new WebhookDelivery { ClientId = clientId, EventType = eventType, PayloadJson = body });
        }
    }

    public sealed class WebhookService : IWebhookService
    {
        private readonly FdmsDbContext _db;
        private readonly IDataProtector _protector;
        private readonly IConfiguration _cfg;

        public WebhookService(FdmsDbContext db, IDataProtectionProvider dp, IConfiguration cfg)
        {
            _db = db; _cfg = cfg;
            _protector = dp.CreateProtector("Fdms.Webhook.Secret.v1");
        }

        public async Task<WebhookConfigDto> SetAsync(Guid clientId, string url, CancellationToken ct)
        {
            var uri = ValidateUrl(url, _cfg.GetValue<bool>("Webhooks:AllowInsecureTargets"));
            var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            var secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            client.WebhookUrl = uri.ToString();
            client.WebhookSecretProtected = _protector.Protect(secret);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "webhook.set", Detail = uri.Host });
            await _db.SaveChangesAsync(ct);
            return new WebhookConfigDto(client.WebhookUrl, secret);   // the secret is shown only here
        }

        public async Task<WebhookConfigDto> GetAsync(Guid clientId, CancellationToken ct)
        {
            var c = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            return new WebhookConfigDto(c.WebhookUrl, null);
        }

        public async Task ClearAsync(Guid clientId, CancellationToken ct)
        {
            var c = await _db.Clients.FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            c.WebhookUrl = null; c.WebhookSecretProtected = null;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyList<WebhookDeliveryDto>> ListAsync(Guid clientId, int take, CancellationToken ct) =>
            (await _db.WebhookDeliveries.AsNoTracking().Where(d => d.ClientId == clientId)
                .OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct))
            .Select(d => new WebhookDeliveryDto(d.Id, d.EventType, d.Status.ToString(), d.Attempts, d.NextAttemptAt, d.DeliveredAt, d.LastError, d.CreatedAt)).ToList();

        /// <summary>HTTPS only; no credentials in the URL; hosts that are literally private/loopback are refused up front.</summary>
        public static Uri ValidateUrl(string url, bool allowInsecure)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw GatewayException.BadRequest("WEBHOOK_URL_INVALID", "url must be an absolute URL.");
            if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecure && uri.Scheme == Uri.UriSchemeHttp))
                throw GatewayException.BadRequest("WEBHOOK_URL_INVALID", "Webhook URLs must use https (plain http to localhost is only accepted when Webhooks:AllowInsecureTargets is on, which is the default in development only).");
            if (!string.IsNullOrEmpty(uri.UserInfo)) throw GatewayException.BadRequest("WEBHOOK_URL_INVALID", "Credentials in the URL are not allowed.");
            if (!allowInsecure && IPAddress.TryParse(uri.Host, out var ip) && IsInternal(ip))
                throw GatewayException.BadRequest("WEBHOOK_URL_INVALID", "Webhook targets on private or loopback addresses are not allowed.");
            if (!allowInsecure && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)))
                throw GatewayException.BadRequest("WEBHOOK_URL_INVALID", "Webhook targets on local hosts are not allowed.");
            return uri;
        }

        public static bool IsInternal(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast;
            var b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] >= 224;
        }
    }

    /// <summary>Delivers outbox rows with HMAC signatures and exponential backoff (1m, 5m, 30m, 2h, 6h, 12h, 24h).</summary>
    public sealed class WebhookDeliveryJob : BackgroundService
    {
        private static readonly TimeSpan[] Backoff =
            { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24) };

        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _cfg;
        private readonly ILogger<WebhookDeliveryJob> _log;
        private readonly HttpClient _http;

        public WebhookDeliveryJob(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<WebhookDeliveryJob> log)
        {
            _scopes = scopes; _cfg = cfg; _log = log;
            _http = SafeClient(cfg);
        }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stop);
                    await RunOnceAsync(stop);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogError(ex, "Webhook delivery cycle failed"); }
            }
        }

        /// <summary>An HTTP client for outgoing webhooks: no redirects, and every address is checked when connecting so no target can reach an internal host.</summary>
        public static HttpClient SafeClient(IConfiguration cfg)
        {
            var allowInternal = cfg.GetValue<bool>("Webhooks:AllowInsecureTargets");
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,   // a redirect could point at an internal address
                ConnectCallback = async (ctx, ct) =>
                {
                    // Resolve and check at connect time, so DNS tricks cannot reach internal hosts.
                    var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    var ok = addrs.Where(a => allowInternal || !WebhookService.IsInternal(a)).ToArray();
                    if (ok.Length == 0) throw new HttpRequestException("Webhook target resolves to a blocked address.");
                    var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await s.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct); return new NetworkStream(s, ownsSocket: true); }
                    catch { s.Dispose(); throw; }
                },
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        public async Task RunOnceAsync(CancellationToken ct)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Fdms.Webhook.Secret.v1");
            var now = DateTime.UtcNow;

            var due = await db.WebhookDeliveries.Where(d => d.Status == WebhookStatus.Pending && d.NextAttemptAt <= now)
                .OrderBy(d => d.CreatedAt).Take(50).ToListAsync(ct);
            foreach (var d in due)
            {
                var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == d.ClientId, ct);
                if (client?.WebhookUrl == null || client.WebhookSecretProtected == null)
                {
                    d.Status = WebhookStatus.Failed; d.LastError = "Webhook no longer configured."; continue;
                }

                d.Attempts++;
                try
                {
                    var secret = protector.Unprotect(client.WebhookSecretProtected);
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                    using var req = new HttpRequestMessage(HttpMethod.Post, client.WebhookUrl) { Content = new StringContent(d.PayloadJson, Encoding.UTF8, "application/json") };
                    req.Headers.TryAddWithoutValidation("X-Fdms-Event", d.EventType);
                    req.Headers.TryAddWithoutValidation("X-Fdms-Delivery", d.Id.ToString());
                    req.Headers.TryAddWithoutValidation("X-Fdms-Timestamp", ts);
                    req.Headers.TryAddWithoutValidation("X-Fdms-Signature", "sha256=" + Sign(secret, ts, d.PayloadJson));

                    using var res = await _http.SendAsync(req, ct);
                    if (res.IsSuccessStatusCode) { d.Status = WebhookStatus.Delivered; d.DeliveredAt = DateTime.UtcNow; d.LastError = null; }
                    else Retry(d, $"HTTP {(int)res.StatusCode}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    Retry(d, ex.Message.Length > 400 ? ex.Message[..400] : ex.Message);
                }
            }
            if (due.Count > 0) await db.SaveChangesAsync(ct);
        }

        /// <summary>HMAC-SHA256 over "timestamp.body" (hex). Receivers recompute it and compare in constant time.</summary>
        public static string Sign(string secret, string timestamp, string body) =>
            Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

        private static void Retry(WebhookDelivery d, string error)
        {
            d.LastError = error;
            if (d.Attempts > Backoff.Length) { d.Status = WebhookStatus.Failed; return; }
            d.NextAttemptAt = DateTime.UtcNow + Backoff[d.Attempts - 1];
        }
    }
}
