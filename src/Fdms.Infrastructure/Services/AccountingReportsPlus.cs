using System.Globalization;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>The cash flow statement, the financial dashboard (ratios and trends) and profit and loss by cost centre.</summary>
    public sealed partial class AccountingReports
    {
        private static readonly string[] PlOrder = { "revenue", "cost_of_sales", "operating_expense", "other_income", "finance_cost", "income_tax" };

        // ---- profit and loss by cost centre ---------------------------------------------------------------------------------------

        public async Task<ProfitLossByCentreDto> ProfitLossByCentreAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var cur = Cur(currency); var (f, t) = Range(from, to);
            var accounts = (await AccountsAsync(clientId, ct)).Where(a => ProfitAndLoss(a.Type)).ToDictionary(a => a.Id);
            var ids = accounts.Keys.ToList();
            var sums = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Currency == cur && l.Date >= f && l.Date <= t && ids.Contains(l.AccountId))
                .GroupBy(l => new { l.AccountId, l.CostCentreId }).Select(g => new { g.Key.AccountId, g.Key.CostCentreId, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
            var usedCentres = sums.Where(s => s.CostCentreId != null).Select(s => s.CostCentreId!.Value).Distinct().ToList();
            var centres = await _db.CostCentres.AsNoTracking().Where(c => c.ClientId == clientId && (c.IsActive || usedCentres.Contains(c.Id))).OrderBy(c => c.Code).ToListAsync(ct);
            var columns = centres.Select(c => new CentreColumn(c.Id, c.Code, c.Name)).ToList();
            columns.Add(new CentreColumn(null, "", "Not assigned"));
            int Col(Guid? id) => id == null ? columns.Count - 1 : Math.Max(0, columns.FindIndex(c => c.Id == id));

            var rows = new List<CentreRow>();
            var bySection = new Dictionary<string, decimal[]>();
            foreach (var key in PlOrder) bySection[key] = new decimal[columns.Count];
            foreach (var g in sums.GroupBy(s => s.AccountId))
            {
                var a = accounts[g.Key];
                var amounts = new decimal[columns.Count];
                foreach (var s in g) amounts[Col(s.CostCentreId)] += a.Type == "Income" ? s.C - s.D : s.D - s.C;
                if (amounts.All(x => x == 0)) continue;
                var section = ChartCodes.SubTypeOf(a);
                for (var i = 0; i < amounts.Length; i++) bySection[section][i] += amounts[i];
                rows.Add(new CentreRow(section, a.Id, a.Code, a.Name, amounts, amounts.Sum()));
            }
            rows = rows.OrderBy(r => Array.IndexOf(PlOrder, r.Section)).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();

            decimal[] Combine(Func<int, decimal> f2) => Enumerable.Range(0, columns.Count).Select(f2).ToArray();
            var gross = Combine(i => bySection["revenue"][i] - bySection["cost_of_sales"][i]);
            var operating = Combine(i => gross[i] - bySection["operating_expense"][i] + bySection["other_income"][i]);
            var net = Combine(i => operating[i] - bySection["finance_cost"][i] - bySection["income_tax"][i]);
            CentreSummary Sum(string label, decimal[] v) => new(label, v, v.Sum());
            var summaries = new List<CentreSummary>
            {
                Sum("Revenue", bySection["revenue"]), Sum("Cost of sales", bySection["cost_of_sales"]), Sum("Gross profit", gross),
                Sum("Operating expenses", bySection["operating_expense"]), Sum("Other income", bySection["other_income"]), Sum("Operating profit", operating),
                Sum("Finance costs", bySection["finance_cost"]), Sum("Income tax", bySection["income_tax"]), Sum("Net profit", net),
            };
            return new ProfitLossByCentreDto(cur, f, t, columns, rows, summaries);
        }

        // ---- cash flow statement --------------------------------------------------------------------------------------------------

        public async Task<CashFlowDto> CashFlowAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var cur = Cur(currency); var (f, t) = Range(from, to);
            return await CashFlowCoreAsync(clientId, cur, f, t, ct);
        }

        private async Task<CashFlowDto> CashFlowCoreAsync(Guid clientId, string cur, DateTime f, DateTime t, CancellationToken ct)
        {
            var accounts = await AccountsAsync(clientId, ct);
            var byId = accounts.ToDictionary(a => a.Id);
            var move = await SumsAsync(clientId, cur, f, t, ct);
            var before = await SumsAsync(clientId, cur, null, f.AddDays(-1), ct);
            // depreciation and disposals are not cash: their entries are taken out of the movements and shown as adjustments
            var special = await (from l in _db.JournalLines.AsNoTracking() join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                                 where l.ClientId == clientId && l.Currency == cur && l.Date >= f && l.Date <= t && (e.Source == "depn" || e.Source == "asset")
                                 group l by new { l.AccountId, e.Source } into g
                                 select new { g.Key.AccountId, g.Key.Source, Net = g.Sum(x => x.Debit - x.Credit) }).ToListAsync(ct);

            decimal Net(Guid id) { var (d, c) = move.GetValueOrDefault(id); return d - c; }
            var isCash = new Func<LedgerAccount, bool>(a => a.Type == "Asset" && ChartCodes.SubTypeOf(a) == "cash");
            var netProfit = -accounts.Where(a => ProfitAndLoss(a.Type)).Sum(a => Net(a.Id));

            // non-cash: depreciation charged (the expense side) is added back; a gain or loss on disposal is moved out of operating into investing
            var depreciation = special.Where(s => s.Source == "depn" && byId.TryGetValue(s.AccountId, out var a) && ProfitAndLoss(a.Type)).Sum(s => s.Net);
            var disposalPl = special.Where(s => s.Source == "asset" && byId.TryGetValue(s.AccountId, out var a) && ProfitAndLoss(a.Type)).Sum(s => s.Net);   // loss positive
            var depnOnSheet = special.Where(s => s.Source == "depn" && byId.TryGetValue(s.AccountId, out var a) && !ProfitAndLoss(a.Type)).ToDictionary(s => s.AccountId, s => s.Net);

            var operating = new List<CashFlowLine> { new("Net profit", null, netProfit) };
            if (depreciation != 0) operating.Add(new("Depreciation", null, depreciation));
            if (disposalPl != 0) operating.Add(new(disposalPl > 0 ? "Loss on disposal of assets" : "Gain on disposal of assets", null, disposalPl));
            var investing = new List<CashFlowLine>(); var financing = new List<CashFlowLine>();
            foreach (var a in accounts.Where(a => !ProfitAndLoss(a.Type) && !isCash(a)).OrderBy(a => a.Code, StringComparer.Ordinal))
            {
                var m = Net(a.Id) - depnOnSheet.GetValueOrDefault(a.Id);
                if (m == 0) continue;
                var effect = -m;   // a rise in an asset uses cash; a rise in a liability or equity brings it in
                switch (ChartCodes.SubTypeOf(a))
                {
                    case "current_asset": operating.Add(new($"{(effect < 0 ? "Increase" : "Decrease")} in {a.Name.ToLowerInvariant()}", a.Id, effect)); break;
                    case "current_liability": operating.Add(new($"{(effect > 0 ? "Increase" : "Decrease")} in {a.Name.ToLowerInvariant()}", a.Id, effect)); break;
                    case "fixed_asset": case "non_current_asset": investing.Add(new(effect < 0 ? $"Purchase of {a.Name.ToLowerInvariant()}" : $"Disposal of {a.Name.ToLowerInvariant()}", a.Id, effect)); break;
                    default: financing.Add(new(a.Name, a.Id, effect)); break;
                }
            }
            if (disposalPl != 0) investing.Add(new(disposalPl > 0 ? "Loss on disposal (in the proceeds)" : "Gain on disposal (in the proceeds)", null, -disposalPl));
            var sections = new[]
            {
                new CashFlowSection("operating", "Cash from operating activities", operating, operating.Sum(x => x.Amount)),
                new CashFlowSection("investing", "Cash from investing activities", investing, investing.Sum(x => x.Amount)),
                new CashFlowSection("financing", "Cash from financing activities", financing, financing.Sum(x => x.Amount)),
            };
            var change = sections.Sum(s => s.Total);
            var opening = accounts.Where(isCash).Sum(a => { var (d, c) = before.GetValueOrDefault(a.Id); return d - c; });
            var closing = opening + accounts.Where(isCash).Sum(a => Net(a.Id));
            return new CashFlowDto(cur, f, t, netProfit, sections, change, opening, closing, change == closing - opening);
        }

        // ---- the financial dashboard ----------------------------------------------------------------------------------------------

        private static decimal? Div(decimal a, decimal b, decimal times = 1) => b == 0 ? null : decimal.Round(a / b * times, 2, MidpointRounding.AwayFromZero);

        public async Task<FinancialDashboardDto> DashboardAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var cur = Cur(currency); var (f, t) = Range(from, to);
            var accounts = await AccountsAsync(clientId, ct);
            var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
            var pl = await SumsAsync(clientId, cur, f, t, ct);
            decimal Pl(string sub) => accounts.Where(a => ProfitAndLoss(a.Type) && ChartCodes.SubTypeOf(a) == sub).Sum(a => { var (d, c) = pl.GetValueOrDefault(a.Id); return a.Type == "Income" ? c - d : d - c; });
            var revenue = Pl("revenue"); var cost = Pl("cost_of_sales"); var opex = Pl("operating_expense"); var other = Pl("other_income");
            var gross = revenue - cost; var operating = gross - opex + other; var net = operating - Pl("finance_cost") - Pl("income_tax");

            var (bal, prior) = await PositionAsync(clientId, cur, accounts, t, ChartCodes.YearStart(t, rules.FiscalYearStartMonth), ct);
            decimal Sheet(Func<LedgerAccount, bool> which) => accounts.Where(a => !ProfitAndLoss(a.Type) && which(a)).Sum(a => (ChartCodes.IsDebitNormal(a.Type) ? 1 : -1) * bal.GetValueOrDefault(a.Id));
            var cash = Sheet(a => ChartCodes.SubTypeOf(a) == "cash");
            var currentAssets = Sheet(a => ChartCodes.SubTypeOf(a) is "cash" or "current_asset");
            var currentLiabilities = Sheet(a => ChartCodes.SubTypeOf(a) == "current_liability");
            var liabilities = Sheet(a => a.Type == "Liability");
            var currentYear = -accounts.Where(a => ProfitAndLoss(a.Type)).Sum(a => bal.GetValueOrDefault(a.Id));
            var equity = Sheet(a => a.Type == "Equity") - prior + currentYear;
            var receivables = Sheet(a => a.Role == "receivable");
            var payables = Sheet(a => a.Role == "payable");
            var days = (t - f).Days + 1;

            var ratios = new List<RatioDto>
            {
                new("current", "Current ratio", Div(currentAssets, currentLiabilities), "x", "Current assets for every 1 of current liabilities. Above 1.5 is comfortable."),
                new("cash", "Cash ratio", Div(cash, currentLiabilities), "x", "Cash and bank for every 1 of current liabilities."),
                new("gross_margin", "Gross margin", Div(gross, revenue, 100), "%", "What is left of each sale after its direct cost."),
                new("operating_margin", "Operating margin", Div(operating, revenue, 100), "%", "Profit from running the business, per sale."),
                new("net_margin", "Net margin", Div(net, revenue, 100), "%", "Profit after everything, per sale."),
                new("debtor_days", "Debtor days", Div(receivables, revenue, days), "days", "How long customers take to pay, on average."),
                new("creditor_days", "Creditor days", Div(payables, cost + opex, days), "days", "How long you take to pay suppliers, on average."),
                new("debt_equity", "Debt to equity", Div(liabilities, equity), "x", "Liabilities for every 1 of the owners' equity."),
                new("roe", "Return on equity", Div(net, equity, 100), "%", "Profit in the period for every 100 of equity."),
            };

            // the last twelve months to the end date: revenue, expenses, profit and the cash at each month end
            var start = new DateTime(t.Year, t.Month, 1).AddMonths(-11);
            var plIds = accounts.Where(a => ProfitAndLoss(a.Type)).Select(a => a.Id).ToList();
            var cashIds = accounts.Where(a => a.Type == "Asset" && ChartCodes.SubTypeOf(a) == "cash").Select(a => a.Id).ToList();
            var monthly = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Currency == cur && l.Date >= start && l.Date <= t && (plIds.Contains(l.AccountId) || cashIds.Contains(l.AccountId)))
                .GroupBy(l => new { l.Date.Year, l.Date.Month, l.AccountId }).Select(g => new { g.Key.Year, g.Key.Month, g.Key.AccountId, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
            var cashBefore = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Currency == cur && l.Date < start && cashIds.Contains(l.AccountId)).SumAsync(l => (decimal?)(l.Debit - l.Credit), ct) ?? 0;
            var byId = accounts.ToDictionary(a => a.Id);
            var trend = new List<BooksTrendPoint>(); var running = cashBefore;
            for (var m = start; m <= t; m = m.AddMonths(1))
            {
                var rows = monthly.Where(x => x.Year == m.Year && x.Month == m.Month).ToList();
                var rev = rows.Where(x => byId[x.AccountId].Type == "Income").Sum(x => x.C - x.D);
                var exp = rows.Where(x => byId[x.AccountId].Type == "Expense").Sum(x => x.D - x.C);
                running += rows.Where(x => cashIds.Contains(x.AccountId)).Sum(x => x.D - x.C);
                trend.Add(new BooksTrendPoint(m.ToString("yyyy-MM", CultureInfo.InvariantCulture), rev, exp, rev - exp, running));
            }
            var top = accounts.Where(a => a.Type == "Expense").Select(a => { var (d, c) = pl.GetValueOrDefault(a.Id); return new CashFlowLine(a.Code + " " + a.Name, a.Id, d - c); })
                .Where(x => x.Amount != 0).OrderByDescending(x => x.Amount).Take(6).ToList();
            return new FinancialDashboardDto(cur, f, t, revenue, gross, net, cash, receivables, payables, ratios, trend, top);
        }
    }
}
