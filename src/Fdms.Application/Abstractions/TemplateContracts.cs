namespace Fdms.Application.Abstractions
{
    /// <summary>
    /// What a company may change on its printed invoices. The fiscal parts (invoice and receipt numbers, device id, tax summary, ZIMRA QR,
    /// device signature, ZIMRA receipt id, validation notices) are always printed and cannot be switched off here.
    /// </summary>
    public sealed class InvoiceTemplateSettings
    {
        /// <summary>Colour of the title, rules and table header, as #rrggbb.</summary>
        public string Accent { get; set; } = "#2563eb";
        public bool ShowLogo { get; set; } = true;
        /// <summary>left, center or right (A4; the 80 mm receipt is always centred).</summary>
        public string LogoAlign { get; set; } = "left";
        /// <summary>Shown instead of the taxpayer name ZIMRA holds (e.g. a trading name).</summary>
        public string? DisplayName { get; set; }
        /// <summary>Address, phone, email, website: one line per row.</summary>
        public string? HeaderText { get; set; }
        /// <summary>The invoice heading. It must contain FISCAL TAX INVOICE (ZIMRA's wording), e.g. "FISCAL TAX INVOICE - ORIGINAL"; empty = FISCAL TAX INVOICE. Credit and debit notes keep their own titles.</summary>
        public string? InvoiceTitle { get; set; }
        public bool ShowBuyer { get; set; } = true;
        public bool ShowPayments { get; set; } = true;
        public bool ShowHsCode { get; set; }
        public bool ShowQuantity { get; set; } = true;
        public bool ShowUnitPrice { get; set; } = true;
        /// <summary>Print the tax rate next to each line, e.g. "[15%]".</summary>
        public bool ShowLineTax { get; set; } = true;
        public string? BankDetails { get; set; }
        public string? Terms { get; set; }
        public string? FooterText { get; set; }

        // ---- layout (A4): every value is chosen from a closed list, and the defaults reproduce the standard look ----
        /// <summary>plain, or band (the company block on a coloured band).</summary>
        public string HeaderStyle { get; set; } = "plain";
        /// <summary>band (white text on the accent colour), plain or underline.</summary>
        public string TitleStyle { get; set; } = "band";
        /// <summary>left, center or right.</summary>
        public string TitleAlign { get; set; } = "center";
        /// <summary>lines, striped, grid or solid (header filled with the accent colour).</summary>
        public string TableStyle { get; set; } = "lines";
        /// <summary>stacked (one list) or columns (invoice details on the left, buyer on the right).</summary>
        public string PartiesLayout { get; set; } = "stacked";
        /// <summary>full (across the page) or right (a compact block on the right).</summary>
        public string TotalsAlign { get; set; } = "full";
        /// <summary>Where the verification QR sits: left, center or right at the foot, top-left or top-right beside the invoice details, or with-totals (beside the totals).</summary>
        public string QrPosition { get; set; } = "center";
        /// <summary>small, medium or large.</summary>
        public string QrSize { get; set; } = "medium";
        /// <summary>compact, normal or relaxed: the space inside each table row and between text lines.</summary>
        public string RowSpacing { get; set; } = "normal";
        /// <summary>The word for the grand total line (default TOTAL), separate from the table's total column heading.</summary>
        public string? LabelGrandTotal { get; set; }
        /// <summary>small, medium or large.</summary>
        public string LogoSize { get; set; } = "medium";
        /// <summary>narrow, normal or wide.</summary>
        public string Margins { get; set; } = "normal";
        /// <summary>Body text size in points (8-13).</summary>
        public int FontSize { get; set; } = 10;
        /// <summary>Order of the table columns: item, hs, qty, price, tax, total. Null keeps the standard columns. Item and total are always present.</summary>
        public List<string>? Columns { get; set; }
        // ---- the company's own words for the standard labels (empty = ours) ----
        public string? LabelInvoiceNo { get; set; }
        public string? LabelDate { get; set; }
        public string? LabelBuyer { get; set; }
        public string? LabelItem { get; set; }
        public string? LabelHsCode { get; set; }
        public string? LabelQuantity { get; set; }
        public string? LabelPrice { get; set; }
        public string? LabelTax { get; set; }
        public string? LabelTotal { get; set; }
        // ---- contact details that fill [Company Address], [Telephone], [Email] and [Website] in the wording (empty = the branch's, then ZIMRA's, details) ----
        public string? Address { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string? Website { get; set; }

        /// <summary>The company's active bank accounts, loaded when an invoice prints so [Bank Name] and [Account Number] can be filled. Never stored or sent to the browser.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public List<BankAccountInfo>? Banks { get; set; }

        /// <summary>A free layout built in the invoice builder (A4). When it has elements, A4 invoices print exactly as laid out here; the 80 mm roll keeps the standard layout.</summary>
        public CanvasLayout? Canvas { get; set; }

        /// <summary>The pictures the canvas uses, loaded when an invoice prints. Never stored here or sent to the browser.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public Dictionary<Guid, byte[]>? Assets { get; set; }
    }

    public sealed record BankAccountInfo(string Bank, string AccountName, string AccountNumber, string? BranchName, string? BranchCode, string Currency, bool IsDefault);

    /// <summary>What the AI read from an existing invoice: settings ready to apply in the editor, and notes about what could not be matched.</summary>
    public sealed record TemplateAnalysis(InvoiceTemplateSettings Settings, IReadOnlyList<string> Notes, string? Summary);

    public sealed record TemplateDto(Guid Id, Guid? BranchId, string? BranchName, string Name, InvoiceTemplateSettings Settings, bool HasLogo, DateTime UpdatedAt, int DeviceCount = 0);

    /// <summary>BranchId null = the company's default template. One template per company default and per branch.</summary>
    public sealed record SaveTemplateRequest(Guid? Id, Guid? BranchId, string Name, InvoiceTemplateSettings Settings);

    /// <summary>Renders a sample invoice with the settings being edited (not saved). Format is "a4" or "receipt"; TemplateId supplies the stored logo.</summary>
    public sealed record PreviewTemplateRequest(InvoiceTemplateSettings Settings, string Format, Guid? TemplateId, bool Large = false);

    public sealed record TemplateLogo(byte[] Bytes, string ContentType);

    public interface ITemplateService
    {
        Task<IReadOnlyList<TemplateDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<TemplateDto> SaveAsync(Guid clientId, SaveTemplateRequest req, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<TemplateDto> SetLogoAsync(Guid clientId, Guid id, byte[] bytes, string contentType, CancellationToken ct);
        Task<TemplateDto> RemoveLogoAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<TemplateLogo?> GetLogoAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<byte[]> PreviewAsync(Guid clientId, PreviewTemplateRequest req, CancellationToken ct);
        /// <summary>Reads an existing invoice (PDF, PNG or JPEG) with the AI and proposes matching settings. Nothing is saved.</summary>
        Task<TemplateAnalysis> AnalyzeAsync(Guid clientId, byte[] file, string contentType, string? ip, CancellationToken ct);

        // ---- the invoice builder ----
        Task<IReadOnlyList<TemplateAssetDto>> ListAssetsAsync(Guid clientId, CancellationToken ct);
        /// <summary>A picture for the builder (PNG or JPEG, up to 2 MB): a background, a signature, a stamp, a decoration.</summary>
        Task<TemplateAssetDto> UploadAssetAsync(Guid clientId, byte[] bytes, string? name, CancellationToken ct);
        Task<TemplateLogo?> GetAssetAsync(Guid clientId, Guid id, CancellationToken ct);
        Task DeleteAssetAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>The sample invoice the builder shows, as data (the company's own details when it has a device).</summary>
        Task<InvoiceDocumentDto> SampleDocumentAsync(Guid clientId, CancellationToken ct);
        /// <summary>The sample invoice drawn with the settings being edited, as a PDF, a Word document or JSON.</summary>
        Task<RenderedFile> RenderSampleAsync(Guid clientId, RenderTemplateRequest req, CancellationToken ct);
        Task<TemplateExportDto> ExportAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Puts an exported template (its layout, logo and pictures) into this template, replacing what it had.</summary>
        Task<TemplateDto> ImportAsync(Guid clientId, Guid id, TemplateExportDto file, CancellationToken ct);
    }
}
