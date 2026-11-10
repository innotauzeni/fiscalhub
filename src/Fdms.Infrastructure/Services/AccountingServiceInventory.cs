using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The stock side of sales, kept up to date with every look at the books: each fiscal receipt and each invoice from the books that sold a stock item takes it out of the
    /// till's location (its branch's, otherwise the default) at average cost and posts the cost of sales; a credit note brings it back. Only from the day stock counting started.
    /// </summary>
    public sealed partial class AccountingService
    {
        private async Task SyncStockAsync(Guid clientId, CancellationToken ct)
        {
            var settings = await _db.AccountingSettings.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (settings?.InventoryStartDate is not { } start) return;
            var tracked = await _db.Products.Where(p => p.ClientId == clientId && p.TrackStock).ToListAsync(ct);
            if (tracked.Count == 0) return;
            var byName = tracked.GroupBy(p => p.Name.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            var byCode = tracked.Where(p => p.Code != null).GroupBy(p => p.Code!.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            Product? Match(string name) { var k = (name ?? "").Trim().ToLowerInvariant(); return byName.GetValueOrDefault(k) ?? byCode.GetValueOrDefault(k); }

            var locations = await _db.StockLocations.AsNoTracking().Where(l => l.ClientId == clientId && l.IsActive).ToListAsync(ct);
            Guid? defaultLocation = locations.OrderByDescending(l => l.IsDefault).ThenBy(l => l.Code).FirstOrDefault()?.Id;
            var deviceBranch = await _db.Devices.AsNoTracking().Where(d => d.ClientId == clientId && d.BranchId != null).ToDictionaryAsync(d => d.Id, d => d.BranchId!.Value, ct);
            Guid LocationFor(Guid? device) =>
                device is { } dv && deviceBranch.TryGetValue(dv, out var b) && locations.FirstOrDefault(l => l.BranchId == b) is { } bl ? bl.Id : defaultLocation!.Value;
            var acc = await StockEngine.AccountsAsync(_db, clientId, ct);
            var cur = await StockEngine.CurrencyAsync(_db, clientId, ct);
            var locked = await LockedUntilAsync(clientId, ct);

            // one sale (or return) document per receipt or invoice, with its cost of sales
            async Task PostAsync(Guid sourceId, string number, DateTime date, bool isReturn, Guid location, IEnumerable<(Product P, decimal Qty)> lines)
            {
                if (defaultLocation == null) defaultLocation = (await StockEngine.DefaultLocationAsync(_db, clientId, ct)).Id;
                var doc = new StockDocument { ClientId = clientId, Kind = isReturn ? "return" : "sale", Number = number.Length > 30 ? number[..30] : number, Date = date.Date, Status = "Posted",
                    LocationId = location, SourceId = sourceId, PostedAt = DateTime.UtcNow, CreatedBy = "system" };
                var parts = new List<(Guid, decimal)>(); var cogs = new List<(Guid, decimal)>(); var n = 0;
                foreach (var (p, q) in lines)
                {
                    var qty = isReturn ? q : -q;
                    var (unit, value) = await StockEngine.MoveAsync(_db, doc, p, location, qty, null, date, ct);
                    doc.Lines.Add(new StockDocumentLine { DocumentId = doc.Id, ClientId = clientId, Sort = ++n, ProductId = p.Id, Quantity = q, UnitCost = Math.Round(unit, 4), Amount = Math.Abs(value) });
                    parts.Add((acc.InventoryFor(p), value)); cogs.Add((acc.CogsFor(p), -value));
                }
                doc.Subtotal = doc.Total = doc.Lines.Sum(l => l.Amount);
                _db.StockDocuments.Add(doc);
                // inventory against cost of sales, each on its own account (a category can have its own)
                var journal = new List<JournalLineInput>();
                foreach (var g in parts.Concat(cogs).GroupBy(c => c.Item1))
                {
                    var v = g.Sum(x => x.Item2);
                    if (v > 0) journal.Add(new JournalLineInput(g.Key, v, 0, null)); else if (v < 0) journal.Add(new JournalLineInput(g.Key, 0, -v, null));
                }
                if (journal.Count >= 2 && journal.Sum(l => l.Debit) == journal.Sum(l => l.Credit))
                {
                    var on = locked is { } lk && date.Date <= lk ? lk.AddDays(1) : date.Date;
                    var memo = $"Cost of {(isReturn ? "goods returned on" : "sales on")} {number}" + (on != date.Date ? $" (dated {date:yyyy-MM-dd})" : "");
                    doc.JournalEntryId = (await PostLinesAsync(clientId, on, cur, memo.Length > 200 ? memo[..200] : memo, number.Length > 60 ? number[..60] : number, "cogs", doc.Id, journal, ct)).Id;
                }
                await _db.SaveChangesAsync(ct);
            }

            // fiscal receipts (read once; a window behind the mark catches receipts that were held and went through later)
            var mark = (settings.StockSyncedAt ?? DateTime.MinValue.AddDays(8)).AddDays(-7);
            var done = (await _db.StockDocuments.AsNoTracking().Where(d => d.ClientId == clientId && d.SourceId != null && (d.Kind == "sale" || d.Kind == "return")).Select(d => d.SourceId!.Value).ToListAsync(ct)).ToHashSet();
            var newest = settings.StockSyncedAt;
            for (var round = 0; round < 20; round++)
            {
                var batch = await _db.Receipts.AsNoTracking()
                    .Where(r => r.ClientId == clientId && r.CreatedAt > mark && r.ReceiptDate >= start && (r.Status == ReceiptStatus.Submitted || r.Status == ReceiptStatus.Pending || r.Status == ReceiptStatus.OfflineIssued))
                    .OrderBy(r => r.CreatedAt).Take(500).ToListAsync(ct);
                if (batch.Count == 0) break;
                foreach (var r in batch)
                {
                    mark = r.CreatedAt; if (newest == null || r.CreatedAt > newest) newest = r.CreatedAt;
                    if (done.Contains(r.Id) || r.Type == ReceiptType.DebitNote) continue;
                    ReceiptPayload? p = null;
                    try { if (!string.IsNullOrEmpty(r.PayloadJson)) p = JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options); } catch (JsonException) { }
                    if (p == null) continue;
                    var lines = p.ReceiptLines.Select(l => (P: Match(l.ReceiptLineName), Q: Math.Abs(l.ReceiptLineQuantity))).Where(x => x.P != null && x.Q > 0).Select(x => (x.P!, x.Q)).ToList();
                    if (lines.Count == 0) continue;
                    await PostAsync(r.Id, r.InvoiceNo, r.ReceiptDate, r.Type == ReceiptType.CreditNote, LocationFor(r.DeviceId), lines);
                    done.Add(r.Id);
                }
                if (batch.Count < 500) break;
            }
            if (newest != settings.StockSyncedAt) { settings.StockSyncedAt = newest; await _db.SaveChangesAsync(ct); }

            // invoices and credit notes from the books
            var docs = await _db.SalesInvoices.AsNoTracking().Include(d => d.Lines)
                .Where(d => d.ClientId == clientId && d.Status == "Issued" && d.Kind != "opening" && d.Date >= start && d.Lines.Any(l => l.ProductId != null) && !_db.StockDocuments.Any(s => s.SourceId == d.Id))
                .OrderBy(d => d.IssuedAt).Take(500).ToListAsync(ct);
            var ids = tracked.ToDictionary(p => p.Id);
            foreach (var d in docs)
            {
                var lines = d.Lines.Where(l => l.ProductId is { } pid && ids.ContainsKey(pid)).Select(l => (ids[l.ProductId!.Value], l.Quantity)).ToList();
                if (lines.Count == 0) continue;
                await PostAsync(d.Id, d.Number ?? "", d.Date, d.Kind == "credit_note", defaultLocation ?? (await StockEngine.DefaultLocationAsync(_db, clientId, ct)).Id, lines);
            }

            // an invoice voided after its stock went out: the stock comes back and the cost of sales is reversed
            var voided = await (from s in _db.StockDocuments where s.ClientId == clientId && s.Status == "Posted" && (s.Kind == "sale" || s.Kind == "return")
                                join i in _db.SalesInvoices on s.SourceId equals i.Id where i.Status == "Void" select s).ToListAsync(ct);
            foreach (var s in voided)
            {
                var moves = await _db.StockMovements.Where(m => m.DocumentId == s.Id).ToListAsync(ct);
                var pids = moves.Select(m => m.ProductId).Distinct().ToList();
                var prods = await _db.Products.Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
                foreach (var m in moves) await StockEngine.MoveAsync(_db, s, prods[m.ProductId], m.LocationId, -m.Quantity, m.UnitCost, s.Date, ct);
                if (s.JournalEntryId is { } eid && await _db.JournalEntries.AnyAsync(e => e.Id == eid && e.ReversedById == null, ct))
                    try { await ReverseAtAsync(clientId, eid, null, ct); } catch (GatewayException) { }
                s.Status = "Void"; s.VoidedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }
    }
}
