using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Fdms.Infrastructure.Services
{
    /// <summary>Who the company is, for the top of its statements and exported reports (its name, and the taxpayer details ZIMRA has for its devices).</summary>
    public sealed record CompanyIdentity(string Name, string? Tin, string? Vat, string? Address, string? Email)
    {
        public static async Task<CompanyIdentity> LoadAsync(FdmsDbContext db, Guid clientId, CancellationToken ct)
        {
            if (PlatformBooks.Is(clientId))
            {
                var json = await db.SystemSettings.AsNoTracking().Where(s => s.Key == "BillingProfile").Select(s => s.Value).FirstOrDefaultAsync(ct);
                BillingProfile? p = null;
                try { if (json != null) p = JsonSerializer.Deserialize<BillingProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); } catch (JsonException) { }
                return new CompanyIdentity(string.IsNullOrWhiteSpace(p?.CompanyName) ? "FiscalHub" : p!.CompanyName, p?.TaxNumber, null, p?.Address, p?.Email);
            }
            var c = await db.Clients.AsNoTracking().Where(x => x.Id == clientId).Select(x => new { x.Name, x.Email }).FirstOrDefaultAsync(ct);
            var d = await db.Devices.AsNoTracking().Where(x => x.ClientId == clientId && x.TaxPayerTin != null).OrderBy(x => x.CreatedAt)
                .Select(x => new { x.TaxPayerName, x.TaxPayerTin, x.VatNumber, x.BranchAddress }).FirstOrDefaultAsync(ct);
            return new CompanyIdentity(d?.TaxPayerName ?? c?.Name ?? "Company", d?.TaxPayerTin, d?.VatNumber, d?.BranchAddress, c?.Email);
        }
    }

    internal static class Guard
    {
        public static void WholeCompany(ICallerScope scope)
        {
            if (scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
        }
    }

    // =================================================================================================================
    // cost centres
    // =================================================================================================================

    public sealed class CostCentreService : ICostCentreService
    {
        private static readonly string[] Kinds = { "department", "project", "branch" };
        private static readonly Regex CodeRx = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope;
        public CostCentreService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        public async Task<IReadOnlyList<CostCentreDto>> ListAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var list = await _db.CostCentres.AsNoTracking().Where(c => c.ClientId == clientId).OrderBy(c => c.Code).ToListAsync(ct);
            var branches = await _db.Branches.AsNoTracking().Where(b => b.ClientId == clientId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
            var ids = list.Select(c => (Guid?)c.Id).ToList();
            var used = (await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && ids.Contains(l.CostCentreId)).Select(l => l.CostCentreId).Distinct().ToListAsync(ct)).ToHashSet();
            return list.Select(c => new CostCentreDto(c.Id, c.Code, c.Name, c.Kind, c.BranchId, c.BranchId is { } b ? branches.GetValueOrDefault(b) : null, c.IsActive, used.Contains(c.Id))).ToList();
        }

        public async Task<CostCentreDto> SaveAsync(Guid clientId, SaveCostCentreRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var code = (req.Code ?? "").Trim(); var name = (req.Name ?? "").Trim(); var kind = (req.Kind ?? "department").Trim().ToLowerInvariant();
            if (!CodeRx.IsMatch(code)) throw GatewayException.BadRequest("CODE_INVALID", "The code is 1-20 letters or digits (for example HRE or PRJ-01).");
            if (name.Length is < 2 or > 100) throw GatewayException.BadRequest("NAME_INVALID", "Give it a name (2-100 characters).");
            if (!Kinds.Contains(kind)) throw GatewayException.BadRequest("KIND_INVALID", "Choose department, project or branch.");
            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            Guid? branch = kind == "branch" ? req.BranchId : null;
            if (kind == "branch" && branch == null) throw GatewayException.BadRequest("BRANCH_REQUIRED", "Choose the branch whose sales go to this cost centre.");
            if (branch != null && !await _db.Branches.AnyAsync(b => b.Id == branch && b.ClientId == clientId, ct)) throw GatewayException.BadRequest("BRANCH_INVALID", "That branch is not yours.");
            if (branch != null && await _db.CostCentres.AnyAsync(c => c.ClientId == clientId && c.BranchId == branch && c.Id != id && c.IsActive, ct))
                throw GatewayException.Conflict("BRANCH_TAKEN", "Another cost centre already takes that branch's sales.");
            if (await _db.CostCentres.AnyAsync(c => c.ClientId == clientId && c.Code == code && c.Id != id, ct)) throw GatewayException.Conflict("CODE_EXISTS", "Another cost centre already uses that code.");
            var c = id == null ? new CostCentre { ClientId = clientId } : await _db.CostCentres.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Cost centre");
            c.Code = code; c.Name = name; c.Kind = kind; c.BranchId = branch; c.IsActive = req.IsActive;
            if (id == null) _db.CostCentres.Add(c);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = id == null ? "ledger.costcentre.create" : "ledger.costcentre.update", Detail = $"{code} {name} ({kind})" });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(clientId, ct)).First(x => x.Id == c.Id);
        }

        public async Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var c = await _db.CostCentres.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Cost centre");
            if (await _db.JournalLines.AnyAsync(l => l.CostCentreId == id, ct) || await _db.Bills.AnyAsync(b => b.CostCentreId == id, ct))
                throw GatewayException.Conflict("COST_CENTRE_IN_USE", "Amounts are posted to this cost centre, so it cannot be deleted. Switch it off instead.");
            _db.CostCentres.Remove(c);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.costcentre.delete", Detail = $"{c.Code} {c.Name}" });
            await _db.SaveChangesAsync(ct);
        }
    }

    // =================================================================================================================
    // recurring journals, opening balances and closing the year
    // =================================================================================================================

    public sealed class BooksAutomationService : IBooksAutomationService
    {
        private static readonly string[] Frequencies = { "weekly", "monthly", "quarterly", "yearly" };
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IAccountingService _books; private readonly IAccountingReports _reports;
        private readonly IFixedAssetService _assets; private readonly ILogger<BooksAutomationService> _log;

        public BooksAutomationService(FdmsDbContext db, ICallerScope scope, IAccountingService books, IAccountingReports reports, IFixedAssetService assets, ILogger<BooksAutomationService> log)
        { _db = db; _scope = scope; _books = books; _reports = reports; _assets = assets; _log = log; }

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        // ---- recurring journals ----

        private static List<JournalLineInput> LinesOf(RecurringJournal r) { try { return JsonSerializer.Deserialize<List<JournalLineInput>>(r.LinesJson, Json) ?? new(); } catch (JsonException) { return new(); } }

        private static RecurringJournalDto Dto(RecurringJournal r) { var lines = LinesOf(r); return new(r.Id, r.Name, r.Currency, r.Memo, r.Reference, lines, r.Frequency, r.NextDate, r.EndDate, r.AutoReverse, r.IsActive, r.LastRunAt, r.LastEntryNumber, r.LastError, r.RunCount, lines.Sum(l => l.Debit)); }

        public static DateTime Advance(DateTime d, string frequency) => frequency switch { "weekly" => d.AddDays(7), "quarterly" => d.AddMonths(3), "yearly" => d.AddYears(1), _ => d.AddMonths(1) };

        public async Task<IReadOnlyList<RecurringJournalDto>> ListRecurringAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            return (await _db.RecurringJournals.AsNoTracking().Where(r => r.ClientId == clientId).OrderBy(r => r.NextDate).ToListAsync(ct)).Select(Dto).ToList();
        }

        public async Task<RecurringJournalDto> SaveRecurringAsync(Guid clientId, SaveRecurringJournalRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var name = (req.Name ?? "").Trim(); var memo = (req.Memo ?? "").Trim(); var freq = (req.Frequency ?? "").Trim().ToLowerInvariant(); var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (name.Length is < 2 or > 100) throw GatewayException.BadRequest("NAME_INVALID", "Give it a name (2-100 characters).");
            if (memo.Length is < 3 or > 200) throw GatewayException.BadRequest("MEMO_INVALID", "Describe the entry (3-200 characters).");
            if (!Frequencies.Contains(freq)) throw GatewayException.BadRequest("FREQUENCY_INVALID", "Choose weekly, monthly, quarterly or yearly.");
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (req.NextDate.Year < 2000 || req.NextDate > Today.AddYears(5)) throw GatewayException.BadRequest("DATE_INVALID", "Choose the date of the next posting.");
            if (req.EndDate is { } end && end.Date < req.NextDate.Date) throw GatewayException.BadRequest("DATE_INVALID", "The end date is before the next posting.");
            var lines = (req.Lines ?? Array.Empty<JournalLineInput>()).ToList();
            if (lines.Count is < 2 or > 60) throw GatewayException.BadRequest("LINES_INVALID", "An entry needs at least two lines.");
            if (lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0) == (l.Credit > 0) || l.Debit != Math.Round(l.Debit, 2) || l.Credit != Math.Round(l.Credit, 2)))
                throw GatewayException.BadRequest("LINE_INVALID", "Each line has either a debit or a credit amount (two decimals at most).");
            if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit)) throw GatewayException.BadRequest("UNBALANCED", "Debits and credits must be equal.");
            var ids = lines.Select(l => l.AccountId).Distinct().ToList();
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && ids.Contains(a.Id)).ToListAsync(ct);
            if (accounts.Count != ids.Count || accounts.Any(a => a.IsGroup || !a.IsActive)) throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose accounts in use from your chart (not headings).");
            await _books.CheckCentresAsync(clientId, lines.Select(l => l.CostCentreId), ct);

            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            var r = id == null ? new RecurringJournal { ClientId = clientId, CreatedBy = _scope.UserName } : await _db.RecurringJournals.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Recurring journal");
            r.Name = name; r.Memo = memo; r.Currency = cur; r.Frequency = freq; r.NextDate = req.NextDate.Date; r.EndDate = req.EndDate?.Date; r.AutoReverse = req.AutoReverse; r.IsActive = req.IsActive;
            r.Reference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim()[..Math.Min(50, req.Reference.Trim().Length)];
            r.LinesJson = JsonSerializer.Serialize(lines.Select(l => l with { Description = string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim() }), Json); r.LastError = null;
            if (id == null) _db.RecurringJournals.Add(r);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.recurring.save", Detail = $"{name}: {freq} from {r.NextDate:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return Dto(r);
        }

        public async Task DeleteRecurringAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var r = await _db.RecurringJournals.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Recurring journal");
            _db.RecurringJournals.Remove(r);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.recurring.delete", Detail = r.Name });
            await _db.SaveChangesAsync(ct);
        }

        /// <summary>Posts one occurrence (on its scheduled date) and moves the schedule on; an accrual is reversed on the first day of the next month.</summary>
        private async Task<JournalDto> PostOnceAsync(RecurringJournal r, CancellationToken ct)
        {
            var date = r.NextDate;
            DateTime? reverseOn = r.AutoReverse ? new DateTime(date.Year, date.Month, 1).AddMonths(1) : null;
            var entry = await _books.CreateJournalAsync(r.ClientId, new CreateJournalRequest(date, r.Currency, r.Memo, r.Reference ?? r.Name[..Math.Min(50, r.Name.Length)], LinesOf(r), reverseOn), ct);
            r.LastRunAt = DateTime.UtcNow; r.LastEntryNumber = entry.Number; r.RunCount++; r.LastError = null;
            r.NextDate = Advance(r.NextDate, r.Frequency);
            if (r.EndDate is { } end && r.NextDate > end) r.IsActive = false;
            await _db.SaveChangesAsync(ct);
            return entry;
        }

        public async Task<JournalDto> RunRecurringAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var r = await _db.RecurringJournals.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Recurring journal");
            if (!r.IsActive) throw GatewayException.Conflict("RECURRING_OFF", "Switch the recurring journal on first.");
            return await PostOnceAsync(r, ct);
        }

        public async Task<int> RunDueAsync(DateTime today, CancellationToken ct)
        {
            var due = await _db.RecurringJournals.Where(r => r.IsActive && r.NextDate <= today).OrderBy(r => r.NextDate).Take(200).ToListAsync(ct);
            var n = 0;
            foreach (var r in due)
            {
                for (var i = 0; i < 12 && r.IsActive && r.NextDate <= today; i++)   // catches up a few missed periods, never an endless backlog at once
                {
                    try { await PostOnceAsync(r, ct); n++; }
                    catch (Exception ex) when (ex is GatewayException or DbUpdateException)
                    {
                        _db.ChangeTracker.Clear();
                        var fresh = await _db.RecurringJournals.FirstAsync(x => x.Id == r.Id, ct);
                        fresh.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                        await _db.SaveChangesAsync(ct);
                        _log.LogWarning("Recurring journal {Id} could not post: {Message}", r.Id, ex.Message);
                        break;
                    }
                }
            }
            return n;
        }

        // ---- opening balances ----

        public async Task<JournalDto> OpeningBalancesAsync(Guid clientId, OpeningBalancesRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await _books.EnsureChartAsync(clientId, ct);
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (await _db.JournalEntries.AnyAsync(e => e.ClientId == clientId && e.Source == "opening" && e.Currency == cur && e.ReversedById == null, ct))
                throw GatewayException.Conflict("OPENING_EXISTS", $"Opening balances in {cur} were already entered. Reverse that entry under Journals to enter them again.");
            var lines = (req.Lines ?? Array.Empty<OpeningBalanceLine>()).Where(l => l.Debit != 0 || l.Credit != 0).ToList();
            if (lines.Count == 0) throw GatewayException.BadRequest("LINES_INVALID", "Enter at least one balance.");
            if (lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0 && l.Credit > 0) || l.Debit != Math.Round(l.Debit, 2) || l.Credit != Math.Round(l.Credit, 2)))
                throw GatewayException.BadRequest("LINE_INVALID", "Each balance is either a debit or a credit, with at most two decimals.");
            var ids = lines.Select(l => l.AccountId).Distinct().ToList();
            if (ids.Count != lines.Count) throw GatewayException.BadRequest("LINE_INVALID", "Enter each account once.");
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && ids.Contains(a.Id)).ToListAsync(ct);
            if (accounts.Count != ids.Count || accounts.Any(a => a.IsGroup || a.Type is "Income" or "Expense"))
                throw GatewayException.BadRequest("ACCOUNT_INVALID", "Opening balances are entered on asset, liability and equity accounts (not headings). Earlier profit belongs in retained earnings.");

            var post = lines.Select(l => (l.AccountId, l.Debit, l.Credit)).ToList();
            var diff = post.Sum(l => l.Debit) - post.Sum(l => l.Credit);
            if (diff != 0)
            {
                var balancing = await _db.LedgerAccounts.FirstOrDefaultAsync(a => a.ClientId == clientId && a.Role == "opening_balance", ct);
                if (balancing == null)
                {
                    var codes = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).Select(a => a.Code).ToListAsync(ct);
                    var code = ChartCodes.Next("Equity", null, codes, await ChartCodes.RulesAsync(_db, clientId, ct)).Code ?? "3090";
                    balancing = new LedgerAccount { ClientId = clientId, Code = code, Name = "Opening balance equity", Type = "Equity", Role = "opening_balance", SubType = "equity", IsSystem = true };
                    _db.LedgerAccounts.Add(balancing); await _db.SaveChangesAsync(ct);
                }
                post.Add(diff > 0 ? (balancing.Id, 0m, diff) : (balancing.Id, -diff, 0m));
            }
            return await _books.PostEntryAsync(clientId, req.Date.Date, cur, "Opening balances", "OPENING", "opening", null, post, ct);
        }

        // ---- closing the year ----

        private async Task<(DateTime Start, DateTime End)> YearAsync(Guid clientId, int? year, CancellationToken ct)
        {
            var month = (await ChartCodes.RulesAsync(_db, clientId, ct)).FiscalYearStartMonth;
            var lastEnded = ChartCodes.YearStart(Today, month).AddDays(-1);
            var end = year is { } y ? (month == 1 ? new DateTime(y, 12, 31) : new DateTime(y, month, 1).AddDays(-1)) : lastEnded;
            if (end.Year < 2000 || end.Year > Today.Year + 1) throw GatewayException.BadRequest("YEAR_INVALID", "Choose a financial year.");
            return (ChartCodes.YearStart(end, month), end);
        }

        private async Task<List<AmountDto>> ResultAsync(Guid clientId, DateTime start, DateTime end, CancellationToken ct)
        {
            var pl = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && (a.Type == "Income" || a.Type == "Expense")).Select(a => a.Id).ToListAsync(ct);
            return (await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && pl.Contains(l.AccountId) && l.Date >= start && l.Date <= end)
                .GroupBy(l => l.Currency).Select(g => new { g.Key, Net = g.Sum(x => x.Credit - x.Debit) }).ToListAsync(ct)).Select(x => new AmountDto(x.Key, x.Net)).OrderBy(x => x.Currency).ToList();
        }

        private static YearEndDto History(YearClose c) =>
            new(c.YearStart, c.YearEnd, c.ReopenedAt == null, c.ClosedAt, c.ClosedBy, JsonSerializer.Deserialize<List<AmountDto>>(c.ResultJson, Json) ?? new(), Array.Empty<YearEndCheck>(), null, Array.Empty<YearEndDto>());

        public async Task<YearEndDto> YearEndAsync(Guid clientId, int? year, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await _books.SyncSalesAsync(clientId, ct);
            var (start, end) = await YearAsync(clientId, year, ct);
            var closed = await _db.YearCloses.AsNoTracking().Where(c => c.ClientId == clientId && c.YearEnd == end && c.ReopenedAt == null).FirstOrDefaultAsync(ct);
            var checks = new List<YearEndCheck>();
            checks.Add(new("The year has ended", end < Today, end < Today ? null : $"It ends on {end:d MMM yyyy}."));
            foreach (var cur in await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Date <= end).Select(l => l.Currency).Distinct().ToListAsync(ct))
            {
                var tb = await _reports.TrialBalanceAsync(clientId, cur, end, ct);
                checks.Add(new($"Trial balance in {cur} balances", tb.Balanced, tb.Balanced ? null : $"Debits {tb.TotalDebit:N2}, credits {tb.TotalCredit:N2}."));
            }
            var unmatched = await _db.BankStatementLines.AsNoTracking().CountAsync(s => s.ClientId == clientId && s.Date <= end && s.JournalLineId == null, ct);
            checks.Add(new("Bank statements reconciled", unmatched == 0, unmatched == 0 ? null : $"{unmatched} statement line(s) up to the year end are not matched."));
            var monthEnd = new DateTime(end.Year, end.Month, 1);
            var activeAssets = await _db.FixedAssets.AsNoTracking().Where(a => a.ClientId == clientId && a.Status == "Active" && a.PurchaseDate <= end).Select(a => a.Id).ToListAsync(ct);
            var charged = await _db.DepreciationCharges.AsNoTracking().Where(c => activeAssets.Contains(c.AssetId) && c.Period == monthEnd).Select(c => c.AssetId).Distinct().CountAsync(ct);
            checks.Add(new("Depreciation run to the year end", charged >= activeAssets.Count, charged >= activeAssets.Count ? null : $"{activeAssets.Count - charged} asset(s) have no charge for {end:MMM yyyy} (it can be run when closing)."));
            var history = (await _db.YearCloses.AsNoTracking().Where(c => c.ClientId == clientId).OrderByDescending(c => c.YearEnd).ThenByDescending(c => c.ClosedAt).Take(20).ToListAsync(ct)).Select(History).ToList();
            return new YearEndDto(start, end, closed != null, closed?.ClosedAt, closed?.ClosedBy, await ResultAsync(clientId, start, end, ct), checks, await _books.LockedUntilAsync(clientId, ct), history);
        }

        public async Task<YearEndDto> CloseYearAsync(Guid clientId, CloseYearRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var (start, end) = await YearAsync(clientId, req.YearEnd.Year, ct);   // a financial year is named by the calendar year it ends in
            if (end != req.YearEnd.Date) throw GatewayException.BadRequest("YEAR_INVALID", $"Your financial year ends on {end:d MMM yyyy}.");
            if (end >= Today) throw GatewayException.Conflict("YEAR_OPEN", "A year can be closed once it has ended.");
            if (await _db.YearCloses.AnyAsync(c => c.ClientId == clientId && c.YearEnd == end && c.ReopenedAt == null, ct)) throw GatewayException.Conflict("YEAR_CLOSED", "That year is closed already.");
            if (req.Depreciate) await _assets.DepreciateAsync(clientId, end, ct);
            var state = await YearEndAsync(clientId, end.Year, ct);
            if (state.Checks.FirstOrDefault(c => c.Label.StartsWith("Trial balance") && !c.Ok) is { } bad) throw GatewayException.Conflict("TB_UNBALANCED", bad.Label + " — fix it before closing.");
            var locked = await _books.LockedUntilAsync(clientId, ct);
            if (locked == null || locked < end) await _books.SetLockAsync(clientId, end, ct);
            _db.YearCloses.Add(new YearClose { ClientId = clientId, YearStart = start, YearEnd = end, ResultJson = JsonSerializer.Serialize(state.NetProfit, Json), ClosedBy = _scope.UserName });
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.year.close", Detail = $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}: {string.Join(", ", state.NetProfit.Select(p => $"{p.Amount:N2} {p.Currency}"))}" });
            await _db.SaveChangesAsync(ct);
            return await YearEndAsync(clientId, end.Year, ct);
        }

        public async Task<YearEndDto> ReopenYearAsync(Guid clientId, DateTime yearEnd, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var c = await _db.YearCloses.FirstOrDefaultAsync(x => x.ClientId == clientId && x.YearEnd == yearEnd.Date && x.ReopenedAt == null, ct) ?? throw GatewayException.NotFound("Closed year");
            c.ReopenedAt = DateTime.UtcNow; c.ReopenedBy = _scope.UserName;
            var earlier = await _db.YearCloses.AsNoTracking().Where(x => x.ClientId == clientId && x.ReopenedAt == null && x.YearEnd < c.YearStart).OrderByDescending(x => x.YearEnd).Select(x => (DateTime?)x.YearEnd).FirstOrDefaultAsync(ct);
            var locked = await _books.LockedUntilAsync(clientId, ct);
            if (locked != null && locked >= c.YearStart) await _books.SetLockAsync(clientId, earlier, ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.year.reopen", Detail = $"{c.YearStart:yyyy-MM-dd} to {c.YearEnd:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return await YearEndAsync(clientId, c.YearEnd.Year, ct);
        }
    }

    /// <summary>Once a day: posts the recurring journals that are due.</summary>
    public sealed class RecurringJournalJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes; private readonly ILogger<RecurringJournalJob> _log;
        public RecurringJournalJob(IServiceScopeFactory scopes, ILogger<RecurringJournalJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<IBooksAutomationService>().RunDueAsync(DateTime.UtcNow.AddHours(2).Date, stop);
                    if (n > 0) _log.LogInformation("Posted {N} recurring journal(s)", n);
                    var m = await scope.ServiceProvider.GetRequiredService<IScheduleService>().RunAllDueAsync(DateTime.UtcNow.AddHours(2).Date, stop);
                    if (m > 0) _log.LogInformation("Released {N} month(s) of prepayments and deferred income", m);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Recurring journals round failed"); }
                try { await Task.Delay(TimeSpan.FromHours(1), stop); } catch (OperationCanceledException) { break; }
            }
        }
    }

    // =================================================================================================================
    // statements as PDF and by email
    // =================================================================================================================

    public sealed class StatementService : IStatementService
    {
        static StatementService() => QuestPDF.Settings.License = LicenseType.Community;
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IReceivablesService _ar; private readonly IPayablesService _ap; private readonly IEmailSender _email;
        public StatementService(FdmsDbContext db, ICallerScope scope, IReceivablesService ar, IPayablesService ap, IEmailSender email) { _db = db; _scope = scope; _ar = ar; _ap = ap; _email = email; }

        private static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9_-]+", "-").Trim('-') is { Length: > 0 } x ? x[..Math.Min(40, x.Length)] : "statement";

        /// <summary>What is still owing at the end date, by how long it has been due.</summary>
        private static StatementAgeing Age(IEnumerable<(DateTime Due, decimal Outstanding)> items, DateTime asAt)
        {
            decimal cur = 0, a = 0, b = 0, c = 0, d = 0;
            foreach (var (due, amt) in items.Where(i => i.Outstanding > 0))
            {
                var days = (asAt - due).Days;
                if (days <= 0) cur += amt; else if (days <= 30) a += amt; else if (days <= 60) b += amt; else if (days <= 90) c += amt; else d += amt;
            }
            return new StatementAgeing(cur, a, b, c, d);
        }

        public async Task<(byte[] Pdf, string FileName)> CustomerPdfAsync(Guid clientId, string customer, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var st = await _ar.StatementAsync(clientId, customer, currency, from, to, ct);
            var open = await _ar.ListAsync(clientId, new OpenInvoiceFilter("open", customer, null, st.Currency), ct);
            var ageing = Age(open.Where(i => i.Date <= to).Select(i => (i.DueDate, i.Outstanding)), to.Date);
            var me = await CompanyIdentity.LoadAsync(_db, clientId, ct);
            return (Render(me, "CUSTOMER STATEMENT", st.Customer, st.Tin, st.Currency, st.From, st.To, st.Opening, st.Lines, st.Closing, ageing, "Amount due"), $"statement-{Safe(st.Customer)}-{st.To:yyyy-MM-dd}.pdf");
        }

        public async Task<(byte[] Pdf, string FileName)> SupplierPdfAsync(Guid clientId, Guid supplierId, string currency, DateTime from, DateTime to, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var st = await _ap.StatementAsync(clientId, supplierId, currency, from, to, ct);
            var bills = await _ap.ListBillsAsync(clientId, new BillFilter("open", supplierId, null, st.Currency), ct);
            var ageing = Age(bills.Where(b => b.Date <= to).Select(b => (b.DueDate, b.Outstanding)), to.Date);
            var me = await CompanyIdentity.LoadAsync(_db, clientId, ct);
            return (Render(me, "SUPPLIER STATEMENT", st.Supplier, st.Tin, st.Currency, st.From, st.To, st.Opening, st.Lines, st.Closing, ageing, "Balance owed"), $"statement-{Safe(st.Supplier)}-{st.To:yyyy-MM-dd}.pdf");
        }

        private static byte[] Render(CompanyIdentity me, string title, string party, string? partyTin, string cur, DateTime from, DateTime to, decimal opening, IReadOnlyList<StatementLine> lines, decimal closing,
            StatementAgeing ageing, string dueLabel)
        {
            var c = CultureInfo.InvariantCulture;
            string M(decimal v) => v.ToString("N2", c);
            string D(DateTime d) => d.ToString("d MMM yyyy", c);
            const string accent = "#0f766e";
            return Document.Create(doc => doc.Page(page =>
            {
                page.Size(PageSizes.A4); page.Margin(36); page.DefaultTextStyle(t => t.FontSize(9));
                page.Header().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(me.Name).Bold().FontSize(14).FontColor(accent);
                        if (me.Address != null) col.Item().Text(me.Address);
                        if (me.Tin != null) col.Item().Text("TIN " + me.Tin + (me.Vat != null ? "   VAT " + me.Vat : ""));
                        if (me.Email != null) col.Item().Text(me.Email);
                    });
                    row.ConstantItem(220).Column(col =>
                    {
                        col.Item().AlignRight().Text(title).Bold().FontSize(15).FontColor(accent);
                        col.Item().AlignRight().Text($"{D(from)} to {D(to)}");
                        col.Item().AlignRight().Text("Currency " + cur);
                    });
                });
                page.Content().PaddingTop(14).Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(p =>
                    {
                        p.Item().Text(party).Bold().FontSize(11);
                        if (!string.IsNullOrEmpty(partyTin)) p.Item().Text("TIN " + partyTin);
                    });
                    col.Item().Table(t =>
                    {
                        t.ColumnsDefinition(cd => { cd.ConstantColumn(64); cd.ConstantColumn(80); cd.RelativeColumn(); cd.ConstantColumn(70); cd.ConstantColumn(70); cd.ConstantColumn(76); });
                        void H(string s, bool right = false) { var cell = t.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(4); if (right) cell.AlignRight().Text(s).Bold(); else cell.Text(s).Bold(); }
                        H("Date"); H("Reference"); H("Description"); H("Debit", true); H("Credit", true); H("Balance", true);
                        void Row(string a, string b, string desc, string dr, string cr, string bal, bool bold = false)
                        {
                            foreach (var (v, right) in new[] { (a, false), (b, false), (desc, false), (dr, true), (cr, true), (bal, true) })
                            {
                                var cell = t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3);
                                var text = right ? cell.AlignRight().Text(v) : cell.Text(v);
                                if (bold) text.Bold();
                            }
                        }
                        Row("", "", "Opening balance", "", "", M(opening), true);
                        foreach (var l in lines) Row(D(l.Date), l.Reference, l.Description, l.Debit != 0 ? M(l.Debit) : "", l.Credit != 0 ? M(l.Credit) : "", M(l.Balance));
                        Row("", "", "Closing balance", M(lines.Sum(x => x.Debit)), M(lines.Sum(x => x.Credit)), M(closing), true);
                    });
                    col.Item().PaddingTop(6).Table(t =>
                    {
                        t.ColumnsDefinition(cd => { for (var i = 0; i < 6; i++) cd.RelativeColumn(); });
                        foreach (var h in new[] { "Current", "1-30 days", "31-60 days", "61-90 days", "Over 90 days", dueLabel })
                            t.Cell().Background(Colors.Grey.Lighten3).Padding(4).AlignCenter().Text(h).Bold();
                        foreach (var v in new[] { ageing.Current, ageing.Days1To30, ageing.Days31To60, ageing.Days61To90, ageing.Over90, closing })
                            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4).AlignCenter().Text(M(v));
                    });
                });
                page.Footer().AlignCenter().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" of "); x.TotalPages(); x.Span($" · {me.Name} · generated {DateTime.UtcNow.AddHours(2):d MMM yyyy HH:mm}"); });
            })).GeneratePdf();
        }

        public async Task<EmailStatementResult> EmailAsync(Guid clientId, EmailStatementRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            byte[] pdf; string file; string party; string? email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
            if (req.SupplierId is { } sid)
            {
                (pdf, file) = await SupplierPdfAsync(clientId, sid, req.Currency, req.From, req.To, ct);
                var s = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Supplier");
                party = s.Name; email ??= s.Email;
            }
            else if (!string.IsNullOrWhiteSpace(req.Customer))
            {
                (pdf, file) = await CustomerPdfAsync(clientId, req.Customer, req.Currency, req.From, req.To, ct);
                var open = await _ar.ListAsync(clientId, new OpenInvoiceFilter(null, req.Customer, null, null, 1), ct);
                party = open.FirstOrDefault()?.Customer ?? req.Customer;
                var tin = open.FirstOrDefault()?.Tin;
                email ??= await _db.Customers.AsNoTracking().Where(c => c.ClientId == clientId && ((tin != null && c.Tin == tin) || c.Name == party) && c.Email != null).Select(c => c.Email).FirstOrDefaultAsync(ct);
            }
            else throw GatewayException.BadRequest("PARTY_REQUIRED", "Choose the customer or supplier.");
            if (email == null || !Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw GatewayException.BadRequest("EMAIL_REQUIRED", $"There is no email address for {party}. Type one.");
            if (!_email.Configured) throw GatewayException.Conflict("EMAIL_NOT_SET_UP", "Email is not set up on this server, so the statement cannot be sent. Download the PDF instead.");
            var me = await CompanyIdentity.LoadAsync(_db, clientId, ct);
            var note = string.IsNullOrWhiteSpace(req.Message) ? "" : $"<p>{System.Net.WebUtility.HtmlEncode(req.Message.Trim())}</p>";
            var html = $"<p>Dear {System.Net.WebUtility.HtmlEncode(party)},</p>{note}<p>Please find attached your statement from {System.Net.WebUtility.HtmlEncode(me.Name)} for {req.From:d MMM yyyy} to {req.To:d MMM yyyy}.</p><p>Kind regards,<br/>{System.Net.WebUtility.HtmlEncode(me.Name)}</p>";
            if (await _email.TrySendAsync(email, $"Statement from {me.Name}", html, new[] { new EmailAttachment(file, pdf, "application/pdf") }, ct) is { } refused)
                throw GatewayException.Conflict("EMAIL_FAILED", "The mail server did not take the statement: " + refused);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.statement.email", Detail = $"{party} → {email}" });
            await _db.SaveChangesAsync(ct);
            return new EmailStatementResult(email);
        }
    }

    // =================================================================================================================
    // exporting any accounting table as PDF, Excel or CSV
    // =================================================================================================================

    public sealed class ReportExporter : IReportExporter
    {
        static ReportExporter() => QuestPDF.Settings.License = LicenseType.Community;
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope;
        public ReportExporter(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        private static decimal? Number(string? s) =>
            s != null && decimal.TryParse(s.Replace(",", "").Trim(), NumberStyles.Number | NumberStyles.AllowParentheses, CultureInfo.InvariantCulture, out var v) ? v : null;

        public async Task<(byte[] Bytes, string ContentType, string FileName)> ExportAsync(Guid clientId, ExportRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var format = (req.Format ?? "").Trim().ToLowerInvariant();
            if (format is not ("pdf" or "xlsx" or "csv")) throw GatewayException.BadRequest("FORMAT_INVALID", "Choose PDF, Excel or CSV.");
            var cols = req.Columns ?? Array.Empty<ExportColumn>();
            var rows = req.Rows ?? Array.Empty<ExportRow>();
            if (cols.Count is < 1 or > 30) throw GatewayException.BadRequest("COLUMNS_INVALID", "A report has 1 to 30 columns.");
            if (rows.Count > 20000) throw GatewayException.BadRequest("TOO_LARGE", "At most 20 000 rows can be exported at once.");
            var title = string.IsNullOrWhiteSpace(req.Title) ? "Report" : req.Title.Trim()[..Math.Min(120, req.Title.Trim().Length)];
            var me = await CompanyIdentity.LoadAsync(_db, clientId, ct);
            var name = Regex.Replace(string.IsNullOrWhiteSpace(req.FileName) ? title : req.FileName, "[^A-Za-z0-9_-]+", "-").Trim('-').ToLowerInvariant();
            if (name.Length == 0) name = "report";
            return format switch
            {
                "csv" => (Csv(cols, rows), "text/csv", name + ".csv"),
                "xlsx" => (Excel(me, title, req.Subtitle, cols, rows), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx"),
                _ => (Pdf(me, title, req.Subtitle, cols, rows, req.Landscape || cols.Count > 6), "application/pdf", name + ".pdf"),
            };
        }

        private static byte[] Csv(IReadOnlyList<ExportColumn> cols, IReadOnlyList<ExportRow> rows)
        {
            static string Esc(string? v) { var s = v ?? ""; return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", cols.Select(c => Esc(c.Header))));
            foreach (var r in rows) sb.AppendLine(string.Join(",", Enumerable.Range(0, cols.Count).Select(i => Esc((i == 0 ? new string(' ', r.Indent * 2) : "") + (i < r.Cells.Count ? r.Cells[i] : "")))));
            return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        }

        private static byte[] Excel(CompanyIdentity me, string title, string? subtitle, IReadOnlyList<ExportColumn> cols, IReadOnlyList<ExportRow> rows)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(title.Length > 31 ? title[..31] : Regex.Replace(title, @"[\[\]\*\?/\\:]", " "));
            ws.Cell(1, 1).Value = me.Name; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 13;
            ws.Cell(2, 1).Value = title; ws.Cell(2, 1).Style.Font.Bold = true;
            if (!string.IsNullOrWhiteSpace(subtitle)) ws.Cell(3, 1).Value = subtitle;
            const int head = 5;
            for (var i = 0; i < cols.Count; i++)
            {
                var cell = ws.Cell(head, i + 1); cell.Value = cols[i].Header; cell.Style.Font.Bold = true; cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0");
                if (cols[i].Kind != "text") cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            var r = head;
            foreach (var row in rows)
            {
                r++;
                for (var i = 0; i < cols.Count; i++)
                {
                    var v = i < row.Cells.Count ? row.Cells[i] : null;
                    var cell = ws.Cell(r, i + 1);
                    if (cols[i].Kind != "text" && Number(v) is { } n) { cell.Value = n; cell.Style.NumberFormat.Format = cols[i].Kind == "money" ? "#,##0.00;(#,##0.00)" : "#,##0.##"; }
                    else cell.Value = v ?? "";
                    if (i == 0 && row.Indent > 0) cell.Style.Alignment.Indent = row.Indent;
                }
                var range = ws.Range(r, 1, r, cols.Count);
                switch (row.Style)
                {
                    case "bold": range.Style.Font.Bold = true; break;
                    case "total": range.Style.Font.Bold = true; range.Style.Border.TopBorder = XLBorderStyleValues.Thin; break;
                    case "section": range.Style.Font.Bold = true; range.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9"); break;
                    case "muted": range.Style.Font.FontColor = XLColor.Gray; break;
                }
            }
            ws.SheetView.FreezeRows(head);
            ws.Columns(1, cols.Count).AdjustToContents(head, r);
            foreach (var col in ws.Columns(1, cols.Count)) if (col.Width > 60) col.Width = 60;
            using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
        }

        private static byte[] Pdf(CompanyIdentity me, string title, string? subtitle, IReadOnlyList<ExportColumn> cols, IReadOnlyList<ExportRow> rows, bool landscape)
        {
            var c = CultureInfo.InvariantCulture;
            return Document.Create(doc => doc.Page(page =>
            {
                page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4); page.Margin(30); page.DefaultTextStyle(t => t.FontSize(8.5f));
                page.Header().PaddingBottom(8).Column(h =>
                {
                    h.Item().Text(me.Name).Bold().FontSize(12).FontColor("#0f766e");
                    h.Item().Text(title).Bold().FontSize(14);
                    if (!string.IsNullOrWhiteSpace(subtitle)) h.Item().Text(subtitle).FontColor(Colors.Grey.Darken1);
                });
                page.Content().Table(t =>
                {
                    t.ColumnsDefinition(cd => { for (var i = 0; i < cols.Count; i++) if (i == 0 || cols[i].Kind == "text") cd.RelativeColumn(i == 0 ? 3 : 2); else cd.RelativeColumn(1.3f); });
                    t.Header(hd =>
                    {
                        foreach (var col in cols)
                        {
                            var cell = hd.Cell().Background(Colors.Grey.Lighten3).PaddingVertical(4).PaddingHorizontal(3);
                            if (col.Kind == "text") cell.Text(col.Header).Bold(); else cell.AlignRight().Text(col.Header).Bold();
                        }
                    });
                    foreach (var row in rows)
                    {
                        for (var i = 0; i < cols.Count; i++)
                        {
                            var v = i < row.Cells.Count ? row.Cells[i] ?? "" : "";
                            var cell = t.Cell().BorderBottom(row.Style == "total" ? 1 : 0.4f).BorderColor(row.Style == "total" ? Colors.Grey.Darken1 : Colors.Grey.Lighten2)
                                .Background(row.Style == "section" ? Colors.Grey.Lighten4 : Colors.White).PaddingVertical(2.5f).PaddingHorizontal(3);
                            if (i == 0 && row.Indent > 0) cell = cell.PaddingLeft(3 + row.Indent * 9);
                            var text = cols[i].Kind == "text" ? cell.Text(v) : cell.AlignRight().Text(v);
                            if (row.Style is "bold" or "total" or "section") text.Bold();
                            if (row.Style == "muted") text.FontColor(Colors.Grey.Darken1);
                        }
                    }
                });
                page.Footer().AlignCenter().Text(x => { x.Span("Page "); x.CurrentPageNumber(); x.Span(" of "); x.TotalPages(); x.Span($" · generated {DateTime.UtcNow.AddHours(2).ToString("d MMM yyyy HH:mm", c)}"); });
            })).GeneratePdf();
        }
    }
}
