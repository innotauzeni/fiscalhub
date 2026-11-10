using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;

namespace Fdms.Infrastructure.Integrations
{
    /// <summary>
    /// Zoho Books over HTTP: OAuth 2.0 (code, refresh, revoke) at the account's sign-in server, and the Books API v3 at the account's API server.
    /// Zoho answers some errors with HTTP 200 and an "error" or a non-zero "code" in the body, so every answer is checked.
    /// Only Zoho's own servers are ever called: both addresses are checked against Zoho's domains before use.
    /// </summary>
    public sealed class ZohoApi : IZohoApi
    {
        private static readonly Regex AccountsHost = new(@"^https://accounts\.(zoho\.(com|eu|in|com\.au|jp|sa|uk|com\.cn)|zohocloud\.ca)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ApiHost = new(@"^https://(www\.)?zohoapis\.(com|eu|in|com\.au|jp|ca|sa|uk|com\.cn)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Zoho's data centres: key, label, sign-in server, API server.</summary>
        public static readonly IReadOnlyList<(string Key, string Label, string Accounts, string Api)> Regions = new[]
        {
            ("com", "United States, Africa and most countries (zoho.com)", "https://accounts.zoho.com", "https://www.zohoapis.com"),
            ("eu", "Europe (zoho.eu)", "https://accounts.zoho.eu", "https://www.zohoapis.eu"),
            ("in", "India (zoho.in)", "https://accounts.zoho.in", "https://www.zohoapis.in"),
            ("com.au", "Australia (zoho.com.au)", "https://accounts.zoho.com.au", "https://www.zohoapis.com.au"),
            ("jp", "Japan (zoho.jp)", "https://accounts.zoho.jp", "https://www.zohoapis.jp"),
            ("ca", "Canada (zohocloud.ca)", "https://accounts.zohocloud.ca", "https://www.zohoapis.ca"),
            ("sa", "Saudi Arabia (zoho.sa)", "https://accounts.zoho.sa", "https://www.zohoapis.sa"),
            ("uk", "United Kingdom (zoho.uk)", "https://accounts.zoho.uk", "https://www.zohoapis.uk"),
        };

        /// <summary>The Zoho Books web address for an account's sign-in server (accounts.zoho.eu → books.zoho.eu).</summary>
        public static string BooksWeb(string accountsServer) => accountsServer.TrimEnd('/').Replace("https://accounts.", "https://books.");
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly HttpClient _http;
        public ZohoApi(HttpClient http) => _http = http;

        public static bool IsAccountsServer(string? url) => url != null && AccountsHost.IsMatch(url.TrimEnd('/'));
        public static bool IsApiDomain(string? url) => url != null && ApiHost.IsMatch(url.TrimEnd('/'));

        private static string Accounts(string url) => IsAccountsServer(url) ? url.TrimEnd('/') : throw new ZohoException(400, "That is not a Zoho sign-in server.");
        private static string Api(string url) => IsApiDomain(url) ? url.TrimEnd('/') : throw new ZohoException(400, "That is not a Zoho API server.");

        // ---- OAuth ----

        private async Task<ZohoTokens> TokenAsync(string accountsServer, Dictionary<string, string> form, bool refreshing, CancellationToken ct)
        {
            using var res = await _http.PostAsync(Accounts(accountsServer) + "/oauth/v2/token", new FormUrlEncodedContent(form), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            JsonElement j;
            try { j = JsonDocument.Parse(body).RootElement; } catch (JsonException) { throw new ZohoException((int)res.StatusCode, "Zoho's sign-in server gave an unreadable answer."); }
            if (j.TryGetProperty("error", out var err))
            {
                var e = err.GetString() ?? "error";
                // a refresh token that no longer works (revoked, expired, wrong client) needs the person to sign in again
                throw new ZohoException((int)res.StatusCode, "Zoho refused: " + e, unauthorized: refreshing || e is "invalid_code" or "invalid_client" or "invalid_grant");
            }
            if (!res.IsSuccessStatusCode || !j.TryGetProperty("access_token", out var at)) throw new ZohoException((int)res.StatusCode, "Zoho did not give an access token.");
            return new ZohoTokens(at.GetString()!, j.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                j.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var secs) ? secs : 3600, j.TryGetProperty("api_domain", out var ad) ? ad.GetString() : null);
        }

        public Task<ZohoTokens> ExchangeCodeAsync(string accountsServer, string clientId, string clientSecret, string? redirectUri, string code, CancellationToken ct)
        {
            var form = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code };
            if (redirectUri != null) form["redirect_uri"] = redirectUri;   // a Self Client code has none
            return TokenAsync(accountsServer, form, false, ct);
        }

        public Task<ZohoTokens> RefreshAsync(string accountsServer, string clientId, string clientSecret, string refreshToken, CancellationToken ct) =>
            TokenAsync(accountsServer, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["client_secret"] = clientSecret, ["refresh_token"] = refreshToken }, true, ct);

        public async Task RevokeAsync(string accountsServer, string refreshToken, CancellationToken ct)
        {
            using var res = await _http.PostAsync(Accounts(accountsServer) + "/oauth/v2/token/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }), ct);
        }

        // ---- Books API ----

        private async Task<JsonElement> SendAsync(HttpMethod method, string apiDomain, string token, string path, HttpContent? content, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, Api(apiDomain) + "/books/v3/" + path) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", token);
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new ZohoException(429, "Zoho's limit of calls was reached; the sync continues later.", rateLimited: true);
            if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ZohoException(401, "Zoho no longer accepts this connection's access.", unauthorized: true);
            JsonElement j;
            try { j = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone(); }
            catch (JsonException) { throw new ZohoException((int)res.StatusCode, $"Zoho gave an unreadable answer ({(int)res.StatusCode})."); }
            var code = j.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
            if (!res.IsSuccessStatusCode || code != 0)
            {
                var message = j.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new ZohoException((int)res.StatusCode, $"Zoho: {message ?? res.StatusCode.ToString()}" + (code != 0 ? $" (code {code})" : ""), unauthorized: code is 14 or 57);
            }
            return j;
        }

        private static string Org(ZohoAccess a) => "organization_id=" + Uri.EscapeDataString(a.OrganizationId);
        private static string Plural(string kind) => kind == "creditnote" ? "creditnotes" : "invoices";
        private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number ? v.GetRawText() : null;
        private static decimal Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : decimal.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : null, NumberStyles.Number, Inv, out var d) ? d : 0 : 0;
        private static decimal? NumOrNull(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String && decimal.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), NumberStyles.Number, Inv, out var d) ? d : null;
        private static DateTime? When(string? s) => DateTime.TryParse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

        public async Task<IReadOnlyList<ZohoOrganization>> OrganizationsAsync(string apiDomain, string accessToken, CancellationToken ct)
        {
            var j = await SendAsync(HttpMethod.Get, apiDomain, accessToken, "organizations", null, ct);
            return j.TryGetProperty("organizations", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(o => new ZohoOrganization(Str(o, "organization_id") ?? "", Str(o, "name") ?? "", Str(o, "currency_code"))).Where(o => o.Id.Length > 0).ToList()
                : new List<ZohoOrganization>();
        }

        public async Task<ZohoPage> ListAsync(ZohoAccess a, string kind, DateTime? modifiedSince, int page, CancellationToken ct)
        {
            var q = $"{Plural(kind)}?{Org(a)}&page={page}&per_page=200&sort_column=last_modified_time&sort_order=A";
            if (modifiedSince is { } since) q += "&last_modified_time=" + Uri.EscapeDataString(since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + "+0000");
            var j = await SendAsync(HttpMethod.Get, a.ApiDomain, a.AccessToken, q, null, ct);
            var idKey = kind == "creditnote" ? "creditnote_id" : "invoice_id";
            var noKey = kind == "creditnote" ? "creditnote_number" : "invoice_number";
            var items = j.TryGetProperty(Plural(kind), out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(d => new ZohoDocumentHead(Str(d, idKey) ?? "", Str(d, noKey) ?? "", Str(d, "status") ?? "", When(Str(d, "date")), Num(d, "total"),
                    Str(d, "currency_code") ?? "", Str(d, "customer_name"), When(Str(d, "last_modified_time")))).Where(d => d.Id.Length > 0).ToList()
                : new List<ZohoDocumentHead>();
            var more = j.TryGetProperty("page_context", out var pc) && pc.TryGetProperty("has_more_page", out var hm) && hm.ValueKind == JsonValueKind.True;
            return new ZohoPage(items, more);
        }

        public async Task<ZohoDocument> GetAsync(ZohoAccess a, string kind, string id, CancellationToken ct)
        {
            var j = await SendAsync(HttpMethod.Get, a.ApiDomain, a.AccessToken, $"{Plural(kind)}/{Uri.EscapeDataString(id)}?{Org(a)}", null, ct);
            var d = j.GetProperty(kind == "creditnote" ? "creditnote" : "invoice");
            var lines = d.TryGetProperty("line_items", out var li) && li.ValueKind == JsonValueKind.Array
                ? li.EnumerateArray().Select(l =>
                {
                    // the HS code: Zoho's HSN/SAC field, else a line custom field called "HS code"
                    var hs = Str(l, "hsn_or_sac");
                    if (string.IsNullOrWhiteSpace(hs) && l.TryGetProperty("item_custom_fields", out var icf) && icf.ValueKind == JsonValueKind.Array)
                        hs = icf.EnumerateArray().Where(f => (Str(f, "label") ?? "").Replace(" ", "").Contains("HS", StringComparison.OrdinalIgnoreCase)).Select(f => Str(f, "value")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    return new ZohoLine(Str(l, "name") ?? Str(l, "description") ?? "Item", Str(l, "description"), Num(l, "quantity"), Num(l, "rate"), Num(l, "item_total"),
                        NumOrNull(l, "tax_percentage"), Str(l, "tax_name"), string.IsNullOrWhiteSpace(hs) ? null : hs.Trim());
                }).ToList()
                : new List<ZohoLine>();
            var fields = d.TryGetProperty("custom_fields", out var cf) && cf.ValueKind == JsonValueKind.Array
                ? cf.EnumerateArray().Select(f => new ZohoCustomField(Str(f, "customfield_id") ?? "", Str(f, "label") ?? "", Str(f, "value"))).Where(f => f.Id.Length > 0).ToList()
                : new List<ZohoCustomField>();
            string? referenceInvoice = Str(d, "invoice_id") ?? Str(d, "reference_invoice_id");
            if (referenceInvoice == null && d.TryGetProperty("invoices", out var inv) && inv.ValueKind == JsonValueKind.Array)
                referenceInvoice = inv.EnumerateArray().Select(x => Str(x, "invoice_id")).FirstOrDefault(x => x != null);
            var entityDiscount = Str(d, "discount_type") == "entity_level" ? Num(d, "discount_total") : 0;
            return new ZohoDocument(Str(d, kind == "creditnote" ? "creditnote_id" : "invoice_id") ?? id, Str(d, kind == "creditnote" ? "creditnote_number" : "invoice_number") ?? "",
                Str(d, "status") ?? "", When(Str(d, "date")), Str(d, "currency_code") ?? "", Str(d, "customer_id"), Str(d, "customer_name"),
                d.TryGetProperty("is_inclusive_tax", out var it) && it.ValueKind == JsonValueKind.True, Num(d, "total"), entityDiscount,
                Num(d, "shipping_charge"), NumOrNull(d, "shipping_charge_tax_percentage"), Num(d, "adjustment"), lines, fields, kind == "creditnote" ? referenceInvoice : null, Str(d, "notes"));
        }

        public async Task<ZohoContact?> ContactAsync(ZohoAccess a, string contactId, CancellationToken ct)
        {
            var j = await SendAsync(HttpMethod.Get, a.ApiDomain, a.AccessToken, $"contacts/{Uri.EscapeDataString(contactId)}?{Org(a)}", null, ct);
            if (!j.TryGetProperty("contact", out var c)) return null;
            string? Custom(string contains) => c.TryGetProperty("custom_fields", out var cf) && cf.ValueKind == JsonValueKind.Array
                ? cf.EnumerateArray().Where(f => (Str(f, "label") ?? "").Contains(contains, StringComparison.OrdinalIgnoreCase)).Select(f => Str(f, "value")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) : null;
            return new ZohoContact(Str(c, "contact_name") ?? "", Str(c, "company_name"), Custom("TIN") ?? Str(c, "tax_reg_no"), Str(c, "vat_reg_no") ?? Custom("VAT"), Str(c, "email"), Str(c, "phone"));
        }

        public async Task CommentAsync(ZohoAccess a, string kind, string id, string text, CancellationToken ct) =>
            await SendAsync(HttpMethod.Post, a.ApiDomain, a.AccessToken, $"{Plural(kind)}/{Uri.EscapeDataString(id)}/comments?{Org(a)}",
                new StringContent(JsonSerializer.Serialize(new { description = text, show_comment_to_clients = false }), Encoding.UTF8, "application/json"), ct);

        public async Task AttachAsync(ZohoAccess a, string kind, string id, byte[] pdf, string fileName, CancellationToken ct)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(pdf); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "attachment", fileName);
            await SendAsync(HttpMethod.Post, a.ApiDomain, a.AccessToken, $"{Plural(kind)}/{Uri.EscapeDataString(id)}/attachment?{Org(a)}&can_send_in_email=true", form, ct);
        }

        public async Task<ZohoRawResponse> RawAsync(ZohoAccess a, string method, string path, string? query, string? jsonBody, CancellationToken ct)
        {
            var url = Api(a.ApiDomain) + "/books/v3/" + path + "?" + Org(a) + (string.IsNullOrEmpty(query) ? "" : "&" + query);
            using var req = new HttpRequestMessage(new HttpMethod(method), url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Zoho-oauthtoken", a.AccessToken);
            if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new ZohoException(429, "Zoho's limit of calls was reached; try again shortly.", rateLimited: true);
            if (res.StatusCode == HttpStatusCode.Unauthorized) throw new ZohoException(401, "Zoho no longer accepts this connection's access.", unauthorized: true);
            return new ZohoRawResponse((int)res.StatusCode, body);
        }

        public async Task SetCustomFieldsAsync(ZohoAccess a, string kind, string id, IReadOnlyList<(string FieldId, string Value)> values, CancellationToken ct) =>
            await SendAsync(HttpMethod.Put, a.ApiDomain, a.AccessToken, $"{Plural(kind)}/{Uri.EscapeDataString(id)}/customfields?{Org(a)}",
                new StringContent(JsonSerializer.Serialize(values.Select(v => new { customfield_id = v.FieldId, value = v.Value })), Encoding.UTF8, "application/json"), ct);
    }
}
