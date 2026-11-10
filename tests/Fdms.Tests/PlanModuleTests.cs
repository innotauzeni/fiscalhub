using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>Plans include optional modules; a company sees and can use only what its plan includes, in the console and with API tokens alike.</summary>
    public class PlanModuleTests : IClassFixture<MessagingFactory>
    {
        private readonly MessagingFactory _f;
        private static int _seq = 96000;
        public PlanModuleTests(MessagingFactory f) => _f = f;

        private static object Plan(string code, string name, string[] modules) => new { id = 0, code, name, priceMonthly = 10, priceYearly = 100, currency = "USD", maxDevices = 0, maxUsers = 0, maxBranches = 0,
            maxReceiptsPerMonth = 0, features = new string[0], trialDays = 0, isActive = true, isPublic = true, sortOrder = 60, modules };

        private static string[] Slugs(JsonElement login) => login.GetProperty("menus").EnumerateArray().Select(m => m.GetProperty("slugname").GetString()!).ToArray();

        [Fact]
        public async Task A_company_only_sees_and_uses_the_modules_its_plan_includes()
        {
            var op = await AdminAsync(_f);
            Assert.Contains((await _f.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/public/plan-modules", J)).EnumerateArray(), m => m.GetProperty("key").GetString() == "accounting");
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/plans", Plan("bad" + _seq, "Bad", new[] { "teleport" }))).StatusCode);
            var basic = await (await op.PutAsJsonAsync("/api/v1/plans", Plan("basic" + Interlocked.Increment(ref _seq), "Basic", Array.Empty<string>()))).Json();
            Assert.Empty(basic.GetProperty("modules").EnumerateArray());
            var pro = await (await op.PutAsJsonAsync("/api/v1/plans", Plan("pro" + Interlocked.Increment(ref _seq), "Pro", new[] { "accounting", "api" }))).Json();
            Assert.Equal(new[] { "accounting", "api" }, pro.GetProperty("modules").EnumerateArray().Select(m => m.GetString()).ToArray());

            var co = await NewCompanyAsync(_f, "Modules Co");
            Assert.True((await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = pro.GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" })).IsSuccessStatusCode);
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-MOD");

            // Pro: accounting and API tokens work; WhatsApp & SMS and Zoho are not in the plan
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync("/api/v1/accounting/accounts")).StatusCode);
            var token = await NewTokenAsync(_f, co.Admin, dev);
            Assert.Equal(HttpStatusCode.OK, (await token.Http.GetAsync("/api/v1/devices")).StatusCode);
            var noWa = await co.Admin.GetAsync("/api/v1/messaging/whatsapp");
            Assert.Equal(HttpStatusCode.Forbidden, noWa.StatusCode);
            Assert.Contains("PLAN_FEATURE", await noWa.Content.ReadAsStringAsync());
            Assert.Contains("Pro plan does not include WhatsApp and SMS", await noWa.Content.ReadAsStringAsync());
            var proLogin = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = co.AdminEmail, password = UserPassword })).Json();
            var proMenu = Slugs(proLogin);
            Assert.Contains("accounting/journals", proMenu);
            Assert.Contains("tokens", proMenu);
            Assert.DoesNotContain("messaging", proMenu);
            Assert.DoesNotContain("integrations", proMenu);
            Assert.Contains("receipts", proMenu);   // fiscalising is never optional

            // down to Basic: accounting and the API token stop at once, fiscalising carries on
            Assert.True((await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = basic.GetProperty("id").GetInt32() })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/accounting/accounts")).StatusCode);
            var refused = await token.Http.GetAsync("/api/v1/devices");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("API tokens and webhooks", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/tokens", new { name = "x", scopes = AllTokenScopes })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync("/api/v1/devices")).StatusCode);
            var basicMenu = Slugs(await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = co.AdminEmail, password = UserPassword })).Json());
            Assert.DoesNotContain(basicMenu, s => s.StartsWith("accounting/"));
            Assert.DoesNotContain("tokens", basicMenu);
            Assert.Contains("receipts", basicMenu);
        }
    }
}
