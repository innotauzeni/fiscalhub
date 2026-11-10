using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Fiscal;
using Fdms.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fdms.Tests
{
    /// <summary>Headless integration (upload a document, get the fiscalised result back) and the activity trail of people and systems on the devices.</summary>
    public class IntegrationTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 26000;
        public IntegrationTests(AccessFactory f) => _f = f;

        private static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;   // the API leaves null fields out

        private static MultipartFormDataContent Upload(byte[] bytes, string name)
        {
            var c = new MultipartFormDataContent();
            var part = new ByteArrayContent(bytes); part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            c.Add(part, "file", name); return c;
        }

        /// <summary>The trail is written as the response completes, so give it a moment.</summary>
        private static async Task<JsonElement> WaitForActivityAsync(HttpClient who, string query, Func<JsonElement, bool> ok)
        {
            JsonElement last = default;
            for (var i = 0; i < 40; i++)
            {
                last = await who.GetFromJsonAsync<JsonElement>("/api/v1/activity" + query, J);
                if (ok(last)) return last;
                await Task.Delay(100);
            }
            return last;
        }

        [Fact]
        public async Task A_system_can_upload_a_document_get_it_fiscalised_and_download_the_result_without_using_the_console()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Headless Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-H");
            var tok = (await TestKit.NewTokenAsync(_f, co.Admin, dev)).Http;
            var docx = Fdms.Infrastructure.Import.WordInvoiceTemplate.Build(sample: true);

            // wrong type / not a document
            Assert.Equal(HttpStatusCode.BadRequest, (await tok.PostAsync($"/api/v1/devices/{dev}/fiscalise", Upload(new byte[] { 1, 2, 3 }, "x.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await tok.PostAsync($"/api/v1/devices/{dev}/fiscalise", Upload(new byte[] { 1, 2, 3, 4 }, "x.docx"))).StatusCode);

            // a dry run changes nothing and returns the draft
            var dry = await (await tok.PostAsync($"/api/v1/devices/{dev}/fiscalise?submit=false", Upload(docx, "invoice.docx"))).Json();
            Assert.False(dry.GetProperty("submitted").GetBoolean());
            Assert.False(Has(dry.GetProperty("items")[0], "fiscal"));   // nothing was fiscalised
            Assert.Empty((await tok.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("items").EnumerateArray());

            // the real call: fiscalised, with the fiscal result and download links in the same answer
            var res = await tok.PostAsync($"/api/v1/devices/{dev}/fiscalise", Upload(docx, "invoice.docx"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Json();
            Assert.Equal(1, body.GetProperty("succeeded").GetInt32());
            var item = body.GetProperty("items")[0];
            var fiscal = item.GetProperty("fiscal");
            Assert.Equal("INV-2041", fiscal.GetProperty("invoiceNo").GetString());
            Assert.True(fiscal.GetProperty("receiptGlobalNo").GetInt64() > 0);
            Assert.Equal(50.5m, fiscal.GetProperty("total").GetDecimal());
            var id = item.GetProperty("receiptId").GetGuid();

            // the printable invoice and the QR come straight back to the calling system
            var pdf = await tok.GetAsync(fiscal.GetProperty("links").GetProperty("pdfA4").GetString());
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync()).Take(4).ToArray()));
            Assert.Equal("image/png", (await tok.GetAsync(fiscal.GetProperty("links").GetProperty("qr").GetString())).Content.Headers.ContentType?.MediaType);

            // repeating the upload never fiscalises twice, and the result can be found again by the caller's own reference
            var again = await (await tok.PostAsync($"/api/v1/devices/{dev}/fiscalise", Upload(docx, "invoice.docx"))).Json();
            Assert.Equal(id, again.GetProperty("items")[0].GetProperty("receiptId").GetGuid());
            var found = await tok.GetFromJsonAsync<JsonElement>("/api/v1/receipts/lookup?externalRef=" + Uri.EscapeDataString("import:INV-2041"), J);
            Assert.Equal(1, found.GetProperty("count").GetInt32());
            Assert.Equal(id, found.GetProperty("items")[0].GetProperty("receiptId").GetGuid());
            Assert.Equal(1, (await tok.GetFromJsonAsync<JsonElement>("/api/v1/receipts/lookup?invoiceNo=INV-2041", J)).GetProperty("count").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await tok.GetAsync("/api/v1/receipts/lookup")).StatusCode);
            Assert.Equal(id, (await tok.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{id}/fiscal", J)).GetProperty("receiptId").GetGuid());

            // a token of another company cannot see it
            var other = await TestKit.NewCompanyAsync(_f, "Headless Other");
            var (od, _) = await TestKit.RegisterDeviceAsync(other.Admin, Interlocked.Increment(ref _seq), "SN-O");
            var otherTok = (await TestKit.NewTokenAsync(_f, other.Admin, od)).Http;
            Assert.Equal(HttpStatusCode.NotFound, (await otherTok.GetAsync($"/api/v1/receipts/{id}/fiscal")).StatusCode);
            Assert.Equal(0, (await otherTok.GetFromJsonAsync<JsonElement>("/api/v1/receipts/lookup?invoiceNo=INV-2041", J)).GetProperty("count").GetInt32());
        }

        [Fact]
        public async Task A_debit_note_adds_to_an_accepted_invoice_once_and_the_three_document_types_all_work()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Debit Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-D");
            var tok = (await TestKit.NewTokenAsync(_f, co.Admin, dev)).Http;

            var inv = await (await tok.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new { externalRef = "D-1", invoiceNo = "D-1", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } } })).Json();
            var id = inv.GetProperty("id").GetGuid();
            Assert.Equal("Submitted", inv.GetProperty("status").GetString());

            var body = new { reason = "Delivery charge left off", lines = new[] { new { name = "Delivery", hsCode = "99001000", quantity = 1, unitPrice = 23, taxPercent = 15 } } };
            var res = await tok.PostAsJsonAsync($"/api/v1/receipts/{id}/debit-note", body);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var dn = await res.Json();
            Assert.Equal("DebitNote", dn.GetProperty("type").GetString());
            Assert.Equal(23m, dn.GetProperty("total").GetDecimal());
            Assert.Equal("DN-D-1", dn.GetProperty("invoiceNo").GetString());
            Assert.Equal("Submitted", dn.GetProperty("status").GetString());

            // sent again it is the same note, not a second charge; a different charge becomes a second note
            Assert.Equal(dn.GetProperty("id").GetGuid(), (await (await tok.PostAsJsonAsync($"/api/v1/receipts/{id}/debit-note", body)).Json()).GetProperty("id").GetGuid());
            var second = await (await tok.PostAsJsonAsync($"/api/v1/receipts/{id}/debit-note", new { reason = "Interest", moneyType = "Card", lines = new[] { new { name = "Interest", hsCode = "99001000", quantity = 1, unitPrice = 5, taxPercent = 15 } } })).Json();
            Assert.Equal("DN-D-1-2", second.GetProperty("invoiceNo").GetString());

            // the credit note still works on the same invoice, and the notes cannot be stacked on a note or built from nothing
            Assert.Equal(HttpStatusCode.OK, (await tok.PostAsJsonAsync($"/api/v1/receipts/{id}/correct", new { reason = "Wrong item", issueReplacement = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await tok.PostAsJsonAsync($"/api/v1/receipts/{dn.GetProperty("id").GetGuid()}/debit-note", body)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await tok.PostAsJsonAsync($"/api/v1/receipts/{id}/debit-note", new { reason = "x", lines = body.lines })).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await tok.PostAsJsonAsync($"/api/v1/receipts/{id}/debit-note", new { reason = "No lines here", lines = Array.Empty<object>() })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await tok.PostAsJsonAsync($"/api/v1/receipts/{id}/debit-note", new { reason = "Free charge", lines = new[] { new { name = "Free", hsCode = "99001000", quantity = 1, unitPrice = 0, taxPercent = 15 } } })).StatusCode);

            // another company cannot add to it
            var other = await TestKit.NewCompanyAsync(_f, "Debit Other");
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsJsonAsync($"/api/v1/receipts/{id}/debit-note", body)).StatusCode);
        }

        [Fact]
        public async Task A_fiscal_day_left_open_is_closed_automatically_at_the_end_of_the_day_and_never_before()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Midnight Co");
            var (d1, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-M1");
            var (d2, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-M2");
            foreach (var d in new[] { d1, d2 }) Assert.True((await co.Admin.PostAsync($"/api/v1/devices/{d}/day/open", null)).IsSuccessStatusCode);
            async Task<string?> Status(Guid d) => (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/devices/{d}/day", J)).GetProperty("status").GetString();
            Assert.Equal("FiscalDayOpened", await Status(d1));

            FiscalDayAutoCloseJob Job(string? time = null) => new(_f.Services.GetRequiredService<IServiceScopeFactory>(),
                new ConfigurationBuilder().AddInMemoryCollection(time == null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["FiscalDay:AutoCloseTime"] = time }).Build(), NullLogger<FiscalDayAutoCloseJob>.Instance);
            var today = FdmsClock.CatNow().Date;

            // before the cut-off, and when switched off: nothing is closed
            Assert.Equal(0, await Job().RunOnceAsync(today.AddHours(12), CancellationToken.None));
            Assert.Equal(0, await Job().RunOnceAsync(today.AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(30), CancellationToken.None));   // a day opened today is not yet due on yesterday's cut-off
            Assert.Equal(0, await Job("").RunOnceAsync(today.AddHours(23).AddMinutes(59).AddSeconds(30), CancellationToken.None));
            Assert.Equal("FiscalDayOpened", await Status(d1));

            // at 23:59 every open day is closed, and the work is recorded
            Assert.True(await Job().RunOnceAsync(today.AddHours(23).AddMinutes(59).AddSeconds(30), CancellationToken.None) >= 2);   // (other tests in this class may have left days open too)
            Assert.Equal("FiscalDayClosed", await Status(d1)); Assert.Equal("FiscalDayClosed", await Status(d2));
            Assert.Equal(0, await Job().RunOnceAsync(today.AddHours(23).AddMinutes(59).AddSeconds(40), CancellationToken.None));   // nothing left to close

            // a day still open after midnight (the service was down at 23:59) is caught up with straight away
            Assert.True((await co.Admin.PostAsync($"/api/v1/devices/{d1}/day/open", null)).IsSuccessStatusCode);
            Assert.True(await Job().RunOnceAsync(today.AddDays(1).AddMinutes(30), CancellationToken.None) >= 1);
            Assert.Equal("FiscalDayClosed", await Status(d1));

            // a custom time works (21:00)
            Assert.True((await co.Admin.PostAsync($"/api/v1/devices/{d2}/day/open", null)).IsSuccessStatusCode);
            Assert.Equal(0, await Job("21:00").RunOnceAsync(today.AddHours(20), CancellationToken.None));
            Assert.True(await Job("21:00").RunOnceAsync(today.AddDays(1).AddHours(21).AddMinutes(1), CancellationToken.None) >= 1);   // the next 21:00 (the day may have been opened after today's)
            Assert.Equal("FiscalDayClosed", await Status(d2));

            var audit = await TestKit.AuditAsync(_f, co.ClientId, "action=fiscalday.autoclose");
            Assert.True(audit.GetProperty("total").GetInt32() >= 4);
        }

        [Fact]
        public async Task The_dashboard_says_whether_each_fiscal_day_is_open_and_what_is_still_waiting_for_zimra()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Dash Day Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-DD");
            var one = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", J)).GetProperty("deviceList")[0];
            Assert.Equal("FiscalDayClosed", one.GetProperty("dayStatus").GetString());
            Assert.False(Has(one, "dayOpenedAt"));

            Assert.True((await co.Admin.PostAsync($"/api/v1/devices/{dev}/day/open", null)).IsSuccessStatusCode);
            var d = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", J);
            one = d.GetProperty("deviceList")[0];
            Assert.Equal("FiscalDayOpened", one.GetProperty("dayStatus").GetString());
            Assert.True(Has(one, "dayOpenedAt"));
            Assert.Equal(24, one.GetProperty("dayMaxHrs").GetInt32());
            Assert.Equal(0, one.GetProperty("pendingReceipts").GetInt32());
            Assert.Equal("23:59", d.GetProperty("autoCloseTime").GetString());   // so the console can say when a forgotten day closes by itself
        }

        [Fact]
        public async Task Activity_shows_what_people_and_systems_did_on_each_device_to_those_who_may_see_it_only()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Tracked Co");
            var other = await TestKit.NewCompanyAsync(_f, "Tracked Other");
            var (dev, zid) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-T");
            var (dev2, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-T2");
            var tok = (await TestKit.NewTokenAsync(_f, co.Admin, dev, name: "ERP link")).Http;

            Assert.Equal(HttpStatusCode.OK, (await tok.GetAsync($"/api/v1/devices/{dev}")).StatusCode);
            Assert.True((int)(await tok.GetAsync($"/api/v1/devices/{dev2}")).StatusCode >= 400);    // refused to this token: recorded as an error
            var docx = Fdms.Infrastructure.Import.WordInvoiceTemplate.Build(sample: true);
            Assert.Equal(HttpStatusCode.OK, (await tok.PostAsync($"/api/v1/devices/{dev}/fiscalise", Upload(docx, "i.docx"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync("/api/v1/receipts")).StatusCode);

            // the company reads its own trail: the token's calls carry its name and device, the person's carry theirs
            var all = await WaitForActivityAsync(co.Admin, "?pageSize=100", r => r.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("path").GetString()!.EndsWith("/receipts") && i.GetProperty("actorType").GetString() == "user"));
            var items = all.GetProperty("items").EnumerateArray().ToList();
            var up = items.First(i => i.GetProperty("path").GetString()!.EndsWith("/fiscalise"));
            Assert.Equal("token", up.GetProperty("actorType").GetString());
            Assert.Contains("ERP link", up.GetProperty("actorName").GetString());
            Assert.Equal(dev, up.GetProperty("deviceId").GetGuid());
            Assert.Equal(zid, up.GetProperty("zimraDeviceId").GetInt32());
            Assert.Equal(200, up.GetProperty("statusCode").GetInt32());
            Assert.Contains(items, i => i.GetProperty("actorType").GetString() == "user" && i.GetProperty("path").GetString() == "/api/v1/receipts");
            Assert.Contains(items, i => i.GetProperty("statusCode").GetInt32() >= 400);
            Assert.DoesNotContain(items, i => i.GetProperty("path").GetString()!.StartsWith("/api/v1/activity"));    // reading the trail is not part of it

            // filters
            Assert.All((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/activity?actorType=token&pageSize=100", J)).GetProperty("items").EnumerateArray(), i => Assert.Equal("token", i.GetProperty("actorType").GetString()));
            Assert.All((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/activity?outcome=errors", J)).GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("statusCode").GetInt32() >= 400));
            Assert.All((await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/activity?deviceId={dev}", J)).GetProperty("items").EnumerateArray(), i => Assert.Equal(dev, i.GetProperty("deviceId").GetGuid()));

            // per device: last seen, calls, the receipt it issued, its live tokens
            var devices = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/activity/devices", J)).EnumerateArray().ToList();
            Assert.Equal(2, devices.Count);
            var d1 = devices.First(d => d.GetProperty("deviceId").GetGuid() == dev);
            Assert.True(d1.GetProperty("calls24h").GetInt32() >= 2);
            Assert.Equal(1, d1.GetProperty("tokenCount").GetInt32());
            Assert.True(Has(d1, "lastTokenCallAt"));
            Assert.False(Has(devices.First(d => d.GetProperty("deviceId").GetGuid() == dev2), "lastTokenCallAt"));

            // another company sees none of it; a token cannot read the trail; a viewer role is refused
            Assert.DoesNotContain((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/activity?pageSize=100", J)).GetProperty("items").EnumerateArray(), i => i.GetProperty("clientId").GetGuid() == co.ClientId);
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/activity/devices", J)).EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await tok.GetAsync("/api/v1/activity")).StatusCode);
            var email = TestKit.Unique("view") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "V", surname = "W", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await (await TestKit.LoginAsync(_f, email, TestKit.UserPassword)).GetAsync("/api/v1/activity")).StatusCode);

            // the operator sees every company and can narrow to one
            var op = await TestKit.AdminAsync(_f);
            var opAll = await op.GetFromJsonAsync<JsonElement>($"/api/v1/activity?clientId={co.ClientId}&pageSize=100", J);
            Assert.All(opAll.GetProperty("items").EnumerateArray(), i => Assert.Equal(co.ClientId, i.GetProperty("clientId").GetGuid()));
            Assert.Equal("Tracked Co", opAll.GetProperty("items")[0].GetProperty("clientName").GetString());
        }
    }
}
