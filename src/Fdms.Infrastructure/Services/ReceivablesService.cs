using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// What customers owe. A fiscal invoice (or part of one) that was not paid when it was issued goes to receivables in the books, and so does an invoice issued from the books
    /// (accounting-only companies) or an unpaid invoice brought in when the books started here. This follows each one to the day it is settled, by payments received or by credit
    /// notes against it, and ages what is left by how long it has been due.
    /// </summary>
    public sealed class ReceivablesService : IReceivablesService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IAccountingService _books;
        public ReceivablesService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private sealed class Item
        {
            public Guid Id; public string No = ""; public DateTime Date; public DateTime? Due; public string Currency = ""; public decimal Total; public string Source = "fiscal";
            public string Key = ""; public string Name = ""; public string? Tin; public decimal OnAccount; public decimal Credited; public List<ArPayment> Pays = new();
            public decimal Received(DateTime? at = null) => Pays.Where(p => !p.IsVoid && (at == null || p.Date <= at)).Sum(p => p.Amount);
        }

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
        }

        /// <summary>The same customer key a fiscal invoice gets: by TIN when there is one, otherwise by name.</summary>
        internal static string KeyOf(string name, string? tin) => !string.IsNullOrWhiteSpace(tin) ? "tin:" + tin.Trim() : "name:" + name.Trim().ToLowerInvariant();

        private static (string Key, string Name, string? Tin) Customer(Receipt r)
        {
            try
            {
                var b = string.IsNullOrEmpty(r.PayloadJson) ? null : JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options)?.BuyerData;
                if (b == null) return ("none", "Walk-in customers", null);
                var name = !string.IsNullOrWhiteSpace(b.BuyerTradeName) ? b.BuyerTradeName! : b.BuyerRegisterName;
                var tin = string.IsNullOrWhiteSpace(b.BuyerTin) ? null : b.BuyerTin.Trim();
                return (tin != null ? "tin:" + tin : "name:" + name.Trim().ToLowerInvariant(), name.Trim(), tin);
            }
            catch (JsonException) { return ("none", "Walk-in customers", null); }
        }

        public async Task<ReceivablesSettingsDto> GetSettingsAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            return new ReceivablesSettingsDto(await _db.AccountingSettings.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => s.CustomerTermsDays).FirstOrDefaultAsync(ct) is var d && d > 0 ? d : 30);
        }

        public async Task<ReceivablesSettingsDto> SetTermsAsync(Guid clientId, int termsDays, CancellationToken ct)
        {
            RequireWholeCompany();
            if (termsDays is < 0 or > 365) throw GatewayException.BadRequest("TERMS_INVALID", "Payment terms are between 0 and 365 days.");
            var row = await _db.AccountingSettings.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (row == null) _db.AccountingSettings.Add(row = new AccountingSetting { ClientId = clientId });
            row.CustomerTermsDays = termsDays; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            await _db.SaveChangesAsync(ct);
            return new ReceivablesSettingsDto(termsDays);
        }

        /// <summary>Every fiscal invoice that put something on account, with what has been credited and received against it.</summary>
        private async Task<List<Item>> LoadAsync(Guid clientId, string? currency, CancellationToken ct)
        {
            await _books.SyncSalesAsync(clientId, ct);
            var recIds = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && a.Role == "receivable").Select(a => a.Id).ToListAsync(ct);
            var lines = await (from l in _db.JournalLines.AsNoTracking() join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                               where l.ClientId == clientId && recIds.Contains(l.AccountId) && e.Source == "sales" && e.SourceId != null && (currency == null || l.Currency == currency)
                               select new { e.SourceId, l.Debit, l.Credit }).ToListAsync(ct);
            var byReceipt = lines.GroupBy(x => x.SourceId!.Value).ToDictionary(g => g.Key, g => (Dr: g.Sum(x => x.Debit), Cr: g.Sum(x => x.Credit)));
            var ids = byReceipt.Keys.ToList();
            var receipts = ids.Count == 0 ? new List<Receipt>() : await _db.Receipts.AsNoTracking().Where(r => r.ClientId == clientId && ids.Contains(r.Id)).ToListAsync(ct);

            var items = receipts.Where(r => r.Type != ReceiptType.CreditNote && byReceipt[r.Id].Dr > 0).Select(r =>
            {
                var (key, name, tin) = Customer(r);
                return new Item { Id = r.Id, No = r.InvoiceNo, Date = r.ReceiptDate, Currency = r.Currency, Total = Math.Abs(r.Total), Key = key, Name = name, Tin = tin, OnAccount = byReceipt[r.Id].Dr };
            }).ToDictionary(i => i.Id);
            // invoices from the books and unpaid invoices brought in at the start, with the credit notes against them
            var docs = await _db.SalesInvoices.AsNoTracking().Where(d => d.ClientId == clientId && d.Status == "Issued" && (currency == null || d.Currency == currency)).ToListAsync(ct);
            foreach (var d in docs.Where(d => d.Kind != "credit_note"))
                items[d.Id] = new Item { Id = d.Id, No = d.Number ?? "", Date = d.Date, Due = d.DueDate, Currency = d.Currency, Total = d.Total, Source = d.Kind == "opening" ? "opening" : "invoice",
                    Key = KeyOf(d.CustomerName, d.CustomerTin), Name = d.CustomerName, Tin = d.CustomerTin, OnAccount = d.Total };
            foreach (var cn in docs.Where(d => d.Kind == "credit_note" && d.OriginalId != null))
                if (items.TryGetValue(cn.OriginalId!.Value, out var orig)) orig.Credited += cn.Total;
            foreach (var cn in receipts.Where(r => r.Type == ReceiptType.CreditNote && byReceipt[r.Id].Cr > 0 && r.OriginalReceiptId != null))
                if (items.TryGetValue(cn.OriginalReceiptId!.Value, out var target)) target.Credited += byReceipt[cn.Id].Cr;
            var itemIds = items.Keys.ToList();
            if (itemIds.Count > 0)
                foreach (var p in await _db.ArPayments.AsNoTracking().Where(p => p.ClientId == clientId && itemIds.Contains(p.ReceiptId)).OrderBy(p => p.Date).ToListAsync(ct)) items[p.ReceiptId].Pays.Add(p);
            return items.Values.OrderByDescending(i => i.Date).ToList();
        }

        private async Task<int> TermsAsync(Guid clientId, CancellationToken ct) => (await GetSettingsAsync(clientId, ct)).TermsDays;

        private async Task<Dictionary<Guid, string>> AccountNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
        {
            var list = ids.Distinct().ToList();
            return await _db.LedgerAccounts.AsNoTracking().Where(a => list.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code + " " + a.Name, ct);
        }

        private OpenInvoiceDto ToDto(Item i, int terms, DateTime today, IReadOnlyDictionary<Guid, string> accounts)
        {
            var received = i.Received(); var outstanding = i.OnAccount - i.Credited - received;
            var due = (i.Due ?? i.Date.Date.AddDays(terms)).Date;
            return new OpenInvoiceDto(i.Id, i.No, i.Date, due, i.Key, i.Name, i.Tin, i.Currency, i.Total, i.OnAccount, i.Credited, received, outstanding,
                outstanding > 0 && due < today ? (int)(today - due).TotalDays : 0, i.Pays.Select(p => new ArPaymentDto(p.Id, p.Date, p.Amount, accounts.GetValueOrDefault(p.AccountId, ""), p.Reference, p.IsVoid)).ToList(), i.Source);
        }

        public async Task<IReadOnlyList<OpenInvoiceDto>> ListAsync(Guid clientId, OpenInvoiceFilter f, CancellationToken ct)
        {
            RequireWholeCompany();
            var cur = string.IsNullOrWhiteSpace(f.Currency) ? null : f.Currency.Trim().ToUpperInvariant();
            var items = await LoadAsync(clientId, cur, ct);
            var terms = await TermsAsync(clientId, ct); var today = DateTime.UtcNow.AddHours(2).Date;
            var accounts = await AccountNamesAsync(items.SelectMany(i => i.Pays.Select(p => p.AccountId)), ct);
            var list = items.Select(i => ToDto(i, terms, today, accounts)).ToList();
            if (!string.IsNullOrWhiteSpace(f.Customer)) list = list.Where(i => i.CustomerKey == f.Customer).ToList();
            if (!string.IsNullOrWhiteSpace(f.Q)) { var q = f.Q.Trim(); list = list.Where(i => i.InvoiceNo.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Customer.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(); }
            list = (f.Status ?? "open").ToLowerInvariant() switch { "all" => list, "paid" => list.Where(i => i.Outstanding <= 0).ToList(), "overdue" => list.Where(i => i.DaysOverdue > 0).ToList(), _ => list.Where(i => i.Outstanding > 0).ToList() };
            return list.Take(Math.Clamp(f.Take, 1, 1000)).ToList();
        }

        private async Task<OpenInvoiceDto> OneAsync(Guid clientId, Guid receiptId, CancellationToken ct) =>
            (await ListAsync(clientId, new OpenInvoiceFilter("all", null, null, null, 1000), ct)).FirstOrDefault(i => i.ReceiptId == receiptId) ?? throw GatewayException.NotFound("Invoice");

        public async Task<OpenInvoiceDto> ReceiveAsync(Guid clientId, Guid receiptId, ReceiveRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var inv = await OneAsync(clientId, receiptId, ct);
            if (req.Amount <= 0 || req.Amount != Math.Round(req.Amount, 2)) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter an amount above zero with at most two decimals.");
            if (req.Amount > inv.Outstanding) throw GatewayException.Conflict("OVERPAYMENT", $"Only {inv.Outstanding:N2} {inv.Currency} is still owing on this invoice.");
            if (req.Date.Year < 2000 || req.Date > DateTime.UtcNow.AddDays(2)) throw GatewayException.BadRequest("DATE_INVALID", "That date is not valid.");
            var into = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.AccountId && a.ClientId == clientId, ct) ?? throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose the cash or bank account it was paid into.");
            if (into.Type != "Asset" || !into.IsActive || into.Role == "receivable") throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose a cash, bank or wallet account.");
            var receivable = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && a.Role == "receivable" && a.IsActive).OrderBy(a => a.Code).FirstOrDefaultAsync(ct)
                ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The accounts receivable account is missing or switched off in your chart of accounts.");
            var pay = new ArPayment { ClientId = clientId, ReceiptId = receiptId, Date = req.Date.Date, Amount = req.Amount, AccountId = into.Id, Reference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim() };
            if (pay.Reference is { Length: > 50 }) throw GatewayException.BadRequest("REFERENCE_INVALID", "The reference can be at most 50 characters.");
            var entry = await _books.PostEntryAsync(clientId, pay.Date, inv.Currency, $"Payment received for invoice {inv.InvoiceNo} - {inv.Customer}", pay.Reference ?? inv.InvoiceNo, "arpay", pay.Id,
                new List<(Guid, decimal, decimal)> { (into.Id, req.Amount, 0m), (receivable.Id, 0m, req.Amount) }, ct);
            pay.JournalEntryId = entry.Id;
            _db.ArPayments.Add(pay);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "receivable.receive", Detail = $"{inv.InvoiceNo} {req.Amount:0.00} {inv.Currency}" });
            await _db.SaveChangesAsync(ct);
            return await OneAsync(clientId, receiptId, ct);
        }

        public async Task<OpenInvoiceDto> VoidPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct)
        {
            RequireWholeCompany();
            var pay = await _db.ArPayments.FirstOrDefaultAsync(p => p.Id == paymentId && p.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Payment");
            if (pay.IsVoid) throw GatewayException.Conflict("ALREADY_VOID", "That payment is already void.");
            if (pay.JournalEntryId is { } eid) await _books.ReverseJournalAsync(clientId, eid, ct);
            pay.IsVoid = true;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "receivable.void", Detail = $"{pay.Amount:0.00}" });
            await _db.SaveChangesAsync(ct);
            return await OneAsync(clientId, pay.ReceiptId, ct);
        }

        public async Task<ReceivableAgeingDto> AgeingAsync(Guid clientId, string currency, DateTime asAt, CancellationToken ct)
        {
            RequireWholeCompany();
            var cur = (currency ?? "").Trim().ToUpperInvariant();
            if (cur.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency.");
            var at = asAt.Date; var terms = await TermsAsync(clientId, ct);
            var rows = new Dictionary<string, (string Name, decimal[] V)>();
            foreach (var i in (await LoadAsync(clientId, cur, ct)).Where(x => x.Date.Date <= at))
            {
                var owing = i.OnAccount - i.Credited - i.Received(at);
                if (owing <= 0) continue;
                var late = (at - (i.Due ?? i.Date.Date.AddDays(terms)).Date).TotalDays;
                var bucket = late <= 0 ? 0 : late <= 30 ? 1 : late <= 60 ? 2 : late <= 90 ? 3 : 4;
                if (!rows.TryGetValue(i.Key, out var r)) rows[i.Key] = r = (i.Name, new decimal[5]);
                r.V[bucket] += owing;
            }
            ReceivableAgeingRow Row(string key, string name, decimal[] v) => new(key, name, v[0], v[1], v[2], v[3], v[4], v.Sum());
            var list = rows.Select(kv => Row(kv.Key, kv.Value.Name, kv.Value.V)).OrderByDescending(r => r.Total).ThenBy(r => r.Customer).ToList();
            return new ReceivableAgeingDto(cur, at, list, Row("", "Total", new[] { list.Sum(r => r.NotDue), list.Sum(r => r.Days1To30), list.Sum(r => r.Days31To60), list.Sum(r => r.Days61To90), list.Sum(r => r.Over90) }));
        }

        public async Task<CustomerStatementDto> StatementAsync(Guid clientId, string customerKey, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            RequireWholeCompany();
            var cur = (currency ?? "").Trim().ToUpperInvariant();
            if (cur.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency.");
            var f = from.Date; var t = to.Date;
            if (t < f) throw GatewayException.BadRequest("RANGE_INVALID", "The end date is before the start date.");
            await _books.SyncSalesAsync(clientId, ct);
            var recIds = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && a.Role == "receivable").Select(a => a.Id).ToListAsync(ct);
            var moves = await (from l in _db.JournalLines.AsNoTracking() join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                               where l.ClientId == clientId && recIds.Contains(l.AccountId) && e.Source == "sales" && e.SourceId != null && l.Currency == cur
                               select new { e.SourceId, l.Debit, l.Credit }).ToListAsync(ct);
            var ids = moves.Select(m => m.SourceId!.Value).Distinct().ToList();
            var receipts = ids.Count == 0 ? new List<Receipt>() : await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
            var byId = receipts.ToDictionary(r => r.Id);
            // a credit note belongs to the customer of the invoice it credits, even when it was issued without buyer details
            var mine = receipts.Select(r => (R: r, C: Customer(r.Type == ReceiptType.CreditNote && r.OriginalReceiptId is { } o && byId.TryGetValue(o, out var orig) ? orig : r))).Where(x => x.C.Key == customerKey).ToList();
            var docs = (await _db.SalesInvoices.AsNoTracking().Where(d => d.ClientId == clientId && d.Status == "Issued" && d.Currency == cur).ToListAsync(ct))
                .Where(d => KeyOf(d.CustomerName, d.CustomerTin) == customerKey).ToList();
            if (mine.Count == 0 && docs.Count == 0) throw GatewayException.NotFound("Customer");
            var who = mine.Count > 0 ? (Name: mine[0].C.Name, Tin: mine[0].C.Tin) : (Name: docs[0].CustomerName, Tin: docs[0].CustomerTin);
            var raw = new List<(DateTime Date, string Kind, string Ref, string Text, decimal Dr, decimal Cr)>();
            foreach (var d in docs)
                if (d.Kind == "credit_note") raw.Add((d.Date.Date, "credit", d.Number ?? "", "Credit note", 0, d.Total));
                else raw.Add((d.Date.Date, "invoice", d.Number ?? "", d.Kind == "opening" ? "Invoice (brought forward)" : "Invoice", d.Total, 0));
            foreach (var (r, _) in mine)
            {
                var dr = moves.Where(m => m.SourceId == r.Id).Sum(m => m.Debit); var cr = moves.Where(m => m.SourceId == r.Id).Sum(m => m.Credit);
                if (r.Type == ReceiptType.CreditNote) { if (cr > 0) raw.Add((r.ReceiptDate.Date, "credit", r.InvoiceNo, "Credit note", 0, cr)); }
                else if (dr > 0) raw.Add((r.ReceiptDate.Date, "invoice", r.InvoiceNo, r.Type == ReceiptType.DebitNote ? "Debit note" : "Invoice", dr, 0));
            }
            var mineIds = mine.Select(x => x.R.Id).Concat(docs.Where(d => d.Kind != "credit_note").Select(d => d.Id)).ToList();
            var inv = mine.ToDictionary(x => x.R.Id, x => x.R.InvoiceNo);
            foreach (var d in docs.Where(d => d.Kind != "credit_note")) inv[d.Id] = d.Number ?? "";
            foreach (var p in await _db.ArPayments.AsNoTracking().Where(p => p.ClientId == clientId && !p.IsVoid && mineIds.Contains(p.ReceiptId)).ToListAsync(ct))
                raw.Add((p.Date, "payment", p.Reference ?? inv[p.ReceiptId], "Payment received for " + inv[p.ReceiptId], 0, p.Amount));
            var opening = raw.Where(x => x.Date < f).Sum(x => x.Dr - x.Cr);
            var bal = opening; var lines = new List<StatementLine>();
            foreach (var x in raw.Where(x => x.Date >= f && x.Date <= t).OrderBy(x => x.Date).ThenBy(x => x.Kind == "invoice" ? 0 : x.Kind == "credit" ? 1 : 2))
            { bal += x.Dr - x.Cr; lines.Add(new StatementLine(x.Date, x.Kind, x.Ref, x.Text, x.Dr, x.Cr, bal)); }
            return new CustomerStatementDto(who.Name, who.Tin, cur, f, t, opening, lines, bal);
        }
    }
}
