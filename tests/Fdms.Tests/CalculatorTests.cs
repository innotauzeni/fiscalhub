using Fdms.Application.Fiscal;
using Fdms.Domain.Enums;

namespace Fdms.Tests
{
    public class CalculatorTests
    {
        private static readonly Dictionary<int, TaxDefinition> Taxes = new()
        {
            [1] = new TaxDefinition(1, "A", null),
            [2] = new TaxDefinition(2, "B", 0m),
            [3] = new TaxDefinition(3, "C", 15m),
        };

        [Fact]
        public void Exclusive_lines_add_tax_on_top()
        {
            var r = ReceiptCalculator.Calculate(ReceiptType.FiscalInvoice, "usd", false,
                new[] { new LineInput("Shoes", "85456852", 1m, 25m, 3) }, null, Taxes);
            Assert.Equal("USD", r.Currency);
            Assert.Equal(3.75m, r.Taxes.Single().TaxAmount);
            Assert.Equal(28.75m, r.Taxes.Single().SalesAmountWithTax);
            Assert.Equal(28.75m, r.Total);
            Assert.Equal(28.75m, r.Payments.Single().Amount);
        }

        [Fact]
        public void Inclusive_lines_extract_tax()
        {
            var r = ReceiptCalculator.Calculate(ReceiptType.FiscalInvoice, "USD", true,
                new[] { new LineInput("Shoes", "85456852", 1m, 115m, 3) }, null, Taxes);
            Assert.Equal(15m, r.Taxes.Single().TaxAmount);
            Assert.Equal(115m, r.Total);
        }

        [Fact]
        public void Exempt_and_zero_rated_have_no_tax_amount()
        {
            var r = ReceiptCalculator.Calculate(ReceiptType.FiscalInvoice, "USD", true,
                new[] { new LineInput("Bread", null, 2m, 5m, 1), new LineInput("Milk", null, 1m, 3m, 2) }, null, Taxes);
            Assert.All(r.Taxes, t => Assert.Equal(0m, t.TaxAmount));
            Assert.Equal(13m, r.Total);
        }

        [Fact]
        public void Credit_note_is_negated()
        {
            var r = ReceiptCalculator.Calculate(ReceiptType.CreditNote, "USD", true,
                new[] { new LineInput("Shoes", null, 1m, 115m, 3) }, null, Taxes);
            Assert.Equal(-115m, r.Total);
            Assert.Equal(-15m, r.Taxes.Single().TaxAmount);
            Assert.Equal(-115m, r.Payments.Single().Amount);
        }

        [Fact]
        public void Discount_line_reduces_invoice_total()
        {
            var r = ReceiptCalculator.Calculate(ReceiptType.FiscalInvoice, "USD", true,
                new[] { new LineInput("Shoes", null, 1m, 100m, 3), new LineInput("Promo", null, 1m, 10m, 3, ReceiptLineType.Discount) }, null, Taxes);
            Assert.Equal(90m, r.Total);
        }

        [Fact]
        public void Unknown_tax_id_is_rejected_with_RCPT025()
        {
            var ex = Assert.Throws<ReceiptCalculationException>(() => ReceiptCalculator.Calculate(
                ReceiptType.FiscalInvoice, "USD", true, new[] { new LineInput("X", null, 1m, 1m, 99) }, null, Taxes));
            Assert.Equal("RCPT025", ex.Code);
        }

        [Fact]
        public void Mismatched_payments_are_rejected_with_RCPT039()
        {
            var ex = Assert.Throws<ReceiptCalculationException>(() => ReceiptCalculator.Calculate(
                ReceiptType.FiscalInvoice, "USD", true, new[] { new LineInput("X", null, 1m, 10m, 2) },
                new[] { new PaymentInput(MoneyType.Cash, 5m) }, Taxes));
            Assert.Equal("RCPT039", ex.Code);
        }

        [Fact]
        public void Counters_follow_receipt_type_and_merge_to_zero_after_credit()
        {
            var inv = ReceiptCalculator.Calculate(ReceiptType.FiscalInvoice, "USD", true,
                new[] { new LineInput("Shoes", null, 1m, 115m, 3) }, null, Taxes);
            var cn = ReceiptCalculator.Calculate(ReceiptType.CreditNote, "USD", true,
                new[] { new LineInput("Shoes", null, 1m, 115m, 3) }, null, Taxes);

            var afterInvoice = FiscalCounterCalculator.Merge(Array.Empty<FiscalCounter>(), FiscalCounterCalculator.Deltas(inv));
            Assert.Contains(afterInvoice, c => c.Type == FiscalCounterType.SaleByTax && c.Value == 115m);
            Assert.Contains(afterInvoice, c => c.Type == FiscalCounterType.SaleTaxByTax && c.Value == 15m);
            Assert.Contains(afterInvoice, c => c.Type == FiscalCounterType.BalanceByMoneyType && c.MoneyType == MoneyType.Cash && c.Value == 115m);

            var afterCredit = FiscalCounterCalculator.Merge(afterInvoice, FiscalCounterCalculator.Deltas(cn));
            // Sale counters stay (credit notes have their own counters); cash balance nets to zero and is dropped.
            Assert.DoesNotContain(afterCredit, c => c.Type == FiscalCounterType.BalanceByMoneyType);
            Assert.Contains(afterCredit, c => c.Type == FiscalCounterType.CreditNoteByTax && c.Value == -115m);
        }
    }
}
