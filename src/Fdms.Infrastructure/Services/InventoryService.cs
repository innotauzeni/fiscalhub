using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Inventory: categories and subcategories, locations, stock items, and the documents that move stock: goods received vouchers (GRV), transfers, adjustments and stock takes,
    /// and opening stock. Posting one moves the stock and posts its value to the books; sales (fiscal receipts and invoices from the books) take stock out by themselves.
    /// </summary>
    public sealed partial class InventoryService : IInventoryService
    {
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IAccountingService _books; private readonly IProductService _products;
        public InventoryService(FdmsDbContext db, ICallerScope scope, IAccountingService books, IProductService products) { _db = db; _scope = scope; _books = books; _products = products; }

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;
        private static readonly Dictionary<string, string> Prefix = new() { ["grv"] = "GRV-", ["transfer"] = "TRF-", ["adjustment"] = "ADJ-", ["opening"] = "OPN-" };

        private static string? Clean(string? v, int max, string what)
        {
            v = v?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            return v.Length > max ? throw GatewayException.BadRequest("TOO_LONG", $"{what} can be at most {max} characters.") : v;
        }

        /// <summary>Keeps the stock of sales up to date before anything is shown (the books' sync does it).</summary>
        private async Task PrepareAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await _books.SyncSalesAsync(clientId, ct);
        }

        // ---- settings ---------------------------------------------------------------------------------------------------------------

        public async Task<InventorySettingsDto> SettingsAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await _books.EnsureChartAsync(clientId, ct);
            var acc = await StockEngine.AccountsAsync(_db, clientId, ct);
            var s = await _db.AccountingSettings.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            var def = await _db.StockLocations.AsNoTracking().Where(l => l.ClientId == clientId && l.IsActive).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Code).Select(l => (Guid?)l.Id).FirstOrDefaultAsync(ct);
            return new InventorySettingsDto(s?.InventoryStartDate, await StockEngine.CurrencyAsync(_db, clientId, ct), acc.Inventory, acc.Cogs, acc.Adjustment, def);
        }

        public async Task<InventorySettingsDto> SaveSettingsAsync(Guid clientId, SaveInventorySettingsRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var s = await _db.AccountingSettings.FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            if (s == null) _db.AccountingSettings.Add(s = new AccountingSetting { ClientId = clientId });
            if (req.StartDate is { } d && (d.Year < 2000 || d.Date > Today.AddDays(1))) throw GatewayException.BadRequest("DATE_INVALID", "Choose a start date that is not in the future.");
            if (req.StartDate == null && await _db.StockDocuments.AnyAsync(x => x.ClientId == clientId && x.Status == "Posted", ct))
                throw GatewayException.Conflict("INVENTORY_STARTED", "Stock documents are posted: the start date can be moved, not removed.");
            if (s.InventoryStartDate == null && req.StartDate != null) s.StockSyncedAt = null;   // receipts from the start date on are read
            s.InventoryStartDate = req.StartDate?.Date; s.UpdatedAt = DateTime.UtcNow; s.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.stock.start", Detail = req.StartDate?.ToString("yyyy-MM-dd") ?? "not started" });
            await _db.SaveChangesAsync(ct);
            return await SettingsAsync(clientId, ct);
        }

        // ---- categories ---------------------------------------------------------------------------------------------------------------

        public async Task<IReadOnlyList<StockCategoryDto>> CategoriesAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var cats = await _db.StockCategories.AsNoTracking().Where(c => c.ClientId == clientId).ToListAsync(ct);
            var counts = await _db.Products.AsNoTracking().Where(p => p.ClientId == clientId && p.CategoryId != null).GroupBy(p => p.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var list = new List<StockCategoryDto>();
            foreach (var c in cats.Where(c => c.ParentId == null).OrderBy(c => c.Name))
            {
                var subs = cats.Where(s => s.ParentId == c.Id).OrderBy(s => s.Name).ToList();
                list.Add(new StockCategoryDto(c.Id, null, c.Name, c.Code, c.InventoryAccountId, c.CogsAccountId, c.IsActive, counts.GetValueOrDefault(c.Id) + subs.Sum(s => counts.GetValueOrDefault(s.Id)), 0, c.Name));
                list.AddRange(subs.Select(s => new StockCategoryDto(s.Id, c.Id, s.Name, s.Code, s.InventoryAccountId, s.CogsAccountId, s.IsActive, counts.GetValueOrDefault(s.Id), 1, c.Name + " / " + s.Name)));
            }
            return list;
        }

        public async Task<StockCategoryDto> SaveCategoryAsync(Guid clientId, SaveStockCategoryRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var name = (req.Name ?? "").Trim();
            if (name.Length is < 2 or > 100) throw GatewayException.BadRequest("NAME_INVALID", "Name the category (2-100 characters).");
            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            if (req.ParentId is { } pid)
            {
                var parent = await _db.StockCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == pid && c.ClientId == clientId, ct) ?? throw GatewayException.BadRequest("PARENT_INVALID", "That category is not yours.");
                if (parent.ParentId != null) throw GatewayException.BadRequest("PARENT_INVALID", "A subcategory sits under a category, not under another subcategory.");
                if (pid == id) throw GatewayException.BadRequest("PARENT_INVALID", "A category cannot sit under itself.");
                if (id != null && await _db.StockCategories.AnyAsync(c => c.ParentId == id, ct)) throw GatewayException.BadRequest("PARENT_INVALID", "This category has subcategories, so it cannot become one.");
            }
            if (await _db.StockCategories.AnyAsync(c => c.ClientId == clientId && c.ParentId == req.ParentId && c.Name == name && c.Id != id, ct)) throw GatewayException.Conflict("CATEGORY_EXISTS", "There is already a category with that name there.");
            foreach (var (acc, type, what) in new[] { (req.InventoryAccountId, "Asset", "an asset account"), (req.CogsAccountId, "Expense", "an expense account") })
                if (acc is { } a && !await _db.LedgerAccounts.AnyAsync(x => x.Id == a && x.ClientId == clientId && x.Type == type && !x.IsGroup && x.IsActive, ct))
                    throw GatewayException.BadRequest("ACCOUNT_INVALID", $"Choose {what} (not a heading).");
            var c = id == null ? new StockCategory { ClientId = clientId } : await _db.StockCategories.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Category");
            c.Name = name; c.Code = Clean(req.Code, 20, "The code"); c.ParentId = req.ParentId; c.InventoryAccountId = req.InventoryAccountId; c.CogsAccountId = req.CogsAccountId; c.IsActive = req.IsActive;
            if (id == null) _db.StockCategories.Add(c);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.stock.category", Detail = name });
            await _db.SaveChangesAsync(ct);
            return (await CategoriesAsync(clientId, ct)).First(x => x.Id == c.Id);
        }

        public async Task DeleteCategoryAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var c = await _db.StockCategories.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Category");
            if (await _db.StockCategories.AnyAsync(x => x.ParentId == id, ct)) throw GatewayException.Conflict("HAS_SUBCATEGORIES", "Delete or move its subcategories first.");
            if (await _db.Products.AnyAsync(p => p.CategoryId == id, ct)) throw GatewayException.Conflict("HAS_ITEMS", "Items are in it: move them first, or switch the category off.");
            _db.StockCategories.Remove(c);
            await _db.SaveChangesAsync(ct);
        }

        // ---- locations ---------------------------------------------------------------------------------------------------------------

        public async Task<IReadOnlyList<StockLocationDto>> LocationsAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var locs = await _db.StockLocations.AsNoTracking().Where(l => l.ClientId == clientId).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Code).ToListAsync(ct);
            var branches = await _db.Branches.AsNoTracking().Where(b => b.ClientId == clientId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
            var stats = await (from m in _db.StockMovements.AsNoTracking() where m.ClientId == clientId group m by new { m.LocationId, m.ProductId } into g select new { g.Key.LocationId, Q = g.Sum(x => x.Quantity), V = g.Sum(x => x.Value) }).ToListAsync(ct);
            return locs.Select(l => new StockLocationDto(l.Id, l.Code, l.Name, l.BranchId, l.BranchId is { } b ? branches.GetValueOrDefault(b) : null, l.IsDefault, l.IsActive,
                stats.Count(s => s.LocationId == l.Id && s.Q != 0), stats.Where(s => s.LocationId == l.Id).Sum(s => s.V))).ToList();
        }

        public async Task<StockLocationDto> SaveLocationAsync(Guid clientId, SaveStockLocationRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var code = (req.Code ?? "").Trim().ToUpperInvariant(); var name = (req.Name ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9_-]{0,19}$")) throw GatewayException.BadRequest("CODE_INVALID", "Give the location a short code (letters and digits, up to 20).");
            if (name.Length is < 2 or > 100) throw GatewayException.BadRequest("NAME_INVALID", "Name the location (2-100 characters).");
            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            if (await _db.StockLocations.AnyAsync(l => l.ClientId == clientId && l.Code == code && l.Id != id, ct)) throw GatewayException.Conflict("LOCATION_EXISTS", "Another location uses that code.");
            if (req.BranchId is { } b && !await _db.Branches.AnyAsync(x => x.Id == b && x.ClientId == clientId, ct)) throw GatewayException.BadRequest("BRANCH_INVALID", "That branch is not yours.");
            var l = id == null ? new StockLocation { ClientId = clientId } : await _db.StockLocations.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Location");
            if (!req.IsActive && l.IsActive && id != null && await _db.StockLevels.AnyAsync(x => x.LocationId == l.Id && x.Quantity != 0, ct))
                throw GatewayException.Conflict("HAS_STOCK", "Stock is still there: transfer it out before switching the location off.");
            l.Code = code; l.Name = name; l.BranchId = req.BranchId; l.IsActive = req.IsActive;
            var first = !await _db.StockLocations.AnyAsync(x => x.ClientId == clientId && x.Id != l.Id, ct);
            l.IsDefault = (req.IsDefault || first) && req.IsActive;
            if (l.IsDefault) foreach (var o in await _db.StockLocations.Where(x => x.ClientId == clientId && x.Id != l.Id && x.IsDefault).ToListAsync(ct)) o.IsDefault = false;
            if (id == null) _db.StockLocations.Add(l);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.stock.location", Detail = $"{code} {name}" });
            await _db.SaveChangesAsync(ct);
            return (await LocationsAsync(clientId, ct)).First(x => x.Id == l.Id);
        }

        // ---- items ---------------------------------------------------------------------------------------------------------------------

        public async Task<IReadOnlyList<StockItemDto>> ItemsAsync(Guid clientId, StockItemFilter f, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var q = _db.Products.AsNoTracking().Where(p => p.ClientId == clientId);
            if (!f.IncludeInactive) q = q.Where(p => p.IsActive);
            if (f.CategoryId is { } cid) q = q.Where(p => p.CategoryId == cid || _db.StockCategories.Any(c => c.Id == p.CategoryId && c.ParentId == cid));
            if (!string.IsNullOrWhiteSpace(f.Q)) { var s = f.Q.Trim(); q = q.Where(p => p.Name.Contains(s) || (p.Code != null && p.Code.Contains(s)) || (p.Barcode != null && p.Barcode == s)); }
            if (f.Status is "stock" or "low" or "out") q = q.Where(p => p.TrackStock);
            var items = await q.OrderBy(p => p.Name).Take(Math.Clamp(f.Take, 1, 5000)).ToListAsync(ct);
            var list = await MapItemsAsync(clientId, items, ct);
            if (f.LocationId is { } lid) list = list.Where(i => i.Levels.Any(l => l.LocationId == lid && l.Quantity != 0)).ToList();
            return (f.Status ?? "").ToLowerInvariant() switch { "low" => list.Where(i => i.BelowReorder).ToList(), "out" => list.Where(i => i.OnHand <= 0).ToList(), _ => list };
        }

        private async Task<List<StockItemDto>> MapItemsAsync(Guid clientId, List<Product> items, CancellationToken ct)
        {
            var ids = items.Select(p => p.Id).ToList();
            var levels = await _db.StockLevels.AsNoTracking().Where(l => l.ClientId == clientId && ids.Contains(l.ProductId)).ToListAsync(ct);
            // the value is what the movements put on the books (quantity x a rounded average would drift by cents)
            var values = await _db.StockMovements.AsNoTracking().Where(m => m.ClientId == clientId && ids.Contains(m.ProductId)).GroupBy(m => m.ProductId).Select(g => new { g.Key, V = g.Sum(x => x.Value) }).ToDictionaryAsync(x => x.Key, x => x.V, ct);
            var locs = await _db.StockLocations.AsNoTracking().Where(l => l.ClientId == clientId).ToDictionaryAsync(l => l.Id, l => l.Code, ct);
            var paths = (await CategoriesAsync(clientId, ct)).ToDictionary(c => c.Id, c => c.Path);
            return items.Select(p =>
            {
                var mine = levels.Where(l => l.ProductId == p.Id && l.Quantity != 0).Select(l => new StockLevelDto(l.LocationId, locs.GetValueOrDefault(l.LocationId, "?"), l.Quantity)).OrderBy(l => l.Location).ToList();
                var onHand = mine.Sum(l => l.Quantity);
                return new StockItemDto(p.Id, p.Code, p.Name, p.Barcode, p.Unit, p.HsCode, p.CategoryId, p.CategoryId is { } c ? paths.GetValueOrDefault(c) : null, p.UnitPrice, p.Currency, p.TaxPercent, p.IsExempt,
                    p.IsActive, p.TrackStock, p.AverageCost, p.LastCost, p.ReorderLevel, p.ReorderQuantity, onHand, values.GetValueOrDefault(p.Id), p.TrackStock && p.ReorderLevel > 0 && onHand <= p.ReorderLevel, mine);
            }).ToList();
        }

        public async Task<StockItemDto> SaveItemAsync(Guid clientId, SaveStockItemRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            if (req.CategoryId is { } c && !await _db.StockCategories.AnyAsync(x => x.Id == c && x.ClientId == clientId, ct)) throw GatewayException.BadRequest("CATEGORY_INVALID", "That category is not yours.");
            if (req.ReorderLevel < 0 || req.ReorderQuantity < 0) throw GatewayException.BadRequest("REORDER_INVALID", "Reorder figures are zero or more.");
            var barcode = Clean(req.Barcode, 60, "The barcode");
            if (barcode != null && await _db.Products.AnyAsync(p => p.ClientId == clientId && p.Barcode == barcode && p.Id != req.Id, ct)) throw GatewayException.Conflict("BARCODE_EXISTS", "Another item has that barcode.");
            if (req.Id is { } id && !req.TrackStock && await _db.StockLevels.AnyAsync(l => l.ProductId == id && l.Quantity != 0, ct))
                throw GatewayException.Conflict("HAS_STOCK", "This item has stock on hand: adjust it to nothing before it stops being counted.");
            var saved = await _products.SaveAsync(clientId, new SaveProductRequest(req.Id, req.Code, req.Name, req.HsCode, req.Unit, req.UnitPrice, req.Currency, req.TaxPercent, req.IsExempt, req.IsActive), ct);
            var p = await _db.Products.FirstAsync(x => x.Id == saved.Id, ct);
            p.CategoryId = req.CategoryId; p.TrackStock = req.TrackStock; p.ReorderLevel = req.ReorderLevel; p.ReorderQuantity = req.ReorderQuantity; p.Barcode = barcode; p.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return (await MapItemsAsync(clientId, new List<Product> { p }, ct))[0];
        }

        // ---- documents -----------------------------------------------------------------------------------------------------------------

        private async Task<List<StockDocumentDto>> MapDocsAsync(Guid clientId, List<StockDocument> docs, CancellationToken ct)
        {
            if (docs.Count == 0) return new();
            var locs = await _db.StockLocations.AsNoTracking().Where(l => l.ClientId == clientId).ToDictionaryAsync(l => l.Id, l => l.Code + " · " + l.Name, ct);
            var sups = await _db.Suppliers.AsNoTracking().Where(s => s.ClientId == clientId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            var pids = docs.SelectMany(d => d.Lines.Select(l => l.ProductId)).Distinct().ToList();
            var prods = await _db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var levels = await _db.StockLevels.AsNoTracking().Where(l => pids.Contains(l.ProductId)).ToListAsync(ct);
            var jids = docs.Where(d => d.JournalEntryId != null).Select(d => d.JournalEntryId!.Value).ToList();
            var jv = await _db.JournalEntries.AsNoTracking().Where(e => jids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
            return docs.Select(d => new StockDocumentDto(d.Id, d.Kind, d.Number, d.Date, d.Status, d.LocationId, locs.GetValueOrDefault(d.LocationId, ""), d.ToLocationId,
                d.ToLocationId is { } t ? locs.GetValueOrDefault(t) : null, d.SupplierId, d.SupplierId is { } s ? sups.GetValueOrDefault(s) : null, d.SupplierReference, d.Mode, d.Reason, d.Notes,
                d.Subtotal, d.Vat, d.Total, d.SourceId, d.BillId, d.JournalEntryId is { } j ? jv.GetValueOrDefault(j) : null, d.CreatedBy, d.CreatedAt, d.PostedAt,
                d.Lines.OrderBy(l => l.Sort).Select(l =>
                {
                    var p = prods.GetValueOrDefault(l.ProductId);
                    return new StockLineDto(l.Id, l.ProductId, p?.Code, p?.Name ?? "(deleted item)", p?.Unit, l.Quantity, l.Counted, l.Expected, l.UnitCost, l.VatPercent, l.Amount,
                        levels.Where(x => x.ProductId == l.ProductId && x.LocationId == d.LocationId).Sum(x => x.Quantity));
                }).ToList())).ToList();
        }

        public async Task<IReadOnlyList<StockDocumentDto>> DocumentsAsync(Guid clientId, StockDocumentFilter f, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var q = _db.StockDocuments.AsNoTracking().Include(d => d.Lines).Where(d => d.ClientId == clientId);
            if (!string.IsNullOrWhiteSpace(f.Kind)) { var kinds = f.Kind.Split(','); q = q.Where(d => kinds.Contains(d.Kind)); }
            if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(d => d.Status == f.Status);
            if (f.LocationId is { } l) q = q.Where(d => d.LocationId == l || d.ToLocationId == l);
            if (f.SupplierId is { } s) q = q.Where(d => d.SupplierId == s);
            if (f.From is { } from) q = q.Where(d => d.Date >= from.Date);
            if (f.To is { } to) q = q.Where(d => d.Date <= to.Date);
            if (!string.IsNullOrWhiteSpace(f.Q)) { var t = f.Q.Trim(); q = q.Where(d => (d.Number != null && d.Number.Contains(t)) || (d.SupplierReference != null && d.SupplierReference.Contains(t)) || (d.Reason != null && d.Reason.Contains(t))); }
            var docs = await q.OrderByDescending(d => d.Date).ThenByDescending(d => d.CreatedAt).Take(Math.Clamp(f.Take, 1, 1000)).ToListAsync(ct);
            return await MapDocsAsync(clientId, docs, ct);
        }

        public async Task<StockDocumentDto> DocumentAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var d = await _db.StockDocuments.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");
            return (await MapDocsAsync(clientId, new List<StockDocument> { d }, ct))[0];
        }

        public async Task<StockDocumentDto> SaveDocumentAsync(Guid clientId, SaveStockDocumentRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var kind = req.Kind is "grv" or "transfer" or "adjustment" or "opening" ? req.Kind : throw GatewayException.BadRequest("KIND_INVALID", "Choose a GRV, a transfer, an adjustment or opening stock.");
            if (req.Date.Year < 2000 || req.Date.Date > Today.AddDays(1)) throw GatewayException.BadRequest("DATE_INVALID", "That date is not valid.");
            var locs = await _db.StockLocations.AsNoTracking().Where(l => l.ClientId == clientId && l.IsActive).Select(l => l.Id).ToListAsync(ct);
            if (!locs.Contains(req.LocationId)) throw GatewayException.BadRequest("LOCATION_INVALID", "Choose a location.");
            if (kind == "transfer" && (req.ToLocationId is not { } to || !locs.Contains(to) || to == req.LocationId)) throw GatewayException.BadRequest("LOCATION_INVALID", "Choose where the stock goes (another location).");
            if (kind == "grv" && (req.SupplierId is not { } sid || !await _db.Suppliers.AnyAsync(s => s.Id == sid && s.ClientId == clientId && s.IsActive, ct))) throw GatewayException.BadRequest("SUPPLIER_INVALID", "Choose the supplier.");
            var mode = kind == "adjustment" ? (req.Mode is "count" ? "count" : "adjust") : null;
            if (kind == "opening" && req.OffsetAccountId is { } off && !await _db.LedgerAccounts.AnyAsync(a => a.Id == off && a.ClientId == clientId && !a.IsGroup && a.IsActive, ct))
                throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose the account opening stock is posted against.");
            var lines = (req.Lines ?? Array.Empty<StockLineInput>()).ToList();
            if (lines.Count == 0) throw GatewayException.BadRequest("LINES_REQUIRED", "Add at least one item.");
            if (lines.Count > 2000) throw GatewayException.BadRequest("LINES_TOO_MANY", "At most 2000 lines.");
            var pids = lines.Select(l => l.ProductId).Distinct().ToList();
            var prods = await _db.Products.AsNoTracking().Where(p => p.ClientId == clientId && pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var built = new List<StockDocumentLine>();
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i]; var n = i + 1;
                if (!prods.TryGetValue(l.ProductId, out var p)) throw GatewayException.BadRequest("ITEM_INVALID", $"Line {n}: choose an item.");
                if (!p.TrackStock) throw GatewayException.BadRequest("ITEM_NOT_STOCK", $"Line {n}: {p.Name} is not counted in stock. Tick \"Track stock\" on the item first.");
                if (mode == "count")
                {
                    if (l.Counted is not { } counted || counted < 0) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n} ({p.Name}): enter what was counted (zero or more).");
                }
                else if (l.Quantity == 0 || (kind != "adjustment" && l.Quantity < 0)) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n} ({p.Name}): enter a quantity{(kind == "adjustment" ? " (+ to add, - to take off)" : " above zero")}.");
                if (Math.Abs(l.Quantity) > 1_000_000_000m) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n}: that quantity is too large.");
                var cost = l.UnitCost ?? 0;
                if (kind is "grv" or "opening" && (l.UnitCost is not { } uc || uc < 0)) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n} ({p.Name}): enter the cost per unit before VAT.");
                if (cost < 0) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n}: a cost cannot be negative.");
                var vat = kind == "grv" ? l.VatPercent ?? 0 : 0;
                if (vat is < 0 or > 100) throw GatewayException.BadRequest("LINE_INVALID", $"Line {n}: VAT is between 0 and 100%.");
                built.Add(new StockDocumentLine { ClientId = clientId, Sort = n, ProductId = p.Id, Quantity = mode == "count" ? 0 : l.Quantity, Counted = mode == "count" ? l.Counted : null,
                    UnitCost = Math.Round(cost, 4), VatPercent = vat, Amount = Math.Round(Math.Abs(l.Quantity) * cost, 2, MidpointRounding.AwayFromZero) });
            }
            if (mode == "count" && built.GroupBy(b => b.ProductId).Any(g => g.Count() > 1)) throw GatewayException.BadRequest("LINE_INVALID", "Count each item once.");

            StockDocument d;
            if (req.Id is { } id && id != Guid.Empty)
            {
                d = await _db.StockDocuments.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");
                if (d.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", "Only a draft can be changed. Void a posted document instead.");
                if (d.Kind != kind) throw GatewayException.BadRequest("KIND_INVALID", "A draft keeps its kind.");
                _db.StockDocumentLines.RemoveRange(d.Lines); d.Lines = new();
            }
            else { d = new StockDocument { ClientId = clientId, Kind = kind, CreatedBy = _scope.UserName }; _db.StockDocuments.Add(d); }
            d.Date = req.Date.Date; d.LocationId = req.LocationId; d.ToLocationId = kind == "transfer" ? req.ToLocationId : null; d.SupplierId = kind == "grv" ? req.SupplierId : null;
            d.SupplierReference = Clean(req.SupplierReference, 50, "The supplier's reference"); d.Mode = mode; d.Reason = Clean(req.Reason, 200, "The reason"); d.Notes = Clean(req.Notes, 500, "The notes");
            d.OffsetAccountId = kind == "opening" ? req.OffsetAccountId : null;
            foreach (var l in built) { l.DocumentId = d.Id; d.Lines.Add(l); _db.StockDocumentLines.Add(l); }
            Totals(d);
            await _db.SaveChangesAsync(ct);
            return await DocumentAsync(clientId, d.Id, ct);
        }

        private static void Totals(StockDocument d)
        {
            d.Subtotal = d.Lines.Sum(l => l.Amount);
            d.Vat = d.Kind == "grv" ? d.Lines.Sum(l => Math.Round(l.Amount * l.VatPercent / 100m, 2, MidpointRounding.AwayFromZero)) : 0;
            d.Total = d.Subtotal + d.Vat;
        }

        public async Task DeleteDocumentAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var d = await _db.StockDocuments.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");
            if (d.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", "A posted document is never deleted: void it instead.");
            _db.StockDocuments.Remove(d);
            await _db.SaveChangesAsync(ct);
        }

        // ---- posting and voiding ---------------------------------------------------------------------------------------------------------

        private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
        {
            T result = default!;
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
                result = await work();
                if (tx != null) await tx.CommitAsync(ct);
            });
            return result;
        }

        public async Task<StockDocumentDto> PostDocumentAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            await InTransactionAsync(async () =>
            {
                var d = await _db.StockDocuments.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");
                if (d.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", "That document is already posted.");
                if (!await _db.StockLocations.AnyAsync(l => l.Id == d.LocationId && l.IsActive, ct) || (d.ToLocationId is { } t && !await _db.StockLocations.AnyAsync(l => l.Id == t && l.IsActive, ct)))
                    throw GatewayException.Conflict("LOCATION_INACTIVE", "One of its locations is switched off.");
                var acc = await StockEngine.AccountsAsync(_db, clientId, ct);
                var cur = await StockEngine.CurrencyAsync(_db, clientId, ct);
                var pids = d.Lines.Select(l => l.ProductId).Distinct().ToList();
                var prods = await _db.Products.Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
                var seq = (await _db.StockDocuments.Where(x => x.ClientId == clientId && x.Kind == d.Kind).MaxAsync(x => (int?)x.Seq, ct) ?? 0) + 1;
                d.Seq = seq; d.Number = Prefix[d.Kind] + seq.ToString("D6");
                var parts = new List<(Guid Account, decimal Value)>();

                foreach (var l in d.Lines.OrderBy(x => x.Sort))
                {
                    var p = prods[l.ProductId];
                    switch (d.Kind)
                    {
                        case "grv":
                        case "opening":
                            {
                                var (_, v) = await StockEngine.MoveAsync(_db, d, p, d.LocationId, l.Quantity, l.UnitCost, d.Date, ct);
                                if (d.Kind == "grv") p.LastCost = l.UnitCost;
                                parts.Add((acc.InventoryFor(p), v));
                                break;
                            }
                        case "transfer":
                            {
                                var have = await StockEngine.OnHandAsync(_db, p.Id, d.LocationId, ct);
                                if (have < l.Quantity) throw GatewayException.Conflict("NOT_ENOUGH_STOCK", $"Only {have:0.####} of {p.Name} is at the location it is sent from.");
                                var (unit, _) = await StockEngine.MoveAsync(_db, d, p, d.LocationId, -l.Quantity, null, d.Date, ct);
                                await StockEngine.MoveAsync(_db, d, p, d.ToLocationId!.Value, l.Quantity, unit, d.Date, ct);
                                l.UnitCost = Math.Round(unit, 4); l.Amount = Math.Round(l.Quantity * unit, 2, MidpointRounding.AwayFromZero);
                                break;
                            }
                        case "adjustment":
                            {
                                var have = await StockEngine.OnHandAsync(_db, p.Id, d.LocationId, ct);
                                if (d.Mode == "count") { l.Expected = have; l.Quantity = (l.Counted ?? 0) - have; }
                                if (l.Quantity == 0) { l.Amount = 0; l.UnitCost = p.AverageCost; break; }
                                if (have + l.Quantity < 0) throw GatewayException.Conflict("NOT_ENOUGH_STOCK", $"Only {have:0.####} of {p.Name} is there to take off.");
                                decimal? cost = l.Quantity > 0 && l.UnitCost > 0 ? l.UnitCost : null;
                                var (unit, v) = await StockEngine.MoveAsync(_db, d, p, d.LocationId, l.Quantity, cost, d.Date, ct);
                                l.UnitCost = Math.Round(unit, 4); l.Amount = v;
                                parts.Add((acc.InventoryFor(p), v));
                                break;
                            }
                    }
                }
                Totals(d);
                if (d.Kind == "adjustment") { d.Subtotal = d.Lines.Sum(l => l.Amount); d.Total = d.Subtotal; }

                // the books: what the stock is worth, against the supplier, the adjustments account or opening equity
                string label = d.Kind switch { "grv" => "GRV", "opening" => "Opening stock", "adjustment" => d.Mode == "count" ? "Stock take" : "Stock adjustment", _ => "Transfer" };
                if (d.Kind == "grv")
                {
                    var sup = await _db.Suppliers.FirstAsync(s => s.Id == d.SupplierId, ct);
                    var payable = await ChartCodes.ByRoleAsync(_db, clientId, "payable", "2010", ct) ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The accounts payable account is missing.");
                    var number = d.SupplierReference ?? d.Number!;
                    if (await _db.Bills.AnyAsync(b => b.ClientId == clientId && b.SupplierId == sup.Id && b.Number == number && !b.IsVoid, ct))
                        throw GatewayException.Conflict("BILL_EXISTS", $"Bill {number} from {sup.Name} is already in your bills. Give the GRV the supplier's own invoice number, or leave it empty.");
                    var lines = StockEngine.Lines(parts, payable.Id, null);
                    lines.RemoveAll(x => x.AccountId == payable.Id);
                    if (d.Vat > 0)
                    {
                        var vatIn = await ChartCodes.ByRoleAsync(_db, clientId, "vat_input", "1310", ct) ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The VAT input account is missing.");
                        lines.Add(new JournalLineInput(vatIn.Id, d.Vat, 0, null));
                    }
                    lines.Add(new JournalLineInput(payable.Id, 0, d.Total, sup.Name));
                    if (d.Total > 0)
                    {
                        var entry = await _books.PostLinesAsync(clientId, d.Date, cur, $"GRV {d.Number} - {sup.Name}", number, "grv", d.Id, lines, ct);
                        d.JournalEntryId = entry.Id;
                        var bill = new Bill { ClientId = clientId, SupplierId = sup.Id, Number = number, Date = d.Date, DueDate = d.Date.AddDays(sup.TermsDays), Currency = cur, Memo = $"Goods received on {d.Number}",
                            Subtotal = d.Subtotal, Vat = d.Vat, Total = d.Total, ExpenseAccountId = acc.Inventory, JournalEntryId = entry.Id, CreatedBy = _scope.UserName };
                        _db.Bills.Add(bill); d.BillId = bill.Id;
                    }
                }
                else if (d.Kind is "adjustment" or "opening")
                {
                    Guid other = d.Kind == "adjustment" ? acc.Adjustment : d.OffsetAccountId ?? (await OpeningEquityAsync(clientId, ct)).Id;
                    var lines = StockEngine.Lines(parts, other, null);
                    if (lines.Count >= 2)
                        d.JournalEntryId = (await _books.PostLinesAsync(clientId, d.Date, cur, $"{label} {d.Number}{(d.Reason != null ? " - " + d.Reason : "")}", d.Number, d.Kind == "opening" ? "stock_opening" : "stock_adj", d.Id, lines, ct)).Id;
                }
                d.Status = "Posted"; d.PostedAt = DateTime.UtcNow;
                await StockEngine.StartAsync(_db, clientId, d.Date, ct);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.stock." + d.Kind, Detail = $"{label} {d.Number}: {d.Lines.Count} line(s), {d.Total:0.00} {cur}" });
                await _db.SaveChangesAsync(ct);
                return true;
            }, ct);
            return await DocumentAsync(clientId, id, ct);
        }

        private async Task<LedgerAccount> OpeningEquityAsync(Guid clientId, CancellationToken ct)
        {
            var acc = await _db.LedgerAccounts.FirstOrDefaultAsync(a => a.ClientId == clientId && a.Role == "opening_balance", ct);
            if (acc != null) return acc;
            var codes = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).Select(a => a.Code).ToListAsync(ct);
            acc = new LedgerAccount { ClientId = clientId, Code = ChartCodes.Next("Equity", null, codes, await ChartCodes.RulesAsync(_db, clientId, ct)).Code ?? "3090", Name = "Opening balance equity", Type = "Equity", Role = "opening_balance", SubType = "equity", IsSystem = true };
            _db.LedgerAccounts.Add(acc); await _db.SaveChangesAsync(ct);
            return acc;
        }

        public async Task<StockDocumentDto> VoidDocumentAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await InTransactionAsync(async () =>
            {
                var d = await _db.StockDocuments.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");
                if (d.Status != "Posted") throw GatewayException.Conflict("NOT_POSTED", "Only a posted document can be voided (a draft is deleted).");
                if (d.Kind is "sale" or "return") throw GatewayException.Conflict("FROM_A_SALE", "This came from a sale: cancel or credit the invoice instead.");
                if (d.BillId is { } bid)
                {
                    if (await _db.BillPayments.AnyAsync(p => p.BillId == bid && !p.IsVoid, ct)) throw GatewayException.Conflict("HAS_PAYMENTS", "The supplier's bill has payments: void them under Bills first.");
                    var bill = await _db.Bills.FirstAsync(b => b.Id == bid, ct); bill.IsVoid = true;
                }
                await ReverseStockAsync(d, ct);
                if (d.JournalEntryId is { } eid) await _books.ReverseJournalAsync(clientId, eid, ct);
                d.Status = "Void"; d.VoidedAt = DateTime.UtcNow;
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.stock.void", Detail = d.Number });
                await _db.SaveChangesAsync(ct);
                return true;
            }, ct);
            return await DocumentAsync(clientId, id, ct);
        }

        /// <summary>Every movement of a document undone at the cost it moved at; refused where the stock has gone since.</summary>
        internal async Task ReverseStockAsync(StockDocument d, CancellationToken ct)
        {
            var moves = await _db.StockMovements.Where(m => m.DocumentId == d.Id).OrderByDescending(m => m.CreatedAt).ToListAsync(ct);
            var pids = moves.Select(m => m.ProductId).Distinct().ToList();
            var prods = await _db.Products.Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            foreach (var m in moves.Where(m => m.Quantity > 0))
            {
                var have = await StockEngine.OnHandAsync(_db, m.ProductId, m.LocationId, ct);
                if (have < m.Quantity) throw GatewayException.Conflict("STOCK_USED", $"{prods[m.ProductId].Name}: only {have:0.####} of the {m.Quantity:0.####} it brought in is still there.");
            }
            foreach (var m in moves) await StockEngine.MoveAsync(_db, d, prods[m.ProductId], m.LocationId, -m.Quantity, m.UnitCost, DateTime.UtcNow.AddHours(2).Date < d.Date ? d.Date : Today, ct);
        }
    }
}
