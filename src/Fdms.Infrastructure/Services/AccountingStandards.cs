using System.Globalization;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Fdms.Infrastructure.Services
{
    // =================================================================================================================
    // exchange rates and the base currency
    // =================================================================================================================

    /// <summary>The company's base currency and what each other currency was worth in it over time, so the reports can show every currency together.</summary>
    public sealed class ExchangeRateService : IExchangeRateService
    {
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope;
        public ExchangeRateService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        internal static async Task<string> BaseAsync(FdmsDbContext db, Guid clientId, CancellationToken ct) =>
            await db.AccountingSettings.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => s.BaseCurrency).FirstOrDefaultAsync(ct) ?? "USD";

        public async Task<RatesDto> ListAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var b = await BaseAsync(_db, clientId, ct);
            var used = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId).Select(l => l.Currency).Distinct().ToListAsync(ct);
            var rates = await _db.ExchangeRates.AsNoTracking().Where(r => r.ClientId == clientId).OrderBy(r => r.Currency).ThenByDescending(r => r.Date).ToListAsync(ct);
            var currencies = used.Concat(rates.Select(r => r.Currency)).Where(c => c != b).Distinct().OrderBy(c => c).ToList();
            return new RatesDto(b, currencies, rates.Select(r => new ExchangeRateDto(r.Id, r.Currency, r.Date, r.Rate)).ToList());
        }

        public async Task<RatesDto> SaveAsync(Guid clientId, SaveExchangeRateRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (cur == await BaseAsync(_db, clientId, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "That is your base currency: its rate is always 1.");
            if (req.Rate <= 0 || req.Rate > 1_000_000_000m) throw GatewayException.BadRequest("RATE_INVALID", "The rate must be above zero.");
            if (req.Date.Year < 2000 || req.Date.Date > DateTime.UtcNow.AddDays(366)) throw GatewayException.BadRequest("DATE_INVALID", "That date is not valid.");
            var row = await _db.ExchangeRates.FirstOrDefaultAsync(r => r.ClientId == clientId && r.Currency == cur && r.Date == req.Date.Date, ct);
            if (row == null) _db.ExchangeRates.Add(row = new ExchangeRate { ClientId = clientId, Currency = cur, Date = req.Date.Date });
            row.Rate = Math.Round(req.Rate, 8); row.CreatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.rate.save", Detail = $"{cur} {row.Rate} from {row.Date:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return await ListAsync(clientId, ct);
        }

        public async Task<RatesDto> DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var row = await _db.ExchangeRates.FirstOrDefaultAsync(r => r.Id == id && r.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Rate");
            _db.ExchangeRates.Remove(row);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.rate.delete", Detail = $"{row.Currency} {row.Rate} from {row.Date:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return await ListAsync(clientId, ct);
        }

        public async Task<RatesDto> SetBaseAsync(Guid clientId, SetBaseCurrencyRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var cur = (req.BaseCurrency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            var row = await _db.AccountingSettings.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (row == null) _db.AccountingSettings.Add(row = new AccountingSetting { ClientId = clientId });
            if (row.BaseCurrency != cur && await _db.ExchangeRates.AnyAsync(r => r.ClientId == clientId, ct))
                throw GatewayException.Conflict("RATES_EXIST", "Your rates are against the current base currency. Delete them before changing it.");
            row.BaseCurrency = cur; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.base-currency", Detail = cur });
            await _db.SaveChangesAsync(ct);
            return await ListAsync(clientId, ct);
        }
    }

    // =================================================================================================================
    // prepayments and deferred income
    // =================================================================================================================

    /// <summary>
    /// Amounts paid or received in advance, released month by month: a prepayment from its asset account to an expense, deferred income from its liability account to income.
    /// Each month is released once it has ended (dated its last day); a month in a closed period is released on the first open day and says so.
    /// </summary>
    public sealed class ScheduleService : IScheduleService
    {
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IAccountingService _books;
        public ScheduleService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        internal static decimal Share(AmortisationSchedule s, int month)
        {
            var each = Math.Round(s.Amount / s.Months, 2, MidpointRounding.AwayFromZero);
            return month < s.Months - 1 ? each : s.Amount - each * (s.Months - 1);
        }

        private async Task<List<ScheduleDto>> MapAsync(Guid clientId, List<AmortisationSchedule> list, CancellationToken ct)
        {
            var ids = list.Select(s => s.Id).ToList();
            var charges = await _db.AmortisationCharges.AsNoTracking().Where(c => ids.Contains(c.ScheduleId)).ToListAsync(ct);
            var entryIds = charges.Where(c => c.JournalEntryId != null).Select(c => c.JournalEntryId!.Value).ToList();
            var numbers = await _db.JournalEntries.AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
            var accIds = list.SelectMany(s => new[] { s.BalanceAccountId, s.PnlAccountId }).Distinct().ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => accIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Code + " " + a.Name, ct);
            return list.Select(s =>
            {
                var mine = charges.Where(c => c.ScheduleId == s.Id).ToDictionary(c => c.Period);
                var plan = Enumerable.Range(0, s.Months).Select(m =>
                {
                    var period = s.StartMonth.AddMonths(m);
                    var done = mine.GetValueOrDefault(period);
                    return new ScheduleMonthDto(period, done?.Amount ?? Share(s, m), done != null, done?.JournalEntryId is { } j ? numbers.GetValueOrDefault(j) : null);
                }).ToList();
                var released = mine.Values.Sum(c => c.Amount);
                return new ScheduleDto(s.Id, s.Name, s.Kind, s.Currency, s.Amount, s.StartMonth, s.Months, s.BalanceAccountId, accs.GetValueOrDefault(s.BalanceAccountId, ""),
                    s.PnlAccountId, accs.GetValueOrDefault(s.PnlAccountId, ""), s.CostCentreId, s.Status, s.Notes, released, s.Status == "Cancelled" ? 0 : s.Amount - released, mine.Count, plan);
            }).ToList();
        }

        public async Task<IReadOnlyList<ScheduleDto>> ListAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var list = await _db.AmortisationSchedules.AsNoTracking().Where(s => s.ClientId == clientId).OrderBy(s => s.Status == "Active" ? 0 : 1).ThenByDescending(s => s.StartMonth).ToListAsync(ct);
            return await MapAsync(clientId, list, ct);
        }

        private async Task<ScheduleDto> OneAsync(Guid clientId, Guid id, CancellationToken ct) =>
            (await MapAsync(clientId, await _db.AmortisationSchedules.AsNoTracking().Where(s => s.Id == id && s.ClientId == clientId).ToListAsync(ct), ct)).FirstOrDefault() ?? throw GatewayException.NotFound("Schedule");

        public async Task<ScheduleDto> SaveAsync(Guid clientId, SaveScheduleRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await _books.EnsureChartAsync(clientId, ct);
            var name = (req.Name ?? "").Trim();
            if (name.Length is < 2 or > 120) throw GatewayException.BadRequest("NAME_INVALID", "Name it (2-120 characters), for example \"Vehicle insurance 2026\".");
            var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
            if (notes is { Length: > 300 }) throw GatewayException.BadRequest("TOO_LONG", "The notes can be at most 300 characters.");
            AmortisationSchedule s;
            if (req.Id is { } id && id != Guid.Empty)
            {
                s = await _db.AmortisationSchedules.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Schedule");
                if (await _db.AmortisationCharges.AnyAsync(c => c.ScheduleId == id, ct))
                {
                    s.Name = name; s.Notes = notes;   // months already released: only the name and notes change
                    await _db.SaveChangesAsync(ct);
                    return await OneAsync(clientId, id, ct);
                }
            }
            else { s = new AmortisationSchedule { ClientId = clientId, CreatedBy = _scope.UserName }; _db.AmortisationSchedules.Add(s); }

            var kind = req.Kind is "deferred_income" ? "deferred_income" : req.Kind is "prepayment" ? "prepayment" : throw GatewayException.BadRequest("KIND_INVALID", "Choose a prepayment or deferred income.");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (req.Amount <= 0 || req.Amount != Math.Round(req.Amount, 2) || req.Amount > 1_000_000_000m) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter the amount (above zero, at most two decimals).");
            if (req.Months is < 1 or > 120) throw GatewayException.BadRequest("MONTHS_INVALID", "Release it over 1 to 120 months.");
            if (req.StartMonth.Year < 2000) throw GatewayException.BadRequest("DATE_INVALID", "Choose the first month.");
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && (a.Id == req.BalanceAccountId || a.Id == req.PnlAccountId)).ToDictionaryAsync(a => a.Id, ct);
            var (balType, pnlType, balWhat, pnlWhat) = kind == "prepayment" ? ("Asset", "Expense", "a prepayments (asset) account", "an expense account") : ("Liability", "Income", "a deferred income (liability) account", "an income account");
            if (!accs.TryGetValue(req.BalanceAccountId, out var bal) || bal.Type != balType || bal.IsGroup || !bal.IsActive) throw GatewayException.BadRequest("ACCOUNT_INVALID", $"Hold it in {balWhat}.");
            if (!accs.TryGetValue(req.PnlAccountId, out var pnl) || pnl.Type != pnlType || pnl.IsGroup || !pnl.IsActive) throw GatewayException.BadRequest("ACCOUNT_INVALID", $"Release it to {pnlWhat}.");
            await _books.CheckCentresAsync(clientId, new[] { req.CostCentreId }, ct);
            s.Name = name; s.Notes = notes; s.Kind = kind; s.Currency = cur; s.Amount = req.Amount; s.Months = req.Months; s.StartMonth = new DateTime(req.StartMonth.Year, req.StartMonth.Month, 1);
            s.BalanceAccountId = bal.Id; s.PnlAccountId = pnl.Id; s.CostCentreId = req.CostCentreId; s.Status = "Active";
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.schedule.save", Detail = $"{name}: {req.Amount:N2} {cur} over {req.Months} months" });
            await _db.SaveChangesAsync(ct);
            return await OneAsync(clientId, s.Id, ct);
        }

        public async Task<ScheduleDto> CancelAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var s = await _db.AmortisationSchedules.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Schedule");
            if (s.Status != "Active") throw GatewayException.Conflict("NOT_ACTIVE", "That schedule is not running.");
            s.Status = "Cancelled";
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.schedule.cancel", Detail = s.Name });
            await _db.SaveChangesAsync(ct);
            return await OneAsync(clientId, id, ct);
        }

        private async Task<int> ReleaseAsync(AmortisationSchedule s, DateTime today, CancellationToken ct)
        {
            var done = (await _db.AmortisationCharges.AsNoTracking().Where(c => c.ScheduleId == s.Id).Select(c => c.Period).ToListAsync(ct)).ToHashSet();
            var locked = await _books.LockedUntilAsync(s.ClientId, ct);
            var posted = 0;
            for (var m = 0; m < s.Months; m++)
            {
                var period = s.StartMonth.AddMonths(m);
                var end = period.AddMonths(1).AddDays(-1);
                if (end > today) break;
                if (done.Contains(period)) continue;
                var amount = Share(s, m);
                var date = locked is { } l && end <= l ? l.AddDays(1) : end;
                var memo = $"{s.Name}: {period:MMM yyyy} ({m + 1} of {s.Months})" + (date != end ? $" (for {end:d MMM yyyy})" : "");
                var prepay = s.Kind == "prepayment";
                var lines = new List<JournalLineInput>
                {
                    new(s.PnlAccountId, prepay ? amount : 0, prepay ? 0 : amount, null, s.CostCentreId),
                    new(s.BalanceAccountId, prepay ? 0 : amount, prepay ? amount : 0, null),
                };
                var entry = await _books.PostLinesAsync(s.ClientId, date, s.Currency, memo, s.Name.Length > 60 ? s.Name[..60] : s.Name, "schedule", null, lines, ct);
                _db.AmortisationCharges.Add(new AmortisationCharge { ClientId = s.ClientId, ScheduleId = s.Id, Period = period, Amount = amount, JournalEntryId = entry.Id });
                await _db.SaveChangesAsync(ct);
                posted++; done.Add(period);
            }
            if (done.Count >= s.Months && s.Status == "Active")
            {
                var row = await _db.AmortisationSchedules.FirstAsync(x => x.Id == s.Id, ct);
                row.Status = "Finished"; await _db.SaveChangesAsync(ct);
            }
            return posted;
        }

        public async Task<RunSchedulesResult> RunAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var n = 0;
            foreach (var s in await _db.AmortisationSchedules.AsNoTracking().Where(x => x.ClientId == clientId && x.Status == "Active").ToListAsync(ct))
                n += await ReleaseAsync(s, Today, ct);
            return new RunSchedulesResult(n);
        }

        public async Task<int> RunAllDueAsync(DateTime today, CancellationToken ct)
        {
            var n = 0;
            foreach (var s in await _db.AmortisationSchedules.AsNoTracking().Where(x => x.Status == "Active" && x.StartMonth <= today).ToListAsync(ct))
            {
                try { n += await ReleaseAsync(s, today, ct); }
                catch (GatewayException) { /* an account switched off or the like: it waits for the company to fix it */ }
            }
            return n;
        }
    }

    // =================================================================================================================
    // changes in equity, the financial statements pack, and the audit trail
    // =================================================================================================================

    public sealed class FinancialStatementsService : IFinancialStatementsService
    {
        static FinancialStatementsService() => QuestPDF.Settings.License = LicenseType.Community;
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IAccountingReports _reports; private readonly IAccountingService _books;
        public FinancialStatementsService(FdmsDbContext db, ICallerScope scope, IAccountingReports reports, IAccountingService books) { _db = db; _scope = scope; _reports = reports; _books = books; }

        public const string StandardPolicies =
            "Basis of preparation\nThese financial statements have been prepared on the historical cost basis and on the accrual basis of accounting, in accordance with the International Financial Reporting Standard for Small and Medium-sized Entities (IFRS for SMEs).\n\n" +
            "Revenue\nRevenue is recognised when goods are delivered or services are rendered, at the amount the business expects to be entitled to, net of discounts and credit notes.\n\n" +
            "Property, plant and equipment\nItems are carried at cost less accumulated depreciation. Depreciation is charged so as to write off the cost, less residual value, over the useful life of each asset.\n\n" +
            "Receivables and payables\nTrade receivables and payables are carried at the amounts owing. Amounts not expected to be recovered are written off.\n\n" +
            "Prepayments and deferred income\nAmounts paid or received in advance are released to profit or loss over the period they relate to.\n\n" +
            "Foreign currencies\nEach currency is recorded in its own ledger. Where figures are combined, assets and liabilities are translated at the rate at the reporting date and income and expenses at the rate on the date of each transaction; the difference is shown in equity.";

        public async Task<ChangesInEquityDto> ChangesInEquityAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await _books.SyncSalesAsync(clientId, ct);
            var cur = (currency ?? "").Trim().ToUpperInvariant();
            if (cur.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency.");
            var f = from.Date; var t = to.Date;
            if (t < f) throw GatewayException.BadRequest("RANGE_INVALID", "The end date is before the start date.");
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).ToListAsync(ct);
            var types = accounts.ToDictionary(a => a.Id, a => a.Type);
            var sums = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Currency == cur && l.Date <= t)
                .GroupBy(l => new { l.AccountId, Before = l.Date < f }).Select(g => new { g.Key.AccountId, g.Key.Before, Net = g.Sum(x => x.Credit - x.Debit) }).ToListAsync(ct);
            decimal Sum(Func<string, bool> type, bool before, Guid? account = null) =>
                sums.Where(s => s.Before == before && types.TryGetValue(s.AccountId, out var ty) && type(ty) && (account == null || s.AccountId == account)).Sum(s => s.Net);
            static bool Pl(string ty) => ty is "Income" or "Expense";

            var retained = accounts.Where(a => a.Role == "retained" && !a.IsGroup).OrderByDescending(a => a.IsSystem).ThenBy(a => a.Code).FirstOrDefault();
            var rows = new List<EquityMovementRow>();
            var profit = Sum(Pl, false);
            foreach (var a in accounts.Where(a => a.Type == "Equity" && !a.IsGroup).OrderBy(a => a.Code))
            {
                var open = Sum(ty => ty == "Equity", true, a.Id); var other = Sum(ty => ty == "Equity", false, a.Id);
                var isRetained = a.Id == retained?.Id;
                if (isRetained) open += Sum(Pl, true);   // every earlier profit sits in retained earnings
                var p = isRetained ? profit : 0;
                if (open == 0 && other == 0 && p == 0) continue;
                rows.Add(new EquityMovementRow(a.Name, a.Id, open, p, other, open + p + other));
            }
            if (retained == null)
            {
                var open = Sum(Pl, true);
                if (open != 0 || profit != 0) rows.Add(new EquityMovementRow("Retained earnings", null, open, profit, 0, open + profit));
            }
            var total = new EquityMovementRow("Total equity", null, rows.Sum(r => r.Opening), rows.Sum(r => r.Profit), rows.Sum(r => r.Other), rows.Sum(r => r.Closing));
            return new ChangesInEquityDto(cur, f, t, rows, total);
        }

        public async Task<PolicyNotesDto> PolicyNotesAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var text = await _db.AccountingSettings.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => s.PolicyNotes).FirstOrDefaultAsync(ct);
            return string.IsNullOrWhiteSpace(text) ? new PolicyNotesDto(StandardPolicies, true) : new PolicyNotesDto(text, false);
        }

        public async Task<PolicyNotesDto> SavePolicyNotesAsync(Guid clientId, SavePolicyNotesRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var text = string.IsNullOrWhiteSpace(req.Text) || req.Text.Trim() == StandardPolicies ? null : req.Text.Trim();
            if (text is { Length: > 20000 }) throw GatewayException.BadRequest("TOO_LONG", "Keep the notes under 20,000 characters.");
            var row = await _db.AccountingSettings.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (row == null) _db.AccountingSettings.Add(row = new AccountingSetting { ClientId = clientId });
            row.PolicyNotes = text; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.policies", Detail = text == null ? "standard wording" : $"{text.Length} characters" });
            await _db.SaveChangesAsync(ct);
            return await PolicyNotesAsync(clientId, ct);
        }

        // ---- the annual financial statements, as one document ----

        public async Task<(byte[] Pdf, string FileName)> PackAsync(Guid clientId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var f = from.Date; var t = to.Date;
            if (t < f) throw GatewayException.BadRequest("RANGE_INVALID", "The end date is before the start date.");
            var pl = await _reports.ProfitLossAsync(clientId, currency, f, t, ct, "year");
            var bs = await _reports.BalanceSheetAsync(clientId, currency, t, ct, "year");
            var cf = await _reports.CashFlowAsync(clientId, currency, f, t, ct);
            var soce = await ChangesInEquityAsync(clientId, currency, f, t, ct);
            var soceBefore = await ChangesInEquityAsync(clientId, currency, f.AddYears(-1), t.AddYears(-1), ct);
            var policies = (await PolicyNotesAsync(clientId, ct)).Text;
            var me = await CompanyIdentity.LoadAsync(_db, clientId, ct);
            var c = CultureInfo.InvariantCulture;
            string M(decimal v) => v < 0 ? "(" + (-v).ToString("N2", c) + ")" : v.ToString("N2", c);
            string D(DateTime d) => d.ToString("d MMMM yyyy", c);
            var cur = pl.Currency; var thisYear = t.ToString("yyyy", c); var lastYear = t.AddYears(-1).ToString("yyyy", c);
            const string accent = "#0f766e";
            var notes = new List<(string Title, StatementSection Section)>();

            var pdf = Document.Create(doc =>
            {
                void Page(Action<ColumnDescriptor> body) => doc.Page(page =>
                {
                    page.Size(PageSizes.A4); page.Margin(40); page.DefaultTextStyle(x => x.FontSize(9.5f));
                    page.Header().Column(h =>
                    {
                        h.Item().Text(me.Name).Bold().FontSize(11).FontColor(accent);
                        h.Item().Text($"Financial statements for the period {D(f)} to {D(t)} · {cur}").FontSize(8).FontColor(Colors.Grey.Darken1);
                        h.Item().PaddingTop(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    });
                    page.Content().PaddingTop(10).Column(body);
                    page.Footer().AlignCenter().Text(x => { x.Span("Page ").FontSize(8); x.CurrentPageNumber().FontSize(8); x.Span(" of ").FontSize(8); x.TotalPages().FontSize(8); });
                });
                void Title(ColumnDescriptor col, string text) => col.Item().PaddingBottom(6).Text(text).Bold().FontSize(13).FontColor(accent);
                void Grid(ColumnDescriptor col, Action<Action<string, decimal?, decimal?, bool, bool, string?>> rows)
                {
                    col.Item().Table(tb =>
                    {
                        tb.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(34); cd.ConstantColumn(90); cd.ConstantColumn(90); });
                        tb.Cell().Text(""); tb.Cell().AlignRight().Text("Note").FontSize(8).FontColor(Colors.Grey.Darken1);
                        tb.Cell().AlignRight().Text(thisYear).Bold(); tb.Cell().AlignRight().Text(lastYear).Bold().FontColor(Colors.Grey.Darken1);
                        rows((label, now, before, bold, line, note) =>
                        {
                            IContainer Cell() { var x = tb.Cell().PaddingVertical(2.5f); return line ? x.BorderTop(0.75f).BorderColor(Colors.Grey.Darken1) : x; }
                            var a = Cell().Text(label); if (bold) a.Bold();
                            Cell().AlignRight().Text(note ?? "").FontSize(8);
                            var b = Cell().AlignRight().Text(now is { } n ? M(n) : ""); if (bold) b.Bold();
                            var d = Cell().AlignRight().Text(before is { } p ? M(p) : "").FontColor(Colors.Grey.Darken2); if (bold) d.Bold();
                        });
                    });
                }
                string? NoteFor(StatementSection s, string title)
                {
                    if (s.Rows.Count == 0) return null;
                    notes.Add((title, s));
                    return (notes.Count + 1).ToString(c);   // note 1 is the accounting policies
                }

                // cover
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4); page.Margin(60);
                    page.Content().AlignMiddle().Column(col =>
                    {
                        col.Spacing(8);
                        col.Item().Text(me.Name).Bold().FontSize(24).FontColor(accent);
                        if (me.Tin != null) col.Item().Text("TIN " + me.Tin).FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingTop(20).Text("Annual financial statements").FontSize(18);
                        col.Item().Text($"for the period ended {D(t)}").FontSize(13);
                        col.Item().Text($"Presented in {cur}").FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingTop(30).Text("Contents").Bold();
                        foreach (var x in new[] { "Statement of financial position", "Statement of profit or loss", "Statement of changes in equity", "Statement of cash flows", "Notes to the financial statements" })
                            col.Item().Text("•  " + x);
                        col.Item().PaddingTop(30).Text($"Prepared {DateTime.UtcNow.AddHours(2):d MMMM yyyy}. These statements have not been audited.").FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                });

                // financial position
                Page(col =>
                {
                    Title(col, $"Statement of financial position as at {D(t)}");
                    Grid(col, row =>
                    {
                        StatementSection S(string key) => bs.Sections.First(s => s.Key == key);
                        void Section(string key, string heading, string noteTitle)
                        {
                            var s = S(key); if (s.Rows.Count == 0) return;
                            row(heading, s.Total, s.CompareTotal, false, false, NoteFor(s, noteTitle));
                        }
                        row("ASSETS", null, null, true, false, null);
                        Section("non_current_assets", "Non-current assets", "Non-current assets");
                        Section("current_assets", "Current assets", "Current assets");
                        row("Total assets", bs.TotalAssets, bs.CompareTotalAssets, true, true, null);
                        row(" ", null, null, false, false, null);
                        row("EQUITY AND LIABILITIES", null, null, true, false, null);
                        Section("equity", "Equity", "Equity");
                        Section("non_current_liabilities", "Non-current liabilities", "Non-current liabilities");
                        Section("current_liabilities", "Current liabilities", "Current liabilities");
                        row("Total equity and liabilities", bs.TotalLiabilitiesAndEquity, bs.CompareTotalLiabilitiesAndEquity, true, true, null);
                    });
                    if (!bs.Balanced) col.Item().PaddingTop(8).Text("The statement does not balance: check the trial balance before relying on it.").FontColor(Colors.Red.Darken2);
                });

                // profit or loss
                Page(col =>
                {
                    Title(col, $"Statement of profit or loss for the period ended {D(t)}");
                    Grid(col, row =>
                    {
                        StatementSection S(string key) => pl.Sections.First(s => s.Key == key);
                        void Line(string key, string label, bool minus)
                        {
                            var s = S(key); if (s.Rows.Count == 0) return;
                            row(label, minus ? -s.Total : s.Total, s.CompareTotal is { } x ? (minus ? -x : x) : null, false, false, NoteFor(s, label));
                        }
                        Line("revenue", "Revenue", false);
                        Line("cost_of_sales", "Cost of sales", true);
                        row("Gross profit", pl.GrossProfit, pl.CompareGrossProfit, true, true, null);
                        Line("other_income", "Other income", false);
                        Line("operating_expense", "Operating expenses", true);
                        row("Operating profit", pl.OperatingProfit, pl.CompareOperatingProfit, true, true, null);
                        Line("finance_cost", "Finance costs", true);
                        row("Profit before tax", pl.ProfitBeforeTax, pl.CompareProfitBeforeTax, true, true, null);
                        Line("income_tax", "Income tax", true);
                        row("Profit for the period", pl.NetProfit, pl.CompareNetProfit, true, true, null);
                    });
                });

                // changes in equity
                Page(col =>
                {
                    Title(col, $"Statement of changes in equity for the period ended {D(t)}");
                    void Table(ChangesInEquityDto e, string label)
                    {
                        col.Item().PaddingTop(6).Text(label).SemiBold();
                        col.Item().Table(tb =>
                        {
                            tb.ColumnsDefinition(cd => { cd.RelativeColumn(); for (var i = 0; i < 4; i++) cd.ConstantColumn(82); });
                            foreach (var h in new[] { "", "Opening", "Profit", "Other movements", "Closing" }) tb.Cell().BorderBottom(0.75f).PaddingVertical(3).AlignRight().Text(h).Bold().FontSize(8.5f);
                            foreach (var r in e.Rows.Append(e.Total))
                            {
                                var total = r == e.Total;
                                IContainer Cell() { var x = tb.Cell().PaddingVertical(2.5f); return total ? x.BorderTop(0.75f) : x; }
                                var a = Cell().Text(r.Component); if (total) a.Bold();
                                foreach (var v in new[] { r.Opening, r.Profit, r.Other, r.Closing }) { var b = Cell().AlignRight().Text(M(v)); if (total) b.Bold(); }
                            }
                        });
                    }
                    Table(soce, $"{D(f)} to {D(t)}");
                    Table(soceBefore, $"{D(f.AddYears(-1))} to {D(t.AddYears(-1))}");
                    col.Item().PaddingTop(8).Text("Other movements are capital introduced, drawings and dividends, and any other entries made directly to equity.").FontSize(8).FontColor(Colors.Grey.Darken1);
                });

                // cash flows
                Page(col =>
                {
                    Title(col, $"Statement of cash flows for the period ended {D(t)}");
                    col.Item().Table(tb =>
                    {
                        tb.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(100); });
                        void R(string label, decimal? v, bool bold = false, bool line = false)
                        {
                            IContainer Cell() { var x = tb.Cell().PaddingVertical(2.5f); return line ? x.BorderTop(0.75f) : x; }
                            var a = Cell().Text(label); if (bold) a.Bold();
                            var b = Cell().AlignRight().Text(v is { } n ? M(n) : ""); if (bold) b.Bold();
                        }
                        R("Profit for the period", cf.NetProfit, true);
                        foreach (var s in cf.Sections)
                        {
                            R(s.Title, null, true);
                            foreach (var l in s.Lines) R("   " + l.Label, l.Amount);
                            R("Net cash from " + s.Title.ToLowerInvariant(), s.Total, true, true);
                        }
                        R("Net change in cash", cf.NetChange, true, true);
                        R("Cash at the start of the period", cf.OpeningCash);
                        R("Cash at the end of the period", cf.ClosingCash, true, true);
                    });
                });

                // notes
                Page(col =>
                {
                    Title(col, "Notes to the financial statements");
                    col.Item().Text("1. Accounting policies").Bold().FontSize(11);
                    foreach (var para in policies.Replace("\r\n", "\n").Split("\n\n"))
                    {
                        var lines = para.Split('\n');
                        col.Item().PaddingTop(6).Text(lines[0]).SemiBold();
                        if (lines.Length > 1) col.Item().Text(string.Join(" ", lines.Skip(1)));
                    }
                    var n = 1;
                    foreach (var (title, s) in notes)
                    {
                        n++;
                        col.Item().PaddingTop(14).Text($"{n}. {title}").Bold().FontSize(11);
                        col.Item().Table(tb =>
                        {
                            tb.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(90); cd.ConstantColumn(90); });
                            tb.Cell().Text(""); tb.Cell().AlignRight().Text(thisYear).Bold(); tb.Cell().AlignRight().Text(lastYear).Bold().FontColor(Colors.Grey.Darken1);
                            foreach (var r in s.Rows)
                            {
                                tb.Cell().PaddingVertical(2).PaddingLeft(r.Level * 10).Text(r.Name);
                                tb.Cell().PaddingVertical(2).AlignRight().Text(M(r.Amount));
                                tb.Cell().PaddingVertical(2).AlignRight().Text(r.Compare is { } x ? M(x) : "").FontColor(Colors.Grey.Darken2);
                            }
                            tb.Cell().BorderTop(0.75f).PaddingVertical(2).Text("Total").Bold();
                            tb.Cell().BorderTop(0.75f).PaddingVertical(2).AlignRight().Text(M(s.Total)).Bold();
                            tb.Cell().BorderTop(0.75f).PaddingVertical(2).AlignRight().Text(s.CompareTotal is { } y ? M(y) : "").Bold();
                        });
                    }
                });
            }).GeneratePdf();
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.statements.pack", Detail = $"{cur} {f:yyyy-MM-dd} to {t:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return (pdf, $"financial-statements-{cur}-{t:yyyy-MM-dd}.pdf");
        }

        // ---- who changed the books ----

        private static readonly (string Prefix, string Label)[] Labels =
        {
            ("ledger.manual", "Journal posted"), ("ledger.reversal", "Journal reversed"), ("ledger.account.create", "Account created"), ("ledger.account.update", "Account changed"), ("ledger.account.delete", "Account deleted"),
            ("ledger.period", "Period closed or reopened"), ("ledger.year.close", "Year closed"), ("ledger.year.reopen", "Year reopened"), ("ledger.opening", "Opening balances"), ("ledger.conversion", "Balances imported"),
            ("ledger.invoice", "Invoice posted"), ("ledger.credit_note", "Credit note posted"), ("ledger.bill", "Bill posted"), ("ledger.billpay", "Bill paid"), ("ledger.arpay", "Customer payment"),
            ("ledger.cashdoc", "Cashbook document"), ("ledger.cashbook", "Cashbook entry"), ("ledger.recurring", "Recurring journal"), ("ledger.schedule", "Prepayment schedule"), ("ledger.rate", "Exchange rate"),
            ("ledger.base-currency", "Base currency"), ("ledger.renumber", "Chart renumbered"), ("ledger.chart", "Chart settings"), ("ledger.costcentre", "Cost centre"), ("ledger.statement", "Statement sent"),
            ("ledger.statements", "Financial statements"), ("ledger.policies", "Accounting policies"), ("ledger.depn", "Depreciation"), ("ledger.asset", "Fixed asset"), ("ledger.vat", "VAT"),
            ("ledger.", "Books"), ("sales.", "Sales invoice"), ("bill.", "Bill"), ("supplier.", "Supplier"), ("receivable.", "Customer payment"), ("budget.", "Budget"), ("bank.", "Bank reconciliation"), ("asset.", "Fixed asset"),
        };

        public async Task<IReadOnlyList<BooksAuditRow>> AuditAsync(Guid clientId, BooksAuditFilter f, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var q = _db.AuditLogs.AsNoTracking().Where(a => a.ClientId == clientId && (a.Action.StartsWith("ledger.") || a.Action.StartsWith("sales.") || a.Action.StartsWith("bill.")
                || a.Action.StartsWith("supplier.") || a.Action.StartsWith("receivable.") || a.Action.StartsWith("budget.") || a.Action.StartsWith("bank.") || a.Action.StartsWith("asset.")));
            if (f.From is { } from) q = q.Where(a => a.At >= from.Date.AddHours(-2));
            if (f.To is { } to) q = q.Where(a => a.At < to.Date.AddDays(1).AddHours(-2));
            if (!string.IsNullOrWhiteSpace(f.User)) { var u = f.User.Trim(); q = q.Where(a => a.UserName != null && a.UserName.Contains(u)); }
            if (!string.IsNullOrWhiteSpace(f.Q)) { var s = f.Q.Trim(); q = q.Where(a => a.Action.Contains(s) || (a.Detail != null && a.Detail.Contains(s))); }
            var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(Math.Clamp(f.Take, 1, 2000)).ToListAsync(ct);
            return rows.Select(a => new BooksAuditRow(a.Id, a.At, a.UserName ?? (a.ActorType == "system" ? "System" : null), a.Action,
                Labels.FirstOrDefault(l => a.Action.StartsWith(l.Prefix)).Label ?? a.Action, a.Detail, a.IpAddress)).ToList();
        }
    }
}
