using System.Globalization;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The books as reports: general ledger, trial balance, profit and loss, balance sheet and cashbook. Every figure comes straight from the journal lines, per currency.
    /// Like any accounting system, income and expense accounts start each financial year at nil: earlier years' profit is shown in retained earnings, and this year's as
    /// current year earnings. Sub-accounts roll up into their parents; statements are classified by each account's sub-type.
    /// </summary>
    public sealed partial class AccountingReports : IAccountingReports
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IAccountingService _books;
        public AccountingReports(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private async Task PrepareAsync(Guid clientId, CancellationToken ct)
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot see the company's books.");
            await _books.SyncSalesAsync(clientId, ct);   // the books are always up to date with the receipts that were confirmed
        }

        private static string Cur(string? c)
        {
            var x = (c ?? "").Trim().ToUpperInvariant();
            if (x.Length != 3 || !x.All(char.IsLetter)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency.");
            return x;
        }

        private static (DateTime From, DateTime To) Range(DateTime from, DateTime to)
        {
            var f = from.Date; var t = to.Date;
            if (t < f) throw GatewayException.BadRequest("RANGE_INVALID", "The end date is before the start date.");
            return (f, t);
        }

        private static bool ProfitAndLoss(string type) => type is "Income" or "Expense";

        public async Task<AccountingMeta> MetaAsync(Guid clientId, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var byCur = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId).GroupBy(l => l.Currency).Select(g => new { Cur = g.Key, N = g.Count(), First = g.Min(x => x.Date) }).ToListAsync(ct);
            var fromBanks = await _db.BankAccounts.AsNoTracking().Where(b => b.ClientId == clientId).Select(b => b.CurrencyCode).Distinct().ToListAsync(ct);
            var currencies = byCur.Select(x => x.Cur).Concat(fromBanks).Concat(new[] { "USD" }).Distinct().OrderBy(c => c).ToList();
            var cash = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && a.IsActive && !a.IsGroup && a.Type == "Asset" && ((a.Role != null && ChartCodes.CashRoles.Contains(a.Role)) || a.BankAccountId != null)).OrderBy(a => a.Code).ToListAsync(ct);
            var first = byCur.Count == 0 ? DateTime.UtcNow.AddHours(2).Date : byCur.Min(x => x.First);
            return new AccountingMeta(currencies, byCur.OrderByDescending(x => x.N).Select(x => x.Cur).FirstOrDefault() ?? "USD", cash.Select(a => AccountingService.ToDto(a, true)).ToList(), first);
        }

        private async Task<List<LedgerAccount>> AccountsAsync(Guid clientId, CancellationToken ct) =>
            await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).ToListAsync(ct);

        /// <summary>Debits and credits per account for lines dated in [from, to] (from null: since the start).</summary>
        private async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>> SumsAsync(Guid clientId, string cur, DateTime? from, DateTime to, CancellationToken ct, Guid? centre = null)
        {
            if (await FxSumsAsync(clientId, cur, from, to, centre, ct) is { } converted) return converted;
            var q = _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Currency == cur && l.Date <= to);
            if (from != null) q = q.Where(l => l.Date >= from);
            if (centre == Guid.Empty) q = q.Where(l => l.CostCentreId == null);
            else if (centre != null) q = q.Where(l => l.CostCentreId == centre);
            return (await q.GroupBy(l => l.AccountId).Select(g => new { g.Key, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => (x.D, x.C));
        }

        /// <summary>
        /// Each account's balance as at a date, debit-positive: balance sheet accounts since the start, income and expenses since the start of that financial year.
        /// Earlier years' profit (debit-positive, so a profit is negative) comes back on its own, to be shown in retained earnings.
        /// </summary>
        private async Task<(Dictionary<Guid, decimal> Balance, decimal PriorYears)> PositionAsync(Guid clientId, string cur, List<LedgerAccount> accounts, DateTime asAt, DateTime yearStart, CancellationToken ct)
        {
            var all = await SumsAsync(clientId, cur, null, asAt, ct);
            var thisYear = await SumsAsync(clientId, cur, yearStart, asAt, ct);
            var byId = accounts.ToDictionary(a => a.Id);
            var balance = new Dictionary<Guid, decimal>(); var prior = 0m;
            foreach (var (id, (d, c)) in all)
            {
                if (!byId.TryGetValue(id, out var a)) continue;
                if (ProfitAndLoss(a.Type))
                {
                    var (yd, yc) = thisYear.GetValueOrDefault(id);
                    balance[id] = yd - yc;
                    prior += (d - c) - (yd - yc);
                }
                else balance[id] = d - c;
            }
            return (balance, prior);
        }

        private static LedgerAccount? Retained(List<LedgerAccount> accounts) =>
            accounts.Where(a => a.Role == "retained" && !a.IsGroup).OrderByDescending(a => a.IsSystem).ThenBy(a => a.Code).FirstOrDefault();

        // ---- general ledger ------------------------------------------------------------------------------------------------------

        public async Task<LedgerReportDto> LedgerAsync(Guid clientId, Guid accountId, string currency, DateTime from, DateTime to, CancellationToken ct, bool includeSubAccounts = false, Guid? costCentreId = null)
        {
            await PrepareAsync(clientId, ct);
            var cur = Cur(currency); var (f, t) = Range(from, to);
            var accounts = await AccountsAsync(clientId, ct);
            var acc = accounts.FirstOrDefault(a => a.Id == accountId) ?? throw GatewayException.NotFound("Account");
            var ids = includeSubAccounts ? ChartCodes.WithDescendants(accounts, accountId).ToList() : new List<Guid> { accountId };
            var byId = accounts.ToDictionary(a => a.Id);
            var sign = ChartCodes.IsDebitNormal(acc.Type) ? 1m : -1m;
            // income and expenses open each financial year at nil, as in the trial balance
            var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
            var openFrom = ProfitAndLoss(acc.Type) ? ChartCodes.YearStart(f, rules.FiscalYearStartMonth) : DateTime.MinValue;
            var cc = costCentreId; var unassigned = cc == Guid.Empty;
            var before = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && ids.Contains(l.AccountId) && l.Currency == cur && l.Date < f && l.Date >= openFrom
                    && (cc == null || (unassigned ? l.CostCentreId == null : l.CostCentreId == cc)))
                .GroupBy(l => 1).Select(g => new { D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).FirstOrDefaultAsync(ct);
            var opening = sign * ((before?.D ?? 0) - (before?.C ?? 0));
            var rows = await (from l in _db.JournalLines.AsNoTracking() join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                              where l.ClientId == clientId && ids.Contains(l.AccountId) && l.Currency == cur && l.Date >= f && l.Date <= t
                                    && (cc == null || (unassigned ? l.CostCentreId == null : l.CostCentreId == cc))
                              orderby l.Date, e.Seq select new { e.Id, l.AccountId, l.Date, e.Number, e.Memo, e.Reference, l.Description, l.Debit, l.Credit, e.Source, e.SourceId }).ToListAsync(ct);
            var bal = opening; var lines = new List<LedgerLineDto>();
            foreach (var r in rows)
            {
                bal += sign * (r.Debit - r.Credit);
                var a = byId.GetValueOrDefault(r.AccountId);
                lines.Add(new LedgerLineDto(r.Id, r.Date, r.Number, string.IsNullOrWhiteSpace(r.Description) ? r.Memo : r.Description!, r.Reference, r.Debit, r.Credit, bal,
                    includeSubAccounts ? a?.Code : null, includeSubAccounts ? a?.Name : null, r.Source, r.SourceId));
            }
            var tree = ChartCodes.Tree(accounts).First(x => x.Account.Id == accountId);
            return new LedgerReportDto(AccountingService.ToDto(acc, true, tree.Level, tree.HasChildren), cur, f, t, opening, lines, rows.Sum(r => r.Debit), rows.Sum(r => r.Credit), bal, includeSubAccounts && ids.Count > 1);
        }

        // ---- trial balance ------------------------------------------------------------------------------------------------------

        public async Task<TrialBalanceDto> TrialBalanceAsync(Guid clientId, string currency, DateTime asAt, CancellationToken ct, DateTime? from = null, bool includeZero = false)
        {
            await PrepareAsync(clientId, ct);
            var cur = Cur(currency); var at = asAt.Date;
            var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
            var yearStart = ChartCodes.YearStart(at, rules.FiscalYearStartMonth);
            var f = (from ?? yearStart).Date;
            if (f > at) throw GatewayException.BadRequest("RANGE_INVALID", "The start date is after the end date.");
            var accounts = await AccountsAsync(clientId, ct);

            // opening: balance sheet accounts since the start, income and expenses since the start of AsAt's financial year (nil when the period starts with it)
            var (opening, _) = await PositionAsync(clientId, cur, accounts, f.AddDays(-1), yearStart, ct);
            var period = await SumsAsync(clientId, cur, f, at, ct);
            // a period that starts in an earlier year keeps only this year's income and expenses on their accounts; the rest is earlier years' profit
            var thisYear = f < yearStart ? await SumsAsync(clientId, cur, yearStart, at, ct) : period;
            // every year before AsAt's financial year is closed to retained earnings
            var (_, prior) = await PositionAsync(clientId, cur, accounts, at, yearStart, ct);

            var rows = new Dictionary<Guid, (decimal Open, decimal Dr, decimal Cr)>();
            foreach (var a in accounts)
            {
                var (d, c) = (ProfitAndLoss(a.Type) ? thisYear : period).GetValueOrDefault(a.Id);
                rows[a.Id] = (opening.GetValueOrDefault(a.Id), d, c);
            }
            var retained = Retained(accounts);
            var priorRow = (Open: prior, Dr: 0m, Cr: 0m);
            if (retained != null) { var r = rows[retained.Id]; rows[retained.Id] = (r.Open + priorRow.Open, r.Dr, r.Cr); }

            var tree = ChartCodes.Tree(accounts);
            var sub = new Dictionary<Guid, (decimal Open, decimal Dr, decimal Cr)>();
            for (var i = tree.Count - 1; i >= 0; i--)   // children come after their parent, so walking back adds them up first
            {
                var (a, level, _) = tree[i];
                var own = rows[a.Id];
                var total = own;
                for (var j = i + 1; j < tree.Count && tree[j].Level > level; j++)
                    if (tree[j].Level == level + 1) { var s = sub[tree[j].Account.Id]; total = (total.Open + s.Open, total.Dr + s.Dr, total.Cr + s.Cr); }
                sub[a.Id] = total;
            }

            var list = new List<TrialBalanceRow>();
            foreach (var (a, level, hasChildren) in tree)
            {
                var o = rows[a.Id]; var s = sub[a.Id];
                var closing = o.Open + o.Dr - o.Cr; var subClosing = s.Open + s.Dr - s.Cr;
                if (!includeZero && s.Open == 0 && s.Dr == 0 && s.Cr == 0 && subClosing == 0) continue;
                list.Add(new TrialBalanceRow(a.Id, a.Code, a.Name, a.Type, closing > 0 ? closing : 0, closing < 0 ? -closing : 0, ChartCodes.SubTypeOf(a), a.ParentId, level, hasChildren, a.IsGroup,
                    o.Open, o.Dr, o.Cr, closing, s.Open, s.Dr, s.Cr, subClosing));
            }
            if (retained == null && priorRow.Open != 0)
                list.Add(new TrialBalanceRow(Guid.Empty, "", "Retained earnings (earlier years)", "Equity", priorRow.Open > 0 ? priorRow.Open : 0, priorRow.Open < 0 ? -priorRow.Open : 0, "equity",
                    Opening: priorRow.Open, Closing: priorRow.Open, SubtotalOpening: priorRow.Open, SubtotalClosing: priorRow.Open));
            var td = list.Sum(r => r.Debit); var tc = list.Sum(r => r.Credit);
            if (Combining(cur) && td != tc)
            {
                // other currencies at today's rate on the balance sheet but their own day's rate in profit and loss: the difference is the translation reserve
                var x = tc - td;
                list.Add(new TrialBalanceRow(Guid.Empty, "", "Foreign currency translation difference", "Equity", x > 0 ? x : 0, x < 0 ? -x : 0, "equity", Closing: x, SubtotalClosing: x));
                td = list.Sum(r => r.Debit); tc = list.Sum(r => r.Credit);
            }
            return new TrialBalanceDto(cur, at, list, td, tc, td == tc, f,
                list.Sum(r => r.Opening > 0 ? r.Opening : 0), list.Sum(r => r.Opening < 0 ? -r.Opening : 0), list.Sum(r => r.PeriodDebit), list.Sum(r => r.PeriodCredit));
        }

        // ---- statements ---------------------------------------------------------------------------------------------------------

        private static string Label(string subType) => ChartCodes.SubTypes.First(s => s.Key == subType).Label;

        /// <summary>
        /// One section of a statement: the accounts of the given sub-types in tree order (a parent from another section does not hold its children here), their own amount on the
        /// statement's side, their subtotal with sub-accounts, and the comparative figures.
        /// </summary>
        private static StatementSection Section(string key, string title, List<LedgerAccount> accounts, string[] subTypes, Func<LedgerAccount, decimal> amount, Func<LedgerAccount, decimal>? compare,
            IEnumerable<StatementRow>? extra = null)
        {
            var mine = accounts.Where(a => subTypes.Contains(ChartCodes.SubTypeOf(a))).ToList();
            var tree = ChartCodes.Tree(mine);
            var sub = new Dictionary<Guid, (decimal A, decimal C)>();
            for (var i = tree.Count - 1; i >= 0; i--)
            {
                var (a, level, _) = tree[i];
                var total = (A: amount(a), C: compare?.Invoke(a) ?? 0);
                for (var j = i + 1; j < tree.Count && tree[j].Level > level; j++)
                    if (tree[j].Level == level + 1) { var s = sub[tree[j].Account.Id]; total = (total.A + s.A, total.C + s.C); }
                sub[a.Id] = total;
            }
            var rows = new List<StatementRow>();
            foreach (var (a, level, hasChildren) in tree)
            {
                var s = sub[a.Id];
                if (s.A == 0 && s.C == 0) continue;
                rows.Add(new StatementRow(a.Id, a.Code, a.Name, amount(a), compare == null ? null : compare(a), level, a.ParentId, hasChildren, hasChildren ? s.A : null, hasChildren && compare != null ? s.C : null));
            }
            if (extra != null) rows.AddRange(extra.Where(r => r.Amount != 0 || (r.Compare ?? 0) != 0));
            return new StatementSection(title, rows, rows.Sum(r => r.Amount), key, compare == null ? null : rows.Sum(r => r.Compare ?? 0));
        }

        private static (DateTime From, DateTime To)? CompareRange(string? compare, DateTime f, DateTime t)
        {
            switch ((compare ?? "").Trim().ToLowerInvariant())
            {
                case "previous": var days = (t - f).Days + 1; return (f.AddDays(-days), f.AddDays(-1));
                case "year": return (f.AddYears(-1), t.AddYears(-1));
                case "": case "none": return null;
                default: throw GatewayException.BadRequest("COMPARE_INVALID", "Compare with none, previous or year.");
            }
        }

        public async Task<ProfitLossDto> ProfitLossAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct, string? compare = null, Guid? costCentreId = null)
        {
            await PrepareAsync(clientId, ct);
            var cur = Cur(currency); var (f, t) = Range(from, to);
            var accounts = (await AccountsAsync(clientId, ct)).Where(a => ProfitAndLoss(a.Type)).ToList();
            var now = await SumsAsync(clientId, cur, f, t, ct, costCentreId);
            var range = CompareRange(compare, f, t);
            var then = range is { } r ? await SumsAsync(clientId, cur, r.From, r.To, ct, costCentreId) : null;

            // income on the credit side, expenses on the debit side
            decimal Amt(Dictionary<Guid, (decimal Debit, decimal Credit)> s, LedgerAccount a) { var (d, c) = s.GetValueOrDefault(a.Id); return a.Type == "Income" ? c - d : d - c; }
            Func<LedgerAccount, decimal>? Cmp() => then == null ? null : a => Amt(then, a);
            var revenue = Section("revenue", "Revenue", accounts, new[] { "revenue" }, a => Amt(now, a), Cmp());
            var cost = Section("cost_of_sales", "Cost of sales", accounts, new[] { "cost_of_sales" }, a => Amt(now, a), Cmp());
            var opex = Section("operating_expense", "Operating expenses", accounts, new[] { "operating_expense" }, a => Amt(now, a), Cmp());
            var other = Section("other_income", "Other income", accounts, new[] { "other_income" }, a => Amt(now, a), Cmp());
            var finance = Section("finance_cost", "Finance costs", accounts, new[] { "finance_cost" }, a => Amt(now, a), Cmp());
            var tax = Section("income_tax", "Income tax", accounts, new[] { "income_tax" }, a => Amt(now, a), Cmp());

            var gross = revenue.Total - cost.Total;
            var operating = gross - opex.Total + other.Total;
            var beforeTax = operating - finance.Total;
            decimal? C(StatementSection s) => s.CompareTotal;
            decimal? cGross = then == null ? null : C(revenue) - C(cost);
            decimal? cOperating = cGross - C(opex) + C(other);
            decimal? cBeforeTax = cOperating - C(finance);
            return new ProfitLossDto(cur, f, t, new[] { revenue, cost, opex, other, finance, tax }, gross, beforeTax - tax.Total, operating, beforeTax,
                range?.From, range?.To, cGross, then == null ? null : cOperating, then == null ? null : cBeforeTax, then == null ? null : cBeforeTax - C(tax));
        }

        private sealed record Position(Dictionary<Guid, decimal> Balance, decimal PriorYears, decimal CurrentYear);

        private async Task<Position> BalanceSheetPositionAsync(Guid clientId, string cur, List<LedgerAccount> accounts, DateTime at, int startMonth, CancellationToken ct)
        {
            var (bal, prior) = await PositionAsync(clientId, cur, accounts, at, ChartCodes.YearStart(at, startMonth), ct);
            var currentYear = -accounts.Where(a => ProfitAndLoss(a.Type)).Sum(a => bal.GetValueOrDefault(a.Id));   // credit-positive: a profit is positive
            return new Position(bal, -prior, currentYear);
        }

        public async Task<BalanceSheetDto> BalanceSheetAsync(Guid clientId, string currency, DateTime asAt, CancellationToken ct, string? compare = null)
        {
            await PrepareAsync(clientId, ct);
            var cur = Cur(currency); var at = asAt.Date;
            var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
            DateTime? compareAt = (compare ?? "").Trim().ToLowerInvariant() switch
            {
                "" or "none" => null,
                "year" => at.AddYears(-1),
                var d when DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) => x.Date,
                _ => throw GatewayException.BadRequest("COMPARE_INVALID", "Compare with none, year or a date (yyyy-MM-dd)."),
            };
            var accounts = await AccountsAsync(clientId, ct);
            var now = await BalanceSheetPositionAsync(clientId, cur, accounts, at, rules.FiscalYearStartMonth, ct);
            var then = compareAt is { } ca ? await BalanceSheetPositionAsync(clientId, cur, accounts, ca, rules.FiscalYearStartMonth, ct) : null;
            var retained = Retained(accounts);

            // assets on the debit side; liabilities and equity on the credit side; retained earnings carry the earlier years' profit
            decimal Amt(Position p, LedgerAccount a) => (ChartCodes.IsDebitNormal(a.Type) ? 1 : -1) * p.Balance.GetValueOrDefault(a.Id) + (a.Id == retained?.Id ? p.PriorYears : 0);
            Func<LedgerAccount, decimal>? Cmp() => then == null ? null : a => Amt(then, a);
            var sheet = accounts.Where(a => !ProfitAndLoss(a.Type)).ToList();
            var nonCurrentAssets = Section("non_current_assets", "Non-current assets", sheet, new[] { "fixed_asset", "non_current_asset" }, a => Amt(now, a), Cmp());
            var currentAssets = Section("current_assets", "Current assets", sheet, new[] { "current_asset", "cash" }, a => Amt(now, a), Cmp());
            var currentLiabilities = Section("current_liabilities", "Current liabilities", sheet, new[] { "current_liability" }, a => Amt(now, a), Cmp());
            var nonCurrentLiabilities = Section("non_current_liabilities", "Non-current liabilities", sheet, new[] { "non_current_liability" }, a => Amt(now, a), Cmp());
            var extra = new List<StatementRow> { new(Guid.Empty, "", "Current year earnings", now.CurrentYear, then?.CurrentYear) };
            if (retained == null) extra.Insert(0, new StatementRow(Guid.Empty, "", "Retained earnings", now.PriorYears, then?.PriorYears));
            var equity = Section("equity", "Equity", sheet, new[] { "equity" }, a => Amt(now, a), Cmp(), extra);

            var assets = nonCurrentAssets.Total + currentAssets.Total;
            var liabilities = currentLiabilities.Total + nonCurrentLiabilities.Total;
            if (Combining(cur))
            {
                // the other currencies, translated: what does not add up is the translation reserve
                var reserve = assets - liabilities - equity.Total;
                decimal? reserveThen = then == null ? null : (nonCurrentAssets.CompareTotal + currentAssets.CompareTotal) - (currentLiabilities.CompareTotal + nonCurrentLiabilities.CompareTotal) - equity.CompareTotal;
                if (reserve != 0 || (reserveThen ?? 0) != 0)
                    equity = Section("equity", "Equity", sheet, new[] { "equity" }, a => Amt(now, a), Cmp(), extra.Append(new StatementRow(Guid.Empty, "", "Foreign currency translation reserve", reserve, reserveThen)));
            }
            decimal? cAssets = then == null ? null : nonCurrentAssets.CompareTotal + currentAssets.CompareTotal;
            decimal? cLiabilities = then == null ? null : currentLiabilities.CompareTotal + nonCurrentLiabilities.CompareTotal;
            return new BalanceSheetDto(cur, at, new[] { nonCurrentAssets, currentAssets, currentLiabilities, nonCurrentLiabilities, equity }, assets, liabilities + equity.Total,
                assets == liabilities + equity.Total, liabilities, equity.Total, compareAt, cAssets, cLiabilities, equity.CompareTotal, cLiabilities + equity.CompareTotal);
        }

        // ---- cashbook -----------------------------------------------------------------------------------------------------------

        public async Task<CashbookDto> CashbookAsync(Guid clientId, Guid accountId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var cur = Cur(currency); var (f, t) = Range(from, to);
            var acc = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Account");
            if (acc.Type != "Asset") throw GatewayException.BadRequest("ACCOUNT_INVALID", "A cashbook is kept for cash, bank and wallet accounts.");
            var before = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.AccountId == accountId && l.Currency == cur && l.Date < f).Select(l => new { l.Debit, l.Credit }).ToListAsync(ct);
            var opening = before.Sum(x => x.Debit) - before.Sum(x => x.Credit);
            var rows = await (from l in _db.JournalLines.AsNoTracking() join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                              where l.ClientId == clientId && l.AccountId == accountId && l.Currency == cur && l.Date >= f && l.Date <= t
                              orderby l.Date, e.Seq select new { LineId = l.Id, EntryId = e.Id, l.Date, e.Number, e.Memo, e.Reference, l.Description, l.Debit, l.Credit, e.Source }).ToListAsync(ct);
            // the other side of each entry, as a bank ledger shows it
            var entryIds = rows.Select(r => r.EntryId).Distinct().ToList();
            var others = (await (from l in _db.JournalLines.AsNoTracking() join a in _db.LedgerAccounts.AsNoTracking() on l.AccountId equals a.Id
                                 where entryIds.Contains(l.EntryId) && l.AccountId != accountId select new { l.EntryId, a.Code, a.Name }).ToListAsync(ct))
                .GroupBy(x => x.EntryId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Code + " " + x.Name).Distinct()));
            var matched = (await _db.BankStatementLines.AsNoTracking().Where(s => s.ClientId == clientId && s.AccountId == accountId && s.JournalLineId != null).Select(s => s.JournalLineId!.Value).ToListAsync(ct)).ToHashSet();
            var bal = opening; var lines = new List<CashLineDto>();
            foreach (var r in rows) { bal += r.Debit - r.Credit; lines.Add(new CashLineDto(r.LineId, r.EntryId, r.Date, r.Number, string.IsNullOrWhiteSpace(r.Description) ? r.Memo : r.Description!, r.Reference, r.Debit, r.Credit, bal, matched.Contains(r.LineId), others.GetValueOrDefault(r.EntryId), r.Source)); }
            return new CashbookDto(AccountingService.ToDto(acc, true), cur, f, t, opening, lines, rows.Sum(r => r.Debit), rows.Sum(r => r.Credit), bal);
        }
    }
}
