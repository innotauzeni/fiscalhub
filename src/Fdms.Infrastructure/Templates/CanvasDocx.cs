using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Fdms.Application.Abstractions;

namespace Fdms.Infrastructure.Templates
{
    /// <summary>
    /// The invoice as a Word document laid out like the PDF: every element of the builder becomes a shape pinned to the same place on the page
    /// (text boxes, tables inside boxes, pictures, rectangles, circles and lines). Without a built layout, a clean standard layout is used.
    /// Hand-written Office Open XML (no Office library on the server).
    /// </summary>
    public static class CanvasDocx
    {
        private const long EmuPerMm = 36000;
        private const double TwipPerMm = 1440 / 25.4;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static string X(string? s) => SecurityElement.Escape(s ?? "") ?? "";
        private static long E(double mm) => (long)Math.Round(mm * EmuPerMm);
        private static string Rgb(string? hex, string dflt) => (CanvasPdfRenderer.IsHex(hex) ? hex! : dflt).TrimStart('#').ToUpperInvariant();

        private sealed class Media { public readonly List<(string Name, byte[] Bytes, string Ext)> Files = new(); }

        public static byte[] Build(InvoiceDocumentDto doc, CanvasLayout? layout, byte[]? logo, IReadOnlyDictionary<Guid, byte[]>? assets, byte[]? qrPng)
        {
            layout ??= StandardLayout();
            var media = new Media();
            var body = new StringBuilder();
            var id = 1;
            if (!string.Equals(layout.Background, "#ffffff", StringComparison.OrdinalIgnoreCase) && CanvasPdfRenderer.IsHex(layout.Background))
                body.Append(Shape(id++, new CanvasElement { Type = "shape", X = 0, Y = 0, W = CanvasPdfRenderer.PageW, H = CanvasPdfRenderer.PageH, Style = new CanvasStyle { Fill = layout.Background } }, behind: true));
            foreach (var el in layout.Elements)
            {
                var st = el.Style ?? new CanvasStyle();
                switch (el.Type)
                {
                    case "shape": body.Append(Shape(id++, el)); break;
                    case "line": body.Append(Line(id++, el)); break;
                    case "logo": if (logo is { Length: > 0 }) body.Append(Picture(id++, el, logo, media)); break;
                    case "image": if (el.AssetId is { } a && assets?.GetValueOrDefault(a) is { Length: > 0 } img) body.Append(Picture(id++, el, img, media)); break;
                    case "qr":
                        if (qrPng != null)
                        {
                            var caption = string.IsNullOrWhiteSpace(el.Label) ? 0 : Math.Min(6, el.H / 4);
                            body.Append(Picture(id++, new CanvasElement { X = el.X, Y = el.Y, W = el.W, H = el.H - caption, Style = st }, qrPng, media));
                            if (caption > 0) body.Append(TextBox(id++, new CanvasElement { X = el.X, Y = el.Y + el.H - caption, W = el.W, H = caption, Style = new CanvasStyle { FontSize = Math.Max(6, st.FontSize * 0.75), Align = "center", Color = st.Color, Padding = 0 } }, Para(el.Label!, new CanvasStyle { FontSize = Math.Max(6, st.FontSize * 0.75), Align = "center", Color = st.Color })));
                        }
                        break;
                    case "text": body.Append(TextBox(id++, el, Paras(InvoiceDocumentFactory.Fill(el.Text, doc.Fields), st))); break;
                    case "field":
                        var v = el.Field != null ? doc.Fields.GetValueOrDefault(el.Field) : null;
                        body.Append(TextBox(id++, el, Paras(string.IsNullOrEmpty(el.Label) ? v ?? "" : $"{InvoiceDocumentFactory.Fill(el.Label, doc.Fields)} {v}".Trim(), st)));
                        break;
                    case "fiscal": body.Append(TextBox(id++, el, Paras(CanvasPdfRenderer.FiscalText(el, doc), st), grow: true)); break;
                    case "items": body.Append(TextBox(id++, el, ItemsTable(el, st, doc), grow: true)); break;
                    case "totals": body.Append(TextBox(id++, el, TotalsTable(el, st, doc), grow: true)); break;
                }
            }

            var section = "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"0\" w:right=\"0\" w:bottom=\"0\" w:left=\"0\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr>";
            var document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\""
                + " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\""
                + " xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">"
                + $"<w:body><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr>{body}</w:p>{section}</w:body></w:document>";

            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                + "<Relationship Id=\"rIdSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>");
            foreach (var f in media.Files) rels.Append($"<Relationship Id=\"{f.Name}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{f.Name}.{f.Ext}\"/>");
            rels.Append("</Relationships>");

            const string styles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Calibri\" w:cs=\"Calibri\"/><w:color w:val=\"0F172A\"/><w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/><w:lang w:val=\"en-ZW\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
                + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
                + "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"85\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"85\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style></w:styles>";
            const string settings = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>";
            var contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
                + "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>";
            const string rootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>";
            var core = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
                + $"<dc:title>{X(doc.Fields.GetValueOrDefault("title"))} {X(doc.Fields.GetValueOrDefault("invoiceNo"))}</dc:title><dc:creator>FiscalHub</dc:creator><dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>";

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                void Add(string name, byte[] b) { var e = zip.CreateEntry(name, CompressionLevel.Optimal); using var s = e.Open(); s.Write(b, 0, b.Length); }
                void AddText(string name, string xml) => Add(name, new UTF8Encoding(false).GetBytes(xml));
                AddText("[Content_Types].xml", contentTypes); AddText("_rels/.rels", rootRels); AddText("word/document.xml", document); AddText("word/_rels/document.xml.rels", rels.ToString());
                AddText("word/styles.xml", styles); AddText("word/settings.xml", settings); AddText("docProps/core.xml", core);
                foreach (var f in media.Files) Add($"word/media/{f.Name}.{f.Ext}", f.Bytes);
            }
            return ms.ToArray();
        }

        // ---- the standard layout, for invoices without a built design ----

        public static CanvasLayout StandardLayout() => new()
        {
            Elements = new List<CanvasElement>
            {
                new() { Type = "logo", X = 15, Y = 12, W = 45, H = 22 },
                new() { Type = "field", Field = "company", X = 65, Y = 12, W = 130, H = 9, Style = new CanvasStyle { FontSize = 16, Bold = true, Color = "#1d4ed8", Align = "right" } },
                new() { Type = "text", Text = "{address}\n{phone} · {email}\nTIN {tin} · VAT {vat}", X = 65, Y = 21, W = 130, H = 15, Style = new CanvasStyle { FontSize = 9, Align = "right", Color = "#475569" } },
                new() { Type = "field", Field = "title", X = 15, Y = 40, W = 180, H = 10, Style = new CanvasStyle { FontSize = 15, Bold = true, Color = "#ffffff", Fill = "#1d4ed8", Align = "center", VAlign = "middle" } },
                new() { Type = "text", Text = "Bill to:\n{buyerName}\nTIN {buyerTin} · VAT {buyerVat}\n{buyerAddress}", X = 15, Y = 54, W = 95, H = 24, Style = new CanvasStyle { FontSize = 9.5 } },
                new() { Type = "text", Text = "Invoice No: {invoiceNo}\nDate: {dateTime}\nCurrency: {currency}", X = 115, Y = 54, W = 80, H = 24, Style = new CanvasStyle { FontSize = 9.5, Align = "right" } },
                new() { Type = "items", X = 15, Y = 82, W = 180, H = 120, Columns = new List<string> { "no", "item", "hs", "qty", "price", "total" }, Style = new CanvasStyle { FontSize = 9, HeaderFill = "#1d4ed8", HeaderColor = "#ffffff", StripeFill = "#f1f5f9" } },
                new() { Type = "totals", X = 115, Y = 206, W = 80, H = 30, Style = new CanvasStyle { FontSize = 10, HeaderFill = "#1d4ed8", HeaderColor = "#ffffff" } },
                new() { Type = "qr", X = 15, Y = 240, W = 32, H = 36, Label = "Scan to verify", Style = new CanvasStyle { FontSize = 8 } },
                new() { Type = "fiscal", X = 50, Y = 240, W = 145, H = 36, Style = new CanvasStyle { FontSize = 7.5, Color = "#475569" } },
            },
        };

        // ---- drawing pieces ----

        private static string Anchor(int id, double x, double y, double w, double h, string graphic, bool behind = false) =>
            $"<w:r><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"{id + 1}\" behindDoc=\"{(behind ? 1 : 0)}\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">"
            + "<wp:simplePos x=\"0\" y=\"0\"/>"
            + $"<wp:positionH relativeFrom=\"page\"><wp:posOffset>{E(x)}</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"page\"><wp:posOffset>{E(y)}</wp:posOffset></wp:positionV>"
            + $"<wp:extent cx=\"{E(w)}\" cy=\"{E(h)}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/><wp:docPr id=\"{id}\" name=\"Element {id}\"/><wp:cNvGraphicFramePr/>"
            + $"<a:graphic>{graphic}</a:graphic></wp:anchor></w:drawing></w:r>";

        private static string Fill(CanvasStyle st)
        {
            var alpha = st.Opacity < 1 ? $"<a:alpha val=\"{(int)(Math.Clamp(st.Opacity, 0.05, 1) * 100000)}\"/>" : "";
            if (CanvasPdfRenderer.IsHex(st.Fill) && CanvasPdfRenderer.IsHex(st.Fill2))
            {
                var ang = st.GradientDir switch { "vertical" => 5400000, "diagonal" => 2700000, _ => 0 };
                return $"<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"{Rgb(st.Fill, "FFFFFF")}\">{alpha}</a:srgbClr></a:gs><a:gs pos=\"100000\"><a:srgbClr val=\"{Rgb(st.Fill2, "FFFFFF")}\">{alpha}</a:srgbClr></a:gs></a:gsLst><a:lin ang=\"{ang}\" scaled=\"0\"/></a:gradFill>";
            }
            return CanvasPdfRenderer.IsHex(st.Fill) ? $"<a:solidFill><a:srgbClr val=\"{Rgb(st.Fill, "FFFFFF")}\">{alpha}</a:srgbClr></a:solidFill>" : "<a:noFill/>";
        }

        private static string Outline(CanvasStyle st, bool line = false)
        {
            var w = line ? (st.BorderWidth > 0 ? st.BorderWidth : 1) : st.BorderWidth;
            var color = line ? Rgb(st.Border, Rgb(st.Color, "0F172A")) : Rgb(st.Border, "0F172A");
            return w > 0 && (line || CanvasPdfRenderer.IsHex(st.Border)) ? $"<a:ln w=\"{(long)(Math.Clamp(w, 0.25, 20) * 12700)}\"><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill></a:ln>" : "<a:ln><a:noFill/></a:ln>";
        }

        private static string Geometry(CanvasElement el, CanvasStyle st, double w, double h)
        {
            if (el.Shape == "ellipse") return "<a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom>";
            if (st.Radius > 0)
            {
                var adj = (int)Math.Clamp(st.Radius / Math.Max(Math.Min(w, h), 0.1) * 100000, 0, 50000);
                return $"<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val {adj}\"/></a:avLst></a:prstGeom>";
            }
            return "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>";
        }

        private static string Shape(int id, CanvasElement el, bool behind = false)
        {
            var (x, y, w, h) = CanvasPdfRenderer.Clamp(el);
            var st = el.Style ?? new CanvasStyle();
            var g = "<a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr/>"
                  + $"<wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{E(w)}\" cy=\"{E(h)}\"/></a:xfrm>{Geometry(el, st, w, h)}{Fill(st)}{Outline(st)}</wps:spPr>"
                  + "<wps:bodyPr/></wps:wsp></a:graphicData>";
            return Anchor(id, x, y, w, h, g, behind);
        }

        private static string Line(int id, CanvasElement el)
        {
            var (x, y, w, h) = CanvasPdfRenderer.Clamp(el);
            var st = el.Style ?? new CanvasStyle();
            var horizontal = w >= h;
            var (lx, ly, lw, lh) = horizontal ? (x, y + h / 2, w, 0d) : (x + w / 2, y, 0d, h);
            var g = "<a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvCnPr/>"
                  + $"<wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{E(lw)}\" cy=\"{E(lh)}\"/></a:xfrm><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>{Outline(st, true)}</wps:spPr>"
                  + "<wps:bodyPr/></wps:wsp></a:graphicData>";
            return Anchor(id, lx, ly, lw, lh, g);
        }

        private static string TextBox(int id, CanvasElement el, string content, bool grow = false)
        {
            var (x, y, w, h) = CanvasPdfRenderer.Clamp(el);
            var st = el.Style ?? new CanvasStyle();
            var pad = E(Math.Clamp(st.Padding, 0, 20));
            var anchor = st.VAlign switch { "middle" => "ctr", "bottom" => "b", _ => "t" };
            var g = "<a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:cNvSpPr txBox=\"1\"/>"
                  + $"<wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{E(w)}\" cy=\"{E(h)}\"/></a:xfrm>{Geometry(el, st, w, h)}{Fill(st)}{Outline(st)}</wps:spPr>"
                  + $"<wps:txbx><w:txbxContent>{(string.IsNullOrEmpty(content) ? "<w:p/>" : content)}</w:txbxContent></wps:txbx>"
                  + $"<wps:bodyPr rot=\"0\" vert=\"horz\" wrap=\"square\" lIns=\"{pad}\" tIns=\"{pad}\" rIns=\"{pad}\" bIns=\"{pad}\" anchor=\"{anchor}\" anchorCtr=\"0\">{(grow ? "<a:spAutoFit/>" : "<a:noAutofit/>")}</wps:bodyPr></wps:wsp></a:graphicData>";
            return Anchor(id, x, y, w, h, g);
        }

        private static string Picture(int id, CanvasElement el, byte[] bytes, Media media)
        {
            var (x, y, w, h) = CanvasPdfRenderer.Clamp(el);
            var st = el.Style ?? new CanvasStyle();
            var ext = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "jpeg" : "png";
            if (st.Fit != "stretch" && ImageSize(bytes) is { } size && size.W > 0 && size.H > 0)
            {
                // keep the picture's own shape inside the box, placed as aligned
                var scale = Math.Min(w / size.W, h / size.H);
                var (pw, ph) = (size.W * scale, size.H * scale);
                x += st.Align switch { "left" => 0, "right" => w - pw, _ => (w - pw) / 2 };
                y += st.VAlign switch { "top" => 0, "bottom" => h - ph, _ => (h - ph) / 2 };
                (w, h) = (pw, ph);
            }
            var rel = $"rIdImg{media.Files.Count + 1}";
            media.Files.Add((rel, bytes, ext));
            var g = "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
                  + $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"Picture {id}\"/><pic:cNvPicPr/></pic:nvPicPr>"
                  + $"<pic:blipFill><a:blip r:embed=\"{rel}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
                  + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{E(w)}\" cy=\"{E(h)}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData>";
            return Anchor(id, x, y, w, h, g);
        }

        /// <summary>Width and height of a PNG or JPEG in pixels (only the shape matters here).</summary>
        public static (double W, double H)? ImageSize(byte[] b)
        {
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50) return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var marker = b[i + 1];
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                    i += 2 + ((b[i + 2] << 8) | b[i + 3]);
                }
            }
            return null;
        }

        // ---- text ----

        private static string RunProps(CanvasStyle st, double? size = null, string? color = null, bool? bold = null)
        {
            var sb = new StringBuilder("<w:rPr>");
            if (!string.IsNullOrWhiteSpace(st.Font) && CanvasPdfRenderer.Fonts.Contains(st.Font)) sb.Append($"<w:rFonts w:ascii=\"{X(st.Font)}\" w:hAnsi=\"{X(st.Font)}\" w:cs=\"{X(st.Font)}\"/>");
            if (bold ?? st.Bold) sb.Append("<w:b/>");
            if (st.Italic) sb.Append("<w:i/>");
            if (st.Uppercase) sb.Append("<w:caps/>");
            sb.Append($"<w:color w:val=\"{Rgb(color ?? st.Color, "0F172A")}\"/>");
            var pt = Math.Clamp(size ?? st.FontSize, 4, 72);
            if (st.LetterSpacing != 0) sb.Append($"<w:spacing w:val=\"{(int)Math.Round(Math.Clamp(st.LetterSpacing, -0.1, 1) * pt * 20)}\"/>");
            var half = (int)Math.Round(pt * 2);
            sb.Append($"<w:sz w:val=\"{half}\"/><w:szCs w:val=\"{half}\"/>");
            if (st.Underline) sb.Append("<w:u w:val=\"single\"/>");
            return sb.Append("</w:rPr>").ToString();
        }

        private static string Jc(string? align) => align switch { "center" => "center", "right" => "right", _ => "left" };

        private static string Para(string text, CanvasStyle st, double? size = null, string? color = null, bool? bold = null, string? align = null) =>
            $"<w:p><w:pPr><w:spacing w:after=\"0\" w:line=\"{(int)Math.Round(240 * Math.Clamp(st.LineHeight, 0.8, 3))}\" w:lineRule=\"auto\"/><w:jc w:val=\"{Jc(align ?? st.Align)}\"/></w:pPr>"
            + $"<w:r>{RunProps(st, size, color, bold)}<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r></w:p>";

        private static string Paras(string text, CanvasStyle st) =>
            string.Concat((text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => Para(l, st)));

        private static string Cell(int width, string content, string? fill, string? borderBottom) =>
            $"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>"
            + (borderBottom != null ? $"<w:tcBorders><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"{borderBottom}\"/></w:tcBorders>" : "")
            + (fill != null ? $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/>" : "")
            + $"<w:vAlign w:val=\"center\"/></w:tcPr>{content}</w:tc>";

        private static string Table(int[] widths, string rows) =>
            $"<w:tbl><w:tblPr><w:tblW w:w=\"{widths.Sum()}\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/></w:tblPr><w:tblGrid>{string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>"))}</w:tblGrid>{rows}</w:tbl><w:p/>";

        private static string RowSpacing(CanvasStyle st, string inner) =>
            inner.Replace("<w:spacing w:after=\"0\"", $"<w:spacing w:before=\"{(int)(Math.Clamp(st.RowPadding, 0, 10) * TwipPerMm)}\" w:after=\"{(int)(Math.Clamp(st.RowPadding, 0, 10) * TwipPerMm)}\"");

        private static string ItemsTable(CanvasElement el, CanvasStyle st, InvoiceDocumentDto doc)
        {
            var cols = CanvasPdfRenderer.ColumnsOf(el);
            var total = (int)((Math.Max(el.W - 2 * Math.Clamp(st.Padding, 0, 20), 10)) * TwipPerMm);
            var sum = cols.Sum(CanvasPdfRenderer.ColumnWeight);
            var widths = cols.Select(k => (int)(total * CanvasPdfRenderer.ColumnWeight(k) / sum)).ToArray();
            string Align(string k) => k == "item" ? "left" : k is "no" or "hs" or "qty" or "tax" ? "center" : "right";
            var header = CanvasPdfRenderer.IsHex(st.HeaderFill) ? Rgb(st.HeaderFill, "FFFFFF") : null;
            var line = Rgb(st.LineColor, "E2E8F0");
            var rows = new StringBuilder("<w:tr><w:trPr><w:tblHeader/></w:trPr>");
            for (var i = 0; i < cols.Count; i++)
                rows.Append(Cell(widths[i], RowSpacing(st, Para(CanvasPdfRenderer.ColumnLabel(el, cols[i], doc), st, null, CanvasPdfRenderer.IsHex(st.HeaderColor) ? st.HeaderColor : st.Color, true, Align(cols[i]))), header, null));
            rows.Append("</w:tr>");
            for (var r = 0; r < doc.Lines.Count; r++)
            {
                var stripe = CanvasPdfRenderer.IsHex(st.StripeFill) && r % 2 == 1 ? Rgb(st.StripeFill, "FFFFFF") : null;
                rows.Append("<w:tr>");
                for (var i = 0; i < cols.Count; i++)
                    rows.Append(Cell(widths[i], RowSpacing(st, Para(CanvasPdfRenderer.Cell(doc.Lines[r], cols[i]), st, null, null, false, Align(cols[i]))), stripe, line));
                rows.Append("</w:tr>");
            }
            return Table(widths, rows.ToString());
        }

        private static string TotalsTable(CanvasElement el, CanvasStyle st, InvoiceDocumentDto doc)
        {
            var total = (int)((Math.Max(el.W - 2 * Math.Clamp(st.Padding, 0, 20), 10)) * TwipPerMm);
            var widths = new[] { (int)(total * 0.62), total - (int)(total * 0.62) };
            var line = Rgb(st.LineColor, "E2E8F0");
            var rows = new StringBuilder();
            foreach (var (label, amount, strong) in CanvasPdfRenderer.TotalLines(el, doc))
            {
                var fill = strong && CanvasPdfRenderer.IsHex(st.HeaderFill) ? Rgb(st.HeaderFill, "FFFFFF") : null;
                var color = strong && CanvasPdfRenderer.IsHex(st.HeaderColor) ? st.HeaderColor : st.Color;
                var size = strong ? st.FontSize * 1.15 : st.FontSize;
                rows.Append("<w:tr>")
                    .Append(Cell(widths[0], RowSpacing(st, Para(label, st, size, color, strong || st.Bold, "left")), fill, fill == null ? line : null))
                    .Append(Cell(widths[1], RowSpacing(st, Para(amount, st, size, color, strong || st.Bold, "right")), fill, fill == null ? line : null))
                    .Append("</w:tr>");
            }
            return Table(widths, rows.ToString());
        }
    }
}
