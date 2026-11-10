using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fdms.Application.Fdms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fdms.Infrastructure.Fdms
{
    public sealed class FdmsOptions
    {
        public const string Section = "Fdms";
        public string TestBaseUrl { get; set; } = "https://fdmsapitest.zimra.co.zw";
        public string ProductionBaseUrl { get; set; } = "https://fdmsapi.zimra.co.zw";
        public int TimeoutSeconds { get; set; } = 30;   // spec 7.4
        public int MaxTransientRetries { get; set; } = 2;
        /// <summary>
        /// ZIMRA's server chain is not trusted on many hosts and the existing dara/ZimraEGS clients skip validation, so this
        /// defaults to true. Client authentication (the device certificate) is still enforced by FDMS. Set false once the host trusts the chain.
        /// </summary>
        public bool AcceptAnyServerCertificate { get; set; } = true;
    }

    /// <summary>
    /// Typed FDMS client. One HttpClient (with the device certificate) is built per device and cached, because
    /// re-importing a private key per request leaks key files and eventually causes mTLS 401s on IIS.
    /// </summary>
    public sealed class FdmsGateway : IFdmsGateway, IDisposable
    {
        private readonly FdmsOptions _opt;
        private readonly ILogger<FdmsGateway> _log;
        private readonly HttpClient _public;
        private readonly ConcurrentDictionary<string, (HttpClient Client, X509Certificate2 Cert)> _clients = new();

        public FdmsGateway(IOptions<FdmsOptions> options, ILogger<FdmsGateway> log)
        {
            _opt = options.Value;
            _log = log;
            _public = new HttpClient(NewHandler(null)) { Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds) };
        }

        private string BaseUrl(FdmsEnvironment env) =>
            (env == FdmsEnvironment.Production ? _opt.ProductionBaseUrl : _opt.TestBaseUrl).TrimEnd('/');

        private SocketsHttpHandler NewHandler(X509Certificate2? cert)
        {
            var h = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
            if (cert != null) h.SslOptions.ClientCertificates = new X509CertificateCollection { cert };
            if (_opt.AcceptAnyServerCertificate)
            {
                h.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                _log.LogDebug("FDMS server certificate validation is disabled (Fdms:AcceptAnyServerCertificate).");
            }
            return h;
        }

        // ---- public endpoints ----------------------------------------------------------------------------------

        public Task<VerifyTaxpayerResponse> VerifyTaxpayerAsync(FdmsEnvironment env, int deviceId, string activationKey, string deviceSerialNo,
            string modelName, string modelVersion, CancellationToken ct = default) =>
            SendAsync<VerifyTaxpayerResponse>(_public, HttpMethod.Post, $"{BaseUrl(env)}/Public/v1/{deviceId}/VerifyTaxpayerInformation",
                modelName, modelVersion, new { activationKey, deviceSerialNo }, "VerifyTaxpayerInformation", ct);

        public Task<RegisterDeviceResponse> RegisterDeviceAsync(FdmsEnvironment env, int deviceId, string activationKey, string csrPem,
            string modelName, string modelVersion, CancellationToken ct = default) =>
            SendAsync<RegisterDeviceResponse>(_public, HttpMethod.Post, $"{BaseUrl(env)}/Public/v1/{deviceId}/RegisterDevice",
                modelName, modelVersion, new { activationKey, certificateRequest = csrPem }, "RegisterDevice", ct);

        // ---- device endpoints (mTLS) ---------------------------------------------------------------------------

        public Task<RegisterDeviceResponse> IssueCertificateAsync(FdmsDeviceCredentials d, string csrPem, CancellationToken ct = default) =>
            SendAsync<RegisterDeviceResponse>(ClientFor(d), HttpMethod.Post, DeviceUrl(d, "IssueCertificate"), d.ModelName, d.ModelVersion,
                new { certificateRequest = csrPem }, "IssueCertificate", ct);

        public Task<FdmsConfig> GetConfigAsync(FdmsDeviceCredentials d, CancellationToken ct = default) =>
            SendAsync<FdmsConfig>(ClientFor(d), HttpMethod.Get, DeviceUrl(d, "GetConfig"), d.ModelName, d.ModelVersion, null, "GetConfig", ct);

        public Task<FdmsStatus> GetStatusAsync(FdmsDeviceCredentials d, CancellationToken ct = default) =>
            SendAsync<FdmsStatus>(ClientFor(d), HttpMethod.Get, DeviceUrl(d, "GetStatus"), d.ModelName, d.ModelVersion, null, "GetStatus", ct);

        public Task<OpenDayResponse> OpenDayAsync(FdmsDeviceCredentials d, DateTime fiscalDayOpened, int? fiscalDayNo, CancellationToken ct = default) =>
            SendAsync<OpenDayResponse>(ClientFor(d), HttpMethod.Post, DeviceUrl(d, "OpenDay"), d.ModelName, d.ModelVersion,
                new { fiscalDayOpened = fiscalDayOpened.ToString("yyyy-MM-ddTHH:mm:ss"), fiscalDayNo }, "OpenDay", ct);

        public Task<SubmitReceiptResponse> SubmitReceiptAsync(FdmsDeviceCredentials d, ReceiptPayload receipt, CancellationToken ct = default) =>
            SendAsync<SubmitReceiptResponse>(ClientFor(d), HttpMethod.Post, DeviceUrl(d, "SubmitReceipt"), d.ModelName, d.ModelVersion,
                new { receipt }, "SubmitReceipt", ct, retryTransient: false);

        public Task<CloseDayResponse> CloseDayAsync(FdmsDeviceCredentials d, CloseDayRequest request, CancellationToken ct = default) =>
            SendAsync<CloseDayResponse>(ClientFor(d), HttpMethod.Post, DeviceUrl(d, "CloseDay"), d.ModelName, d.ModelVersion,
                request, "CloseDay", ct, retryTransient: false);

        public Task<PingResponse> PingAsync(FdmsDeviceCredentials d, CancellationToken ct = default) =>
            SendAsync<PingResponse>(ClientFor(d), HttpMethod.Post, DeviceUrl(d, "Ping"), d.ModelName, d.ModelVersion, null, "Ping", ct);

        public Task<StockListResponse> GetStockListAsync(FdmsDeviceCredentials d, string? hsCode, string? goodName, int offset, int limit, CancellationToken ct = default)
        {
            var q = new List<string> { "offset=" + Math.Max(0, offset), "limit=" + Math.Clamp(limit, 1, 100) };
            if (!string.IsNullOrWhiteSpace(hsCode)) q.Add("hsCode=" + Uri.EscapeDataString(hsCode));
            if (!string.IsNullOrWhiteSpace(goodName)) q.Add("goodName=" + Uri.EscapeDataString(goodName));
            return SendAsync<StockListResponse>(ClientFor(d), HttpMethod.Get, DeviceUrl(d, "GetStockList") + "?" + string.Join("&", q),
                d.ModelName, d.ModelVersion, null, "GetStockList", ct);
        }

        public Task<ServerCertificateResponse> GetServerCertificateAsync(FdmsEnvironment env, string? thumbprint, string modelName, string modelVersion, CancellationToken ct = default) =>
            SendAsync<ServerCertificateResponse>(_public, HttpMethod.Get,
                $"{BaseUrl(env)}/Public/v1/GetServerCertificate" + (string.IsNullOrWhiteSpace(thumbprint) ? "" : "?thumbprint=" + Uri.EscapeDataString(thumbprint)),
                modelName, modelVersion, null, "GetServerCertificate", ct);

        public Task<SubmitFileResponse> SubmitFileAsync(FdmsDeviceCredentials d, string fileName, string base64Content, CancellationToken ct = default)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes(base64Content));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(file, "file", fileName);
            // not retried here: the caller keeps the file Pending and FDMS ignores a file/receipt it already has
            return SendAsync<SubmitFileResponse>(ClientFor(d), HttpMethod.Post, DeviceUrl(d, "SubmitFile"), d.ModelName, d.ModelVersion, null, "SubmitFile", ct,
                retryTransient: false, rawContent: form);
        }

        public Task<FileStatusResponse> GetFileStatusAsync(FdmsDeviceCredentials d, DateTime uploadedFrom, DateTime uploadedTill, string? operationId, CancellationToken ct = default)
        {
            var q = $"fileUploadedFrom={uploadedFrom:yyyy-MM-dd}&fileUploadedTill={uploadedTill:yyyy-MM-dd}" +
                    (string.IsNullOrWhiteSpace(operationId) ? "" : "&operationID=" + Uri.EscapeDataString(operationId));
            return SendAsync<FileStatusResponse>(ClientFor(d), HttpMethod.Get, DeviceUrl(d, "GetFileStatus") + "?" + q, d.ModelName, d.ModelVersion, null, "GetFileStatus", ct);
        }

        public void Invalidate(int deviceId)
        {
            foreach (var key in _clients.Keys.Where(k => k.StartsWith($"{deviceId}|")).ToList())
            {
                if (_clients.TryRemove(key, out var entry))
                {
                    entry.Client.Dispose();
                    entry.Cert.Dispose();
                }
            }
        }

        // ---- plumbing ------------------------------------------------------------------------------------------

        private string DeviceUrl(FdmsDeviceCredentials d, string op) => $"{BaseUrl(d.Environment)}/Device/v1/{d.DeviceId}/{op}";

        private HttpClient ClientFor(FdmsDeviceCredentials d)
        {
            // Keyed by device + certificate so a renewed certificate gets a fresh client automatically.
            var key = $"{d.DeviceId}|{d.CertificatePem.GetHashCode()}";
            return _clients.GetOrAdd(key, _ =>
            {
                var cert = LoadCertificate(d.CertificatePem, d.PrivateKeyPem);
                var client = new HttpClient(NewHandler(cert)) { Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds) };
                return (client, cert);
            }).Client;
        }

        private static X509Certificate2 LoadCertificate(string certPem, string keyPem)
        {
            var temp = X509Certificate2.CreateFromPem(certPem, keyPem);
            if (!OperatingSystem.IsWindows()) return temp;

            // Schannel cannot use an ephemeral key for client auth; round-trip through PFX and persist ONCE (cached).
            var pfx = temp.Export(X509ContentType.Pfx);
            temp.Dispose();
            try
            {
                return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // IIS application pools often cannot write to the machine key store ("Access denied"): use the pool's own profile instead
                // (needs "Load User Profile" = True on the pool). See deploy/DEPLOY-WINDOWS.md.
                try { return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable); }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // last resort: keep the key in memory only (no key store at all)
                    return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
                }
            }
        }

        private async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, string modelName, string modelVersion,
            object? body, string op, CancellationToken ct, bool retryTransient = true, HttpContent? rawContent = null)
        {
            var attempts = retryTransient ? _opt.MaxTransientRetries + 1 : 1;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(method, url);
                    req.Headers.TryAddWithoutValidation("DeviceModelName", modelName);
                    req.Headers.TryAddWithoutValidation("DeviceModelVersion", modelVersion);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    string? json = null;
                    if (rawContent != null) req.Content = rawContent;
                    else if (body != null)
                    {
                        json = JsonSerializer.Serialize(body, FdmsJson.Options);
                        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }

                    using var res = await client.SendAsync(req, ct);
                    var text = await res.Content.ReadAsStringAsync(ct);
                    _log.LogInformation("FDMS {Op} {Status} req={Req} res={Res}", op, (int)res.StatusCode, json, text);

                    if (res.IsSuccessStatusCode)
                        return JsonSerializer.Deserialize<T>(text, FdmsJson.Options)
                               ?? throw new FdmsException((int)res.StatusCode, null, $"FDMS {op} returned an empty body.", text);

                    throw ToException(op, res.StatusCode, text);
                }
                catch (FdmsException ex) when (ex.IsTransient && attempt < attempts)
                {
                    _log.LogWarning("FDMS {Op} transient failure ({Status}), retry {Attempt}/{Max}", op, ex.HttpStatus, attempt, attempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
                }
                catch (HttpRequestException ex)
                {
                    var fe = new FdmsException(0, null, $"Network error calling FDMS {op}: {ex.Message}", null, ex);
                    if (attempt >= attempts) throw fe;
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    var fe = new FdmsException(0, null, $"FDMS {op} timed out after {_opt.TimeoutSeconds}s.", null, ex);
                    if (attempt >= attempts) throw fe;
                }
            }
        }

        private static FdmsException ToException(string op, HttpStatusCode status, string body)
        {
            string? code = null, title = null, detail = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var r = doc.RootElement;
                if (r.TryGetProperty("errorCode", out var ec)) code = ec.GetString();
                if (r.TryGetProperty("title", out var t)) title = t.GetString();
                if (r.TryGetProperty("detail", out var dt)) detail = dt.GetString();
            }
            catch (JsonException) { /* not ProblemDetails JSON */ }

            var msg = $"FDMS {op} failed: HTTP {(int)status}" + (code != null ? $" [{code}]" : "") +
                      (title != null ? $" {title}" : "") + (detail != null ? $" — {detail}" : "");
            return new FdmsException((int)status, code, msg, body);
        }

        public void Dispose()
        {
            _public.Dispose();
            foreach (var e in _clients.Values) { e.Client.Dispose(); e.Cert.Dispose(); }
        }
    }
}
