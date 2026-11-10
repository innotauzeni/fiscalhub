using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Starting the books here (a "conversion"): the trial balance at the conversion date, the customers' unpaid invoices and the suppliers' unpaid bills, read from Excel or CSV,
    /// checked together (the trial balance balances; receivables and payables equal the detail behind them) and posted in one go. Accounts that are not in the chart yet are created.
    /// When the detail is given, receivables and payables are posted invoice by invoice, so each can be paid and aged; the trial balance's own lines for them are left out.
    /// </summary>
    public sealed class ConversionService : IConversionService
    {
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IAccountingService _books;
        public ConversionService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;
        private static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public async Task<IReadOnlyList<ConversionDoneDto>> StatusAsync(Guid clientId, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var done = await _db.JournalEntries.AsNoTracking().Where(e => e.ClientId == clientId && e.Source == "opening" && e.ReversedById == null && e.ReversesId == null)
                .OrderBy(e => e.Currency).Select(e => new { e.Currency, e.Date, e.Number, e.Id }).ToListAsync(ct);
            var ar = await _db.SalesInvoices.AsNoTracking().Where(d => d.ClientId == clientId && d.Kind == "opening" && d.Status == "Issued").GroupBy(d => d.Currency).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            var openingEquity = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && a.Role == "opening_balance").Select(a => a.Id).ToListAsync(ct);
            var ap = await _db.Bills.AsNoTracking().Where(b => b.ClientId == clientId && !b.IsVoid && openingEquity.Contains(b.ExpenseAccountId)).GroupBy(b => b.Currency).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
            return done.Select(d => new ConversionDoneDto(d.Currency, d.Date, d.Number, d.Id, ar.FirstOrDefault(x => x.Key == d.Currency)?.N ?? 0, ap.FirstOrDefault(x => x.Key == d.Currency)?.N ?? 0)).ToList();
        }

        // ---- templates and reading files -----------------------------------------------------------------------------------------

        private static readonly Dictionary<string, (string[] Headers, object[][] Examples, string Help)> Templates = new()
        {
            ["balances"] = (new[] { "Code", "Account name", "Type", "Debit", "Credit" },
                new[] { new object[] { "1020", "Bank", "Asset", 12500.00, "" }, new object[] { "1100", "Accounts receivable", "Asset", 3400.00, "" }, new object[] { "2010", "Accounts payable", "Liability", "", 1800.00 },
                        new object[] { "3010", "Owner's capital", "Equity", "", 10000.00 }, new object[] { "4010", "Sales", "Income", "", 8100.00 }, new object[] { "5110", "Rent", "Expense", 4000.00, "" } },
                "Your trial balance at the conversion date: one row per account with its balance as a debit or a credit. Accounts not in your chart yet are created (give their name and type: Asset, Liability, Equity, Income or Expense). Income and expense balances are this financial year's figures so far."),
            ["customers"] = (new[] { "Customer", "Invoice number", "Invoice date", "Due date", "Amount owing", "Email", "TIN" },
                new[] { new object[] { "Moyo Traders", "INV-1041", "2026-08-14", "2026-09-13", 2400.00, "accounts@moyo.co.zw", "" }, new object[] { "Chipo's Salon", "INV-1057", "2026-09-02", "", 1000.00, "", "" } },
                "Every invoice a customer still owed at the conversion date, with what was still owing on it (not the original amount if part was paid). Their total must equal accounts receivable in your trial balance."),
            ["suppliers"] = (new[] { "Supplier", "Bill number", "Bill date", "Due date", "Amount owing", "Email", "TIN" },
                new[] { new object[] { "Harare Office Supplies", "HOS-778", "2026-09-10", "2026-10-10", 1800.00, "", "" } },
                "Every supplier bill still unpaid at the conversion date, with what was still owing on it. Their total must equal accounts payable in your trial balance."),
        };

        public (byte[] Bytes, string FileName) Template(string kind)
        {
            if (!Templates.TryGetValue(kind, out var t)) throw GatewayException.BadRequest("KIND_INVALID", "Choose balances, customers or suppliers.");
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(kind == "balances" ? "Trial balance" : kind == "customers" ? "Customers owing" : "Suppliers owed");
            for (var i = 0; i < t.Headers.Length; i++) { ws.Cell(1, i + 1).Value = t.Headers[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2F3F1"); }
            for (var r = 0; r < t.Examples.Length; r++)
                for (var c = 0; c < t.Examples[r].Length; c++)
                {
                    var v = t.Examples[r][c]; var cell = ws.Cell(r + 2, c + 1);
                    if (v is double d) { cell.Value = d; cell.Style.NumberFormat.Format = "#,##0.00"; }
                    else if (v is string s && DateTime.TryParseExact(s, "yyyy-MM-dd", C, DateTimeStyles.None, out var dt)) { cell.Value = dt; cell.Style.DateFormat.Format = "yyyy-mm-dd"; }
                    else cell.Value = v?.ToString() ?? "";
                }
            ws.Columns().AdjustToContents();
            var help = wb.AddWorksheet("How to fill it in");
            help.Cell(1, 1).Value = t.Help; help.Cell(1, 1).Style.Alignment.WrapText = true; help.Column(1).Width = 100;
            help.Cell(3, 1).Value = "Replace the example rows with your own. Keep the first row (the column names). Dates as yyyy-mm-dd or dd/mm/yyyy.";
            using var ms = new MemoryStream(); wb.SaveAs(ms);
            return (ms.ToArray(), $"fiscalhub-{kind}-template.xlsx");
        }

        internal static List<List<string>> ReadTable(string fileName, Stream file)
        {
            var rows = new List<List<string>>();
            if (fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var wb = new XLWorkbook(file);
                    var ws = wb.Worksheets.First();
                    var used = ws.RangeUsed();
                    if (used == null) return rows;
                    var lastCol = used.LastColumn().ColumnNumber();
                    foreach (var r in used.Rows())
                    {
                        var cells = new List<string>();
                        for (var c = 1; c <= lastCol; c++)
                        {
                            var cell = r.WorksheetRow().Cell(c);
                            cells.Add(cell.DataType switch
                            {
                                XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", C),
                                XLDataType.Number => cell.GetDouble().ToString("0.############", C),
                                _ => cell.GetFormattedString().Trim(),
                            });
                        }
                        rows.Add(cells);
                    }
                }
                catch (Exception ex) when (ex is not GatewayException) { throw GatewayException.BadRequest("FILE_UNREADABLE", "That Excel file could not be read. Save it again as .xlsx (or CSV) and retry."); }
                return rows;
            }
            if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                throw GatewayException.BadRequest("FILE_TYPE", "Upload an Excel file (.xlsx) or a CSV file.");
            using var reader = new StreamReader(file, Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                if (line.Trim().Length > 0) rows.Add(ChartService.SplitCsv(line));
            return rows;
        }

        internal static decimal? Amount(string? v)
        {
            var s = (v ?? "").Trim();
            if (s.Length == 0 || s == "-") return 0;
            var negative = s.StartsWith('(') && s.EndsWith(')') || s.StartsWith('-') || s.EndsWith("CR", StringComparison.OrdinalIgnoreCase);
            s = Regex.Replace(s, @"[^0-9.,]", "");
            if (s.Contains(',') && s.Contains('.')) s = s.Replace(",", "");                 // 1,234.56
            else if (s.Contains(',') && Regex.IsMatch(s, @",\d{1,2}$")) s = s.Replace(',', '.');   // 1234,56
            else s = s.Replace(",", "");
            return decimal.TryParse(s, NumberStyles.Number, C, out var d) ? Math.Round(negative ? -d : d, 2) : null;
        }

        internal static DateTime? Date(string? v)
        {
            var s = (v ?? "").Trim();
            if (s.Length == 0) return null;
            string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d MMM yyyy", "dd MMM yyyy", "d-MMM-yyyy", "dd-MMM-yy", "yyyy-MM-dd HH:mm:ss" };
            return DateTime.TryParseExact(s, formats, C, DateTimeStyles.None, out var d) ? d.Date : null;
        }

        /// <summary>Which column holds what, from the header row (any of the usual names), or the template's order when the file has no header.</summary>
        internal static (Dictionary<string, int> Map, bool HasHeader) Columns(List<string> first, Dictionary<string, string[]> names, string[] order)
        {
            var map = new Dictionary<string, int>();
            for (var i = 0; i < first.Count; i++)
            {
                var h = Regex.Replace(first[i].ToLowerInvariant(), @"[^a-z]", "");
                foreach (var (key, alts) in names) if (!map.ContainsKey(key) && alts.Contains(h)) { map[key] = i; break; }
            }
            if (map.Count >= 2) return (map, true);
            return (order.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i), false);
        }

        public async Task<ConversionFileDto> ReadAsync(Guid clientId, string kind, string fileName, Stream file, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await Task.CompletedTask;
            if (!Templates.ContainsKey(kind)) throw GatewayException.BadRequest("KIND_INVALID", "Choose balances, customers or suppliers.");
            var table = ReadTable(fileName ?? "", file);
            var problems = new List<string>();
            if (table.Count == 0) throw GatewayException.BadRequest("FILE_EMPTY", "The file is empty.");
            if (table.Count > 5001) throw GatewayException.BadRequest("FILE_TOO_LARGE", "At most 5000 rows at a time.");
            string Cell(List<string> row, Dictionary<string, int> map, string key) => map.TryGetValue(key, out var i) && i < row.Count ? row[i].Trim() : "";

            if (kind == "balances")
            {
                var (map, header) = Columns(table[0], new()
                {
                    ["code"] = new[] { "code", "accountcode", "accountno", "accountnumber", "acc", "accno", "number", "glcode", "ledgercode" },
                    ["name"] = new[] { "name", "accountname", "account", "description", "accountdescription", "ledger" },
                    ["type"] = new[] { "type", "accounttype", "class", "category" },
                    ["debit"] = new[] { "debit", "dr", "debits", "debitbalance" },
                    ["credit"] = new[] { "credit", "cr", "credits", "creditbalance" },
                    ["balance"] = new[] { "balance", "amount", "closingbalance", "net" },
                }, new[] { "code", "name", "type", "debit", "credit" });
                var list = new List<ConversionBalanceRow>();
                foreach (var (row, i) in table.Select((r, i) => (r, i)).Skip(header ? 1 : 0))
                {
                    var n = i + 1;
                    var code = Cell(row, map, "code");
                    if (code.Length == 0 || Regex.IsMatch(code, "^(total|totals)$", RegexOptions.IgnoreCase)) continue;
                    decimal dr, cr;
                    if (map.ContainsKey("debit") || map.ContainsKey("credit"))
                    {
                        var d = Amount(Cell(row, map, "debit")); var c = Amount(Cell(row, map, "credit"));
                        if (d == null || c == null) { problems.Add($"Row {n} ({code}): the debit or credit is not a number."); continue; }
                        (dr, cr) = (d.Value, c.Value);
                        if (dr < 0) (dr, cr) = (0, cr - dr);
                        if (cr < 0) (dr, cr) = (dr - cr, 0);
                    }
                    else
                    {
                        var b = Amount(Cell(row, map, "balance"));
                        if (b == null) { problems.Add($"Row {n} ({code}): the balance is not a number."); continue; }
                        (dr, cr) = b.Value >= 0 ? (b.Value, 0m) : (0m, -b.Value);
                    }
                    var name = Cell(row, map, "name"); var type = Cell(row, map, "type");
                    list.Add(new ConversionBalanceRow(code, name.Length == 0 ? null : name, type.Length == 0 ? null : type, dr, cr));
                }
                return new ConversionFileDto(kind, list, Array.Empty<ConversionPartyRow>(), problems);
            }
            else
            {
                var (map, header) = Columns(table[0], new()
                {
                    ["name"] = new[] { "customer", "supplier", "name", "customername", "suppliername", "client", "vendor", "party", "account" },
                    ["number"] = new[] { "invoicenumber", "billnumber", "number", "invoice", "bill", "invoiceno", "billno", "reference", "ref", "documentnumber", "docno" },
                    ["date"] = new[] { "invoicedate", "billdate", "date", "documentdate" },
                    ["due"] = new[] { "duedate", "due", "paymentdue" },
                    ["amount"] = new[] { "amountowing", "amount", "balance", "outstanding", "owing", "amountdue", "due amount", "balancedue", "openamount" },
                    ["email"] = new[] { "email", "emailaddress" },
                    ["tin"] = new[] { "tin", "taxnumber", "bpnumber" },
                }, new[] { "name", "number", "date", "due", "amount", "email", "tin" });
                var list = new List<ConversionPartyRow>();
                foreach (var (row, i) in table.Select((r, i) => (r, i)).Skip(header ? 1 : 0))
                {
                    var n = i + 1;
                    var name = Cell(row, map, "name");
                    if (name.Length == 0 || Regex.IsMatch(name, "^(total|totals)$", RegexOptions.IgnoreCase)) continue;
                    var number = Cell(row, map, "number"); var date = Date(Cell(row, map, "date")); var due = Date(Cell(row, map, "due")); var amount = Amount(Cell(row, map, "amount"));
                    if (date == null) { problems.Add($"Row {n} ({name}): the date \"{Cell(row, map, "date")}\" is not a date (use yyyy-mm-dd or dd/mm/yyyy)."); continue; }
                    if (amount == null) { problems.Add($"Row {n} ({name}): the amount is not a number."); continue; }
                    var email = Cell(row, map, "email"); var tin = Cell(row, map, "tin");
                    list.Add(new ConversionPartyRow(name, number, date.Value, due, amount.Value, email.Length == 0 ? null : email, tin.Length == 0 ? null : tin));
                }
                return new ConversionFileDto(kind, Array.Empty<ConversionBalanceRow>(), list, problems);
            }
        }

        // ---- checking --------------------------------------------------------------------------------------------------------------

        private sealed class Plan
        {
            public string Currency = ""; public DateTime Date;
            public List<(ConversionBalanceRow Row, LedgerAccount? Account, string Name, string Type, int Line, string? SubType)> Balances = new();
            public List<ConversionPartyRow> Customers = new(); public List<ConversionPartyRow> Suppliers = new();
            public List<string> Problems = new(); public List<string> Warnings = new();
            public decimal Dr, Cr, ArTb, ApTb, ArDetail, ApDetail; public int NewCustomers, NewSuppliers; public int Terms;
            public HashSet<Guid> ArAccounts = new(), ApAccounts = new();
        }

        private static string? GuessType(string code, ChartRules rules)
        {
            var digits = new string(code.Where(char.IsDigit).ToArray());
            return new[] { "Asset", "Liability", "Equity", "Income", "Expense" }.Select(t => (t, s: ChartCodes.StartOf(rules, t))).Where(x => x.s.Length > 0 && digits.StartsWith(x.s))
                .OrderByDescending(x => x.s.Length).Select(x => x.t).FirstOrDefault();
        }

        private async Task<Plan> PlanAsync(Guid clientId, ConversionRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            await _books.EnsureChartAsync(clientId, ct);
            var p = new Plan { Date = req.Date.Date, Currency = (req.Currency ?? "").Trim().ToUpperInvariant() };
            if (!await _db.Currencies.AnyAsync(c => c.Code == p.Currency && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (p.Date.Year < 2000 || p.Date > Today.AddDays(1)) throw GatewayException.BadRequest("DATE_INVALID", "The conversion date cannot be in the future.");
            if (await _books.LockedUntilAsync(clientId, ct) is { } locked && p.Date <= locked) p.Problems.Add($"Your books are closed up to {locked:d MMM yyyy}. Choose a later conversion date or reopen the period.");
            if (await _db.JournalEntries.AnyAsync(e => e.ClientId == clientId && e.Source == "opening" && e.Currency == p.Currency && e.ReversedById == null && e.ReversesId == null, ct))
                p.Problems.Add($"Opening balances in {p.Currency} were already posted. Reverse that entry under Journals to bring them in again.");
            p.Terms = (await _db.AccountingSettings.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => (int?)s.CustomerTermsDays).FirstOrDefaultAsync(ct)) is > 0 and var t ? t : 30;

            var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).ToListAsync(ct);
            var byCode = accounts.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = req.Balances ?? Array.Empty<ConversionBalanceRow>();
            if (rows.Count > 5000) throw GatewayException.BadRequest("TOO_MANY", "At most 5000 accounts.");
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i]; var n = i + 1; var code = (r.Code ?? "").Trim();
                if (r.Debit == 0 && r.Credit == 0) continue;
                if (!AccountingService.CodeRx.IsMatch(code)) { p.Problems.Add($"Line {n}: \"{code}\" is not a valid account code."); continue; }
                if (!seen.Add(code)) { p.Problems.Add($"Line {n}: account {code} appears twice. Give each account one line."); continue; }
                if (r.Debit < 0 || r.Credit < 0 || r.Debit != Math.Round(r.Debit, 2) || r.Credit != Math.Round(r.Credit, 2)) { p.Problems.Add($"Line {n} ({code}): amounts are positive with at most two decimals."); continue; }
                if (r.Debit > 0 && r.Credit > 0) { p.Problems.Add($"Line {n} ({code}): a balance is either a debit or a credit, not both."); continue; }
                var givenType = string.IsNullOrWhiteSpace(r.Type) ? null : ChartService.TypeOf(r.Type!);
                if (byCode.TryGetValue(code, out var acc))
                {
                    if (acc.IsGroup) { p.Problems.Add($"Line {n}: {acc.Code} {acc.Name} is a heading. Put its balance on one of its sub-accounts."); continue; }
                    if (!acc.IsActive) { p.Problems.Add($"Line {n}: {acc.Code} {acc.Name} is switched off in your chart. Switch it on first."); continue; }
                    if (givenType != null && givenType != acc.Type) p.Warnings.Add($"Line {n}: {acc.Code} is a {acc.Type} account in your chart (the file says {givenType}); it stays {acc.Type}.");
                    if (acc.Role == "receivable") p.ArAccounts.Add(acc.Id);
                    if (acc.Role == "payable") p.ApAccounts.Add(acc.Id);
                    p.Balances.Add((r with { Code = acc.Code }, acc, acc.Name, acc.Type, n, ChartCodes.SubTypeOf(acc)));
                }
                else
                {
                    var name = (r.Name ?? "").Trim();
                    if (name.Length is < 2 or > 100) { p.Problems.Add($"Line {n}: account {code} is not in your chart; give it a name (2-100 characters) to create it."); continue; }
                    if (!string.IsNullOrWhiteSpace(r.Type) && givenType == null) { p.Problems.Add($"Line {n} ({code}): the type \"{r.Type}\" is not Asset, Liability, Equity, Income or Expense."); continue; }
                    var type = givenType ?? GuessType(code, rules);
                    if (type == null) { p.Problems.Add($"Line {n}: say what type account {code} is (Asset, Liability, Equity, Income or Expense)."); continue; }
                    if (ChartCodes.Problem(code, type, rules) is { } bad) { p.Problems.Add($"Line {n} ({code}): {bad}"); continue; }
                    var sub = string.IsNullOrWhiteSpace(r.SubType) ? null : ChartService.SubTypeOf(r.SubType!, type);
                    if (!string.IsNullOrWhiteSpace(r.SubType) && sub == null) p.Warnings.Add($"Line {n} ({code}): \"{r.SubType}\" is not a place a {type} account can show; it is placed like its neighbours.");
                    // like the account just before it in the chart (1650 after 1590 accumulated depreciation is a fixed asset)
                    sub ??= accounts.Where(a => a.Type == type && !a.IsGroup && string.CompareOrdinal(a.Code, code) < 0).OrderByDescending(a => a.Code, StringComparer.Ordinal).Select(ChartCodes.SubTypeOf).FirstOrDefault();
                    if (sub == "cash") sub = null;   // cash and bank accounts are made under Banks
                    p.Balances.Add((r with { Code = code }, null, name, type, n, sub));
                }
            }
            p.Dr = p.Balances.Sum(b => b.Row.Debit); p.Cr = p.Balances.Sum(b => b.Row.Credit);
            p.ArTb = p.Balances.Where(b => b.Account != null && p.ArAccounts.Contains(b.Account.Id)).Sum(b => b.Row.Debit - b.Row.Credit);
            p.ApTb = p.Balances.Where(b => b.Account != null && p.ApAccounts.Contains(b.Account.Id)).Sum(b => b.Row.Credit - b.Row.Debit);
            if (p.Dr != p.Cr && !req.PostDifferenceToEquity)
                p.Problems.Add($"The trial balance does not balance: debits {p.Dr:N2}, credits {p.Cr:N2} (a difference of {Math.Abs(p.Dr - p.Cr):N2}). Correct the file, or tick \"post the difference to opening balance equity\".");
            else if (p.Dr != p.Cr) p.Warnings.Add($"The difference of {Math.Abs(p.Dr - p.Cr):N2} goes to opening balance equity. Find out what it is and correct it with a journal.");
            if (p.Balances.Any(b => b.Type is "Income" or "Expense"))
                p.Warnings.Add("Income and expense balances are posted as this financial year's figures so far, dated on the conversion date.");

            async Task Parties(IReadOnlyList<ConversionPartyRow>? input, List<ConversionPartyRow> into, string what, bool customers)
            {
                var list = input ?? Array.Empty<ConversionPartyRow>();
                if (list.Count > 5000) throw GatewayException.BadRequest("TOO_MANY", $"At most 5000 {what} at a time.");
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var names = customers ? await _db.Customers.AsNoTracking().Where(c => c.ClientId == clientId).Select(c => c.Name).ToListAsync(ct)
                    : await _db.Suppliers.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => s.Name).ToListAsync(ct);
                var known = names.ToHashSet(StringComparer.OrdinalIgnoreCase); var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < list.Count; i++)
                {
                    var r = list[i]; var n = i + 1; var name = (r.Name ?? "").Trim(); var number = (r.Number ?? "").Trim();
                    if (name.Length is < 2 or > 120) { p.Problems.Add($"{what} row {n}: the name must be 2-120 characters."); continue; }
                    if (number.Length is < 1 or > 50) { p.Problems.Add($"{what} row {n} ({name}): give the document number (up to 50 characters)."); continue; }
                    if (r.Amount <= 0 || r.Amount != Math.Round(r.Amount, 2)) { p.Problems.Add($"{what} row {n} ({name} {number}): the amount owing must be above zero with at most two decimals."); continue; }
                    if (r.Date.Date > p.Date) { p.Problems.Add($"{what} row {n} ({name} {number}): dated after the conversion date."); continue; }
                    if (r.DueDate is { } due && due.Date < r.Date.Date) { p.Problems.Add($"{what} row {n} ({name} {number}): due before its date."); continue; }
                    if (!keys.Add(name + "|" + number)) { p.Problems.Add($"{what} row {n}: {name} {number} appears twice."); continue; }
                    if (!known.Contains(name) && added.Add(name)) { if (customers) p.NewCustomers++; else p.NewSuppliers++; }
                    into.Add(r with { Name = name, Number = number, Email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim(), Tin = string.IsNullOrWhiteSpace(r.Tin) ? null : r.Tin.Trim() });
                }
            }
            await Parties(req.Customers, p.Customers, "Customer", true);
            await Parties(req.Suppliers, p.Suppliers, "Supplier", false);
            p.ArDetail = p.Customers.Sum(c => c.Amount); p.ApDetail = p.Suppliers.Sum(s => s.Amount);

            if (p.Customers.Count > 0 && await _db.SalesInvoices.AsNoTracking().Where(d => d.ClientId == clientId && d.Kind == "opening" && d.Status == "Issued").Select(d => d.CustomerName + "|" + d.Number).ToListAsync(ct) is var have)
                foreach (var c in p.Customers.Where(c => have.Contains(c.Name + "|" + c.Number, StringComparer.OrdinalIgnoreCase)))
                    p.Problems.Add($"{c.Name} {c.Number} was already brought in.");
            if (p.Suppliers.Count > 0)
            {
                var have2 = await (from b in _db.Bills.AsNoTracking() join s in _db.Suppliers.AsNoTracking() on b.SupplierId equals s.Id where b.ClientId == clientId && !b.IsVoid select s.Name + "|" + b.Number).ToListAsync(ct);
                foreach (var s in p.Suppliers.Where(s => have2.Contains(s.Name + "|" + s.Number, StringComparer.OrdinalIgnoreCase)))
                    p.Problems.Add($"Bill {s.Number} from {s.Name} is already in your bills.");
            }
            if (p.Balances.Count > 0)
            {
                if (p.Customers.Count > 0 && p.ArTb != p.ArDetail)
                    p.Problems.Add($"Accounts receivable in the trial balance is {p.ArTb:N2} but the customers' invoices add up to {p.ArDetail:N2}. They must agree.");
                if (p.Suppliers.Count > 0 && p.ApTb != p.ApDetail)
                    p.Problems.Add($"Accounts payable in the trial balance is {p.ApTb:N2} but the suppliers' bills add up to {p.ApDetail:N2}. They must agree.");
                if (p.Customers.Count == 0 && p.ArTb != 0) p.Warnings.Add("Receivables are brought in as one balance. Add the customers' unpaid invoices to follow and age each one.");
                if (p.Suppliers.Count == 0 && p.ApTb != 0) p.Warnings.Add("Payables are brought in as one balance. Add the suppliers' unpaid bills to pay and age each one.");
            }
            if (p.Balances.Count == 0 && p.Customers.Count == 0 && p.Suppliers.Count == 0) p.Problems.Add("There is nothing to bring in yet.");
            return p;
        }

        public async Task<ConversionCheckDto> CheckAsync(Guid clientId, ConversionRequest req, CancellationToken ct)
        {
            var p = await PlanAsync(clientId, req, ct);
            var lines = p.Balances.Select(b => new ConversionLineDto(b.Line, b.Row.Code, b.Name, b.Type, b.Account == null, b.Row.Debit, b.Row.Credit,
                b.Account != null && p.ArAccounts.Contains(b.Account.Id) && p.Customers.Count > 0 ? "Posted invoice by invoice from the customers' list"
                : b.Account != null && p.ApAccounts.Contains(b.Account.Id) && p.Suppliers.Count > 0 ? "Posted bill by bill from the suppliers' list" : b.Account == null ? "New account" : null, b.SubType)).ToList();
            return new ConversionCheckDto(lines, p.Balances.Count(b => b.Account == null), p.Dr, p.Cr, p.Dr - p.Cr, p.ArTb, p.ArDetail, p.ApTb, p.ApDetail, p.NewCustomers, p.NewSuppliers,
                p.Problems, p.Warnings, p.Problems.Count == 0);
        }

        // ---- posting ---------------------------------------------------------------------------------------------------------------

        public async Task<ConversionResultDto> PostAsync(Guid clientId, ConversionRequest req, CancellationToken ct)
        {
            ConversionResultDto? result = null;
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
                var p = await PlanAsync(clientId, req, ct);
                if (p.Problems.Count > 0) throw GatewayException.BadRequest("CONVERSION_INVALID", p.Problems[0] + (p.Problems.Count > 1 ? $" (and {p.Problems.Count - 1} more)" : ""));

                // the accounts that are new
                var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
                var created = 0; var ids = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in p.Balances)
                {
                    if (b.Account != null) { ids[b.Row.Code] = b.Account.Id; continue; }
                    var a = new LedgerAccount { ClientId = clientId, Code = b.Row.Code, Name = b.Name, Type = b.Type, SubType = b.SubType };
                    _db.LedgerAccounts.Add(a); ids[b.Row.Code] = a.Id; created++;
                }
                var equity = await OpeningEquityAsync(clientId, rules, ct);
                if (created > 0) await _db.SaveChangesAsync(ct);

                // the trial balance, without receivables/payables when they come invoice by invoice; what is left over goes to opening balance equity
                string? number = null;
                var lines = new List<JournalLineInput>();
                foreach (var b in p.Balances)
                {
                    var id = ids[b.Row.Code];
                    if (p.Customers.Count > 0 && p.ArAccounts.Contains(id)) continue;
                    if (p.Suppliers.Count > 0 && p.ApAccounts.Contains(id)) continue;
                    lines.Add(new JournalLineInput(id, b.Row.Debit, b.Row.Credit, null));
                }
                var diff = lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);
                if (diff != 0) lines.Add(new JournalLineInput(equity.Id, diff < 0 ? -diff : 0, diff > 0 ? diff : 0, "Opening balance equity"));
                if (lines.Count(l => l.Debit != 0 || l.Credit != 0) >= 2)
                    number = (await _books.PostLinesAsync(clientId, p.Date, p.Currency, $"Opening balances at {p.Date:d MMM yyyy}", "OPENING", "opening", null, lines, ct)).Number;

                // customers' unpaid invoices: each its own receivable, so it can be paid and aged
                var receivable = await ChartCodes.ByRoleAsync(_db, clientId, "receivable", "1100", ct) ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The accounts receivable account is missing.");
                var customers = await _db.Customers.Where(c => c.ClientId == clientId).ToListAsync(ct);
                var newCustomers = 0;
                foreach (var row in p.Customers)
                {
                    var cust = customers.FirstOrDefault(c => string.Equals(c.Name, row.Name, StringComparison.OrdinalIgnoreCase));
                    if (cust == null)
                    {
                        cust = new Customer { ClientId = clientId, Name = row.Name, Email = row.Email, Tin = row.Tin };
                        _db.Customers.Add(cust); customers.Add(cust); newCustomers++;
                    }
                    var doc = new SalesInvoice
                    {
                        ClientId = clientId, Kind = "opening", Number = row.Number, CustomerId = cust.Id, CustomerName = cust.Name, CustomerTin = cust.Tin, CustomerEmail = cust.Email, CustomerAddress = cust.Address,
                        Date = row.Date.Date, DueDate = (row.DueDate ?? row.Date.AddDays(p.Terms)).Date, Currency = p.Currency, Subtotal = row.Amount, Total = row.Amount, Status = "Issued", IssuedAt = DateTime.UtcNow,
                        Notes = $"Brought forward at {p.Date:d MMM yyyy}", CreatedBy = _scope.UserName,
                    };
                    doc.Lines.Add(new SalesInvoiceLine { InvoiceId = doc.Id, ClientId = clientId, Sort = 1, Description = "Balance brought forward", Quantity = 1, UnitPrice = row.Amount, Amount = row.Amount, AccountId = equity.Id });
                    _db.SalesInvoices.Add(doc);
                    await _db.SaveChangesAsync(ct);
                    var entry = await _books.PostEntryAsync(clientId, p.Date, p.Currency, $"Brought forward: invoice {row.Number} - {cust.Name}", row.Number, "opening_ar", doc.Id,
                        new List<(Guid, decimal, decimal)> { (receivable.Id, row.Amount, 0m), (equity.Id, 0m, row.Amount) }, ct);
                    doc.JournalEntryId = entry.Id;
                }

                // suppliers' unpaid bills: each its own payable
                var payable = p.Suppliers.Count == 0 ? null : await ChartCodes.ByRoleAsync(_db, clientId, "payable", "2010", ct) ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The accounts payable account is missing.");
                var suppliers = await _db.Suppliers.Where(s => s.ClientId == clientId).ToListAsync(ct);
                var newSuppliers = 0;
                foreach (var row in p.Suppliers)
                {
                    var sup = suppliers.FirstOrDefault(s => string.Equals(s.Name, row.Name, StringComparison.OrdinalIgnoreCase));
                    if (sup == null)
                    {
                        sup = new Supplier { ClientId = clientId, Name = row.Name, Email = row.Email, Tin = row.Tin };
                        _db.Suppliers.Add(sup); suppliers.Add(sup); newSuppliers++;
                        await _db.SaveChangesAsync(ct);
                    }
                    var bill = new Bill { ClientId = clientId, SupplierId = sup.Id, Number = row.Number, Date = row.Date.Date, DueDate = (row.DueDate ?? row.Date.AddDays(sup.TermsDays)).Date, Currency = p.Currency,
                        Memo = $"Brought forward at {p.Date:d MMM yyyy}", Subtotal = row.Amount, Total = row.Amount, ExpenseAccountId = equity.Id, CreatedBy = _scope.UserName };
                    _db.Bills.Add(bill);
                    await _db.SaveChangesAsync(ct);
                    var entry = await _books.PostEntryAsync(clientId, p.Date, p.Currency, $"Brought forward: bill {row.Number} - {sup.Name}", row.Number, "opening_ap", bill.Id,
                        new List<(Guid, decimal, decimal)> { (equity.Id, row.Amount, 0m), (payable!.Id, 0m, row.Amount) }, ct);
                    bill.JournalEntryId = entry.Id;
                }
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.conversion", Detail = $"{p.Currency} at {p.Date:yyyy-MM-dd}: {p.Balances.Count} balances, {p.Customers.Count} customer invoices, {p.Suppliers.Count} supplier bills, {created} new accounts" });
                await _db.SaveChangesAsync(ct);
                if (tx != null) await tx.CommitAsync(ct);
                result = new ConversionResultDto(number, created, p.Customers.Count, p.Suppliers.Count, newCustomers, newSuppliers);
            });
            return result!;
        }

        /// <summary>The equity account opening balances are posted against (created the first time).</summary>
        internal async Task<LedgerAccount> OpeningEquityAsync(Guid clientId, ChartRules rules, CancellationToken ct)
        {
            var acc = await _db.LedgerAccounts.FirstOrDefaultAsync(a => a.ClientId == clientId && a.Role == "opening_balance", ct);
            if (acc != null) return acc;
            var codes = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).Select(a => a.Code).ToListAsync(ct);
            codes.AddRange(_db.ChangeTracker.Entries<LedgerAccount>().Where(e => e.State == EntityState.Added).Select(e => e.Entity.Code));
            var code = ChartCodes.Next("Equity", null, codes, rules).Code ?? "3090";
            acc = new LedgerAccount { ClientId = clientId, Code = code, Name = "Opening balance equity", Type = "Equity", Role = "opening_balance", SubType = "equity", IsSystem = true };
            _db.LedgerAccounts.Add(acc);
            await _db.SaveChangesAsync(ct);
            return acc;
        }
    }
}
