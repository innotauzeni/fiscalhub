using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Infrastructure.Services;

namespace Fdms.Tests
{
    /// <summary>The richer invoice layout, and reading a company's existing invoice with the AI into the same settings the editor uses.</summary>
    public class TemplateDesignTests : IClassFixture<AiFactory>
    {
        private const string Key = "AIzaSyTESTKEYTESTKEYTESTKEY1234567890ab";
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        public TemplateDesignTests(AiFactory f) => _f = f;

        private async Task AiOnAsync(bool on = true)
        {
            var op = await TestKit.AdminAsync(_f);
            var res = await op.PutAsJsonAsync("/api/v1/admin/ai", new { apiKey = on ? Key : (string?)null, clearKey = !on, model = "gemini-2.5-flash", chatEnabled = true, reportsEnabled = true, summariesEnabled = false, dailyChatLimit = 500, dailyReportLimitPerCompany = 100 });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        private static object Settings(Action<Dictionary<string, object?>>? tweak = null)
        {
            var d = new Dictionary<string, object?>
            {
                ["accent"] = "#0f766e", ["showLogo"] = false, ["logoAlign"] = "left", ["showBuyer"] = true, ["showPayments"] = true, ["showHsCode"] = false, ["showQuantity"] = true, ["showUnitPrice"] = true, ["showLineTax"] = true,
                ["headerStyle"] = "plain", ["titleStyle"] = "band", ["titleAlign"] = "center", ["tableStyle"] = "lines", ["partiesLayout"] = "stacked", ["totalsAlign"] = "full", ["qrPosition"] = "center",
                ["logoSize"] = "medium", ["margins"] = "normal", ["fontSize"] = 10,
            };
            tweak?.Invoke(d);
            return d;
        }

        [Fact]
        public async Task Every_layout_choice_prints_a_valid_invoice_and_unknown_choices_are_refused()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Design Co");
            async Task<HttpResponseMessage> Preview(object s, string fmt = "a4") => await co.Admin.PostAsJsonAsync("/api/v1/templates/preview", new { settings = s, format = fmt });
            async Task Ok(Action<Dictionary<string, object?>> t, string fmt = "a4")
            {
                var r = await Preview(Settings(t), fmt);
                Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
                Assert.Equal("image/png", r.Content.Headers.ContentType?.MediaType);
            }
            await Ok(_ => { });
            foreach (var v in new[] { "plain", "band" }) await Ok(d => d["headerStyle"] = v);
            foreach (var v in new[] { "band", "plain", "underline" }) foreach (var a in new[] { "left", "center", "right" }) await Ok(d => { d["titleStyle"] = v; d["titleAlign"] = a; });
            foreach (var v in new[] { "lines", "striped", "grid", "solid" }) await Ok(d => d["tableStyle"] = v);
            foreach (var v in new[] { "stacked", "columns" }) await Ok(d => d["partiesLayout"] = v);
            foreach (var v in new[] { "full", "right" }) await Ok(d => d["totalsAlign"] = v);
            foreach (var v in new[] { "left", "center", "right", "top-left", "top-right", "with-totals" }) foreach (var z in new[] { "small", "large" }) await Ok(d => { d["qrPosition"] = v; d["qrSize"] = z; });
            foreach (var v in new[] { "compact", "normal", "relaxed" }) foreach (var st in new[] { "lines", "striped", "grid", "solid" }) await Ok(d => { d["rowSpacing"] = v; d["tableStyle"] = st; d["labelGrandTotal"] = "AMOUNT DUE"; });
            await Ok(d => d["qrPosition"] = "top-right", "receipt");   // the roll keeps its QR at the foot
            foreach (var v in new[] { "narrow", "normal", "wide" }) await Ok(d => d["margins"] = v);
            await Ok(d => { d["columns"] = new[] { "qty", "item", "tax", "price", "hs", "total" }; d["labelItem"] = "Description"; d["labelTotal"] = "Amount"; d["labelInvoiceNo"] = "Tax Invoice No."; d["labelBuyer"] = "Bill to"; d["fontSize"] = 12; });
            await Ok(d => { d["headerStyle"] = "band"; d["titleStyle"] = "underline"; d["tableStyle"] = "solid"; d["partiesLayout"] = "columns"; d["totalsAlign"] = "right"; });
            await Ok(d => { d["columns"] = new[] { "item", "tax", "total" }; d["tableStyle"] = "striped"; }, "receipt");   // the roll ignores what it has no room for

            foreach (var bad in new Action<Dictionary<string, object?>>[]
            {
                d => d["tableStyle"] = "fancy", d => d["headerStyle"] = "neon", d => d["fontSize"] = 40, d => d["columns"] = new[] { "item", "cost" }, d => d["titleAlign"] = "diagonal", d => d["labelTotal"] = new string('x', 31),
            })
                Assert.Equal(HttpStatusCode.BadRequest, (await Preview(Settings(bad))).StatusCode);

            // the standard columns and the cleaned custom list
            Assert.Equal(new[] { "item", "qty", "price", "total" }, ReceiptPdfRenderer.ResolveColumns(new InvoiceTemplateSettings(), false));
            Assert.Equal(new[] { "qty", "item", "price", "total" }, ReceiptPdfRenderer.ResolveColumns(new InvoiceTemplateSettings { Columns = new() { "qty", "item", "bogus", "total", "price", "qty" } }, false));
            Assert.Equal(new[] { "item", "total" }, ReceiptPdfRenderer.ResolveColumns(new InvoiceTemplateSettings { Columns = new() { "hs", "tax" } }, true));

            // a saved template keeps every choice
            var saved = await (await co.Admin.PutAsJsonAsync("/api/v1/templates", new { id = (Guid?)null, branchId = (Guid?)null, name = "Their look", settings = Settings(d => { d["tableStyle"] = "grid"; d["columns"] = new[] { "item", "qty", "total" }; d["labelItem"] = "Description"; }) })).Json();
            var s = saved.GetProperty("settings");
            Assert.Equal("grid", s.GetProperty("tableStyle").GetString()); Assert.Equal("Description", s.GetProperty("labelItem").GetString());
            Assert.Equal(new[] { "item", "qty", "total" }, s.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
        }

        [Fact]
        public async Task A_template_says_how_many_devices_print_with_it_so_an_unused_one_is_not_a_mystery()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Count Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, 53001, "SN-C");
            var branch = await (await co.Admin.PutAsJsonAsync("/api/v1/branches", new { code = "MAG", name = "Magwegwe", isActive = true })).Json();
            var branchId = branch.GetProperty("id").GetGuid();

            // a look for a branch that holds no device: nobody prints with it (the device has no branch, and there is no company default)
            var look = await (await co.Admin.PutAsJsonAsync("/api/v1/templates", new { id = (Guid?)null, branchId, name = "Magwegwe look", settings = Settings() })).Json();
            Assert.Equal(0, look.GetProperty("deviceCount").GetInt32());

            // once the branch holds the device, it does
            await co.Admin.PutAsJsonAsync($"/api/v1/branches/{branchId}/devices", new { ids = new[] { dev } });
            var list = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/templates", J);
            Assert.Equal(1, list[0].GetProperty("deviceCount").GetInt32());

            // a company default serves only the devices whose branch has no look of its own
            var def = await (await co.Admin.PutAsJsonAsync("/api/v1/templates", new { id = (Guid?)null, branchId = (Guid?)null, name = "Default", settings = Settings() })).Json();
            Assert.Equal(0, def.GetProperty("deviceCount").GetInt32());
            await co.Admin.PutAsJsonAsync($"/api/v1/branches/{branchId}/devices", new { ids = Array.Empty<Guid>() });
            var after = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/templates", J)).EnumerateArray().ToList();
            Assert.Equal(1, after.First(t => t.GetProperty("name").GetString() == "Default").GetProperty("deviceCount").GetInt32());
            Assert.Equal(0, after.First(t => t.GetProperty("name").GetString() == "Magwegwe look").GetProperty("deviceCount").GetInt32());
        }

        [Fact]
        public void Placeholders_in_the_wording_are_filled_from_the_company_and_never_printed_as_brackets()
        {
            const string header = "Address: [Company Address], Harare, Zimbabwe\nTel: [Telephone]• Email: [Email] Website: [Website]\nTIN: [TAXPAYER TIN] VAT No: [VAT NUMBER]";
            var values = new Dictionary<string, string?> { ["address"] = "12 Samora Machel Ave", ["phone"] = "+263 242 700 000", ["email"] = "accounts@texgold.co.zw", ["website"] = null, ["tin"] = "2900001901", ["vat"] = "221208123", ["company"] = "Texgold" };
            var filled = ReceiptPdfRenderer.Fill(header, values)!.Split('\n');
            Assert.Equal("Address: 12 Samora Machel Ave, Harare, Zimbabwe", filled[0]);
            Assert.Equal("Tel: +263 242 700 000• Email: accounts@texgold.co.zw", filled[1]);   // no website: its label goes with it
            Assert.Equal("TIN: 2900001901 VAT No: 221208123", filled[2]);

            // nothing known: the lines that were only placeholders disappear and no label is left hanging
            var none = ReceiptPdfRenderer.Fill(header, new Dictionary<string, string?>())!;
            Assert.Equal("Harare, Zimbabwe", none);
            Assert.DoesNotContain("[", none);

            // other spellings, {{braces}}, and what it does not know is left alone
            Assert.Equal("Call +263 242 700 000 - Texgold [Terms]", ReceiptPdfRenderer.Fill("Call {{ Phone Number }} - [company name] [Terms]", values));
            Assert.Equal("plain text", ReceiptPdfRenderer.Fill("plain text", values)); Assert.Null(ReceiptPdfRenderer.Fill(null, values));
        }

        [Fact]
        public void Payment_placeholders_take_the_invoices_real_method_and_the_banks_account()
        {
            const string block = "Payment Method: [BANK / ECOCASH / CASH / CARD]\nBank: [BANK NAME] Account: [ACCOUNT NUMBER]\nReference: INV-2026-000001";
            var values = new Dictionary<string, string?> { ["paymentmethod"] = "Cash / Card", ["bank"] = "NMB", ["accountnumber"] = "40123456789", ["invoiceno"] = "INV80220224" };
            var lines = ReceiptPdfRenderer.Fill(block, values)!.Split('\n');
            Assert.Equal("Payment Method: Cash / Card", lines[0]);
            Assert.Equal("Bank: NMB Account: 40123456789", lines[1]);
            Assert.Equal("Reference: INV-2026-000001", lines[2]);   // Fill alone leaves literal text; the renderer swaps a sample number for the real one

            // no bank on file: the bank line goes away entirely, the payment method stays
            var noBank = ReceiptPdfRenderer.Fill(block, new Dictionary<string, string?> { ["paymentmethod"] = "Cash" })!.Split('\n');
            Assert.Equal(new[] { "Payment Method: Cash", "Reference: INV-2026-000001" }, noBank);
        }

        [Fact]
        public async Task Contact_details_in_a_template_are_kept_and_an_invoice_with_placeholders_prints()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Fill Co");
            var saved = await (await co.Admin.PutAsJsonAsync("/api/v1/templates", new
            {
                id = (Guid?)null, branchId = (Guid?)null, name = "Mine",
                settings = Settings(d => { d["headerText"] = "Address: [Company Address]\nWebsite: [Website]\nTIN: [TAXPAYER TIN]"; d["address"] = "12 Samora Machel Ave"; d["phone"] = "+263 242 700 000"; d["email"] = "a@fill.test"; d["website"] = "www.fill.test"; }),
            })).Json();
            var s = saved.GetProperty("settings");
            Assert.Equal("www.fill.test", s.GetProperty("website").GetString()); Assert.Equal("12 Samora Machel Ave", s.GetProperty("address").GetString());
            var res = await co.Admin.PostAsJsonAsync("/api/v1/templates/preview", new { settings = s, format = "a4" });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        private static MultipartFormDataContent Upload(byte[] bytes, string name = "invoice.pdf", string type = "application/pdf")
        {
            var m = new MultipartFormDataContent(); var c = new ByteArrayContent(bytes); c.Headers.ContentType = new MediaTypeHeaderValue(type); m.Add(c, "file", name); return m;
        }

        private static readonly byte[] Pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF");

        [Fact]
        public async Task An_existing_invoice_is_read_into_validated_editor_settings_and_nothing_is_kept()
        {
            await AiOnAsync();
            var co = await TestKit.NewCompanyAsync(_f, "Inherit Co");
            _f.Gemini.Calls.Clear();
            _f.Gemini.Handler = _ => new GeminiResult(JsonSerializer.Serialize(new
            {
                summary = "A teal invoice with a grid table.", accent = "#0F766E", hasLogo = true, logoAlign = "right", headerStyle = "band", titleStyle = "underline", titleAlign = "left", tableStyle = "grid",
                partiesLayout = "columns", totalsAlign = "right", margins = "narrow", fontSize = 9, invoiceTitle = "FISCAL TAX INVOICE - COPY", displayName = "Inherit Co Pvt Ltd", headerText = "12 Samora Machel Ave\nHarare\ninfo@inherit.test",
                columns = new[] { "item", "qty", "price", "tax", "total" }, labels = new { invoiceNo = "Invoice #", buyer = "Bill to", item = "Description", total = "Amount" },
                bankDetails = "NMB 40123456789", terms = "Pay in 30 days", footerText = "Thank you", showBuyer = true, showPayments = false,
            }), null);

            var res = await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(Pdf));
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var a = await res.Json(); var s = a.GetProperty("settings");
            Assert.Equal("#0f766e", s.GetProperty("accent").GetString()); Assert.Equal("grid", s.GetProperty("tableStyle").GetString());
            Assert.Equal("band", s.GetProperty("headerStyle").GetString()); Assert.Equal("columns", s.GetProperty("partiesLayout").GetString()); Assert.Equal("right", s.GetProperty("totalsAlign").GetString());
            Assert.Equal(9, s.GetProperty("fontSize").GetInt32()); Assert.Equal("Description", s.GetProperty("labelItem").GetString()); Assert.Equal("Bill to", s.GetProperty("labelBuyer").GetString());
            Assert.Equal(new[] { "item", "qty", "price", "tax", "total" }, s.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
            Assert.Contains("12 Samora Machel Ave", s.GetProperty("headerText").GetString()); Assert.False(s.GetProperty("showPayments").GetBoolean());
            Assert.Contains("logo", string.Join(" ", a.GetProperty("notes").EnumerateArray().Select(n => n.GetString())), StringComparison.OrdinalIgnoreCase);

            // what went to Google: the instructions and the file itself, with the rule never to copy customer data
            var call = _f.Gemini.Calls.Last();
            Assert.Equal("application/pdf", call.Messages[0].File!.MimeType); Assert.Equal(Pdf, call.Messages[0].File!.Data);
            Assert.Contains("Never copy the customer's name", call.System);

            // the answer is settings the editor can render straight away
            var preview = await co.Admin.PostAsJsonAsync("/api/v1/templates/preview", new { settings = s, format = "a4" });
            Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            Assert.Empty((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/templates", J)).EnumerateArray());   // nothing was saved

            // odd answers never break it: unknown words are dropped with a note and the standard value stays
            _f.Gemini.Handler = _ => new GeminiResult("{\"accent\":\"teal\",\"tableStyle\":\"zebra\",\"columns\":[\"cost\"],\"fontSize\":99,\"labels\":{\"total\":\"" + new string('x', 80) + "\"}}", null);
            var odd = await (await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(Pdf))).Json();
            var os = odd.GetProperty("settings");
            Assert.Equal("lines", os.GetProperty("tableStyle").GetString()); Assert.Equal("#2563eb", os.GetProperty("accent").GetString()); Assert.Equal(13, os.GetProperty("fontSize").GetInt32());
            Assert.True(odd.GetProperty("notes").GetArrayLength() >= 3);
            Assert.Equal(30, os.GetProperty("labelTotal").GetString()!.Length);

            // an image works too; text that is not an answer is a clear 422
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
            Assert.True((await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(png, "i.png", "image/png"))).IsSuccessStatusCode);
            Assert.Equal("image/png", _f.Gemini.Calls.Last().Messages[0].File!.MimeType);
            _f.Gemini.Handler = _ => new GeminiResult("I cannot do that", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(Pdf))).StatusCode);
            _f.Gemini.Handler = _ => new GeminiResult(null, "Gemini is busy or its quota is used up.", 429);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(Pdf))).StatusCode);
        }

        [Fact]
        public async Task Reading_an_invoice_refuses_other_files_big_files_and_people_without_the_right_and_needs_the_ai_switched_on()
        {
            await AiOnAsync();
            var co = await TestKit.NewCompanyAsync(_f, "Guard Co");
            _f.Gemini.Handler = _ => new GeminiResult("{}", null);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(System.Text.Encoding.ASCII.GetBytes("MZ not a document"), "x.pdf"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(System.Text.Encoding.ASCII.GetBytes("<svg onload=alert(1)>"), "x.svg", "image/svg+xml"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(Pdf.Concat(new byte[4_100_000]).ToArray()))).StatusCode);

            var email = TestKit.Unique("viewer") + "@guard.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Vera", surname = "Viewer", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var viewer = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/templates/analyze", Upload(Pdf))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().PostAsync("/api/v1/templates/analyze", Upload(Pdf))).StatusCode);

            await AiOnAsync(false);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await co.Admin.PostAsync("/api/v1/templates/analyze", Upload(Pdf))).StatusCode);
            await AiOnAsync();
        }
    }
}
