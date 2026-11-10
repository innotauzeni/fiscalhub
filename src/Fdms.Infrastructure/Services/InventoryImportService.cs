using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Excel templates for every inventory import, and the imports themselves: stock items with their categories, subcategories and opening stock per location, and the lines
    /// of a GRV, transfer, adjustment or stock take read from a file.
    /// </summary>
    public sealed class InventoryImportService : IInventoryImportService
    {
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IInventoryService _inventory;
        public InventoryImportService(FdmsDbContext db, ICallerScope scope, IInventoryService inventory) { _db = db; _scope = scope; _inventory = inventory; }

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        // ---- templates -------------------------------------------------------------------------------------------------------------------

        private static readonly Dictionary<string, (string Sheet, string[] Headers, object[][] Examples, string Help)> Templates = new()
        {
            ["items"] = ("Stock items",
                new[] { "Code", "Name", "Category", "Subcategory", "Unit", "Barcode", "HS code", "Selling price", "Currency", "Tax %", "Cost", "Reorder level", "Reorder quantity", "Location", "Opening quantity" },
                new[] { new object[] { "CEM-50", "Cement 50kg", "Building", "Cement", "bag", "6001234500017", "25232900", 12.50, "USD", 15, 9.20, 40, 200, "MAIN", 320 },
                        new object[] { "PNT-20", "Paint white 20L", "Building", "Paint", "tin", "", "32091000", 48.00, "USD", 15, 31.75, 10, 30, "MAIN", 24 },
                        new object[] { "NL-75", "Nails 75mm (1kg)", "Hardware", "Fasteners", "kg", "", "73170000", 3.80, "USD", 15, 2.10, 50, 100, "BYO", 140 } },
                "One row per item. Items already in your list (same code, or same name) are updated. Category and Subcategory are created if they do not exist. Cost is the cost per unit before VAT. Opening quantity (with Location, a location code or name) brings in what is on the shelf today; leave it empty for items you only want listed."),
            ["grv"] = ("GRV lines", new[] { "Code", "Name", "Quantity", "Unit cost", "VAT %" },
                new[] { new object[] { "CEM-50", "Cement 50kg", 100, 9.20, 15 }, new object[] { "PNT-20", "Paint white 20L", 12, 31.75, 15 } },
                "The items on the supplier's delivery: code (or barcode, or the exact name), quantity received, cost per unit before VAT and the VAT rate."),
            ["transfer"] = ("Transfer lines", new[] { "Code", "Name", "Quantity" },
                new[] { new object[] { "CEM-50", "Cement 50kg", 40 }, new object[] { "NL-75", "Nails 75mm (1kg)", 25 } },
                "The items to send: code (or barcode, or the exact name) and quantity."),
            ["adjustment"] = ("Adjustment lines", new[] { "Code", "Name", "Quantity", "Unit cost" },
                new[] { new object[] { "PNT-20", "Paint white 20L", -2, "" }, new object[] { "CEM-50", "Cement 50kg", 5, 9.20 } },
                "Quantity: + to add, - to take off (damaged, expired, stolen). Unit cost only for stock added; empty = the average cost."),
        };

        public async Task<(byte[] Bytes, string FileName)> TemplateAsync(Guid clientId, string kind, Guid? locationId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            using var wb = new XLWorkbook();
            if (kind == "count")
            {
                // a count sheet: every item counted at the location, with what the system holds
                var loc = locationId is { } l ? await _db.StockLocations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == l && x.ClientId == clientId, ct) : null;
                if (loc == null) throw GatewayException.BadRequest("LOCATION_INVALID", "Choose the location to count.");
                var items = await _inventory.ItemsAsync(clientId, new StockItemFilter(null, null, null, "stock"), ct);
                var ws = wb.AddWorksheet("Count " + loc.Code);
                var heads = new[] { "Code", "Name", "Category", "Unit", "System quantity", "Counted" };
                for (var i = 0; i < heads.Length; i++) { ws.Cell(1, i + 1).Value = heads[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2F3F1"); }
                var row = 2;
                foreach (var it in items.OrderBy(x => x.Category).ThenBy(x => x.Name))
                {
                    ws.Cell(row, 1).Value = it.Code ?? ""; ws.Cell(row, 2).Value = it.Name; ws.Cell(row, 3).Value = it.Category ?? ""; ws.Cell(row, 4).Value = it.Unit ?? "";
                    ws.Cell(row, 5).Value = it.Levels.Where(x => x.LocationId == loc.Id).Sum(x => x.Quantity); row++;
                }
                ws.Column(6).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF8E1");
                ws.Columns().AdjustToContents();
                using var m1 = new MemoryStream(); wb.SaveAs(m1);
                return (m1.ToArray(), $"count-sheet-{loc.Code}-{Today:yyyy-MM-dd}.xlsx");
            }
            if (!Templates.TryGetValue(kind, out var t)) throw GatewayException.BadRequest("KIND_INVALID", "Choose items, grv, transfer, adjustment or count.");
            var sheet = wb.AddWorksheet(t.Sheet);
            for (var i = 0; i < t.Headers.Length; i++) { sheet.Cell(1, i + 1).Value = t.Headers[i]; sheet.Cell(1, i + 1).Style.Font.Bold = true; sheet.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2F3F1"); }
            for (var r = 0; r < t.Examples.Length; r++)
                for (var c = 0; c < t.Examples[r].Length; c++)
                {
                    var v = t.Examples[r][c]; var cell = sheet.Cell(r + 2, c + 1);
                    if (v is double d) cell.Value = d; else if (v is int n) cell.Value = n; else cell.Value = v?.ToString() ?? "";
                }
            sheet.Columns().AdjustToContents();
            var help = wb.AddWorksheet("How to fill it in");
            help.Cell(1, 1).Value = t.Help; help.Cell(1, 1).Style.Alignment.WrapText = true; help.Column(1).Width = 110;
            help.Cell(3, 1).Value = "Replace the example rows with your own and keep the first row (the column names). Save as .xlsx or .csv.";
            using var ms = new MemoryStream(); wb.SaveAs(ms);
            return (ms.ToArray(), $"fiscalhub-{kind}-template.xlsx");
        }

        // ---- items --------------------------------------------------------------------------------------------------------------------------

        public async Task<ItemFileDto> ReadItemsAsync(Guid clientId, string fileName, Stream file, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await Task.CompletedTask;
            var table = ConversionService.ReadTable(fileName ?? "", file);
            if (table.Count == 0) throw GatewayException.BadRequest("FILE_EMPTY", "The file is empty.");
            if (table.Count > 10001) throw GatewayException.BadRequest("FILE_TOO_LARGE", "At most 10,000 items at a time.");
            var (map, header) = ConversionService.Columns(table[0], new()
            {
                ["code"] = new[] { "code", "itemcode", "sku", "productcode", "stockcode" },
                ["name"] = new[] { "name", "itemname", "description", "product", "item", "productname" },
                ["category"] = new[] { "category", "group", "department", "maincategory" },
                ["sub"] = new[] { "subcategory", "subgroup", "subcat" },
                ["unit"] = new[] { "unit", "uom", "unitofmeasure" },
                ["barcode"] = new[] { "barcode", "ean", "upc" },
                ["hs"] = new[] { "hscode", "hs", "tariffcode" },
                ["price"] = new[] { "sellingprice", "price", "unitprice", "retailprice", "sellprice" },
                ["currency"] = new[] { "currency", "cur" },
                ["tax"] = new[] { "tax", "vat", "taxrate", "vatrate" },
                ["cost"] = new[] { "cost", "costprice", "unitcost", "averagecost" },
                ["reorder"] = new[] { "reorderlevel", "minimum", "minlevel", "reorderpoint" },
                ["reorderqty"] = new[] { "reorderquantity", "reorderqty", "orderquantity" },
                ["location"] = new[] { "location", "store", "warehouse" },
                ["qty"] = new[] { "openingquantity", "quantity", "qty", "onhand", "stock", "openingstock" },
            }, new[] { "code", "name", "category", "sub", "unit", "barcode", "hs", "price", "currency", "tax", "cost", "reorder", "reorderqty", "location", "qty" });
            string Cell(List<string> row, string key) => map.TryGetValue(key, out var i) && i < row.Count ? row[i].Trim() : "";
            decimal? Num(List<string> row, string key, int n, List<string> problems)
            {
                var v = Cell(row, key);
                if (v.Length == 0) return null;
                var d = ConversionService.Amount(v.TrimEnd('%'));
                if (d == null) problems.Add($"Row {n}: \"{v}\" is not a number.");
                return d;
            }
            var rows = new List<ItemImportRow>(); var problems = new List<string>();
            foreach (var (row, i) in table.Select((r, i) => (r, i)).Skip(header ? 1 : 0))
            {
                var name = Cell(row, "name");
                if (name.Length == 0 && Cell(row, "code").Length == 0) continue;
                var n = i + 1;
                rows.Add(new ItemImportRow(NullIfEmpty(Cell(row, "code")), name, NullIfEmpty(Cell(row, "category")), NullIfEmpty(Cell(row, "sub")), NullIfEmpty(Cell(row, "unit")), NullIfEmpty(Cell(row, "barcode")),
                    NullIfEmpty(Cell(row, "hs")), Num(row, "price", n, problems), NullIfEmpty(Cell(row, "currency")), Num(row, "tax", n, problems), Num(row, "cost", n, problems), Num(row, "reorder", n, problems),
                    Num(row, "reorderqty", n, problems), NullIfEmpty(Cell(row, "location")), Num(row, "qty", n, problems)));
            }
            return new ItemFileDto(rows, problems);
        }

        private static string? NullIfEmpty(string v) => v.Length == 0 ? null : v;

        private sealed class Plan
        {
            public List<(ItemImportRow Row, Product? Existing, int Line)> Items = new();
            public HashSet<string> NewCategories = new(StringComparer.OrdinalIgnoreCase), NewSubs = new(StringComparer.OrdinalIgnoreCase), NewLocations = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Problems = new(), Warnings = new();
            public int Opening; public decimal OpeningValue;
        }

        private async Task<Plan> PlanAsync(Guid clientId, ItemImportRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var p = new Plan();
            var rows = req.Rows ?? Array.Empty<ItemImportRow>();
            if (rows.Count == 0) { p.Problems.Add("There are no items to import."); return p; }
            if (rows.Count > 10000) throw GatewayException.BadRequest("TOO_MANY", "At most 10,000 items at a time.");
            if (req.Date.Year < 2000 || req.Date.Date > Today.AddDays(1)) p.Problems.Add("The opening stock date is not valid.");
            if (await LockedAsync(clientId, ct) is { } locked && req.Date.Date <= locked && rows.Any(r => r.OpeningQuantity is > 0))
                p.Problems.Add($"Your books are closed up to {locked:d MMM yyyy}: choose a later date for the opening stock.");
            var products = await _db.Products.AsNoTracking().Where(x => x.ClientId == clientId).ToListAsync(ct);
            var byCode = products.Where(x => x.Code != null).GroupBy(x => x.Code!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            var byName = products.GroupBy(x => x.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            var cats = await _db.StockCategories.AsNoTracking().Where(c => c.ClientId == clientId).ToListAsync(ct);
            var locs = await _db.StockLocations.AsNoTracking().Where(l => l.ClientId == clientId).ToListAsync(ct);
            var seenCode = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var seenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i]; var n = i + 1; var name = (r.Name ?? "").Trim();
                var existing = r.Code is { } code && byCode.TryGetValue(code.Trim().ToLowerInvariant(), out var e) ? e : byName.GetValueOrDefault(name.ToLowerInvariant());
                if (existing == null && name.Length < 2) { p.Problems.Add($"Row {n}: give the item a name."); continue; }
                if (name.Length > 200) { p.Problems.Add($"Row {n}: the name is longer than 200 characters."); continue; }
                if (r.Code is { Length: > 40 }) { p.Problems.Add($"Row {n}: the code is longer than 40 characters."); continue; }
                if (r.HsCode != null && !Regex.IsMatch(r.HsCode, @"^(?:\d{4}|\d{6}|\d{8})$")) { p.Problems.Add($"Row {n} ({name}): an HS code is 4, 6 or 8 digits."); continue; }
                if (r.SellingPrice is < 0 || r.Cost is < 0 || r.ReorderLevel is < 0 || r.ReorderQuantity is < 0) { p.Problems.Add($"Row {n} ({name}): prices, costs and reorder figures cannot be negative."); continue; }
                if (r.TaxPercent is < 0 or > 100) { p.Problems.Add($"Row {n} ({name}): tax is between 0 and 100%."); continue; }
                if (r.Code != null && !seenCode.Add(r.Code)) { p.Problems.Add($"Row {n}: code {r.Code} appears twice."); continue; }
                if (r.Code == null && name.Length > 0 && !seenName.Add(name)) { p.Problems.Add($"Row {n}: {name} appears twice."); continue; }
                if (r.Subcategory != null && r.Category == null) { p.Problems.Add($"Row {n} ({name}): a subcategory needs its category."); continue; }
                if (r.Category != null)
                {
                    var cat = cats.FirstOrDefault(c => c.ParentId == null && string.Equals(c.Name, r.Category.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (cat == null) p.NewCategories.Add(r.Category.Trim());
                    if (r.Subcategory != null && (cat == null || !cats.Any(c => c.ParentId == cat.Id && string.Equals(c.Name, r.Subcategory.Trim(), StringComparison.OrdinalIgnoreCase))))
                        p.NewSubs.Add(r.Category.Trim() + " / " + r.Subcategory.Trim());
                }
                if (r.OpeningQuantity is { } q && q != 0)
                {
                    if (q < 0) { p.Problems.Add($"Row {n} ({name}): opening stock cannot be negative."); continue; }
                    if (r.Cost == null && (existing == null || existing.AverageCost == 0)) { p.Problems.Add($"Row {n} ({name}): give the cost per unit for its opening stock."); continue; }
                    if (r.Location != null && !locs.Any(l => string.Equals(l.Code, r.Location.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, r.Location.Trim(), StringComparison.OrdinalIgnoreCase)))
                        p.NewLocations.Add(r.Location.Trim());
                    p.Opening++; p.OpeningValue += Math.Round(q * (r.Cost ?? existing!.AverageCost), 2);
                    if (existing != null && await _db.StockLevels.AnyAsync(x => x.ProductId == existing.Id && x.Quantity != 0, ct)) p.Warnings.Add($"Row {n} ({name}): it already has stock; the opening quantity is added to it.");
                }
                if (existing != null && !req.UpdateExisting) p.Warnings.Add($"Row {n} ({name}): already in your list and left as it is (updating is switched off).");
                p.Items.Add((r with { Name = name }, existing, n));
            }
            if (p.NewLocations.Count > 0) p.Warnings.Add("New location(s) will be created: " + string.Join(", ", p.NewLocations) + ".");
            return p;
        }

        /// <summary>The books' closed period.</summary>
        private async Task<DateTime?> LockedAsync(Guid clientId, CancellationToken ct) =>
            await _db.AccountingSettings.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => s.LockedUntil).FirstOrDefaultAsync(ct);

        public async Task<ItemImportCheckDto> CheckItemsAsync(Guid clientId, ItemImportRequest req, CancellationToken ct)
        {
            var p = await PlanAsync(clientId, req, ct);
            var updated = req.UpdateExisting ? p.Items.Count(i => i.Existing != null) : 0;
            return new ItemImportCheckDto(p.Items.Count(i => i.Existing == null), updated, p.Items.Count(i => i.Existing != null) - updated, p.NewCategories.Count, p.NewSubs.Count, p.NewLocations.Count,
                p.Opening, p.OpeningValue, p.Problems, p.Warnings, p.Problems.Count == 0 && p.Items.Count > 0);
        }

        public async Task<ItemImportResultDto> ImportItemsAsync(Guid clientId, ItemImportRequest req, CancellationToken ct)
        {
            var p = await PlanAsync(clientId, req, ct);
            if (p.Problems.Count > 0) throw GatewayException.BadRequest("IMPORT_INVALID", p.Problems[0] + (p.Problems.Count > 1 ? $" (and {p.Problems.Count - 1} more)" : ""));
            var cats = await _db.StockCategories.Where(c => c.ClientId == clientId).ToListAsync(ct);
            var locs = await _db.StockLocations.Where(l => l.ClientId == clientId).ToListAsync(ct);
            int created = 0, updated = 0, newCats = 0, newLocs = 0;
            StockCategory Category(string name, StockCategory? parent)
            {
                var c = cats.FirstOrDefault(x => x.ParentId == parent?.Id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (c != null) return c;
                c = new StockCategory { ClientId = clientId, Name = name.Length > 100 ? name[..100] : name, ParentId = parent?.Id };
                _db.StockCategories.Add(c); cats.Add(c); newCats++;
                return c;
            }
            StockLocation Location(string? name)
            {
                if (name == null) return locs.Where(l => l.IsActive).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Code).FirstOrDefault() ?? NewLocation("MAIN", "Main store", true);
                return locs.FirstOrDefault(l => string.Equals(l.Code, name, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? NewLocation(UniqueCode(name), name, !locs.Any());
            }
            StockLocation NewLocation(string code, string name, bool isDefault)
            {
                var l = new StockLocation { ClientId = clientId, Code = code, Name = name.Length > 100 ? name[..100] : name, IsDefault = isDefault };
                _db.StockLocations.Add(l); locs.Add(l); newLocs++;
                return l;
            }
            string UniqueCode(string name)
            {
                var b = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]", ""); b = b.Length == 0 ? "LOC" : b[..Math.Min(10, b.Length)];
                var c = b; var i = 2;
                while (locs.Any(l => l.Code == c)) c = b + i++;
                return c;
            }

            var opening = new Dictionary<Guid, List<StockLineInput>>();
            foreach (var (r, existing, _) in p.Items)
            {
                Product prod;
                if (existing == null)
                {
                    prod = new Product { ClientId = clientId, Name = r.Name, Code = r.Code?.Trim(), Currency = "USD" };
                    _db.Products.Add(prod); created++;
                }
                else
                {
                    prod = await _db.Products.FirstAsync(x => x.Id == existing.Id, ct);
                    if (req.UpdateExisting) updated++;
                }
                if (existing == null || req.UpdateExisting)
                {
                    if (r.Name.Length >= 2) prod.Name = r.Name;
                    if (r.Code != null) prod.Code = r.Code.Trim();
                    if (r.Unit != null) prod.Unit = r.Unit.Length > 20 ? r.Unit[..20] : r.Unit;
                    if (r.Barcode != null) prod.Barcode = r.Barcode.Length > 60 ? r.Barcode[..60] : r.Barcode;
                    if (r.HsCode != null) prod.HsCode = r.HsCode;
                    if (r.SellingPrice is { } sp) prod.UnitPrice = Math.Round(sp, 4);
                    if (r.Currency is { Length: 3 } cu) prod.Currency = cu.ToUpperInvariant();
                    if (r.TaxPercent is { } tx) { prod.TaxPercent = Math.Round(tx, 2); prod.IsExempt = false; }
                    if (r.ReorderLevel is { } rl) prod.ReorderLevel = rl;
                    if (r.ReorderQuantity is { } rq) prod.ReorderQuantity = rq;
                    if (r.Category != null) { var cat = Category(r.Category.Trim(), null); prod.CategoryId = r.Subcategory != null ? Category(r.Subcategory.Trim(), cat).Id : cat.Id; }
                    prod.TrackStock = true; prod.IsActive = true; prod.UpdatedAt = DateTime.UtcNow;
                    if (prod.AverageCost == 0 && r.Cost is { } c0 && (r.OpeningQuantity ?? 0) == 0) prod.AverageCost = Math.Round(c0, 4);   // a starting cost for items with no stock yet
                }
                if (r.OpeningQuantity is { } q && q > 0)
                {
                    prod.TrackStock = true;
                    var loc = Location(r.Location?.Trim());
                    if (!opening.TryGetValue(loc.Id, out var list)) opening[loc.Id] = list = new();
                    list.Add(new StockLineInput(prod.Id, q, r.Cost ?? prod.AverageCost));
                }
            }
            await _db.SaveChangesAsync(ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.stock.import", Detail = $"{created} new, {updated} updated, {newCats} categories, {newLocs} locations" });
            await _db.SaveChangesAsync(ct);

            // opening stock: one document per location, posted (stock in, against opening balance equity unless another account was chosen)
            var numbers = new List<string>();
            foreach (var (loc, lines) in opening)
            {
                var doc = await _inventory.SaveDocumentAsync(clientId, new SaveStockDocumentRequest(null, "opening", req.Date, loc, null, null, null, null, "Imported opening stock", null, lines, req.OffsetAccountId), ct);
                numbers.Add((await _inventory.PostDocumentAsync(clientId, doc.Id, ct)).Number ?? "");
            }
            return new ItemImportResultDto(created, updated, newCats, newLocs, numbers);
        }

        // ---- lines of a document, from a file ----

        public async Task<LineFileDto> ReadLinesAsync(Guid clientId, string kind, string fileName, Stream file, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            if (kind is not ("grv" or "transfer" or "adjustment" or "count")) throw GatewayException.BadRequest("KIND_INVALID", "Choose grv, transfer, adjustment or count.");
            var table = ConversionService.ReadTable(fileName ?? "", file);
            if (table.Count == 0) throw GatewayException.BadRequest("FILE_EMPTY", "The file is empty.");
            if (table.Count > 2001) throw GatewayException.BadRequest("FILE_TOO_LARGE", "At most 2000 lines at a time.");
            var (map, header) = ConversionService.Columns(table[0], new()
            {
                ["code"] = new[] { "code", "itemcode", "sku", "stockcode", "barcode" },
                ["name"] = new[] { "name", "itemname", "description", "item", "product" },
                ["qty"] = new[] { "quantity", "qty", "received", "change" },
                ["cost"] = new[] { "unitcost", "cost", "costprice", "price" },
                ["vat"] = new[] { "vat", "vatpercent", "tax", "taxrate" },
                ["counted"] = new[] { "counted", "count", "physical", "physicalcount" },
            }, kind == "count" ? new[] { "code", "name", "category", "unit", "system", "counted" } : new[] { "code", "name", "qty", "cost", "vat" });
            string Cell(List<string> row, string key) => map.TryGetValue(key, out var i) && i < row.Count ? row[i].Trim() : "";
            var products = await _db.Products.AsNoTracking().Where(x => x.ClientId == clientId && x.IsActive).ToListAsync(ct);
            var lines = new List<LineFileRow>(); var problems = new List<string>();
            foreach (var (row, i) in table.Select((r, i) => (r, i)).Skip(header ? 1 : 0))
            {
                var n = i + 1; var code = Cell(row, "code"); var name = Cell(row, "name");
                if (code.Length == 0 && name.Length == 0) continue;
                var prod = (code.Length > 0 ? products.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)) ?? products.FirstOrDefault(x => x.Barcode == code) : null)
                    ?? (name.Length > 0 ? products.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) : null);
                if (prod == null) { problems.Add($"Row {n}: no item with code \"{code}\"{(name.Length > 0 ? $" or name \"{name}\"" : "")}."); continue; }
                if (!prod.TrackStock) { problems.Add($"Row {n}: {prod.Name} is not counted in stock."); continue; }
                if (kind == "count")
                {
                    var counted = Cell(row, "counted");
                    if (counted.Length == 0) continue;   // not counted: left out
                    var c = ConversionService.Amount(counted);
                    if (c is not { } cv || cv < 0) { problems.Add($"Row {n} ({prod.Name}): the count is not a number."); continue; }
                    lines.Add(new LineFileRow(prod.Id, prod.Code, prod.Name, 0, null, null, cv));
                    continue;
                }
                var q = ConversionService.Amount(Cell(row, "qty"));
                if (q is not { } qv || qv == 0) { problems.Add($"Row {n} ({prod.Name}): give a quantity."); continue; }
                var cost = Cell(row, "cost").Length > 0 ? ConversionService.Amount(Cell(row, "cost")) : null;
                var vat = Cell(row, "vat").Length > 0 ? ConversionService.Amount(Cell(row, "vat").TrimEnd('%')) : null;
                lines.Add(new LineFileRow(prod.Id, prod.Code, prod.Name, qv, cost, vat, null));
            }
            return new LineFileDto(lines, problems);
        }
    }
}
