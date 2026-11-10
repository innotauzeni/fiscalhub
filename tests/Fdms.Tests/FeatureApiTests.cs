using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Fdms.Tests
{
    /// <summary>Same harness as GatewayApiTests, with webhook delivery to local targets switched on.</summary>
    public sealed class FeatureFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly string _keys = Path.Combine(Path.GetTempPath(), "fdms-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestKit.JwtKey,
                ["Seed:AdminEmail"] = TestKit.AdminEmail,
                ["Seed:AdminPassword"] = TestKit.AdminPassword,
                ["Seed:ForcePasswordChange"] = "false",
                ["RateLimit:LoginPerMinute"] = "100000",
                ["Auth:ActiveCacheSeconds"] = "0",
                ["DataProtection:KeysPath"] = _keys,
                ["RateLimit:PerMinute"] = "100000",
                ["Fdms:DistributedLocks"] = "false",
                ["Webhooks:AllowInsecureTargets"] = "true",
            }));
            builder.ConfigureServices(s =>
            {
                TestDb.Use(s, _db);
                s.RemoveAll<IFdmsGateway>();
                s.AddSingleton<IFdmsGateway>(Fdms);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    public class FeatureApiTests : IClassFixture<FeatureFactory>
    {
        private readonly FeatureFactory _f;
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private static int _seq = 7000;
        public FeatureApiTests(FeatureFactory f) => _f = f;

        /// <summary>Token client for the device (integration calls) plus the signed-in administrator (webhooks, registering devices).</summary>
        private async Task<(HttpClient Http, Guid Device, HttpClient User)> SetupFullAsync(string name)
        {
            var co = await TestKit.NewCompanyAsync(_f, name);
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-9");
            return ((await TestKit.NewTokenAsync(_f, co.Admin, dev)).Http, dev, co.Admin);
        }

        private async Task<(HttpClient Http, Guid Device)> SetupAsync(string name)
        {
            var (http, dev, _) = await SetupFullAsync(name);
            return (http, dev);
        }

        private static MultipartFormDataContent File(byte[] bytes, string fileName)
        {
            var c = new MultipartFormDataContent();
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            c.Add(part, "file", fileName);
            return c;
        }

        private static byte[] Xlsx(Action<ClosedXML.Excel.IXLWorksheet> fill)
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();
            fill(wb.AddWorksheet("Invoices"));
            using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
        }

        private static void Row(ClosedXML.Excel.IXLWorksheet ws, int r, params object[] v) { for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = v[i]?.ToString() ?? ""; }
        private static void Header(ClosedXML.Excel.IXLWorksheet ws) { string[] h = { "Invoice No", "Currency", "Receipt Type", "Original Invoice No", "Notes", "Item Description", "HS Code", "Quantity", "Unit Price", "Tax Percent", "Payment Method" }; for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i]; }

        [Fact]
        public async Task Lines_can_name_a_tax_by_percent_or_exempt_and_preview_does_not_consume_numbers()
        {
            var (c, dev) = await SetupAsync("Percent Co");
            var body = new
            {
                externalRef = "P1", invoiceNo = "P-1", receiptType = "FiscalInvoice", currency = "usd", linesTaxInclusive = true,
                lines = new object[]
                {
                    new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 },
                    new { name = "Bread", hsCode = "19059010", quantity = 2, unitPrice = 1, isExempt = true },
                },
            };
            var prev = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts/preview", body);
            Assert.Equal(HttpStatusCode.OK, prev.StatusCode);
            var p = await prev.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(117m, p.GetProperty("total").GetDecimal());
            Assert.Equal(2, p.GetProperty("taxes").GetArrayLength());
            Assert.Empty((await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("items").EnumerateArray());   // nothing stored

            var real = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", body);
            Assert.Equal(HttpStatusCode.Created, real.StatusCode);
            Assert.Equal(1, (await real.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("receiptCounter").GetInt32());   // preview used no number

            var bad = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts/preview", new { externalRef = "P2", invoiceNo = "P-2", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                lines = new[] { new { name = "X", hsCode = "85456852", quantity = 1, unitPrice = 1, taxPercent = 99 } } });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

            // an HS code of the wrong length is refused up front (ZIMRA would only flag it with RCPT048 after numbering the receipt)
            var shortHs = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new { externalRef = "P3", invoiceNo = "P-3", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                lines = new[] { new { name = "Maize", hsCode = "0199199", quantity = 1, unitPrice = 1, taxPercent = 15 } } });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, shortHs.StatusCode);
            Assert.Equal("RCPT048", (await shortHs.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("errorCode").GetString());
            Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("items").GetArrayLength());   // no receipt (and no number) was consumed
        }

        [Fact]
        public async Task Excel_import_previews_then_fiscalises_once_and_handles_credit_notes()
        {
            var (c, dev) = await SetupAsync("Excel Co");
            var xlsx = Xlsx(ws =>
            {
                Header(ws);
                Row(ws, 2, "X-1", "USD", "", "", "", "Shoes", "85456852", 2, 25, 15, "Cash");
                Row(ws, 3, "", "", "", "", "", "Bread", "19059010", 5, 1, "Exempt", "");
                Row(ws, 4, "X-2", "USD", "", "", "", "Belt", "42033000", 1, 10, 15, "EcoCash");
            });

            // preview: nothing fiscalised
            var prevText = await (await c.PostAsync($"/api/v1/devices/{dev}/import/excel", File(xlsx, "inv.xlsx"))).Content.ReadAsStringAsync();
            var prev = JsonDocument.Parse(prevText).RootElement;
            Assert.True(prev.GetProperty("succeeded").GetInt32() == 2, prevText);
            Assert.False(prev.GetProperty("submitted").GetBoolean());
            Assert.Equal(2, prev.GetProperty("succeeded").GetInt32());
            Assert.Equal(55m, prev.GetProperty("items")[0].GetProperty("total").GetDecimal());   // 2*25 + 5 exempt, tax-inclusive
            Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());

            // submit
            var sub = await c.PostAsync($"/api/v1/devices/{dev}/import/excel?submit=true", File(xlsx, "inv.xlsx"));
            var s = await sub.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(2, s.GetProperty("succeeded").GetInt32());
            Assert.All(s.GetProperty("items").EnumerateArray(), i => Assert.Equal("Submitted", i.GetProperty("status").GetString()));

            // same file again: idempotent
            var again = await (await c.PostAsync($"/api/v1/devices/{dev}/import/excel?submit=true", File(xlsx, "inv.xlsx"))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(s.GetProperty("items")[0].GetProperty("receiptId").GetGuid(), again.GetProperty("items")[0].GetProperty("receiptId").GetGuid());
            Assert.Equal(2, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());

            // credit note by original invoice number
            var cn = Xlsx(ws => { Header(ws); Row(ws, 2, "CN-X-1", "USD", "CreditNote", "X-1", "Returned", "Shoes", "85456852", 1, 25, 15, "Cash"); });
            var cnRes = await (await c.PostAsync($"/api/v1/devices/{dev}/import/excel?submit=true", File(cn, "cn.xlsx"))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(1, cnRes.GetProperty("succeeded").GetInt32());
            Assert.Equal(-25m, cnRes.GetProperty("items")[0].GetProperty("total").GetDecimal());
        }

        [Fact]
        public async Task A_file_with_a_bad_row_submits_nothing()
        {
            var (c, dev) = await SetupAsync("Dirty Co");
            var xlsx = Xlsx(ws => { Header(ws); Row(ws, 2, "D-1", "USD", "", "", "", "Good", "85456852", 1, 10, 15, ""); Row(ws, 3, "D-1", "USD", "", "", "", "Bad", "85456852", 1, "xyz", 15, ""); });
            var res = await (await c.PostAsync($"/api/v1/devices/{dev}/import/excel?submit=true", File(xlsx, "d.xlsx"))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(0, res.GetProperty("succeeded").GetInt32());
            Assert.Contains("Row 3", res.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).Aggregate((a, b) => a + b));
            Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());
        }

        [Fact]
        public async Task Wrong_file_types_are_refused()
        {
            var (c, dev) = await SetupAsync("Types Co");
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/v1/devices/{dev}/import/excel", File(Encoding.UTF8.GetBytes("hello"), "x.xlsx"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/v1/devices/{dev}/import/excel", File(new byte[] { 0x50, 0x4B, 1, 2 }, "x.csv"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/v1/devices/{dev}/import/pdf", File(Encoding.UTF8.GetBytes("not a pdf"), "x.pdf"))).StatusCode);
        }

        // ------------------------------------------------------------------ the Word invoice

        private static string WordText(byte[] docx) { using var ms = new MemoryStream(docx); return Fdms.Infrastructure.Import.DocxTextExtractor.Extract(ms); }

        [Fact]
        public void The_word_invoice_template_is_a_well_formed_docx_with_every_fiscal_field()
        {
            foreach (var sample in new[] { false, true })
            {
                var bytes = Fdms.Infrastructure.Import.WordInvoiceTemplate.Build(sample);
                using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
                var names = zip.Entries.Select(e => e.FullName).ToList();
                Assert.Equal("[Content_Types].xml", names[0]);   // the package index comes first
                foreach (var part in new[] { "_rels/.rels", "word/document.xml", "word/styles.xml", "word/settings.xml", "word/footer1.xml", "word/_rels/document.xml.rels", "docProps/core.xml" }) Assert.Contains(part, names);
                foreach (var e in zip.Entries) { using var r = e.Open(); System.Xml.Linq.XDocument.Load(r); }   // every part is well-formed XML
            }
            var xml = new System.IO.StreamReader(new System.IO.Compression.ZipArchive(new MemoryStream(Fdms.Infrastructure.Import.WordInvoiceTemplate.Build())).GetEntry("word/document.xml")!.Open()).ReadToEnd();
            foreach (var needed in new[] { "TAX INVOICE", "Invoice No", "Bill to", "Seller TIN", "Seller VAT number", "Buyer TIN", "Buyer VAT number", "HS code", "VAT %", "Unit price", "Subtotal (excl. VAT)", "Total Due", "Fiscal details", "Fiscal device ID", "Fiscal day no", "Receipt global no", "Verification code", "QR code" })
                Assert.Contains(needed, xml);
            Assert.Equal(12, System.Text.RegularExpressions.Regex.Matches(xml, @"<w:t xml:space=""preserve"">\d+\.</w:t>").Count);   // twelve numbered line rows
        }

        [Fact]
        public void The_filled_example_is_read_back_correctly_including_the_buyers_own_tin_and_the_hs_codes()
        {
            var d = Fdms.Application.Import.InvoiceTextParser.Parse(WordText(Fdms.Infrastructure.Import.WordInvoiceTemplate.Build(sample: true)));
            Assert.Equal("INV-2041", d.InvoiceNo);
            Assert.Equal(new DateTime(2026, 10, 6), d.InvoiceDate);
            Assert.Equal("USD", d.Currency);
            Assert.Equal("Moyo Retailers (Pvt) Ltd", d.Buyer?.RegisterName);
            Assert.Equal("2001234567", d.Buyer?.Tin);            // the buyer's, not the seller's 1234567890
            Assert.Equal("220054321", d.Buyer?.VatNumber);
            Assert.Equal("accounts@moyoretailers.co.zw", d.Buyer?.Email);
            Assert.Equal(2, d.Lines.Count);
            Assert.Equal(("Bread 700g", "19059010", 10m, 1.15m, 15m), (d.Lines[0].Name, d.Lines[0].HsCode, d.Lines[0].Quantity, d.Lines[0].UnitPrice, d.Lines[0].TaxPercent!.Value));
            Assert.Equal(("Maize meal 10kg", "11022000", 5m, 7.80m, 15m), (d.Lines[1].Name, d.Lines[1].HsCode, d.Lines[1].Quantity, d.Lines[1].UnitPrice, d.Lines[1].TaxPercent!.Value));
            Assert.Equal(50.5m, d.Payments.Single().Amount);
            Assert.Equal(Fdms.Domain.Enums.MoneyType.Cash, d.Payments.Single().MoneyType);
            Assert.DoesNotContain(d.Warnings, w => w.Contains("differs"));
        }

        [Fact]
        public void The_pdf_saved_from_the_word_template_is_read_even_when_the_vat_rate_has_no_percent_sign_and_boxes_share_a_line()
        {
            // the text layer of tax-invoice-example.pdf exactly as it is extracted (two-column header, an HS code column and a VAT rate printed as 15.5)
            const string text = "Acme Trading (Pvt) Ltd TAX INVOICE\n12 Samora Machel Avenue, Harare\nInvoice No: INV-2041\nTel: +263 242 123456 Email: accounts@acme.co.zw\nDate: 06/10/2026\nTIN: 1234567890 VAT No: 220012345\n"
                + "Due date: 13/10/2026\nCurrency: USD\nBILL TO PAYMENT\nMoyo Retailers (Pvt) Ltd Method: Cash\n45 Jason Moyo Street, Bulawayo Prices include VAT.\nTIN: 2001234567 VAT No: 220054321\nEmail: accounts@moyoretailers.co.zw\n"
                + "# Description HS code Qty Unit price VAT % Amount\n1. Bread 700g 19059010 10 1.15 15.5 11.50\n2. Maize meal 10kg 11022000 5 7.80 15.5 39.00\nSubtotal (excl. VAT) 43.91\nVAT 6.59\nTotal Due 50.50\n"
                + "Notes: Goods remain the property of the seller until paid in full.\nFISCAL DETAILS\nFiscal device ID: ______\nReceipt global no: ______ QR code\nVerification code: ______ appears here\n";
            var d = Fdms.Application.Import.InvoiceTextParser.Parse(text);
            Assert.Equal("INV-2041", d.InvoiceNo);
            Assert.Equal("Moyo Retailers (Pvt) Ltd", d.Buyer?.RegisterName);   // not "PAYMENT"
            Assert.Equal("2001234567", d.Buyer?.Tin);
            Assert.Equal(2, d.Lines.Count);
            Assert.Equal(("Bread 700g", "19059010", 10m, 1.15m, 15.5m), (d.Lines[0].Name, d.Lines[0].HsCode, d.Lines[0].Quantity, d.Lines[0].UnitPrice, d.Lines[0].TaxPercent!.Value));
            Assert.Equal(("Maize meal 10kg", "11022000", 5m, 7.80m, 15.5m), (d.Lines[1].Name, d.Lines[1].HsCode, d.Lines[1].Quantity, d.Lines[1].UnitPrice, d.Lines[1].TaxPercent!.Value));
            Assert.DoesNotContain(d.Warnings, w => w.Contains("differs") || w.Contains("Tax rates were not read"));
        }

        [Fact]
        public void Rows_with_a_bare_vat_column_but_no_hs_code_and_ordinary_four_figure_rows_are_read_correctly()
        {
            var d = Fdms.Application.Import.InvoiceTextParser.Parse("Invoice No: Q-1\nCooking oil 2L 3 4.50 15.5 13.50\nWidget 4 2.50 1.00 10.00\nTotal Due: 23.50");
            Assert.Equal((3m, 4.5m, 15.5m), (d.Lines[0].Quantity, d.Lines[0].UnitPrice, d.Lines[0].TaxPercent!.Value));   // qty, price, VAT %, amount
            Assert.Null(d.Lines[0].HsCode);
            Assert.Equal(4m, d.Lines[1].Quantity); Assert.Equal(2.5m, d.Lines[1].UnitPrice);                              // 4 x 2.50 = 10.00: the 1.00 is a discount column, not a tax rate
        }

        [Fact]
        public void An_empty_template_is_not_mistaken_for_an_invoice()
        {
            var d = Fdms.Application.Import.InvoiceTextParser.Parse(WordText(Fdms.Infrastructure.Import.WordInvoiceTemplate.Build()));
            Assert.Empty(d.Lines);
            Assert.Contains(d.Warnings, w => w.Contains("No line items"));
        }

        [Fact]
        public void Hs_code_columns_and_descriptions_with_numbers_are_understood_in_pdf_text_too()
        {
            var d = Fdms.Application.Import.InvoiceTextParser.Parse("Invoice No: P-1\n1. Maize meal 10kg 11022000 5 7.80 15% 39.00\n2. Cooking oil 2L 15079000 3 4.50 15% 13.50\nWidget 1000 2.00 2000.00\nTotal Due: 62.50");
            Assert.Equal(("Maize meal 10kg", "11022000", 5m, 7.80m), (d.Lines[0].Name, d.Lines[0].HsCode, d.Lines[0].Quantity, d.Lines[0].UnitPrice));
            Assert.Equal("15079000", d.Lines[1].HsCode);
            Assert.Null(d.Lines[2].HsCode);   // three numbers: quantity, price, amount - not an HS code
            Assert.Equal(1000m, d.Lines[2].Quantity);
        }

        [Fact]
        public async Task A_filled_word_invoice_is_imported_previewed_and_submitted_and_the_template_downloads_publicly()
        {
            var (c, dev) = await SetupAsync("Word Co");
            var docx = Fdms.Infrastructure.Import.WordInvoiceTemplate.Build(sample: true);

            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/v1/devices/{dev}/import/word", File(Encoding.UTF8.GetBytes("not a docx"), "x.docx"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/v1/devices/{dev}/import/word", File(docx, "x.pdf"))).StatusCode);

            var preview = await (await c.PostAsync($"/api/v1/devices/{dev}/import/word", File(docx, "invoice.docx"))).Content.ReadFromJsonAsync<JsonElement>(J);
            var item = preview.GetProperty("items")[0];
            Assert.True(item.GetProperty("status").GetString() == "Preview", item.ToString());
            Assert.Equal("INV-2041", item.GetProperty("invoiceNo").GetString());
            Assert.Equal(50.5m, item.GetProperty("total").GetDecimal());
            Assert.Equal("19059010", item.GetProperty("request").GetProperty("lines")[0].GetProperty("hsCode").GetString());   // the HS codes came from the document
            Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());

            var sent = await (await c.PostAsync($"/api/v1/devices/{dev}/import/word?submit=true", File(docx, "invoice.docx"))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(1, sent.GetProperty("succeeded").GetInt32());
            Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());

            // the templates: signed-in download, and a free public one
            var tpl = await c.GetAsync("/api/v1/import/word-template");
            Assert.Equal(HttpStatusCode.OK, tpl.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", tpl.Content.Headers.ContentType!.MediaType);
            var anon = _f.CreateClient();
            var pub = await anon.GetAsync("/api/v1/public/templates/tax-invoice.docx");
            Assert.Equal(HttpStatusCode.OK, pub.StatusCode);
            Assert.Equal((byte)'P', (await pub.Content.ReadAsByteArrayAsync())[0]);
            Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/v1/public/templates/tax-invoice.docx?sample=true")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/import/word-template")).StatusCode);
        }

        [Fact]
        public async Task A_real_text_pdf_is_read_into_a_reviewable_draft_and_can_then_be_submitted()
        {
            QuestPDF.Settings.License = LicenseType.Community;
            var pdf = Document.Create(d => d.Page(p =>
            {
                p.Margin(40);
                p.Content().Column(col =>
                {
                    col.Item().Text("ACME Pvt Ltd");
                    col.Item().Text("Invoice No: PDF-77");
                    col.Item().Text("Date: 31/01/2025");
                    col.Item().Text("Bill To: Zeta Traders");
                    col.Item().Text("Buyer TIN: 2000123456");
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(4); c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); });
                        foreach (var h in new[] { "Description", "Qty", "Unit Price", "Total" }) t.Cell().Text(h);
                        foreach (var cell in new[] { "Men's shoes", "2", "25.00", "50.00", "Leather belt", "1", "12.50", "12.50" }) t.Cell().Text(cell);
                    });
                    col.Item().Text("Total Due: 62.50");
                });
            })).GeneratePdf();

            var (c, dev) = await SetupAsync("Pdf Co");

            // a PDF carries no HS codes, and this device is VAT-registered: the draft comes back with a clear reason, not a guess
            var first = await (await c.PostAsync($"/api/v1/devices/{dev}/import/pdf", File(pdf, "inv.pdf"))).Content.ReadFromJsonAsync<JsonElement>(J);
            var failed = first.GetProperty("items")[0];
            Assert.Equal("Failed", failed.GetProperty("status").GetString());
            Assert.Equal("RCPT047", failed.GetProperty("errorCode").GetString());
            Assert.Equal("PDF-77", failed.GetProperty("invoiceNo").GetString());
            Assert.Equal(2, failed.GetProperty("request").GetProperty("lines").GetArrayLength());   // the recognised draft is still returned

            // supplying an explicit HS code makes it previewable (and says so in the warnings)
            var draft = await (await c.PostAsync($"/api/v1/devices/{dev}/import/pdf?defaultHsCode=85456852", File(pdf, "inv.pdf"))).Content.ReadFromJsonAsync<JsonElement>(J);
            var item = draft.GetProperty("items")[0];
            Assert.True(item.GetProperty("status").GetString() == "Preview", item.ToString());
            Assert.Equal("PDF-77", item.GetProperty("invoiceNo").GetString());
            Assert.Equal(62.5m, item.GetProperty("total").GetDecimal());
            Assert.Contains(item.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()), w => w!.Contains("85456852"));
            var request = item.GetProperty("request");
            Assert.Equal(2, request.GetProperty("lines").GetArrayLength());
            Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());   // still nothing fiscalised

            // the draft is a ready-to-send receipt request: the reviewer edits it and posts it
            var posted = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", request);
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);

            // submitting the same document again is idempotent on the invoice number
            var again = await (await c.PostAsync($"/api/v1/devices/{dev}/import/pdf?submit=true&defaultHsCode=85456852", File(pdf, "inv.pdf"))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(posted.Headers.Location!.ToString().Split('/').Last(), again.GetProperty("items")[0].GetProperty("receiptId").GetGuid().ToString());
            Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());

            // a malformed HS code is reported against the invoice and nothing is fiscalised
            var badHs = await (await c.PostAsync($"/api/v1/devices/{dev}/import/pdf?submit=true&defaultHsCode=12", File(pdf, "inv.pdf"))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("HS_CODE_INVALID", badHs.GetProperty("items")[0].GetProperty("errorCode").GetString());
        }

        [Fact]
        public async Task Receipt_pdf_list_and_template_work()
        {
            var (c, dev) = await SetupAsync("Pdf Out Co");
            var rec = await (await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = "Z1", invoiceNo = "Z-1", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                buyer = new { registerName = "Zeta Traders", tin = "2000123456" },
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } },
            })).Content.ReadFromJsonAsync<JsonElement>(J);
            var id = rec.GetProperty("id").GetGuid();

            foreach (var fmt in new[] { "receipt", "a4" })
            {
                var res = await c.GetAsync($"/api/v1/receipts/{id}/pdf?format={fmt}");
                Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
                var bytes = await res.Content.ReadAsByteArrayAsync();
                Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
            }

            var list = await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts?search=Z-1&status=Submitted", J);
            Assert.Equal(1, list.GetProperty("total").GetInt32());
            Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/v1/receipts?status=Pending", J)).GetProperty("total").GetInt32());

            var tpl = await c.GetAsync("/api/v1/import/excel-template");
            Assert.Equal(HttpStatusCode.OK, tpl.StatusCode);
            Assert.Equal("PK", Encoding.ASCII.GetString((await tpl.Content.ReadAsByteArrayAsync()).AsSpan(0, 2)));
        }

        [Fact]
        public async Task Webhook_is_signed_delivered_and_retried_after_failure()
        {
            // a tiny receiver
            var listener = new System.Net.HttpListener();
            var port = new Random().Next(20000, 40000);
            listener.Prefixes.Add($"http://127.0.0.1:{port}/hook/");
            listener.Start();
            var received = new List<(string Event, string Sig, string Ts, string Body)>();
            var fail = true;
            var loop = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); } catch { break; }
                    using var sr = new StreamReader(ctx.Request.InputStream);
                    var body = await sr.ReadToEndAsync();
                    received.Add((ctx.Request.Headers["X-Fdms-Event"]!, ctx.Request.Headers["X-Fdms-Signature"]!, ctx.Request.Headers["X-Fdms-Timestamp"]!, body));
                    ctx.Response.StatusCode = fail ? 500 : 200;
                    ctx.Response.Close();
                }
            });

            try
            {
                var (c, dev, user) = await SetupFullAsync("Hook Co");
                var set = await user.PutAsJsonAsync("/api/v1/webhook", new { url = $"http://127.0.0.1:{port}/hook/" });
                Assert.Equal(HttpStatusCode.OK, set.StatusCode);
                var secret = (await set.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("secret").GetString()!;
                Assert.StartsWith("whsec_", secret);
                Assert.False((await user.GetFromJsonAsync<JsonElement>("/api/v1/webhook", J)).TryGetProperty("secret", out _));   // never shown again

                await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
                {
                    externalRef = "H1", invoiceNo = "H-1", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                    lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } },
                });

                var job = new WebhookDeliveryJob(_f.Services.GetRequiredService<IServiceScopeFactory>(), _f.Services.GetRequiredService<IConfiguration>(), NullLogger<WebhookDeliveryJob>.Instance);
                await job.RunOnceAsync(CancellationToken.None);   // receiver answers 500 -> retried later
                Assert.Single(received);
                var d1 = await user.GetFromJsonAsync<JsonElement>("/api/v1/webhook/deliveries", J);
                Assert.Equal("Pending", d1[0].GetProperty("status").GetString());
                Assert.Equal(1, d1[0].GetProperty("attempts").GetInt32());

                // make it due now and let the receiver accept
                fail = false;
                using (var scope = _f.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
                    foreach (var w in db.WebhookDeliveries) w.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
                    await db.SaveChangesAsync();
                }
                await job.RunOnceAsync(CancellationToken.None);
                Assert.Equal(2, received.Count);
                Assert.Equal("Delivered", (await user.GetFromJsonAsync<JsonElement>("/api/v1/webhook/deliveries", J))[0].GetProperty("status").GetString());

                var (ev, sig, ts, body) = received[1];
                Assert.Equal("receipt.fiscalised", ev);
                Assert.Equal("sha256=" + WebhookDeliveryJob.Sign(secret, ts, body), sig);          // receiver can verify authenticity
                Assert.Equal("H-1", JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("invoiceNo").GetString());
            }
            finally { listener.Stop(); await loop; }
        }

        [Fact]
        public async Task Webhook_urls_to_internal_hosts_are_refused_by_default()
        {
            // strict mode: a host without the AllowInsecureTargets switch
            using var strict = _f.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Webhooks:AllowInsecureTargets"] = "false" })));
            var co = await TestKit.NewCompanyAsync(strict, "Strict Co");
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/webhook", new { url = "https://127.0.0.1/hook" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/webhook", new { url = "http://example.com/hook" })).StatusCode);
        }
    }
}
