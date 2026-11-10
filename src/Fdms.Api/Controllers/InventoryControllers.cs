using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>Inventory: items, categories, locations, GRVs, transfers, adjustments and stock takes, stock reports, imports and templates.</summary>
    [ApiController, Authorize, Route("api/v1/inventory")]
    public class InventoryController : ControllerBase
    {
        private readonly IInventoryService _s; private readonly IInventoryImportService _import;
        public InventoryController(IInventoryService s, IInventoryImportService import) { _s = s; _import = import; }
        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;
        private Guid Me => User.ClientId();

        [HttpGet("settings"), HasPermission(Perm.InventoryView)] public async Task<IActionResult> Settings(CancellationToken ct) => Ok(await _s.SettingsAsync(Me, ct));
        [HttpPut("settings"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> SaveSettings(SaveInventorySettingsRequest body, CancellationToken ct) => Ok(await _s.SaveSettingsAsync(Me, body, ct));

        [HttpGet("categories"), HasPermission(Perm.InventoryView)] public async Task<IActionResult> Categories(CancellationToken ct) => Ok(await _s.CategoriesAsync(Me, ct));
        [HttpPut("categories"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> SaveCategory(SaveStockCategoryRequest body, CancellationToken ct) => Ok(await _s.SaveCategoryAsync(Me, body, ct));
        [HttpDelete("categories/{id:guid}"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct) { await _s.DeleteCategoryAsync(Me, id, ct); return NoContent(); }

        [HttpGet("locations"), HasPermission(Perm.InventoryView)] public async Task<IActionResult> Locations(CancellationToken ct) => Ok(await _s.LocationsAsync(Me, ct));
        [HttpPut("locations"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> SaveLocation(SaveStockLocationRequest body, CancellationToken ct) => Ok(await _s.SaveLocationAsync(Me, body, ct));

        [HttpGet("items"), HasPermission(Perm.InventoryView)]
        public async Task<IActionResult> Items([FromQuery] string? q, [FromQuery] Guid? categoryId, [FromQuery] Guid? locationId, [FromQuery] string? status, [FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
            Ok(await _s.ItemsAsync(Me, new StockItemFilter(q, categoryId, locationId, status, includeInactive), ct));
        [HttpPut("items"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> SaveItem(SaveStockItemRequest body, CancellationToken ct) => Ok(await _s.SaveItemAsync(Me, body, ct));

        [HttpGet("documents"), HasPermission(Perm.InventoryView)]
        public async Task<IActionResult> Documents([FromQuery] string? kind, [FromQuery] string? status, [FromQuery] Guid? locationId, [FromQuery] Guid? supplierId, [FromQuery] string? q,
            [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int take = 300, CancellationToken ct = default) =>
            Ok(await _s.DocumentsAsync(Me, new StockDocumentFilter(kind, status, locationId, supplierId, q, from, to, take), ct));
        [HttpGet("documents/{id:guid}"), HasPermission(Perm.InventoryView)] public async Task<IActionResult> Document(Guid id, CancellationToken ct) => Ok(await _s.DocumentAsync(Me, id, ct));
        [HttpPut("documents"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> SaveDocument(SaveStockDocumentRequest body, CancellationToken ct) => Ok(await _s.SaveDocumentAsync(Me, body, ct));
        [HttpDelete("documents/{id:guid}"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> DeleteDocument(Guid id, CancellationToken ct) { await _s.DeleteDocumentAsync(Me, id, ct); return NoContent(); }
        [HttpPost("documents/{id:guid}/post"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> Post(Guid id, CancellationToken ct) => Ok(await _s.PostDocumentAsync(Me, id, ct));
        [HttpPost("documents/{id:guid}/void"), HasPermission(Perm.InventoryManage)] public async Task<IActionResult> Void(Guid id, CancellationToken ct) => Ok(await _s.VoidDocumentAsync(Me, id, ct));
        [HttpGet("documents/{id:guid}/pdf"), HasPermission(Perm.InventoryView)]
        public async Task<IActionResult> Pdf(Guid id, CancellationToken ct) { var (pdf, name) = await _s.DocumentPdfAsync(Me, id, ct); return File(pdf, "application/pdf", name); }

        [HttpGet("reports/on-hand"), HasPermission(Perm.InventoryView)]
        public async Task<IActionResult> OnHand([FromQuery] DateTime? asAt, [FromQuery] Guid? locationId, [FromQuery] Guid? categoryId, CancellationToken ct) => Ok(await _s.OnHandAsync(Me, asAt, locationId, categoryId, ct));
        [HttpGet("reports/card"), HasPermission(Perm.InventoryView)]
        public async Task<IActionResult> Card([FromQuery] Guid productId, [FromQuery] Guid? locationId, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
            Ok(await _s.StockCardAsync(Me, productId, locationId, from ?? new DateTime(Today.Year, 1, 1), to ?? Today, ct));
        [HttpGet("reports/reorder"), HasPermission(Perm.InventoryView)] public async Task<IActionResult> Reorder(CancellationToken ct) => Ok(await _s.ReorderAsync(Me, ct));

        [HttpGet("templates/{kind}"), HasPermission(Perm.InventoryView)]
        public async Task<IActionResult> Template(string kind, [FromQuery] Guid? locationId, CancellationToken ct)
        {
            var (bytes, name) = await _import.TemplateAsync(Me, kind, locationId, ct);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
        }
        [HttpPost("import/items/read"), HasPermission(Perm.InventoryManage), RequestSizeLimit(15_000_000)]
        public async Task<IActionResult> ReadItems(IFormFile file, CancellationToken ct) => Ok(await _import.ReadItemsAsync(Me, file.FileName, await Copy(file, ct), ct));
        [HttpPost("import/items/check"), HasPermission(Perm.InventoryManage), RequestSizeLimit(15_000_000)] public async Task<IActionResult> CheckItems(ItemImportRequest body, CancellationToken ct) => Ok(await _import.CheckItemsAsync(Me, body, ct));
        [HttpPost("import/items"), HasPermission(Perm.InventoryManage), RequestSizeLimit(15_000_000)] public async Task<IActionResult> ImportItems(ItemImportRequest body, CancellationToken ct) => Ok(await _import.ImportItemsAsync(Me, body, ct));
        [HttpPost("import/lines/{kind}"), HasPermission(Perm.InventoryManage), RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> ReadLines(string kind, IFormFile file, CancellationToken ct) => Ok(await _import.ReadLinesAsync(Me, kind, file.FileName, await Copy(file, ct), ct));

        private static async Task<Stream> Copy(IFormFile? file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Choose a file.");
            var ms = new MemoryStream(); await using var s = file.OpenReadStream(); await s.CopyToAsync(ms, ct); ms.Position = 0;
            return ms;
        }
    }
}
