using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>The invoice builder: a free A4 layout saved as the company's template, printed on real fiscal invoices, and exported as PDF, Word and JSON.</summary>
    public class InvoiceBuilderTests : IClassFixture<ExtrasFactory>
    {
        private readonly ExtrasFactory _f;
        private static int _seq = 61000;
        public InvoiceBuilderTests(ExtrasFactory f) => _f = f;

        private static byte[] Png()
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode("builder test picture", QRCoder.QRCodeGenerator.ECCLevel.L);
            return new QRCoder.PngByteQRCode(data).GetGraphic(4);
        }

        private static async Task<Guid> UploadAsync(HttpClient c)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Png()); file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "signature.png");
            var res = await c.PostAsync("/api/v1/templates/assets", form);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return (await res.Json()).GetProperty("id").GetGuid();
        }

        /// <summary>A design in the spirit of a cosmetics brand's invoice: gradient bands, a title band, rounded boxes, a pink table.</summary>
        private static object Canvas(Guid picture, bool withFiscal = true)
        {
            var els = new List<object>
            {
                new { type = "shape", x = 0, y = 0, w = 210, h = 48, style = new { fill = "#fdf2f8", fill2 = "#fce7f3", gradientDir = "horizontal" } },
                new { type = "logo", x = 12, y = 8, w = 50, h = 30 },
                new { type = "field", field = "company", x = 66, y = 10, w = 90, h = 12, style = new { fontSize = 22, bold = true, color = "#831843", font = "Georgia", uppercase = true, letterSpacing = 0.1 } },
                new { type = "text", text = "Cosmetics | ZIMRA fiscal | HS code\nBeauty Â· Confidence Â· You", x = 66, y = 23, w = 90, h = 12, style = new { fontSize = 8, color = "#9d174d" } },
                new { type = "image", assetId = picture, x = 168, y = 8, w = 30, h = 30 },
                new { type = "text", text = "{address}\nTIN: {tin}   VAT: {vat}\nTax: {taxRates} Standard VAT", x = 12, y = 50, w = 186, h = 14, style = new { fontSize = 9, fill = "#fdf2f8", radius = 3, padding = 2.5, color = "#831843" } },
                new { type = "field", field = "title", x = 55, y = 68, w = 100, h = 12, style = new { fontSize = 20, bold = true, color = "#ffffff", fill = "#9d174d", fill2 = "#db2777", radius = 6, align = "center", vAlign = "middle", font = "Georgia", letterSpacing = 0.2 } },
                new { type = "text", text = "BILL TO:\nName: {buyerName}\nAddress: {buyerAddress}\nTIN (if applicable): {buyerTin}", x = 12, y = 84, w = 105, h = 30, style = new { fontSize = 9, border = "#f9a8d4", borderWidth = 0.8, radius = 3, padding = 3 } },
                new { type = "text", text = "Invoice No:  {invoiceNo}\nInvoice Date:  {date}\nCurrency:  {currency}", x = 122, y = 84, w = 76, h = 30, style = new { fontSize = 9, fill = "#fdf2f8", radius = 3, padding = 3, lineHeight = 1.6 } },
                new { type = "items", x = 12, y = 118, w = 186, h = 100, columns = new[] { "no", "item", "hs", "qty", "price", "total" },
                      labels = new Dictionary<string, string> { ["item"] = "DESCRIPTION (Product / Service)", ["price"] = "UNIT PRICE ({currency})", ["total"] = "AMOUNT ({currency})" },
                      style = new { fontSize = 8.5, headerFill = "#9d174d", headerColor = "#ffffff", stripeFill = "#fdf2f8", lineColor = "#fbcfe8" } },
                new { type = "totals", x = 120, y = 222, w = 78, h = 26, labels = new Dictionary<string, string> { ["subtotal"] = "SUBTOTAL ({currency})", ["taxes"] = "VAT ({rate})" },
                      style = new { fontSize = 9.5, headerFill = "#9d174d", headerColor = "#ffffff", lineColor = "#fbcfe8" } },
                new { type = "text", text = "PAYMENT TERMS:\nâ€¢ Payment due as per agreement.\nâ€¢ All amounts are in {currency}.", x = 12, y = 222, w = 100, h = 24, style = new { fontSize = 8.5, fill = "#fdf2f8", radius = 3, padding = 3 } },
                new { type = "line", x = 12, y = 251, w = 186, h = 1, style = new { border = "#db2777", borderWidth = 1 } },
                new { type = "text", text = "Thank you for your business!", x = 12, y = 254, w = 90, h = 10, style = new { fontSize = 14, italic = true, color = "#9d174d", font = "Georgia" } },
                new { type = "shape", shape = "ellipse", x = 186, y = 258, w = 12, h = 12, style = new { fill = "#fbcfe8", opacity = 0.6 } },
            };
            if (withFiscal)
            {
                els.Add(new { type = "qr", x = 12, y = 266, w = 26, h = 28, label = "Scan to verify", style = new { fontSize = 7 } });
                els.Add(new { type = "fiscal", x = 42, y = 268, w = 140, h = 24, style = new { fontSize = 7, color = "#6b7280" } });
            }
            return new { background = "#ffffff", elements = els };
        }

        private static object Settings(object canvas) => new { accent = "#be185d", showLogo = true, logoAlign = "left", fontSize = 10, displayName = "Akery Pvt Ltd", address = "78 Manyame, Harare, Zimbabwe", canvas };

        [Fact]
        public async Task A_built_layout_must_keep_the_fiscal_parts_and_renders_as_png_pdf_word_and_json()
        {
            var co = await NewCompanyAsync(_f, "Builder Co");
            var fields = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/templates/fields", J);
            Assert.Contains(fields.GetProperty("fields").EnumerateArray(), x => x.GetProperty("key").GetString() == "buyerName");
            var picture = await UploadAsync(co.Admin);

            // without the QR and the fiscal details it is refused
            var bad = await co.Admin.PutAsJsonAsync("/api/v1/templates", new { id = (Guid?)null, branchId = (Guid?)null, name = "No fiscal", settings = Settings(Canvas(picture, withFiscal: false)) });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("CANVAS_FISCAL_REQUIRED", await bad.Content.ReadAsStringAsync());

            var saved = await co.Admin.PutAsJsonAsync("/api/v1/templates", new { id = (Guid?)null, branchId = (Guid?)null, name = "Akery pink", settings = Settings(Canvas(picture)) });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var tpl = await saved.Json();
            Assert.Equal(17, tpl.GetProperty("settings").GetProperty("canvas").GetProperty("elements").GetArrayLength());

            // the live preview (kept for a visual check), and the three files
            var png = await co.Admin.PostAsJsonAsync("/api/v1/templates/preview", new { settings = Settings(Canvas(picture)), format = "a4", templateId = tpl.GetProperty("id").GetGuid(), large = true });
            Assert.True(png.IsSuccessStatusCode, await png.Content.ReadAsStringAsync());
            var pngBytes = await png.Content.ReadAsByteArrayAsync();
            Assert.Equal(0x89, pngBytes[0]);
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "fdms-builder-preview.png"), pngBytes);

            async Task<HttpResponseMessage> Render(string format) => await co.Admin.PostAsJsonAsync("/api/v1/templates/render", new { settings = Settings(Canvas(picture)), format, templateId = tpl.GetProperty("id").GetGuid() });
            var pdf = await Render("pdf");
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
            var docx = await Render("docx");
            var docxBytes = await docx.Content.ReadAsByteArrayAsync();
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "fdms-builder-sample.docx"), docxBytes);
            using (var zip = new ZipArchive(new MemoryStream(docxBytes)))
            {
                using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
                var xml = reader.ReadToEnd();
                Assert.Contains("wps:wsp", xml);
                Assert.Contains("INV-0001", xml);
                Assert.Contains(zip.Entries, e => e.FullName.StartsWith("word/media/"));
            }
            var json = await (await Render("json")).Json();
            Assert.Equal("INV-0001", json.GetProperty("fields").GetProperty("invoiceNo").GetString());
            Assert.Equal(17, json.GetProperty("template").GetProperty("elements").GetArrayLength());

            // ZIMRA's wording: the heading is FISCAL TAX INVOICE, never just "INVOICE"
            async Task<HttpResponseMessage> SaveWith(Func<List<JsonElement>, List<object>> change)
            {
                var els = JsonSerializer.Deserialize<List<JsonElement>>(JsonSerializer.Serialize(((dynamic)Canvas(picture)).elements), J)!;
                return await co.Admin.PutAsJsonAsync("/api/v1/templates", new { id = tpl.GetProperty("id").GetGuid(), branchId = (Guid?)null, name = "Akery pink", settings = Settings(new { background = "#ffffff", elements = change(els) }) });
            }
            var noTitle = await SaveWith(els => els.Where(e => !(e.TryGetProperty("field", out var f) && f.GetString() == "title")).Cast<object>().ToList());
            Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);
            Assert.Contains("FISCAL TAX INVOICE", await noTitle.Content.ReadAsStringAsync());
            var bare = await SaveWith(els => els.Cast<object>().Append(new { type = "text", text = "INVOICE", x = 60, y = 40, w = 90, h = 12 }).ToList());
            Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
            Assert.Contains("TITLE_NOT_FISCAL", await bare.Content.ReadAsStringAsync());
            var plainTitle = await co.Admin.PutAsJsonAsync("/api/v1/templates", new { id = tpl.GetProperty("id").GetGuid(), branchId = (Guid?)null, name = "Akery pink", settings = new { accent = "#be185d", showLogo = true, logoAlign = "left", fontSize = 10, invoiceTitle = "Tax Invoice" } });
            Assert.Equal(HttpStatusCode.BadRequest, plainTitle.StatusCode);
            Assert.Contains("TITLE_NOT_FISCAL", await plainTitle.Content.ReadAsStringAsync());

            // a picture on a template cannot be deleted
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/templates/assets/{picture}")).StatusCode);
        }

        [Fact]
        public async Task Real_invoices_print_with_the_built_design_and_export_as_word_and_json_and_templates_move_between_companies()
        {
            var co = await NewCompanyAsync(_f, "Akery Live");
            var picture = await UploadAsync(co.Admin);
            var saved = await co.Admin.PutAsJsonAsync("/api/v1/templates", new { id = (Guid?)null, branchId = (Guid?)null, name = "Akery pink", settings = Settings(Canvas(picture)) });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var tplId = (await saved.Json()).GetProperty("id").GetGuid();

            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-BLD");
            var r = await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = "AK-1", invoiceNo = "INV000001", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                buyer = new { registerName = "Glow Salon (Pvt) Ltd", tin = "2000123456", vatNumber = "220099887" },
                lines = new[]
                {
                    new { name = "Body Lotion - Moisturizing (500ml)", hsCode = "33049900", quantity = 20, unitPrice = 8.5m, taxPercent = 15 },
                    new { name = "Face Cream - Hydrating (50g)", hsCode = "33049900", quantity = 15, unitPrice = 9m, taxPercent = 15 },
                },
                payments = new[] { new { moneyType = "Cash", amount = 305m } },
            });
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
            var id = (await r.Json()).GetProperty("id").GetGuid();

            var pdf = await co.Admin.GetByteArrayAsync($"/api/v1/receipts/{id}/pdf?format=a4");
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "fdms-builder-invoice.pdf"), pdf);
            using (var p = UglyToad.PdfPig.PdfDocument.Open(pdf))
            {
                // the extractor may drop the spaces between words: compare without them
                var text = string.Concat(string.Join("", p.GetPages().Select(pg => pg.Text)).Where(ch => !char.IsWhiteSpace(ch)));
                foreach (var expected in new[] { "INV000001", "GlowSalon", "DESCRIPTION", "Scantoverify", "Fiscalday", "AKERYPVTLTD", "FISCALTAXINVOICE" }) Assert.Contains(expected, text);
            }

            var docx = await co.Admin.GetAsync($"/api/v1/receipts/{id}/docx");
            Assert.True(docx.IsSuccessStatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", docx.Content.Headers.ContentType!.MediaType);
            var doc = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{id}/document", J);
            Assert.Equal("INV000001", doc.GetProperty("fields").GetProperty("invoiceNo").GetString());
            Assert.Equal("Glow Salon (Pvt) Ltd", doc.GetProperty("fields").GetProperty("buyerName").GetString());
            Assert.Equal(2, doc.GetProperty("lines").GetArrayLength());
            Assert.Equal(305m, doc.GetProperty("total").GetDecimal());
            Assert.True(doc.GetProperty("fiscal").GetProperty("deviceId").GetInt32() > 0);

            // export, then load it into another company's template: its pictures come along under new ids
            var export = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/templates/{tplId}/export", J);
            Assert.Equal("fiscalhub-invoice-template", export.GetProperty("format").GetString());
            Assert.Single(export.GetProperty("assets").EnumerateArray());
            var other = await NewCompanyAsync(_f, "Other Salon");
            var otherTpl = await (await other.Admin.PutAsJsonAsync("/api/v1/templates", new { id = (Guid?)null, branchId = (Guid?)null, name = "Plain", settings = new { accent = "#2563eb", showLogo = true, logoAlign = "left", fontSize = 10 } })).Json();
            var imported = await other.Admin.PostAsJsonAsync($"/api/v1/templates/{otherTpl.GetProperty("id").GetGuid()}/import", export);
            Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());
            var img = (await imported.Json()).GetProperty("settings").GetProperty("canvas").GetProperty("elements").EnumerateArray().First(e => e.GetProperty("type").GetString() == "image");
            var newPicture = img.GetProperty("assetId").GetGuid();
            Assert.NotEqual(picture, newPicture);
            Assert.True((await other.Admin.GetAsync($"/api/v1/templates/assets/{newPicture}")).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/templates/assets/{picture}")).StatusCode);   // the first company's picture stays its own
        }
    }
}
