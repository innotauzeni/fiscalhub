using System.Text.Json;
using ClosedXML.Excel;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Import;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Import;
using Fdms.Infrastructure.Services;

namespace Fdms.Tests
{
    public class WireFormatTests
    {
        [Fact]
        public void Submit_payload_matches_the_shape_that_works_in_dara()
        {
            var receipt = new ReceiptPayload("FiscalInvoice", "USD", 1, 7, "INV-1", null, null, "2025-01-31T10:00:00",
                new CreditDebitNoteDto(99, null, 5, 2, "2025-01-30T09:00:00"), true,
                new List<ReceiptLineDto> { new("Sale", 1, "85456852", "Shoes", 25m, 1m, 25m, null, 15m, 3), new("Sale", 2, "19059010", "Bread", 1m, 1m, 1m, null, null, 1) },
                new List<ReceiptTaxDto> { new(null, 15m, 3, 3.26m, 25m), new(null, null, 1, 0m, 1m) },
                new List<ReceiptPaymentDto> { new("Cash", 26m) }, 26m, "Receipt48", new SignatureDto("h", "s"), null, null);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { receipt }, FdmsJson.Options));
            var root = doc.RootElement;
            Assert.Equal(new[] { "receipt" }, root.EnumerateObject().Select(p => p.Name).ToArray());   // no deviceID in the envelope
            var r = root.GetProperty("receipt");
            Assert.Equal("FiscalInvoice", r.GetProperty("receiptType").GetString());
            Assert.Equal("2025-01-31T10:00:00", r.GetProperty("receiptDate").GetString());
            Assert.Equal(99, r.GetProperty("creditDebitNote").GetProperty("receiptID").GetInt64());
            Assert.Equal(5, r.GetProperty("creditDebitNote").GetProperty("receiptGlobalNo").GetInt64());
            Assert.Equal(2, r.GetProperty("creditDebitNote").GetProperty("fiscalDayNo").GetInt32());

            var l1 = r.GetProperty("receiptLines")[0];
            Assert.Equal("85456852", l1.GetProperty("receiptLineHSCode").GetString());
            Assert.Equal(3, l1.GetProperty("taxID").GetInt32());
            Assert.Equal(15m, l1.GetProperty("taxPercent").GetDecimal());
            var l2 = r.GetProperty("receiptLines")[1];
            Assert.False(l2.TryGetProperty("taxPercent", out _));       // exempt: omitted entirely
            Assert.False(l2.TryGetProperty("taxCode", out _));          // never sent unless configured
            Assert.False(r.GetProperty("receiptTaxes")[1].TryGetProperty("taxPercent", out _));
            Assert.Equal("Cash", r.GetProperty("receiptPayments")[0].GetProperty("moneyTypeCode").GetString());
            Assert.False(r.TryGetProperty("buyerData", out _));
        }

        [Fact]
        public void Close_day_payload_has_no_device_id_and_uses_string_enums()
        {
            var req = new CloseDayRequest(3, new List<FiscalCounterDto>
            {
                new("SaleByTax", "USD", 15m, 3, null, 100m),
                new("BalanceByMoneyType", "USD", null, null, "Cash", 100m),
            }, new SignatureDto("h", "s"), 4);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(req, FdmsJson.Options));
            var r = doc.RootElement;
            Assert.False(r.TryGetProperty("deviceID", out _));
            Assert.Equal(3, r.GetProperty("fiscalDayNo").GetInt32());
            Assert.Equal(4, r.GetProperty("receiptCounter").GetInt32());
            Assert.Equal(3, r.GetProperty("fiscalDayCounters")[0].GetProperty("fiscalCounterTaxID").GetInt32());
            Assert.Equal("Cash", r.GetProperty("fiscalDayCounters")[1].GetProperty("fiscalCounterMoneyType").GetString());
            Assert.False(r.GetProperty("fiscalDayCounters")[1].TryGetProperty("fiscalCounterTaxID", out _));
        }
    }

    public class ImportParsingTests
    {
        [Theory]
        [InlineData("1,234.50", 1234.50)]
        [InlineData("US$ 12.00", 12.00)]
        [InlineData("15%", 15)]
        [InlineData("(5.00)", 5.00)]
        public void Numbers_are_parsed_leniently(string raw, double expected) =>
            Assert.True(ImportParsing.TryParseDecimal(raw, out var v) && v == (decimal)expected);

        [Theory]
        [InlineData("Exempt", null, true, false)]
        [InlineData("15", 15.0, false, false)]
        [InlineData("15.5%", 15.5, false, false)]
        [InlineData("zero rated", 0.0, false, false)]
        [InlineData("abc", null, false, true)]
        public void Tax_cells_are_understood(string raw, double? pct, bool exempt, bool invalid)
        {
            var t = ImportParsing.ParseTax(raw);
            Assert.Equal(pct.HasValue ? (decimal)pct.Value : null, t.Percent);
            Assert.Equal(exempt, t.Exempt);
            Assert.Equal(invalid, t.Invalid);
        }

        [Theory]
        [InlineData("EcoCash", MoneyType.MobileWallet)]
        [InlineData("Visa card", MoneyType.Card)]
        [InlineData("RTGS", MoneyType.BankTransfer)]
        [InlineData("", MoneyType.Cash)]
        [InlineData("Barter", MoneyType.Other)]
        public void Payment_methods_map_to_money_types(string raw, MoneyType expected) => Assert.Equal(expected, ImportParsing.ParseMoneyType(raw));

        [Fact]
        public void Pdf_text_is_read_into_a_draft_with_warnings()
        {
            const string text = @"ACME Pvt Ltd
INVOICE
Invoice No: INV-2025-001
Date: 31/01/2025
Bill To: Zeta Traders
Buyer TIN: 2000123456
Description Qty Unit Price Total
1. Men's shoes 2 25.00 50.00
2. Leather belt 1 12.50 12.50
Subtotal 62.50
Total Due: 62.50
Paid by EcoCash";
            var d = InvoiceTextParser.Parse(text);
            Assert.Equal("INV-2025-001", d.InvoiceNo);
            Assert.Equal(new DateTime(2025, 1, 31), d.InvoiceDate);
            Assert.Equal("Zeta Traders", d.Buyer?.RegisterName);
            Assert.Equal("2000123456", d.Buyer?.Tin);
            Assert.Equal(2, d.Lines.Count);
            Assert.Equal(("Men's shoes", 2m, 25m), (d.Lines[0].Name, d.Lines[0].Quantity, d.Lines[0].UnitPrice));
            Assert.Equal(("Leather belt", 1m, 12.5m), (d.Lines[1].Name, d.Lines[1].Quantity, d.Lines[1].UnitPrice));
            Assert.Equal(MoneyType.MobileWallet, d.Payments.Single().MoneyType);
            Assert.Equal(62.5m, d.Payments.Single().Amount);
            Assert.Contains(d.Warnings, w => w.Contains("Tax rates were not read"));
        }

        [Fact]
        public void Totals_and_headings_are_not_mistaken_for_items()
        {
            var d = InvoiceTextParser.Parse("Invoice No: A1\nDescription Qty Price\nSubtotal 100.00\nTotal 115.00\nVAT 15% 15.00\n");
            Assert.Empty(d.Lines);
            Assert.Contains(d.Warnings, w => w.Contains("No line items"));
        }

        [Fact]
        public void Empty_text_explains_scanned_pdfs()
        {
            var d = InvoiceTextParser.Parse("   ");
            Assert.Contains(d.Warnings, w => w.Contains("scanned"));
        }
    }

    public class ExcelParserTests
    {
        private static MemoryStream Workbook(Action<IXLWorksheet> fill)
        {
            var wb = new XLWorkbook();
            fill(wb.AddWorksheet("Invoices"));
            var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0; return ms;
        }

        private static void Header(IXLWorksheet ws, params string[] h) { for (var i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i]; }
        private static void Row(IXLWorksheet ws, int r, params object[] v) { for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = v[i]?.ToString() ?? ""; }

        [Fact]
        public void Rows_are_grouped_into_invoices_and_blank_invoice_no_continues_the_invoice_above()
        {
            using var f = Workbook(ws =>
            {
                Header(ws, "Invoice No", "Currency", "Receipt Type", "Item Description", "Quantity", "Unit Price", "Tax Percent", "Payment Method", "Payment Amount");
                Row(ws, 2, "A1", "usd", "", "Shoes", 2, 25, 15, "Cash", 57.5);
                Row(ws, 3, "", "", "", "Bread", 1, 1, "Exempt", "Cash", 57.5);   // same payment repeated -> kept once
                Row(ws, 4, "A2", "ZWG", "CreditNote", "Pen", 3, 2, "", "", "");
            });
            var (drafts, errors) = ExcelInvoiceParser.Parse(f);
            Assert.Empty(errors);
            Assert.Equal(2, drafts.Count);
            Assert.Equal(2, drafts[0].Lines.Count);
            Assert.True(drafts[0].Lines[1].IsExempt);
            Assert.Single(drafts[0].Payments);
            Assert.Equal("USD", drafts[0].Currency);
            Assert.Equal(ReceiptType.CreditNote, drafts[1].ReceiptType);
            Assert.Null(drafts[1].Lines[0].TaxPercent);
        }

        [Fact]
        public void Bad_rows_are_reported_with_their_row_number()
        {
            using var f = Workbook(ws =>
            {
                Header(ws, "Invoice No", "Item Description", "Quantity", "Unit Price", "Tax Percent");
                Row(ws, 2, "B1", "Good", 1, 10, 15);
                Row(ws, 3, "B1", "Bad price", 1, "abc", 15);
                Row(ws, 4, "B1", "Bad tax", 1, 10, "banana");
                Row(ws, 5, "B1", "Neg qty", -2, 10, 15);
            });
            var (drafts, errors) = ExcelInvoiceParser.Parse(f);
            Assert.Single(drafts[0].Lines);
            Assert.Contains(errors, e => e.StartsWith("Row 3"));
            Assert.Contains(errors, e => e.StartsWith("Row 4"));
            Assert.Contains(errors, e => e.StartsWith("Row 5"));
        }

        [Fact]
        public void Missing_required_columns_are_reported()
        {
            using var f = Workbook(ws => { Header(ws, "Invoice No", "Quantity"); Row(ws, 2, "X", 1); });
            var (_, errors) = ExcelInvoiceParser.Parse(f);
            Assert.Equal(2, errors.Count);
        }

        [Fact]
        public void The_template_is_itself_importable()
        {
            using var f = new MemoryStream(ExcelInvoiceParser.BuildTemplate());
            var (drafts, errors) = ExcelInvoiceParser.Parse(f);
            Assert.Empty(errors);
            Assert.Equal(new[] { "INV-1001", "CN-1001" }, drafts.Select(d => d.InvoiceNo).ToArray());
            Assert.Equal("INV-1001", drafts[1].OriginalInvoiceNo);
            Assert.Equal(2, drafts[0].Lines.Count);
        }
    }

    public class WebhookUrlTests
    {
        [Theory]
        [InlineData("http://example.com/hook")]
        [InlineData("https://localhost/hook")]
        [InlineData("https://127.0.0.1/hook")]
        [InlineData("https://10.0.0.5/hook")]
        [InlineData("https://192.168.1.1/hook")]
        [InlineData("https://169.254.169.254/latest/meta-data")]
        [InlineData("https://user:pw@example.com/hook")]
        [InlineData("not a url")]
        public void Unsafe_targets_are_refused(string url) =>
            Assert.Throws<GatewayException>(() => WebhookService.ValidateUrl(url, allowInsecure: false));

        [Fact]
        public void A_public_https_url_is_accepted() =>
            Assert.Equal("example.com", WebhookService.ValidateUrl("https://example.com/hook", false).Host);
    }
}
