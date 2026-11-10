using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;

namespace Fdms.Infrastructure.Integrations.Connectors
{
    /// <summary>What the adapters share: calls with the product's refusals turned into ConnectorException, the OAuth token call, and JSON reading.</summary>
    public abstract class ConnectorHttp
    {
        protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        protected readonly HttpClient Http;
        protected ConnectorHttp(HttpClient http) => Http = http;
        protected abstract string Label { get; }

        protected static string Cut(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];

        protected static string Query(params (string Key, string Value)[] p) => string.Join("&", p.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

        protected static StringContent Body(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

        protected static HttpRequestMessage Request(HttpMethod method, string url, string? bearer, HttpContent? content = null)
        {
            var req = new HttpRequestMessage(method, url) { Content = content };
            if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return req;
        }

        /// <summary>The product's own words for a refusal, when it gives them.</summary>
        protected virtual string ErrorText(string body) => body;

        protected async Task<JsonElement> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            using (req)
            {
                if (!req.Headers.Accept.Any()) req.Headers.Accept.ParseAdd("application/json");
                using var res = await Http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ConnectorException(401, $"{Label} no longer accepts the sign-in.", unauthorized: true);
                if ((int)res.StatusCode == 429) throw new ConnectorException(429, $"{Label} asked FiscalHub to slow down. It carries on in the next round.", rateLimited: true);
                if (!res.IsSuccessStatusCode)
                {
                    string why; try { why = ErrorText(body); } catch (Exception) { why = body; }
                    throw new ConnectorException((int)res.StatusCode, $"{Label} refused ({(int)res.StatusCode}): {Cut(string.IsNullOrWhiteSpace(why) ? res.ReasonPhrase : why, 400)}");
                }
                if (string.IsNullOrWhiteSpace(body)) return default;
                try { using var doc = JsonDocument.Parse(body); return doc.RootElement.Clone(); }
                catch (JsonException) { throw new ConnectorException((int)res.StatusCode, $"{Label} gave an unreadable answer."); }
            }
        }

        /// <summary>The OAuth token call. Basic: the app's id and secret go in the Authorization header (QuickBooks, Xero), otherwise in the form (Sage).</summary>
        protected async Task<ConnectorTokens> TokenAsync(string url, ConnectorApp app, Dictionary<string, string> form, bool basic, bool refreshing, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (basic) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.ClientId}:{app.ClientSecret}")));
            else { form["client_id"] = app.ClientId; form["client_secret"] = app.ClientSecret; }
            req.Content = new FormUrlEncodedContent(form);
            req.Headers.Accept.ParseAdd("application/json");
            using var res = await Http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            JsonElement j;
            try { using var doc = JsonDocument.Parse(body); j = doc.RootElement.Clone(); }
            catch (JsonException) { throw new ConnectorException((int)res.StatusCode, $"{Label}'s sign-in server gave an unreadable answer."); }
            var token = j.S("access_token");
            if (!res.IsSuccessStatusCode || token == null)
            {
                var e = j.S("error") ?? $"status {(int)res.StatusCode}";
                throw new ConnectorException((int)res.StatusCode, $"{Label} refused the sign-in: {e}", unauthorized: refreshing || e is "invalid_grant" or "invalid_client" or "unauthorized_client");
            }
            var secs = (int)(j.DN("expires_in") ?? 1800);
            return new ConnectorTokens(token, j.S("refresh_token"), secs);
        }

        protected static readonly Regex Digits = new(@"^\d+$", RegexOptions.Compiled);

        /// <summary>A ZIMRA TIN has 10 digits and a VAT number 9; anything else is left out.</summary>
        protected static (string? Tin, string? Vat) TaxNumbers(params string?[] values)
        {
            string? tin = null, vat = null;
            foreach (var v in values)
            {
                var d = (v ?? "").Replace(" ", "").Replace("-", "");
                if (!Digits.IsMatch(d)) continue;
                if (d.Length == 10) tin ??= d; else if (d.Length == 9) vat ??= d;
            }
            return (tin, vat);
        }

        protected static string? Hs(string? code) => code != null && Regex.IsMatch(code.Trim(), @"^\d{4,10}$") ? code.Trim() : null;
    }

    /// <summary>Reading JSON without ceremony: a missing value is null (or 0), never an exception.</summary>
    public static class ConnectorJson
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Regex MsDate = new(@"^/Date\((-?\d+)([+-]\d{4})?\)/$", RegexOptions.Compiled);

        public static JsonElement G(this JsonElement e, params string[] path)
        {
            var cur = e;
            foreach (var p in path)
            {
                if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out var next)) return default;
                cur = next;
            }
            return cur;
        }

        public static string? S(this JsonElement e, params string[] path)
        {
            var v = e.G(path);
            return v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        }

        public static decimal? DN(this JsonElement e, params string[] path)
        {
            var v = e.G(path);
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, Inv, out d)) return d;
            return null;
        }

        public static decimal D(this JsonElement e, params string[] path) => e.DN(path) ?? 0m;

        public static IEnumerable<JsonElement> A(this JsonElement e, params string[] path)
        {
            var v = e.G(path);
            return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();
        }

        /// <summary>A date in ISO form, Odoo's "yyyy-MM-dd HH:mm:ss" (UTC) or Microsoft's "/Date(ms+0000)/" (Xero); always UTC.</summary>
        public static DateTime? T(this JsonElement e, params string[] path)
        {
            var s = e.S(path);
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = MsDate.Match(s);
            if (m.Success) return DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(m.Groups[1].Value, Inv)).UtcDateTime;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var odoo)) return odoo;
            return DateTime.TryParse(s, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        }
    }
}
