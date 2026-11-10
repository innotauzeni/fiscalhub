using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Fdms.Infrastructure.Import
{
    /// <summary>
    /// A ready-to-use Word (.docx) invoice for businesses that have no structured invoice. It carries the fields a ZIMRA fiscal tax invoice needs: the seller's
    /// name, TIN and VAT number, the buyer's name, TIN and VAT number, invoice number and date, a line table with HS code, quantity, unit price and VAT rate, the
    /// totals, and a box for the fiscal details (device, fiscal day, receipt number, verification code and QR code) that the gateway adds when the invoice is
    /// fiscalised. Header fields are click-to-fill content controls. Built by hand with System.IO.Compression, so no Office library is needed.
    /// The same file can be uploaded straight back to the importer (see <see cref="DocxTextExtractor"/>).
    /// </summary>
    public static class WordInvoiceTemplate
    {
        private const string Brand = "2563EB", Ink = "0F172A", Grey = "64748B", Soft = "F1F5F9", Line = "CBD5E1";
        private static int _sdtId = 41000;

        // ---------------------------------------------------------------- xml helpers
        private static string E(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

        private static string R(string text, bool bold = false, int size = 19, string? color = null, bool italic = false, bool caps = false) =>
            $"<w:r><w:rPr>{(bold ? "<w:b/>" : "")}{(italic ? "<w:i/>" : "")}{(caps ? "<w:caps/>" : "")}{(color != null ? $"<w:color w:val=\"{color}\"/>" : "")}<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">{E(text)}</w:t></w:r>";

        private static string P(string runs, string? align = null, int before = 0, int after = 0, string? border = null) =>
            $"<w:p><w:pPr>{(border != null ? $"<w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"1\" w:color=\"{border}\"/></w:pBdr>" : "")}<w:spacing w:before=\"{before}\" w:after=\"{after}\"/>{(align != null ? $"<w:jc w:val=\"{align}\"/>" : "")}</w:pPr>{runs}</w:p>";

        /// <summary>A click-to-fill field: shows grey help text until the user types. With a value (the filled example) it is plain filled-in text.</summary>
        private static string Field(string alias, string placeholder, string? value, int size = 19, bool bold = false)
        {
            var id = Interlocked.Increment(ref _sdtId);
            if (value != null)
                return $"<w:sdt><w:sdtPr><w:alias w:val=\"{E(alias)}\"/><w:tag w:val=\"{E(alias.Replace(' ', '_'))}\"/><w:id w:val=\"{id}\"/><w:text/></w:sdtPr><w:sdtContent>{R(value, bold, size, Ink)}</w:sdtContent></w:sdt>";
            return $"<w:sdt><w:sdtPr><w:alias w:val=\"{E(alias)}\"/><w:tag w:val=\"{E(alias.Replace(' ', '_'))}\"/><w:id w:val=\"{id}\"/><w:placeholder><w:docPart w:val=\"DefaultPlaceholder_-1854013440\"/></w:placeholder><w:showingPlcHdr/><w:text/></w:sdtPr><w:sdtContent><w:r><w:rPr><w:rStyle w:val=\"PlaceholderText\"/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr><w:t xml:space=\"preserve\">{E(placeholder)}</w:t></w:r></w:sdtContent></w:sdt>";
        }

        private static string Cell(int width, string content, string? fill = null, string? vAlign = null, int margin = 100, string? borders = null) =>
            $"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>{borders ?? ""}{(fill != null ? $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/>" : "")}<w:tcMar><w:top w:w=\"{margin}\" w:type=\"dxa\"/><w:left w:w=\"{margin + 20}\" w:type=\"dxa\"/><w:bottom w:w=\"{margin}\" w:type=\"dxa\"/><w:right w:w=\"{margin + 20}\" w:type=\"dxa\"/></w:tcMar>{(vAlign != null ? $"<w:vAlign w:val=\"{vAlign}\"/>" : "")}</w:tcPr>{content}</w:tc>";

        private static string Row(string cells, int height = 0, bool header = false) =>
            $"<w:tr><w:trPr><w:cantSplit/>{(height > 0 ? $"<w:trHeight w:val=\"{height}\" w:hRule=\"atLeast\"/>" : "")}{(header ? "<w:tblHeader/>" : "")}</w:trPr>{cells}</w:tr>";

        private static string Table(int[] widths, string rows, string? borderColor = null, string? align = null)
        {
            var b = borderColor == null ? "<w:tblBorders><w:top w:val=\"nil\"/><w:left w:val=\"nil\"/><w:bottom w:val=\"nil\"/><w:right w:val=\"nil\"/><w:insideH w:val=\"nil\"/><w:insideV w:val=\"nil\"/></w:tblBorders>"
                : $"<w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{borderColor}\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{borderColor}\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{borderColor}\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{borderColor}\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{borderColor}\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{borderColor}\"/></w:tblBorders>";
            return $"<w:tbl><w:tblPr><w:tblW w:w=\"{widths.Sum()}\" w:type=\"dxa\"/>{(align != null ? $"<w:jc w:val=\"{align}\"/>" : "")}{b}<w:tblLayout w:type=\"fixed\"/></w:tblPr><w:tblGrid>{string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>"))}</w:tblGrid>{rows}</w:tbl>";
        }

        private static string Label(string text) => R(text, true, 15, Grey, caps: true);

        // ---------------------------------------------------------------- sample data (the filled example)
        public sealed record SampleLine(string Description, string Hs, decimal Qty, decimal Price, decimal Vat);

        private static readonly SampleLine[] SampleLines =
        {
            new("Bread 700g", "19059010", 10, 1.15m, 15),
            new("Maize meal 10kg", "11022000", 5, 7.80m, 15),
        };

        // ---------------------------------------------------------------- the document
        public static byte[] Build(bool sample = false)
        {
            string? V(string v) => sample ? v : null;
            var total = SampleLines.Sum(l => l.Qty * l.Price);
            var vat = Math.Round(total - total / 1.15m, 2);
            var body = new StringBuilder();

            // ---- header: who is selling, and what this document is
            var left = P(Field("Company name", "Your company name", V("Acme Trading (Pvt) Ltd"), 36, true), after: 40)
                     + P(Field("Address", "Street, suburb, city", V("12 Samora Machel Avenue, Harare")), after: 20)
                     + P(R("Tel: ", size: 17, color: Grey) + Field("Phone", "+263 77 000 0000", V("+263 242 123456"), 17) + R("   Email: ", size: 17, color: Grey) + Field("Email", "accounts@yourcompany.com", V("accounts@acme.co.zw"), 17), after: 20)
                     + P(R("TIN: ", true, 17, Grey) + Field("Seller TIN", "Your 10-digit TIN", V("1234567890"), 17, true) + R("   VAT No: ", true, 17, Grey) + Field("Seller VAT number", "Your VAT number", V("220012345"), 17, true));
            var right = P(R("TAX INVOICE", true, 40, Brand), "right", after: 80)
                      + P(R("Invoice No: ", true, 18, Grey) + Field("Invoice number", "INV-0001", V("INV-2041"), 20, true), "right", after: 20)
                      + P(R("Date: ", true, 18, Grey) + Field("Invoice date", "DD/MM/YYYY", V("06/10/2026"), 19), "right", after: 20)
                      + P(R("Due date: ", true, 18, Grey) + Field("Due date", "DD/MM/YYYY", V("13/10/2026"), 19), "right", after: 20)
                      + P(R("Currency: ", true, 18, Grey) + Field("Currency", "USD", V("USD"), 19), "right");
            body.Append(Table(new[] { 6000, 4206 }, Row(Cell(6000, left) + Cell(4206, right, vAlign: "top"))));
            body.Append(P("", border: Brand, after: 120));

            // ---- buyer and payment
            var bill = P(Label("Bill to"), after: 40)
                     + P(Field("Buyer name", "Customer / company name", V("Moyo Retailers (Pvt) Ltd"), 21, true), after: 20)
                     + P(Field("Buyer address", "Customer address", V("45 Jason Moyo Street, Bulawayo")), after: 20)
                     + P(R("TIN: ", true, 17, Grey) + Field("Buyer TIN", "Customer TIN (needed for business customers)", V("2001234567"), 17) + R("   VAT No: ", true, 17, Grey) + Field("Buyer VAT number", "Customer VAT no.", V("220054321"), 17), after: 20)
                     + P(R("Email: ", true, 17, Grey) + Field("Buyer email", "customer@email.com", V("accounts@moyoretailers.co.zw"), 17));
            body.Append(Table(new[] { 10206 }, Row(Cell(10206, bill, Soft, margin: 140))));
            body.Append(P(R("Payment method: ", true, 18, Grey) + Field("Payment method", "Cash", V("Cash"), 19) + R("      Prices include VAT.", size: 17, color: Grey, italic: true), before: 100, after: 120));

            // ---- line items
            var w = new[] { 560, 3700, 1250, 800, 1300, 900, 1696 };
            string H(int i, string t, string align) => Cell(w[i], P(R(t, true, 17, "FFFFFF"), align), Brand, "center", 90);
            var rows = new StringBuilder();
            rows.Append(Row(H(0, "#", "left") + H(1, "Description", "left") + H(2, "HS code", "left") + H(3, "Qty", "right") + H(4, "Unit price", "right") + H(5, "VAT %", "right") + H(6, "Amount", "right"), 420, true));
            const int slots = 12;
            for (var i = 0; i < slots; i++)
            {
                var l = sample && i < SampleLines.Length ? SampleLines[i] : null;
                string D(int c, string t, string align = "left") => Cell(w[c], P(t.Length == 0 ? "" : R(t, size: 18, color: Ink), align), i % 2 == 1 ? "F8FAFC" : null, "center", 70);
                rows.Append(Row(
                    D(0, l != null || !sample ? $"{i + 1}." : "") + D(1, l?.Description ?? "") + D(2, l?.Hs ?? "") + D(3, l?.Qty.ToString("0.##") ?? "", "right") + D(4, l?.Price.ToString("0.00") ?? "", "right")
                    + D(5, l != null ? $"{l.Vat:0.##}%" : "", "right") + D(6, l != null ? (l.Qty * l.Price).ToString("0.00") : "", "right"), 330));
            }
            body.Append(Table(w, rows.ToString(), Line));
            body.Append(P("", after: 100));

            // ---- totals (right aligned)
            string T(string label, string value, bool strong) =>
                Row(Cell(3000, P(R(label, strong, strong ? 22 : 18, strong ? Brand : Ink), "right"), strong ? Soft : null, "center", 80) + Cell(2200, P(R(value, strong, strong ? 22 : 18, strong ? Brand : Ink), "right"), strong ? Soft : null, "center", 80), strong ? 420 : 0);
            body.Append(Table(new[] { 3000, 2200 },
                T("Subtotal (excl. VAT)", sample ? (total - vat).ToString("0.00") : "", false) + T("VAT", sample ? vat.ToString("0.00") : "", false) + T("Total Due", sample ? total.ToString("0.00") : "", true), align: "right"));
            body.Append(P("", after: 100));

            // ---- notes
            body.Append(P(R("Notes: ", true, 17, Grey) + Field("Notes", "Optional: delivery note, terms, bank details for payment", V("Goods remain the property of the seller until paid in full."), 17), after: 140));

            // ---- fiscal details: filled in by the gateway when the invoice is fiscalised
            string F(string label) => P(R(label, true, 16, Grey) + R(" ______________________________", size: 16, color: Line), after: 50);
            var fiscal = P(Label("Fiscal details"), after: 30) + P(R("Added automatically when this invoice is fiscalised through FDMS.", size: 15, color: Grey, italic: true), after: 80)
                       + F("Fiscal device ID:") + F("Fiscal day no:") + F("Receipt global no:") + F("Verification code:");
            var qrBorder = $"<w:tcBorders><w:top w:val=\"dashed\" w:sz=\"6\" w:space=\"0\" w:color=\"{Line}\"/><w:left w:val=\"dashed\" w:sz=\"6\" w:space=\"0\" w:color=\"{Line}\"/><w:bottom w:val=\"dashed\" w:sz=\"6\" w:space=\"0\" w:color=\"{Line}\"/><w:right w:val=\"dashed\" w:sz=\"6\" w:space=\"0\" w:color=\"{Line}\"/></w:tcBorders>";
            var qr = P(R("QR code", true, 16, Grey), "center", before: 560) + P(R("appears here", size: 15, color: Grey, italic: true), "center");
            body.Append(Table(new[] { 7000, 3206 }, Row(Cell(7000, fiscal, Soft, margin: 140) + Cell(3206, qr, null, "center", 100, qrBorder), 1900)));

            var section = "<w:sectPr><w:footerReference w:type=\"default\" r:id=\"rIdFooter\"/><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"850\" w:right=\"850\" w:bottom=\"850\" w:left=\"850\" w:header=\"400\" w:footer=\"400\" w:gutter=\"0\"/></w:sectPr>";
            var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>{body}{section}</w:body></w:document>";
            var footer = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">{P(R("Tax invoice · includes the details ZIMRA fiscal invoices carry · prepared with FDMS Gateway", size: 14, color: Grey), "center")}</w:ftr>";
            const string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:color w:val=\"0F172A\"/><w:sz w:val=\"19\"/><w:szCs w:val=\"19\"/><w:lang w:val=\"en-ZW\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"252\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
                + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
                + "<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/></w:style>"
                + "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>"
                + "<w:style w:type=\"character\" w:styleId=\"PlaceholderText\"><w:name w:val=\"Placeholder Text\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:rPr><w:color w:val=\"94A3B8\"/></w:rPr></w:style></w:styles>";
            const string settings = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>";
            const string contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
                + "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/><Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>"
                + "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>";
            const string rootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>";
            const string docRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"rIdSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/><Relationship Id=\"rIdFooter\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/></Relationships>";
            var core = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:title>{(sample ? "Tax invoice - filled example" : "Tax invoice template")}</dc:title><dc:creator>FDMS Gateway</dc:creator><dcterms:created xsi:type=\"dcterms:W3CDTF\">2026-01-01T00:00:00Z</dcterms:created></cp:coreProperties>";

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                void Add(string name, string xml) { var e = zip.CreateEntry(name, CompressionLevel.Optimal); using var s = e.Open(); var b = new UTF8Encoding(false).GetBytes(xml); s.Write(b, 0, b.Length); }
                Add("[Content_Types].xml", contentTypes); Add("_rels/.rels", rootRels); Add("word/document.xml", document); Add("word/_rels/document.xml.rels", docRels);
                Add("word/styles.xml", styles); Add("word/settings.xml", settings); Add("word/footer1.xml", footer); Add("docProps/core.xml", core);
            }
            return ms.ToArray();
        }
    }

    /// <summary>Turns a filled-in Word invoice back into plain text lines that the invoice reader understands (the same reader that handles PDFs).</summary>
    public static class DocxTextExtractor
    {
        private const long MaxXmlBytes = 6_000_000;
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        public static string Extract(Stream file)
        {
            using var ms = new MemoryStream(); file.CopyTo(ms); ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a Word document.");
            if (entry.Length > MaxXmlBytes) throw new InvalidDataException("The document is too large.");   // refuse a zip bomb before reading
            using var xs = entry.Open();
            using var reader = XmlReader.Create(xs, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
            var doc = XDocument.Load(reader);
            var body = doc.Root?.Element(W + "body") ?? throw new InvalidDataException("The document has no body.");
            var sb = new StringBuilder();
            Blocks(body, sb);
            return sb.ToString();
        }

        private static void Blocks(XElement parent, StringBuilder sb)
        {
            foreach (var el in parent.Elements())
            {
                if (el.Name == W + "p") { var t = Text(el); if (t.Length > 0) sb.AppendLine(t); }
                else if (el.Name == W + "tbl") Table(el, sb);
                else if (el.Name == W + "sdt") { var c = el.Element(W + "sdtContent"); if (c != null) Blocks(c, sb); }
            }
        }

        /// <summary>A row of one-line cells (the line items, the totals) becomes one line; a block with several lines per cell (the header, the buyer box) keeps each paragraph on its own line.</summary>
        private static void Table(XElement tbl, StringBuilder sb)
        {
            foreach (var tr in tbl.Elements(W + "tr"))
            {
                var cells = tr.Elements(W + "tc").Select(tc => tc.Elements(W + "p").Select(Text).Where(t => t.Length > 0).ToList()).ToList();
                if (cells.All(c => c.Count <= 1)) { var line = string.Join("  ", cells.Select(c => c.FirstOrDefault() ?? "").Where(t => t.Length > 0)); if (line.Length > 0) sb.AppendLine(line); }
                else foreach (var c in cells) foreach (var t in c) sb.AppendLine(t);
            }
        }

        private static string Text(XElement p)
        {
            var sb = new StringBuilder();
            foreach (var n in p.Descendants())
            {
                if (n.Name == W + "t") sb.Append(n.Value);
                else if (n.Name == W + "tab" || n.Name == W + "br") sb.Append(' ');
            }
            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }
    }
}
