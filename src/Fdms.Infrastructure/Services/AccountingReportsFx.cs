using Fdms.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Every currency shown together in the base currency (IAS 21 translation): asset and liability balances at the rate on the report date, income, expenses and equity at the rate
    /// on each transaction's date. What does not add up after that is the foreign currency translation difference, shown in equity.
    /// </summary>
    public sealed partial class AccountingReports
    {
        private sealed class Fx
        {
            public string Base = "USD";
            public Dictionary<string, List<(DateTime Date, decimal Rate)>> Rates = new();
            public Dictionary<Guid, string> Types = new();

            public decimal Rate(string currency, DateTime on)
            {
                if (currency == Base) return 1;
                if (!Rates.TryGetValue(currency, out var list) || list.Count == 0)
                    throw GatewayException.BadRequest("RATE_MISSING", $"There is no exchange rate for {currency}. Add it under Accounting → Exchange rates.");
                // the rate in force on that day; before the first rate, the first one
                var best = list[0].Rate;
                foreach (var (d, r) in list) { if (d > on) break; best = r; }
                return best;
            }
        }

        private Fx? _fx;

        public async Task<string> UseBaseCurrencyAsync(Guid clientId, CancellationToken ct)
        {
            var b = await ExchangeRateService.BaseAsync(_db, clientId, ct);
            var rates = await _db.ExchangeRates.AsNoTracking().Where(r => r.ClientId == clientId).OrderBy(r => r.Date).ToListAsync(ct);
            _fx = new Fx
            {
                Base = b,
                Rates = rates.GroupBy(r => r.Currency).ToDictionary(g => g.Key, g => g.Select(r => (r.Date, r.Rate)).ToList()),
                Types = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).ToDictionaryAsync(a => a.Id, a => a.Type, ct),
            };
            return b;
        }

        /// <summary>The converted debits and credits per account, when the reports were asked to combine currencies (null otherwise).</summary>
        private async Task<Dictionary<Guid, (decimal Debit, decimal Credit)>?> FxSumsAsync(Guid clientId, string cur, DateTime? from, DateTime to, Guid? centre, CancellationToken ct)
        {
            if (_fx is not { } fx || cur != fx.Base) return null;
            var q = _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.Date <= to);
            if (from != null) q = q.Where(l => l.Date >= from);
            if (centre == Guid.Empty) q = q.Where(l => l.CostCentreId == null);
            else if (centre != null) q = q.Where(l => l.CostCentreId == centre);
            var groups = await q.GroupBy(l => new { l.AccountId, l.Currency, l.Date }).Select(g => new { g.Key.AccountId, g.Key.Currency, g.Key.Date, D = g.Sum(x => x.Debit), C = g.Sum(x => x.Credit) }).ToListAsync(ct);
            var result = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
            foreach (var g in groups)
            {
                var type = fx.Types.GetValueOrDefault(g.AccountId, "Asset");
                var rate = fx.Rate(g.Currency, type is "Asset" or "Liability" ? to : g.Date);
                var (d, c) = result.GetValueOrDefault(g.AccountId);
                result[g.AccountId] = (d + g.D * rate, c + g.C * rate);
            }
            return result.ToDictionary(kv => kv.Key, kv => (Math.Round(kv.Value.Debit, 2), Math.Round(kv.Value.Credit, 2)));
        }

        /// <summary>True while the reports combine currencies into this one.</summary>
        private bool Combining(string cur) => _fx != null && _fx.Base == cur;
    }
}
