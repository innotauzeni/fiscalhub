using Fdms.Domain.Enums;

namespace Fdms.Application.Fiscal
{
    /// <summary>Fiscal day counter arithmetic (spec section 6). Credit notes arrive with negative amounts already.</summary>
    public static class FiscalCounterCalculator
    {
        /// <summary>Counter deltas produced by one receipt.</summary>
        public static IReadOnlyList<FiscalCounter> Deltas(CalculatedReceipt r)
        {
            var (byTax, taxByTax) = r.Type switch
            {
                ReceiptType.FiscalInvoice => (FiscalCounterType.SaleByTax, FiscalCounterType.SaleTaxByTax),
                ReceiptType.CreditNote => (FiscalCounterType.CreditNoteByTax, FiscalCounterType.CreditNoteTaxByTax),
                _ => (FiscalCounterType.DebitNoteByTax, FiscalCounterType.DebitNoteTaxByTax)
            };

            var list = new List<FiscalCounter>();
            foreach (var t in r.Taxes)
            {
                list.Add(new FiscalCounter(byTax, r.Currency, t.TaxId, t.TaxPercent, null, t.SalesAmountWithTax));
                list.Add(new FiscalCounter(taxByTax, r.Currency, t.TaxId, t.TaxPercent, null, t.TaxAmount));
            }
            foreach (var p in r.Payments)
                list.Add(new FiscalCounter(FiscalCounterType.BalanceByMoneyType, r.Currency, null, null, p.MoneyType, p.Amount));
            return list;
        }

        /// <summary>Merges deltas into running totals; zero results are dropped (zero counters must not be submitted).</summary>
        public static IReadOnlyList<FiscalCounter> Merge(IEnumerable<FiscalCounter> current, IEnumerable<FiscalCounter> deltas)
        {
            var map = new Dictionary<(FiscalCounterType, string, int?, decimal?, MoneyType?), FiscalCounter>();
            foreach (var c in current.Concat(deltas))
            {
                var key = (c.Type, c.Currency.ToUpperInvariant(), c.TaxId, c.TaxPercent, c.MoneyType);
                map[key] = map.TryGetValue(key, out var existing) ? existing with { Value = existing.Value + c.Value } : c;
            }
            return map.Values.Where(c => c.Value != 0m).ToList();
        }
    }
}
