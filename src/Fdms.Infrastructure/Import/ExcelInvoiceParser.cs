using ClosedXML.Excel;
using Fdms.Application.Abstractions;
using Fdms.Application.Import;

namespace Fdms.Infrastructure.Import
{
    /// <summary>
    /// Reads an invoice spreadsheet: one row per line item, rows grouped into invoices by "Invoice No" (a blank invoice no
    /// continues the invoice above). Column names are case-insensitive and the original dara headers are all accepted.
    /// </summary>
    public static class ExcelInvoiceParser
    {
        public const int MaxRows = 5000;

        private static readonly Dictionary<string, string[]> Columns = new()
        {
            ["invoice"] = new[] { "invoice no", "invoice number", "invoiceno", "invoice #" },
            ["date"] = new[] { "invoice date", "date" },
            ["currency"] = new[] { "currency" },
            ["type"] = new[] { "receipt type", "type", "document type" },
            ["notes"] = new[] { "notes", "reason", "note" },
            ["original"] = new[] { "original invoice no", "original invoice", "original invoice number" },
            ["buyer"] = new[] { "customer name", "buyer name", "customer" },
            ["tin"] = new[] { "customer tin", "buyer tin", "tin" },
            ["vat"] = new[] { "customer vat no", "customer vat", "buyer vat no", "customer vat number" },
            ["email"] = new[] { "customer email", "buyer email" },
            ["phone"] = new[] { "customer phone", "buyer phone" },
            ["item"] = new[] { "item description", "description", "item", "product" },
            ["qty"] = new[] { "quantity", "qty" },
            ["price"] = new[] { "unit price", "price" },
            ["tax"] = new[] { "tax percent", "tax %", "vat %", "tax", "vat" },
            ["taxid"] = new[] { "tax id", "taxid" },
            ["hs"] = new[] { "hs code", "hscode", "hs" },
            ["paymethod"] = new[] { "payment method", "pay method" },
            ["payamount"] = new[] { "payment amount", "pay amount" },
        };

        public static (List<InvoiceDraft> Drafts, List<string> Errors) Parse(Stream file)
        {
            var errors = new List<string>();
            var drafts = new List<InvoiceDraft>();
            using var wb = new XLWorkbook(file);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null) { errors.Add("The workbook has no worksheets."); return (drafts, errors); }
            var used = ws.RangeUsed();
            if (used == null || used.RowCount() < 2) { errors.Add("The sheet is empty or has no data rows."); return (drafts, errors); }
            if (used.RowCount() - 1 > MaxRows) { errors.Add($"The sheet has more than {MaxRows} rows; split it into smaller files."); return (drafts, errors); }

            var first = used.FirstRow().RowNumber();
            var firstCol = used.FirstColumn().ColumnNumber();
            var lastCol = used.LastColumn().ColumnNumber();
            var map = new Dictionary<string, int>();
            for (var c = firstCol; c <= lastCol; c++)
            {
                var h = ws.Cell(first, c).GetString().Trim().ToLowerInvariant();
                if (h.Length == 0) continue;
                foreach (var (key, names) in Columns)
                    if (!map.ContainsKey(key) && names.Contains(h)) map[key] = c;
            }
            foreach (var need in new[] { "item", "price" })
                if (!map.ContainsKey(need)) errors.Add($"Required column missing: \"{(need == "item" ? "Item Description" : "Unit Price")}\".");
            if (errors.Count > 0) return (drafts, errors);

            string Get(int row, string key) => map.TryGetValue(key, out var c) ? ws.Cell(row, c).GetFormattedString().Trim() : "";

            InvoiceDraft? current = null;
            var byNo = new Dictionary<string, InvoiceDraft>(StringComparer.OrdinalIgnoreCase);
            for (var row = first + 1; row <= used.LastRow().RowNumber(); row++)
            {
                var desc = Get(row, "item");
                if (string.IsNullOrWhiteSpace(desc)) continue;
                var no = Get(row, "invoice");

                if (no.Length > 0)
                {
                    if (!byNo.TryGetValue(no, out current))
                    {
                        current = new InvoiceDraft { InvoiceNo = no };
                        byNo[no] = current;
                        drafts.Add(current);
                        current.InvoiceDate = ImportParsing.ParseDate(Get(row, "date"));
                        var cur = Get(row, "currency");
                        current.Currency = cur.Length == 0 ? null : cur.ToUpperInvariant();
                        current.ReceiptType = ImportParsing.ParseReceiptType(Get(row, "type"));
                        current.Notes = NullIfEmpty(Get(row, "notes"));
                        current.OriginalInvoiceNo = NullIfEmpty(Get(row, "original"));
                        var name = Get(row, "buyer"); var tin = Get(row, "tin");
                        if (name.Length > 0 && tin.Length > 0)
                            current.Buyer = new BuyerRequest(name, null, tin, NullIfEmpty(Get(row, "vat")), NullIfEmpty(Get(row, "email")), NullIfEmpty(Get(row, "phone")));
                        else if (name.Length > 0 || tin.Length > 0)
                            current.Warnings.Add($"Row {row}: a buyer needs both a name and a TIN; buyer details were ignored.");
                    }
                }
                else if (current == null)
                {
                    errors.Add($"Row {row}: no Invoice No (and no invoice above to continue).");
                    continue;
                }

                if (!ImportParsing.TryParseDecimal(Get(row, "qty"), out var qty) || Get(row, "qty").Length == 0) qty = 1;
                if (qty <= 0) { errors.Add($"Row {row}: quantity must be greater than 0."); continue; }
                if (!ImportParsing.TryParseDecimal(Get(row, "price"), out var price)) { errors.Add($"Row {row}: Unit Price is not a number."); continue; }

                var tax = ImportParsing.ParseTax(Get(row, "tax"));
                if (tax.Invalid) { errors.Add($"Row {row}: Tax Percent \"{Get(row, "tax")}\" is not understood (use a number, 0, or Exempt)."); continue; }
                int? taxId = null;
                var taxIdText = Get(row, "taxid");
                if (taxIdText.Length > 0)
                {
                    if (!int.TryParse(taxIdText, out var tid)) { errors.Add($"Row {row}: Tax ID must be a whole number."); continue; }
                    taxId = tid;
                }

                current!.Lines.Add(new ReceiptLineRequest(desc, NullIfEmpty(Get(row, "hs")), qty, price, taxId, tax.Percent, tax.Exempt));

                var method = Get(row, "paymethod"); var amountText = Get(row, "payamount");
                if (method.Length > 0 || amountText.Length > 0)
                {
                    var pay = new ReceiptPaymentRequest(ImportParsing.ParseMoneyType(method), ImportParsing.TryParseDecimal(amountText, out var a) ? a : 0m);
                    // the same payment is often repeated on every line of an invoice: keep it once
                    if (!current.Payments.Any(p => p.MoneyType == pay.MoneyType && p.Amount == pay.Amount)) current.Payments.Add(pay);
                }
            }
            return (drafts, errors);
        }

        private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        public static byte[] BuildTemplate()
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Invoices");
            string[] headers =
            {
                "Invoice No", "Invoice Date", "Currency", "Receipt Type", "Original Invoice No", "Notes", "Customer Name", "Customer TIN",
                "Customer VAT No", "Customer Email", "Customer Phone", "Item Description", "HS Code", "Quantity", "Unit Price", "Tax Percent", "Payment Method", "Payment Amount",
            };
            for (var i = 0; i < headers.Length; i++)
            {
                var c = ws.Cell(1, i + 1);
                c.Value = headers[i]; c.Style.Font.Bold = true; c.Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
            }
            object[][] rows =
            {
                new object[] { "INV-1001", "2025-01-31", "USD", "FiscalInvoice", "", "", "ACME Pvt Ltd", "1234567890", "123456789", "accounts@acme.co.zw", "", "Men's shoes", "64039100", 2, 25.00, 15, "Cash", 57.50 },
                new object[] { "", "", "", "", "", "", "", "", "", "", "", "Bread", "19059010", 5, 1.00, "Exempt", "", "" },
                new object[] { "CN-1001", "2025-02-02", "USD", "CreditNote", "INV-1001", "Returned goods", "", "", "", "", "", "Men's shoes", "64039100", 1, 25.00, 15, "Cash", "" },
            };
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                {
                    var cell = ws.Cell(r + 2, c + 1);
                    switch (rows[r][c]) { case int i: cell.Value = i; break; case double d: cell.Value = d; break; default: cell.Value = rows[r][c]?.ToString() ?? ""; break; }
                }
            ws.Columns().AdjustToContents();

            var help = wb.AddWorksheet("Read me");
            string[] lines =
            {
                "One row per line item. Rows with the same Invoice No form one invoice; leave Invoice No blank to continue the invoice above.",
                "Receipt Type: FiscalInvoice (default), CreditNote or DebitNote. Credit/debit notes need Original Invoice No and Notes (the reason).",
                "Unit Price is positive for every type; credit notes are negated automatically.",
                "Tax Percent: a number (15, 15.5, 0) or Exempt. Leave blank to use the default rate chosen at upload.",
                "Payment Method: Cash, Card, MobileWallet (EcoCash, OneMoney...), BankTransfer, Coupon, Credit, Other. Leave payments blank to record the full amount as Cash.",
                "HS Code is required for VAT-registered taxpayers (4 or 8 digits).",
                "Customer name AND TIN must both be given for buyer details to be sent.",
                "Uploading the same invoice number again never fiscalises it twice.",
            };
            for (var i = 0; i < lines.Length; i++) help.Cell(i + 1, 1).Value = lines[i];
            help.Column(1).Width = 140;

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }
    }
}
