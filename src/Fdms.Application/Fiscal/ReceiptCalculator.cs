using Fdms.Domain.Enums;

namespace Fdms.Application.Fiscal
{
    public sealed class ReceiptCalculationException : Exception
    {
        public string Code { get; }
        public ReceiptCalculationException(string code, string message) : base(message) => Code = code;
    }

    /// <summary>
    /// Turns caller lines into FDMS-valid receipt lines, tax lines and total (rules RCPT016-RCPT040).
    /// Credit notes are negated here so callers always send positive quantities/prices.
    /// </summary>
    public static class ReceiptCalculator
    {
        private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

        public static CalculatedReceipt Calculate(
            ReceiptType type, string currency, bool linesTaxInclusive,
            IReadOnlyList<LineInput> lines, IReadOnlyList<PaymentInput>? payments,
            IReadOnlyDictionary<int, TaxDefinition> taxes)
        {
            if (lines.Count == 0) throw new ReceiptCalculationException("RCPT016", "At least one receipt line is required.");

            var sign = type == ReceiptType.CreditNote ? -1m : 1m;
            var outLines = new List<ReceiptLine>();
            var no = 1;
            foreach (var l in lines)
            {
                if (l.Quantity <= 0) throw new ReceiptCalculationException("RCPT023", $"Line {no}: quantity must be greater than 0.");
                if (!taxes.TryGetValue(l.TaxId, out var tax))
                    throw new ReceiptCalculationException("RCPT025", $"Line {no}: tax id {l.TaxId} is not configured for this device.");

                // Sale lines are positive for invoices/debit notes and negative for credit notes; discounts are the opposite.
                var lineSign = l.LineType == ReceiptLineType.Discount ? -sign : sign;
                var signedPrice = Math.Abs(l.UnitPrice) * lineSign;
                outLines.Add(new ReceiptLine(no++, l.LineType, l.HsCode, l.Name, signedPrice, l.Quantity,
                    Round2(signedPrice * l.Quantity), tax.Code, tax.Percent, tax.TaxId));
            }

            var taxLines = new List<ReceiptTaxLine>();
            foreach (var g in outLines.GroupBy(x => (x.TaxId, x.TaxCode, x.TaxPercent)).OrderBy(g => g.Key.TaxId))
            {
                var sum = g.Sum(x => x.Total);
                var pct = g.Key.TaxPercent ?? 0m;
                decimal taxAmount, salesWithTax;
                if (linesTaxInclusive)
                {
                    taxAmount = Round2(sum * pct / (100m + pct));
                    salesWithTax = sum;
                }
                else
                {
                    taxAmount = Round2(sum * pct / 100m);
                    salesWithTax = sum + taxAmount;
                }
                taxLines.Add(new ReceiptTaxLine(g.Key.TaxId, g.Key.TaxCode, g.Key.TaxPercent, taxAmount, salesWithTax));
            }

            var total = taxLines.Sum(t => t.SalesAmountWithTax);

            if (type != ReceiptType.CreditNote && total < 0)
                throw new ReceiptCalculationException("RCPT040", "Receipt total must be >= 0 for invoices and debit notes.");
            if (type == ReceiptType.CreditNote && total > 0)
                throw new ReceiptCalculationException("RCPT040", "Receipt total must be <= 0 for credit notes.");

            // No payments supplied -> assume the full amount was paid in cash.
            var outPayments = payments is { Count: > 0 }
                ? payments.Select(p => new ReceiptPaymentLine(p.MoneyType, Round2(Math.Abs(p.Amount) * sign))).ToList()
                : new List<ReceiptPaymentLine> { new(MoneyType.Cash, total) };

            var paid = outPayments.Sum(p => p.Amount);
            if (paid != total)
                throw new ReceiptCalculationException("RCPT039", $"Payments ({paid}) must equal receipt total ({total}).");

            return new CalculatedReceipt(type, currency.ToUpperInvariant(), linesTaxInclusive, outLines, taxLines, outPayments, total);
        }
    }
}
