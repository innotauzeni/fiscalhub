using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Infrastructure.Services;

namespace Fdms.Tests
{
    /// <summary>The books: sales posting from fiscal receipts, journals, the reports, the cashbook and bank reconciliation.</summary>
    public class AccountingTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 54000;
        public AccountingTests(AiFactory f) => _f = f;

        private static readonly string Wide = "from=2020-01-01&to=2035-01-01";

        private static async Task<Dictionary<string, Guid>> ChartAsync(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());

        private async Task<(TestKit.Company Co, Guid Dev)> CompanyAsync(string name)
        {
            var co = await TestKit.NewCompanyAsync(_f, name);
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-ACC");
            return (co, dev);
        }

        private static async Task<JsonElement> InvoiceAsync(HttpClient c, Guid dev, string no, string item, int qty, decimal price, string method = "Cash", string type = "FiscalInvoice", Guid? original = null)
        {
            var res = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = no, invoiceNo = no, receiptType = type, currency = "USD", linesTaxInclusive = true, notes = type == "CreditNote" ? "returned" : null, originalReceiptId = original,
                lines = new[] { new { name = item, hsCode = "85456852", quantity = qty, unitPrice = price, taxPercent = 15 } },
                payments = new[] { new { moneyType = method, amount = qty * price } },
            });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await res.Json();
        }

        private static decimal Row(JsonElement tb, string code, string side) => tb.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("code").GetString() == code).Select(r => r.GetProperty(side).GetDecimal()).FirstOrDefault();

        [Fact]
        public async Task Fiscal_receipts_post_themselves_and_the_reports_agree_with_each_other()
        {
            var (co, dev) = await CompanyAsync("Books Co");
            var first = await InvoiceAsync(co.Admin, dev, "BK-1", "Shoes", 1, 115);                 // cash
            await InvoiceAsync(co.Admin, dev, "BK-2", "Hat", 2, 115, "Card");                       // card
            await InvoiceAsync(co.Admin, dev, "BK-3", "Shoes", 1, 115, "Cash", "CreditNote", first.GetProperty("id").GetGuid());
            var chart = await ChartAsync(co.Admin);
            Assert.True(chart.ContainsKey("4010") && chart.ContainsKey("2110") && chart.ContainsKey("1010"));

            var journals = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?" + Wide, J);
            Assert.Equal(3, journals.GetArrayLength());
            foreach (var j in journals.EnumerateArray())
            {
                var lines = j.GetProperty("lines").EnumerateArray().ToList();
                Assert.Equal(lines.Sum(l => l.GetProperty("debit").GetDecimal()), lines.Sum(l => l.GetProperty("credit").GetDecimal()));   // every entry balances
                Assert.Equal("sales", j.GetProperty("source").GetString());
            }
            Assert.Equal(0, (await (await co.Admin.PostAsync("/api/v1/accounting/sync", null)).Json()).GetProperty("posted").GetInt32());   // nothing is posted twice
            Assert.Equal(3, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?" + Wide, J)).GetArrayLength());

            // trial balance: sales 300, returns 100, VAT 30 (45 collected less 15 credited), card 230, cash back to nothing
            var tb = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={DateTime.UtcNow.AddHours(2).Year}-12-31", J);
            Assert.True(tb.GetProperty("balanced").GetBoolean());
            Assert.Equal(300m, Row(tb, "4010", "credit")); Assert.Equal(100m, Row(tb, "4020", "debit")); Assert.Equal(30m, Row(tb, "2110", "credit")); Assert.Equal(230m, Row(tb, "1040", "debit"));
            Assert.Equal(0m, Row(tb, "1010", "debit") + Row(tb, "1010", "credit"));

            var pl = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss?currency=USD&{Wide}", J);
            Assert.Equal(200m, pl.GetProperty("netProfit").GetDecimal());
            var bs = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/reports/balance-sheet?currency=USD&asAt=2035-01-01", J);
            Assert.True(bs.GetProperty("balanced").GetBoolean()); Assert.Equal(230m, bs.GetProperty("totalAssets").GetDecimal());

            // a manual expense: unbalanced and one-sided entries are refused, a good one lands in the reports, and a reversal puts it back
            object Entry(object[] lines) => new { date = "2026-10-05", currency = "USD", memo = "October rent", reference = "RENT-10", lines };
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/journals", Entry(new object[] { new { accountId = chart["5110"], debit = 50m, credit = 0m }, new { accountId = chart["1010"], debit = 0m, credit = 40m } }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/journals", Entry(new object[] { new { accountId = chart["5110"], debit = 50m, credit = 50m }, new { accountId = chart["1010"], debit = 0m, credit = 0m } }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/journals", Entry(new object[] { new { accountId = Guid.NewGuid(), debit = 50m, credit = 0m }, new { accountId = chart["1010"], debit = 0m, credit = 50m } }))).StatusCode);
            var rent = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/journals", Entry(new object[] { new { accountId = chart["5110"], debit = 50m, credit = 0m }, new { accountId = chart["1010"], debit = 0m, credit = 50m } }))).Json();
            Assert.StartsWith("JV-", rent.GetProperty("number").GetString());
            Assert.Equal(150m, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss?currency=USD&{Wide}", J)).GetProperty("netProfit").GetDecimal());

            // the cashbook of the cash account: the sale in, the refund and the rent out, with a running balance
            var cash = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/cashbook?accountId={chart["1010"]}&currency=USD&{Wide}", J);
            Assert.Equal(115m, cash.GetProperty("totalReceipts").GetDecimal()); Assert.Equal(165m, cash.GetProperty("totalPayments").GetDecimal()); Assert.Equal(-50m, cash.GetProperty("closing").GetDecimal());
            var ledger = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/ledger?accountId={chart["5110"]}&currency=USD&{Wide}", J);
            Assert.Equal(50m, ledger.GetProperty("closing").GetDecimal());

            var rentId = rent.GetProperty("id").GetGuid();
            var reversal = await (await co.Admin.PostAsync($"/api/v1/accounting/journals/{rentId}/reverse", null)).Json();
            Assert.True(reversal.GetProperty("isReversal").GetBoolean());
            Assert.Equal(200m, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss?currency=USD&{Wide}", J)).GetProperty("netProfit").GetDecimal());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/accounting/journals/{rentId}/reverse", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/accounting/journals/{reversal.GetProperty("id").GetGuid()}/reverse", null)).StatusCode);

            // chart of accounts: add one, a used or standard account cannot be deleted, codes are unique
            var added = await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/accounts", new { id = (Guid?)null, code = "5125", name = "Water", type = "Expense", isActive = true })).Json();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/accounts", new { id = (Guid?)null, code = "5125", name = "Again", type = "Expense", isActive = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/accounting/accounts/{chart["4010"]}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/accounting/accounts/{added.GetProperty("id").GetGuid()}")).StatusCode);
        }

        [Fact]
        public async Task Books_belong_to_one_company_and_need_the_right()
        {
            var (a, dev) = await CompanyAsync("Books A");
            await InvoiceAsync(a.Admin, dev, "ISO-1", "Shoes", 1, 115);
            var b = await TestKit.NewCompanyAsync(_f, "Books B");
            Assert.Empty((await b.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?" + Wide, J)).EnumerateArray());
            var chartA = await ChartAsync(a.Admin);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/v1/accounting/reports/ledger?accountId={chartA["4010"]}&currency=USD&{Wide}")).StatusCode);
            var journalA = (await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?" + Wide, J))[0].GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/v1/accounting/journals/{journalA}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsync($"/api/v1/accounting/journals/{journalA}/reverse", null)).StatusCode);

            // a viewer and an API token have no books; the platform operator keeps the platform's own books, never a company's
            var email = TestKit.Unique("viewer") + "@books.test";
            Assert.True((await a.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Vera", surname = "Viewer", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var viewer = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/accounting/accounts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/v1/accounting/sync", null)).StatusCode);
            var token = await TestKit.NewTokenAsync(_f, a.Admin, dev);
            Assert.Equal(HttpStatusCode.Forbidden, (await token.Http.GetAsync("/api/v1/accounting/accounts")).StatusCode);
            var op = await TestKit.AdminAsync(_f);
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/v1/accounting/accounts")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/v1/accounting/journals/{journalA}")).StatusCode);
        }

        [Fact]
        public async Task A_bank_statement_is_imported_matched_and_what_the_books_missed_becomes_entries()
        {
            var (co, dev) = await CompanyAsync("Recon Co");
            var bank = await (await co.Admin.PutAsJsonAsync("/api/v1/banks", new { name = "NMB", isActive = true })).Json();
            await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", new { bankId = bank.GetProperty("id").GetGuid(), accountName = "Recon Co", accountNumber = "40123456789", currency = "USD", isDefault = true, isActive = true });
            var accounts = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToList();
            var bankLedger = accounts.First(x => x.TryGetProperty("bankAccountId", out var ba) && ba.ValueKind == JsonValueKind.String);   // the bank account got its own ledger account
            var bankId = bankLedger.GetProperty("id").GetGuid();
            var chart = await ChartAsync(co.Admin);

            // the books: a deposit in, and a payment out
            async Task Cashbook(string kind, decimal amount, string text, string date) =>
                Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/accounting/cashbook-entry", new { date, accountId = bankId, kind, counterAccountId = kind == "receipt" ? chart["4090"] : chart["5160"], amount, currency = "USD", description = text, reference = "R" })).IsSuccessStatusCode);
            await Cashbook("receipt", 100m, "Customer deposit", "2026-10-01"); await Cashbook("payment", 20m, "Stationery", "2026-10-04");

            // the bank's statement: the same two, plus a fee the books do not know about
            var csv = "Date,Description,Amount\n2026-10-01,DEPOSIT CUSTOMER,100.00\n2026-10-03,SERVICE FEE,-5.00\n2026-10-05,CHEQUE 0012,-20.00";
            var import = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/bank/statement/import", new { accountId = bankId, csv })).Json();
            Assert.Equal(3, import.GetProperty("imported").GetInt32());
            var again = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/bank/statement/import", new { accountId = bankId, csv })).Json();   // the same statement twice adds nothing
            Assert.Equal(0, again.GetProperty("imported").GetInt32()); Assert.Equal(3, again.GetProperty("duplicates").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/bank/statement/import", new { accountId = bankId, csv = "nothing useful here" })).StatusCode);

            var rec = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/bank/reconciliation?accountId={bankId}&asAt=2026-10-31&statementBalance=75", J);
            Assert.Equal(80m, rec.GetProperty("ledgerBalance").GetDecimal());                 // 100 in, 20 out
            Assert.Equal(2, rec.GetProperty("suggestions").GetArrayLength());                 // the deposit and the cheque match by amount and date
            Assert.Equal(75m, rec.GetProperty("unmatchedStatement").GetDecimal());           // nothing is matched yet: 100 - 5 - 20

            Assert.Equal(2, (await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/bank/auto-match", new { accountId = bankId })).Json()).GetProperty("matched").GetInt32());
            Assert.Equal(-5m, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/bank/reconciliation?accountId={bankId}&asAt=2026-10-31", J)).GetProperty("unmatchedStatement").GetDecimal());   // only the fee is left, and it is only on the statement
            var feeLine = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/bank/reconciliation?accountId={bankId}&asAt=2026-10-31", J)).GetProperty("statementLines").EnumerateArray().First(l => !l.GetProperty("matched").GetBoolean());
            Assert.Equal(-5m, feeLine.GetProperty("amount").GetDecimal());
            var feeId = feeLine.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/bank/match", new { statementLineId = feeId, journalLineId = Guid.NewGuid() })).StatusCode);   // there is no such ledger line

            // the fee becomes a bank-charges entry that matches it, and the books and the bank agree
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/accounting/bank/create-entry", new { statementLineId = feeId, counterAccountId = chart["5150"], memo = "Monthly service fee" })).IsSuccessStatusCode);
            var done = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/bank/reconciliation?accountId={bankId}&asAt=2026-10-31&statementBalance=75", J);
            Assert.Equal(75m, done.GetProperty("ledgerBalance").GetDecimal()); Assert.Equal(0m, done.GetProperty("difference").GetDecimal());
            Assert.All(done.GetProperty("statementLines").EnumerateArray(), l => Assert.True(l.GetProperty("matched").GetBoolean()));
            Assert.Equal(5m, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/ledger?accountId={chart["5150"]}&currency=USD&{Wide}", J)).GetProperty("closing").GetDecimal());

            // a wrong statement balance shows the gap; matched lines cannot be deleted until un-matched
            Assert.Equal(7m, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/bank/reconciliation?accountId={bankId}&asAt=2026-10-31&statementBalance=82", J)).GetProperty("difference").GetDecimal());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/accounting/bank/statement/{feeId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.PostAsync($"/api/v1/accounting/bank/unmatch/{feeId}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/accounting/bank/statement/{feeId}")).StatusCode);

            // other layouts: day-first dates and separate debit and credit columns
            var csv2 = "Date,Details,Debit,Credit\n06/10/2026,Rent,300.00,\n07/10/2026,Transfer in,,\"1,200.00\"";
            var r2 = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/bank/statement/import", new { accountId = bankId, csv = csv2 })).Json();
            Assert.Equal(2, r2.GetProperty("imported").GetInt32());
            var lines2 = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/bank/reconciliation?accountId={bankId}&asAt=2026-10-31", J)).GetProperty("statementLines").EnumerateArray().ToList();
            Assert.Contains(lines2, l => l.GetProperty("amount").GetDecimal() == -300m); Assert.Contains(lines2, l => l.GetProperty("amount").GetDecimal() == 1200m);
        }

        [Fact]
        public void Statement_dates_and_amounts_are_read_the_way_banks_write_them()
        {
            Assert.True(BankReconService.TryDate("05/10/2026", out var d)); Assert.Equal(new DateTime(2026, 10, 5), d);
            Assert.True(BankReconService.TryDate("2026-10-05", out d)); Assert.True(BankReconService.TryDate("5 Oct 2026", out d)); Assert.False(BankReconService.TryDate("yesterday", out _));
            Assert.True(BankReconService.TryAmount("1,234.50", out var v)); Assert.Equal(1234.50m, v);
            Assert.True(BankReconService.TryAmount("(45.00)", out v)); Assert.Equal(-45m, v);
            Assert.True(BankReconService.TryAmount("12.00 DR", out v)); Assert.Equal(-12m, v);
            Assert.True(BankReconService.TryAmount("USD -7.5", out v)); Assert.Equal(-7.5m, v);
            Assert.False(BankReconService.TryAmount("", out _)); Assert.False(BankReconService.TryAmount("abc", out _));
        }
    }
}
