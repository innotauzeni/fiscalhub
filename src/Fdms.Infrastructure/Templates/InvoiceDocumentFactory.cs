using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Services;

namespace Fdms.Infrastructure.Templates
{
    /// <summary>
    /// Turns a fiscal receipt into <see cref="InvoiceDocumentDto"/>: every value the invoice can print, the lines, the taxes and the fiscal identifiers.
    /// The invoice builder's PDF, the Word export and the JSON export all draw from this one place, so they always agree.
    /// </summary>
    public static class InvoiceDocumentFactory
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Regex Placeholder = new(@"\{\s*([A-Za-z]+)\s*\}", RegexOptions.Compiled);

        public static string Money(decimal v) => v.ToString("N2", Inv);

        public static string Title(Receipt r, InvoiceTemplateSettings s) => r.Type switch
        {
            ReceiptType.CreditNote => FiscalTitles.CreditNote,
            ReceiptType.DebitNote => FiscalTitles.DebitNote,
            _ => FiscalTitles.ForInvoice(s.InvoiceTitle),
        };

        private static string PaymentName(string code) => code switch { "BankTransfer" => "Bank transfer", "MobileWallet" => "Mobile wallet (EcoCash)", "Cash" => "Cash", "Card" => "Card", "Coupon" => "Coupon", "Credit" => "Credit", _ => code };

        private static BankAccountInfo? PickBank(List<BankAccountInfo>? banks, string currency) =>
            banks == null || banks.Count == 0 ? null :
            banks.Where(b => string.Equals(b.Currency, currency, StringComparison.OrdinalIgnoreCase)).OrderByDescending(b => b.IsDefault).FirstOrDefault() ?? banks.FirstOrDefault(b => b.IsDefault) ?? banks[0];

        private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        public static InvoiceDocumentDto Build(Receipt r, Device d, InvoiceTemplateSettings s)
        {
            var p = JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options)!;
            var bank = PickBank(s.Banks, r.Currency);
            var buyer = p.BuyerData;
            var address = buyer?.BuyerAddress == null ? null : string.Join(", ", new[] { buyer.BuyerAddress.HouseNo, buyer.BuyerAddress.Street, buyer.BuyerAddress.City, buyer.BuyerAddress.Province }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var methods = p.ReceiptPayments.Select(x => PaymentName(x.MoneyTypeCode)).Distinct().ToList();
            var taxes = p.ReceiptTaxes.Select(t => new InvoiceDocumentTax(t.TaxPercent.HasValue ? $"VAT {t.TaxPercent:0.##}%" : "Exempt", t.TaxPercent, t.SalesAmountWithTax - t.TaxAmount, t.TaxAmount, t.SalesAmountWithTax)).ToList();
            var subtotal = taxes.Sum(t => t.Net);
            var taxTotal = taxes.Sum(t => t.Tax);
            var notices = string.IsNullOrWhiteSpace(r.ValidationErrorsJson) ? new List<string>() :
                (JsonSerializer.Deserialize<List<JsonElement>>(r.ValidationErrorsJson, FdmsJson.Options) ?? new())
                .Select(e => $"{e.GetProperty("code").GetString()} ({e.GetProperty("color").GetString()})").ToList();
            var bankDetails = Blank(ReceiptPdfRenderer.FillFor(s.BankDetails, s, d, r, p));

            var fields = new Dictionary<string, string?>
            {
                ["title"] = Title(r, s), ["invoiceNo"] = r.InvoiceNo, ["date"] = r.ReceiptDate.ToString("dd MMM yyyy", Inv), ["dateTime"] = r.ReceiptDate.ToString("dd MMM yyyy HH:mm", Inv),
                ["currency"] = r.Currency, ["notes"] = Blank(p.ReceiptNotes), ["paymentMethod"] = methods.Count == 0 ? null : string.Join(" / ", methods),
                ["originalInvoice"] = p.CreditDebitNote == null ? null : $"Receipt {p.CreditDebitNote.ReceiptGlobalNo} (ZIMRA ID {p.CreditDebitNote.ReceiptId}), fiscal day {p.CreditDebitNote.FiscalDayNo}",
                ["company"] = Blank(s.DisplayName) ?? d.TaxPayerName, ["tin"] = d.TaxPayerTin, ["vat"] = Blank(d.VatNumber),
                ["address"] = Blank(s.Address) ?? Blank(d.BranchAddress), ["phone"] = Blank(s.Phone) ?? Blank(d.BranchPhone), ["email"] = Blank(s.Email) ?? Blank(d.BranchEmail), ["website"] = Blank(s.Website),
                ["branch"] = Blank(d.BranchName),
                ["buyerName"] = Blank(buyer?.BuyerRegisterName), ["buyerTradeName"] = Blank(buyer?.BuyerTradeName), ["buyerTin"] = Blank(buyer?.BuyerTin), ["buyerVat"] = Blank(buyer?.VatNumber),
                ["buyerAddress"] = Blank(address), ["buyerPhone"] = Blank(buyer?.BuyerContacts?.PhoneNo), ["buyerEmail"] = Blank(buyer?.BuyerContacts?.Email),
                ["subtotal"] = Money(subtotal), ["taxTotal"] = Money(taxTotal), ["total"] = Money(r.Total),
                ["taxRates"] = string.Join(", ", p.ReceiptTaxes.Where(t => t.TaxPercent.HasValue).Select(t => $"{t.TaxPercent:0.##}%").Distinct()),
                ["bankName"] = bank?.Bank, ["accountName"] = bank?.AccountName, ["accountNumber"] = bank?.AccountNumber, ["bankBranch"] = bank?.BranchName,
                ["bankDetails"] = bankDetails, ["terms"] = Blank(ReceiptPdfRenderer.FillFor(s.Terms, s, d, r, p)), ["footer"] = Blank(ReceiptPdfRenderer.FillFor(s.FooterText, s, d, r, p)),
                ["deviceId"] = d.ZimraDeviceId.ToString(Inv), ["fiscalDay"] = r.FiscalDayNo.ToString(Inv), ["receiptCounter"] = r.ReceiptCounter.ToString(Inv), ["globalNo"] = r.ReceiptGlobalNo.ToString(Inv),
                ["zimraReceiptId"] = r.FdmsReceiptId?.ToString(Inv), ["verificationUrl"] = Blank(r.QrUrl), ["signature"] = Blank(r.Hash),
            };

            var lines = p.ReceiptLines.Select((l, i) => new InvoiceDocumentLine(i + 1, l.ReceiptLineName, l.ReceiptLineHSCode, l.ReceiptLineQuantity, l.ReceiptLinePrice, l.TaxPercent, l.ReceiptLineTotal, l.ReceiptLineType == "Discount")).ToList();
            var status = r.Status == ReceiptStatus.Submitted ? "Submitted" : r.Status == ReceiptStatus.Pending ? "NotYetConfirmed" : r.Status.ToString();
            return new InvoiceDocumentDto("fiscalhub-invoice", 1, r.Type.ToString(), r.Currency, fields, lines, taxes,
                p.ReceiptPayments.Select(x => new InvoiceDocumentPayment(PaymentName(x.MoneyTypeCode), x.PaymentAmount)).ToList(), subtotal, taxTotal, r.Total,
                new InvoiceDocumentFiscal(d.ZimraDeviceId, r.FiscalDayNo, r.ReceiptCounter, r.ReceiptGlobalNo, r.FdmsReceiptId, Blank(r.QrUrl), Blank(r.Hash), status, notices),
                s.Canvas is { Elements.Count: > 0 } ? s.Canvas : null);
        }

        /// <summary>Fills {placeholders} with the invoice's values. An unknown placeholder is left as typed; a known one with no value becomes empty.</summary>
        public static string Fill(string? text, IReadOnlyDictionary<string, string?> fields) =>
            string.IsNullOrEmpty(text) ? "" : Placeholder.Replace(text, m => InvoiceFields.Keys.Contains(m.Groups[1].Value) ? fields.GetValueOrDefault(m.Groups[1].Value) ?? "" : m.Value);
    }
}
