using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>Branches, assigning devices to them, and limiting users to branches / devices.</summary>
    public class BranchAccessTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 14000;
        public BranchAccessTests(AccessFactory f) => _f = f;

        private static object Invoice(string extRef) => new
        {
            externalRef = extRef, invoiceNo = "INV-" + extRef, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
            lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } },
        };

        private static async Task<Guid> BranchAsync(HttpClient admin, string code, string name = "Branch")
        {
            var res = await admin.PutAsJsonAsync("/api/v1/branches", new { code, name, address = "1 Main St", phone = "0242", isActive = true });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return (await res.Json()).GetProperty("id").GetGuid();
        }

        private async Task<(string Id, HttpClient Http)> UserAsync(TestKit.Company co, string role = "Operator")
        {
            var email = TestKit.Unique("branchuser") + "@users.test";
            var res = await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Till", surname = "Operator", role, password = TestKit.UserPassword });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return ((await res.Json()).GetProperty("id").GetString()!, await TestKit.LoginAsync(_f, email, TestKit.UserPassword));
        }

        private static async Task<string[]> DeviceIdsSeenBy(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/devices", J)).EnumerateArray().Select(d => d.GetProperty("id").GetString()!).OrderBy(x => x).ToArray();

        [Fact]
        public async Task Branches_are_managed_per_company_and_devices_are_assigned_to_them()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Branch Co");
            var other = await TestKit.NewCompanyAsync(_f, "Other Branch Co");
            var (d1, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var (d2, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var (foreign, _) = await TestKit.RegisterDeviceAsync(other.Admin, Interlocked.Increment(ref _seq));

            var hre = await BranchAsync(co.Admin, "HRE-01", "Harare CBD");
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/branches", new { code = "HRE-01", name = "Again", isActive = true })).StatusCode);   // code is unique per company
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/branches", new { code = "bad code!", name = "X", isActive = true })).StatusCode);
            await BranchAsync(other.Admin, "HRE-01", "Same code, other company");                                                                                              // ...but not across companies

            // assign: devices of another company can never be put in this company's branch
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync($"/api/v1/branches/{hre}/devices", new { ids = new[] { d1, foreign } })).StatusCode);
            var assigned = await (await co.Admin.PutAsJsonAsync($"/api/v1/branches/{hre}/devices", new { ids = new[] { d1 } })).Json();
            Assert.Equal(new[] { d1.ToString() }, assigned.GetProperty("deviceIds").EnumerateArray().Select(x => x.GetString()).ToArray());

            var devices = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/devices", J);
            var first = devices.EnumerateArray().First(d => d.GetProperty("id").GetGuid() == d1);
            Assert.Equal(hre, first.GetProperty("branchId").GetGuid());
            Assert.Equal("Harare CBD", first.GetProperty("assignedBranch").GetString());
            Assert.False(devices.EnumerateArray().First(d => d.GetProperty("id").GetGuid() == d2).TryGetProperty("branchId", out _));

            // a branch that still has devices cannot be deleted; an empty one can; another company cannot touch it
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/branches/{hre}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.DeleteAsync($"/api/v1/branches/{hre}")).StatusCode);
            Assert.Single((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/branches", J)).EnumerateArray());
            await co.Admin.PutAsJsonAsync($"/api/v1/branches/{hre}/devices", new { ids = Array.Empty<Guid>() });   // moving the devices out empties it
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/branches/{hre}")).StatusCode);

            // only people who manage branches may use these endpoints
            var viewer = (await TestKit.LoginAsync(_f, (await ViewerEmail(co)), TestKit.UserPassword));
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/branches")).StatusCode);
        }

        private async Task<string> ViewerEmail(TestKit.Company co)
        {
            var email = TestKit.Unique("viewer") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "V", surname = "V", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            return email;
        }

        [Fact]
        public async Task A_restricted_user_reaches_only_the_devices_of_their_branches_and_assigned_devices()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Restricted Co");
            var (a, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var (b, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var (c, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var branchA = await BranchAsync(co.Admin, "A-01", "Branch A");
            await co.Admin.PutAsJsonAsync($"/api/v1/branches/{branchA}/devices", new { ids = new[] { a } });

            var (userId, user) = await UserAsync(co);
            Assert.Equal(3, (await DeviceIdsSeenBy(user)).Length);                                   // unrestricted by default
            Assert.Equal("all", (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/users/{userId}/access", J)).GetProperty("scope").GetString());

            // limit to branch A
            var set = await co.Admin.PutAsJsonAsync($"/api/v1/users/{userId}/access", new { scope = "restricted", branchIds = new[] { branchA }, deviceIds = Array.Empty<Guid>() });
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal(new[] { a.ToString() }, (await set.Json()).GetProperty("effectiveDeviceIds").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal(new[] { a.ToString() }, await DeviceIdsSeenBy(user));                       // applies to the SAME session at once (no re-login)

            // what they can do on their device ...
            Assert.Equal(HttpStatusCode.Created, (await user.PostAsJsonAsync($"/api/v1/devices/{a}/receipts", Invoice("RA-1"))).StatusCode);
            Assert.Equal(1, (await user.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("items").GetArrayLength());
            Assert.Equal(1, (await user.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", J)).GetProperty("devices").GetInt32());

            // ... and what they cannot do on any other device of the same company
            Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsJsonAsync($"/api/v1/devices/{b}/receipts", Invoice("RB-1"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/devices/{b}/day")).StatusCode);

            // a company administrator who is limited to branch A: other devices are invisible to them too, and they cannot widen anyone's reach (or their own)
            var (adminId, limitedAdmin) = await UserAsync(co, "ClientAdmin");
            await co.Admin.PutAsJsonAsync($"/api/v1/users/{adminId}/access", new { scope = "restricted", branchIds = new[] { branchA } });
            Assert.Equal(HttpStatusCode.NotFound, (await limitedAdmin.PostAsJsonAsync($"/api/v1/devices/{b}/tokens", new { name = "x", scopes = new[] { "receipts.view" } })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await limitedAdmin.PostAsJsonAsync($"/api/v1/devices/{a}/tokens", new { name = "x", scopes = new[] { "receipts.view" } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await limitedAdmin.GetAsync("/api/v1/branches")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await limitedAdmin.PutAsJsonAsync($"/api/v1/users/{userId}/access", new { scope = "all" })).StatusCode);
            var selfId = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users", J)).EnumerateArray().First(u => u.GetProperty("email").GetString() == co.AdminEmail).GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync($"/api/v1/users/{selfId}/access", new { scope = "all" })).StatusCode);   // nobody changes their own access

            // an admin issues on device B; the restricted user still does not see that receipt
            Assert.Equal(HttpStatusCode.Created, (await co.Admin.PostAsJsonAsync($"/api/v1/devices/{b}/receipts", Invoice("RB-2"))).StatusCode);
            Assert.Equal(1, (await user.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("items").GetArrayLength());
            var bReceipt = (await (await co.Admin.GetAsync($"/api/v1/receipts?deviceId={b}")).Json()).GetProperty("items")[0].GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/v1/receipts/{bReceipt}")).StatusCode);

            // registering new devices is for people with the whole company
            Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = Interlocked.Increment(ref _seq), activationKey = "ABCD1234", serialNo = "s", modelName = "m", modelVersion = "v" })).StatusCode);

            // add device C directly, and a new device joining branch A is reached automatically
            await co.Admin.PutAsJsonAsync($"/api/v1/users/{userId}/access", new { scope = "restricted", branchIds = new[] { branchA }, deviceIds = new[] { c } });
            Assert.Equal(new[] { a.ToString(), c.ToString() }.OrderBy(x => x).ToArray(), await DeviceIdsSeenBy(user));
            await co.Admin.PutAsJsonAsync($"/api/v1/branches/{branchA}/devices", new { ids = new[] { a, b } });
            Assert.Equal(3, (await DeviceIdsSeenBy(user)).Length);

            // validation
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync($"/api/v1/users/{userId}/access", new { scope = "restricted", branchIds = Array.Empty<Guid>(), deviceIds = Array.Empty<Guid>() })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync($"/api/v1/users/{userId}/access", new { scope = "everything" })).StatusCode);
            var other = await TestKit.NewCompanyAsync(_f, "Foreign Branch Co");
            var foreignBranch = await BranchAsync(other.Admin, "X-01", "Foreign");
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync($"/api/v1/users/{userId}/access", new { scope = "restricted", branchIds = new[] { foreignBranch } })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PutAsJsonAsync($"/api/v1/users/{userId}/access", new { scope = "all" })).StatusCode);   // not their user

            // back to everything
            await co.Admin.PutAsJsonAsync($"/api/v1/users/{userId}/access", new { scope = "all" });
            Assert.Equal(3, (await DeviceIdsSeenBy(user)).Length);
            Assert.Equal("all", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users", J)).EnumerateArray().First(u => u.GetProperty("id").GetString() == userId).GetProperty("accessScope").GetString());
        }
    }
}
