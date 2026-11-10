using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The heart of inventory: moving an item in or out of a location, keeping its weighted average cost (company-wide, in the base currency), and the accounts stock posts to.
    /// Stock in at a cost changes the average; stock out (and transfers) go at the average. Taking back stock that came in (a voided GRV) removes it at the cost it came in at.
    /// </summary>
    internal static class StockEngine
    {
        public sealed record Accounts(Guid Inventory, Guid Cogs, Guid Adjustment, Dictionary<Guid, (Guid Inventory, Guid Cogs)> ByCategory)
        {
            public Guid InventoryFor(Product p) => p.CategoryId is { } c && ByCategory.TryGetValue(c, out var a) ? a.Inventory : Inventory;
            public Guid CogsFor(Product p) => p.CategoryId is { } c && ByCategory.TryGetValue(c, out var a) ? a.Cogs : Cogs;
        }

        /// <summary>The inventory, cost of sales and stock adjustment accounts (found by role; the standard chart's 1200 and 5010 take the roles; the adjustment account is made the first time).</summary>
        public static async Task<Accounts> AccountsAsync(FdmsDbContext db, Guid clientId, CancellationToken ct)
        {
            var all = await db.LedgerAccounts.Where(a => a.ClientId == clientId).ToListAsync(ct);
            var rules = await ChartCodes.RulesAsync(db, clientId, ct);
            var changed = false;
            LedgerAccount Find(string role, string code, string name, string type, string? subType)
            {
                var a = all.Where(x => x.Role == role && !x.IsGroup).OrderBy(x => x.Code).FirstOrDefault();
                if (a != null) return a;
                a = all.FirstOrDefault(x => x.Code == code && x.Role == null && x.Type == type && !x.IsGroup);
                if (a == null)
                {
                    var next = ChartCodes.Next(type, null, all.Select(x => x.Code), rules).Code ?? code + "9";
                    a = new LedgerAccount { ClientId = clientId, Code = next, Name = name, Type = type, SubType = subType, IsSystem = true };
                    db.LedgerAccounts.Add(a); all.Add(a);
                }
                a.Role = role; changed = true;
                return a;
            }
            var inv = Find("inventory", "1200", "Inventory", "Asset", "current_asset");
            var cogs = Find("cogs", "5010", "Cost of sales", "Expense", "cost_of_sales");
            var adj = Find("stock_adjustment", "5015", "Stock adjustments and write-offs", "Expense", "cost_of_sales");
            if (changed) await db.SaveChangesAsync(ct);

            var cats = await db.StockCategories.AsNoTracking().Where(c => c.ClientId == clientId).ToListAsync(ct);
            var byId = cats.ToDictionary(c => c.Id);
            var map = new Dictionary<Guid, (Guid, Guid)>();
            foreach (var c in cats)
            {
                var parent = c.ParentId is { } p && byId.TryGetValue(p, out var pc) ? pc : null;
                map[c.Id] = (c.InventoryAccountId ?? parent?.InventoryAccountId ?? inv.Id, c.CogsAccountId ?? parent?.CogsAccountId ?? cogs.Id);
            }
            return new Accounts(inv.Id, cogs.Id, adj.Id, map);
        }

        public static Task<string> CurrencyAsync(FdmsDbContext db, Guid clientId, CancellationToken ct) => ExchangeRateService.BaseAsync(db, clientId, ct);

        /// <summary>Where sales take stock from when nothing says otherwise: the default location, made the first time ("MAIN").</summary>
        public static async Task<StockLocation> DefaultLocationAsync(FdmsDbContext db, Guid clientId, CancellationToken ct)
        {
            var l = await db.StockLocations.Where(x => x.ClientId == clientId && x.IsActive).OrderByDescending(x => x.IsDefault).ThenBy(x => x.Code).FirstOrDefaultAsync(ct);
            if (l != null) return l;
            l = new StockLocation { ClientId = clientId, Code = "MAIN", Name = "Main store", IsDefault = true };
            db.StockLocations.Add(l); await db.SaveChangesAsync(ct);
            return l;
        }

        /// <summary>Stock counting starts with the first document posted (sales before it never take stock out).</summary>
        public static async Task StartAsync(FdmsDbContext db, Guid clientId, DateTime date, CancellationToken ct)
        {
            var s = await db.AccountingSettings.FirstOrDefaultAsync(x => x.ClientId == clientId, ct);
            if (s == null) db.AccountingSettings.Add(s = new AccountingSetting { ClientId = clientId });
            if (s.InventoryStartDate == null || s.InventoryStartDate > date.Date) { s.InventoryStartDate = date.Date; s.StockSyncedAt = null; }   // every sale dated from then on is read
        }

        private static async Task<List<StockLevel>> LevelsAsync(FdmsDbContext db, Guid productId, CancellationToken ct)
        {
            var loaded = await db.StockLevels.Where(l => l.ProductId == productId).ToListAsync(ct);
            return loaded.Concat(db.StockLevels.Local.Where(l => l.ProductId == productId)).Distinct().ToList();
        }

        /// <summary>What is at one location now (counting changes not yet saved).</summary>
        public static async Task<decimal> OnHandAsync(FdmsDbContext db, Guid productId, Guid locationId, CancellationToken ct) =>
            (await LevelsAsync(db, productId, ct)).Where(l => l.LocationId == locationId).Sum(l => l.Quantity);

        /// <summary>
        /// Moves quantity (signed) of an item at a location. Cost: the cost per unit of stock coming in (changes the average) or of stock being taken back out at the cost it came in
        /// (a voided receipt); null: at the average cost. Returns the cost per unit used and the movement's value.
        /// </summary>
        public static async Task<(decimal UnitCost, decimal Value)> MoveAsync(FdmsDbContext db, StockDocument doc, Product product, Guid locationId, decimal quantity, decimal? cost, DateTime date, CancellationToken ct)
        {
            var levels = await LevelsAsync(db, product.Id, ct);
            var level = levels.FirstOrDefault(l => l.LocationId == locationId);
            if (level == null) { level = new StockLevel { ClientId = doc.ClientId, ProductId = product.Id, LocationId = locationId }; db.StockLevels.Add(level); levels.Add(level); }
            var total = levels.Sum(l => l.Quantity);
            var unit = cost ?? product.AverageCost;
            if (cost is { } c)
            {
                var after = total + quantity;
                // the value on hand changes by quantity x cost; the average is what is left over the quantity left
                if (after > 0 && total > 0) product.AverageCost = Math.Max(0, Math.Round((total * product.AverageCost + quantity * c) / after, 4));
                else if (quantity > 0) product.AverageCost = Math.Round(c, 4);
            }
            level.Quantity += quantity;
            var value = Math.Round(quantity * unit, 2, MidpointRounding.AwayFromZero);
            db.StockMovements.Add(new StockMovement { ClientId = doc.ClientId, DocumentId = doc.Id, ProductId = product.Id, LocationId = locationId, Date = date.Date, Quantity = quantity,
                UnitCost = Math.Round(unit, 4), Value = value, BalanceAfter = level.Quantity, AverageCostAfter = product.AverageCost });
            return (unit, value);
        }

        /// <summary>Journal lines that move value between inventory (per account) and another account: positive value = stock up.</summary>
        public static List<JournalLineInput> Lines(IEnumerable<(Guid InventoryAccount, decimal Value)> parts, Guid other, string? text = null)
        {
            var lines = new List<JournalLineInput>();
            foreach (var g in parts.GroupBy(p => p.InventoryAccount))
            {
                var v = g.Sum(x => x.Value);
                if (v > 0) lines.Add(new JournalLineInput(g.Key, v, 0, text));
                else if (v < 0) lines.Add(new JournalLineInput(g.Key, 0, -v, text));
            }
            var net = lines.Sum(l => l.Debit - l.Credit);
            if (net > 0) lines.Add(new JournalLineInput(other, 0, net, text));
            else if (net < 0) lines.Add(new JournalLineInput(other, -net, 0, text));
            return lines;
        }
    }
}
