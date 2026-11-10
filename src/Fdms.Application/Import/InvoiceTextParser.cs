using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Enums;

namespace Fdms.Application.Import
{
    /// <summary>
    /// Best-effort invoice reader for text taken from a (digital) PDF. It reads one line at a time, which survives far
    /// more layouts than the whole-page regex it replaces: a line item is a description followed by numeric columns.
    /// Results are always a DRAFT for a human to confirm — the importer warns about everything it had to guess.
    /// </summary>
    public static class InvoiceTextParser
    {
        private static readonly Regex Num = new(@"\(?-?\d[\d,]*(?:\.\d+)?\)?%?", RegexOptions.Compiled);
        private static readonly string[] NonItemStarts =
        {
            "description", "item", "product", "qty", "quantity", "unit price", "price", "amount", "subtotal", "sub total", "sub-total",
            "total", "grand total", "balance", "vat", "tax", "discount", "invoice", "date", "tel", "phone", "email", "page", "notes",
            "terms", "bank", "account", "bill to", "ship to", "sold to", "customer", "tin", "reg", "due", "payment", "thank", "www", "from", "to:",
        };



        public static InvoiceDraft Parse(string text)
        {
            var d = new InvoiceDraft();
            if (string.IsNullOrWhiteSpace(text)) { d.Warnings.Add("The PDF contains no readable text (scanned image?). OCR is not supported."); return d; }

            d.InvoiceNo = First(text, @"invoice\s*(?:no\.?|number|#)\s*[:#]?\s*([A-Za-z0-9][\w\-/]{1,40})", @"\binv[-\s#:]*([A-Za-z0-9][\w\-/]{1,40})");
            d.InvoiceDate = ImportParsing.ParseDate(First(text,
                @"(?:invoice\s*)?date\s*[:\-]?\s*(\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4})", @"(?:invoice\s*)?date\s*[:\-]?\s*(\d{4}-\d{2}-\d{2})", @"(\d{4}-\d{2}-\d{2})"));
            d.Currency = DetectCurrency(text);
            d.ReceiptType = Regex.IsMatch(text, @"\bcredit\s*note\b", RegexOptions.IgnoreCase) ? ReceiptType.CreditNote
                : Regex.IsMatch(text, @"\bdebit\s*note\b", RegexOptions.IgnoreCase) ? ReceiptType.DebitNote : ReceiptType.FiscalInvoice;

            var name = FindBuyerName(text);
            // the seller's own TIN, VAT number and email come first on an invoice: the buyer's are the ones after the "Bill to" label
            var label = Regex.Match(text, @"\b(?:bill(?:ed)?\s*to|sold\s*to|customer|buyer|client)\b", RegexOptions.IgnoreCase);
            var buyerText = label.Success ? text[label.Index..] : text;
            var tin = First(buyerText, @"\b(?:buyer\s*)?(?:tin|tax\s*id)\s*(?:no\.?)?\s*[:#]?\s*(\d{6,12})");
            if (name != null && tin != null)
                d.Buyer = new BuyerRequest(name.Trim(), null, tin, First(buyerText, @"vat\s*(?:no\.?|number|reg\w*)\s*[:#]?\s*(\d{6,12})"),
                    First(buyerText, @"([A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})"), null);
            else if (name != null || tin != null)
                d.Warnings.Add("Buyer details were only partly found (a buyer needs both a name and a TIN); the buyer was left out.");

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length < 6) continue;
                var item = TryParseItem(line);
                if (item != null) d.Lines.Add(item);
            }
            if (d.Lines.Count == 0) d.Warnings.Add("No line items could be recognised. Use the Excel import or the JSON API for this document.");

            var total = FindGrandTotal(text);
            var linesTotal = d.Lines.Sum(l => l.Quantity * l.UnitPrice);
            if (total.HasValue && d.Lines.Count > 0 && Math.Abs(total.Value - linesTotal) > 0.05m)
                d.Warnings.Add($"The document total ({total:0.00}) differs from the sum of the recognised lines ({linesTotal:0.00}) — check quantities, prices and tax.");

            if (d.Lines.Count > 0)
                d.Payments.Add(new ReceiptPaymentRequest(DetectMoneyType(text), total ?? linesTotal));
            if (d.Lines.Any(l => l.TaxPercent == null))
                d.Warnings.Add("Tax rates were not read from the PDF; the default rate or the device standard rate will be applied unless you correct the lines.");
            if (d.InvoiceNo == null) d.Warnings.Add("No invoice number was found.");
            return d;

        }


        /// <summary>"Widget  2  10.00  20.00" or "1. Widget 2 x 10.00 = 20.00" -> description, qty, unit price (last number is the line total).</summary>
        internal static ReceiptLineRequest? TryParseItem(string line)
        {
            var l = Regex.Replace(line, @"^\s*\d{1,3}[\.\)]\s+", "");           // leading "1." / "2)"
            var m = Regex.Match(l, @"^(?<desc>[A-Za-z].*?)\s+(?<nums>(?:[\$€£]?\(?-?\d[\d,]*(?:\.\d+)?\)?%?\s*(?:x|X|=)?\s*)+)$");
            if (!m.Success) return null;
            var desc = Regex.Replace(m.Groups["desc"].Value, @"\s+", " ").Trim(' ', '-', ':', '|');
            if (desc.Length < 3 || IsNonItem(desc)) return null;

            var nums = Num.Matches(m.Groups["nums"].Value).Select(x => x.Value).ToList();
            if (nums.Count < 2) return null;
            decimal? taxPct = null;
            var values = new List<decimal>(); var tokens = new List<string>();
            foreach (var n in nums)
            {
                if (n.EndsWith('%')) { if (ImportParsing.TryParseDecimal(n, out var p)) taxPct = p; continue; }
                if (!ImportParsing.TryParseDecimal(n.Trim('(', ')'), out var v)) return null;
                values.Add(v); tokens.Add(n);
            }

            static bool Adds(decimal qty, decimal price, decimal amount) => Math.Abs(qty * price - amount) <= 0.05m;
            string? hs = null;
            // a leading 4, 6 or 8 digit whole number is the HS code column, but only when the rest of the row adds up
            // (quantity x unit price = amount, with an optional VAT-rate figure between price and amount), so ordinary figures are never misread
            if (values.Count >= 4 && Regex.IsMatch(tokens[0], @"^(?:\d{4}|\d{6}|\d{8})$"))
            {
                var rest = values.Skip(1).ToList();
                if ((rest.Count == 3 && Adds(rest[0], rest[1], rest[2])) || (rest.Count == 4 && Adds(rest[0], rest[1], rest[3]) && rest[2] is >= 0 and <= 100))
                { hs = tokens[0]; values.RemoveAt(0); }
            }
            // a bare VAT-rate column: quantity, unit price, rate, amount (a rate printed without its % sign)
            if (values.Count == 4 && Adds(values[0], values[1], values[3]) && values[2] is >= 0 and <= 100)
            {
                taxPct ??= values[2]; values.RemoveAt(2);
            }
            decimal qty, price;
            switch (values.Count)
            {
                case 2:  // description, unit price, total   OR   description, qty, total
                    qty = 1; price = values[0]; break;
                case >= 3:
                    qty = values[0]; price = values[1];
                    // qty * price should roughly match the last column; otherwise the columns are probably price/qty swapped
                    if (Math.Abs(qty * price - values[^1]) > 0.05m && Math.Abs(values[1] * values[0] - values[^1]) > 0.05m)
                    {
                        if (Math.Abs(values[0] - values[^1]) < 0.01m) { qty = 1; price = values[0]; }
                        else return null;
                    }
                    break;
                default: return null;
            }
            if (qty <= 0 || price <= 0) return null;
            return new ReceiptLineRequest(desc, hs, qty, price, TaxPercent: taxPct);
        }

        private static readonly Regex BuyerLabel = new(@"\b(?:bill(?:ed)?\s*to|sold\s*to|customer|buyer|client)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LabelWord = new(@"^(?:payment|method|date|due|invoice|tin|vat|email|tel|phone|address|currency|notes|prices?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The buyer's name is the first real line after the "Bill to" label. In a two-column layout the label can share its line with another box's heading
        /// ("BILL TO  PAYMENT") and the name with that box's first row ("Moyo Retailers  Method: Cash"): both are skipped or trimmed.
        /// </summary>
        private static string? FindBuyerName(string text)
        {
            var m = BuyerLabel.Match(text);
            if (!m.Success) return null;
            foreach (var raw in text[(m.Index + m.Length)..].Split('\n').Take(5))
            {
                var l = raw.Trim(' ', ':', '-', '\t', '\r');
                if (l.Length < 3 || !char.IsLetter(l[0]) || LabelWord.IsMatch(l)) continue;
                l = Regex.Replace(l, @"\s+(?:method|payment|due\s*date|date|currency|invoice\s*no)\s*:.*$", "", RegexOptions.IgnoreCase).Trim();
                if (l.Length >= 3) return l.Length > 80 ? l[..80] : l;
            }
            return null;
        }

        private static bool IsNonItem(string desc)
        {
            var d = desc.ToLowerInvariant();
            return NonItemStarts.Any(s => d == s || d.StartsWith(s + " ") || d.StartsWith(s + ":")) || d.Contains("@");
        }

        private static decimal? FindGrandTotal(string text)
        {
            foreach (var pattern in new[] { @"(?:grand\s*total|total\s*due|amount\s*due|balance\s*due|total\s*payable)\s*[:\-]?\s*[\$€£]?\s*([\d,]+(?:\.\d{1,2})?)", @"\btotal\s*[:\-]?\s*[\$€£]?\s*([\d,]+(?:\.\d{1,2})?)\s*$" })
            {
                var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (matches.Count > 0 && ImportParsing.TryParseDecimal(matches[^1].Groups[1].Value, out var v) && v > 0) return v;
            }
            return null;
        }

        private static string? First(string text, params string[] patterns)
        {
            foreach (var p in patterns)
            {
                var m = Regex.Match(text, p, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (m.Success && m.Groups.Count > 1 && !string.IsNullOrWhiteSpace(m.Groups[1].Value)) return m.Groups[1].Value.Trim();
            }
            return null;
        }

        private static string? DetectCurrency(string text)
        {
            if (Regex.IsMatch(text, @"\b(ZWG|ZiG)\b")) return "ZWG";
            if (Regex.IsMatch(text, @"\bZWL\b", RegexOptions.IgnoreCase)) return "ZWL";
            if (Regex.IsMatch(text, @"\bUSD\b|US\$|\$", RegexOptions.IgnoreCase)) return "USD";
            if (Regex.IsMatch(text, @"\bZAR\b|\bR\s?\d", RegexOptions.IgnoreCase)) return "ZAR";
            if (Regex.IsMatch(text, @"\bGBP\b|£", RegexOptions.IgnoreCase)) return "GBP";
            if (Regex.IsMatch(text, @"\bEUR\b|€", RegexOptions.IgnoreCase)) return "EUR";
            return null;
        }

        private static MoneyType DetectMoneyType(string text) =>
            Regex.IsMatch(text, @"\b(ecocash|onemoney|innbucks|mobile\s*money)\b", RegexOptions.IgnoreCase) ? MoneyType.MobileWallet
            : Regex.IsMatch(text, @"\b(visa|mastercard|card|swipe)\b", RegexOptions.IgnoreCase) ? MoneyType.Card
            : Regex.IsMatch(text, @"\b(bank\s*transfer|eft|rtgs)\b", RegexOptions.IgnoreCase) ? MoneyType.BankTransfer
            : MoneyType.Cash;
    }
}
