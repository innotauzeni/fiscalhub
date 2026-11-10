using System.Globalization;
using System.Text.RegularExpressions;
using Fdms.Domain.Enums;

namespace Fdms.Application.Import
{
    /// <summary>Cell/value normalisation shared by the spreadsheet and PDF importers.</summary>
    public static class ImportParsing
    {
        /// <summary>"1,234.50", "US$ 12.00", "15%" -> number. Returns false when nothing numeric is found.</summary>
        public static bool TryParseDecimal(string? raw, out decimal value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var cleaned = Regex.Replace(raw.Trim(), @"[^\d\.\-]", "");
            return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        public static ReceiptType ParseReceiptType(string? raw)
        {
            var t = (raw ?? "").Trim().ToUpperInvariant().Replace(" ", "").Replace("_", "");
            return t switch
            {
                "CREDITNOTE" or "CN" or "CREDIT" => ReceiptType.CreditNote,
                "DEBITNOTE" or "DN" or "DEBIT" => ReceiptType.DebitNote,
                _ => ReceiptType.FiscalInvoice,
            };
        }

        public static MoneyType ParseMoneyType(string? raw)
        {
            var t = (raw ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (t.Length == 0) return MoneyType.Cash;
            if (t.Contains("card") || t is "visa" or "mastercard" or "swipe" or "pos") return MoneyType.Card;
            if (t.Contains("mobile") || t.Contains("ecocash") || t.Contains("onemoney") || t.Contains("innbucks") || t.Contains("telecash") || t.Contains("wallet")) return MoneyType.MobileWallet;
            if (t.Contains("bank") || t.Contains("transfer") || t.Contains("eft") || t.Contains("rtgs")) return MoneyType.BankTransfer;
            if (t.Contains("coupon") || t.Contains("voucher")) return MoneyType.Coupon;
            if (t.Contains("credit")) return MoneyType.Credit;
            if (t.Contains("cash")) return MoneyType.Cash;
            return MoneyType.Other;
        }

        /// <summary>"Exempt"/"E" -> exempt; "15", "15%", "15.5" -> percentage; blank -> neither (caller applies a default).</summary>
        public static (decimal? Percent, bool Exempt, bool Blank, bool Invalid) ParseTax(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, false, true, false);
            var t = raw.Trim().ToLowerInvariant();
            if (t is "exempt" or "e" or "ex" or "exempted") return (null, true, false, false);
            if (t is "zero" or "zero rated" or "zero-rated" or "zr") return (0m, false, false, false);
            return TryParseDecimal(t, out var p) && p is >= 0 and <= 100 ? (p, false, false, false) : (null, false, false, true);
        }

        public static DateTime? ParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] formats = { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yy", "yyyy/MM/dd", "yyyy-MM-ddTHH:mm:ss", "dd MMM yyyy", "d MMM yyyy" };
            return DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;
        }
    }
}
