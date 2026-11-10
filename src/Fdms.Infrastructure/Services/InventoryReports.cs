using System.Globalization;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Fdms.Infrastructure.Services
{
    public sealed partial class InventoryService
    {
        static InventoryService() => QuestPDF.Settings.License = LicenseType.Community;

        // ---- stock on hand and its value, at any date ----

        public async Task<StockOnHandDto> OnHandAsync(Guid clientId, DateTime? asAt, Guid? locationId, Guid? categoryId, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var at = (asAt ?? Today).Date;
            var q = _db.StockMovements.AsNoTracking().Where(m => m.ClientId == clientId && m.Date <= at);
            if (locationId is { } l) q = q.Where(m => m.LocationId == l);
            var sums = await q.GroupBy(m => m.ProductId).Select(g => new { g.Key, Q = g.Sum(x => x.Quantity), V = g.Sum(x => x.Value) }).ToListAsync(ct);
            var ids = sums.Select(s => s.Key).ToList();
            var prods = await _db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var cats = (await CategoriesAsync(clientId, ct)).ToDictionary(c => c.Id);
            var rows = new List<StockOnHandRow>();
            foreach (var s in sums.Where(s => s.Q != 0 || s.V != 0))
            {
                if (!prods.TryGetValue(s.Key, out var p)) continue;
                if (categoryId is { } cid && p.CategoryId != cid && !(p.CategoryId is { } pc && cats.TryGetValue(pc, out var c) && c.ParentId == cid)) continue;
                rows.Add(new StockOnHandRow(p.Id, p.Code, p.Name, p.CategoryId is { } k && cats.TryGetValue(k, out var cc) ? cc.Path : null, p.Unit, s.Q, s.Q != 0 ? Math.Round(s.V / s.Q, 4) : 0, s.V, p.ReorderLevel));
            }
            rows = rows.OrderBy(r => r.Category ?? "~").ThenBy(r => r.Name).ToList();
            var cur = await StockEngine.CurrencyAsync(_db, clientId, ct);
            decimal? ledger = null;
            if (locationId == null && categoryId == null)
            {
                var acc = await StockEngine.AccountsAsync(_db, clientId, ct);
                var accounts = acc.ByCategory.Values.Select(v => v.Inventory).Append(acc.Inventory).Distinct().ToList();
                ledger = await _db.JournalLines.AsNoTracking().Where(x => x.ClientId == clientId && x.Currency == cur && x.Date <= at && accounts.Contains(x.AccountId)).SumAsync(x => x.Debit - x.Credit, ct);
            }
            var total = rows.Sum(r => r.Value);
            return new StockOnHandDto(at, locationId, cur, rows, rows.Sum(r => r.Quantity), total, ledger, ledger is { } lv ? total - lv : null);
        }

        private static string KindLabel(string kind, string? mode) => kind switch
        {
            "grv" => "Goods received", "transfer" => "Transfer", "adjustment" => mode == "count" ? "Stock take" : "Adjustment", "opening" => "Opening stock", "sale" => "Sale", "return" => "Return", _ => kind,
        };

        public async Task<StockCardDto> StockCardAsync(Guid clientId, Guid productId, Guid? locationId, DateTime from, DateTime to, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == productId && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Item");
            var f = from.Date; var t = to.Date;
            if (t < f) throw GatewayException.BadRequest("RANGE_INVALID", "The end date is before the start date.");
            var q = _db.StockMovements.AsNoTracking().Where(m => m.ProductId == productId && m.Date <= t);
            if (locationId is { } l) q = q.Where(m => m.LocationId == l);
            var moves = await q.OrderBy(m => m.Date).ThenBy(m => m.CreatedAt).ToListAsync(ct);
            var docIds = moves.Select(m => m.DocumentId).Distinct().ToList();
            var docs = await _db.StockDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
            var locs = await _db.StockLocations.AsNoTracking().Where(x => x.ClientId == clientId).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
            var openQ = moves.Where(m => m.Date < f).Sum(m => m.Quantity); var openV = moves.Where(m => m.Date < f).Sum(m => m.Value);
            var bal = openQ; var lines = new List<StockCardLine>();
            foreach (var m in moves.Where(m => m.Date >= f))
            {
                bal += m.Quantity;
                var d = docs.GetValueOrDefault(m.DocumentId);
                lines.Add(new StockCardLine(m.Date, m.DocumentId, d == null ? "" : KindLabel(d.Kind, d.Mode), d?.Number, locs.GetValueOrDefault(m.LocationId, ""), d?.SupplierReference ?? d?.Reason,
                    m.Quantity > 0 ? m.Quantity : 0, m.Quantity < 0 ? -m.Quantity : 0, bal, m.UnitCost, m.Value));
            }
            var item = (await MapItemsAsync(clientId, new List<Product> { p }, ct))[0];
            return new StockCardDto(item, locationId, f, t, openQ, openV, lines, bal, openV + lines.Sum(x => x.Value));
        }

        public async Task<IReadOnlyList<ReorderRow>> ReorderAsync(Guid clientId, CancellationToken ct)
        {
            var items = await ItemsAsync(clientId, new StockItemFilter(null, null, null, "low"), ct);
            var ids = items.Select(i => i.Id).ToList();
            var last = await (from l in _db.StockDocumentLines.AsNoTracking() join d in _db.StockDocuments.AsNoTracking() on l.DocumentId equals d.Id
                              where d.ClientId == clientId && d.Kind == "grv" && d.Status == "Posted" && ids.Contains(l.ProductId)
                              select new { l.ProductId, d.Date, d.SupplierId }).ToListAsync(ct);
            var sups = await _db.Suppliers.AsNoTracking().Where(s => s.ClientId == clientId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            return items.Select(i =>
            {
                var from = last.Where(x => x.ProductId == i.Id).OrderByDescending(x => x.Date).FirstOrDefault();
                var suggest = i.ReorderQuantity > 0 ? i.ReorderQuantity : Math.Max(0, i.ReorderLevel * 2 - i.OnHand);
                return new ReorderRow(i.Id, i.Code, i.Name, i.Category, i.Unit, i.OnHand, i.ReorderLevel, suggest, i.LastCost, from?.SupplierId is { } s ? sups.GetValueOrDefault(s) : null);
            }).OrderBy(r => r.Name).ToList();
        }

        // ---- the printed document: GRV, transfer note, stock take sheet, adjustment, opening stock ----

        public async Task<(byte[] Pdf, string FileName)> DocumentPdfAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var d = await DocumentAsync(clientId, id, ct);
            var me = await CompanyIdentity.LoadAsync(_db, clientId, ct);
            var cur = await StockEngine.CurrencyAsync(_db, clientId, ct);
            var c = CultureInfo.InvariantCulture;
            string M(decimal v) => v.ToString("N2", c);
            string Q(decimal v) => v.ToString("#,0.####", c);
            const string accent = "#0f766e";
            var title = d.Kind switch { "grv" => "GOODS RECEIVED VOUCHER", "transfer" => "STOCK TRANSFER NOTE", "adjustment" => d.Mode == "count" ? "STOCK TAKE" : "STOCK ADJUSTMENT", "opening" => "OPENING STOCK", _ => "STOCK DOCUMENT" };
            var count = d.Mode == "count";
            var costs = d.Kind != "transfer" || d.Status == "Posted";
            var pdf = Document.Create(doc => doc.Page(page =>
            {
                page.Size(PageSizes.A4); page.Margin(36); page.DefaultTextStyle(x => x.FontSize(9.5f));
                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(me.Name).Bold().FontSize(14).FontColor(accent);
                        if (me.Address != null) col.Item().Text(me.Address);
                        if (me.Tin != null) col.Item().Text("TIN " + me.Tin);
                    });
                    row.ConstantItem(230).Column(col =>
                    {
                        col.Item().AlignRight().Text(d.Status == "Draft" ? "DRAFT " + title : title).Bold().FontSize(15).FontColor(accent);
                        col.Item().AlignRight().Text(d.Number ?? "Not posted yet").SemiBold();
                        col.Item().AlignRight().Text("Date " + d.Date.ToString("d MMM yyyy", c));
                        if (d.Status == "Void") col.Item().AlignRight().Text("VOID").Bold().FontColor(Colors.Red.Darken2);
                    });
                });
                page.Content().PaddingTop(14).Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Background(Colors.Grey.Lighten4).Padding(8).Row(r =>
                    {
                        r.RelativeItem().Column(x =>
                        {
                            if (d.Kind == "grv") { x.Item().Text("Supplier").FontSize(8).FontColor(Colors.Grey.Darken1); x.Item().Text(d.Supplier ?? "").Bold(); if (d.SupplierReference != null) x.Item().Text("Their invoice / delivery note " + d.SupplierReference); }
                            else if (d.Kind == "transfer") { x.Item().Text("From").FontSize(8).FontColor(Colors.Grey.Darken1); x.Item().Text(d.Location).Bold(); }
                            else { x.Item().Text("Location").FontSize(8).FontColor(Colors.Grey.Darken1); x.Item().Text(d.Location).Bold(); }
                        });
                        r.RelativeItem().Column(x =>
                        {
                            if (d.Kind == "transfer") { x.Item().Text("To").FontSize(8).FontColor(Colors.Grey.Darken1); x.Item().Text(d.ToLocation ?? "").Bold(); }
                            else if (d.Kind == "grv") { x.Item().Text("Received into").FontSize(8).FontColor(Colors.Grey.Darken1); x.Item().Text(d.Location).Bold(); }
                            if (d.Reason != null) x.Item().Text("Reason: " + d.Reason);
                        });
                    });
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(cd =>
                        {
                            cd.ConstantColumn(70); cd.RelativeColumn(); cd.ConstantColumn(40);
                            if (count) { cd.ConstantColumn(60); cd.ConstantColumn(60); cd.ConstantColumn(60); }
                            else cd.ConstantColumn(60);
                            if (costs) { cd.ConstantColumn(66); cd.ConstantColumn(76); }
                        });
                        void H(string s, bool right = true) { var cell = t.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4); (right ? cell.AlignRight() : cell).Text(s).Bold(); }
                        H("Code", false); H("Item", false); H("Unit", false);
                        if (count) { H("System"); H("Counted"); H("Difference"); } else H(d.Kind == "adjustment" ? "Change" : "Quantity");
                        if (costs) { H("Unit cost"); H("Amount"); }
                        foreach (var l in d.Lines)
                        {
                            IContainer Cell() => t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3);
                            Cell().Text(l.Code ?? ""); Cell().Text(l.Name); Cell().Text(l.Unit ?? "");
                            if (count) { Cell().AlignRight().Text(l.Expected is { } e ? Q(e) : "__________"); Cell().AlignRight().Text(l.Counted is { } k ? Q(k) : ""); Cell().AlignRight().Text(d.Status == "Posted" ? Q(l.Quantity) : ""); }
                            else Cell().AlignRight().Text(Q(l.Quantity));
                            if (costs) { Cell().AlignRight().Text(M(l.UnitCost)); Cell().AlignRight().Text(M(l.Amount)); }
                        }
                    });
                    if (costs)
                        col.Item().AlignRight().Width(230).Column(tot =>
                        {
                            void Line(string label, string value, bool bold = false) => tot.Item().Row(r => { var a = r.RelativeItem().Text(label); var b = r.ConstantItem(100).AlignRight().Text(value); if (bold) { a.Bold(); b.Bold(); } });
                            if (d.Kind == "grv") { Line("Before VAT", M(d.Subtotal)); Line("VAT", M(d.Vat)); }
                            Line($"Total {cur}", M(d.Kind == "adjustment" ? d.Lines.Sum(l => l.Amount) : d.Total), true);
                        });
                    if (d.Notes != null) col.Item().Text(d.Notes);
                    col.Item().PaddingTop(30).Row(r =>
                    {
                        foreach (var who in d.Kind switch { "grv" => new[] { "Received by", "Checked by" }, "transfer" => new[] { "Sent by", "Received by" }, _ => new[] { "Counted by", "Approved by" } })
                            r.RelativeItem().PaddingRight(20).Column(x => { x.Item().PaddingTop(20).LineHorizontal(0.75f); x.Item().Text(who + " (name, signature, date)").FontSize(8).FontColor(Colors.Grey.Darken1); });
                    });
                });
                page.Footer().AlignCenter().Text(x => { x.Span("Page ").FontSize(8); x.CurrentPageNumber().FontSize(8); x.Span(" of ").FontSize(8); x.TotalPages().FontSize(8); x.Span($" · {me.Name}").FontSize(8); });
            })).GeneratePdf();
            return (pdf, $"{d.Kind}-{d.Number ?? "draft"}.pdf");
        }
    }
}
