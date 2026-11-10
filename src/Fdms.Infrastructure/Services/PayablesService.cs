using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// What the company owes its suppliers. A bill debits the expense (and VAT input) and credits accounts payable; a payment debits accounts payable and credits the cash or bank account.
    /// Both post to the books as ordinary journal entries, so the trial balance, profit and loss and balance sheet include them without any extra work.
    /// </summary>
    public sealed class PayablesService : IPayablesService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IAccountingService _books;
        public PayablesService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
        }

        private static string? Clean(string? v, int max, string what)
        {
            v = v?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (v.Length > max) throw GatewayException.BadRequest("TOO_LONG", $"{what} can be at most {max} characters.");
            return v;
        }

        private static decimal Paid(IEnumerable<BillPayment> p) => p.Where(x => !x.IsVoid).Sum(x => x.Amount);

        // ---- suppliers --------------------------------------------------------------------------------------------------------

        public async Task<IReadOnlyList<SupplierDto>> ListSuppliersAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            var suppliers = await _db.Suppliers.AsNoTracking().Where(s => s.ClientId == clientId).OrderBy(s => s.Name).ToListAsync(ct);
            var bills = await _db.Bills.AsNoTracking().Where(b => b.ClientId == clientId && !b.IsVoid).ToListAsync(ct);
            var pays = (await _db.BillPayments.AsNoTracking().Where(p => p.ClientId == clientId && !p.IsVoid).ToListAsync(ct)).GroupBy(p => p.BillId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            return suppliers.Select(s =>
            {
                var open = bills.Where(b => b.SupplierId == s.Id && b.Total - pays.GetValueOrDefault(b.Id) > 0).ToList();
                return new SupplierDto(s.Id, s.Name, s.Tin, s.Email, s.Phone, s.Address, s.TermsDays, s.IsActive, open.Count,
                    open.GroupBy(b => b.Currency).OrderBy(g => g.Key).Select(g => new AmountDto(g.Key, g.Sum(b => b.Total - pays.GetValueOrDefault(b.Id)))).ToList());
            }).ToList();
        }

        public async Task<SupplierDto> SaveSupplierAsync(Guid clientId, SaveSupplierRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var name = (req.Name ?? "").Trim();
            if (name.Length is < 2 or > 120) throw GatewayException.BadRequest("NAME_INVALID", "Give the supplier a name (2-120 characters).");
            if (req.TermsDays is < 0 or > 365) throw GatewayException.BadRequest("TERMS_INVALID", "Payment terms are between 0 and 365 days.");
            var email = Clean(req.Email, 120, "The email");
            if (email != null && !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw GatewayException.BadRequest("EMAIL_INVALID", "That email address does not look right.");
            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            if (await _db.Suppliers.AnyAsync(s => s.ClientId == clientId && s.Name == name && s.Id != id, ct)) throw GatewayException.Conflict("SUPPLIER_EXISTS", "You already have a supplier with that name.");
            var sup = id == null ? new Supplier { ClientId = clientId } : await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == id && s.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Supplier");
            sup.Name = name; sup.Tin = Clean(req.Tin, 20, "The TIN"); sup.Email = email; sup.Phone = Clean(req.Phone, 40, "The phone"); sup.Address = Clean(req.Address, 300, "The address");
            sup.TermsDays = req.TermsDays; sup.IsActive = req.IsActive;
            if (id == null) _db.Suppliers.Add(sup);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = id == null ? "supplier.create" : "supplier.update", Detail = name });
            await _db.SaveChangesAsync(ct);
            return (await ListSuppliersAsync(clientId, ct)).First(s => s.Id == sup.Id);
        }

        // ---- bills ------------------------------------------------------------------------------------------------------------

        private async Task<List<BillDto>> MapAsync(Guid clientId, List<Bill> bills, CancellationToken ct)
        {
            if (bills.Count == 0) return new();
            var ids = bills.Select(b => b.Id).ToList();
            var pays = await _db.BillPayments.AsNoTracking().Where(p => p.ClientId == clientId && ids.Contains(p.BillId)).OrderBy(p => p.Date).ThenBy(p => p.CreatedAt).ToListAsync(ct);
            var sups = await _db.Suppliers.AsNoTracking().Where(s => s.ClientId == clientId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            var accIds = bills.Select(b => b.ExpenseAccountId).Concat(pays.Select(p => p.AccountId)).Distinct().ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => accIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code + " " + a.Name, ct);
            var entries = bills.Where(b => b.JournalEntryId != null).Select(b => b.JournalEntryId!.Value).ToList();
            var numbers = await _db.JournalEntries.AsNoTracking().Where(e => entries.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
            var today = DateTime.UtcNow.AddHours(2).Date;
            return bills.Select(b =>
            {
                var mine = pays.Where(p => p.BillId == b.Id).ToList(); var paid = Paid(mine); var outstanding = b.IsVoid ? 0 : b.Total - paid;
                var status = b.IsVoid ? "Void" : outstanding <= 0 ? "Paid" : paid > 0 ? "Part paid" : "Unpaid";
                return new BillDto(b.Id, b.SupplierId, sups.GetValueOrDefault(b.SupplierId, ""), b.Number, b.Date, b.DueDate, b.Currency, b.Memo, b.Subtotal, b.Vat, b.Total, paid, outstanding, status,
                    outstanding > 0 && b.DueDate.Date < today ? (int)(today - b.DueDate.Date).TotalDays : 0, b.ExpenseAccountId, accs.GetValueOrDefault(b.ExpenseAccountId, ""),
                    b.JournalEntryId == null ? null : numbers.GetValueOrDefault(b.JournalEntryId.Value), mine.Select(p => new BillPaymentDto(p.Id, p.Date, p.Amount, p.AccountId, accs.GetValueOrDefault(p.AccountId, ""), p.Reference, p.IsVoid)).ToList());
            }).ToList();
        }

        public async Task<IReadOnlyList<BillDto>> ListBillsAsync(Guid clientId, BillFilter f, CancellationToken ct)
        {
            RequireWholeCompany();
            var q = _db.Bills.AsNoTracking().Where(b => b.ClientId == clientId);
            if (f.SupplierId != null) q = q.Where(b => b.SupplierId == f.SupplierId);
            if (!string.IsNullOrWhiteSpace(f.Currency)) q = q.Where(b => b.Currency == f.Currency!.ToUpper());
            if (!string.IsNullOrWhiteSpace(f.Q)) { var s = f.Q.Trim(); q = q.Where(b => b.Number.Contains(s) || (b.Memo != null && b.Memo.Contains(s))); }
            var rows = await q.OrderByDescending(b => b.Date).ThenByDescending(b => b.CreatedAt).Take(Math.Clamp(f.Take, 1, 500)).ToListAsync(ct);
            var list = await MapAsync(clientId, rows, ct);
            return string.IsNullOrWhiteSpace(f.Status) ? list : f.Status.ToLowerInvariant() switch
            {
                "open" => list.Where(b => b.Outstanding > 0).ToList(), "overdue" => list.Where(b => b.DaysOverdue > 0).ToList(), "paid" => list.Where(b => b.Status == "Paid").ToList(), "void" => list.Where(b => b.Status == "Void").ToList(), _ => list,
            };
        }

        private async Task<BillDto> OneAsync(Guid clientId, Guid id, CancellationToken ct) =>
            (await MapAsync(clientId, await _db.Bills.AsNoTracking().Where(b => b.Id == id && b.ClientId == clientId).ToListAsync(ct), ct)).FirstOrDefault() ?? throw GatewayException.NotFound("Bill");

        /// <summary>The account the system uses for a role (found by role, so a renumbered chart still works; the standard code for older charts).</summary>
        private async Task<LedgerAccount> ByRoleAsync(Guid clientId, string role, string code, string what, CancellationToken ct) =>
            await ChartCodes.ByRoleAsync(_db, clientId, role, code, ct) is { IsActive: true } a ? a
            : throw GatewayException.Conflict("ACCOUNT_MISSING", $"The {what} account is missing or switched off in your chart of accounts.");

        public async Task<BillDto> CreateBillAsync(Guid clientId, CreateBillRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await _books.EnsureChartAsync(clientId, ct);
            var sup = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.SupplierId && s.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Supplier");
            if (!sup.IsActive) throw GatewayException.BadRequest("SUPPLIER_INACTIVE", "That supplier is switched off.");
            var number = (req.Number ?? "").Trim();
            if (number.Length is < 1 or > 50) throw GatewayException.BadRequest("NUMBER_INVALID", "Enter the supplier's invoice number (up to 50 characters).");
            if (req.Date.Year < 2000 || req.Date > DateTime.UtcNow.AddDays(31)) throw GatewayException.BadRequest("DATE_INVALID", "That bill date is not valid.");
            var due = (req.DueDate ?? req.Date.AddDays(sup.TermsDays)).Date;
            if (due < req.Date.Date) throw GatewayException.BadRequest("DATE_INVALID", "The due date is before the bill date.");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (req.Subtotal <= 0 || req.Vat < 0 || req.Vat > req.Subtotal || req.Subtotal != Math.Round(req.Subtotal, 2) || req.Vat != Math.Round(req.Vat, 2) || req.Subtotal > 1_000_000_000m)
                throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter the amount before VAT (above zero) and the VAT (not more than the amount), with at most two decimals.");
            if (await _db.Bills.AnyAsync(b => b.ClientId == clientId && b.SupplierId == sup.Id && b.Number == number && !b.IsVoid, ct)) throw GatewayException.Conflict("BILL_EXISTS", "That supplier invoice number has already been entered.");
            var expense = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.ExpenseAccountId && a.ClientId == clientId, ct) ?? throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose the account this bill is for.");
            if (!expense.IsActive || expense.Type is not ("Expense" or "Asset")) throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose an expense (or asset) account for the purchase.");
            var payable = await ByRoleAsync(clientId, "payable", "2010", "accounts payable", ct);
            var vatInput = req.Vat > 0 ? await ByRoleAsync(clientId, "vat_input", "1310", "VAT input", ct) : null;
            var total = req.Subtotal + req.Vat;

            var bill = new Bill { ClientId = clientId, SupplierId = sup.Id, Number = number, Date = req.Date.Date, DueDate = due, Currency = cur, Memo = Clean(req.Memo, 200, "The note"), Subtotal = req.Subtotal, Vat = req.Vat, Total = total, ExpenseAccountId = expense.Id, CostCentreId = req.CostCentreId, CreatedBy = _scope.UserName };
            await _books.CheckCentresAsync(clientId, new[] { req.CostCentreId }, ct);
            var lines = new List<(Guid, decimal, decimal)> { (expense.Id, req.Subtotal, 0m) };
            if (vatInput != null) lines.Add((vatInput.Id, req.Vat, 0m));
            lines.Add((payable.Id, 0m, total));
            var entry = await _books.PostEntryAsync(clientId, bill.Date, cur, $"Bill {number} - {sup.Name}", number, "bill", bill.Id, lines, ct,
                req.CostCentreId is { } cc ? (req.ExpenseAccountId, cc) : null);
            bill.JournalEntryId = entry.Id;
            _db.Bills.Add(bill);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bill.create", Detail = $"{sup.Name} {number} {total:0.00} {cur}" });
            await _db.SaveChangesAsync(ct);
            return await OneAsync(clientId, bill.Id, ct);
        }

        public async Task<BillDto> VoidBillAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            RequireWholeCompany();
            var bill = await _db.Bills.FirstOrDefaultAsync(b => b.Id == id && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Bill");
            if (bill.IsVoid) throw GatewayException.Conflict("ALREADY_VOID", "That bill is already void.");
            if (await _db.StockDocuments.AnyAsync(d => d.BillId == id && d.Status == "Posted", ct)) throw GatewayException.Conflict("FROM_A_GRV", "This bill came from a GRV: void the GRV under Inventory, which voids the bill and takes the stock back out.");
            if (await _db.BillPayments.AnyAsync(p => p.BillId == id && !p.IsVoid, ct)) throw GatewayException.Conflict("HAS_PAYMENTS", "Void the payments first, then the bill.");
            if (bill.JournalEntryId is { } eid) await _books.ReverseJournalAsync(clientId, eid, ct);
            bill.IsVoid = true;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bill.void", Detail = bill.Number });
            await _db.SaveChangesAsync(ct);
            return await OneAsync(clientId, id, ct);
        }

        public async Task<BillDto> PayBillAsync(Guid clientId, Guid id, PayBillRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var bill = await _db.Bills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Bill");
            if (bill.IsVoid) throw GatewayException.Conflict("BILL_VOID", "That bill is void.");
            var outstanding = bill.Total - Paid(await _db.BillPayments.AsNoTracking().Where(p => p.BillId == id).ToListAsync(ct));
            if (req.Amount <= 0 || req.Amount != Math.Round(req.Amount, 2)) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter an amount above zero with at most two decimals.");
            if (req.Amount > outstanding) throw GatewayException.Conflict("OVERPAYMENT", $"Only {outstanding:N2} {bill.Currency} is still owing on this bill.");
            if (req.Date.Year < 2000 || req.Date > DateTime.UtcNow.AddDays(2)) throw GatewayException.BadRequest("DATE_INVALID", "That payment date is not valid.");
            var from = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.AccountId && a.ClientId == clientId, ct) ?? throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose the cash or bank account it was paid from.");
            if (from.Type != "Asset" || !from.IsActive) throw GatewayException.BadRequest("ACCOUNT_INVALID", "Pay from a cash, bank or wallet account.");
            var payable = await ByRoleAsync(clientId, "payable", "2010", "accounts payable", ct);
            var sup = await _db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == bill.SupplierId, ct);
            var pay = new BillPayment { ClientId = clientId, BillId = id, Date = req.Date.Date, Amount = req.Amount, AccountId = from.Id, Reference = Clean(req.Reference, 50, "The reference") };
            var entry = await _books.PostEntryAsync(clientId, pay.Date, bill.Currency, $"Payment of bill {bill.Number} - {sup.Name}", pay.Reference ?? bill.Number, "billpay", pay.Id,
                new List<(Guid, decimal, decimal)> { (payable.Id, req.Amount, 0m), (from.Id, 0m, req.Amount) }, ct);
            pay.JournalEntryId = entry.Id;
            _db.BillPayments.Add(pay);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bill.pay", Detail = $"{sup.Name} {bill.Number} {req.Amount:0.00} {bill.Currency}" });
            await _db.SaveChangesAsync(ct);
            return await OneAsync(clientId, id, ct);
        }

        public async Task<BillDto> VoidPaymentAsync(Guid clientId, Guid paymentId, CancellationToken ct)
        {
            RequireWholeCompany();
            var pay = await _db.BillPayments.FirstOrDefaultAsync(p => p.Id == paymentId && p.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Payment");
            if (pay.IsVoid) throw GatewayException.Conflict("ALREADY_VOID", "That payment is already void.");
            if (pay.JournalEntryId is { } eid) await _books.ReverseJournalAsync(clientId, eid, ct);
            pay.IsVoid = true;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bill.payment.void", Detail = $"{pay.Amount:0.00}" });
            await _db.SaveChangesAsync(ct);
            return await OneAsync(clientId, pay.BillId, ct);
        }

        // ---- ageing -----------------------------------------------------------------------------------------------------------

        public async Task<SupplierStatementDto> StatementAsync(Guid clientId, Guid supplierId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            RequireWholeCompany();
            var cur = (currency ?? "").Trim().ToUpperInvariant();
            if (cur.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency.");
            var f = from.Date; var t = to.Date;
            if (t < f) throw GatewayException.BadRequest("RANGE_INVALID", "The end date is before the start date.");
            var sup = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId && s.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Supplier");
            var bills = await _db.Bills.AsNoTracking().Where(b => b.ClientId == clientId && b.SupplierId == supplierId && b.Currency == cur && !b.IsVoid).ToListAsync(ct);
            var billIds = bills.Select(b => b.Id).ToList();
            var number = bills.ToDictionary(b => b.Id, b => b.Number);
            var payments = await _db.BillPayments.AsNoTracking().Where(p => p.ClientId == clientId && !p.IsVoid && billIds.Contains(p.BillId)).ToListAsync(ct);
            // Debit = paid to the supplier, Credit = billed by the supplier; the running balance is what is owed
            var raw = new List<(DateTime Date, int Order, string Kind, string Ref, string Text, decimal Dr, decimal Cr)>();
            foreach (var b in bills) raw.Add((b.Date.Date, 0, "bill", b.Number, string.IsNullOrWhiteSpace(b.Memo) ? "Bill" : b.Memo!, 0, b.Total));
            foreach (var p in payments) raw.Add((p.Date.Date, 1, "payment", p.Reference ?? number[p.BillId], "Payment for bill " + number[p.BillId], p.Amount, 0));
            var opening = raw.Where(x => x.Date < f).Sum(x => x.Cr - x.Dr);
            var bal = opening; var lines = new List<StatementLine>();
            foreach (var x in raw.Where(x => x.Date >= f && x.Date <= t).OrderBy(x => x.Date).ThenBy(x => x.Order))
            { bal += x.Cr - x.Dr; lines.Add(new StatementLine(x.Date, x.Kind, x.Ref, x.Text, x.Dr, x.Cr, bal)); }
            return new SupplierStatementDto(sup.Name, sup.Tin, cur, f, t, opening, lines, bal);
        }

        public async Task<AgeingDto> AgeingAsync(Guid clientId, string currency, DateTime asAt, CancellationToken ct)
        {
            RequireWholeCompany();
            var cur = (currency ?? "").Trim().ToUpperInvariant();
            if (cur.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency.");
            var at = asAt.Date;
            var bills = await _db.Bills.AsNoTracking().Where(b => b.ClientId == clientId && !b.IsVoid && b.Currency == cur && b.Date <= at).ToListAsync(ct);
            var ids = bills.Select(b => b.Id).ToList();
            var pays = (await _db.BillPayments.AsNoTracking().Where(p => p.ClientId == clientId && !p.IsVoid && ids.Contains(p.BillId) && p.Date <= at).ToListAsync(ct)).GroupBy(p => p.BillId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            var names = await _db.Suppliers.AsNoTracking().Where(s => s.ClientId == clientId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            var rows = new Dictionary<Guid, decimal[]>();
            foreach (var b in bills)
            {
                var owing = b.Total - pays.GetValueOrDefault(b.Id);
                if (owing <= 0) continue;
                var late = (at - b.DueDate.Date).TotalDays;
                var bucket = late <= 0 ? 0 : late <= 30 ? 1 : late <= 60 ? 2 : late <= 90 ? 3 : 4;
                (rows.TryGetValue(b.SupplierId, out var r) ? r : rows[b.SupplierId] = new decimal[5])[bucket] += owing;
            }
            AgeingRow Row(Guid id, string name, decimal[] v) => new(id, name, v[0], v[1], v[2], v[3], v[4], v.Sum());
            var list = rows.Select(kv => Row(kv.Key, names.GetValueOrDefault(kv.Key, ""), kv.Value)).OrderByDescending(r => r.Total).ThenBy(r => r.Supplier).ToList();
            var totals = Row(Guid.Empty, "Total", new[] { list.Sum(r => r.NotDue), list.Sum(r => r.Days1To30), list.Sum(r => r.Days31To60), list.Sum(r => r.Days61To90), list.Sum(r => r.Over90) });
            return new AgeingDto(cur, at, list, totals);
        }
    }
}
