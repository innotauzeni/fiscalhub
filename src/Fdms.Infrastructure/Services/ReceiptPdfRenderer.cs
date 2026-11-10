using System.Globalization;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Fdms.Infrastructure.Services
{
    public enum ReceiptPdfFormat { Receipt48, InvoiceA4 }

    /// <summary>
    /// Printable fiscal receipt: 80 mm thermal roll or A4, with the ZIMRA verification QR. The company's template (logo, colour, header,
    /// columns, bank details, terms, footer) changes how it looks; the fiscal elements are always printed.
    /// </summary>
    public static class ReceiptPdfRenderer
    {
        static ReceiptPdfRenderer() => QuestPDF.Settings.License = LicenseType.Community;

        public static byte[] Render(Receipt r, Device d, ReceiptPdfFormat format, InvoiceTemplateSettings? template = null, byte[]? logo = null)
        {
            template ??= new InvoiceTemplateSettings();
            // a layout from the invoice builder (A4); if it cannot be drawn the invoice still prints, in the standard layout
            if (format == ReceiptPdfFormat.InvoiceA4 && template.Canvas is { Elements.Count: > 0 })
            {
                try { return Templates.CanvasPdfRenderer.Build(r, d, template, logo).GeneratePdf(); }
                catch (Exception) { /* never let a design stop a fiscal invoice from printing */ }
            }
            return Build(r, d, format, template, logo).GeneratePdf();
        }

        /// <summary>First page as a PNG, for the template editor's live preview.</summary>
        public static byte[] RenderPreviewPng(Receipt r, Device d, ReceiptPdfFormat format, InvoiceTemplateSettings template, byte[]? logo, int dpi = 110) =>
            (format == ReceiptPdfFormat.InvoiceA4 && template.Canvas is { Elements.Count: > 0 } ? Templates.CanvasPdfRenderer.Build(r, d, template, logo) : Build(r, d, format, template, logo))
                .GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = dpi }).First();

        // ---- [placeholders] in the company's own wording: filled from the company's details, never printed as brackets ----
        private static readonly System.Text.RegularExpressions.Regex Token = new(@"(?<label>[A-Za-z][A-Za-z .]{0,24}:\s*)?(?:\[\s*(?<k>[A-Za-z][A-Za-z ._/&-]{1,40}?)\s*\]|\{\{\s*(?<k2>[A-Za-z][A-Za-z ._/&-]{1,40}?)\s*\}\})", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string? Canonical(string key)
        {
            var k = new string(key.Where(char.IsLetter).ToArray()).ToLowerInvariant();
            return k switch
            {
                "companyname" or "name" or "tradingname" or "taxpayername" => "company",
                "companyaddress" or "address" or "physicaladdress" or "branchaddress" => "address",
                "telephone" or "tel" or "phone" or "phonenumber" or "contactnumber" or "telephonenumber" or "cell" or "mobile" => "phone",
                "email" or "emailaddress" => "email", "website" or "web" or "url" or "websiteaddress" => "website",
                "taxpayertin" or "tin" or "tinnumber" or "taxpayertinnumber" => "tin", "vatnumber" or "vat" or "vatno" or "vatregistration" or "vatregno" => "vat",
                "branch" or "branchname" => "branch", "deviceid" => "deviceid",
                "invoiceno" or "invoicenumber" or "invoice" or "reference" or "referenceno" or "ref" or "receiptno" => "invoiceno", "date" or "invoicedate" or "issuedate" => "date",
                "total" or "totalamount" or "amount" or "amountdue" => "total", "currency" => "currency",
                "paymentmethod" or "paymentmethods" or "paymentmode" or "paymenttype" or "modeofpayment" => "paymentmethod",
                "bankname" or "bank" or "bankingdetails" => "bank", "accountnumber" or "accountno" or "acc" or "accno" or "bankaccount" or "bankaccountnumber" => "accountnumber", "accountname" or "accountholder" => "accountname",
                "bankbranch" or "bankbranchname" => "bankbranch", "branchcode" or "sortcode" or "bankbranchcode" => "bankbranchcode",
                _ when k.Contains("cash") && (k.Contains("bank") || k.Contains("card") || k.Contains("ecocash")) => "paymentmethod",   // e.g. [BANK / ECOCASH / CASH / CARD]
                _ => null,
            };
        }

        /// <summary>
        /// Replaces placeholders such as [Company Address], [Telephone], [Email], [Website], [TAXPAYER TIN] and [VAT NUMBER] (or {{...}}) with the company's real details. A placeholder with nothing
        /// to put in it is removed together with its label ("Website: [Website]" disappears), so no brackets and no dangling labels are ever printed. Placeholders it does not know are left as typed.
        /// </summary>
        public static string? Fill(string? text, IReadOnlyDictionary<string, string?> values)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains('[') && !text.Contains("{{")) return text;
            var lines = text.Replace("\r\n", "\n").Split('\n').Select(line =>
            {
                var changed = false;
                var filled = Token.Replace(line, m =>
                {
                    var key = Canonical(m.Groups["k"].Success ? m.Groups["k"].Value : m.Groups["k2"].Value);
                    if (key == null) return m.Value;
                    changed = true;
                    var v = values.GetValueOrDefault(key);
                    return string.IsNullOrWhiteSpace(v) ? "" : m.Groups["label"].Value + v.Trim();
                });
                if (!changed) return line;
                filled = System.Text.RegularExpressions.Regex.Replace(filled, @"\s{2,}", " ");
                filled = System.Text.RegularExpressions.Regex.Replace(filled, @"([•|·;,]\s*){2,}", "$1");                      // separators left side by side
                filled = System.Text.RegularExpressions.Regex.Replace(filled.Trim(), @"^[•|·;,\s]+|[•|·;,\s]+$", "");          // and at the ends
                return filled;
            }).Where(l => l.Trim().Length > 0);
            return string.Join("\n", lines);
        }

        /// <summary>A copy of the settings with the placeholders filled in for this device.</summary>
        private static string PaymentName(string code) => code switch { "BankTransfer" => "Bank transfer", "MobileWallet" => "Mobile wallet (EcoCash)", "Cash" => "Cash", "Card" => "Card", "Coupon" => "Coupon", "Credit" => "Credit", _ => code };

        /// <summary>The account to print for this invoice: the default for its currency, else any account in that currency, else the default account, else the first.</summary>
        private static BankAccountInfo? PickBank(List<BankAccountInfo>? banks, string currency) =>
            banks == null || banks.Count == 0 ? null :
            banks.Where(b => string.Equals(b.Currency, currency, StringComparison.OrdinalIgnoreCase)).OrderByDescending(b => b.IsDefault).FirstOrDefault() ?? banks.FirstOrDefault(b => b.IsDefault) ?? banks[0];

        /// <summary>The invoice number a sample shows as an example ("Reference: INV-2026-000001") is replaced by the real one.</summary>
        private static readonly System.Text.RegularExpressions.Regex SampleReference = new(@"(?<=\b(?:Reference|Ref|Invoice No|Invoice Number)\.?:\s*)[A-Z]{2,6}-\d{4}-\d{3,}\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The company's own wording (bank details, terms, footer) with its [placeholders] filled for this invoice.</summary>
        public static string? FillFor(string? text, InvoiceTemplateSettings t, Device d, Receipt r, ReceiptPayload p)
        {
            var f = Fill(text, Values(t, d, r, p));
            return f == null ? null : SampleReference.Replace(f, r.InvoiceNo);
        }

        private static Dictionary<string, string?> Values(InvoiceTemplateSettings t, Device d, Receipt r, ReceiptPayload p)
        {
            var bank = PickBank(t.Banks, r.Currency);
            var methods = p.ReceiptPayments.Select(x => PaymentName(x.MoneyTypeCode)).Distinct().ToList();
            return new Dictionary<string, string?>
            {
                ["company"] = string.IsNullOrWhiteSpace(t.DisplayName) ? d.TaxPayerName : t.DisplayName, ["address"] = string.IsNullOrWhiteSpace(t.Address) ? d.BranchAddress : t.Address,
                ["phone"] = string.IsNullOrWhiteSpace(t.Phone) ? d.BranchPhone : t.Phone, ["email"] = string.IsNullOrWhiteSpace(t.Email) ? d.BranchEmail : t.Email, ["website"] = t.Website,
                ["tin"] = d.TaxPayerTin, ["vat"] = d.VatNumber, ["branch"] = d.BranchName, ["deviceid"] = d.ZimraDeviceId.ToString(),
                ["invoiceno"] = r.InvoiceNo, ["date"] = r.ReceiptDate.ToString("yyyy-MM-dd"), ["total"] = r.Total.ToString("N2", CultureInfo.InvariantCulture), ["currency"] = r.Currency,
                ["paymentmethod"] = methods.Count == 0 ? null : string.Join(" / ", methods),
                ["bank"] = bank?.Bank, ["accountnumber"] = bank?.AccountNumber, ["accountname"] = bank?.AccountName, ["bankbranch"] = bank?.BranchName, ["bankbranchcode"] = bank?.BranchCode,
            };
        }

        /// <summary>A copy of the settings with the placeholders filled in for this device and this invoice.</summary>
        private static InvoiceTemplateSettings Personalise(InvoiceTemplateSettings t, Device d, Receipt r, ReceiptPayload p)
        {
            var bank = PickBank(t.Banks, r.Currency);
            var methods = p.ReceiptPayments.Select(x => PaymentName(x.MoneyTypeCode)).Distinct().ToList();
            var values = new Dictionary<string, string?>
            {
                ["company"] = string.IsNullOrWhiteSpace(t.DisplayName) ? d.TaxPayerName : t.DisplayName, ["address"] = string.IsNullOrWhiteSpace(t.Address) ? d.BranchAddress : t.Address,
                ["phone"] = string.IsNullOrWhiteSpace(t.Phone) ? d.BranchPhone : t.Phone, ["email"] = string.IsNullOrWhiteSpace(t.Email) ? d.BranchEmail : t.Email, ["website"] = t.Website,
                ["tin"] = d.TaxPayerTin, ["vat"] = d.VatNumber, ["branch"] = d.BranchName, ["deviceid"] = d.ZimraDeviceId.ToString(),
                ["invoiceno"] = r.InvoiceNo, ["date"] = r.ReceiptDate.ToString("yyyy-MM-dd"), ["total"] = r.Total.ToString("N2", CultureInfo.InvariantCulture), ["currency"] = r.Currency,
                ["paymentmethod"] = methods.Count == 0 ? null : string.Join(" / ", methods),
                ["bank"] = bank?.Bank, ["accountnumber"] = bank?.AccountNumber, ["accountname"] = bank?.AccountName, ["bankbranch"] = bank?.BranchName, ["bankbranchcode"] = bank?.BranchCode,
            };
            string? Go(string? text) { var f = Fill(text, values); return f == null ? null : SampleReference.Replace(f, r.InvoiceNo); }
            var c = JsonSerializer.Deserialize<InvoiceTemplateSettings>(JsonSerializer.Serialize(t))!;
            c.DisplayName = Go(t.DisplayName); c.HeaderText = Go(t.HeaderText); c.InvoiceTitle = Go(t.InvoiceTitle);
            c.BankDetails = Go(t.BankDetails); c.Terms = Go(t.Terms); c.FooterText = Go(t.FooterText);
            return c;
        }

        private static IDocument Build(Receipt r, Device d, ReceiptPdfFormat format, InvoiceTemplateSettings template, byte[]? logo)
        {
            var p = JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options)!;
            var s = Personalise(template, d, r, p);
            var thermal = format == ReceiptPdfFormat.Receipt48;
            var accent = Regex(s.Accent) ? s.Accent : "#2563eb";
            var title = r.Type switch { ReceiptType.CreditNote => FiscalTitles.CreditNote, ReceiptType.DebitNote => FiscalTitles.DebitNote, _ => FiscalTitles.ForInvoice(s.InvoiceTitle) };
            var inv = CultureInfo.InvariantCulture;
            string M(decimal v) => v.ToString("N2", inv);
            var errors = string.IsNullOrWhiteSpace(r.ValidationErrorsJson) ? new List<string>() :
                (JsonSerializer.Deserialize<List<JsonElement>>(r.ValidationErrorsJson, FdmsJson.Options) ?? new())
                .Select(e => $"{e.GetProperty("code").GetString()} ({e.GetProperty("color").GetString()})").ToList();
            var showLogo = s.ShowLogo && logo is { Length: > 0 } && CanDecode(logo);   // an unreadable logo must never stop a receipt from printing
            var company = string.IsNullOrWhiteSpace(s.DisplayName) ? d.TaxPayerName ?? "" : s.DisplayName!.Trim();
            var headerLines = (s.HeaderText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            byte[]? qrPng = null;
            if (!string.IsNullOrEmpty(r.QrUrl))
            {
                using var gen = new QRCodeGenerator();
                using var data = gen.CreateQrCode(r.QrUrl, QRCodeGenerator.ECCLevel.M);
                qrPng = new PngByteQRCode(data).GetGraphic(6);
            }

            // ---- the company's layout choices (A4 only; the roll keeps its fixed compact form) ----
            var fs = thermal ? 8 : Math.Clamp(s.FontSize, 8, 13);
            var margin = thermal ? 8 : s.Margins switch { "narrow" => 24, "wide" => 50, _ => 36 };
            var band = !thermal && s.HeaderStyle == "band";
            string L(string? custom, string dflt) => string.IsNullOrWhiteSpace(custom) ? dflt : custom.Trim();
            var lblInvoice = L(s.LabelInvoiceNo, "Invoice No"); var lblDate = L(s.LabelDate, "Date"); var lblBuyer = L(s.LabelBuyer, "Buyer");
            var lblItem = L(s.LabelItem, "Item"); var lblHs = L(s.LabelHsCode, "HS code"); var lblQty = L(s.LabelQuantity, "Qty"); var lblPrice = L(s.LabelPrice, "Price");
            var lblTax = L(s.LabelTax, "VAT"); var lblTotal = L(s.LabelTotal, "Total");
            var columns = ResolveColumns(s, thermal);
            var tableStyle = thermal ? "lines" : s.TableStyle;
            var titleAlign = s.TitleAlign switch { "left" => PdfAlign.Left, "right" => PdfAlign.Right, _ => PdfAlign.Center };
            var qrPlace = thermal ? "center" : s.QrPosition;
            var qrAlign = qrPlace switch { "left" or "top-left" or "with-totals" => PdfAlign.Left, "right" or "top-right" => PdfAlign.Right, _ => PdfAlign.Center };
            var qrSize = thermal ? 90 : s.QrSize switch { "small" => 80, "large" => 150, _ => 110 };
            var qrAtFoot = qrPlace is "left" or "center" or "right";
            var (cellPad, lineHeight) = thermal ? (1f, 1.1f) : s.RowSpacing switch { "compact" => (2f, 1.15f), "relaxed" => (7f, 1.45f), _ => (4f, 1.3f) };
            var lblGrand = L(s.LabelGrandTotal, "TOTAL").ToUpperInvariant();
            var (logoW, logoH) = s.LogoSize switch { "small" => (100, 45), "large" => (210, 100), _ => (150, 70) };

            return Document.Create(doc => doc.Page(page =>
            {
                if (thermal) { page.ContinuousSize(226); page.Margin(margin); page.DefaultTextStyle(t => t.FontSize(fs)); }
                else { page.Size(PageSizes.A4); page.Margin(margin); page.DefaultTextStyle(t => t.FontSize(fs).LineHeight(lineHeight)); }

                page.Content().Column(col =>
                {
                    col.Spacing(thermal ? 2 : 4);

                    // ---- header: logo + company ----
                    void CompanyBlock(IContainer c, PdfAlign align) => c.Column(cc =>
                    {
                        cc.Item().Aligned(align).Text(company).Bold().FontSize(thermal ? 10 : fs + 5).FontColor(band ? Colors.White : thermal ? Colors.Black : accent);
                        void Line(string text) { var span = cc.Item().Aligned(align).Text(text); if (band) span.FontColor(Colors.White); }
                        foreach (var line in headerLines) Line(line);
                        if (!string.IsNullOrEmpty(d.BranchName)) Line(d.BranchName);
                        // the taxpayer's TIN and VAT are always printed, but not twice when the company's own header lines already carry them
                        if (string.IsNullOrEmpty(d.TaxPayerTin) || !headerLines.Any(l => l.Contains(d.TaxPayerTin, StringComparison.Ordinal)))
                            Line($"TIN: {d.TaxPayerTin}" + (string.IsNullOrEmpty(d.VatNumber) ? "" : $"   VAT: {d.VatNumber}"));
                    });
                    IContainer LogoBox(IContainer c) => band ? c.Background(Colors.White).Padding(4) : c;

                    if (thermal)
                    {
                        if (showLogo) col.Item().AlignCenter().MaxWidth(110).MaxHeight(50).Image(logo!).FitArea();
                        col.Item().Element(c => CompanyBlock(c, PdfAlign.Center));
                    }
                    else
                    {
                        var align = s.LogoAlign switch { "center" => PdfAlign.Center, "right" => PdfAlign.Right, _ => PdfAlign.Left };
                        IContainer Frame(IContainer c) => band ? c.Background(accent).Padding(10) : c;
                        col.Item().Element(Frame).Column(hc =>
                        {
                            if (showLogo && s.LogoAlign == "center") hc.Item().AlignCenter().Element(LogoBox).MaxWidth(logoW + 30).MaxHeight(logoH).Image(logo!).FitArea();
                            hc.Item().Row(row =>
                            {
                                if (showLogo && s.LogoAlign == "left") row.ConstantItem(logoW).Element(LogoBox).MaxHeight(logoH).Image(logo!).FitArea();
                                row.RelativeItem().PaddingLeft(showLogo && s.LogoAlign == "left" ? 12 : 0).PaddingRight(showLogo && s.LogoAlign == "right" ? 12 : 0)
                                   .Element(c => CompanyBlock(c, showLogo && s.LogoAlign == "left" ? PdfAlign.Left : align));
                                if (showLogo && s.LogoAlign == "right") row.ConstantItem(logoW).Element(LogoBox).MaxHeight(logoH).Image(logo!).FitArea();
                            });
                        });
                    }

                    // ---- title ----
                    if (thermal) col.Item().PaddingTop(4).AlignCenter().Text(title).Bold().FontSize(10).FontColor(accent);
                    else if (s.TitleStyle == "plain" || s.TitleStyle == "underline")
                    {
                        col.Item().PaddingTop(6).Aligned(titleAlign).Text(title).Bold().FontSize(fs + 5).FontColor(accent);
                        if (s.TitleStyle == "underline") col.Item().LineHorizontal(1.5f).LineColor(accent);
                    }
                    else col.Item().PaddingTop(6).Background(accent).Padding(6).Aligned(titleAlign).Text(title).Bold().FontSize(fs + 3).FontColor(Colors.White);
                    if (r.Status != ReceiptStatus.Submitted)
                        col.Item().AlignCenter().Text(r.Status == ReceiptStatus.Pending ? "NOT YET CONFIRMED BY ZIMRA" : "REJECTED BY ZIMRA").Bold().FontColor(Colors.Red.Medium);
                    col.Item().LineHorizontal(0.5f).LineColor(accent);

                    // ---- fiscal identifiers (always printed) and buyer ----
                    void Identifiers(ColumnDescriptor c)
                    {
                        c.Item().Text($"{lblInvoice}: {r.InvoiceNo}");
                        c.Item().Text($"{lblDate}: {r.ReceiptDate:yyyy-MM-dd HH:mm:ss}");
                        c.Item().Text($"Receipt #{r.ReceiptCounter}  Global #{r.ReceiptGlobalNo}  Fiscal day {r.FiscalDayNo}");
                        c.Item().Text($"Device ID: {d.ZimraDeviceId}");
                    }
                    void BuyerBlock(ColumnDescriptor c)
                    {
                        if (s.ShowBuyer && p.BuyerData != null)
                        {
                            c.Item().Text($"{lblBuyer}: {p.BuyerData.BuyerRegisterName}  (TIN {p.BuyerData.BuyerTin})");
                            if (!string.IsNullOrEmpty(p.BuyerData.VatNumber)) c.Item().Text($"{lblBuyer} VAT: {p.BuyerData.VatNumber}");
                        }
                        if (!string.IsNullOrWhiteSpace(p.ReceiptNotes)) c.Item().Text($"Note: {p.ReceiptNotes}");
                    }
                    void Parties(ColumnDescriptor c)
                    {
                        if (!thermal && s.PartiesLayout == "columns")
                            c.Item().Row(row => { row.RelativeItem().Column(Identifiers); row.ConstantItem(16); row.RelativeItem().Column(BuyerBlock); });
                        else { Identifiers(c); BuyerBlock(c); }
                    }
                    void QrBox(IContainer c) => c.Column(q =>
                    {
                        q.Item().Width(qrSize).Image(qrPng!);
                        q.Item().Width(qrSize).AlignCenter().Text("Scan to verify").FontSize(7);
                    });
                    if (!thermal && qrPng != null && qrPlace is "top-left" or "top-right")
                        col.Item().Row(row =>
                        {
                            if (qrPlace == "top-left") row.ConstantItem(qrSize + 14).Element(QrBox);
                            row.RelativeItem().Column(Parties);
                            if (qrPlace == "top-right") row.ConstantItem(qrSize + 14).AlignRight().Element(QrBox);
                        });
                    else Parties(col);
                    col.Item().LineHorizontal(0.5f).LineColor(accent);

                    // ---- lines ----
                    string Label(string key) => key switch { "item" => lblItem, "hs" => lblHs, "qty" => lblQty, "price" => lblPrice, "tax" => lblTax, _ => lblTotal };
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            foreach (var key in columns)
                                c.RelativeColumn(key switch { "item" => thermal ? 4 : 5.2f, "hs" => 1.9f, "qty" => 1.2f, "price" => 1.9f, "tax" => 1.4f, _ => 2.1f });   // roomy enough that a heading such as UNIT PRICE stays on one line
                        });
                        var padX = thermal ? 1f : 5f;
                        t.Header(h =>
                        {
                            IContainer Head(IContainer c) => (tableStyle switch
                            {
                                "solid" => c.Background(accent),
                                "grid" => c.Border(0.6f).BorderColor(accent),
                                "striped" => c.Background(Colors.Grey.Lighten3).BorderBottom(1f).BorderColor(accent),
                                _ => c.BorderBottom(1f).BorderColor(accent),
                            }).PaddingVertical(cellPad + 1).PaddingHorizontal(padX).AlignMiddle();
                            foreach (var key in columns)
                            {
                                var cell = h.Cell().Element(Head);
                                var text = key is "item" or "hs" ? cell.Text(Label(key)) : cell.AlignRight().Text(Label(key));
                                text.Bold().FontSize(thermal ? fs : fs - 1).FontColor(tableStyle == "solid" ? Colors.White : accent);
                            }
                        });
                        var row = 0;
                        foreach (var l in p.ReceiptLines)
                        {
                            var i = row++;
                            IContainer Body(IContainer c) => (tableStyle switch
                            {
                                "striped" => i % 2 == 0 ? c.Background(Colors.Grey.Lighten4) : c,
                                "grid" => c.Border(0.5f).BorderColor(Colors.Grey.Lighten1),
                                _ => c.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2),   // a fine rule between rows keeps long lists easy to follow
                            }).PaddingVertical(cellPad).PaddingHorizontal(padX).AlignMiddle();
                            var inlineTax = !columns.Contains("tax") && s.ShowLineTax;
                            var tax = !inlineTax ? "" : l.TaxPercent.HasValue ? $" [{l.TaxPercent:0.##}%]" : " [Exempt]";
                            foreach (var key in columns)
                            {
                                var cell = t.Cell().Element(Body);
                                switch (key)
                                {
                                    case "item": cell.Text(l.ReceiptLineName + tax + (l.ReceiptLineType == "Discount" ? " (discount)" : "")); break;
                                    case "hs": cell.Text(l.ReceiptLineHSCode ?? ""); break;
                                    case "qty": cell.AlignRight().Text(l.ReceiptLineQuantity.ToString("0.####", inv)); break;
                                    case "price": cell.AlignRight().Text(M(l.ReceiptLinePrice)); break;
                                    case "tax": cell.AlignRight().Text(l.TaxPercent.HasValue ? $"{l.TaxPercent:0.##}%" : "Exempt"); break;
                                    default: cell.AlignRight().Text(M(l.ReceiptLineTotal)); break;
                                }
                            }
                        }
                    });
                    col.Item().LineHorizontal(0.5f).LineColor(accent);

                    // ---- tax summary and total (always printed) ----
                    void Totals(ColumnDescriptor tc)
                    {
                        foreach (var t in p.ReceiptTaxes)
                            tc.Item().Row(row =>
                            {
                                row.RelativeItem().Text(t.TaxPercent.HasValue ? $"VAT {t.TaxPercent:0.##}% on {M(t.SalesAmountWithTax - t.TaxAmount)}" : $"Exempt {M(t.SalesAmountWithTax)}");
                                row.ConstantItem(thermal ? 60 : 90).AlignRight().Text(M(t.TaxAmount));
                            });
                        tc.Item().Row(row =>
                        {
                            row.RelativeItem().Text($"{lblGrand} ({r.Currency})").Bold().FontSize(thermal ? 10 : fs + 2);
                            row.ConstantItem(thermal ? 70 : 100).AlignRight().Text(M(r.Total)).Bold().FontSize(thermal ? 10 : fs + 2).FontColor(accent);
                        });
                        if (s.ShowPayments)
                            foreach (var pay in p.ReceiptPayments)
                                tc.Item().Row(row => { row.RelativeItem().Text(pay.MoneyTypeCode); row.ConstantItem(thermal ? 60 : 90).AlignRight().Text(M(pay.PaymentAmount)); });
                    }
                    if (!thermal && qrPng != null && qrPlace == "with-totals")
                        col.Item().Row(row => { row.ConstantItem(qrSize + 16).Element(QrBox); row.RelativeItem().Column(Totals); });
                    else if (!thermal && s.TotalsAlign == "right") col.Item().Row(row => { row.RelativeItem(3); row.RelativeItem(4).Column(Totals); });
                    else Totals(col);
                    col.Item().LineHorizontal(0.5f).LineColor(accent);

                    // ---- company's own wording ----
                    if (!string.IsNullOrWhiteSpace(s.BankDetails))
                    {
                        col.Item().PaddingTop(2).Text("Payment details").Bold().FontSize(thermal ? 7 : fs - 1).FontColor(accent);
                        col.Item().Text(s.BankDetails!.Trim()).FontSize(thermal ? 7 : fs - 1);
                    }
                    if (!string.IsNullOrWhiteSpace(s.Terms))
                    {
                        col.Item().PaddingTop(2).Text("Terms").Bold().FontSize(thermal ? 7 : fs - 1).FontColor(accent);
                        col.Item().Text(s.Terms!.Trim()).FontSize(thermal ? 7 : fs - 1);
                    }

                    // ---- verification (always printed) ----
                    if (qrPng != null)
                    {
                        // the QR itself sits where the template puts it; the verification address is always printed at the foot too
                        if (thermal || qrAtFoot) col.Item().PaddingTop(4).Aligned(qrAlign).Width(qrSize).Image(qrPng);
                        col.Item().PaddingTop(qrAtFoot || thermal ? 0 : 4).Aligned(qrAlign).Text("Verify this receipt:").FontSize(thermal ? 7 : 8);
                        col.Item().Aligned(qrAlign).Text(r.QrUrl!).FontSize(thermal ? 6 : 8);
                    }
                    if (r.FdmsReceiptId.HasValue) col.Item().Aligned(qrAlign).Text($"ZIMRA receipt ID: {r.FdmsReceiptId}").FontSize(thermal ? 7 : 8);
                    col.Item().Aligned(qrAlign).Text($"Device signature: {r.Hash}").FontSize(thermal ? 5 : 7);
                    if (errors.Count > 0)
                        col.Item().PaddingTop(4).Text("ZIMRA validation notices: " + string.Join(", ", errors)).FontColor(Colors.Orange.Darken2).FontSize(thermal ? 7 : fs - 1);
                    if (!string.IsNullOrWhiteSpace(s.FooterText))
                        col.Item().PaddingTop(6).AlignCenter().Text(s.FooterText!.Trim()).Italic().FontSize(thermal ? 7 : fs - 1).FontColor(Colors.Grey.Darken2);
                });
            }));
        }

        public static readonly string[] ColumnKeys = { "item", "hs", "qty", "price", "tax", "total" };

        /// <summary>The table columns in print order. A custom list is cleaned (known keys, no repeats, item and total always present); otherwise the standard columns from the Show flags.</summary>
        public static List<string> ResolveColumns(InvoiceTemplateSettings s, bool thermal)
        {
            List<string> cols;
            if (s.Columns is { Count: > 0 })
            {
                cols = s.Columns.Select(c => (c ?? "").Trim().ToLowerInvariant()).Where(ColumnKeys.Contains).Distinct().ToList();
                if (!cols.Contains("item")) cols.Insert(0, "item");
                cols.Remove("total"); cols.Add("total");   // the total is always the last column
            }
            else
            {
                cols = new List<string> { "item" };
                if (s.ShowHsCode) cols.Add("hs");
                if (s.ShowQuantity) cols.Add("qty");
                if (s.ShowUnitPrice) cols.Add("price");
                cols.Add("total");
            }
            if (thermal) { cols.Remove("hs"); cols.Remove("tax"); }   // the roll is too narrow for extra columns
            return cols;
        }

        /// <summary>True when the bytes are an image the PDF engine can draw (checked at upload so a bad file can never break receipts).</summary>
        public static bool CanDecode(byte[] image)
        {
            try { Document.Create(d => d.Page(p => { p.Size(PageSizes.A4); p.Content().MaxWidth(50).MaxHeight(50).Image(image).FitArea(); })).GeneratePdf(); return true; }   // scaled into a small box: only an undecodable file fails
            catch (Exception) { return false; }
        }

        private static bool Regex(string? hex) => hex != null && System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$");
    }

    internal enum PdfAlign { Left, Center, Right }

    internal static class TextAlignExtensions
    {
        public static IContainer Aligned(this IContainer c, PdfAlign a) => a switch { PdfAlign.Center => c.AlignCenter(), PdfAlign.Right => c.AlignRight(), _ => c.AlignLeft() };
    }
}
