using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>SMS providers in memory: every SMS that would be sent, and through which account.</summary>
    public sealed class FakeSmsApi : ISmsApi
    {
        public sealed record Sent(Guid AccountId, string Provider, string To, string Text);
        public ConcurrentQueue<Sent> Messages { get; } = new();
        public Task<SmsSendResult> SendAsync(SmsConfig c, string to, string text, CancellationToken ct)
        {
            Messages.Enqueue(new Sent(c.AccountId, c.Provider, to, text));
            return Task.FromResult(SmsSendResult.Sent("sms-" + Messages.Count));
        }
        public List<Sent> To(string phone) => Messages.Where(m => m.To == phone).ToList();
    }

    public sealed class MessagingFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        public FakeWhatsAppApi WhatsApp { get; } = new();
        public FakeSmsApi Sms { get; } = new();
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
                s.RemoveAll<ISmsApi>(); s.AddSingleton<ISmsApi>(Sms);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    /// <summary>SMS through the platform's or a company's own provider, and a company's own WhatsApp number for its staff and its customers.</summary>
    public class MessagingTests : IClassFixture<MessagingFactory>
    {
        private readonly MessagingFactory _f;
        private static int _seq = 93000;
        private static int _msg;
        public MessagingTests(MessagingFactory f) => _f = f;

        private static object Sms(bool enabled, string provider, string? apiKey = null, string? username = null, string? apiSecret = null, string? senderId = "FiscalHub", string? apiUrl = null, bool allowCompanies = true) =>
            new { enabled, provider, apiKey, username, apiSecret, senderId, apiUrl, allowCompanies };

        private async Task<Guid> SaleAsync(HttpClient c, Guid dev, string no, string? buyerPhone)
        {
            var res = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = no, invoiceNo = no, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, autoOpenDay = true,
                lines = new[] { new { name = "Shoes", hsCode = "64039900", quantity = 1, unitPrice = 115, taxPercent = 15 } },
                buyer = buyerPhone == null ? null : new { registerName = "Moyo Traders", tin = "2001234567", phone = buyerPhone },
            });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return (await res.Json()).GetProperty("id").GetGuid();
        }

        private async Task ProcessAsync()
        {
            using var scope = _f.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISmsService>().ProcessPendingAsync(default);
            await scope.ServiceProvider.GetRequiredService<IWhatsAppService>().ProcessPendingAsync(default);
        }

        [Fact]
        public async Task Invoices_alerts_and_codes_go_by_SMS_through_the_company_account_first_then_the_platform()
        {
            var op = await AdminAsync(_f);
            // the platform's account: everything a provider needs, or it cannot be switched on; secrets are never shown again
            var incomplete = await op.PutAsJsonAsync("/api/v1/admin/sms", Sms(true, "NHUME"));
            Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
            Assert.Contains("SMS_INCOMPLETE", await incomplete.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/admin/sms", Sms(true, "ESOLUTIONS", username: "u", apiSecret: "p", apiUrl: "http://plain.example.test"))).StatusCode);
            var platform = await (await op.PutAsJsonAsync("/api/v1/admin/sms", Sms(true, "NHUME", apiKey: "nhume-test-key"))).Json();
            Assert.True(platform.GetProperty("configured").GetBoolean());
            Assert.True(platform.GetProperty("hasApiKey").GetBoolean());
            Assert.DoesNotContain("nhume-test-key", platform.GetRawText());

            var co = await NewCompanyAsync(_f, "Sms Shop");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/admin/sms")).StatusCode);
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-SMS");
            var receipt = await SaleAsync(co.Admin, dev, "SMS-1", "077 123 4567");

            // no own account: the platform's sends, to the buyer's phone on the invoice, with the link to view and verify it
            var sent = await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{receipt}/sms", new { })).Json();
            Assert.True(sent.GetProperty("sent").GetBoolean(), sent.GetRawText());
            var sms = _f.Sms.To("263771234567").Last();
            Assert.Equal("NHUME", sms.Provider);
            Assert.Contains("FISCAL TAX INVOICE SMS-1", sms.Text);
            Assert.Contains("/share/", sms.Text + "/share/");   // the share link is in the text
            Assert.Contains("http", sms.Text);

            // the company's own account (eSolutions) takes over
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/messaging/sms", Sms(true, "ESOLUTIONS", username: "shop"))).StatusCode);
            var own = await (await co.Admin.PutAsJsonAsync("/api/v1/messaging/sms", Sms(true, "ESOLUTIONS", username: "shop", apiSecret: "pw", apiUrl: "https://sms.esolutions.example.test/api", senderId: "SMSSHOP"))).Json();
            Assert.True(own.GetProperty("configured").GetBoolean());
            await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{receipt}/sms", new { phone = "+263 78 000 1111" });
            Assert.Equal("ESOLUTIONS", _f.Sms.To("263780001111").Single().Provider);
            Assert.Contains((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/messaging/sms/log", J)).EnumerateArray(), m => m.GetProperty("kind").GetString() == "invoice");

            // own account off and the platform no longer shares: nothing is sent, and the answer says why
            await co.Admin.PutAsJsonAsync("/api/v1/messaging/sms", Sms(false, "ESOLUTIONS"));
            await op.PutAsJsonAsync("/api/v1/admin/sms", Sms(true, "NHUME", allowCompanies: false));
            var none = await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{receipt}/sms", new { phone = "+263 78 000 2222" })).Json();
            Assert.False(none.GetProperty("configured").GetBoolean());
            Assert.Empty(_f.Sms.To("263780002222"));
            await op.PutAsJsonAsync("/api/v1/admin/sms", Sms(true, "NHUME", allowCompanies: true));

            // alerts: only for people who switch them on, with a phone number on their profile
            var noPhone = await co.Admin.PutAsJsonAsync("/api/v1/profile/sms", new { alerts = true });
            Assert.Equal(HttpStatusCode.BadRequest, noPhone.StatusCode);
            Assert.True((await co.Admin.PutAsJsonAsync("/api/v1/profile", new { name = "Ada", surname = "Admin", phone = "+263 71 555 0101" })).IsSuccessStatusCode);
            var prefs = await (await co.Admin.PutAsJsonAsync("/api/v1/profile/sms", new { alerts = true })).Json();
            Assert.True(prefs.GetProperty("alerts").GetBoolean());
            Assert.True(prefs.GetProperty("available").GetBoolean());
            using (var scope = _f.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<INotificationService>().RaiseAsync(co.ClientId, "rejected", "bad", "sms-test-" + Guid.NewGuid(), "Receipt INV-9 was rejected", "Fix the HS code.", "/receipts", default, email: false);
            await ProcessAsync();
            Assert.Contains(_f.Sms.To("263715550101"), m => m.Text.Contains("Receipt INV-9 was rejected"));

            // a forgotten password: the code also goes by SMS, and the log keeps it masked
            Assert.True((await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = co.AdminEmail })).IsSuccessStatusCode);
            var code = _f.Sms.To("263715550101").Last();
            Assert.Matches(@"reset code is \d{6}", code.Text);
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
                Assert.Contains(await db.SmsMessages.Where(m => m.Kind == "code" && m.To == "263715550101").Select(m => m.Body).ToListAsync(), b => b.Contains("******"));
            }
        }

        // ---- a company's own WhatsApp number ----

        private const string CoSecret = "company-app-secret";

        private static string Payload(string phoneNumberId, string from, string? text, string? replyId = null)
        {
            var id = "wamid.co" + Interlocked.Increment(ref _msg);
            object message = replyId == null
                ? new { from, id, timestamp = "1760000000", type = "text", text = new { body = text } }
                : new { from, id, timestamp = "1760000000", type = "interactive", interactive = new { type = "list_reply", list_reply = new { id = replyId, title = text ?? replyId } } };
            return JsonSerializer.Serialize(new { @object = "whatsapp_business_account", entry = new[] { new { id = "1", changes = new[] { new { field = "messages",
                value = new { messaging_product = "whatsapp", metadata = new { display_phone_number = "263779990000", phone_number_id = phoneNumberId }, messages = new[] { message } } } } } } });
        }

        private async Task<List<FakeWhatsAppApi.Sent>> SayAsync(Guid account, string from, string? text, string? replyId = null, string secret = CoSecret)
        {
            var before = _f.WhatsApp.To(from).Count;
            var body = Payload("5550001112", from, text, replyId);
            var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/whatsapp/webhook/{account:N}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
            var res = await _f.CreateClient().SendAsync(req);
            if (secret != CoSecret) { Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode); return new(); }
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            await ProcessAsync();
            return _f.WhatsApp.To(from).Skip(before).ToList();
        }

        [Fact]
        public async Task A_company_runs_the_assistant_on_its_own_number_for_its_staff_and_its_customers_get_only_their_own_invoices()
        {
            var co = await NewCompanyAsync(_f, "Own Number Shop");
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-OWN");

            // the company fills in its Meta details; its webhook address and verify token are its own
            var first = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/messaging/whatsapp", J);
            var webhook = first.GetProperty("webhookUrl").GetString()!;
            var verify = first.GetProperty("verifyToken").GetString()!;
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/messaging/whatsapp", new { enabled = true, phoneNumberId = "5550001112", customersEnabled = true })).StatusCode);
            var saved = await (await co.Admin.PutAsJsonAsync("/api/v1/messaging/whatsapp", new { enabled = true, phoneNumberId = "5550001112", businessAccountId = "77", displayNumber = "+263 77 999 0000",
                accessToken = "company-token", appSecret = CoSecret, alertTemplate = "shop_alert", alertTemplateLanguage = "en", apiVersion = "v21.0", rotateVerifyToken = false, customersEnabled = true, customerGreeting = (string?)null })).Json();
            Assert.True(saved.GetProperty("configured").GetBoolean(), saved.GetRawText());
            Assert.DoesNotContain("company-token", saved.GetRawText());
            var account = Guid.Parse(webhook[(webhook.LastIndexOf('/') + 1)..]);
            Assert.Equal("ok-123", await _f.CreateClient().GetStringAsync($"/api/v1/whatsapp/webhook/{account:N}?hub.mode=subscribe&hub.verify_token={verify}&hub.challenge=ok-123"));
            Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClient().GetAsync($"/api/v1/whatsapp/webhook/{account:N}?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=x")).StatusCode);

            // another company cannot take the same number
            var other = await NewCompanyAsync(_f, "Number Thief");
            Assert.Equal(HttpStatusCode.Conflict, (await other.Admin.PutAsJsonAsync("/api/v1/messaging/whatsapp", new { enabled = false, phoneNumberId = "5550001112", customersEnabled = true, rotateVerifyToken = false })).StatusCode);

            // a call not signed with the company's app secret is refused
            await SayAsync(account, "263770000009", "hi", secret: "someone-else");

            // staff link to the company's own number and get the assistant there
            var link = await (await co.Admin.PostAsync("/api/v1/profile/whatsapp/link", null)).Json();
            Assert.Contains("wa.me/263779990000", link.GetProperty("waLink").GetString());
            const string staff = "263771230000";
            var linked = await SayAsync(account, staff, "LINK " + link.GetProperty("code").GetString());
            Assert.Contains(linked, m => m.Text.Contains("linked"));
            Assert.Contains("m:quick", linked.Last().Options);

            // a staff member of another company is a stranger here: their link code does not work on this number
            var op = await AdminAsync(_f);   // the other company has no number of its own, so it links on the platform's
            Assert.True((await op.PutAsJsonAsync("/api/v1/admin/whatsapp", new { enabled = true, phoneNumberId = "1234567890999", businessAccountId = "1", displayNumber = "+263 77 000 0009",
                accessToken = "platform-token", appSecret = "platform-secret", alertTemplate = (string?)null, alertTemplateLanguage = "en", apiVersion = "v21.0", rotateVerifyToken = false })).IsSuccessStatusCode);
            var otherLink = await (await other.Admin.PostAsync("/api/v1/profile/whatsapp/link", null)).Json();
            var refused = await SayAsync(account, "263771239999", "LINK " + otherLink.GetProperty("code").GetString());
            Assert.Contains("not valid", refused.First().Text);

            // customers: their own invoices, matched by the phone number on the invoice, and nobody else's
            var mineId = await SaleAsync(co.Admin, dev, "OWN-1", "+263 77 444 5566");
            await SaleAsync(co.Admin, dev, "OWN-2", "0779998877");
            const string customer = "263774445566";
            var hello = (await SayAsync(account, customer, "hi")).Last();
            Assert.Contains("Own Number Shop", hello.Text);
            Assert.Contains("k:mine", hello.Options);
            var list = (await SayAsync(account, customer, "My invoices", "k:mine")).Last();
            Assert.Equal(new[] { "k:r:" + mineId }, list.Options);
            var pdf = await SayAsync(account, customer, "OWN-1", "k:r:" + mineId);
            Assert.Contains(pdf, m => m.Text.Contains("FISCAL TAX INVOICE *OWN-1*"));
            Assert.Contains(pdf, m => m.Kind == "pdf");
            Assert.Contains("not available", (await SayAsync(account, "263771112299", "x", "k:r:" + mineId)).First().Text);   // someone else asking for it
            await SayAsync(account, customer, "Find an invoice", "k:find");
            Assert.Contains("No invoice OWN-2", (await SayAsync(account, customer, "OWN-2")).First().Text);

            // "talk to us" tells the company
            await SayAsync(account, customer, "Talk to us", "k:talk");
            var notes = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J);
            Assert.Contains("+263774445566", notes.GetRawText());

            // the company's alerts go out from its own number
            using (var scope = _f.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<INotificationService>().RaiseAsync(co.ClientId, "rejected", "bad", "own-wa-" + Guid.NewGuid(), "Receipt X was rejected", null, "/receipts", default, email: false);
                var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
                Assert.Equal(account, (await db.WhatsAppMessages.Where(m => m.Kind == "alert" && m.Phone == staff).OrderByDescending(m => m.CreatedAt).FirstAsync()).AccountId);
            }

            // customers can be switched off: then a stranger only hears how staff link
            await co.Admin.PutAsJsonAsync("/api/v1/messaging/whatsapp", new { enabled = true, phoneNumberId = "5550001112", businessAccountId = "77", displayNumber = "+263 77 999 0000",
                alertTemplate = "shop_alert", alertTemplateLanguage = "en", apiVersion = "v21.0", rotateVerifyToken = false, customersEnabled = false });
            Assert.Contains("not linked", (await SayAsync(account, "263775550000", "hi")).First().Text);
        }
    }
}
