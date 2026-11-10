using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>Outcome of starting a charge. Status: Pending, Paid or Failed. Redirect is set when the customer must leave for the gateway's page.</summary>
    public sealed record ChargeResult(bool Ok, string Status, string? ProviderReference, string? Message, string? RedirectUrl = null);

    public sealed record StripeSession(string Id, bool Paid, decimal Amount, string Currency, string? Reference);
    public sealed record EcoCashStatus(string Status, decimal Amount, string? EcoCashReference);

    /// <summary>EcoCash EIP (Econet's own merchant gateway): charges the subscriber directly with a PIN prompt, then confirms by status lookup.</summary>
    public interface IEcoCashGateway
    {
        Task<bool> IsConfiguredAsync(CancellationToken ct);
        Task<ChargeResult> ChargeAsync(string reference, decimal amount, string currency, string phone, string description, string notifyUrl, CancellationToken ct);
        Task<EcoCashStatus?> StatusAsync(string phone, string reference, CancellationToken ct);
    }

    /// <summary>Stripe hosted checkout for card payments, over Stripe's REST API (no SDK).</summary>
    public interface IStripeGateway
    {
        Task<bool> IsConfiguredAsync(CancellationToken ct);
        Task<ChargeResult> CreateCheckoutAsync(string reference, decimal amount, string currency, string email, string description, string successUrl, string cancelUrl, CancellationToken ct);
        Task<StripeSession?> GetSessionAsync(string sessionId, CancellationToken ct);
        /// <summary>Verifies the Stripe-Signature header and returns the completed checkout session; null when it is not genuine or not a completed checkout.</summary>
        Task<StripeSession?> ParseWebhookAsync(string payload, string signatureHeader, CancellationToken ct);
    }

    public sealed class EcoCashGateway : IEcoCashGateway
    {
        private readonly HttpClient _http;
        private readonly IPaymentGatewaySettings _settings;
        private readonly ILogger<EcoCashGateway> _log;
        public EcoCashGateway(HttpClient http, IPaymentGatewaySettings settings, ILogger<EcoCashGateway> log) { _http = http; _settings = settings; _log = log; }

        public async Task<bool> IsConfiguredAsync(CancellationToken ct) => await _settings.CredentialsAsync(PaymentGatewaySettings.EcoCashCode, ct) != null;

        /// <summary>EcoCash wants a bare MSISDN: 263XXXXXXXXX. Accepts 07…, +263… and 263….</summary>
        public static string? ToMsisdn(string? phone)
        {
            var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length < 9) return null;
            if (digits.StartsWith("263")) return digits;
            if (digits.StartsWith("0")) return "263" + digits[1..];
            return digits.Length == 9 ? "263" + digits : digits;
        }

        /// <summary>EcoCash's vocabulary varies by endpoint and version, so it is normalised once.</summary>
        public static string MapStatus(string? status) => (status ?? "").Trim().ToUpperInvariant() switch
        {
            "COMPLETED" or "SUCCESS" or "SUCCESSFUL" or "PAID" or "CHARGED" => "Paid",
            "REFUNDED" or "REVERSED" => "Refunded",
            "FAILED" or "DECLINED" or "ERROR" or "REJECTED" or "INSUFFICIENT_BALANCE" => "Failed",
            "CANCELLED" or "CANCELED" or "EXPIRED" or "TIMEOUT" => "Cancelled",
            _ => "Pending",
        };

        public async Task<ChargeResult> ChargeAsync(string reference, decimal amount, string currency, string phone, string description, string notifyUrl, CancellationToken ct)
        {
            var c = await _settings.CredentialsAsync(PaymentGatewaySettings.EcoCashCode, ct);
            if (c == null) return new(false, "Failed", null, "EcoCash is not configured.");
            var msisdn = ToMsisdn(phone);
            if (msisdn == null) return new(false, "Failed", null, "A valid EcoCash mobile number is required.");
            var payload = new Dictionary<string, object?>
            {
                ["clientCorrelator"] = reference, ["notifyUrl"] = notifyUrl, ["referenceCode"] = reference, ["tranType"] = "MER", ["endUserId"] = msisdn, ["remarks"] = description,
                ["transactionOperationStatus"] = "Charged",
                ["paymentAmount"] = new Dictionary<string, object?>
                {
                    // EcoCash's own spelling: "charginginformation", all lower case. Anything else is silently rejected.
                    ["charginginformation"] = new Dictionary<string, object?> { ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = currency, ["description"] = description },
                    ["chargeMetaData"] = new Dictionary<string, object?> { ["channel"] = c["channel"] },
                },
                ["merchantCode"] = c["merchantCode"], ["merchantPin"] = c["merchantPin"], ["merchantNumber"] = c["merchantNumber"], ["countryCode"] = c["countryCode"],
                ["terminalID"] = c["terminalId"], ["location"] = c["location"],
                ["superMerchantName"] = string.IsNullOrEmpty(c["superMerchantName"]) ? c["merchantName"] : c["superMerchantName"], ["merchantName"] = c["merchantName"],
            };
            var (ok, body, error) = await SendAsync(HttpMethod.Post, "transactions/amount/", payload, c, ct);
            if (!ok) return new(false, "Failed", null, error);
            var status = MapStatus(Read(body, "transactionStatus") ?? Read(body, "transactionOperationStatus"));
            var eco = Read(body, "ecocashReference") ?? Read(body, "transactionReference") ?? Read(body, "serverReferenceCode") ?? Read(body, "id") ?? Read(body, "referenceCode");
            return status is "Failed" or "Cancelled" ? new(false, "Failed", eco, Read(body, "message") ?? Read(body, "description") ?? "EcoCash declined the payment request.") : new(true, status, eco, "Approve the prompt on your phone.");
        }

        public async Task<EcoCashStatus?> StatusAsync(string phone, string reference, CancellationToken ct)
        {
            var c = await _settings.CredentialsAsync(PaymentGatewaySettings.EcoCashCode, ct);
            var msisdn = ToMsisdn(phone);
            if (c == null || msisdn == null || string.IsNullOrWhiteSpace(reference)) return null;
            var (ok, body, _) = await SendAsync(HttpMethod.Get, $"{Uri.EscapeDataString(msisdn)}/transactions/amount/{Uri.EscapeDataString(reference)}", null, c, ct);
            if (!ok) return null;
            decimal.TryParse(Read(body, "amount"), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount);
            return new EcoCashStatus(MapStatus(Read(body, "transactionStatus") ?? Read(body, "transactionOperationStatus")), amount, Read(body, "ecocashReference") ?? Read(body, "transactionReference"));
        }

        private async Task<(bool Ok, string? Body, string? Error)> SendAsync(HttpMethod method, string path, object? payload, IReadOnlyDictionary<string, string> c, CancellationToken ct)
        {
            // absolute on purpose: a rooted path on a BaseAddress would drop the "/sandbox/payment/v1" prefix and 404
            var url = $"{c["baseUrl"].TrimEnd('/')}/{path.TrimStart('/')}";
            try
            {
                using var msg = new HttpRequestMessage(method, url);
                msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c["username"]}:{c["password"]}")));
                msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                // EcoCash's host sits behind a firewall that refuses clients without a browser-like User-Agent
                msg.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                if (payload != null) msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var res = await _http.SendAsync(msg, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) return (true, body, null);
                _log.LogWarning("EcoCash {Method} {Path} failed with {Status}: {Body}", method.Method, path, (int)res.StatusCode, body.Length > 400 ? body[..400] : body);   // the request is never logged: it carries the merchant PIN
                var html = body.TrimStart().StartsWith("<", StringComparison.Ordinal);
                return (false, body, Read(body, "message") ?? Read(body, "description") ?? Read(body, "error")
                    ?? (html ? "EcoCash could not be reached right now. Try again shortly or use another payment method." : $"EcoCash returned {(int)res.StatusCode}."));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (false, null, "EcoCash did not respond in time. Please try again."); }
            catch (HttpRequestException ex) { _log.LogWarning(ex, "Could not reach EcoCash"); return (false, null, "Could not reach EcoCash. Please try again shortly."); }
        }

        /// <summary>Depth-first, case-insensitive lookup: the payload nests differently between the charge and status responses.</summary>
        private static string? Read(string? json, string name)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { using var doc = JsonDocument.Parse(json); return Find(doc.RootElement, name); } catch (JsonException) { return null; }
        }

        private static string? Find(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return p.Value.ToString();
                    if (Find(p.Value, name) is { } n) return n;
                }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var i in e.EnumerateArray()) if (Find(i, name) is { } n) return n;
            return null;
        }
    }

    public sealed class StripeGateway : IStripeGateway
    {
        private readonly HttpClient _http;
        private readonly IPaymentGatewaySettings _settings;
        private readonly ILogger<StripeGateway> _log;
        public StripeGateway(HttpClient http, IPaymentGatewaySettings settings, ILogger<StripeGateway> log) { _http = http; _settings = settings; _log = log; }

        public async Task<bool> IsConfiguredAsync(CancellationToken ct) => await _settings.CredentialsAsync(PaymentGatewaySettings.StripeCode, ct) != null;

        public async Task<ChargeResult> CreateCheckoutAsync(string reference, decimal amount, string currency, string email, string description, string successUrl, string cancelUrl, CancellationToken ct)
        {
            var c = await _settings.CredentialsAsync(PaymentGatewaySettings.StripeCode, ct);
            if (c == null) return new(false, "Failed", null, "Stripe is not configured.");
            var form = new List<KeyValuePair<string, string>>
            {
                new("mode", "payment"), new("payment_method_types[0]", "card"), new("client_reference_id", reference), new("customer_email", email), new("success_url", successUrl), new("cancel_url", cancelUrl),
                new("line_items[0][quantity]", "1"), new("line_items[0][price_data][currency]", currency.ToLowerInvariant()),
                new("line_items[0][price_data][unit_amount]", ((long)Math.Round(amount * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)),
                new("line_items[0][price_data][product_data][name]", description),
            };
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions") { Content = new FormUrlEncodedContent(form) };
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c["secretKey"]);
                using var res = await _http.SendAsync(msg, ct);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (!res.IsSuccessStatusCode) return new(false, "Failed", null, doc.RootElement.TryGetProperty("error", out var er) && er.TryGetProperty("message", out var m) ? m.GetString() : "Stripe refused the payment.");
                return new(true, "Pending", doc.RootElement.GetProperty("id").GetString(), null, doc.RootElement.GetProperty("url").GetString());
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { _log.LogWarning(ex, "Could not reach Stripe"); return new(false, "Failed", null, "Stripe is not reachable right now. Try again shortly."); }
        }

        private static StripeSession? ToSession(JsonElement o)
        {
            if (!o.TryGetProperty("id", out var id)) return null;
            return new StripeSession(id.GetString()!, o.TryGetProperty("payment_status", out var ps) && ps.GetString() == "paid",
                o.TryGetProperty("amount_total", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() / 100m : 0m,
                o.TryGetProperty("currency", out var cur) ? (cur.GetString() ?? "").ToUpperInvariant() : "",
                o.TryGetProperty("client_reference_id", out var rf) ? rf.GetString() : null);
        }

        public async Task<StripeSession?> GetSessionAsync(string sessionId, CancellationToken ct)
        {
            var c = await _settings.CredentialsAsync(PaymentGatewaySettings.StripeCode, ct);
            if (c == null || string.IsNullOrWhiteSpace(sessionId)) return null;
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/checkout/sessions/" + Uri.EscapeDataString(sessionId));
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c["secretKey"]);
                using var res = await _http.SendAsync(msg, ct);
                if (!res.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                return ToSession(doc.RootElement);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { _log.LogWarning(ex, "Could not look up the Stripe session"); return null; }
        }

        public async Task<StripeSession?> ParseWebhookAsync(string payload, string signatureHeader, CancellationToken ct)
        {
            var c = await _settings.CredentialsAsync(PaymentGatewaySettings.StripeCode, ct);
            if (c == null || string.IsNullOrEmpty(c["webhookSecret"]) || !VerifySignature(payload, signatureHeader, c["webhookSecret"], DateTimeOffset.UtcNow)) return null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() != "checkout.session.completed") return null;
                return ToSession(root.GetProperty("data").GetProperty("object"));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        }

        /// <summary>Stripe-Signature: t=timestamp,v1=hex(HMAC-SHA256(secret, "t.payload")). Messages older than five minutes are refused (replay protection).</summary>
        public static bool VerifySignature(string payload, string header, string secret, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(header)) return false;
            string? t = null; var v1 = new List<string>();
            foreach (var part in header.Split(','))
            {
                var kv = part.Split('=', 2); if (kv.Length != 2) continue;
                if (kv[0].Trim() == "t") t = kv[1].Trim(); else if (kv[0].Trim() == "v1") v1.Add(kv[1].Trim());
            }
            if (t == null || !long.TryParse(t, out var ts) || Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(ts)).TotalSeconds) > 300) return false;
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}"))).ToLowerInvariant();
            return v1.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(s.ToLowerInvariant())));
        }
    }
}
