using System.Globalization;
using System.Text;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Fdms.Infrastructure.Templates
{
    /// <summary>
    /// Draws an invoice exactly as laid out in the invoice builder: every element at its place on the A4 page (millimetres from the top-left corner),
    /// later elements over earlier ones. Text that is too long for its box is shrunk to fit rather than cut off; the fiscal details always print.
    /// </summary>
    public static class CanvasPdfRenderer
    {
        public const double PageW = 210, PageH = 297;
        private const float PtPerMm = 72f / 25.4f;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static float P(double mm) => (float)(mm * PtPerMm);

        /// <summary>Fonts the builder offers (the server must have them; the built-in Lato is always there).</summary>
        public static readonly IReadOnlyList<string> Fonts = new[] { "Lato", "Arial", "Times New Roman", "Georgia", "Verdana", "Trebuchet MS", "Courier New", "Segoe UI" };

        public static readonly IReadOnlyList<string> ItemColumns = new[] { "no", "item", "hs", "qty", "price", "tax", "total" };
        public static readonly IReadOnlyList<string> TotalRows = new[] { "subtotal", "taxes", "total", "payments" };

        public static IDocument Build(Receipt r, Device d, InvoiceTemplateSettings s, byte[]? logo)
        {
            var doc = InvoiceDocumentFactory.Build(r, d, s);
            var watermark = r.Status == ReceiptStatus.Submitted ? null : r.Status == ReceiptStatus.Pending ? "NOT YET CONFIRMED BY ZIMRA" : "REJECTED BY ZIMRA";
            return Draw(doc, s.Canvas!, logo, s.Assets, Qr(r.QrUrl), watermark);
        }

        public static byte[]? Qr(string? url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            return new PngByteQRCode(data).GetGraphic(8);
        }

        /// <summary>Keeps a box on the page and at least 1 mm big.</summary>
        public static (double X, double Y, double W, double H) Clamp(CanvasElement e)
        {
            var x = Math.Clamp(e.X, 0, PageW - 1); var y = Math.Clamp(e.Y, 0, PageH - 1);
            var w = Math.Clamp(e.W, 1, PageW - x - 0.05); var h = Math.Clamp(e.H, 1, PageH - y - 0.05);
            return (x, y, w, h);
        }

        public static IDocument Draw(InvoiceDocumentDto doc, CanvasLayout layout, byte[]? logo, IReadOnlyDictionary<Guid, byte[]>? assets, byte[]? qrPng, string? watermark) =>
            Document.Create(c => c.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor("#0f172a"));
                page.Content().Layers(layers =>
                {
                    layers.PrimaryLayer().Background(Hex(layout.Background, "#ffffff")).Extend();
                    foreach (var el in layout.Elements)
                    {
                        var (x, y, w, h) = Clamp(el);
                        var box = layers.Layer().PaddingLeft(P(x)).PaddingTop(P(y)).Width(P(w)).Height(P(h));
                        Element(box, el, doc, logo, assets, qrPng);
                    }
                    if (watermark != null)
                        layers.Layer().PaddingTop(P(4)).AlignCenter().Background("#fee2e2").PaddingVertical(3).PaddingHorizontal(10)
                            .Text(watermark).Bold().FontSize(11).FontColor("#b91c1c");
                });
            }));

        private static void Element(IContainer c, CanvasElement el, InvoiceDocumentDto doc, byte[]? logo, IReadOnlyDictionary<Guid, byte[]>? assets, byte[]? qrPng)
        {
            var st = el.Style ?? new CanvasStyle();
            switch (el.Type)
            {
                case "shape":
                    c.Svg(size => BoxSvg(size, st, el.Shape == "ellipse"));
                    return;
                case "line":
                    c.Svg(size => LineSvg(size, st));
                    return;
            }

            // a box behind the content: a plain colour directly; rounded corners, borders, gradients and transparency as a drawing
            var needsDrawing = st.Radius > 0 || st.BorderWidth > 0 && IsHex(st.Border) || IsHex(st.Fill2) || st.Opacity < 1 && IsHex(st.Fill);
            if (needsDrawing)
            {
                c.Layers(l =>
                {
                    l.PrimaryLayer().Svg(size => BoxSvg(size, st, false));
                    l.Layer().Element(inner => Content(inner, el, st, doc, logo, assets, qrPng));
                });
                return;
            }
            if (IsHex(st.Fill)) c = c.Background(st.Fill!);
            Content(c, el, st, doc, logo, assets, qrPng);
        }

        private static void Content(IContainer c, CanvasElement el, CanvasStyle st, InvoiceDocumentDto doc, byte[]? logo, IReadOnlyDictionary<Guid, byte[]>? assets, byte[]? qrPng)
        {
            var padded = c.Padding(P(Math.Clamp(st.Padding, 0, 20)));
            switch (el.Type)
            {
                case "text":
                    TextBox(padded, InvoiceDocumentFactory.Fill(el.Text, doc.Fields), st);
                    break;
                case "field":
                    var value = el.Field != null ? doc.Fields.GetValueOrDefault(el.Field) : null;
                    TextBox(padded, string.IsNullOrEmpty(el.Label) ? value ?? "" : $"{InvoiceDocumentFactory.Fill(el.Label, doc.Fields)} {value}".Trim(), st);
                    break;
                case "logo":
                    Picture(padded, logo, st);
                    break;
                case "image":
                    Picture(padded, el.AssetId is { } id && assets != null ? assets.GetValueOrDefault(id) : null, st);
                    break;
                case "qr":
                    if (qrPng == null) break;
                    // the caption gets its line first; the code takes the rest of the box
                    var captionSize = Size(st) * 0.75f;
                    var caption = string.IsNullOrWhiteSpace(el.Label) ? 0 : captionSize * 1.5f;
                    var codeHeight = Math.Max(P(Clamp(el).H) - 2 * P(Math.Clamp(st.Padding, 0, 20)) - caption, 4);
                    padded.Column(col =>
                    {
                        col.Item().Height(codeHeight).AlignCenter().Image(qrPng).FitArea();
                        if (caption > 0) col.Item().AlignCenter().Text(el.Label!.Trim()).FontSize(captionSize).FontColor(Hex(st.Color, "#0f172a"));
                    });
                    break;
                case "fiscal":
                    TextBox(padded, FiscalText(el, doc), st);
                    break;
                case "items":
                    padded.ScaleToFit().Element(t => Items(t, el, st, doc));
                    break;
                case "totals":
                    padded.ScaleToFit().Element(t => Totals(t, el, st, doc));
                    break;
            }
        }

        public static string FiscalText(CanvasElement el, InvoiceDocumentDto doc)
        {
            var f = doc.Fiscal;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(el.Label)) sb.AppendLine(el.Label!.Trim());
            sb.AppendLine($"Receipt {f.ReceiptCounter} · Global {f.GlobalNo} · Fiscal day {f.FiscalDay} · Device {f.DeviceId}");
            if (f.ZimraReceiptId.HasValue) sb.AppendLine($"ZIMRA receipt ID: {f.ZimraReceiptId}");
            if (!string.IsNullOrEmpty(f.VerificationUrl)) sb.AppendLine($"Verify: {f.VerificationUrl}");
            if (!string.IsNullOrEmpty(f.Signature)) sb.AppendLine($"Device signature: {f.Signature}");
            if (f.Notices.Count > 0) sb.AppendLine("ZIMRA validation notices: " + string.Join(", ", f.Notices));
            return sb.ToString().TrimEnd();
        }

        private static float Size(CanvasStyle st) => (float)Math.Clamp(st.FontSize, 4, 72);

        private static void TextBox(IContainer c, string text, CanvasStyle st)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (st.Uppercase) text = text.ToUpperInvariant();
            var v = st.VAlign switch { "middle" => c.AlignMiddle(), "bottom" => c.AlignBottom(), _ => c.AlignTop() };
            v.ScaleToFit().Text(t =>
            {
                switch (st.Align) { case "center": t.AlignCenter(); break; case "right": t.AlignRight(); break; default: t.AlignLeft(); break; }
                Styled(t.Span(text), st);
            });
        }

        private static TextSpanDescriptor Styled(TextSpanDescriptor s, CanvasStyle st, float? size = null, string? color = null, bool? bold = null)
        {
            s.FontSize(size ?? Size(st)).FontColor(Hex(color ?? st.Color, "#0f172a")).LineHeight((float)Math.Clamp(st.LineHeight, 0.8, 3));
            if (bold ?? st.Bold) s.Bold();
            if (st.Italic) s.Italic();
            if (st.Underline) s.Underline();
            if (st.LetterSpacing != 0) s.LetterSpacing((float)Math.Clamp(st.LetterSpacing, -0.1, 1));
            if (!string.IsNullOrWhiteSpace(st.Font) && Fonts.Contains(st.Font)) s.FontFamily(st.Font);
            return s;
        }

        private static void Picture(IContainer c, byte[]? bytes, CanvasStyle st)
        {
            if (bytes is not { Length: > 0 }) return;
            var aligned = st.Align switch { "left" => c.AlignLeft(), "right" => c.AlignRight(), _ => c.AlignCenter() };
            aligned = st.VAlign switch { "top" => aligned.AlignTop(), "bottom" => aligned.AlignBottom(), _ => aligned.AlignMiddle() };
            if (st.Fit == "stretch") c.Image(bytes).FitUnproportionally();
            else aligned.Image(bytes).FitArea();
        }

        private static string Label(CanvasElement el, string key, string dflt, InvoiceDocumentDto doc) =>
            InvoiceDocumentFactory.Fill(el.Labels != null && el.Labels.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : dflt, doc.Fields);

        public static List<string> ColumnsOf(CanvasElement el) =>
            el.Columns is { Count: > 0 } ? el.Columns.Where(ItemColumns.Contains).Distinct().ToList() is { Count: > 0 } c ? c : ItemColumns.ToList() : new List<string> { "no", "item", "qty", "price", "total" };

        public static string ColumnLabel(CanvasElement el, string key, InvoiceDocumentDto doc) => Label(el, key, key switch
        {
            "no" => "#", "item" => "Description", "hs" => "HS Code", "qty" => "Qty", "price" => "Unit price", "tax" => "VAT", _ => "Amount",
        }, doc);

        public static float ColumnWeight(string key) => key switch { "no" => 0.55f, "item" => 4.6f, "hs" => 1.6f, "qty" => 1f, "price" => 1.7f, "tax" => 1.1f, _ => 1.9f };

        public static string Cell(InvoiceDocumentLine l, string key) => key switch
        {
            "no" => l.No.ToString(Inv),
            "item" => l.Description + (l.Discount ? " (discount)" : ""),
            "hs" => l.HsCode ?? "",
            "qty" => l.Quantity.ToString("0.####", Inv),
            "price" => InvoiceDocumentFactory.Money(l.UnitPrice),
            "tax" => l.TaxPercent.HasValue ? $"{l.TaxPercent:0.##}%" : "Exempt",
            _ => InvoiceDocumentFactory.Money(l.Amount),
        };

        private static void Items(IContainer c, CanvasElement el, CanvasStyle st, InvoiceDocumentDto doc)
        {
            var cols = ColumnsOf(el);
            var fs = Size(st);
            var pad = P(Math.Clamp(st.RowPadding, 0, 10));
            var line = Hex(st.LineColor, "#e2e8f0");
            c.Table(t =>
            {
                t.ColumnsDefinition(cd => { foreach (var k in cols) cd.RelativeColumn(ColumnWeight(k)); });
                t.Header(h =>
                {
                    foreach (var k in cols)
                    {
                        IContainer cell = h.Cell();
                        if (IsHex(st.HeaderFill)) cell = cell.Background(st.HeaderFill!);
                        cell = cell.PaddingVertical(pad + 1).PaddingHorizontal(4);
                        var aligned = k is "item" ? cell.AlignLeft() : k is "no" or "hs" or "qty" or "tax" ? cell.AlignCenter() : cell.AlignRight();
                        aligned.Text(t2 => Styled(t2.Span(ColumnLabel(el, k, doc)), st, fs, IsHex(st.HeaderColor) ? st.HeaderColor : st.Color, true));
                    }
                });
                for (var i = 0; i < doc.Lines.Count; i++)
                {
                    var l = doc.Lines[i];
                    var stripe = IsHex(st.StripeFill) && i % 2 == 1;
                    foreach (var k in cols)
                    {
                        IContainer cell = t.Cell();
                        if (stripe) cell = cell.Background(st.StripeFill!);
                        cell = cell.BorderBottom(0.5f).BorderColor(line).PaddingVertical(pad).PaddingHorizontal(4);
                        var aligned = k is "item" ? cell.AlignLeft() : k is "no" or "hs" or "qty" or "tax" ? cell.AlignCenter() : cell.AlignRight();
                        aligned.Text(t2 => Styled(t2.Span(Cell(l, k)), st, fs, null, false));
                    }
                }
            });
        }

        public static List<string> RowsOf(CanvasElement el) =>
            el.Columns is { Count: > 0 } ? el.Columns.Where(TotalRows.Contains).Distinct().ToList() is { Count: > 0 } c ? c : TotalRows.Take(3).ToList() : TotalRows.Take(3).ToList();

        /// <summary>The rows of the totals block: (label, amount, emphasised).</summary>
        public static List<(string Label, string Amount, bool Strong)> TotalLines(CanvasElement el, InvoiceDocumentDto doc)
        {
            var rows = new List<(string, string, bool)>();
            foreach (var k in RowsOf(el))
            {
                switch (k)
                {
                    case "subtotal": rows.Add((Label(el, "subtotal", "Subtotal ({currency})", doc), InvoiceDocumentFactory.Money(doc.Subtotal), false)); break;
                    case "taxes":
                        foreach (var t in doc.Taxes)
                            rows.Add((t.Percent.HasValue ? Label(el, "taxes", "VAT ({rate})", doc).Replace("{rate}", $"{t.Percent:0.##}%") : "Exempt", InvoiceDocumentFactory.Money(t.Tax), false));
                        break;
                    case "total": rows.Add((Label(el, "total", "TOTAL ({currency})", doc), InvoiceDocumentFactory.Money(doc.Total), true)); break;
                    case "payments": foreach (var p in doc.Payments) rows.Add((p.Method, InvoiceDocumentFactory.Money(p.Amount), false)); break;
                }
            }
            return rows;
        }

        private static void Totals(IContainer c, CanvasElement el, CanvasStyle st, InvoiceDocumentDto doc)
        {
            var fs = Size(st);
            var pad = P(Math.Clamp(st.RowPadding, 0, 10));
            var line = Hex(st.LineColor, "#e2e8f0");
            c.Column(col =>
            {
                foreach (var (label, amount, strong) in TotalLines(el, doc))
                {
                    var row = col.Item();
                    if (strong && IsHex(st.HeaderFill)) row = row.Background(st.HeaderFill!);
                    else row = row.BorderBottom(0.5f).BorderColor(line);
                    var color = strong && IsHex(st.HeaderColor) ? st.HeaderColor : st.Color;
                    row.PaddingVertical(pad).PaddingHorizontal(4).Row(r =>
                    {
                        r.RelativeItem().Text(t => Styled(t.Span(label), st, strong ? fs * 1.15f : fs, color, strong || st.Bold));
                        r.AutoItem().AlignRight().Text(t => Styled(t.Span(amount), st, strong ? fs * 1.15f : fs, color, strong || st.Bold));
                    });
                }
            });
        }

        // ---- drawings ----

        public static bool IsHex(string? v) => v != null && System.Text.RegularExpressions.Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$");
        public static string Hex(string? v, string dflt) => IsHex(v) ? v! : dflt;
        private static string F(double v) => v.ToString("0.##", Inv);

        private static string Paint(CanvasStyle st, string id)
        {
            if (IsHex(st.Fill) && IsHex(st.Fill2))
            {
                var (x2, y2) = st.GradientDir switch { "vertical" => (0, 1), "diagonal" => (1, 1), _ => (1, 0) };
                return $"<defs><linearGradient id=\"{id}\" x1=\"0\" y1=\"0\" x2=\"{x2}\" y2=\"{y2}\"><stop offset=\"0\" stop-color=\"{st.Fill}\"/><stop offset=\"1\" stop-color=\"{st.Fill2}\"/></linearGradient></defs>";
            }
            return "";
        }

        public static string BoxSvg(Size size, CanvasStyle st, bool ellipse)
        {
            double w = Math.Max(size.Width, 1), h = Math.Max(size.Height, 1);
            var bw = IsHex(st.Border) ? Math.Clamp(st.BorderWidth, 0, 20) : 0;
            var fill = IsHex(st.Fill) && IsHex(st.Fill2) ? "url(#g)" : IsHex(st.Fill) ? st.Fill! : "none";
            var stroke = bw > 0 ? $" stroke=\"{st.Border}\" stroke-width=\"{F(bw)}\"" : "";
            var op = Math.Clamp(st.Opacity, 0.05, 1);
            var shape = ellipse
                ? $"<ellipse cx=\"{F(w / 2)}\" cy=\"{F(h / 2)}\" rx=\"{F(Math.Max(w / 2 - bw / 2, 0.1))}\" ry=\"{F(Math.Max(h / 2 - bw / 2, 0.1))}\" fill=\"{fill}\" fill-opacity=\"{F(op)}\"{stroke}/>"
                : $"<rect x=\"{F(bw / 2)}\" y=\"{F(bw / 2)}\" width=\"{F(Math.Max(w - bw, 0.1))}\" height=\"{F(Math.Max(h - bw, 0.1))}\" rx=\"{F(Math.Min(P(st.Radius), Math.Min(w, h) / 2))}\" fill=\"{fill}\" fill-opacity=\"{F(op)}\"{stroke}/>";
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\" viewBox=\"0 0 {F(w)} {F(h)}\">{Paint(st, "g")}{shape}</svg>";
        }

        public static string LineSvg(Size size, CanvasStyle st)
        {
            double w = Math.Max(size.Width, 1), h = Math.Max(size.Height, 1);
            var color = Hex(st.Border, Hex(st.Color, "#0f172a"));
            var bw = Math.Clamp(st.BorderWidth > 0 ? st.BorderWidth : 1, 0.25, 20);
            var (x1, y1, x2, y2) = w >= h ? (0d, h / 2, w, h / 2) : (w / 2, 0d, w / 2, h);
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h)}\" viewBox=\"0 0 {F(w)} {F(h)}\"><line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{color}\" stroke-width=\"{F(bw)}\" stroke-opacity=\"{F(Math.Clamp(st.Opacity, 0.05, 1))}\"/></svg>";
        }
    }
}
