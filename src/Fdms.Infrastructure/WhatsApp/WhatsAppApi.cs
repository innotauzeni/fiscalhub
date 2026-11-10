using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;

namespace Fdms.Infrastructure.WhatsApp
{
    /// <summary>Meta's WhatsApp Cloud API (graph.facebook.com/{version}/{phone-number-id}/messages). Only Meta's own server is ever called.</summary>
    public sealed class WhatsAppApi : IWhatsAppApi
    {
        private const string Graph = "https://graph.facebook.com";
        private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        private readonly HttpClient _http;
        public WhatsAppApi(HttpClient http) => _http = http;

        private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

        private async Task<JsonElement> PostAsync(WhatsAppConfig c, string path, HttpContent content, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Graph}/{c.ApiVersion}/{Uri.EscapeDataString(c.PhoneNumberId)}/{path}") { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.AccessToken);
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            JsonElement j;
            try { j = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone(); }
            catch (JsonException) { throw new WhatsAppException($"WhatsApp gave an unreadable answer ({(int)res.StatusCode}).", (int)res.StatusCode); }
            if (!res.IsSuccessStatusCode || j.TryGetProperty("error", out _))
            {
                var msg = j.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : res.StatusCode.ToString();
                throw new WhatsAppException("WhatsApp: " + msg, (int)res.StatusCode);
            }
            return j;
        }

        private async Task<string?> MessageAsync(WhatsAppConfig c, object payload, CancellationToken ct)
        {
            var j = await PostAsync(c, "messages", new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"), ct);
            return j.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array && ms.GetArrayLength() > 0 && ms[0].TryGetProperty("id", out var id) ? id.GetString() : null;
        }

        public Task<string?> SendTextAsync(WhatsAppConfig c, string to, string text, CancellationToken ct) =>
            MessageAsync(c, new { messaging_product = "whatsapp", recipient_type = "individual", to, type = "text", text = new { preview_url = false, body = Cut(text, 4096) } }, ct);

        public Task<string?> SendButtonsAsync(WhatsAppConfig c, string to, string body, IReadOnlyList<WhatsAppButton> buttons, CancellationToken ct) =>
            MessageAsync(c, new
            {
                messaging_product = "whatsapp", recipient_type = "individual", to, type = "interactive",
                interactive = new
                {
                    type = "button", body = new { text = Cut(body, 1024) },
                    action = new { buttons = buttons.Take(3).Select(b => new { type = "reply", reply = new { id = Cut(b.Id, 256), title = Cut(b.Title, 20) } }).ToArray() },
                },
            }, ct);

        public Task<string?> SendListAsync(WhatsAppConfig c, string to, string body, string button, IReadOnlyList<WhatsAppListRow> rows, CancellationToken ct) =>
            MessageAsync(c, new
            {
                messaging_product = "whatsapp", recipient_type = "individual", to, type = "interactive",
                interactive = new
                {
                    type = "list", body = new { text = Cut(body, 1024) },
                    action = new
                    {
                        button = Cut(button, 20),
                        sections = new[] { new { title = "Choose", rows = rows.Take(10).Select(r => new { id = Cut(r.Id, 200), title = Cut(r.Title, 24), description = r.Description == null ? null : Cut(r.Description, 72) }).ToArray() } },
                    },
                },
            }, ct);

        public async Task<string?> SendDocumentAsync(WhatsAppConfig c, string to, byte[] file, string mimeType, string fileName, string? caption, CancellationToken ct)
        {
            // the file is uploaded to WhatsApp first, then sent by its media id
            var form = new MultipartFormDataContent { { new StringContent("whatsapp"), "messaging_product" }, { new StringContent(mimeType), "type" } };
            var part = new ByteArrayContent(file); part.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            form.Add(part, "file", fileName);
            var up = await PostAsync(c, "media", form, ct);
            var mediaId = up.TryGetProperty("id", out var id) ? id.GetString() : null;
            if (string.IsNullOrEmpty(mediaId)) throw new WhatsAppException("WhatsApp did not accept the file.");
            return await MessageAsync(c, new { messaging_product = "whatsapp", recipient_type = "individual", to, type = "document",
                document = new { id = mediaId, filename = fileName, caption = caption == null ? null : Cut(caption, 1024) } }, ct);
        }

        public Task<string?> SendTemplateAsync(WhatsAppConfig c, string to, string template, string language, IReadOnlyList<string> bodyParameters, CancellationToken ct) =>
            MessageAsync(c, new
            {
                messaging_product = "whatsapp", to, type = "template",
                template = new
                {
                    name = template, language = new { code = language },
                    components = bodyParameters.Count == 0 ? null : new[] { new { type = "body", parameters = bodyParameters.Select(p => new { type = "text", text = Cut(p.Replace('\n', ' '), 1000) }).ToArray() } },
                },
            }, ct);

        public async Task MarkReadAsync(WhatsAppConfig c, string messageId, CancellationToken ct) =>
            await PostAsync(c, "messages", new StringContent(JsonSerializer.Serialize(new { messaging_product = "whatsapp", status = "read", message_id = messageId }), Encoding.UTF8, "application/json"), ct);
    }
}
