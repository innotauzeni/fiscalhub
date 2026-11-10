using Fdms.Application.Fiscal;
using Fdms.Domain.Enums;

namespace Fdms.Tests
{
    /// <summary>
    /// Strings are compared with the "Result used for hash generation" rows of spec section 13.
    /// (The spec's printed base64 hashes cannot be reproduced from its own printed strings, so the strings are the contract.)
    /// </summary>
    public class SignatureStringTests
    {
        private const string PrevHash = "hNVJXP/ACOiE8McD3pKsDlqBXpuaUqQOfPnMyfZWI9k=";

        private static ReceiptTaxLine[] CodedTaxes(int sign) => new[]
        {
            new ReceiptTaxLine(1, "A", null, 0m, 2500m * sign),
            new ReceiptTaxLine(2, "B", 0m, 0m, 3500m * sign),
            new ReceiptTaxLine(3, "C", 15m, 150m * sign, 1150m * sign),
            new ReceiptTaxLine(3, "D", 15m, 300m * sign, 2300m * sign),
        };

        [Fact]
        public void FiscalInvoice_example1()
        {
            var s = SignatureStringBuilder.BuildReceiptString(new ReceiptSigningInput(
                321, ReceiptType.FiscalInvoice, "ZWL", 432, new DateTime(2019, 9, 19, 15, 43, 12), 9450m, CodedTaxes(1), PrevHash));
            Assert.Equal("321FISCALINVOICEZWL4322019-09-19T15:43:12945000A0250000B0.000350000C15.0015000115000D15.0030000230000" + PrevHash, s);
        }

        [Fact]
        public void FiscalInvoice_example2_no_tax_codes_fractional_percent()
        {
            var taxes = new[]
            {
                new ReceiptTaxLine(1, null, null, 0m, 7m),
                new ReceiptTaxLine(2, null, 0m, 0m, 10m),
                new ReceiptTaxLine(3, null, 14.5m, 0.05m, 0.35m),
            };
            var s = SignatureStringBuilder.BuildReceiptString(new ReceiptSigningInput(
                322, ReceiptType.FiscalInvoice, "USD", 85, new DateTime(2019, 9, 19, 9, 23, 7), 40.35m, taxes, PrevHash));
            Assert.Equal("322FISCALINVOICEUSD852019-09-19T09:23:074035" + "07000.000100014.50535" + PrevHash, s);
        }

        [Fact]
        public void CreditNote_amounts_are_negative_cents()
        {
            var s = SignatureStringBuilder.BuildReceiptString(new ReceiptSigningInput(
                321, ReceiptType.CreditNote, "ZWL", 432, new DateTime(2020, 9, 19, 15, 43, 12), -9450m, CodedTaxes(-1), PrevHash));
            Assert.Equal("321CREDITNOTEZWL4322020-09-19T15:43:12-945000A0-250000B0.000-350000C15.00-15000-115000D15.00-30000-230000" + PrevHash, s);
        }

        [Fact]
        public void DebitNote_example1()
        {
            var s = SignatureStringBuilder.BuildReceiptString(new ReceiptSigningInput(
                321, ReceiptType.DebitNote, "ZWL", 432, new DateTime(2020, 9, 19, 15, 43, 12), 9450m, CodedTaxes(1), PrevHash));
            Assert.Equal("321DEBITNOTEZWL4322020-09-19T15:43:12945000A0250000B0.000350000C15.0015000115000D15.0030000230000" + PrevHash, s);
        }

        [Fact]
        public void First_receipt_of_day_has_no_previous_hash()
        {
            var s = SignatureStringBuilder.BuildReceiptString(new ReceiptSigningInput(
                321, ReceiptType.FiscalInvoice, "ZWL", 1, new DateTime(2019, 9, 19, 15, 43, 12), 9450m, CodedTaxes(1), null));
            Assert.EndsWith("D15.0030000230000", s);
        }

        [Fact]
        public void FiscalDay_string_single_currency_orders_by_type_then_tax_then_money_type_and_skips_zero()
        {
            var counters = new[]
            {
                new FiscalCounter(FiscalCounterType.BalanceByMoneyType, "ZWL", null, null, MoneyType.Card, 15000m),
                new FiscalCounter(FiscalCounterType.BalanceByMoneyType, "ZWL", null, null, MoneyType.Cash, 20000m),
                new FiscalCounter(FiscalCounterType.SaleTaxByTax, "ZWL", 3, 15m, null, 2300m),
                new FiscalCounter(FiscalCounterType.SaleByTax, "ZWL", 2, 0m, null, 12000m),
                new FiscalCounter(FiscalCounterType.SaleByTax, "ZWL", 1, null, null, 23000m),
                new FiscalCounter(FiscalCounterType.SaleByTax, "ZWL", 3, 15m, null, 0m), // zero -> omitted
            };
            var s = SignatureStringBuilder.BuildFiscalDayString(321, 84, new DateTime(2019, 9, 23), counters);
            Assert.Equal(
                "321842019-09-23" +
                "SALEBYTAXZWL2300000" + "SALEBYTAXZWL0.001200000" + "SALETAXBYTAXZWL15.00230000" +
                "BALANCEBYMONEYTYPEZWLCASH2000000" + "BALANCEBYMONEYTYPEZWLCARD1500000", s);
        }
    }
}
