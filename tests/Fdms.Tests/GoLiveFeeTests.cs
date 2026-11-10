using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>Going live earns money: a one-off go-live fee (live devices switch on when it is paid) and a monthly fee per live device.</summary>
    public class GoLiveFeeTests : IClassFixture<MessagingFactory>
    {
        private readonly MessagingFactory _f;
        private static int _seq = 97000;
        public GoLiveFeeTests(MessagingFactory f) => _f = f;

        private async Task<(Company Co, int PlanId)> CompanyOnPaidPlanAsync(HttpClient op, string name)
        {
            var plan = await (await op.PutAsJsonAsync("/api/v1/plans", new { id = 0, code = "gl" + Interlocked.Increment(ref _seq), name = "Live plan", priceMonthly = 20, priceYearly = 200, currency = "USD",
                maxDevices = 0, maxUsers = 0, maxBranches = 0, maxReceiptsPerMonth = 0, features = new string[0], trialDays = 0, isActive = true, isPublic = false, sortOrder = 70 })).Json();
            var co = await NewCompanyAsync(_f, name);
            Assert.True((await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = plan.GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" })).IsSuccessStatusCode);
            // companies the operator creates start approved for live devices; these must ask, as self-registered ones do
            Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/v1/admin/go-live/clients/{co.ClientId}/revoke", null)).StatusCode);
            return (co, plan.GetProperty("id").GetInt32());
        }

        private static Task<HttpResponseMessage> RegisterLiveAsync(HttpClient c, string serial) =>
            c.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = Interlocked.Increment(ref _seq), activationKey = "ABCD1234", serialNo = serial, modelName = "Server", modelVersion = "v1", environment = "Production" });

        private async Task<Guid> AskAsync(HttpClient op, Company co)
        {
            await ZimraTestSetAsync(co.Admin, 60000 + Interlocked.Increment(ref _seq));
            var asked = await co.Admin.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "Ada Admin", contactPhone = "+263771234567", zimraTin = (string?)null, notes = (string?)null });
            Assert.True(asked.IsSuccessStatusCode, await asked.Content.ReadAsStringAsync());
            return (await asked.Json()).GetProperty("id").GetGuid();
        }

        [Fact]
        public async Task Approving_go_live_raises_the_fee_live_devices_start_when_it_is_paid_and_each_live_device_is_billed_monthly()
        {
            var op = await AdminAsync(_f);
            Assert.True((await op.PutAsJsonAsync("/api/v1/plans/usage-pricing", new { charge = true, currency = "USD", smsPrice = 0, whatsAppNumberMonthly = 0, extraDeviceMonthly = 0, extraUserMonthly = 0, extraBranchMonthly = 0,
                goLiveFee = 75, liveDeviceMonthly = 3 })).IsSuccessStatusCode);
            var (co, _) = await CompanyOnPaidPlanAsync(op, "Going Live Co");
            var id = await AskAsync(op, co);

            // approved with the standard fee: an invoice, and live devices still locked
            var approved = await (await op.PostAsJsonAsync($"/api/v1/admin/go-live/{id}/approve", new { note = "Welcome" })).Json();
            Assert.Equal("AwaitingPayment", approved.GetProperty("status").GetString());
            Assert.Equal(75m, approved.GetProperty("fee").GetDecimal());
            var invoice = approved.GetProperty("invoiceId").GetGuid();
            var locked = await RegisterLiveAsync(co.Admin, "SN-LIVE-0");
            Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
            Assert.Contains("approved", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J)).GetProperty("blocked").GetString());
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync("/api/v1/devices")).StatusCode);   // the unpaid fee does not lock the account

            // paid: live devices switch on
            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{invoice}/mark-paid", new { method = "Cash" })).IsSuccessStatusCode);
            Assert.True((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J)).GetProperty("approved").GetBoolean());
            var live = await RegisterLiveAsync(co.Admin, "SN-LIVE-1");
            Assert.True(live.IsSuccessStatusCode, await live.Content.ReadAsStringAsync());

            // each live device is billed with the subscription: 20 plan + 1 x 3
            var renewal = await (await op.PostAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}/invoice", null)).Json();
            Assert.Contains("1 live device(s)", renewal.GetProperty("description").GetString());
            Assert.Equal(23m, renewal.GetProperty("subtotal").GetDecimal());

            // the owner can waive the fee for one company
            var (free, _) = await CompanyOnPaidPlanAsync(op, "Free Live Co");
            var waived = await (await op.PostAsJsonAsync($"/api/v1/admin/go-live/{await AskAsync(op, free)}/approve", new { note = (string?)null, fee = 0 })).Json();
            Assert.Equal("Approved", waived.GetProperty("status").GetString());
            Assert.True((await RegisterLiveAsync(free.Admin, "SN-LIVE-2")).IsSuccessStatusCode);

            // rejecting while the fee is unpaid cancels its invoice
            var (late, _) = await CompanyOnPaidPlanAsync(op, "Changed Mind Co");
            var pending = await (await op.PostAsJsonAsync($"/api/v1/admin/go-live/{await AskAsync(op, late)}/approve", new { note = (string?)null })).Json();
            Assert.Equal("Rejected", (await (await op.PostAsJsonAsync($"/api/v1/admin/go-live/{pending.GetProperty("id").GetGuid()}/reject", new { reason = "Company closed its branch" })).Json()).GetProperty("status").GetString());
            Assert.Equal("Void", (await late.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{pending.GetProperty("invoiceId").GetGuid()}", J)).GetProperty("status").GetString());
        }
    }
}
