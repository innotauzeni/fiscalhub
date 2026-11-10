using System.Globalization;
using System.Text;
using Fdms.Domain.Enums;

namespace Fdms.Application.Fiscal
{
    /// <summary>Builds the exact strings that are hashed/signed (spec section 13). Pure and culture-invariant.</summary>
    public static class SignatureStringBuilder
    {
        public static long ToCents(decimal value) =>
            (long)Math.Round(value * 100m, 0, MidpointRounding.AwayFromZero);

        /// <summary>15 -> "15.00", 14.5 -> "14.50", null (exempt) -> "".</summary>
        public static string FormatPercent(decimal? percent) =>
            percent.HasValue ? percent.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";

        public static string BuildReceiptTaxes(IEnumerable<ReceiptTaxLine> taxes)
        {
            var sb = new StringBuilder();
            // taxID ascending, then taxCode alphabetical (empty code sorts before "A").
            foreach (var t in taxes.OrderBy(t => t.TaxId).ThenBy(t => t.TaxCode ?? "", StringComparer.Ordinal))
            {
                sb.Append(t.TaxCode ?? "")
                  .Append(FormatPercent(t.TaxPercent))
                  .Append(ToCents(t.TaxAmount).ToString(CultureInfo.InvariantCulture))
                  .Append(ToCents(t.SalesAmountWithTax).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>deviceID || RECEIPTTYPE || CURRENCY || globalNo || date || totalCents || taxes || previousHash (if not first of day)</summary>
        public static string BuildReceiptString(ReceiptSigningInput r)
        {
            var sb = new StringBuilder();
            sb.Append(r.DeviceId.ToString(CultureInfo.InvariantCulture))
              .Append(r.Type.ToString().ToUpperInvariant())
              .Append(r.Currency.ToUpperInvariant())
              .Append(r.GlobalNo.ToString(CultureInfo.InvariantCulture))
              .Append(r.Date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))
              .Append(ToCents(r.Total).ToString(CultureInfo.InvariantCulture))
              .Append(BuildReceiptTaxes(r.Taxes));
            if (!string.IsNullOrEmpty(r.PreviousReceiptHash))
                sb.Append(r.PreviousReceiptHash);
            return sb.ToString();
        }

        /// <summary>Concatenated counters: only non-zero, ordered by type, currency, then taxID / money type.</summary>
        public static string BuildFiscalCounters(IEnumerable<FiscalCounter> counters)
        {
            var sb = new StringBuilder();
            var ordered = counters
                .Where(c => c.Value != 0m)
                .OrderBy(c => (int)c.Type)
                .ThenBy(c => c.Currency.ToUpperInvariant(), StringComparer.Ordinal)
                .ThenBy(c => c.TaxId ?? 0)
                .ThenBy(c => c.MoneyType.HasValue ? (int)c.MoneyType.Value : 0);

            foreach (var c in ordered)
            {
                sb.Append(c.Type.ToString().ToUpperInvariant()).Append(c.Currency.ToUpperInvariant());
                if (c.Type == FiscalCounterType.BalanceByMoneyType)
                    sb.Append((c.MoneyType ?? MoneyType.Other).ToString().ToUpperInvariant());
                else
                    sb.Append(FormatPercent(c.TaxPercent));
                sb.Append(ToCents(c.Value).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>deviceID || fiscalDayNo || fiscalDayDate (yyyy-MM-dd) || counters (spec 13.3.1).</summary>
        public static string BuildFiscalDayString(int deviceId, int fiscalDayNo, DateTime fiscalDayDate, IEnumerable<FiscalCounter> counters) =>
            string.Concat(
                deviceId.ToString(CultureInfo.InvariantCulture),
                fiscalDayNo.ToString(CultureInfo.InvariantCulture),
                fiscalDayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                BuildFiscalCounters(counters));
    }
}
