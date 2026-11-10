using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>Going live with ZIMRA: six test documents, the request, the email to ZIMRA with the PDFs, and the live device ZIMRA issues reaching the company.</summary>
    public class ZimraGoLiveTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 88000;
        public ZimraGoLiveTests(AiFactory f) => _f = f;

        private async Task<TestKit.Company> PaidCompanyAsync(HttpClient op, string name)
        {
            var plan = await (await op.PutAsJsonAsync("/api/v1/plans", new { id = 0, code = "zt" + Interlocked.Increment(ref _seq), name = "ZIMRA plan", priceMonthly = 20, priceYearly = 200, currency = "USD",
                maxDevices = 0, maxUsers = 0, maxBranches = 0, maxReceiptsPerMonth = 0, features = new string[0], trialDays = 0, isActive = true, isPublic = false, sortOrder = 71 })).Json();
            var co = await TestKit.NewCompanyAsync(_f, name);
            Assert.True((await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = plan.GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/v1/admin/go-live/clients/{co.ClientId}/revoke", null)).StatusCode);
            return co;
        }

        [Fact]
        public async Task Six_test_documents_go_to_zimra_and_the_live_device_reaches_the_company()
        {
            var op = await TestKit.AdminAsync(_f);
            var co = await PaidCompanyAsync(op, "Zimra Ready Co");
            Assert.True((await op.PutAsJsonAsync("/api/v1/plans/usage-pricing", new { charge = false, currency = "USD", smsPrice = 0, whatsAppNumberMonthly = 0, extraDeviceMonthly = 0, extraUserMonthly = 0, extraBranchMonthly = 0, goLiveFee = 0, liveDeviceMonthly = 0 })).IsSuccessStatusCode);

            // five of six: the ZWG debit note is missing, so the request is refused and the list says what is missing
            await TestKit.ZimraTestSetAsync(co.Admin, Interlocked.Increment(ref _seq), new[] { "USD CreditNote", "USD DebitNote", "ZWG CreditNote" });
            var st = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J);
            Assert.False(st.GetProperty("testSetComplete").GetBoolean());
            var missing = st.GetProperty("testDocuments").EnumerateArray().Where(d => !d.TryGetProperty("receiptId", out var r) || r.ValueKind == JsonValueKind.Null).ToList();
            Assert.Equal("ZWG debit note", Assert.Single(missing).GetProperty("slot").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "Rudo Moyo", contactPhone = "0771234567" })).StatusCode);

            // the sixth (a debit note against the ZWG invoice) completes the set
            var zwgInvoice = st.GetProperty("testDocuments").EnumerateArray().Single(d => d.GetProperty("slot").GetString() == "ZWG invoice").GetProperty("receiptId").GetGuid();
            var device = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{zwgInvoice}", J)).GetProperty("deviceId").GetGuid();
            await TestKit.IssueAsync(co.Admin, device, "ZWG", "DebitNote", "T-ZWG-DN-2", zwgInvoice);
            st = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J);
            Assert.True(st.GetProperty("testSetComplete").GetBoolean());
            var asked = await co.Admin.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "Rudo Moyo", contactPhone = "0771234567", zimraTin = "2000111222" });
            Assert.True(asked.IsSuccessStatusCode, await asked.Content.ReadAsStringAsync());
            var id = (await asked.Json()).GetProperty("id").GetGuid();

            // the operator: the email to ZIMRA lists all six, and the pack has the six PDFs and the letter
            var mail = await op.GetFromJsonAsync<JsonElement>($"/api/v1/admin/go-live/{id}/zimra-email", J);
            var body = mail.GetProperty("body").GetString()!;
            Assert.Contains("USD invoice", body); Assert.Contains("ZWG debit note T-ZWG-DN-2", body); Assert.Contains("TIN 2000111222", body);
            Assert.Equal(6, mail.GetProperty("attachments").GetArrayLength());
            using (var zip = new ZipArchive(new MemoryStream(await (await op.GetAsync($"/api/v1/admin/go-live/{id}/pack")).Content.ReadAsByteArrayAsync())))
            {
                Assert.Equal(7, zip.Entries.Count);
                Assert.Contains(zip.Entries, e => e.Name == "request-to-zimra.txt");
            }
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/admin/go-live/{id}/documents/{Guid.NewGuid()}/pdf")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync($"/api/v1/admin/go-live/{id}/zimra-email")).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/v1/admin/go-live/{id}/send-to-zimra", new { to = "not-an-email", subject = "x", body = "y" })).StatusCode);
            var before = _f.Email.Messages.Count;
            var sent = await op.PostAsJsonAsync($"/api/v1/admin/go-live/{id}/send-to-zimra", new { to = "fdms@zimra.test", cc = "ops@platform.test", subject = mail.GetProperty("subject").GetString(), body });
            Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
            Assert.Equal("SentToZimra", (await sent.Json()).GetProperty("status").GetString());
            var toZimra = _f.Email.Messages.Skip(before).Single(m => m.To == "fdms@zimra.test");
            Assert.Equal(6, toZimra.Files.Count);
            Assert.All(toZimra.Files, f => Assert.True(f.Bytes[0] == (byte)'%'));
            Assert.Contains(_f.Email.Messages.Skip(before), m => m.To == "ops@platform.test");
            Assert.Contains("sent to ZIMRA", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J)).GetProperty("blocked").GetString());
            Assert.Equal("fdms@zimra.test", (await op.GetFromJsonAsync<JsonElement>($"/api/v1/admin/go-live/{id}/zimra-email", J)).GetProperty("to").GetString());   // remembered

            // ZIMRA approved and issued the live device: the operator records it and the company sees it
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/v1/admin/go-live/{id}/approve", new { liveDeviceId = 77001, activationKey = "123" })).StatusCode);
            var ok = await op.PostAsJsonAsync($"/api/v1/admin/go-live/{id}/approve", new { note = "ZIMRA approved", fee = 0, liveDeviceId = 77001, activationKey = "zx12cv34", serialNo = "SN-LIVE-1" });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            st = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J);
            Assert.True(st.GetProperty("approved").GetBoolean());
            var live = st.GetProperty("latest").GetProperty("liveDevice");
            Assert.Equal(77001, live.GetProperty("deviceId").GetInt32());
            Assert.Equal("ZX12CV34", live.GetProperty("activationKey").GetString());
            var reg = await co.Admin.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = 77001, activationKey = "ZX12CV34", serialNo = "SN-LIVE-1", modelName = "Server", modelVersion = "v1", environment = "Production" });
            Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

            // the operator's list of live companies says this one went through ZIMRA; a company the operator creates starts on demo only
            var liveList = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/go-live/live-companies", J);
            var mine = liveList.EnumerateArray().Single(c => c.GetProperty("clientId").GetGuid() == co.ClientId);
            Assert.True(mine.GetProperty("viaZimra").GetBoolean());
            Assert.Equal(77001, mine.GetProperty("zimraLiveDeviceId").GetInt32());
            Assert.Equal(1, mine.GetProperty("liveDevices").GetInt32());
            var fresh = await TestKit.NewCompanyAsync(_f, "Fresh Demo Co");
            Assert.False((await fresh.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J)).GetProperty("approved").GetBoolean());
            Assert.DoesNotContain(liveList.EnumerateArray(), c => c.GetProperty("clientId").GetGuid() == fresh.ClientId);
        }
    }
}
