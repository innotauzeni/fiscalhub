using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Infrastructure.Integrations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>The Zoho bridge: a partner's system reads and writes the Zoho Books of companies that shared it, and hears about changes.</summary>
    public class BridgeTests : IClassFixture<ZohoFactory>
    {
        private readonly ZohoFactory _f;
        private static int _seq = 81000;
        public BridgeTests(ZohoFactory f) => _f = f;

        private async Task<(Guid Id, string Code, HttpClient Staff)> PartnerAsync(string name)
        {
            var op = await AdminAsync(_f);
            var res = await op.PutAsJsonAsync("/api/v1/partners", new { id = (Guid?)null, name, code = (string?)null, contactName = "Pia", email = "pia@partner.test", phone = "0770000000", commissionPercent = 10m, isActive = true, notes = (string?)null });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var p = await res.Json();
            var id = p.GetProperty("id").GetGuid();
            var email = Unique("bridge") + "@partner.test";
            var staff = await op.PostAsJsonAsync($"/api/v1/partners/{id}/staff", new { email, name = "Sipho", surname = "Staff", password = UserPassword });
            Assert.True(staff.IsSuccessStatusCode, await staff.Content.ReadAsStringAsync());
            return (id, p.GetProperty("code").GetString()!, await LoginAsync(_f, email, UserPassword));
        }

        private async Task<Company> ConnectedCompanyAsync(string name)
        {
            var co = await NewCompanyAsync(_f, name);
            await ActivateIntegrationAsync(_f, co.ClientId);
            await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-BRIDGE");
            var url = new Uri((await (await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/connect", new { region = "com" })).Json()).GetProperty("authorizeUrl").GetString()!);
            Assert.Contains("ZohoBooks.fullaccess.all", System.Web.HttpUtility.ParseQueryString(url.Query)["scope"]);
            var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"]!;
            var anon = _f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync($"/api/v1/integrations/zoho/callback?code=good-code&state={Uri.EscapeDataString(state)}")).StatusCode);
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/organization", new { organizationId = "700001" })).IsSuccessStatusCode);
            return co;
        }

        private HttpClient KeyClient(string key)
        {
            var c = _f.CreateClient();
            c.DefaultRequestHeaders.Add("X-Bridge-Key", key);
            return c;
        }

        private static async Task<string> NewKeyAsync(HttpClient staff)
        {
            var res = await staff.PostAsJsonAsync("/api/v1/partner/bridge/keys", new { name = "Their server" });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var secret = (await res.Json()).GetProperty("secret").GetString()!;
            Assert.StartsWith("fhb_", secret);
            return secret;
        }

        [Fact]
        public async Task A_partner_reads_and_writes_only_what_the_company_shared()
        {
            var (_, code, staff) = await PartnerAsync("Bridge Partners");
            var co = await ConnectedCompanyAsync("Bridge Co");
            var key = await NewKeyAsync(staff);
            var bridge = KeyClient(key);

            Assert.Empty((await bridge.GetFromJsonAsync<JsonElement>("/api/v1/bridge/companies", J)).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await bridge.GetAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/contacts")).StatusCode);

            // the company shares contacts and items, read only
            var grant = await co.Admin.PutAsJsonAsync("/api/v1/integrations/zoho/bridge", new { partnerCode = code.ToLowerInvariant(), modules = new[] { "items", "contacts" }, canWrite = false });
            Assert.True(grant.IsSuccessStatusCode, await grant.Content.ReadAsStringAsync());
            var companies = await bridge.GetFromJsonAsync<JsonElement>("/api/v1/bridge/companies", J);
            var mine = Assert.Single(companies.EnumerateArray());
            Assert.True(mine.GetProperty("ready").GetBoolean(), mine.GetRawText());
            Assert.Equal("Moyo Retail", mine.GetProperty("organizationName").GetString());

            _f.Zoho.RawAnswers["GET contacts"] = _ => new ZohoRawResponse(200, "{\"code\":0,\"contacts\":[{\"contact_id\":\"c-9\",\"contact_name\":\"Glow Salon\"}]}");
            var read = await bridge.GetAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/contacts?page=2&organization_id=999999");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Contains("Glow Salon", await read.Content.ReadAsStringAsync());
            var call = _f.Zoho.RawCalls.Last(c => c.Path == "contacts");
            Assert.Equal("700001", call.Org);            // always the company's own organisation
            Assert.Equal("page=2", call.Query);           // a different organisation cannot be asked for

            Assert.Equal(HttpStatusCode.Forbidden, (await bridge.PostAsJsonAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/contacts", new { contact_name = "New" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await bridge.GetAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/invoices")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await bridge.GetAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/organizations")).StatusCode);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await bridge.DeleteAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/contacts/c-9")).StatusCode);

            // allowed to write: the body reaches Zoho as sent, and the company's audit log shows it
            await co.Admin.PutAsJsonAsync("/api/v1/integrations/zoho/bridge", new { partnerCode = code, modules = new[] { "contacts", "items", "taxes" }, canWrite = true });
            var write = await bridge.PostAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/contacts", new StringContent("{\"contact_name\":\"Moyo Traders\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
            Assert.Contains(_f.Zoho.RawCalls, c => c.Method == "POST" && c.Path == "contacts" && c.Body!.Contains("Moyo Traders"));
            Assert.Equal(HttpStatusCode.Forbidden, (await bridge.PostAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/settings/taxes", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await bridge.PostAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/contacts", new StringContent("not json", Encoding.UTF8, "application/json"))).StatusCode);

            // nobody else gets in: people, another partner's key, a revoked key
            Assert.Equal(HttpStatusCode.Unauthorized, (await co.Admin.GetAsync("/api/v1/bridge/companies")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/v1/bridge/companies")).StatusCode);
            var (_, _, otherStaff) = await PartnerAsync("Nosy Partners");
            var other = KeyClient(await NewKeyAsync(otherStaff));
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/contacts")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await KeyClient("fhb_made-up").GetAsync("/api/v1/bridge/companies")).StatusCode);

            var portal = await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/bridge", J);
            Assert.Equal(code, portal.GetProperty("partnerCode").GetString());
            var keyRow = portal.GetProperty("keys")[0];
            Assert.True(keyRow.GetProperty("callsToday").GetInt32() >= 2);
            Assert.DoesNotContain(key, portal.GetRawText());   // the key is never shown again
            Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/partner/bridge/keys/{keyRow.GetProperty("id").GetGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await bridge.GetAsync("/api/v1/bridge/companies")).StatusCode);

            // the company sees the partner and how much it used, and can withdraw access
            var grants = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/bridge", J);
            var g = Assert.Single(grants.GetProperty("grants").EnumerateArray());
            Assert.Equal("Bridge Partners", g.GetProperty("partnerName").GetString());
            Assert.True(g.GetProperty("callsThisMonth").GetInt64() >= 2);
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/integrations/zoho/bridge/{g.GetProperty("id").GetGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await co.Admin.PutAsJsonAsync("/api/v1/integrations/zoho/bridge", new { partnerCode = "NOPE-000", modules = new[] { "contacts" }, canWrite = false })).StatusCode);
        }

        [Fact]
        public async Task Changes_in_zoho_reach_the_partners_webhook_signed_and_stay_in_its_feed()
        {
            var (_, code, staff) = await PartnerAsync("Hook Partners");
            var co = await ConnectedCompanyAsync("Hook Co");
            var bridge = KeyClient(await NewKeyAsync(staff));
            Assert.True((await co.Admin.PutAsJsonAsync("/api/v1/integrations/zoho/bridge", new { partnerCode = code, modules = new[] { "contacts" }, canWrite = false })).IsSuccessStatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync("/api/v1/partner/bridge/endpoint", new { webhookUrl = "http://127.0.0.1/hook", watchModules = new[] { "contacts" }, rotateSecret = false })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync("/api/v1/partner/bridge/endpoint", new { webhookUrl = "https://partner.example/hook", watchModules = new[] { "taxes" }, rotateSecret = false })).StatusCode);
            var ep = await (await staff.PutAsJsonAsync("/api/v1/partner/bridge/endpoint", new { webhookUrl = "https://partner.example/hook", watchModules = new[] { "contacts", "invoices" }, rotateSecret = false })).Json();
            var secret = ep.GetProperty("secret").GetString()!;
            Assert.StartsWith("fhbsec_", secret);

            var t0 = DateTime.UtcNow.AddMinutes(20);
            async Task<int> RoundAsync(DateTime now) { using var s = _f.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<IBridgeService>().RunAsync(now, default); }
            Assert.Equal(0, await RoundAsync(t0));   // the first look only notes where Zoho is: no history replayed

            var changed = DateTime.UtcNow.AddMinutes(30).ToString("yyyy-MM-dd'T'HH:mm:ss") + "+0000";
            _f.Zoho.RawAnswers["GET contacts"] = q => new ZohoRawResponse(200, q.Contains("sort_column=last_modified_time")
                ? $"{{\"code\":0,\"contacts\":[{{\"contact_id\":\"c-77\",\"contact_name\":\"Fresh Buyer\",\"created_time\":\"{changed}\",\"last_modified_time\":\"{changed}\"}}],\"page_context\":{{\"has_more_page\":false}}}}"
                : "{\"code\":0,\"contacts\":[]}");
            var before = _f.Bridge.Sent.Count;
            Assert.True(await RoundAsync(t0.AddMinutes(20)) >= 1);
            var sent = _f.Bridge.Sent.Skip(before).Single();
            Assert.Equal("https://partner.example/hook", sent.Url);
            Assert.Equal("zoho.contacts.created", sent.Type);
            var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{sent.Timestamp}.{sent.Body}"))).ToLowerInvariant();
            Assert.Equal(expected, sent.Signature);
            var body = JsonDocument.Parse(sent.Body).RootElement;
            Assert.Equal(co.ClientId, body.GetProperty("companyId").GetGuid());
            Assert.Equal("Fresh Buyer", body.GetProperty("data").GetProperty("contact_name").GetString());

            // nothing new: nothing sent; the same change is never sent twice
            Assert.Equal(0, await RoundAsync(t0.AddMinutes(40)));

            var feed = await bridge.GetFromJsonAsync<JsonElement>("/api/v1/bridge/events?after=0", J);
            var item = Assert.Single(feed.GetProperty("events").EnumerateArray());
            Assert.Equal("c-77", item.GetProperty("recordId").GetString());
            Assert.Empty((await bridge.GetFromJsonAsync<JsonElement>($"/api/v1/bridge/events?after={feed.GetProperty("lastId").GetInt64()}", J)).GetProperty("events").EnumerateArray());

            // the partner's system is down: the change waits and is retried
            var changed2 = DateTime.UtcNow.AddMinutes(70).ToString("yyyy-MM-dd'T'HH:mm:ss") + "+0000";
            _f.Zoho.RawAnswers["GET contacts"] = q => new ZohoRawResponse(200,
                $"{{\"code\":0,\"contacts\":[{{\"contact_id\":\"c-77\",\"contact_name\":\"Fresh Buyer Ltd\",\"created_time\":\"{changed}\",\"last_modified_time\":\"{changed2}\"}}]}}");
            _f.Bridge.Fail = true;
            try { await RoundAsync(t0.AddMinutes(80)); } finally { _f.Bridge.Fail = false; }
            var portal = await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/bridge", J);
            var waiting = portal.GetProperty("recentEvents")[0];
            Assert.Equal("zoho.contacts.updated", waiting.GetProperty("type").GetString());
            Assert.Equal("Pending", waiting.GetProperty("status").GetString());
            Assert.Equal("HTTP 503", waiting.GetProperty("lastError").GetString());
            Assert.True(await RoundAsync(t0.AddMinutes(90)) >= 1);   // the retry goes through

            // the operator sees the partner's traffic
            var op = await AdminAsync(_f);
            var monitor = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations/bridge", J);
            Assert.Contains(monitor.GetProperty("partners").EnumerateArray(), r => r.GetProperty("partner").GetString() == "Hook Partners" && r.GetProperty("webhookHost").GetString() == "partner.example");
        }

        [Fact]
        public async Task A_companys_own_system_pushes_and_pulls_its_own_zoho_books_with_its_own_key()
        {
            var co = await ConnectedCompanyAsync("Own System Co");
            var other = await ConnectedCompanyAsync("Own System Neighbour");
            const string url = "/api/v1/integrations/zoho/bridge/own";

            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync(url + "/keys", new { name = "ERP", modules = Array.Empty<string>(), canWrite = true })).StatusCode);
            var made = await co.Admin.PostAsJsonAsync(url + "/keys", new { name = "Our ERP", modules = new[] { "contacts", "items" }, canWrite = true });
            Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
            var key = (await made.Json()).GetProperty("secret").GetString()!;
            var readOnly = (await (await co.Admin.PostAsJsonAsync(url + "/keys", new { name = "Reports", modules = new[] { "contacts" }, canWrite = false })).Json()).GetProperty("secret").GetString()!;
            var erp = KeyClient(key);

            var me = Assert.Single((await erp.GetFromJsonAsync<JsonElement>("/api/v1/bridge/companies", J)).EnumerateArray());
            Assert.Equal(co.ClientId, me.GetProperty("companyId").GetGuid());
            Assert.True(me.GetProperty("ready").GetBoolean());

            // pull: its own Zoho, without naming the company
            _f.Zoho.RawAnswers["GET contacts"] = _ => new ZohoRawResponse(200, "{\"code\":0,\"contacts\":[{\"contact_id\":\"c-1\",\"contact_name\":\"Own Customer\"}]}");
            var pulled = await erp.GetAsync("/api/v1/bridge/zoho/contacts?page=1");
            Assert.Equal(HttpStatusCode.OK, pulled.StatusCode);
            Assert.Contains("Own Customer", await pulled.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await erp.GetAsync($"/api/v1/bridge/companies/{co.ClientId}/zoho/items")).StatusCode);
            // push
            var pushed = await erp.PostAsync("/api/v1/bridge/zoho/contacts", new StringContent("{\"contact_name\":\"Pushed From ERP\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
            Assert.Contains(_f.Zoho.RawCalls, c => c.Method == "POST" && c.Body!.Contains("Pushed From ERP"));

            // only what the key allows, and only its own company
            Assert.Equal(HttpStatusCode.Forbidden, (await erp.GetAsync("/api/v1/bridge/zoho/invoices")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await KeyClient(readOnly).PostAsync("/api/v1/bridge/zoho/contacts", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await erp.GetAsync($"/api/v1/bridge/companies/{other.ClientId}/zoho/contacts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await erp.GetAsync("/api/v1/devices")).StatusCode);   // the key is for the bridge only

            // changes reach the company's own webhook, and its feed (not any partner's)
            var ep = await (await co.Admin.PutAsJsonAsync(url + "/endpoint", new { webhookUrl = "https://erp.example/zoho-hook", watchModules = new[] { "contacts" }, rotateSecret = false })).Json();
            Assert.StartsWith("fhbsec_", ep.GetProperty("secret").GetString());
            var t0 = DateTime.UtcNow.AddMinutes(200);
            async Task RoundAsync(DateTime now) { using var s = _f.Services.CreateScope(); await s.ServiceProvider.GetRequiredService<IBridgeService>().RunAsync(now, default); }
            await RoundAsync(t0);
            var changed = DateTime.UtcNow.AddMinutes(210).ToString("yyyy-MM-dd'T'HH:mm:ss") + "+0000";
            _f.Zoho.RawAnswers["GET contacts"] = q => new ZohoRawResponse(200, q.Contains("sort_column")
                ? $"{{\"code\":0,\"contacts\":[{{\"contact_id\":\"c-own\",\"contact_name\":\"Changed In Zoho\",\"created_time\":\"{changed}\",\"last_modified_time\":\"{changed}\"}}]}}" : "{\"code\":0,\"contacts\":[]}");
            var before = _f.Bridge.Sent.Count;
            await RoundAsync(t0.AddMinutes(20));
            Assert.Contains(_f.Bridge.Sent.Skip(before), x => x.Url == "https://erp.example/zoho-hook" && x.Body.Contains("Changed In Zoho"));
            var feed = await erp.GetFromJsonAsync<JsonElement>("/api/v1/bridge/events?after=0", J);
            Assert.Contains(feed.GetProperty("events").EnumerateArray(), e => e.GetProperty("recordId").GetString() == "c-own");

            var own = await co.Admin.GetFromJsonAsync<JsonElement>(url, J);
            Assert.Equal(2, own.GetProperty("keys").GetArrayLength());
            Assert.True(own.GetProperty("keys")[0].GetProperty("callsTotal").GetInt64() >= 1 || own.GetProperty("keys")[1].GetProperty("callsTotal").GetInt64() >= 1);
            Assert.DoesNotContain(key, own.GetRawText());

            // revoked: refused at once
            var keyId = own.GetProperty("keys").EnumerateArray().First(k => k.GetProperty("name").GetString() == "Our ERP").GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"{url}/keys/{keyId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await erp.GetAsync("/api/v1/bridge/zoho/contacts")).StatusCode);
        }

        [Fact]
        public async Task A_company_on_the_connector_plan_gets_the_connector_without_fiscal_devices()
        {
            var co = await NewCompanyAsync(_f, "Connector Only Co");
            await ActivateIntegrationAsync(_f, co.ClientId);
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                var plan = db.Plans.Single(p => p.Code == "zoho-connector");
                Assert.Equal("connector", plan.Kind);
                db.Subscriptions.Add(new Fdms.Domain.Entities.Subscription { ClientId = co.ClientId, PlanId = plan.Id, Status = "Active" });
                await db.SaveChangesAsync();
            }
            // no fiscal devices on this plan
            var reg = await co.Admin.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = Interlocked.Increment(ref _seq), activationKey = "ABCD1234", serialNo = "SN-C", modelName = "Server", modelVersion = "v1", environment = "Test" });
            Assert.Equal(HttpStatusCode.Forbidden, reg.StatusCode);
            Assert.Contains("PLAN_CONNECTOR", await reg.Content.ReadAsStringAsync());

            // signing in again: only the connector's menus, with Zoho Books first
            var login = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = co.AdminEmail, password = UserPassword })).Json();
            var slugs = login.GetProperty("menus").EnumerateArray().Select(m => m.GetProperty("slugname").GetString()).ToList();
            Assert.Equal("integrations", slugs[0]);
            Assert.DoesNotContain("devices", slugs);
            Assert.DoesNotContain("receipts", slugs);
            Assert.Contains("billing", slugs);

            // the connector itself works: own keys, and the plan already pays for the bridge
            Assert.True((await (await co.Admin.GetAsync("/api/v1/integrations/zoho/bridge/own")).Json()).GetProperty("connectorPlan").GetBoolean());
            var op = await AdminAsync(_f);
            await op.PutAsJsonAsync("/api/v1/admin/integrations/pricing", new { charge = true, monthlyFee = 0m, currency = "USD", includedDocuments = 0, pricePerExtraDocument = 0m, bridgeMonthlyFee = 25m });
            try
            {
                Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/bridge/own/keys", new { name = "Shop", modules = new[] { "items" }, canWrite = false })).IsSuccessStatusCode);
                using var scope = _f.Services.CreateScope();
                var now = DateTime.UtcNow;
                Assert.Null(await scope.ServiceProvider.GetRequiredService<IIntegrationBilling>().ChargeAsync(co.ClientId, now, now.AddMonths(1), "USD", default));
            }
            finally { await op.PutAsJsonAsync("/api/v1/admin/integrations/pricing", new { charge = false, monthlyFee = 10m, currency = "USD", includedDocuments = 200, pricePerExtraDocument = 0.05m, bridgeMonthlyFee = 0m }); }
        }

        [Fact]
        public async Task Companies_apply_the_owner_approves_with_a_fee_and_the_integration_starts_when_paid_and_can_be_suspended()
        {
            var co = await NewCompanyAsync(_f, "Applying Co");
            var op = await AdminAsync(_f);

            // nothing works before approval
            var blocked = await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/connect", new { region = "com" });
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Contains("INTEGRATION_NOT_ACTIVE", await blocked.Content.ReadAsStringAsync());
            Assert.Equal("None", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho", J)).GetProperty("accessStatus").GetString());

            // the company applies
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/integrations/access", new { uses = Array.Empty<string>(), note = "x" })).StatusCode);
            var applied = await (await co.Admin.PostAsJsonAsync("/api/v1/integrations/access", new { uses = new[] { "own_api", "fiscalise" }, note = "Our ERP pushes invoices to Zoho." })).Json();
            Assert.Equal("Requested", applied.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/integrations/access", new { uses = new[] { "own_api" } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/admin/integrations/access")).StatusCode);   // companies cannot decide

            // the owner approves with a set-up fee: an invoice is raised and the integration waits for it
            var list = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations/access?status=Requested", J);
            var id = list.EnumerateArray().Single(a => a.GetProperty("clientId").GetGuid() == co.ClientId).GetProperty("id").GetGuid();
            var approved = await (await op.PostAsJsonAsync($"/api/v1/admin/integrations/access/{id}/approve", new { setupFee = 50m, note = "Welcome" })).Json();
            Assert.Equal("AwaitingPayment", approved.GetProperty("status").GetString());
            var invoiceId = approved.GetProperty("invoiceId").GetGuid();
            Assert.Equal("Open", approved.GetProperty("invoiceStatus").GetString());
            // the unpaid integration invoice does not lock the account or count as a subscription invoice, and shows under Billing
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Contains((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").EnumerateArray(), i => i.GetProperty("id").GetGuid() == invoiceId);

            // approved but unpaid: the company can set up (connect, create a key), but the key does not work yet
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/connect", new { region = "com" })).IsSuccessStatusCode);
            var key = (await (await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/bridge/own/keys", new { name = "ERP", modules = new[] { "contacts" }, canWrite = false })).Json()).GetProperty("secret").GetString()!;
            var early = await KeyClient(key).GetAsync("/api/v1/bridge/zoho/contacts");
            Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
            Assert.Contains("INTEGRATION_NOT_ACTIVE", await early.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsync("/api/v1/integrations/zoho/sync", null)).StatusCode);

            using (var scope = _f.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IBillingService>().ApplyInvoicePaidAsync(invoiceId, "bank", "REF-INT-1", default);
            Assert.Equal("Active", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/access", J)).GetProperty("status").GetString());
            // paying takes back the "pay to switch it on" notices
            var notes = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J);
            Assert.DoesNotContain(invoiceId.ToString(), notes.GetRawText());
            Assert.DoesNotContain("pay to switch it on", notes.GetRawText());

            // the owner suspends: keys and connecting stop at once; reactivating brings them back
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/v1/admin/integrations/access/companies/{co.ClientId}/suspend", new { reason = "" })).StatusCode);
            Assert.Equal("Suspended", (await (await op.PostAsJsonAsync($"/api/v1/admin/integrations/access/companies/{co.ClientId}/suspend", new { reason = "Unpaid usage" })).Json()).GetProperty("status").GetString());
            var refused = await KeyClient(key).GetAsync("/api/v1/bridge/zoho/contacts");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("suspended", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsJsonAsync("/api/v1/integrations/zoho/connect", new { region = "com" })).StatusCode);
            Assert.Equal("Active", (await (await op.PostAsync($"/api/v1/admin/integrations/access/companies/{co.ClientId}/reactivate", null)).Json()).GetProperty("status").GetString());

            // the owner can revoke any key
            var own = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/bridge/own", J);
            var keyId = own.GetProperty("keys")[0].GetProperty("id").GetGuid();
            Assert.Contains((await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/integrations/access/keys", J)).EnumerateArray(), k => k.GetProperty("id").GetGuid() == keyId && k.GetProperty("ownerKind").GetString() == "company");
            Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsJsonAsync($"/api/v1/admin/integrations/access/keys/{keyId}/revoke", new { reason = "Abuse" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await KeyClient(key).GetAsync("/api/v1/bridge/zoho/contacts")).StatusCode);

            // a rejected application can be made again
            var other = await NewCompanyAsync(_f, "Rejected Co");
            var app2 = await (await other.Admin.PostAsJsonAsync("/api/v1/integrations/access", new { uses = new[] { "partners" } })).Json();
            Assert.Equal("Rejected", (await (await op.PostAsJsonAsync($"/api/v1/admin/integrations/access/{app2.GetProperty("id").GetGuid()}/reject", new { reason = "Tell us more about your system." })).Json()).GetProperty("status").GetString());
            Assert.Equal("Requested", (await (await other.Admin.PostAsJsonAsync("/api/v1/integrations/access", new { uses = new[] { "partners" }, note = "More detail" })).Json()).GetProperty("status").GetString());
        }

        [Fact]
        public async Task Sharing_is_billed_as_a_flat_monthly_fee()
        {
            var (_, code, _) = await PartnerAsync("Billed Partners");
            var co = await ConnectedCompanyAsync("Billed Bridge Co");
            var op = await AdminAsync(_f);
            Assert.True((await op.PutAsJsonAsync("/api/v1/admin/integrations/pricing", new { charge = true, monthlyFee = 0m, currency = "USD", includedDocuments = 0, pricePerExtraDocument = 0m, bridgeMonthlyFee = 25m })).IsSuccessStatusCode);
            try
            {
                using var scope = _f.Services.CreateScope();
                var billing = scope.ServiceProvider.GetRequiredService<IIntegrationBilling>();
                var now = DateTime.UtcNow;
                Assert.Null(await billing.ChargeAsync(co.ClientId, now, now.AddMonths(1), "USD", default));   // not shared yet
                await co.Admin.PutAsJsonAsync("/api/v1/integrations/zoho/bridge", new { partnerCode = code, modules = new[] { "contacts" }, canWrite = false });
                var charge = await billing.ChargeAsync(co.ClientId, now, now.AddMonths(1), "USD", default);
                Assert.Equal(25m, charge!.Value.Amount);
                Assert.Contains("Zoho bridge", charge.Value.Description);
                Assert.True((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/integrations/zoho/bridge", J)).GetProperty("charged").GetBoolean());
            }
            finally { await op.PutAsJsonAsync("/api/v1/admin/integrations/pricing", new { charge = false, monthlyFee = 10m, currency = "USD", includedDocuments = 200, pricePerExtraDocument = 0.05m, bridgeMonthlyFee = 0m }); }
        }

        [Fact]
        public void Paths_queries_and_zoho_times_are_checked()
        {
            Assert.Equal(("contacts/123/address", "contacts"), BridgeService.ParsePath("/contacts/123/address/"));
            Assert.Equal("taxes", BridgeService.ParsePath("settings/taxes").ModuleKey);
            Assert.Null(BridgeService.ParsePath("settings/preferences").ModuleKey);
            Assert.Null(BridgeService.ParsePath("contacts/../organizations").ModuleKey);
            Assert.Null(BridgeService.ParsePath("contacts/a b").ModuleKey);
            Assert.Equal("page=2&search_text=x", BridgeService.CleanQuery("?page=2&organization_id=1&search_text=x&AuthToken=zz"));
            Assert.Equal(new DateTime(2026, 10, 11, 7, 30, 0, DateTimeKind.Utc), BridgeService.ParseZohoTime("2026-10-11T09:30:00+0200"));
            Assert.Null(BridgeService.ParseZohoTime("soon"));
        }
    }
}
