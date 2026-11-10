namespace Fdms.Application.Abstractions
{
    // ---- categories and locations ----
    /// <summary>Level 0: a category; 1: a subcategory. Items: how many items are in it (a category counts its subcategories' too).</summary>
    public sealed record StockCategoryDto(Guid Id, Guid? ParentId, string Name, string? Code, Guid? InventoryAccountId, Guid? CogsAccountId, bool IsActive, int Items, int Level, string Path);
    public sealed record SaveStockCategoryRequest(Guid? Id, Guid? ParentId, string Name, string? Code, Guid? InventoryAccountId, Guid? CogsAccountId, bool IsActive = true);
    public sealed record StockLocationDto(Guid Id, string Code, string Name, Guid? BranchId, string? BranchName, bool IsDefault, bool IsActive, int Items, decimal Value);
    public sealed record SaveStockLocationRequest(Guid? Id, string Code, string Name, Guid? BranchId, bool IsDefault, bool IsActive = true);

    // ---- items ----
    public sealed record StockLevelDto(Guid LocationId, string Location, decimal Quantity);
    public sealed record StockItemDto(Guid Id, string? Code, string Name, string? Barcode, string? Unit, string? HsCode, Guid? CategoryId, string? Category, decimal UnitPrice, string Currency,
        decimal? TaxPercent, bool IsExempt, bool IsActive, bool TrackStock, decimal AverageCost, decimal LastCost, decimal ReorderLevel, decimal ReorderQuantity, decimal OnHand, decimal Value,
        bool BelowReorder, IReadOnlyList<StockLevelDto> Levels);
    public sealed record SaveStockItemRequest(Guid? Id, string? Code, string Name, string? Barcode, string? Unit, string? HsCode, Guid? CategoryId, decimal UnitPrice, string? Currency,
        decimal? TaxPercent, bool IsExempt, bool IsActive, bool TrackStock, decimal ReorderLevel, decimal ReorderQuantity);
    /// <summary>Status: all, stock (tracked only), low (at or below reorder level), out (none on hand).</summary>
    public sealed record StockItemFilter(string? Q, Guid? CategoryId, Guid? LocationId, string? Status, bool IncludeInactive = false, int Take = 2000);

    // ---- documents: GRV, transfer, adjustment / stock take, opening stock ----
    /// <summary>Quantity: received, sent, or the change (+/-); Counted: a stock take's count. UnitCost: before VAT (GRV, opening, adjustments in).</summary>
    public sealed record StockLineInput(Guid ProductId, decimal Quantity, decimal? UnitCost = null, decimal? VatPercent = null, decimal? Counted = null);
    /// <summary>Kind: grv, transfer, adjustment or opening. Mode (adjustment): adjust or count.</summary>
    public sealed record SaveStockDocumentRequest(Guid? Id, string Kind, DateTime Date, Guid LocationId, Guid? ToLocationId, Guid? SupplierId, string? SupplierReference, string? Mode,
        string? Reason, string? Notes, IReadOnlyList<StockLineInput> Lines, Guid? OffsetAccountId = null);
    public sealed record StockLineDto(Guid Id, Guid ProductId, string? Code, string Name, string? Unit, decimal Quantity, decimal? Counted, decimal? Expected, decimal UnitCost, decimal VatPercent, decimal Amount,
        decimal OnHand);
    public sealed record StockDocumentDto(Guid Id, string Kind, string? Number, DateTime Date, string Status, Guid LocationId, string Location, Guid? ToLocationId, string? ToLocation,
        Guid? SupplierId, string? Supplier, string? SupplierReference, string? Mode, string? Reason, string? Notes, decimal Subtotal, decimal Vat, decimal Total, Guid? SourceId,
        Guid? BillId, string? JournalNumber, string? CreatedBy, DateTime CreatedAt, DateTime? PostedAt, IReadOnlyList<StockLineDto> Lines);
    public sealed record StockDocumentFilter(string? Kind, string? Status, Guid? LocationId, Guid? SupplierId, string? Q, DateTime? From, DateTime? To, int Take = 300);

    // ---- reports ----
    public sealed record StockOnHandRow(Guid ProductId, string? Code, string Name, string? Category, string? Unit, decimal Quantity, decimal AverageCost, decimal Value, decimal ReorderLevel);
    /// <summary>LedgerValue: the inventory accounts' balance in the books (all locations only), so the two can be seen to agree.</summary>
    public sealed record StockOnHandDto(DateTime AsAt, Guid? LocationId, string Currency, IReadOnlyList<StockOnHandRow> Rows, decimal TotalQuantity, decimal TotalValue, decimal? LedgerValue, decimal? Difference);
    public sealed record StockCardLine(DateTime Date, Guid DocumentId, string Kind, string? Number, string Location, string? Reference, decimal In, decimal Out, decimal Balance, decimal UnitCost, decimal Value);
    public sealed record StockCardDto(StockItemDto Item, Guid? LocationId, DateTime From, DateTime To, decimal OpeningQuantity, decimal OpeningValue, IReadOnlyList<StockCardLine> Lines, decimal ClosingQuantity, decimal ClosingValue);
    public sealed record ReorderRow(Guid ProductId, string? Code, string Name, string? Category, string? Unit, decimal OnHand, decimal ReorderLevel, decimal Suggested, decimal LastCost, string? LastSupplier);
    /// <summary>The accounts stock posts to by default, and when stock counting started (sales before it do not take stock out).</summary>
    public sealed record InventorySettingsDto(DateTime? StartDate, string Currency, Guid InventoryAccountId, Guid CogsAccountId, Guid AdjustmentAccountId, Guid? DefaultLocationId);
    public sealed record SaveInventorySettingsRequest(DateTime? StartDate);

    public interface IInventoryService
    {
        Task<InventorySettingsDto> SettingsAsync(Guid clientId, CancellationToken ct);
        Task<InventorySettingsDto> SaveSettingsAsync(Guid clientId, SaveInventorySettingsRequest req, CancellationToken ct);
        Task<IReadOnlyList<StockCategoryDto>> CategoriesAsync(Guid clientId, CancellationToken ct);
        Task<StockCategoryDto> SaveCategoryAsync(Guid clientId, SaveStockCategoryRequest req, CancellationToken ct);
        Task DeleteCategoryAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<IReadOnlyList<StockLocationDto>> LocationsAsync(Guid clientId, CancellationToken ct);
        Task<StockLocationDto> SaveLocationAsync(Guid clientId, SaveStockLocationRequest req, CancellationToken ct);
        Task<IReadOnlyList<StockItemDto>> ItemsAsync(Guid clientId, StockItemFilter f, CancellationToken ct);
        Task<StockItemDto> SaveItemAsync(Guid clientId, SaveStockItemRequest req, CancellationToken ct);
        Task<IReadOnlyList<StockDocumentDto>> DocumentsAsync(Guid clientId, StockDocumentFilter f, CancellationToken ct);
        Task<StockDocumentDto> DocumentAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<StockDocumentDto> SaveDocumentAsync(Guid clientId, SaveStockDocumentRequest req, CancellationToken ct);
        Task DeleteDocumentAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Moves the stock and posts to the books (a GRV also raises the supplier's bill).</summary>
        Task<StockDocumentDto> PostDocumentAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>Takes a posted document back out: the stock moves back and its postings (and a GRV's bill) are reversed.</summary>
        Task<StockDocumentDto> VoidDocumentAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<(byte[] Pdf, string FileName)> DocumentPdfAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<StockOnHandDto> OnHandAsync(Guid clientId, DateTime? asAt, Guid? locationId, Guid? categoryId, CancellationToken ct);
        Task<StockCardDto> StockCardAsync(Guid clientId, Guid productId, Guid? locationId, DateTime from, DateTime to, CancellationToken ct);
        Task<IReadOnlyList<ReorderRow>> ReorderAsync(Guid clientId, CancellationToken ct);
    }

    // ---- imports and templates ----
    /// <summary>One item of an items import, with its opening stock (optional).</summary>
    public sealed record ItemImportRow(string? Code, string Name, string? Category, string? Subcategory, string? Unit, string? Barcode, string? HsCode, decimal? SellingPrice, string? Currency,
        decimal? TaxPercent, decimal? Cost, decimal? ReorderLevel, decimal? ReorderQuantity, string? Location, decimal? OpeningQuantity);
    /// <summary>Date: the opening stock's date. UpdateExisting: items already there (same code, or same name) take the file's details.</summary>
    public sealed record ItemImportRequest(IReadOnlyList<ItemImportRow> Rows, DateTime Date, bool UpdateExisting = true, Guid? OffsetAccountId = null);
    public sealed record ItemImportCheckDto(int NewItems, int UpdatedItems, int Unchanged, int NewCategories, int NewSubcategories, int NewLocations, int OpeningLines, decimal OpeningValue,
        IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings, bool CanPost);
    public sealed record ItemImportResultDto(int Created, int Updated, int Categories, int Locations, IReadOnlyList<string> OpeningDocuments);
    public sealed record ItemFileDto(IReadOnlyList<ItemImportRow> Rows, IReadOnlyList<string> Problems);
    /// <summary>Lines read from a file for a document, matched to items by code, barcode or name.</summary>
    public sealed record LineFileRow(Guid ProductId, string? Code, string Name, decimal Quantity, decimal? UnitCost, decimal? VatPercent, decimal? Counted);
    public sealed record LineFileDto(IReadOnlyList<LineFileRow> Lines, IReadOnlyList<string> Problems);

    public interface IInventoryImportService
    {
        /// <summary>items, grv, transfer, adjustment or count (a count sheet of a location's items with what the system holds).</summary>
        Task<(byte[] Bytes, string FileName)> TemplateAsync(Guid clientId, string kind, Guid? locationId, CancellationToken ct);
        Task<ItemFileDto> ReadItemsAsync(Guid clientId, string fileName, Stream file, CancellationToken ct);
        Task<ItemImportCheckDto> CheckItemsAsync(Guid clientId, ItemImportRequest req, CancellationToken ct);
        Task<ItemImportResultDto> ImportItemsAsync(Guid clientId, ItemImportRequest req, CancellationToken ct);
        Task<LineFileDto> ReadLinesAsync(Guid clientId, string kind, string fileName, Stream file, CancellationToken ct);
    }
}
