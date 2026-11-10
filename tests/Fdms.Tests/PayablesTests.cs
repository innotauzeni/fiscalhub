using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>Suppliers, bills and payments posting to the books, the payables ageing, and closing a period.</summary>
    public class PayablesTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 55000;
        public PayablesTests(AiFactory f) => _f = f;
        private static string Day(int ago) => DateTime.UtcNow.AddHours(2).Date.AddDays(-ago).ToString("yyyy-MM-dd");

        private static async Task<Dictionary<string, Guid>> ChartAsync(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());

        private static decimal Tb(JsonElement tb, string code, string side) => tb.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("code").GetString() == code).Select(r => r.GetProperty(side).GetDecimal()).FirstOrDefault();
        private static Task<JsonElement> GetTb(HttpClient c, string asAt = "2035-01-01") => c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={asAt}", J);

        private static async Task<JsonElement> SupplierAsync(HttpClient c, string name, int terms = 30)
        {
            var res = await c.PutAsJsonAsync("/api/v1/accounting/suppliers", new { id = (Guid?)null, name, tin = "2001112223", email = "accounts@" + name.Replace(" ", "").ToLowerInvariant() + ".test", termsDays = terms, isActive = true });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await res.Json();
        }

        private static Task<HttpResponseMessage> BillAsync(HttpClient c, Guid supplier, Guid expense, string number, string date, decimal subtotal, decimal vat) =>
            c.PostAsJsonAsync("/api/v1/accounting/bills", new { supplierId = supplier, number, date, currency = "USD", memo = "Printing", subtotal, vat, expenseAccountId = expense });

        [Fact]
        public async Task A_bill_and_its_payments_post_to_the_books_and_show_in_the_ageing()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Payables Co");
            var chart = await ChartAsync(co.Admin);
            var sup = await SupplierAsync(co.Admin, "Print Shop");
            var supId = sup.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/suppliers", new { id = (Guid?)null, name = "Print Shop", termsDays = 30, isActive = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/suppliers", new { id = (Guid?)null, name = "X", termsDays = 30, isActive = true })).StatusCode);

            // a 45-day-old bill on 30-day terms is 15 days overdue; 100 plus 15 VAT
            var bill = await (await BillAsync(co.Admin, supId, chart["5160"], "PS-1001", Day(45), 100m, 15m)).Json();
            var billId = bill.GetProperty("id").GetGuid();
            Assert.Equal(115m, bill.GetProperty("total").GetDecimal()); Assert.Equal("Unpaid", bill.GetProperty("status").GetString()); Assert.Equal(15, bill.GetProperty("daysOverdue").GetInt32());
            Assert.StartsWith("JV-", bill.GetProperty("journalNumber").GetString());
            var tb = await GetTb(co.Admin, Day(45));   // the expense is in the financial year of the bill
            Assert.True(tb.GetProperty("balanced").GetBoolean());
            Assert.Equal(115m, Tb(tb, "2010", "credit")); Assert.Equal(15m, Tb(tb, "1310", "debit")); Assert.Equal(100m, Tb(tb, "5160", "debit"));

            // refused: the same supplier invoice number twice, a bad amount, an income account, an unknown supplier
            Assert.Equal(HttpStatusCode.Conflict, (await BillAsync(co.Admin, supId, chart["5160"], "PS-1001", Day(1), 10m, 0m)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await BillAsync(co.Admin, supId, chart["5160"], "PS-1002", Day(1), 10m, 50m)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await BillAsync(co.Admin, supId, chart["4010"], "PS-1003", Day(1), 10m, 0m)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await BillAsync(co.Admin, Guid.NewGuid(), chart["5160"], "PS-1004", Day(1), 10m, 0m)).StatusCode);
            var later = await (await BillAsync(co.Admin, supId, chart["5110"], "PS-1005", Day(2), 200m, 0m)).Json();   // due in 28 days: not yet due

            // part payment from the cash account
            var pay = await co.Admin.PostAsJsonAsync($"/api/v1/accounting/bills/{billId}/pay", new { date = Day(0), amount = 50m, accountId = chart["1010"], reference = "CASH-1" });
            Assert.True(pay.IsSuccessStatusCode, await pay.Content.ReadAsStringAsync());
            var paid = await pay.Json();
            Assert.Equal("Part paid", paid.GetProperty("status").GetString()); Assert.Equal(65m, paid.GetProperty("outstanding").GetDecimal());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync($"/api/v1/accounting/bills/{billId}/pay", new { date = Day(0), amount = 66m, accountId = chart["1010"] })).StatusCode);   // more than is owing
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"/api/v1/accounting/bills/{billId}/pay", new { date = Day(0), amount = 5m, accountId = chart["5110"] })).StatusCode);       // not from an expense account
            tb = await GetTb(co.Admin);
            Assert.Equal(265m, Tb(tb, "2010", "credit")); Assert.Equal(50m, Tb(tb, "1010", "credit")); Assert.True(tb.GetProperty("balanced").GetBoolean());   // 115 - 50 + 200

            // the ageing: 65 is 15 days late, 200 is not due yet
            var ageing = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/reports/payables?currency=USD", J);
            var row = ageing.GetProperty("rows")[0];
            Assert.Equal("Print Shop", row.GetProperty("supplier").GetString()); Assert.Equal(65m, row.GetProperty("days1To30").GetDecimal()); Assert.Equal(200m, row.GetProperty("notDue").GetDecimal());
            Assert.Equal(265m, ageing.GetProperty("totals").GetProperty("total").GetDecimal());
            Assert.Equal(1, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/bills?status=overdue", J)).GetArrayLength());
            Assert.Equal(265m, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/suppliers", J))[0].GetProperty("outstanding")[0].GetProperty("amount").GetDecimal());

            // the profit and loss and the balance sheet include it without any extra step
            var pl = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/reports/profit-loss?currency=USD&from=2020-01-01&to=2035-01-01", J);
            Assert.Equal(-300m, pl.GetProperty("netProfit").GetDecimal());
            Assert.True((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/reports/balance-sheet?currency=USD&asAt=2035-01-01", J)).GetProperty("balanced").GetBoolean());

            // a bill with a payment cannot be voided until the payment is; then the books go back
            var payId = paid.GetProperty("payments")[0].GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/accounting/bills/{billId}/void", null)).StatusCode);
            Assert.Equal("Unpaid", (await (await co.Admin.PostAsync($"/api/v1/accounting/bills/payments/{payId}/void", null)).Json()).GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/accounting/bills/payments/{payId}/void", null)).StatusCode);
            Assert.Equal("Void", (await (await co.Admin.PostAsync($"/api/v1/accounting/bills/{billId}/void", null)).Json()).GetProperty("status").GetString());
            tb = await GetTb(co.Admin);
            Assert.Equal(200m, Tb(tb, "2010", "credit") - Tb(tb, "2010", "debit")); Assert.Equal(0m, Tb(tb, "1310", "debit") + Tb(tb, "1310", "credit"));
            Assert.Equal(0m, later.GetProperty("paid").GetDecimal());
        }

        [Fact]
        public async Task A_closed_period_takes_no_new_entries_and_late_sales_land_on_the_first_open_day()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Lock Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-LK");
            Assert.True((await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = "LK-1", invoiceNo = "LK-1", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } }, payments = new[] { new { moneyType = "Cash", amount = 115 } },
            })).IsSuccessStatusCode);
            var chart = await ChartAsync(co.Admin);   // opens the books; the sale is not posted until the first journal list or report
            var open = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/period", J);
            Assert.True(!open.TryGetProperty("lockedUntil", out var none) || none.ValueKind == JsonValueKind.Null);   // nothing is closed to begin with

            // closing up to today: nothing can be posted on or before it, a future date cannot be closed
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/period", new { lockedUntil = Day(-5) })).StatusCode);
            Assert.Equal(Day(0), (await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/period", new { lockedUntil = Day(0) })).Json()).GetProperty("lockedUntil").GetString()![..10]);
            object Rent(string date) => new { date, currency = "USD", memo = "Rent", lines = new object[] { new { accountId = chart["5110"], debit = 10m, credit = 0m }, new { accountId = chart["1010"], debit = 0m, credit = 10m } } };
            var locked = await co.Admin.PostAsJsonAsync("/api/v1/accounting/journals", Rent(Day(3)));
            Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode); Assert.Contains("PERIOD_LOCKED", await locked.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/journals", Rent(Day(0)))).StatusCode);   // closed through today
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/cashbook-entry", new { date = Day(2), accountId = chart["1010"], kind = "receipt", counterAccountId = chart["4090"], amount = 5m, currency = "USD", description = "Late" })).StatusCode);
            var sup = await SupplierAsync(co.Admin, "Locked Supplies");
            Assert.Equal(HttpStatusCode.Conflict, (await BillAsync(co.Admin, sup.GetProperty("id").GetGuid(), chart["5160"], "LK-B1", Day(4), 10m, 0m)).StatusCode);

            // today's sale was confirmed inside the closed period: it is posted on the first open day, and says so
            var journals = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?from=2020-01-01&to=2035-01-01", J);
            var sale = journals.EnumerateArray().First(j => j.GetProperty("source").GetString() == "sales");
            Assert.Equal(DateTime.UtcNow.AddHours(2).Date.AddDays(1).ToString("yyyy-MM-dd"), sale.GetProperty("date").GetString()![..10]);
            Assert.Contains("(dated", sale.GetProperty("memo").GetString());
            // a reversal is dated on the original's day or later, so it lands on the first open day and never inside the closed period
            var reversal = await (await co.Admin.PostAsync($"/api/v1/accounting/journals/{sale.GetProperty("id").GetGuid()}/reverse", null)).Json();
            Assert.True(reversal.GetProperty("date").GetString()![..10].CompareTo(Day(0)) > 0);
        }

        [Fact]
        public async Task Reopening_a_period_works_only_for_those_who_may_and_payables_stay_inside_their_company()
        {
            var a = await TestKit.NewCompanyAsync(_f, "Pay A");
            var chart = await ChartAsync(a.Admin);
            var sup = await SupplierAsync(a.Admin, "Shared Name");
            var bill = await (await BillAsync(a.Admin, sup.GetProperty("id").GetGuid(), chart["5160"], "ISO-1", Day(1), 50m, 0m)).Json();
            var b = await TestKit.NewCompanyAsync(_f, "Pay B");
            Assert.Empty((await b.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/suppliers", J)).EnumerateArray());
            Assert.Empty((await b.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/bills", J)).EnumerateArray());
            var chartB = await ChartAsync(b.Admin);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsJsonAsync($"/api/v1/accounting/bills/{bill.GetProperty("id").GetGuid()}/pay", new { date = Day(0), amount = 10m, accountId = chartB["1010"] })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsync($"/api/v1/accounting/bills/{bill.GetProperty("id").GetGuid()}/void", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await BillAsync(b.Admin, sup.GetProperty("id").GetGuid(), chartB["5160"], "X-1", Day(1), 10m, 0m)).StatusCode);   // another company's supplier

            var email = TestKit.Unique("viewer") + "@pay.test";
            Assert.True((await a.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Vera", surname = "Viewer", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var viewer = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/v1/accounting/period", new { lockedUntil = Day(1) })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/accounting/bills")).StatusCode);

            // close, then reopen: an entry that was refused is accepted
            Assert.True((await a.Admin.PutAsJsonAsync("/api/v1/accounting/period", new { lockedUntil = Day(1) })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await BillAsync(a.Admin, sup.GetProperty("id").GetGuid(), chart["5160"], "ISO-2", Day(1), 10m, 0m)).StatusCode);
            Assert.True((await a.Admin.PutAsJsonAsync("/api/v1/accounting/period", new { lockedUntil = (string?)null })).IsSuccessStatusCode);
            Assert.True((await BillAsync(a.Admin, sup.GetProperty("id").GetGuid(), chart["5160"], "ISO-2", Day(1), 10m, 0m)).IsSuccessStatusCode);
        }

        [Fact]
        public async Task A_supplier_statement_lists_bills_and_payments_with_a_running_balance()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Statement Co");
            var chart = await ChartAsync(co.Admin);
            var supId = (await SupplierAsync(co.Admin, "Paper Mill")).GetProperty("id").GetGuid();
            var b1 = await (await BillAsync(co.Admin, supId, chart["5160"], "PM-1", Day(40), 100m, 15m)).Json();   // 115, before the period
            await BillAsync(co.Admin, supId, chart["5110"], "PM-2", Day(5), 200m, 0m);
            await co.Admin.PostAsJsonAsync($"/api/v1/accounting/bills/{b1.GetProperty("id").GetGuid()}/pay", new { date = Day(2), amount = 50m, accountId = chart["1010"], reference = "CASH-9" });

            var all = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/suppliers/statement?supplierId={supId}&currency=USD&from={Day(60)}&to={Day(0)}", J);
            Assert.Equal(0m, all.GetProperty("opening").GetDecimal());
            var lines = all.GetProperty("lines").EnumerateArray().ToList();
            Assert.Equal(new[] { 115m, 315m, 265m }, lines.Select(l => l.GetProperty("balance").GetDecimal()).ToArray());
            Assert.Equal(265m, all.GetProperty("closing").GetDecimal());
            Assert.Equal("payment", lines[2].GetProperty("kind").GetString());

            // a later window starts from the balance brought forward
            var later = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/suppliers/statement?supplierId={supId}&currency=USD&from={Day(10)}&to={Day(0)}", J);
            Assert.Equal(115m, later.GetProperty("opening").GetDecimal()); Assert.Equal(265m, later.GetProperty("closing").GetDecimal());
            Assert.Equal(2, later.GetProperty("lines").GetArrayLength());

            // another currency is empty, an unknown supplier and a bad range are refused
            Assert.Equal(0m, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/suppliers/statement?supplierId={supId}&currency=ZWG", J)).GetProperty("closing").GetDecimal());
            Assert.Equal(HttpStatusCode.NotFound, (await co.Admin.GetAsync($"/api/v1/accounting/suppliers/statement?supplierId={Guid.NewGuid()}&currency=USD")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.GetAsync($"/api/v1/accounting/suppliers/statement?supplierId={supId}&currency=USD&from={Day(0)}&to={Day(5)}")).StatusCode);
            // another company cannot see it
            var other = await TestKit.NewCompanyAsync(_f, "Other Statement Co");
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/accounting/suppliers/statement?supplierId={supId}&currency=USD")).StatusCode);
        }
    }
}
