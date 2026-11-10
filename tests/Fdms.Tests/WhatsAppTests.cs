using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>WhatsApp in memory: every message the bot would send.</summary>
    public sealed class FakeWhatsAppApi : IWhatsAppApi
    {
        public sealed record Sent(string To, string Kind, string Text, IReadOnlyList<string> Options);
        public ConcurrentQueue<Sent> Messages { get; } = new();
        private Task<string?> Add(string to, string kind, string text, IEnumerable<string>? options = null)
        { Messages.Enqueue(new Sent(to, kind, text, (options ?? Array.Empty<string>()).ToList())); return Task.FromResult<string?>("wamid." + Guid.NewGuid().ToString("N")); }

        public Task<string?> SendTextAsync(WhatsAppConfig c, string to, string text, CancellationToken ct) => Add(to, "text", text);
        public Task<string?> SendButtonsAsync(WhatsAppConfig c, string to, string body, IReadOnlyList<WhatsAppButton> buttons, CancellationToken ct) => Add(to, "buttons", body, buttons.Select(b => b.Id));
        public Task<string?> SendListAsync(WhatsAppConfig c, string to, string body, string button, IReadOnlyList<WhatsAppListRow> rows, CancellationToken ct) => Add(to, "list", body, rows.Select(r => r.Id));
        public Task<string?> SendDocumentAsync(WhatsAppConfig c, string to, byte[] file, string mimeType, string fileName, string? caption, CancellationToken ct) =>
            Add(to, file.Length > 1000 && file[0] == (byte)'%' ? "pdf" : "document", fileName);
        public Task<string?> SendTemplateAsync(WhatsAppConfig c, string to, string template, string language, IReadOnlyList<string> bodyParameters, CancellationToken ct) => Add(to, "template:" + template, string.Join(" | ", bodyParameters));
        public Task MarkReadAsync(WhatsAppConfig c, string messageId, CancellationToken ct) => Task.CompletedTask;

        public List<Sent> To(string phone) => Messages.Where(m => m.To == phone).ToList();
    }

    public sealed class WhatsAppFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        public FakeWhatsAppApi WhatsApp { get; } = new();
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly string _keys = Path.Combine(Path.GetTempPath(), "fdms-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestKit.JwtKey, ["Seed:AdminEmail"] = TestKit.AdminEmail, ["Seed:AdminPassword"] = TestKit.AdminPassword, ["Seed:ForcePasswordChange"] = "false",
                ["DataProtection:KeysPath"] = _keys, ["RateLimit:PerMinute"] = "100000", ["RateLimit:LoginPerMinute"] = "100000", ["Fdms:DistributedLocks"] = "false", ["Auth:ActiveCacheSeconds"] = "0",
            }));
            builder.ConfigureServices(s =>
            {
                TestDb.Use(s, _db);
                s.RemoveAll<IFdmsGateway>(); s.AddSingleton<IFdmsGateway>(Fdms);
                s.RemoveAll<IWhatsAppApi>(); s.AddSingleton<IWhatsAppApi>(WhatsApp);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    /// <summary>The WhatsApp assistant: linking a number, a sale from first message to fiscal PDF, strangers, alerts and the webhook's signature.</summary>
    public class WhatsAppTests : IClassFixture<WhatsAppFactory>
    {
        private const string Secret = "test-app-secret";
        private readonly WhatsAppFactory _f;
        private static int _seq = 91000;
        private static int _msg;
        public WhatsAppTests(WhatsAppFactory f) => _f = f;

        private async Task SetUpAsync()
        {
            var op = await AdminAsync(_f);
            var res = await op.PutAsJsonAsync("/api/v1/admin/whatsapp", new { enabled = true, phoneNumberId = "1234567890123", businessAccountId = "998877", displayNumber = "+263 77 000 0001",
                accessToken = "test-token", appSecret = Secret, alertTemplate = "fiscalhub_alert", alertTemplateLanguage = "en", apiVersion = "v21.0", rotateVerifyToken = false });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        private static string Payload(string from, string? text, string? replyId = null)
        {
            var id = "wamid.test" + Interlocked.Increment(ref _msg);
            object message = replyId == null
                ? new { from, id, timestamp = "1760000000", type = "text", text = new { body = text } }
                : new { from, id, timestamp = "1760000000", type = "interactive", interactive = new { type = "button_reply", button_reply = new { id = replyId, title = text ?? replyId } } };
            return JsonSerializer.Serialize(new { @object = "whatsapp_business_account", entry = new[] { new { id = "998877", changes = new[] { new { field = "messages",
                value = new { messaging_product = "whatsapp", metadata = new { display_phone_number = "263770000001", phone_number_id = "1234567890123" }, messages = new[] { message } } } } } } });
        }

        private static string Sign(string body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        /// <summary>Delivers one message as Meta would and lets the worker answer it; returns what the bot said back.</summary>
        private async Task<List<FakeWhatsAppApi.Sent>> SayAsync(string from, string? text, string? replyId = null)
        {
            var before = _f.WhatsApp.To(from).Count;
            var body = Payload(from, text, replyId);
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/whatsapp/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Hub-Signature-256", Sign(body));
            Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().SendAsync(req)).StatusCode);
            using (var scope = _f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<IWhatsAppService>().ProcessPendingAsync(default);
            return _f.WhatsApp.To(from).Skip(before).ToList();
        }

        [Fact]
        public async Task Staff_link_their_number_and_fiscalise_a_sale_by_chatting()
        {
            await SetUpAsync();
            var co = await NewCompanyAsync(_f, "WhatsApp Shop");
            await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-WA");
            const string phone = "263771112233";

            // a number nobody linked is told how to link it, and nothing else happens
            var stranger = await SayAsync(phone, "hi");
            Assert.Contains("not linked", Assert.Single(stranger).Text);

            var link = await (await co.Admin.PostAsync("/api/v1/profile/whatsapp/link", null)).Json();
            var code = link.GetProperty("code").GetString()!;
            Assert.Contains("wa.me/263770000001", link.GetProperty("waLink").GetString());
            Assert.Equal(HttpStatusCode.OK, (await SayAsync(phone, "LINK 000000")).Count == 1 ? HttpStatusCode.OK : HttpStatusCode.BadRequest);   // a wrong code
            var linked = await SayAsync(phone, "LINK " + code);
            Assert.Contains(linked, m => m.Text.Contains("linked"));
            var menu = linked.Last();
            Assert.Equal("list", menu.Kind);
            Assert.Contains("m:quick", menu.Options);
            Assert.Equal("263771112233", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/profile/whatsapp", J)).GetProperty("linkedNumber").GetString());

            // quick sale: the only device is used, then currency, an item typed in full, VAT, HS code, payment, confirm
            Assert.Contains("cur:USD", (await SayAsync(phone, "Quick sale", "m:quick")).Last().Options);
            Assert.Contains("Add the first item", (await SayAsync(phone, "USD", "cur:USD")).Last().Text);
            Assert.Contains("tax:15", (await SayAsync(phone, "2 x Bread @ 1.50")).Last().Options);
            Assert.Contains("HS code", (await SayAsync(phone, "VAT 15%", "tax:15")).Last().Text);
            var lines = (await SayAsync(phone, "19059000")).Last();
            Assert.Contains("3.00 USD", lines.Text);
            Assert.Contains("l:done", lines.Options);
            Assert.Contains("pay:Cash", (await SayAsync(phone, "Done", "l:done")).Last().Options);
            var preview = (await SayAsync(phone, "Cash", "pay:Cash")).Last();
            Assert.Contains("FISCAL TAX INVOICE", preview.Text);
            Assert.Contains("Total: 3.00 USD", preview.Text);
            Assert.Contains("ok:go", preview.Options);

            var done = await SayAsync(phone, "Fiscalise", "ok:go");
            Assert.Contains(done, m => m.Text.Contains("Fiscalised") && m.Text.Contains("FISCAL TAX INVOICE") && m.Text.Contains("Verification code"));
            Assert.Contains(done, m => m.Kind == "pdf");

            var receipts = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/receipts?pageSize=10", J);
            var r = receipts.GetProperty("items").EnumerateArray().Single();
            Assert.Equal(3.00m, r.GetProperty("total").GetDecimal());
            Assert.StartsWith("WA", r.GetProperty("invoiceNo").GetString());
            // the audit trail names the person, not "system"
            var op = await AdminAsync(_f);
            var audit = await AuditAsync(_f, co.ClientId, "action=receipt.submit");
            Assert.Contains(co.AdminEmail, audit.GetRawText());

            // today's sales and recent receipts
            Assert.Contains("3.00 USD", (await SayAsync(phone, "Today", "m:today")).Last().Text);
            var recent = (await SayAsync(phone, "Recent", "m:recent")).Last();
            Assert.Single(recent.Options);
            Assert.Contains(await SayAsync(phone, "pick", recent.Options[0]), m => m.Kind == "pdf");

            // "cancel" leaves a half-built sale
            await SayAsync(phone, "Quick sale", "m:quick");
            Assert.Contains("Cancelled", (await SayAsync(phone, "cancel")).First().Text);

            // alerts: queued for the linked person and sent as a normal message inside WhatsApp's 24-hour window
            using (var scope = _f.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<INotificationService>().RaiseAsync(co.ClientId, "rejected", "bad", "wa-test-" + Guid.NewGuid(), "Receipt INV-9 was rejected", "Fix the HS code.", "/receipts", default, email: false);
            var before = _f.WhatsApp.To(phone).Count;
            using (var scope = _f.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<IWhatsAppService>().ProcessPendingAsync(default);
            Assert.Contains(_f.WhatsApp.To(phone).Skip(before), m => m.Kind == "text" && m.Text.Contains("Receipt INV-9 was rejected"));

            // unlinking: the number is a stranger again
            Assert.Equal(JsonValueKind.Null, (await (await co.Admin.DeleteAsync("/api/v1/profile/whatsapp")).Json()).TryGetProperty("linkedNumber", out var ln) ? ln.ValueKind : JsonValueKind.Null);
            Assert.Contains("not linked", (await SayAsync(phone, "menu")).Single().Text);
        }

        [Fact]
        public async Task The_webhook_only_accepts_what_meta_signed_and_answers_metas_check()
        {
            await SetUpAsync();
            var op = await AdminAsync(_f);
            var settings = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/whatsapp", J);
            Assert.True(settings.GetProperty("configured").GetBoolean());
            Assert.DoesNotContain("test-token", settings.GetRawText());
            Assert.DoesNotContain(Secret, settings.GetRawText());
            var verify = settings.GetProperty("verifyToken").GetString()!;
            Assert.EndsWith("/api/v1/whatsapp/webhook", settings.GetProperty("webhookUrl").GetString());

            var anon = _f.CreateClient();
            Assert.Equal("12345", await anon.GetStringAsync($"/api/v1/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={verify}&hub.challenge=12345"));
            Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync("/api/v1/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1")).StatusCode);

            var body = Payload("263779999999", "hi");
            var forged = new HttpRequestMessage(HttpMethod.Post, "/api/v1/whatsapp/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            forged.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('0', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(forged)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/v1/whatsapp/webhook", new StringContent(body))).StatusCode);

            // incomplete settings cannot be switched on; a company cannot reach them
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/admin/whatsapp", new { enabled = true, phoneNumberId = "+263 77", displayNumber = "x", rotateVerifyToken = false })).StatusCode);
            var co = await NewCompanyAsync(_f, "WhatsApp Nosy");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/admin/whatsapp")).StatusCode);
        }
    }
}
