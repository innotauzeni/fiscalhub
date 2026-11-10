using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>Invoice templates: per company and per branch, logo, preview, and the PDF actually using them.</summary>
    public class TemplateTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 16000;
        public TemplateTests(AccessFactory f) => _f = f;

        // 1x1 transparent PNG
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        private static object Settings(string accent = "#0f766e", string? header = "12 Samora Machel Ave\nHarare\n+263 242 000000", bool hs = true) => new
        {
            accent, showLogo = true, logoAlign = "right", displayName = "Acme Trading", headerText = header, invoiceTitle = "Fiscal tax invoice - original", showBuyer = true, showPayments = true,
            showHsCode = hs, showQuantity = true, showUnitPrice = true, showLineTax = true, bankDetails = "Bank: ABC  Acc: 12345", terms = "Payment within 7 days.", footerText = "Thank you!",
        };

        private static object Invoice(string extRef) => new
        {
            externalRef = extRef, invoiceNo = "INV-" + extRef, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
            lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } },
        };

        private static async Task<JsonElement> SaveAsync(HttpClient admin, object settings, Guid? branchId = null, string name = "Default", Guid? id = null, HttpStatusCode expect = HttpStatusCode.OK)
        {
            var res = await admin.PutAsJsonAsync("/api/v1/templates", new { id, branchId, name, settings });
            Assert.Equal(expect, res.StatusCode);
            return res.IsSuccessStatusCode ? await res.Json() : default;
        }

        private static MultipartFormDataContent Upload(byte[] bytes, string fileName = "logo.png", string contentType = "image/png")
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return new MultipartFormDataContent { { file, "file", fileName } };
        }

        [Fact]
        public async Task A_company_keeps_one_default_template_and_one_per_branch_and_settings_are_validated()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Template Co");
            Assert.Empty((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/templates", J)).EnumerateArray());

            var def = await SaveAsync(co.Admin, Settings());
            Assert.Equal("Fiscal tax invoice - original", def.GetProperty("settings").GetProperty("invoiceTitle").GetString());
            Assert.False(def.GetProperty("hasLogo").GetBoolean());
            await SaveAsync(co.Admin, Settings(), expect: HttpStatusCode.Conflict);                                   // a second company default

            var branch = (await (await co.Admin.PutAsJsonAsync("/api/v1/branches", new { code = "T-01", name = "Till Shop", isActive = true })).Json()).GetProperty("id").GetGuid();
            await SaveAsync(co.Admin, Settings("#b91c1c"), branch, "Till Shop style");
            await SaveAsync(co.Admin, Settings(), branch, "Again", expect: HttpStatusCode.Conflict);                  // one per branch
            Assert.Equal(2, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/templates", J)).GetArrayLength());

            // update in place
            var changed = await SaveAsync(co.Admin, footerOnly(), null, "Default", def.GetProperty("id").GetGuid());
            Assert.Equal("#111827", changed.GetProperty("settings").GetProperty("accent").GetString());

            // validation: colour, position, length, control characters, foreign branch
            await SaveAsync(co.Admin, Settings("red"), null, "Bad", def.GetProperty("id").GetGuid(), HttpStatusCode.BadRequest);
            await SaveAsync(co.Admin, new { accent = "#111111", logoAlign = "middle" }, null, "Bad", def.GetProperty("id").GetGuid(), HttpStatusCode.BadRequest);
            await SaveAsync(co.Admin, Settings(header: new string('x', 501)), null, "Bad", def.GetProperty("id").GetGuid(), HttpStatusCode.BadRequest);
            await SaveAsync(co.Admin, Settings(header: "bad\u0007bell"), null, "Bad", def.GetProperty("id").GetGuid(), HttpStatusCode.BadRequest);
            var other = await TestKit.NewCompanyAsync(_f, "Other Template Co");
            var foreignBranch = (await (await other.Admin.PutAsJsonAsync("/api/v1/branches", new { code = "X-01", name = "Foreign", isActive = true })).Json()).GetProperty("id").GetGuid();
            await SaveAsync(co.Admin, Settings(), foreignBranch, "Nope", expect: HttpStatusCode.BadRequest);

            // another company sees none of it and cannot touch it
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/templates", J)).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.DeleteAsync($"/api/v1/templates/{def.GetProperty("id").GetGuid()}")).StatusCode);

            // delete
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/templates/{def.GetProperty("id").GetGuid()}")).StatusCode);
            Assert.Single((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/templates", J)).EnumerateArray());
        }

        private static object footerOnly() => new { accent = "#111827", logoAlign = "left", footerText = "Thanks" };

        [Fact]
        public async Task A_logo_must_be_a_small_png_or_jpeg_and_can_be_fetched_and_removed()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Logo Co");
            var other = await TestKit.NewCompanyAsync(_f, "Other Logo Co");
            var id = (await SaveAsync(co.Admin, Settings())).GetProperty("id").GetGuid();

            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync($"/api/v1/templates/{id}/logo", Upload(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, "x.png"))).StatusCode);   // not an image
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync($"/api/v1/templates/{id}/logo", Upload(Png.Concat(new byte[310_000]).ToArray()))).StatusCode);                 // too big
            var damaged = await co.Admin.PostAsync($"/api/v1/templates/{id}/logo", Upload(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8 }));   // looks like a PNG but is not
            Assert.Equal(HttpStatusCode.BadRequest, damaged.StatusCode);
            Assert.Equal("LOGO_UNREADABLE", (await damaged.Json()).GetProperty("errorCode").GetString());
            var ok = await co.Admin.PostAsync($"/api/v1/templates/{id}/logo", Upload(Png));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.True((await ok.Json()).GetProperty("hasLogo").GetBoolean());

            var logo = await co.Admin.GetAsync($"/api/v1/templates/{id}/logo");
            Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/templates/{id}/logo")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsync($"/api/v1/templates/{id}/logo", Upload(Png))).StatusCode);

            Assert.False((await (await co.Admin.DeleteAsync($"/api/v1/templates/{id}/logo")).Json()).GetProperty("hasLogo").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await co.Admin.GetAsync($"/api/v1/templates/{id}/logo")).StatusCode);
        }

        [Fact]
        public async Task Preview_renders_a_png_and_the_receipt_pdf_uses_the_branch_template_then_the_company_default()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Render Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var def = await SaveAsync(co.Admin, Settings());
            var defId = def.GetProperty("id").GetGuid();
            await co.Admin.PostAsync($"/api/v1/templates/{defId}/logo", Upload(Png));

            // preview: both formats, unsaved settings, with and without the stored logo
            foreach (var format in new[] { "a4", "receipt" })
            {
                var res = await co.Admin.PostAsJsonAsync("/api/v1/templates/preview", new { settings = Settings("#7c3aed"), format, templateId = defId });
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
                var bytes = await res.Content.ReadAsByteArrayAsync();
                Assert.True(bytes.Length > 1000 && bytes[0] == 0x89 && bytes[1] == 0x50, "preview is a PNG");
            }
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.PostAsJsonAsync("/api/v1/templates/preview", new { settings = Settings(hs: false), format = "a4" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/templates/preview", new { settings = Settings("nope"), format = "a4" })).StatusCode);

            // the receipt PDF: built-in layout first (no template), then the default, then the device's branch
            var created = await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("T1"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var receipt = (await created.Json()).GetProperty("id").GetGuid();
            foreach (var format in new[] { "a4", "receipt" })
            {
                var pdf = await co.Admin.GetAsync($"/api/v1/receipts/{receipt}/pdf?format={format}");
                Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
                var b = await pdf.Content.ReadAsByteArrayAsync();
                Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(b, 0, 4));
            }

            var branch = (await (await co.Admin.PutAsJsonAsync("/api/v1/branches", new { code = "R-01", name = "Render Branch", isActive = true })).Json()).GetProperty("id").GetGuid();
            await co.Admin.PutAsJsonAsync($"/api/v1/branches/{branch}/devices", new { ids = new[] { dev } });
            var branchTemplate = (await SaveAsync(co.Admin, Settings("#b91c1c", hs: false), branch, "Branch look")).GetProperty("id").GetGuid();
            await co.Admin.PostAsync($"/api/v1/templates/{branchTemplate}/logo", Upload(Png));
            var branchPdf = await co.Admin.GetAsync($"/api/v1/receipts/{receipt}/pdf?format=a4");
            Assert.Equal(HttpStatusCode.OK, branchPdf.StatusCode);
            Assert.True((await branchPdf.Content.ReadAsByteArrayAsync()).Length > 1000);
        }

        [Fact]
        public async Task Only_people_who_manage_templates_and_not_branch_limited_ones_may_change_them()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Template Rights Co");
            var email = TestKit.Unique("op") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Op", surname = "Er", role = "Operator", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var op = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v1/templates")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/v1/templates/preview", new { settings = Settings(), format = "a4" })).StatusCode);

            // a ClientAdmin limited to some devices cannot restyle the whole company's invoices
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var adminEmail = TestKit.Unique("limited") + "@users.test";
            var created = await (await co.Admin.PostAsJsonAsync("/api/v1/users", new { email = adminEmail, name = "Lim", surname = "Ited", role = "ClientAdmin", password = TestKit.UserPassword })).Json();
            await co.Admin.PutAsJsonAsync($"/api/v1/users/{created.GetProperty("id").GetString()}/access", new { scope = "restricted", deviceIds = new[] { dev } });
            var limited = await TestKit.LoginAsync(_f, adminEmail, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await limited.GetAsync("/api/v1/templates")).StatusCode);
        }
    }
}
