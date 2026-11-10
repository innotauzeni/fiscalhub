using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The VAT return worked out from the books: VAT collected on sales (less credit notes), VAT paid on purchases (from bills), and what is therefore payable. Settling it posts one entry that clears
    /// both VAT accounts against the cash or bank account that paid (or received) the difference.
    /// </summary>
    public sealed class VatBooksService : IVatBooksService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IAccountingService _books;
        public VatBooksService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
        }

        private static string Cur(string? c)
        {
            var x = (c ?? "").Trim().ToUpperInvariant();
            if (x.Length != 3 || !x.All(char.IsLetter)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency.");
            return x;
        }

        private async Task<(List<Guid> Output, Guid? Input, List<Guid> Sales, List<Guid> Returns)> AccountsAsync(Guid clientId, CancellationToken ct)
        {
            var all = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).ToListAsync(ct);
            return (all.Where(a => a.Role == "vat_output").Select(a => a.Id).ToList(), (all.FirstOrDefault(a => a.Role == "vat_input" && a.BankAccountId == null) ?? all.FirstOrDefault(a => a.Code == "1310"))?.Id,
                all.Where(a => a.Role == "sales").Select(a => a.Id).ToList(), all.Where(a => a.Role == "sales_returns").Select(a => a.Id).ToList());
        }

        public async Task<BooksVatReturnDto> ReturnAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            RequireWholeCompany();
            var cur = Cur(currency); var f = from.Date; var t = to.Date;
            if (t < f) throw GatewayException.BadRequest("RANGE_INVALID", "The end date is before the start date.");
            await _books.SyncSalesAsync(clientId, ct);
            var (output, input, sales, returns) = await AccountsAsync(clientId, ct);

            // movements in the period, without the settlement entries (those pay the return, they are not part of it)
            var moves = await (from l in _db.JournalLines.AsNoTracking() join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                               where l.ClientId == clientId && l.Currency == cur && l.Date >= f && l.Date <= t && e.Source != "vat"
                               group l by l.AccountId into g select new { Id = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToListAsync(ct);
            decimal Dr(IEnumerable<Guid> ids) => moves.Where(m => ids.Contains(m.Id)).Sum(m => m.Debit);
            decimal Cr(IEnumerable<Guid> ids) => moves.Where(m => ids.Contains(m.Id)).Sum(m => m.Credit);
            var taxable = Cr(sales) - Dr(sales); var returned = Dr(returns) - Cr(returns);
            var outputVat = Cr(output); var creditVat = Dr(output);
            var inputIds = input == null ? new List<Guid>() : new List<Guid> { input.Value };
            var inputVat = Dr(inputIds) - Cr(inputIds);

            var bills = await (from b in _db.Bills.AsNoTracking() join s in _db.Suppliers.AsNoTracking() on b.SupplierId equals s.Id
                               where b.ClientId == clientId && !b.IsVoid && b.Currency == cur && b.Date >= f && b.Date <= t orderby b.Date select new VatBillRow(s.Name, b.Number, b.Date, b.Subtotal, b.Vat)).ToListAsync(ct);
            var purchases = bills.Sum(b => b.Net);

            var (outBal, inBal) = await BalancesAsync(clientId, cur, t, output, input, ct);
            var net = outputVat - creditVat;
            var lines = new List<VatReturnLine>
            {
                new("Sales before VAT", taxable, "Sales account"), new("Less: sales returned (credit notes)", returned, null), new("Net sales", taxable - returned, null),
                new("VAT charged on sales", outputVat, null), new("Less: VAT on credit notes", creditVat, null), new("Net output VAT", net, null),
                new("Purchases before VAT", purchases, "From supplier bills"), new("Input VAT (recoverable)", inputVat, null), new(net - inputVat >= 0 ? "VAT payable to ZIMRA" : "VAT refundable", net - inputVat, null),
            };
            return new BooksVatReturnDto(cur, f, t, taxable, returned, taxable - returned, outputVat, creditVat, net, purchases, inputVat, net - inputVat, outBal, inBal, outBal - inBal, lines, bills);
        }

        private async Task<(decimal Out, decimal In)> BalancesAsync(Guid clientId, string cur, DateTime asAt, List<Guid> output, Guid? input, CancellationToken ct)
        {
            var sums = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Currency == cur && l.Date <= asAt).GroupBy(l => l.AccountId).Select(g => new { Id = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToListAsync(ct);
            var o = sums.Where(s => output.Contains(s.Id)).Sum(s => s.Credit - s.Debit);
            var i = input == null ? 0m : sums.Where(s => s.Id == input.Value).Sum(s => s.Debit - s.Credit);
            return (o, i);
        }

        public async Task<JournalDto> SettleAsync(Guid clientId, SettleVatRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var cur = Cur(req.Currency); var date = req.Date.Date;
            if (date.Year < 2000 || date > DateTime.UtcNow.AddHours(2).Date) throw GatewayException.BadRequest("DATE_INVALID", "Settle VAT on or before today.");
            await _books.SyncSalesAsync(clientId, ct);
            var (output, input, _, _) = await AccountsAsync(clientId, ct);
            if (output.Count == 0) throw GatewayException.Conflict("ACCOUNT_MISSING", "The VAT output account is missing from your chart of accounts.");
            var (outBal, inBal) = await BalancesAsync(clientId, cur, date, output, input, ct);
            if (outBal == 0 && inBal == 0) throw GatewayException.Conflict("NOTHING_TO_SETTLE", "There is no VAT held in this currency up to that date.");
            var net = outBal - inBal;
            Guid? bank = null;
            if (net != 0)
            {
                var acc = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == req.AccountId && a.ClientId == clientId && a.IsActive && a.Type == "Asset", ct)
                    ?? throw GatewayException.BadRequest("ACCOUNT_INVALID", net > 0 ? "Choose the cash or bank account the VAT was paid from." : "Choose the cash or bank account the refund went into.");
                bank = acc.Id;
            }
            if (inBal != 0 && input == null) throw GatewayException.Conflict("ACCOUNT_MISSING", "The VAT input account (1310) is missing from your chart of accounts.");
            // signed debits: output cleared, input cleared, the difference to or from the bank; they add up to zero
            var signed = new List<(Guid Account, decimal Debit)> { (output[0], outBal) };
            if (inBal != 0) signed.Add((input!.Value, -inBal));
            if (bank != null) signed.Add((bank.Value, -net));
            var lines = signed.Where(s => s.Debit != 0).Select(s => (s.Account, s.Debit > 0 ? s.Debit : 0m, s.Debit < 0 ? -s.Debit : 0m)).ToList();
            var memo = net > 0 ? $"VAT paid to ZIMRA (up to {date:yyyy-MM-dd})" : net < 0 ? $"VAT refund from ZIMRA (up to {date:yyyy-MM-dd})" : $"VAT set off (up to {date:yyyy-MM-dd})";
            return await _books.PostEntryAsync(clientId, date, cur, memo, "VAT-" + date.ToString("yyyy-MM"), "vat", null, lines, ct);
        }
    }

    /// <summary>Budgets for a year and how the books are doing against them, account by account, month by month.</summary>
    public sealed class BudgetService : IBudgetService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IAccountingService _books;
        public BudgetService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
        }

        private async Task<List<BudgetDto>> MapAsync(Guid clientId, List<Budget> budgets, CancellationToken ct)
        {
            var ids = budgets.Select(b => b.Id).ToList();
            var lines = await _db.BudgetLines.AsNoTracking().Where(l => l.ClientId == clientId && ids.Contains(l.BudgetId)).ToListAsync(ct);
            var accIds = lines.Select(l => l.AccountId).Distinct().ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => accIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            return budgets.Select(b =>
            {
                var mine = lines.Where(l => l.BudgetId == b.Id).GroupBy(l => l.AccountId).Select(g =>
                {
                    var a = accs.GetValueOrDefault(g.Key); var months = new decimal[12];
                    foreach (var l in g) months[l.Month - 1] = l.Amount;
                    return new BudgetAccountDto(g.Key, a?.Code ?? "", a?.Name ?? "", a?.Type ?? "", months);
                }).OrderBy(a => a.Type == "Income" ? 0 : 1).ThenBy(a => a.Code).ToList();
                return new BudgetDto(b.Id, b.Name, b.Year, b.Currency, mine.Where(a => a.Type == "Income").Sum(a => a.Months.Sum()), mine.Where(a => a.Type == "Expense").Sum(a => a.Months.Sum()), mine);
            }).ToList();
        }

        public async Task<IReadOnlyList<BudgetDto>> ListAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            return await MapAsync(clientId, await _db.Budgets.AsNoTracking().Where(b => b.ClientId == clientId).OrderByDescending(b => b.Year).ThenBy(b => b.Name).ToListAsync(ct), ct);
        }

        public async Task<BudgetDto> SaveAsync(Guid clientId, SaveBudgetRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await _books.EnsureChartAsync(clientId, ct);
            var name = (req.Name ?? "").Trim();
            if (name.Length is < 2 or > 100) throw GatewayException.BadRequest("NAME_INVALID", "Give the budget a name (2-100 characters).");
            if (req.Year is < 2000 or > 2100) throw GatewayException.BadRequest("YEAR_INVALID", "Choose a year between 2000 and 2100.");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            var input = req.Lines ?? Array.Empty<BudgetLineInput>();
            if (input.Count == 0 || input.Count > 200) throw GatewayException.BadRequest("LINES_INVALID", "A budget needs at least one account (and at most 200).");
            if (input.Select(l => l.AccountId).Distinct().Count() != input.Count) throw GatewayException.BadRequest("LINES_INVALID", "An account can appear only once in a budget.");
            foreach (var l in input)
                if (l.Months == null || l.Months.Count != 12 || l.Months.Any(m => m < 0 || m > 1_000_000_000m || m != Math.Round(m, 2))) throw GatewayException.BadRequest("AMOUNT_INVALID", "Each account needs twelve monthly amounts (zero or more, at most two decimals).");
            var ids = input.Select(l => l.AccountId).ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && ids.Contains(a.Id)).ToListAsync(ct);
            if (accs.Count != ids.Count || accs.Any(a => a.Type is not ("Income" or "Expense"))) throw GatewayException.BadRequest("ACCOUNT_INVALID", "A budget covers income and expense accounts from your chart of accounts.");

            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            if (await _db.Budgets.AnyAsync(b => b.ClientId == clientId && b.Name == name && b.Year == req.Year && b.Id != id, ct)) throw GatewayException.Conflict("BUDGET_EXISTS", "You already have a budget with that name for that year.");
            var budget = id == null ? new Budget { ClientId = clientId, CreatedBy = _scope.UserName } : await _db.Budgets.FirstOrDefaultAsync(b => b.Id == id && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Budget");
            budget.Name = name; budget.Year = req.Year; budget.Currency = cur;
            if (id == null) _db.Budgets.Add(budget);
            else _db.BudgetLines.RemoveRange(await _db.BudgetLines.Where(l => l.BudgetId == budget.Id).ToListAsync(ct));
            foreach (var l in input)
                for (var m = 0; m < 12; m++)
                    if (l.Months[m] > 0) _db.BudgetLines.Add(new BudgetLine { BudgetId = budget.Id, ClientId = clientId, AccountId = l.AccountId, Month = m + 1, Amount = l.Months[m] });
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = id == null ? "budget.create" : "budget.update", Detail = $"{name} {req.Year}" });
            await _db.SaveChangesAsync(ct);
            return (await MapAsync(clientId, new List<Budget> { budget }, ct))[0];
        }

        public async Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            RequireWholeCompany();
            var b = await _db.Budgets.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Budget");
            _db.BudgetLines.RemoveRange(await _db.BudgetLines.Where(l => l.BudgetId == id).ToListAsync(ct));
            _db.Budgets.Remove(b);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "budget.delete", Detail = b.Name });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<BudgetVsActualDto> VsActualAsync(Guid clientId, Guid id, int fromMonth, int toMonth, CancellationToken ct)
        {
            RequireWholeCompany();
            if (fromMonth is < 1 or > 12 || toMonth is < 1 or > 12 || toMonth < fromMonth) throw GatewayException.BadRequest("RANGE_INVALID", "Choose months from 1 to 12, the last not before the first.");
            var b = await _db.Budgets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Budget");
            await _books.SyncSalesAsync(clientId, ct);
            var from = new DateTime(b.Year, fromMonth, 1); var to = new DateTime(b.Year, toMonth, 1).AddMonths(1).AddDays(-1);
            var planned = (await _db.BudgetLines.AsNoTracking().Where(l => l.BudgetId == id && l.Month >= fromMonth && l.Month <= toMonth).ToListAsync(ct)).GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            var actual = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Currency == b.Currency && l.Date >= from && l.Date <= to).GroupBy(l => l.AccountId).Select(g => new { Id = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToListAsync(ct);
            var budgeted = (await _db.BudgetLines.AsNoTracking().Where(l => l.BudgetId == id).Select(l => l.AccountId).Distinct().ToListAsync(ct));
            var ids = planned.Keys.Concat(actual.Select(a => a.Id)).Concat(budgeted).Distinct().ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && ids.Contains(a.Id) && (a.Type == "Income" || a.Type == "Expense")).ToListAsync(ct);

            VarianceSection Section(string title, string type)
            {
                var rows = new List<VarianceRow>();
                foreach (var a in accs.Where(x => x.Type == type).OrderBy(x => x.Code))
                {
                    var plan = planned.GetValueOrDefault(a.Id); var sums = actual.FirstOrDefault(x => x.Id == a.Id);
                    var act = sums == null ? 0m : type == "Income" ? sums.Credit - sums.Debit : sums.Debit - sums.Credit;
                    if (plan == 0 && act == 0) continue;
                    var variance = type == "Income" ? act - plan : plan - act;   // positive is good: more income, or less spent
                    rows.Add(new VarianceRow(a.Id, a.Code, a.Name, plan, act, variance, plan == 0 ? null : Math.Round(variance / plan * 100m, 1)));
                }
                var bt = rows.Sum(r => r.Budget); var at = rows.Sum(r => r.Actual);
                return new VarianceSection(title, rows, bt, at, type == "Income" ? at - bt : bt - at);
            }
            var income = Section("Income", "Income"); var expenses = Section("Expenses", "Expense");
            return new BudgetVsActualDto(b.Id, b.Name, b.Year, b.Currency, fromMonth, toMonth, new[] { income, expenses }, income.Budget - expenses.Budget, income.Actual - expenses.Actual, (income.Actual - expenses.Actual) - (income.Budget - expenses.Budget));
        }
    }
}
