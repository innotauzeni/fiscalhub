using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fiscal;
using Fdms.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Messaging
{
    /// <summary>The SMS providers on offer: the fields each needs (for the settings page and for checking a save) and its built-in address.</summary>
    public static class SmsProviders
    {
        public const string Nhume = "NHUME", Twilio = "TWILIO", ESolutions = "ESOLUTIONS", BulkSms = "BULKSMS", AfricasTalking = "AFRICASTALKING", Custom = "CUSTOM";
        public const string NhumeUrl = "https://api.nhume.co.zw/api/v1";

        public static readonly IReadOnlyList<SmsProviderDto> All = new[]
        {
            new SmsProviderDto(Nhume, "Nhume", new[] { "apiKey", "senderId", "apiUrl?" }, NhumeUrl, "API key from your Nhume account and an approved sender ID."),
            new SmsProviderDto(Twilio, "Twilio", new[] { "username", "apiSecret", "senderId" }, "https://api.twilio.com", "Account SID as the user name, the auth token as the secret, and your Twilio number (+1…) or sender ID."),
            new SmsProviderDto(ESolutions, "eSolutions", new[] { "apiUrl", "username", "apiSecret", "senderId" }, null, "The SMS API address eSolutions gave you (messages go to …/single), your user name and password, and your originator (sender ID)."),
            new SmsProviderDto(BulkSms, "BulkSMS", new[] { "username", "apiSecret", "senderId?" }, "https://api.bulksms.com/v1", "API token ID as the user name and the token secret."),
            new SmsProviderDto(AfricasTalking, "Africa's Talking", new[] { "username", "apiKey", "senderId?", "apiUrl?" }, "https://api.africastalking.com/version1/messaging", "Your app user name and API key; a sender ID if one is approved."),
            new SmsProviderDto(Custom, "Other (HTTP)", new[] { "apiUrl", "apiKey?", "senderId?" }, null, "Any HTTPS address that accepts POST {\"phone\", \"message\", \"sender\"} as JSON, with an optional bearer key."),
        };

        public static SmsProviderDto? Find(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sends one SMS through the chosen provider's HTTP API. Every call goes through the webhook-safe client (no redirects, no internal addresses), because companies
    /// type some of these addresses themselves. 429 and 5xx answers, and network errors, count as temporary (tried again later).
    /// </summary>
    public sealed class HttpSmsApi : ISmsApi
    {
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private readonly HttpClient _http;
        public HttpSmsApi(IConfiguration cfg) { _http = WebhookDeliveryJob.SafeClient(cfg); _http.Timeout = TimeSpan.FromSeconds(20); }

        public async Task<SmsSendResult> SendAsync(SmsConfig c, string to, string text, CancellationToken ct)
        {
            try
            {
                return c.Provider.ToUpperInvariant() switch
                {
                    SmsProviders.Nhume => await NhumeAsync(c, to, text, ct),
                    SmsProviders.Twilio => await TwilioAsync(c, to, text, ct),
                    SmsProviders.ESolutions => await ESolutionsAsync(c, to, text, ct),
                    SmsProviders.BulkSms => await BulkSmsAsync(c, to, text, ct),
                    SmsProviders.AfricasTalking => await AfricasTalkingAsync(c, to, text, ct),
                    SmsProviders.Custom => await CustomAsync(c, to, text, ct),
                    _ => SmsSendResult.Fail("Unknown SMS provider " + c.Provider),
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SmsSendResult.Fail("The SMS provider did not answer in time.", true); }
            catch (HttpRequestException ex) { return SmsSendResult.Fail("The SMS provider could not be reached: " + ex.Message, true); }
        }

        private static HttpRequestMessage Post(string url, HttpContent body, AuthenticationHeaderValue? auth = null)
        {
            var m = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            if (auth != null) m.Headers.Authorization = auth;
            m.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return m;
        }

        private static AuthenticationHeaderValue Basic(string? user, string? secret) => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
        private static AuthenticationHeaderValue? Bearer(string? key) => string.IsNullOrWhiteSpace(key) ? null : new AuthenticationHeaderValue("Bearer", key);

        /// <summary>Success: the message id when the answer has one. Failure: the provider's words, trimmed.</summary>
        private async Task<SmsSendResult> SendAsync(HttpRequestMessage req, string name, Func<JsonElement, string?> id, CancellationToken ct)
        {
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                return SmsSendResult.Fail($"{name} {(int)res.StatusCode}: {Trim(body)}", res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500);
            try { using var doc = JsonDocument.Parse(body); return SmsSendResult.Sent(id(doc.RootElement)); }
            catch (JsonException) { return SmsSendResult.Sent(null); }
        }

        private static string Trim(string s) { s = s.Trim(); return s.Length > 300 ? s[..300] : s; }
        private static string? Str(JsonElement e, params string[] path)
        {
            foreach (var p in path)
            {
                if (e.ValueKind == JsonValueKind.Array) { if (e.GetArrayLength() == 0) return null; e = e[0]; }
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
            }
            return e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetRawText(), _ => null };
        }

        // ── Nhume: POST {base}/sms/send { from, to, message }, bearer key ──
        private Task<SmsSendResult> NhumeAsync(SmsConfig c, string to, string text, CancellationToken ct)
        {
            var url = (string.IsNullOrWhiteSpace(c.ApiUrl) ? SmsProviders.NhumeUrl : c.ApiUrl!).TrimEnd('/') + "/sms/send";
            return SendAsync(Post(url, JsonContent.Create(new { from = c.SenderId, to, message = text }, options: J), Bearer(c.ApiKey)), "Nhume", r => Str(r, "data", "id"), ct);
        }

        // ── Twilio: form POST to Accounts/{sid}/Messages.json, basic auth sid:token ──
        private Task<SmsSendResult> TwilioAsync(SmsConfig c, string to, string text, CancellationToken ct)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["To"] = "+" + to, ["From"] = c.SenderId ?? "", ["Body"] = text });
            return SendAsync(Post($"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(c.Username ?? "")}/Messages.json", form, Basic(c.Username, c.ApiSecret)), "Twilio", r => Str(r, "sid"), ct);
        }

        // ── eSolutions: POST {base}/single, basic auth, { originator, destination, messageText, messageReference, messageDate, messageValidity, sendDateTime } ──
        private Task<SmsSendResult> ESolutionsAsync(SmsConfig c, string to, string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(c.ApiUrl)) return Task.FromResult(SmsSendResult.Fail("eSolutions: the API address is not set."));
            var now = FdmsClock.CatNow();
            var reference = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
            var payload = new Dictionary<string, string>
            {
                ["originator"] = c.SenderId ?? "", ["destination"] = to, ["messageText"] = text, ["messageReference"] = reference,
                ["messageDate"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["messageValidity"] = "03:00",
                ["sendDateTime"] = now.ToString("HH:mm", CultureInfo.InvariantCulture),
            };
            return SendAsync(Post(c.ApiUrl!.TrimEnd('/') + "/single", JsonContent.Create(payload), Basic(c.Username, c.ApiSecret)), "eSolutions",
                r => Str(r, "messageId") ?? Str(r, "messageReference") ?? reference, ct);
        }

        // ── BulkSMS: POST https://api.bulksms.com/v1/messages { to, body, from? }, basic auth token id:secret ──
        private Task<SmsSendResult> BulkSmsAsync(SmsConfig c, string to, string text, CancellationToken ct)
        {
            object payload = string.IsNullOrWhiteSpace(c.SenderId) ? new { to = "+" + to, body = text } : new { to = "+" + to, body = text, from = c.SenderId };
            var url = string.IsNullOrWhiteSpace(c.ApiUrl) ? "https://api.bulksms.com/v1/messages" : c.ApiUrl!.TrimEnd('/') + "/messages";
            return SendAsync(Post(url, JsonContent.Create(payload, options: J), Basic(c.Username, c.ApiSecret)), "BulkSMS", r => Str(r, "id"), ct);
        }

        // ── Africa's Talking: form POST, header apiKey ──
        private Task<SmsSendResult> AfricasTalkingAsync(SmsConfig c, string to, string text, CancellationToken ct)
        {
            var fields = new Dictionary<string, string> { ["username"] = c.Username ?? "", ["to"] = "+" + to, ["message"] = text };
            if (!string.IsNullOrWhiteSpace(c.SenderId)) fields["from"] = c.SenderId!;
            var req = Post(string.IsNullOrWhiteSpace(c.ApiUrl) ? "https://api.africastalking.com/version1/messaging" : c.ApiUrl!, new FormUrlEncodedContent(fields));
            req.Headers.TryAddWithoutValidation("apiKey", c.ApiKey ?? "");
            return SendAsync(req, "Africa's Talking", r => Str(r, "SMSMessageData", "Recipients", "messageId"), ct);
        }

        // ── Any HTTPS address: POST { phone, message, sender }, optional bearer key ──
        private Task<SmsSendResult> CustomAsync(SmsConfig c, string to, string text, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(c.ApiUrl)) return Task.FromResult(SmsSendResult.Fail("The SMS address is not set."));
            return SendAsync(Post(c.ApiUrl!, JsonContent.Create(new { phone = to, message = text, sender = c.SenderId ?? "" }, options: J), Bearer(c.ApiKey)), "SMS provider",
                r => Str(r, "id") ?? Str(r, "messageId") ?? Str(r, "data", "id"), ct);
        }
    }
}
