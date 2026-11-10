using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Infrastructure.Integrations.Connectors;
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
    /// <summary>One accounting product in memory (stands in for QuickBooks, Xero, Sage or Odoo): companies, documents, and what the gateway wrote back.</summary>
    public sealed class FakeConnector : IAccountingConnector
    {
        public FakeConnector(string key) => Key = key;
        public string Key { get; }
        public List<ConnectorOrganization> Organizations { get; } = new() { new("R1", "Moyo Books", "USD") };
        public ConcurrentDictionary<(string Org, string Kind, string Id), (ConnectorDocument Doc, DateTime Modified)> Docs { get; } = new();
        public ConcurrentBag<(string Kind, string Id, ConnectorWriteBack Wb)> WrittenBack { get; } = new();
        public volatile bool RefreshRevoked;
        public string? LastRedirectUri;
        public const string OdooKey = "odoo-key-123456";

        public void Put(string org, string kind, ConnectorDocument d, DateTime? modified = null) => Docs[(org, kind, d.Id)] = (d, modified ?? DateTime.UtcNow);

        public string AuthorizeUrl(ConnectorApp app, string redirectUri, string state) =>
            $"https://fake-{Key}.test/auth?client_id={Uri.EscapeDataString(app.ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";

        public Task<ConnectorTokens> ExchangeCodeAsync(ConnectorApp app, string redirectUri, string code, IReadOnlyDictionary<string, string?> callback, CancellationToken ct)
        {
            LastRedirectUri = redirectUri;
            if (code != "good-code" || app.ClientSecret != "qb-secret") throw new ConnectorException(400, "invalid_grant", unauthorized: true);
            callback.TryGetValue("realmId", out var realm);
            return Task.FromResult(new ConnectorTokens("access-" + Guid.NewGuid().ToString("N"), "refresh-" + Guid.NewGuid().ToString("N"), 3600, realm));
        }

        public Task<ConnectorTokens> RefreshAsync(ConnectorApp app, string refreshToken, CancellationToken ct) =>
            RefreshRevoked ? throw new ConnectorException(400, "invalid_grant", unauthorized: true) : Task.FromResult(new ConnectorTokens("access-" + Guid.NewGuid().ToString("N"), "refresh-2", 3600));

        public Task<ConnectorTokens> SignInAsync(ConnectorSession s, CancellationToken ct) =>
            s.AccessToken == OdooKey && s.Database == "moyo" ? Task.FromResult(new ConnectorTokens("7", null, 0)) : throw new ConnectorException(401, "The database, user or API key is wrong.", unauthorized: true);

        public Task<IReadOnlyList<ConnectorOrganization>> OrganizationsAsync(ConnectorSession s, CancellationToken ct) => Task.FromResult<IReadOnlyList<ConnectorOrganization>>(Organizations);

        public Task<ConnectorPage> ListAsync(ConnectorSession s, string kind, DateTime? since, int page, CancellationToken ct)
        {
            var items = Docs.Where(x => x.Key.Org == s.OrganizationId && x.Key.Kind == kind && (since == null || x.Value.Modified >= since))
                .Select(x => new ConnectorDocumentHead(x.Value.Doc.Id, x.Value.Doc.Number, x.Value.Doc.Status, x.Value.Doc.Date, x.Value.Doc.Total, x.Value.Doc.Currency, x.Value.Doc.Buyer?.Name, x.Value.Modified))
                .OrderBy(x => x.LastModified).ToList();
            return Task.FromResult(new ConnectorPage(items.Skip((page - 1) * 100).Take(100).ToList(), items.Count > page * 100));
        }

        public Task<ConnectorDocument> GetAsync(ConnectorSession s, string kind, string id, CancellationToken ct) =>
            Docs.TryGetValue((s.OrganizationId!, kind, id), out var d) ? Task.FromResult(d.Doc) : throw new ConnectorException(404, "not found");

        public Task WriteBackAsync(ConnectorSession s, string kind, string id, ConnectorWriteBack wb, CancellationToken ct) { WrittenBack.Add((kind, id, wb)); return Task.CompletedTask; }
    }

    public sealed class ConnectorFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        public RecordingEmail Email { get; } = new();
        public Dictionary<string, FakeConnector> Products { get; } = new[] { "quickbooks", "xero", "sage", "odoo" }.ToDictionary(k => k, k => new FakeConnector(k));
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
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(Email);
                s.RemoveAll<IAccountingConnector>();
                foreach (var p in Products.Values) s.AddSingleton<IAccountingConnector>(p);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    /// <summary>QuickBooks, Xero, Sage and Odoo: each applied for, approved and paid on its own, then connected, synced, fiscalised, written back and billed.</summary>
    public class ConnectorTests : IClassFixture<ConnectorFactory>
    {
        private readonly ConnectorFactory _f;
        private static int _seq = 73000;
        public ConnectorTests(ConnectorFactory f) => _f = f;

        private static ConnectorDocument Doc(string id, string number, decimal total, IReadOnlyList<ConnectorLine> lines, string status = "open", string? reference = null, ConnectorBuyer? buyer = null) =>
            new(id, number, status, DateTime.UtcNow.Date, "USD", total, lines, buyer ?? new ConnectorBuyer("Glow Salon", "2000123456", "220099887", "glow@example.test", null), reference, null);

        private static ConnectorLine Line(string name, decimal qty, decimal gross, decimal? tax = 15, string? hs = "33049900") => new(name, hs, qty, gross, tax, tax == null);

        private async Task ActivateAsync(Guid clientId, string provider)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
            db.IntegrationAccess.Add(new Fdms.Domain.Entities.IntegrationAccess { ClientId = clientId, Provider = provider, Status = "Active", Uses = "fiscalise", ActivatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        private async Task SetUpAppAsync(HttpClient op, string product)
        {
            var app = await op.PutAsJsonAsync($"/api/v1/admin/integrations/connectors/{product}/app", new { clientId = $"{product}-client", clientSecret = "qb-secret", clearSecret = false, sandbox = true });
            Assert.True(app.IsSuccessStatusCode, await app.Content.ReadAsStringAsync());
        }

        private static async Task<JsonElement> DocAsync(HttpClient c, string product, string number)
        {
            var page = await c.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/connectors/{product}/documents?pageSize=100", J);
            return page.GetProperty("items").EnumerateArray().Single(d => d.GetProperty("externalNumber").GetString() == number);
        }

        [Fact]
        public async Task QuickBooks_is_applied_for_approved_paid_connected_fiscalised_written_back_and_billed_on_its_own()
        {
            var op = await AdminAsync(_f);
            var qb = _f.Products["quickbooks"];
            var co = await NewCompanyAsync(_f, "QB Moyo");
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-QB");

            // the four products are listed; QuickBooks is not set up by the owner yet and the company has not applied
            var list = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/connectors", J);
            Assert.Equal(new[] { "quickbooks", "xero", "sage", "odoo" }, list.EnumerateArray().Select(p => p.GetProperty("product").GetString()).ToArray());
            var first = list[0];
            Assert.Equal("None", first.GetProperty("accessStatus").GetString());
            Assert.False(first.GetProperty("available").GetBoolean());
            var blocked = await co.Admin.PostAsync("/api/v1/integrations/connectors/quickbooks/connect", null);
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Contains("INTEGRATION_NOT_ACTIVE", await blocked.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/admin/integrations/connectors/apps")).StatusCode);   // companies cannot see the apps

            // the owner sets up the QuickBooks app (the secret never comes back) and its prices
            await SetUpAppAsync(op, "quickbooks");
            var apps = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations/connectors/apps", J);
            var qbApp = apps.EnumerateArray().Single(a => a.GetProperty("product").GetString() == "quickbooks");
            Assert.True(qbApp.GetProperty("configured").GetBoolean());
            Assert.EndsWith("/api/v1/integrations/connectors/quickbooks/callback", qbApp.GetProperty("redirectUri").GetString());
            Assert.DoesNotContain("qb-secret", apps.GetRawText());
            Assert.True(apps.EnumerateArray().Single(a => a.GetProperty("product").GetString() == "odoo").GetProperty("configured").GetBoolean());   // Odoo needs no app
            var price = await op.PutAsJsonAsync("/api/v1/admin/integrations/connectors/quickbooks/pricing",
                new { charge = true, monthlyFee = 15m, currency = "USD", includedDocuments = 1, pricePerExtraDocument = 0.5m, setupFee = 40m });
            Assert.True(price.IsSuccessStatusCode, await price.Content.ReadAsStringAsync());
            Assert.Equal(10m, (await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations/connectors/xero/pricing", J)).GetProperty("monthlyFee").GetDecimal());   // each product its own

            // the company applies for QuickBooks; the owner approves with the product's set-up fee
            var applied = await (await co.Admin.PostAsJsonAsync("/api/v1/integrations/access", new { provider = "quickbooks", note = "We invoice in QuickBooks." })).Json();
            Assert.Equal("Requested", applied.GetProperty("status").GetString());
            Assert.Equal("quickbooks", applied.GetProperty("provider").GetString());
            Assert.Equal("None", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/access", J)).GetProperty("status").GetString());   // Zoho untouched
            var pending = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations/access?status=Requested", J);
            var row = pending.EnumerateArray().Single(a => a.GetProperty("clientId").GetGuid() == co.ClientId);
            Assert.Equal("QuickBooks Online", row.GetProperty("providerLabel").GetString());
            var approved = await (await op.PostAsJsonAsync($"/api/v1/admin/integrations/access/{row.GetProperty("id").GetGuid()}/approve", new { setupFee = 40m, note = "Welcome" })).Json();
            Assert.Equal("AwaitingPayment", approved.GetProperty("status").GetString());
            var invoiceId = approved.GetProperty("invoiceId").GetGuid();

            // approved: it can be connected. The sign-in comes back with the company chosen (QuickBooks' realm)
            var start = await co.Admin.PostAsync("/api/v1/integrations/connectors/quickbooks/connect", null);
            Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync());
            var url = new Uri((await start.Json()).GetProperty("authorizeUrl").GetString()!);
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            Assert.Equal("quickbooks-client", q["client_id"]);
            var anon = _f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var forged = await anon.GetAsync($"/api/v1/integrations/connectors/quickbooks/callback?code=good-code&realmId=R1&state=forged");
            Assert.Contains("connector=failed", forged.Headers.Location!.ToString());
            var wrongProduct = await anon.GetAsync($"/api/v1/integrations/connectors/xero/callback?code=good-code&state={Uri.EscapeDataString(q["state"]!)}");
            Assert.Contains("connector=failed", wrongProduct.Headers.Location!.ToString());   // a QuickBooks state does not open Xero
            var cb = await anon.GetAsync($"/api/v1/integrations/connectors/quickbooks/callback?code=good-code&realmId=R1&state={Uri.EscapeDataString(q["state"]!)}");
            Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
            Assert.Contains("/connectors?product=quickbooks&connector=connected", cb.Headers.Location!.ToString());
            Assert.EndsWith("/api/v1/integrations/connectors/quickbooks/callback", qb.LastRedirectUri);
            var conn = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/connectors/quickbooks", J);
            Assert.Equal("Connected", conn.GetProperty("status").GetString());
            Assert.Equal("Moyo Books", conn.GetProperty("organizationName").GetString());
            Assert.DoesNotContain("access-", conn.GetRawText()); Assert.DoesNotContain("refresh-", conn.GetRawText());

            // not paid yet: nothing is pulled or fiscalised
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsync("/api/v1/integrations/connectors/quickbooks/sync", null)).StatusCode);
            using (var scope = _f.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IBillingService>().ApplyInvoicePaidAsync(invoiceId, "bank", "REF-QB-1", default);
            Assert.Equal("Active", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/access?provider=quickbooks", J)).GetProperty("status").GetString());

            var settings = await co.Admin.PutAsJsonAsync("/api/v1/integrations/connectors/quickbooks/settings",
                new { autoFiscalise = false, deviceId = dev, defaultHsCode = "99001000", defaultMoneyType = "Credit", postBack = true, startDate = DateTime.UtcNow.Date.AddDays(-7) });
            Assert.True(settings.IsSuccessStatusCode, await settings.Content.ReadAsStringAsync());

            qb.Put("R1", "invoice", Doc("101", "QB-1001", 25m, new[] { Line("Body lotion", 2, 20m), Line("Face cream", 1, 5m) }));
            qb.Put("R1", "invoice", Doc("102", "QB-1002", 10m, new[] { Line("Draft", 1, 10m) }, status: "draft"));
            var sync = await (await co.Admin.PostAsync("/api/v1/integrations/connectors/quickbooks/sync", null)).Json();
            Assert.Equal(1, sync.GetProperty("new").GetInt32());
            var doc = await DocAsync(co.Admin, "quickbooks", "QB-1001");
            var f = await co.Admin.PostAsync($"/api/v1/integrations/connectors/quickbooks/documents/{doc.GetProperty("id").GetGuid()}/fiscalise", null);
            Assert.True(f.IsSuccessStatusCode, await f.Content.ReadAsStringAsync());
            var done = await f.Json();
            Assert.Equal("Fiscalised", done.GetProperty("status").GetString());
            Assert.NotEqual(JsonValueKind.Null, done.GetProperty("postedBackAt").ValueKind);
            var receipt = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{done.GetProperty("receiptId").GetGuid()}/detail", J);
            Assert.Equal("QB-1001", receipt.GetProperty("receipt").GetProperty("invoiceNo").GetString());
            Assert.Equal(25m, receipt.GetProperty("receipt").GetProperty("total").GetDecimal());
            Assert.Equal("2000123456", receipt.GetProperty("buyer").GetProperty("buyerTIN").GetString());
            var wb = Assert.Single(qb.WrittenBack, w => w.Id == "101");
            Assert.Contains("FISCAL TAX INVOICE", wb.Wb.Text);
            Assert.True(wb.Wb.Pdf is { Length: > 1000 } && wb.Wb.Pdf[0] == (byte)'%');
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/integrations/connectors/quickbooks/documents/{doc.GetProperty("id").GetGuid()}/fiscalise", null)).StatusCode);

            // automatic: a credit note for that invoice, and an invoice whose VAT does not add up
            settings = await co.Admin.PutAsJsonAsync("/api/v1/integrations/connectors/quickbooks/settings",
                new { autoFiscalise = true, deviceId = dev, defaultHsCode = "99001000", defaultMoneyType = "Credit", postBack = true, startDate = DateTime.UtcNow.Date.AddDays(-7) });
            Assert.True(settings.IsSuccessStatusCode);
            qb.Put("R1", "creditnote", Doc("201", "QB-CN-1", 5m, new[] { Line("Face cream", 1, 5m) }, reference: "101"));
            qb.Put("R1", "invoice", Doc("103", "QB-1003", 11.5m, new[] { Line("Gift box", 1, 10m) }));
            sync = await (await co.Admin.PostAsync("/api/v1/integrations/connectors/quickbooks/sync", null)).Json();
            Assert.True(sync.GetProperty("fiscalised").GetInt32() == 1 && sync.GetProperty("failed").GetInt32() == 1, sync.GetRawText());
            var cn = await DocAsync(co.Admin, "quickbooks", "QB-CN-1");
            Assert.Equal("Fiscalised", cn.GetProperty("status").GetString());
            Assert.Equal("CreditNote", (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{cn.GetProperty("receiptId").GetGuid()}", J)).GetProperty("type").GetString());
            Assert.Contains("QuickBooks Online shows 11.50", (await DocAsync(co.Admin, "quickbooks", "QB-1003")).GetProperty("error").GetString());

            // the documents stay with their product: Zoho's list and other companies see none of them
            Assert.Equal(0, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/documents", J)).GetProperty("total").GetInt32());
            var other = await NewCompanyAsync(_f, "QB Other");
            Assert.Equal(0, (await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/connectors/quickbooks/documents", J)).GetProperty("total").GetInt32());
            var foreign = await other.Admin.PostAsync($"/api/v1/integrations/connectors/quickbooks/documents/{cn.GetProperty("id").GetGuid()}/skip", null);
            Assert.False(foreign.IsSuccessStatusCode);
            Assert.DoesNotContain("QB-CN-1", await foreign.Content.ReadAsStringAsync());

            // the monthly fee and the documents above the included one go on the subscription invoice
            using (var scope = _f.Services.CreateScope())
            {
                var from = DateTime.UtcNow.AddMinutes(1);
                var charge = await scope.ServiceProvider.GetRequiredService<IIntegrationBilling>().ChargeAsync(co.ClientId, from, from.AddDays(30), "USD", default);
                Assert.NotNull(charge);
                Assert.Equal(15.5m, charge!.Value.Amount);
                Assert.Contains("QuickBooks Online connection x1 month(s)", charge.Value.Description);
                Assert.Contains("1 extra QuickBooks Online document(s)", charge.Value.Description);
            }

            // the owner suspends QuickBooks only
            var susp = await op.PostAsJsonAsync($"/api/v1/admin/integrations/access/companies/{co.ClientId}/suspend?provider=quickbooks", new { reason = "Unpaid usage" });
            Assert.Equal("Suspended", (await susp.Json()).GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsync("/api/v1/integrations/connectors/quickbooks/sync", null)).StatusCode);
            Assert.Equal("Active", (await (await op.PostAsync($"/api/v1/admin/integrations/access/companies/{co.ClientId}/reactivate?provider=quickbooks", null)).Json()).GetProperty("status").GetString());

            // a revoked sign-in asks for connecting again
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                var c = db.IntegrationConnections.Single(x => x.ClientId == co.ClientId && x.Provider == "quickbooks");
                c.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
            qb.RefreshRevoked = true;
            try
            {
                var failed = await (await co.Admin.PostAsync("/api/v1/integrations/connectors/quickbooks/sync", null)).Json();
                Assert.Contains("Connect again", failed.GetProperty("error").GetString());
                Assert.Equal("NeedsReconnect", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/connectors/quickbooks", J)).GetProperty("status").GetString());
                Assert.Contains("Connect QuickBooks Online again", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetRawText());
            }
            finally { qb.RefreshRevoked = false; }
        }

        [Fact]
        public async Task Odoo_connects_with_its_address_and_key_and_the_background_round_fiscalises()
        {
            var odoo = _f.Products["odoo"];
            odoo.Organizations.Add(new ConnectorOrganization("2", "Moyo Branch Co", "USD"));
            var co = await NewCompanyAsync(_f, "Odoo Moyo");
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-ODOO");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsJsonAsync("/api/v1/integrations/connectors/odoo/key", new { url = "https://moyo.odoo.com", database = "moyo", username = "a@moyo.test", apiKey = FakeConnector.OdooKey })).StatusCode);
            await ActivateAsync(co.ClientId, "odoo");
            await ActivateAsync(co.ClientId, "zoho");   // having Zoho too does not mix the two

            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync("/api/v1/integrations/connectors/odoo/connect", null)).StatusCode);   // no sign-in for Odoo
            var http = await co.Admin.PostAsJsonAsync("/api/v1/integrations/connectors/odoo/key", new { url = "http://moyo.odoo.com", database = "moyo", username = "a@moyo.test", apiKey = FakeConnector.OdooKey });
            Assert.Contains("URL_INVALID", await http.Content.ReadAsStringAsync());
            var wrong = await co.Admin.PostAsJsonAsync("/api/v1/integrations/connectors/odoo/key", new { url = "https://moyo.odoo.com", database = "moyo", username = "a@moyo.test", apiKey = "wrong-key-000" });
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Contains("ODOO_REFUSED", await wrong.Content.ReadAsStringAsync());

            var ok = await co.Admin.PostAsJsonAsync("/api/v1/integrations/connectors/odoo/key", new { url = "https://moyo.odoo.com/", database = "moyo", username = "a@moyo.test", apiKey = FakeConnector.OdooKey });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            var conn = await ok.Json();
            Assert.Equal("ChooseOrganization", conn.GetProperty("status").GetString());
            Assert.Equal("https://moyo.odoo.com", conn.GetProperty("baseUrl").GetString());
            Assert.DoesNotContain(FakeConnector.OdooKey, conn.GetRawText());
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/integrations/connectors/odoo/organization", new { organizationId = "99" })).StatusCode);
            conn = await (await co.Admin.PostAsJsonAsync("/api/v1/integrations/connectors/odoo/organization", new { organizationId = "2" })).Json();
            Assert.Equal("Connected", conn.GetProperty("status").GetString());
            var settings = await co.Admin.PutAsJsonAsync("/api/v1/integrations/connectors/odoo/settings",
                new { autoFiscalise = true, deviceId = dev, defaultHsCode = "99001000", defaultMoneyType = "Cash", postBack = true, startDate = DateTime.UtcNow.Date.AddDays(-7) });
            Assert.True(settings.IsSuccessStatusCode, await settings.Content.ReadAsStringAsync());

            // an exempt line, an odd unit price, and a buyer without a valid TIN (sold without naming the buyer)
            odoo.Put("2", "invoice", Doc("55", "INV/2026/0055", 17.5m, new[] { Line("Consulting", 3, 10m, tax: null), Line("Paper", 1, 7.5m) }, buyer: new ConnectorBuyer("Walk-in", "12", null, null, null)));
            odoo.Put("1", "invoice", Doc("56", "INV/2026/0056", 10m, new[] { Line("Other company", 1, 10m) }));
            using (var scope = _f.Services.CreateScope())
                Assert.True(await scope.ServiceProvider.GetRequiredService<IConnectorService>().RunScheduledAsync(DateTime.UtcNow, default) >= 1);
            var page = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/connectors/odoo/documents", J);
            var d = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal("Fiscalised", d.GetProperty("status").GetString());
            Assert.Contains(odoo.WrittenBack, w => w.Id == "55");
            Assert.Equal(0, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/documents", J)).GetProperty("total").GetInt32());

            // disconnecting stops it and forgets the key
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync("/api/v1/integrations/connectors/odoo")).StatusCode);
            Assert.Equal("NotConnected", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/connectors/odoo", J)).GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync("/api/v1/integrations/connectors/odoo/sync", null)).StatusCode);
        }

        [Fact]
        public void Lines_are_mapped_exactly_or_explained()
        {
            var doc = Doc("1", "X", 0, new[] { Line("Bolts", 3, 10m), Line("Free sample", 1, 0m), Line("Service", 1, 5m, tax: null), new ConnectorLine("Discount", null, 1, -2m, 15, false, Discount: true) });
            var (lines, problem) = ConnectorService.MapLines(doc, "99001000");
            Assert.Null(problem);
            Assert.Equal(3, lines.Count);   // the free line carries nothing
            Assert.Equal(1, lines[0].Quantity); Assert.Equal(10m, lines[0].UnitPrice); Assert.Contains("(3 units)", lines[0].Name);   // 10 / 3 does not split into cents
            Assert.True(lines[1].IsExempt);
            Assert.True(lines[2].IsDiscount); Assert.Equal(2m, lines[2].UnitPrice);
            Assert.Equal("33049900", lines[0].HsCode);
            Assert.NotNull(ConnectorService.MapLines(Doc("2", "Y", 0, new[] { Line("Bad", 1, -5m) }), null).Problem);
            Assert.NotNull(ConnectorService.MapLines(Doc("3", "Z", 0, Array.Empty<ConnectorLine>()), null).Problem);
        }

        [Fact]
        public void Odoo_refuses_private_addresses()
        {
            Assert.False(OdooConnector.IsPublic(IPAddress.Parse("127.0.0.1")));
            Assert.False(OdooConnector.IsPublic(IPAddress.Parse("10.1.2.3")));
            Assert.False(OdooConnector.IsPublic(IPAddress.Parse("172.20.0.1")));
            Assert.False(OdooConnector.IsPublic(IPAddress.Parse("192.168.1.10")));
            Assert.False(OdooConnector.IsPublic(IPAddress.Parse("169.254.169.254")));
            Assert.False(OdooConnector.IsPublic(IPAddress.Parse("::1")));
            Assert.False(OdooConnector.IsPublic(IPAddress.Parse("fd00::1")));
            Assert.False(OdooConnector.IsPublic(IPAddress.Parse("::ffff:10.0.0.1")));
            Assert.True(OdooConnector.IsPublic(IPAddress.Parse("8.8.8.8")));
            Assert.True(OdooConnector.IsPublic(IPAddress.Parse("2606:4700::1111")));
        }
    }
}
