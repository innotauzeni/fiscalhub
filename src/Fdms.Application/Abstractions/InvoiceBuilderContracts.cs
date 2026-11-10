namespace Fdms.Application.Abstractions
{
    // =================================================================================================================
    // The invoice builder: a free A4 layout. Every element sits at X/Y with a W/H size, all in millimetres from the top-left corner of the page;
    // later elements are drawn over earlier ones. The fiscal parts ZIMRA requires (QR code, fiscal details, items and totals) must be on the page.
    // =================================================================================================================

    public sealed class CanvasLayout
    {
        /// <summary>The page colour behind everything, as #rrggbb.</summary>
        public string Background { get; set; } = "#ffffff";
        public List<CanvasElement> Elements { get; set; } = new();
    }

    public sealed class CanvasElement
    {
        public string Id { get; set; } = "";
        /// <summary>text, field, image, logo, shape, line, items, totals, qr, fiscal.</summary>
        public string Type { get; set; } = "text";
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; } = 60;
        public double H { get; set; } = 10;
        /// <summary>text: the words, with {placeholders} such as {invoiceNo} or {buyerName} filled in when the invoice prints.</summary>
        public string? Text { get; set; }
        /// <summary>field: the one value it shows (a key of <see cref="InvoiceFields"/>).</summary>
        public string? Field { get; set; }
        /// <summary>field: words before the value ("Invoice No:"). qr: the caption under the code.</summary>
        public string? Label { get; set; }
        /// <summary>image: the uploaded picture.</summary>
        public Guid? AssetId { get; set; }
        /// <summary>shape: rect or ellipse.</summary>
        public string? Shape { get; set; }
        public bool Locked { get; set; }
        /// <summary>items: the columns in order, from no, item, hs, qty, price, tax, total. totals: the rows from subtotal, taxes, total, payments.</summary>
        public List<string>? Columns { get; set; }
        /// <summary>items and totals: the company's own headings, by column or row key (e.g. "item" = "Description (Product / Service)").</summary>
        public Dictionary<string, string>? Labels { get; set; }
        public CanvasStyle Style { get; set; } = new();
    }

    public sealed class CanvasStyle
    {
        public double FontSize { get; set; } = 10;
        public string? Font { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public bool Uppercase { get; set; }
        public double LetterSpacing { get; set; }
        public double LineHeight { get; set; } = 1.2;
        public string Color { get; set; } = "#0f172a";
        /// <summary>left, center or right.</summary>
        public string Align { get; set; } = "left";
        /// <summary>top, middle or bottom.</summary>
        public string VAlign { get; set; } = "top";
        /// <summary>Background of the box (#rrggbb), or null for none. With Fill2 it is a gradient from Fill to Fill2.</summary>
        public string? Fill { get; set; }
        public string? Fill2 { get; set; }
        /// <summary>horizontal, vertical or diagonal.</summary>
        public string GradientDir { get; set; } = "horizontal";
        public string? Border { get; set; }
        /// <summary>Border (and line) thickness in points.</summary>
        public double BorderWidth { get; set; }
        /// <summary>Rounded corners, in millimetres.</summary>
        public double Radius { get; set; }
        /// <summary>0.05 to 1 (box colours only).</summary>
        public double Opacity { get; set; } = 1;
        /// <summary>Space inside the box, in millimetres.</summary>
        public double Padding { get; set; } = 1;
        // ---- items and totals tables ----
        public string? HeaderFill { get; set; }
        public string? HeaderColor { get; set; }
        public string? StripeFill { get; set; }
        public string? LineColor { get; set; }
        /// <summary>Space above and below each row, in millimetres.</summary>
        public double RowPadding { get; set; } = 1.5;
        // ---- pictures ----
        /// <summary>contain (keep its shape) or stretch (fill the box).</summary>
        public string Fit { get; set; } = "contain";
    }

    /// <summary>The values an invoice can show, with what each means. Used in {placeholders} and field elements.</summary>
    public static class InvoiceFields
    {
        public sealed record FieldInfo(string Key, string Label, string Group);

        public static readonly IReadOnlyList<FieldInfo> All = new FieldInfo[]
        {
            new("title", "Document title (TAX INVOICE / CREDIT NOTE)", "Invoice"), new("invoiceNo", "Invoice number", "Invoice"), new("date", "Invoice date", "Invoice"),
            new("dateTime", "Invoice date and time", "Invoice"), new("currency", "Currency", "Invoice"), new("notes", "Notes", "Invoice"), new("paymentMethod", "How it was paid", "Invoice"),
            new("originalInvoice", "Original invoice (credit / debit notes)", "Invoice"),
            new("company", "Company name", "Company"), new("tin", "Company TIN", "Company"), new("vat", "Company VAT number", "Company"), new("address", "Company address", "Company"),
            new("phone", "Company phone", "Company"), new("email", "Company email", "Company"), new("website", "Company website", "Company"), new("branch", "Branch", "Company"),
            new("buyerName", "Customer name", "Customer"), new("buyerTradeName", "Customer trade name", "Customer"), new("buyerTin", "Customer TIN", "Customer"), new("buyerVat", "Customer VAT number", "Customer"),
            new("buyerAddress", "Customer address", "Customer"), new("buyerPhone", "Customer phone", "Customer"), new("buyerEmail", "Customer email", "Customer"),
            new("subtotal", "Subtotal (before VAT)", "Totals"), new("taxTotal", "VAT total", "Totals"), new("total", "Total", "Totals"), new("taxRates", "VAT rate(s), e.g. 15%", "Totals"),
            new("bankName", "Bank", "Payment"), new("accountName", "Account name", "Payment"), new("accountNumber", "Account number", "Payment"), new("bankBranch", "Bank branch", "Payment"),
            new("bankDetails", "Payment details (from the template)", "Payment"), new("terms", "Terms (from the template)", "Payment"), new("footer", "Footer (from the template)", "Payment"),
            new("deviceId", "Fiscal device ID", "Fiscal"), new("fiscalDay", "Fiscal day", "Fiscal"), new("receiptCounter", "Receipt number in the day", "Fiscal"), new("globalNo", "Global receipt number", "Fiscal"),
            new("zimraReceiptId", "ZIMRA receipt ID", "Fiscal"), new("verificationUrl", "Verification address", "Fiscal"), new("signature", "Device signature", "Fiscal"),
        };

        public static readonly IReadOnlySet<string> Keys = All.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
    }

    // ---- the invoice as data (for other systems, and what every renderer draws from) ----

    public sealed record InvoiceDocumentLine(int No, string Description, string? HsCode, decimal Quantity, decimal UnitPrice, decimal? TaxPercent, decimal Amount, bool Discount);
    public sealed record InvoiceDocumentTax(string Label, decimal? Percent, decimal Net, decimal Tax, decimal Gross);
    public sealed record InvoiceDocumentPayment(string Method, decimal Amount);
    public sealed record InvoiceDocumentFiscal(int DeviceId, int FiscalDay, int ReceiptCounter, long GlobalNo, long? ZimraReceiptId, string? VerificationUrl, string? Signature, string Status, IReadOnlyList<string> Notices);

    /// <summary>
    /// A fiscal invoice as structured data, ready for another system to store or draw: every printable value (Fields, keyed as in <see cref="InvoiceFields"/>),
    /// the lines, the tax summary, payments, totals and the fiscal identifiers. Template is the layout used, when the company built one.
    /// </summary>
    public sealed record InvoiceDocumentDto(string Format, int Version, string Type, string Currency, IReadOnlyDictionary<string, string?> Fields,
        IReadOnlyList<InvoiceDocumentLine> Lines, IReadOnlyList<InvoiceDocumentTax> Taxes, IReadOnlyList<InvoiceDocumentPayment> Payments,
        decimal Subtotal, decimal TaxTotal, decimal Total, InvoiceDocumentFiscal Fiscal, CanvasLayout? Template);

    // ---- builder files ----

    public sealed record TemplateAssetDto(Guid Id, string ContentType, int Size, string? Name, DateTime CreatedAt);

    /// <summary>A template as a file, to copy it to another company or keep it: its settings (with the layout), its logo and its pictures.</summary>
    public sealed record TemplateExportDto(string Format, int Version, string Name, InvoiceTemplateSettings Settings, string? Logo, string? LogoContentType, IReadOnlyList<TemplateExportAsset> Assets);
    public sealed record TemplateExportAsset(Guid Id, string ContentType, string Data, string? Name);

    /// <summary>Renders the sample invoice with the settings being edited, as pdf, docx or json.</summary>
    public sealed record RenderTemplateRequest(InvoiceTemplateSettings Settings, string Format, Guid? TemplateId);
    public sealed record RenderedFile(byte[] Bytes, string ContentType, string FileName);
}

namespace Fdms.Application.Abstractions
{
    /// <summary>
    /// The heading of a printed fiscal document. ZIMRA's wording is required: an invoice always says FISCAL TAX INVOICE (a company's own title is used
    /// only when it contains those words, e.g. "FISCAL TAX INVOICE / QUOTATION REF"); credit and debit notes keep their own titles.
    /// </summary>
    public static class FiscalTitles
    {
        public const string Invoice = "FISCAL TAX INVOICE";
        public const string CreditNote = "CREDIT NOTE";
        public const string DebitNote = "DEBIT NOTE";

        public static bool IsAcceptable(string? custom) => !string.IsNullOrWhiteSpace(custom) && custom.Contains(Invoice, StringComparison.OrdinalIgnoreCase);

        public static string ForInvoice(string? custom) => IsAcceptable(custom) ? custom!.Trim().ToUpperInvariant() : Invoice;

        /// <summary>A line of free text that is a document heading on its own ("INVOICE", "Tax invoice") rather than the required wording.</summary>
        public static bool IsBareHeading(string line)
        {
            var l = System.Text.RegularExpressions.Regex.Replace(line ?? "", @"[^A-Za-z ]", " ").Trim();
            l = System.Text.RegularExpressions.Regex.Replace(l, @"\s+", " ").ToUpperInvariant();
            return l is "INVOICE" or "TAX INVOICE" or "SALES INVOICE" or "TAX INVOICE RECEIPT" or "INVOICE RECEIPT" or "RECEIPT" or "BILL" or "FISCAL INVOICE";
        }
    }
}
