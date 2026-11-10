using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Infrastructure.Integrations;
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
    /// <summary>Zoho Books in memory: organisations, invoices, credit notes and contacts, and a record of everything the gateway wrote back.</summary>
    public sealed class FakeZohoApi : IZohoApi
    {
        public const string Accounts = "https://accounts.zoho.com";
        public const string Api = "https://www.zohoapis.com";
        public List<ZohoOrganization> Organizations { get; } = new() { new("700001", "Moyo Retail", "USD"), new("700002", "Moyo Wholesale", "USD") };
        public ConcurrentDictionary<(string Org, string Kind, string Id), ZohoDocument> Docs { get; } = new();
        public ConcurrentDictionary<(string Org, string Kind, string Id), DateTime> Modified { get; } = new();
        public ConcurrentDictionary<string, ZohoContact> Contacts { get; } = new();
        public ConcurrentBag<(string Kind, string Id, string Text)> Comments { get; } = new();
        public ConcurrentBag<(string Id, byte[] Pdf, string Name)> Attachments { get; } = new();
        public ConcurrentBag<(string Id, string FieldId, string Value)> Fields { get; } = new();
        public volatile bool RefreshRevoked;
        public int Refreshes;
        public string? LastRedirectUri;

        public void Put(string org, string kind, ZohoDocument d, DateTime? modified = null)
        {
            Docs[(org, kind, d.Id)] = d;
            Modified[(org, kind, d.Id)] = modified ?? DateTime.UtcNow;
        }

        public ConcurrentQueue<string> ClientIdsUsed { get; } = new();
        public Task<ZohoTokens> ExchangeCodeAsync(string accountsServer, string clientId, string clientSecret, string? redirectUri, string code, CancellationToken ct)
        {
            LastRedirectUri = redirectUri;
            ClientIdsUsed.Enqueue(clientId);
            if (code != "good-code" || clientSecret is not ("test-secret" or "own-secret")) throw new ZohoException(400, "Zoho refused: invalid_code", unauthorized: true);
            return Task.FromResult(new ZohoTokens("access-" + Guid.NewGuid().ToString("N"), "refresh-1", 3600, Api));
        }

        public Task<ZohoTokens> RefreshAsync(string accountsServer, string clientId, string clientSecret, string refreshToken, CancellationToken ct)
        {
            Interlocked.Increment(ref Refreshes);
            ClientIdsUsed.Enqueue(clientId);
            if (RefreshRevoked) throw new ZohoException(400, "Zoho refused: invalid_grant", unauthorized: true);
            return Task.FromResult(new ZohoTokens("access-" + Guid.NewGuid().ToString("N"), null, 3600, null));
        }

        public Task RevokeAsync(string accountsServer, string refreshToken, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ZohoOrganization>> OrganizationsAsync(string apiDomain, string accessToken, CancellationToken ct) => Task.FromResult<IReadOnlyList<ZohoOrganization>>(Organizations);

        public Task<ZohoPage> ListAsync(ZohoAccess a, string kind, DateTime? modifiedSince, int page, CancellationToken ct)
        {
            var items = Docs.Where(x => x.Key.Org == a.OrganizationId && x.Key.Kind == kind && (modifiedSince == null || Modified[x.Key] >= modifiedSince))
                .Select(x => new ZohoDocumentHead(x.Value.Id, x.Value.Number, x.Value.Status, x.Value.Date, x.Value.Total, x.Value.Currency, x.Value.CustomerName, Modified[x.Key]))
                .OrderBy(x => x.LastModified).ToList();
            return Task.FromResult(new ZohoPage(items.Skip((page - 1) * 200).Take(200).ToList(), items.Count > page * 200));
        }

        public Task<ZohoDocument> GetAsync(ZohoAccess a, string kind, string id, CancellationToken ct) =>
            Docs.TryGetValue((a.OrganizationId, kind, id), out var d) ? Task.FromResult(d) : throw new ZohoException(404, "Zoho: not found");

        public Task<ZohoContact?> ContactAsync(ZohoAccess a, string contactId, CancellationToken ct) => Task.FromResult(Contacts.TryGetValue(contactId, out var c) ? c : null);
        public Task CommentAsync(ZohoAccess a, string kind, string id, string text, CancellationToken ct) { Comments.Add((kind, id, text)); return Task.CompletedTask; }
        public Task AttachAsync(ZohoAccess a, string kind, string id, byte[] pdf, string fileName, CancellationToken ct) { Attachments.Add((id, pdf, fileName)); return Task.CompletedTask; }
        /// <summary>Raw calls the bridge passed through, and the answer to give per "METHOD path" (default: an empty list with code 0).</summary>
        public ConcurrentBag<(string Org, string Method, string Path, string Query, string? Body)> RawCalls { get; } = new();
        public ConcurrentDictionary<string, Func<string, ZohoRawResponse>> RawAnswers { get; } = new();
        public Task<ZohoRawResponse> RawAsync(ZohoAccess a, string method, string path, string? query, string? jsonBody, CancellationToken ct)
        {
            RawCalls.Add((a.OrganizationId, method, path, query ?? "", jsonBody));
            return Task.FromResult(RawAnswers.TryGetValue($"{method} {path}", out var f) ? f(query ?? "") : new ZohoRawResponse(200, "{\"code\":0,\"message\":\"success\"}"));
        }

        public Task SetCustomFieldsAsync(ZohoAccess a, string kind, string id, IReadOnlyList<(string FieldId, string Value)> values, CancellationToken ct)
        {
            foreach (var v in values) Fields.Add((id, v.FieldId, v.Value));
            return Task.CompletedTask;
        }
    }

    /// <summary>Records what would be sent to partners' webhooks; Fail makes the next sends fail.</summary>
    public sealed class RecordingBridgeSender : IBridgeSender
    {
        public ConcurrentQueue<(string Url, string Type, long Id, string Timestamp, string Signature, string Body)> Sent { get; } = new();
        public volatile bool Fail;
        public Task<string?> SendAsync(string url, string eventType, long deliveryId, string timestamp, string signature, string body, CancellationToken ct)
        {
            if (Fail) return Task.FromResult<string?>("HTTP 503");
            Sent.Enqueue((url, eventType, deliveryId, timestamp, signature, body));
            return Task.FromResult<string?>(null);
        }
    }

    public sealed class ZohoFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        public FakeZohoApi Zoho { get; } = new();
        public RecordingEmail Email { get; } = new();
        public RecordingBridgeSender Bridge { get; } = new();
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly string _keys = Path.Combine(Path.GetTempPath(), "fdms-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestKit.JwtKey, ["Seed:AdminEmail"] = TestKit.AdminEmail, ["Seed:AdminPassword"] = TestKit.AdminPassword, ["Seed:ForcePasswordChange"] = "false",
                ["DataProtection:KeysPath"] = _keys, ["RateLimit:PerMinute"] = "100000", ["RateLimit:LoginPerMinute"] = "100000", ["Fdms:DistributedLocks"] = "false", ["Auth:ActiveCacheSeconds"] = "0",
                ["Webhooks:AllowInsecureTargets"] = "false",
                ["Zoho:ClientId"] = "1000.TESTCLIENTID", ["Zoho:ClientSecret"] = "test-secret",
            }));
            builder.ConfigureServices(s =>
            {
                TestDb.Use(s, _db);
                s.RemoveAll<IFdmsGateway>(); s.AddSingleton<IFdmsGateway>(Fdms);
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(Email);
                s.RemoveAll<IZohoApi>(); s.AddSingleton<IZohoApi>(Zoho);
                s.RemoveAll<IBridgeSender>(); s.AddSingleton<IBridgeSender>(Bridge);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    /// <summary>Zoho Books: signing in, choosing the organisation, pulling documents, fiscalising them, writing the result back, token health, the webhook and what it costs.</summary>
    public class ZohoTests : IClassFixture<ZohoFactory>
    {
        private readonly ZohoFactory _f;
        private static int _seq = 71000;
        public ZohoTests(ZohoFactory f) => _f = f;

        private static ZohoLine Line(string name, decimal qty, decimal rate, decimal? tax = 15, string? hs = "33049900", decimal? total = null) =>
            new(name, null, qty, rate, total ?? qty * rate, tax, tax == null ? "VAT Exempt" : $"VAT {tax}%", hs);

        private static ZohoDocument Invoice(string id, string number, decimal total, IReadOnlyList<ZohoLine> lines, string status = "sent", DateTime? date = null, string? customer = "C1",
            IReadOnlyList<ZohoCustomField>? fields = null) =>
            new(id, number, status, date ?? DateTime.UtcNow.Date, "USD", customer, "Glow Salon", true, total, 0, 0, null, 0, lines,
                fields ?? new[] { new ZohoCustomField("cf1", "Fiscal Number", null), new ZohoCustomField("cf2", "Verification Code", null), new ZohoCustomField("cf3", "Customer ref", "x") }, null, null);

        private async Task<(Company Co, Guid Device)> ConnectedAsync(string name, string org = "700001")
        {
            var co = await NewCompanyAsync(_f, name);
            await ActivateIntegrationAsync(_f, co.ClientId);
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-ZOHO");
            var start = await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/connect", new { region = "com" });
            Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync());
            var url = new Uri((await start.Json()).GetProperty("authorizeUrl").GetString()!);
            Assert.Equal("accounts.zoho.com", url.Host);
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            Assert.Equal("ZohoBooks.fullaccess.all", q["scope"]);
            Assert.Equal("offline", q["access_type"]);

            var anon = _f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var cb = await anon.GetAsync($"/api/v1/integrations/zoho/callback?code=good-code&state={Uri.EscapeDataString(q["state"]!)}&accounts-server={Uri.EscapeDataString(FakeZohoApi.Accounts)}");
            Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
            Assert.Contains("/integrations?zoho=choose", cb.Headers.Location!.ToString());
            Assert.EndsWith("/api/v1/integrations/zoho/callback", _f.Zoho.LastRedirectUri);

            var choosing = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J);
            Assert.Equal("ChooseOrganization", choosing.GetProperty("status").GetString());
            Assert.Equal(2, choosing.GetProperty("organizations").GetArrayLength());
            var chosen = await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/organization", new { organizationId = org });
            Assert.True(chosen.IsSuccessStatusCode, await chosen.Content.ReadAsStringAsync());
            var settings = await co.Admin.PutAsJsonAsync("/api/v1/integrations/zoho/settings",
                new { autoFiscalise = false, deviceId = dev, defaultHsCode = "99001000", defaultMoneyType = "Credit", postBack = true, startDate = DateTime.UtcNow.Date.AddDays(-7) });
            Assert.True(settings.IsSuccessStatusCode, await settings.Content.ReadAsStringAsync());
            return (co, dev);
        }

        private static async Task<JsonElement> DocAsync(HttpClient c, string number)
        {
            var page = await c.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/documents?pageSize=100", J);
            return page.GetProperty("items").EnumerateArray().Single(d => d.GetProperty("externalNumber").GetString() == number);
        }

        [Fact]
        public async Task A_company_connects_zoho_and_its_invoices_are_fiscalised_and_written_back()
        {
            var org = "700001";
            _f.Zoho.Contacts["C1"] = new ZohoContact("Glow Salon", "Glow Salon (Pvt) Ltd", "2000123456", "220099887", "glow@example.test", null);
            _f.Zoho.Put(org, "invoice", Invoice("z-inv-1", "INV-001", 25m, new[] { Line("Body lotion", 2, 10m), Line("Face cream", 1, 5m) }));
            _f.Zoho.Put(org, "invoice", Invoice("z-inv-draft", "INV-002", 10m, new[] { Line("Draft", 1, 10m) }, status: "draft"));
            _f.Zoho.Put(org, "invoice", Invoice("z-inv-old", "INV-000", 10m, new[] { Line("Old", 1, 10m) }, date: DateTime.UtcNow.Date.AddDays(-60)));
            var (co, _) = await ConnectedAsync("Zoho Moyo");

            var conn = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J);
            Assert.Equal("Connected", conn.GetProperty("status").GetString());
            Assert.Equal("Moyo Retail", conn.GetProperty("organizationName").GetString());
            Assert.Contains("/api/v1/integrations/zoho/hook/", conn.GetProperty("webhookUrl").GetString());
            Assert.DoesNotContain("refresh-1", conn.GetRawText()); Assert.DoesNotContain("access-", conn.GetRawText());   // no token ever leaves the server

            var sync = await (await co.Admin.PostAsync("/api/v1/integrations/zoho/sync", null)).Json();
            Assert.Equal(1, sync.GetProperty("new").GetInt32());   // the draft and the invoice before the start date are not taken
            var doc = await DocAsync(co.Admin, "INV-001");
            Assert.Equal("Pending", doc.GetProperty("status").GetString());
            Assert.StartsWith("https://books.zoho.com/app/700001#/invoices/", doc.GetProperty("externalUrl").GetString());

            var f = await co.Admin.PostAsync($"/api/v1/integrations/zoho/documents/{doc.GetProperty("id").GetGuid()}/fiscalise", null);
            Assert.True(f.IsSuccessStatusCode, await f.Content.ReadAsStringAsync());
            var done = await f.Json();
            Assert.Equal("Fiscalised", done.GetProperty("status").GetString());
            Assert.True(done.GetProperty("postedBackAt").ValueKind != JsonValueKind.Null, done.GetRawText());
            var receiptId = done.GetProperty("receiptId").GetGuid();

            var receipt = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{receiptId}/detail", J);
            Assert.Equal("INV-001", receipt.GetProperty("receipt").GetProperty("invoiceNo").GetString());
            Assert.Equal(25m, receipt.GetProperty("receipt").GetProperty("total").GetDecimal());
            Assert.Equal("2000123456", receipt.GetProperty("buyer").GetProperty("buyerTIN").GetString());

            var comment = Assert.Single(_f.Zoho.Comments, c => c.Id == "z-inv-1");
            Assert.Contains("FISCAL TAX INVOICE", comment.Text);
            Assert.Contains("verification code", comment.Text);
            Assert.Contains(_f.Zoho.Attachments, a => a.Id == "z-inv-1" && a.Pdf.Length > 1000 && a.Pdf[0] == (byte)'%');
            Assert.Contains(_f.Zoho.Fields, x => x.Id == "z-inv-1" && x.FieldId == "cf1" && x.Value.Contains('/'));
            Assert.Contains(_f.Zoho.Fields, x => x.Id == "z-inv-1" && x.FieldId == "cf2" && x.Value.Length == 19);
            Assert.DoesNotContain(_f.Zoho.Fields, x => x.FieldId == "cf3");

            // a fiscal record is final
            var again = await co.Admin.PostAsync($"/api/v1/integrations/zoho/documents/{doc.GetProperty("id").GetGuid()}/fiscalise", null);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            // automatic from now on: a credit note for the invoice, an invoice whose VAT does not add up, and the invoice edited in Zoho after fiscalising
            var settings = await co.Admin.PutAsJsonAsync("/api/v1/integrations/zoho/settings",
                new { autoFiscalise = true, deviceId = conn.GetProperty("deviceId").GetGuid(), defaultHsCode = "99001000", defaultMoneyType = "Credit", postBack = true, startDate = DateTime.UtcNow.Date.AddDays(-7) });
            Assert.True(settings.IsSuccessStatusCode, await settings.Content.ReadAsStringAsync());
            _f.Zoho.Put(org, "creditnote", new ZohoDocument("z-cn-1", "CN-001", "open", DateTime.UtcNow.Date, "USD", "C1", "Glow Salon", true, 5m, 0, 0, null, 0,
                new[] { Line("Face cream", 1, 5m) }, Array.Empty<ZohoCustomField>(), "z-inv-1", null));
            _f.Zoho.Put(org, "invoice", Invoice("z-inv-bad", "INV-003", 11.5m, new[] { Line("Gift box", 1, 10m) }));
            _f.Zoho.Put(org, "invoice", Invoice("z-inv-1", "INV-001", 30m, new[] { Line("Body lotion", 3, 10m) }), DateTime.UtcNow.AddSeconds(5));
            sync = await (await co.Admin.PostAsync("/api/v1/integrations/zoho/sync", null)).Json();
            var listed = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/documents?pageSize=100", J)).GetRawText();
            Assert.True(sync.GetProperty("fiscalised").GetInt32() == 1 && sync.GetProperty("failed").GetInt32() == 1, sync.GetRawText() + listed);

            var cn = await DocAsync(co.Admin, "CN-001");
            Assert.Equal("Fiscalised", cn.GetProperty("status").GetString());
            var cnReceipt = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{cn.GetProperty("receiptId").GetGuid()}", J);
            Assert.Equal("CreditNote", cnReceipt.GetProperty("type").GetString());
            var bad = await DocAsync(co.Admin, "INV-003");
            Assert.Equal("Failed", bad.GetProperty("status").GetString());
            Assert.Contains("Zoho shows 11.50", bad.GetProperty("error").GetString());
            Assert.True((await DocAsync(co.Admin, "INV-001")).GetProperty("changedAfterFiscalised").GetBoolean());
            var changed = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/documents?status=Changed", J);
            Assert.Equal(1, changed.GetProperty("total").GetInt32());

            // the failed one can be skipped, and brought back
            var skipped = await (await co.Admin.PostAsync($"/api/v1/integrations/zoho/documents/{bad.GetProperty("id").GetGuid()}/skip", null)).Json();
            Assert.Equal("Skipped", skipped.GetProperty("status").GetString());

            // a person with access to some devices only cannot touch the connection, nor can another company see these documents
            var other = await NewCompanyAsync(_f, "Zoho Other");
            Assert.Equal(0, (await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/documents", J)).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsync($"/api/v1/integrations/zoho/documents/{cn.GetProperty("id").GetGuid()}/skip", null)).StatusCode);
            var bell = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J);
            Assert.Contains("INV-003", bell.GetRawText());
        }

        [Fact]
        public async Task A_revoked_zoho_access_asks_for_reconnecting_and_the_webhook_needs_its_key()
        {
            var (co, _) = await ConnectedAsync("Zoho Revoked", org: "700002");
            var conn = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J);
            var hook = new Uri(conn.GetProperty("webhookUrl").GetString()!).AbsolutePath;
            var anon = _f.CreateClient();
            Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync(hook[..^4] + "beef", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anon.PostAsync(hook, null)).StatusCode);

            // the background round takes the connection the webhook asked for
            using (var scope = _f.Services.CreateScope())
                Assert.True(await scope.ServiceProvider.GetRequiredService<IIntegrationService>().RunScheduledAsync(DateTime.UtcNow, default) >= 1);
            Assert.NotEqual(JsonValueKind.Null, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J)).GetProperty("lastSyncAt").ValueKind);

            // force a refresh, which Zoho refuses
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                var c = db.IntegrationConnections.Single(x => x.ClientId == co.ClientId);
                c.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
            _f.Zoho.RefreshRevoked = true;
            try
            {
                var sync = await (await co.Admin.PostAsync("/api/v1/integrations/zoho/sync", null)).Json();
                Assert.Contains("sign in to Zoho again", sync.GetProperty("error").GetString());
                var after = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J);
                Assert.Equal("NeedsReconnect", after.GetProperty("status").GetString());
                var again = await co.Admin.PostAsync("/api/v1/integrations/zoho/sync", null);
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
                Assert.Contains("Zoho Books needs you to sign in again", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetRawText());
                // the hook of a connection that is not live does nothing
                Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync(hook, null)).StatusCode);
            }
            finally { _f.Zoho.RefreshRevoked = false; }

            // the operator sees it among the connections that need attention
            var op = await AdminAsync(_f);
            var monitor = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations", J);
            var row = monitor.GetProperty("connections").EnumerateArray().Single(r => r.GetProperty("clientId").GetGuid() == co.ClientId);
            Assert.Equal("NeedsReconnect", row.GetProperty("status").GetString());
            Assert.True(monitor.GetProperty("needsAttention").GetInt32() >= 1);

            // disconnecting forgets the tokens and changes the webhook address
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync("/api/v1/integrations/zoho")).StatusCode);
            Assert.Equal("NotConnected", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J)).GetProperty("status").GetString());
        }

        [Fact]
        public async Task Sign_in_returns_that_were_tampered_with_or_point_elsewhere_are_refused()
        {
            var co = await NewCompanyAsync(_f, "Zoho Tamper");
            await ActivateIntegrationAsync(_f, co.ClientId);
            var url = new Uri((await (await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/connect", new { region = "eu" })).Json()).GetProperty("authorizeUrl").GetString()!);
            Assert.Equal("accounts.zoho.eu", url.Host);
            var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"]!;
            var anon = _f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            async Task<string> Back(string query) => (await anon.GetAsync("/api/v1/integrations/zoho/callback?" + query)).Headers.Location!.ToString();
            Assert.Contains("zoho=error", await Back($"code=good-code&state={Uri.EscapeDataString(state[..^3] + "abc")}"));
            Assert.Contains("zoho=error", await Back($"code=good-code&state={Uri.EscapeDataString(state)}&accounts-server={Uri.EscapeDataString("https://accounts.evil.example")}"));
            Assert.Contains("zoho=denied", await Back("error=access_denied"));
            Assert.Contains("zoho=error", await Back($"code=bad-code&state={Uri.EscapeDataString(state)}"));
            Assert.Equal("NotConnected", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J)).GetProperty("status").GetString());

            // a company cannot reach the platform's own settings or the monitor
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/admin/integrations")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/admin/integrations/zoho-app")).StatusCode);
        }

        [Fact]
        public async Task The_platform_sets_the_zoho_app_and_prices_and_connections_are_billed()
        {
            var op = await AdminAsync(_f);
            var app = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations/zoho-app", J);
            Assert.True(app.GetProperty("configured").GetBoolean());
            Assert.EndsWith("/api/v1/integrations/zoho/callback", app.GetProperty("redirectUri").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/admin/integrations/zoho-app", new { clientId = "x y", clientSecret = (string?)null, clearSecret = false })).StatusCode);
            // saving the client ID alone keeps the secret
            var saved = await (await op.PutAsJsonAsync("/api/v1/admin/integrations/zoho-app", new { clientId = "1000.TESTCLIENTID", clientSecret = (string?)null, clearSecret = false })).Json();
            Assert.True(saved.GetProperty("hasSecret").GetBoolean());
            Assert.DoesNotContain("test-secret", saved.GetRawText());

            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/admin/integrations/pricing", new { charge = true, monthlyFee = -1m, currency = "USD", includedDocuments = 10, pricePerExtraDocument = 0.1m })).StatusCode);
            var price = await op.PutAsJsonAsync("/api/v1/admin/integrations/pricing", new { charge = true, monthlyFee = 15m, currency = "usd", includedDocuments = 0, pricePerExtraDocument = 0.1m });
            Assert.True(price.IsSuccessStatusCode, await price.Content.ReadAsStringAsync());
            try
            {
                var (co, _) = await ConnectedAsync("Zoho Billed");
                Assert.Equal(15m, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J)).GetProperty("pricing").GetProperty("monthlyFee").GetDecimal());
                using var scope = _f.Services.CreateScope();
                var billing = scope.ServiceProvider.GetRequiredService<IIntegrationBilling>();
                var now = DateTime.UtcNow;
                var charge = await billing.ChargeAsync(co.ClientId, now, now.AddMonths(1), "USD", default);
                Assert.NotNull(charge);
                Assert.Equal(15m, charge!.Value.Amount);
                Assert.Contains("Zoho Books connection", charge.Value.Description);
                Assert.Null(await billing.ChargeAsync(co.ClientId, now, now.AddMonths(1), "ZWG", default));   // another currency is not mixed in
                var monitor = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations", J);
                Assert.True(monitor.GetProperty("monthlyRevenue")[0].GetProperty("amount").GetDecimal() >= 15m);
            }
            finally
            {
                await op.PutAsJsonAsync("/api/v1/admin/integrations/pricing", new { charge = false, monthlyFee = 10m, currency = "USD", includedDocuments = 200, pricePerExtraDocument = 0.05m });
            }
        }

        [Fact]
        public async Task A_company_connects_with_its_own_zoho_app_using_a_self_client_code()
        {
            var co = await NewCompanyAsync(_f, "Own App Co");
            await ActivateIntegrationAsync(_f, co.ClientId);
            const string url = "/api/v1/integrations/zoho/own-app";
            // the typo that gives Zoho's "General Error" page is caught here
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync(url, new { region = "com", clientId = "V1000.ABCDEFGHIJKL", clientSecret = "own-secret", grantCode = "good-code" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync(url, new { region = "com", clientId = "1000.OWNAPPCLIENT01", clientSecret = (string?)null, grantCode = "good-code" })).StatusCode);
            var refused = await co.Admin.PostAsJsonAsync(url, new { region = "com", clientId = "1000.OWNAPPCLIENT01", clientSecret = "own-secret", grantCode = "expired-code" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Generate a new code", await refused.Content.ReadAsStringAsync());

            // the secret was kept, so only a fresh code is needed; no redirect address is sent for a Self Client code
            var ok = await co.Admin.PostAsJsonAsync(url, new { region = "com", clientId = "1000.OWNAPPCLIENT01", clientSecret = (string?)null, grantCode = "good-code" });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            Assert.Null(_f.Zoho.LastRedirectUri);
            var conn = (await ok.Json()).GetProperty("connection");
            Assert.Equal("ChooseOrganization", conn.GetProperty("status").GetString());
            Assert.Equal("1000.OWNAPPCLIENT01", conn.GetProperty("ownClientId").GetString());
            Assert.DoesNotContain("own-secret", conn.GetRawText());
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/organization", new { organizationId = "700002" })).IsSuccessStatusCode);

            // renewing the access token uses the company's app, not the platform's
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                db.IntegrationConnections.Single(x => x.ClientId == co.ClientId).AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
            while (_f.Zoho.ClientIdsUsed.TryDequeue(out _)) { }
            var sync = await (await co.Admin.PostAsync("/api/v1/integrations/zoho/sync", null)).Json();
            Assert.False(sync.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null, sync.GetRawText());
            Assert.Contains("1000.OWNAPPCLIENT01", _f.Zoho.ClientIdsUsed);
            Assert.DoesNotContain("1000.TESTCLIENTID", _f.Zoho.ClientIdsUsed);

            // without a code: the sign-in page of the company's own server-based app
            var start = await (await co.Admin.PostAsJsonAsync(url, new { region = "eu", clientId = "1000.OWNAPPCLIENT01", clientSecret = (string?)null, grantCode = (string?)null })).Json();
            Assert.Contains("accounts.zoho.eu", start.GetProperty("authorizeUrl").GetString());
            Assert.Contains("client_id=1000.OWNAPPCLIENT01", start.GetProperty("authorizeUrl").GetString());

            // forgetting the own app needs a fresh sign-in with the platform's
            var forgot = await (await co.Admin.DeleteAsync(url)).Json();
            Assert.Equal("NeedsReconnect", forgot.GetProperty("status").GetString());
            Assert.False(forgot.TryGetProperty("ownClientId", out var own) && own.ValueKind != JsonValueKind.Null);
        }

        [Fact]
        public void Zoho_documents_map_to_fiscal_lines_exactly_or_say_why_not()
        {
            var plain = new ZohoDocument("1", "A", "sent", null, "USD", null, null, true, 25m, 0, 0, null, 0,
                new[] { Line("Lotion", 2, 10m), Line("Cream", 3, 3m, total: 8m), Line("Water", 1, 5m, tax: null) }, Array.Empty<ZohoCustomField>(), null, null);
            var (lines, problem) = IntegrationService.MapLines(plain, "99001000");
            Assert.Null(problem);
            Assert.Equal(3, lines.Count);
            Assert.Equal(10m, lines[0].UnitPrice);
            Assert.Equal(1, lines[1].Quantity); Assert.Equal(8m, lines[1].UnitPrice);   // a line discount that does not divide evenly becomes one line of the net amount
            Assert.True(lines[2].IsExempt);

            var mixed = plain with { EntityDiscount = 2m };
            Assert.Contains("different VAT rates", IntegrationService.MapLines(mixed, null).Problem);
            var single = plain with { Lines = new[] { Line("Lotion", 2, 10m) }, EntityDiscount = 2m, ShippingCharge = 3m, ShippingTaxPercent = 15, Adjustment = -0.5m };
            var (withExtras, p2) = IntegrationService.MapLines(single, "99001000");
            Assert.Null(p2);
            Assert.Contains(withExtras, l => l.Name == "Discount" && l.IsDiscount && l.TaxPercent == 15);
            Assert.Contains(withExtras, l => l.Name == "Shipping" && l.UnitPrice == 3m);
            Assert.Contains(withExtras, l => l.Name == "Adjustment" && l.IsDiscount && l.UnitPrice == 0.5m);

            Assert.Equal("ABCD-0123-4567-89EF", IntegrationService.VerificationCode("https://fdms.zimra.co.zw/0000012345abcd0123456789ef"));
            Assert.Null(IntegrationService.VerificationCode(null));
            Assert.True(ZohoApi.IsAccountsServer("https://accounts.zoho.eu"));
            Assert.False(ZohoApi.IsAccountsServer("https://accounts.zoho.eu.evil.example"));
            Assert.False(ZohoApi.IsApiDomain("http://www.zohoapis.com"));
        }
    }
}
