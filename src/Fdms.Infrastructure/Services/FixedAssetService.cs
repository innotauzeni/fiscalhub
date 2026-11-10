using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// The register of things the company owns for years. Each month's depreciation is charged once (never twice for the same asset and month) and posted to the books; selling or scrapping an asset
    /// removes its cost and accumulated depreciation and books the gain or loss.
    /// </summary>
    public sealed class FixedAssetService : IFixedAssetService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IAccountingService _books;
        public FixedAssetService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
        }

        private static DateTime MonthStart(DateTime d) => new(d.Year, d.Month, 1);
        private static DateTime MonthEnd(DateTime d) => MonthStart(d).AddMonths(1).AddDays(-1);

        private async Task<FixedAssetDto> MapAsync(FixedAsset a, IReadOnlyDictionary<Guid, string> names, decimal accumulated, DateTime asAt)
        {
            var disposed = a.Status == "Disposed" && a.DisposedOn != null && a.DisposedOn.Value.Date <= asAt;
            await Task.CompletedTask;
            return new FixedAssetDto(a.Id, a.Name, a.Code, a.PurchaseDate, a.Currency, a.Cost, a.ResidualValue, a.UsefulLifeMonths, a.Method, a.RatePercent, names.GetValueOrDefault(a.AssetAccountId, ""), names.GetValueOrDefault(a.AccumulatedAccountId, ""),
                names.GetValueOrDefault(a.ExpenseAccountId, ""), disposed ? "Disposed" : "Active", a.DisposedOn, a.DisposalProceeds, a.Notes, accumulated, disposed ? 0 : a.Cost - accumulated,
                a.Method == "straight_line" ? MonthStart(a.PurchaseDate).AddMonths(a.UsefulLifeMonths - 1) : null);
        }

        public async Task<AssetRegisterDto> RegisterAsync(Guid clientId, DateTime asAt, CancellationToken ct)
        {
            RequireWholeCompany();
            await _books.EnsureChartAsync(clientId, ct);
            var at = asAt.Date;
            var assets = await _db.FixedAssets.AsNoTracking().Where(a => a.ClientId == clientId && a.PurchaseDate <= at).OrderBy(a => a.PurchaseDate).ThenBy(a => a.Name).ToListAsync(ct);
            var charges = (await _db.DepreciationCharges.AsNoTracking().Where(c => c.ClientId == clientId && c.Period <= at).ToListAsync(ct)).GroupBy(c => c.AssetId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            var ids = assets.SelectMany(a => new[] { a.AssetAccountId, a.AccumulatedAccountId, a.ExpenseAccountId }).Distinct().ToList();
            var names = await _db.LedgerAccounts.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code + " " + x.Name, ct);
            var list = new List<FixedAssetDto>();
            foreach (var a in assets) list.Add(await MapAsync(a, names, charges.GetValueOrDefault(a.Id), at));
            var live = list.Where(a => a.Status == "Active").ToList();
            IReadOnlyList<AmountDto> Sum(Func<FixedAssetDto, decimal> pick) => live.GroupBy(a => a.Currency).OrderBy(g => g.Key).Select(g => new AmountDto(g.Key, g.Sum(pick))).ToList();
            return new AssetRegisterDto(at, list, Sum(a => a.Cost), Sum(a => a.AccumulatedDepreciation), Sum(a => a.NetBookValue));
        }

        private async Task<LedgerAccount> AccountAsync(Guid clientId, Guid id, string what, CancellationToken ct) =>
            await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.ClientId == clientId && a.IsActive, ct) ?? throw GatewayException.BadRequest("ACCOUNT_INVALID", $"Choose the {what} account.");

        public async Task<FixedAssetDto> CreateAsync(Guid clientId, CreateAssetRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await _books.EnsureChartAsync(clientId, ct);
            var name = (req.Name ?? "").Trim();
            if (name.Length is < 2 or > 120) throw GatewayException.BadRequest("NAME_INVALID", "Give the asset a name (2-120 characters).");
            if (req.PurchaseDate.Year < 2000 || req.PurchaseDate > DateTime.UtcNow.AddDays(31)) throw GatewayException.BadRequest("DATE_INVALID", "That purchase date is not valid.");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (req.Cost <= 0 || req.Cost != Math.Round(req.Cost, 2) || req.Cost > 1_000_000_000m) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter the cost (above zero, at most two decimals).");
            if (req.ResidualValue < 0 || req.ResidualValue >= req.Cost || req.ResidualValue != Math.Round(req.ResidualValue, 2)) throw GatewayException.BadRequest("RESIDUAL_INVALID", "The value left at the end of its life must be below the cost.");
            var method = req.Method ?? "";
            if (method is not ("straight_line" or "reducing")) throw GatewayException.BadRequest("METHOD_INVALID", "Choose straight line or reducing balance.");
            if (req.UsefulLifeMonths is < 1 or > 600) throw GatewayException.BadRequest("LIFE_INVALID", "The useful life is between 1 and 600 months.");
            if (method == "reducing" && (req.RatePercent <= 0 || req.RatePercent > 100)) throw GatewayException.BadRequest("RATE_INVALID", "A reducing-balance asset needs a yearly rate between 1 and 100 percent.");
            var asset = await AccountAsync(clientId, req.AssetAccountId, "asset", ct); var acc = await AccountAsync(clientId, req.AccumulatedAccountId, "accumulated depreciation", ct); var exp = await AccountAsync(clientId, req.ExpenseAccountId, "depreciation expense", ct);
            if (asset.Type != "Asset" || acc.Type != "Asset" || exp.Type != "Expense") throw GatewayException.BadRequest("ACCOUNT_INVALID", "The asset and its accumulated depreciation are asset accounts; depreciation is an expense account.");
            if (asset.Id == acc.Id) throw GatewayException.BadRequest("ACCOUNT_INVALID", "Accumulated depreciation needs its own account.");
            var code = string.IsNullOrWhiteSpace(req.Code) ? null : req.Code.Trim();
            if (code is { Length: > 30 } || (req.Notes ?? "").Length > 300) throw GatewayException.BadRequest("TOO_LONG", "The code is at most 30 characters and the notes 300.");
            if (code != null && await _db.FixedAssets.AnyAsync(a => a.ClientId == clientId && a.Code == code, ct)) throw GatewayException.Conflict("CODE_EXISTS", "Another asset already uses that code.");

            var fa = new FixedAsset { ClientId = clientId, Name = name, Code = code, PurchaseDate = req.PurchaseDate.Date, Currency = cur, Cost = req.Cost, ResidualValue = req.ResidualValue, UsefulLifeMonths = req.UsefulLifeMonths, Method = method,
                RatePercent = method == "reducing" ? req.RatePercent : 0, AssetAccountId = asset.Id, AccumulatedAccountId = acc.Id, ExpenseAccountId = exp.Id, Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim() };
            if (req.PaidFromAccountId is { } from)
            {
                var src = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == from && a.ClientId == clientId && a.IsActive, ct) ?? throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose the account it was paid from.");
                if (src.Type is not ("Asset" or "Liability")) throw GatewayException.BadRequest("ACCOUNT_INVALID", "Pay from a cash, bank or payable account.");
                await _books.PostEntryAsync(clientId, fa.PurchaseDate, cur, "Purchase of " + name, code, "asset", null, new List<(Guid, decimal, decimal)> { (asset.Id, req.Cost, 0m), (src.Id, 0m, req.Cost) }, ct);
            }
            _db.FixedAssets.Add(fa);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "asset.create", Detail = $"{name} {req.Cost:0.00} {cur}" });
            await _db.SaveChangesAsync(ct);
            return (await RegisterAsync(clientId, DateTime.UtcNow.AddHours(2).Date.AddYears(50), ct)).Assets.First(a => a.Id == fa.Id);
        }

        // ---- depreciation -----------------------------------------------------------------------------------------------------

        public Task<DepreciationResultDto> DepreciateAsync(Guid clientId, DateTime upTo, CancellationToken ct) => RunAsync(clientId, upTo, null, ct);

        private async Task<DepreciationResultDto> RunAsync(Guid clientId, DateTime upTo, Guid? onlyAsset, CancellationToken ct)
        {
            RequireWholeCompany();
            await _books.EnsureChartAsync(clientId, ct);
            var through = upTo.Date;
            if (through > DateTime.UtcNow.AddHours(2).Date.AddDays(31)) throw GatewayException.BadRequest("DATE_INVALID", "Depreciate up to today or an earlier month end.");
            var assets = await _db.FixedAssets.Where(a => a.ClientId == clientId && a.Status == "Active" && (onlyAsset == null || a.Id == onlyAsset)).ToListAsync(ct);
            var existing = (await _db.DepreciationCharges.AsNoTracking().Where(c => c.ClientId == clientId).ToListAsync(ct)).GroupBy(c => c.AssetId).ToDictionary(g => g.Key, g => g.ToList());

            var due = new List<(FixedAsset A, DateTime Month, decimal Amount)>();
            foreach (var a in assets)
            {
                var done = existing.GetValueOrDefault(a.Id) ?? new();
                var charged = done.Sum(c => c.Amount); var have = done.Select(c => c.Period).ToHashSet();
                var lastMonth = a.Method == "straight_line" ? MonthStart(a.PurchaseDate).AddMonths(a.UsefulLifeMonths - 1) : DateTime.MaxValue;
                for (var m = MonthStart(a.PurchaseDate); m <= lastMonth && MonthEnd(m) <= through; m = m.AddMonths(1))
                {
                    if (have.Contains(m)) continue;
                    decimal amount;
                    var depreciable = a.Cost - a.ResidualValue;
                    if (a.Method == "straight_line")
                    {
                        var index = (m.Year - a.PurchaseDate.Year) * 12 + m.Month - a.PurchaseDate.Month;   // 0 = the month of purchase
                        amount = index == a.UsefulLifeMonths - 1 ? depreciable - charged : Math.Min(Math.Round(depreciable / a.UsefulLifeMonths, 2), depreciable - charged);
                    }
                    else amount = Math.Min(Math.Round((a.Cost - charged) * a.RatePercent / 100m / 12m, 2), a.Cost - charged - a.ResidualValue);
                    if (amount <= 0) { if (a.Method == "reducing") break; continue; }
                    due.Add((a, m, amount)); charged += amount;
                }
            }
            if (due.Count == 0) return new DepreciationResultDto(0, 0, Array.Empty<string>());

            var locked = await _books.LockedUntilAsync(clientId, ct);
            if (locked != null && due.Any(d => MonthEnd(d.Month) <= locked.Value.Date))
                throw GatewayException.Conflict("PERIOD_LOCKED", $"Some months to depreciate fall in the closed period (up to {locked.Value:d MMM yyyy}). Reopen the period, or ask for those months to be depreciated by a manual journal.");

            var months = new List<string>();
            foreach (var group in due.GroupBy(d => (d.Month, d.A.Currency)).OrderBy(g => g.Key.Month))
            {
                var lines = new List<(Guid, decimal, decimal)>();
                foreach (var e in group.GroupBy(x => x.A.ExpenseAccountId)) lines.Add((e.Key, e.Sum(x => x.Amount), 0m));
                foreach (var c in group.GroupBy(x => x.A.AccumulatedAccountId)) lines.Add((c.Key, 0m, c.Sum(x => x.Amount)));
                var entry = await _books.PostEntryAsync(clientId, MonthEnd(group.Key.Month), group.Key.Currency, "Depreciation " + group.Key.Month.ToString("yyyy-MM"), null, "depn", null, lines, ct);
                foreach (var x in group) _db.DepreciationCharges.Add(new DepreciationCharge { ClientId = clientId, AssetId = x.A.Id, Period = x.Month, Amount = x.Amount, JournalEntryId = entry.Id });
                await _db.SaveChangesAsync(ct);
                var label = group.Key.Month.ToString("yyyy-MM"); if (!months.Contains(label)) months.Add(label);
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "asset.depreciate", Detail = $"{due.Count} charges up to {through:yyyy-MM-dd}" });
            await _db.SaveChangesAsync(ct);
            return new DepreciationResultDto(due.Count, due.Sum(d => d.Amount), months);
        }

        // ---- disposal ---------------------------------------------------------------------------------------------------------

        public async Task<FixedAssetDto> DisposeAsync(Guid clientId, Guid id, DisposeAssetRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var a = await _db.FixedAssets.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Asset");
            if (a.Status == "Disposed") throw GatewayException.Conflict("ALREADY_DISPOSED", "That asset has already been disposed of.");
            var date = req.Date.Date;
            if (date < a.PurchaseDate.Date || date > DateTime.UtcNow.AddHours(2).Date) throw GatewayException.BadRequest("DATE_INVALID", "The disposal date must be after the purchase and not in the future.");
            if (req.Proceeds < 0 || req.Proceeds != Math.Round(req.Proceeds, 2)) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter what it was sold for (zero if scrapped).");
            LedgerAccount? into = null;
            if (req.Proceeds > 0)
            {
                into = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.ReceivedIntoAccountId && x.ClientId == clientId && x.IsActive && x.Type == "Asset", ct) ?? throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose the cash or bank account the money went into.");
            }
            // depreciate up to the end of the month of disposal first, so the book value is right
            await RunAsync(clientId, MonthEnd(date), a.Id, ct);
            var accumulated = await _db.DepreciationCharges.Where(c => c.AssetId == a.Id).SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
            var nbv = a.Cost - accumulated; var gain = req.Proceeds - nbv;
            var lines = new List<(Guid, decimal, decimal)>();
            if (into != null) lines.Add((into.Id, req.Proceeds, 0m));
            if (accumulated > 0) lines.Add((a.AccumulatedAccountId, accumulated, 0m));
            lines.Add((a.AssetAccountId, 0m, a.Cost));
            if (gain > 0) lines.Add(((await ChartCodes.ByRoleAsync(_db, clientId, "other_income", "4090", ct))?.Id ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The other income account is missing."), 0m, gain));
            if (gain < 0) lines.Add(((await ChartCodes.ByRoleAsync(_db, clientId, "other_expense", "5990", ct))?.Id ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The other expenses account is missing."), -gain, 0m));
            await _books.PostEntryAsync(clientId, date, a.Currency, "Disposal of " + a.Name, a.Code, "asset", null, lines, ct);
            a.Status = "Disposed"; a.DisposedOn = date; a.DisposalProceeds = req.Proceeds;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "asset.dispose", Detail = $"{a.Name} for {req.Proceeds:0.00} {a.Currency}" });
            await _db.SaveChangesAsync(ct);
            return (await RegisterAsync(clientId, DateTime.UtcNow.AddHours(2).Date.AddYears(50), ct)).Assets.First(x => x.Id == a.Id);
        }
    }
}
