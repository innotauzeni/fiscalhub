using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>The VAT return worked out from the books (and its settlement), and budgets compared with what actually happened.</summary>
    public class VatBudgetTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 57000;
        public VatBudgetTests(AiFactory f) => _f = f;
        private static readonly DateTime Now = DateTime.UtcNow.AddHours(2);
        private static string Today => Now.ToString("yyyy-MM-dd");
        private static readonly string Wide = "from=2020-01-01&to=2035-01-01";

        private static async Task<Dictionary<string, Guid>> ChartAsync(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());
        private static decimal Tb(JsonElement tb, string code, string side) => tb.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("code").GetString() == code).Select(r => r.GetProperty(side).GetDecimal()).FirstOrDefault();
        private static Task<JsonElement> GetTb(HttpClient c) => c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/reports/trial-balance?currency=USD&asAt=2035-01-01", J);

        private static async Task<JsonElement> SaleAsync(HttpClient c, Guid dev, string no, decimal price, string type = "FiscalInvoice", Guid? original = null)
        {
            var res = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = no, invoiceNo = no, receiptType = type, currency = "USD", linesTaxInclusive = true, notes = type == "CreditNote" ? "returned" : null, originalReceiptId = original,
                lines = new[] { new { name = "Goods", hsCode = "85456852", quantity = 1, unitPrice = price, taxPercent = 15 } }, payments = new[] { new { moneyType = "Cash", amount = price } },
            });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await res.Json();
        }

        private static async Task BillAsync(HttpClient c, Dictionary<string, Guid> chart, string number, decimal net, decimal vat)
        {
            var sup = await (await c.PutAsJsonAsync("/api/v1/accounting/suppliers", new { id = (Guid?)null, name = "Supplier " + number, termsDays = 30, isActive = true })).Json();
            var res = await c.PostAsJsonAsync("/api/v1/accounting/bills", new { supplierId = sup.GetProperty("id").GetGuid(), number, date = Today, currency = "USD", subtotal = net, vat, expenseAccountId = chart["5160"] });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task The_vat_return_comes_from_the_books_and_settling_it_clears_the_vat_accounts()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Vat Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-VAT");
            var chart = await ChartAsync(co.Admin);
            await SaleAsync(co.Admin, dev, "V-1", 230m); var b = await SaleAsync(co.Admin, dev, "V-2", 115m);
            await SaleAsync(co.Admin, dev, "V-CN", 115m, "CreditNote", b.GetProperty("id").GetGuid());
            await BillAsync(co.Admin, chart, "VB-1", 100m, 15m);

            var r = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/vat/return?currency=USD&{Wide}", J);
            Assert.Equal(300m, r.GetProperty("taxableSales").GetDecimal()); Assert.Equal(100m, r.GetProperty("salesReturns").GetDecimal()); Assert.Equal(200m, r.GetProperty("netSales").GetDecimal());
            Assert.Equal(45m, r.GetProperty("outputVat").GetDecimal()); Assert.Equal(15m, r.GetProperty("creditNoteVat").GetDecimal()); Assert.Equal(30m, r.GetProperty("netOutputVat").GetDecimal());
            Assert.Equal(100m, r.GetProperty("purchases").GetDecimal()); Assert.Equal(15m, r.GetProperty("inputVat").GetDecimal()); Assert.Equal(15m, r.GetProperty("vatPayable").GetDecimal());
            Assert.Equal(1, r.GetProperty("bills").GetArrayLength()); Assert.Equal(15m, r.GetProperty("settlementDue").GetDecimal());

            // paying it needs an account; with one, both VAT accounts are cleared and the money leaves the account
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/vat/settle", new { date = Today, currency = "USD" })).StatusCode);
            var settle = await co.Admin.PostAsJsonAsync("/api/v1/accounting/vat/settle", new { date = Today, currency = "USD", accountId = chart["1010"] });
            Assert.True(settle.IsSuccessStatusCode, await settle.Content.ReadAsStringAsync());
            var tb = await GetTb(co.Admin);
            Assert.True(tb.GetProperty("balanced").GetBoolean());
            Assert.Equal(0m, Tb(tb, "2110", "credit") + Tb(tb, "2110", "debit")); Assert.Equal(0m, Tb(tb, "1310", "debit") + Tb(tb, "1310", "credit"));
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/vat/settle", new { date = Today, currency = "USD", accountId = chart["1010"] })).StatusCode);   // nothing left to settle

            // the return for the period still says what it said (the settlement pays it, it is not part of it); nothing is left to settle
            var again = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/vat/return?currency=USD&{Wide}", J);
            Assert.Equal(15m, again.GetProperty("vatPayable").GetDecimal()); Assert.Equal(0m, again.GetProperty("settlementDue").GetDecimal());

            // more input VAT than output: the settlement is a refund into the bank
            var refund = await TestKit.NewCompanyAsync(_f, "Vat Refund Co");
            var rc = await ChartAsync(refund.Admin);
            await BillAsync(refund.Admin, rc, "RB-1", 100m, 15m);
            var due = await refund.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/vat/return?currency=USD&{Wide}", J);
            Assert.Equal(-15m, due.GetProperty("vatPayable").GetDecimal());
            Assert.True((await refund.Admin.PostAsJsonAsync("/api/v1/accounting/vat/settle", new { date = Today, currency = "USD", accountId = rc["1010"] })).IsSuccessStatusCode);
            var rtb = await GetTb(refund.Admin);
            Assert.Equal(15m, Tb(rtb, "1010", "debit")); Assert.Equal(0m, Tb(rtb, "1310", "debit") + Tb(rtb, "1310", "credit"));

            // a viewer has no rights; other companies have their own books
            var email = TestKit.Unique("viewer") + "@vat.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Vera", surname = "Viewer", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var viewer = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/accounting/vat/return?currency=USD&{Wide}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.GetAsync($"/api/v1/accounting/vat/return?currency=US&{Wide}")).StatusCode);
        }

        private static object Plan(Guid account, decimal monthly) => new { accountId = account, months = Enumerable.Repeat(monthly, 12).ToArray() };

        [Fact]
        public async Task A_budget_is_compared_with_the_books_account_by_account_and_unbudgeted_spending_shows_up()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Budget Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-BG");
            var chart = await ChartAsync(co.Admin);
            await SaleAsync(co.Admin, dev, "BG-1", 115m);                                                               // 100 of sales before VAT, this month
            async Task Rent(decimal amount, string code, string memo) => Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/accounting/journals", new
            { date = Today, currency = "USD", memo, lines = new object[] { new { accountId = chart[code], debit = amount, credit = 0m }, new { accountId = chart["1010"], debit = 0m, credit = amount } } })).IsSuccessStatusCode);
            await Rent(130m, "5110", "Rent"); await Rent(20m, "5160", "Stationery");                                    // rent over budget, stationery not budgeted at all

            var saved = await co.Admin.PutAsJsonAsync("/api/v1/accounting/budgets", new { id = (Guid?)null, name = "Operating plan", year = Now.Year, currency = "USD", lines = new[] { Plan(chart["4010"], 500m), Plan(chart["5110"], 100m) } });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var budget = await saved.Json(); var id = budget.GetProperty("id").GetGuid();
            Assert.Equal(6000m, budget.GetProperty("totalIncome").GetDecimal()); Assert.Equal(1200m, budget.GetProperty("totalExpense").GetDecimal());

            var v = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/budgets/{id}/variance?fromMonth={Now.Month}&toMonth={Now.Month}", J);
            var income = v.GetProperty("sections")[0].GetProperty("rows")[0];
            Assert.Equal(500m, income.GetProperty("budget").GetDecimal()); Assert.Equal(100m, income.GetProperty("actual").GetDecimal()); Assert.Equal(-400m, income.GetProperty("variance").GetDecimal()); Assert.Equal(-80m, income.GetProperty("variancePct").GetDecimal());
            var expenses = v.GetProperty("sections")[1].GetProperty("rows").EnumerateArray().ToList();
            var rent = expenses.First(r => r.GetProperty("code").GetString() == "5110"); var stationery = expenses.First(r => r.GetProperty("code").GetString() == "5160");
            Assert.Equal(-30m, rent.GetProperty("variance").GetDecimal()); Assert.Equal(-30m, rent.GetProperty("variancePct").GetDecimal());               // over budget is unfavourable
            Assert.Equal(0m, stationery.GetProperty("budget").GetDecimal()); Assert.Equal(20m, stationery.GetProperty("actual").GetDecimal()); Assert.True(!stationery.TryGetProperty("variancePct", out var pct) || pct.ValueKind == JsonValueKind.Null);
            Assert.Equal(400m, v.GetProperty("budgetedProfit").GetDecimal()); Assert.Equal(-50m, v.GetProperty("actualProfit").GetDecimal()); Assert.Equal(-450m, v.GetProperty("profitVariance").GetDecimal());

            // editing replaces the plan; a month with no budget compares with zero
            Assert.True((await co.Admin.PutAsJsonAsync("/api/v1/accounting/budgets", new { id, name = "Operating plan", year = Now.Year, currency = "USD", lines = new[] { Plan(chart["4010"], 50m) } })).IsSuccessStatusCode);
            var v2 = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/budgets/{id}/variance?fromMonth=1&toMonth=12", J);
            Assert.Equal(600m, v2.GetProperty("sections")[0].GetProperty("budget").GetDecimal());

            // refused: an asset account, eleven months, a repeated account, a negative amount, the same name twice, a bad range
            object Bad(object line) => new { id = (Guid?)null, name = "Other", year = Now.Year, currency = "USD", lines = new[] { line } };
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/budgets", Bad(Plan(chart["1010"], 10m)))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/budgets", Bad(new { accountId = chart["4010"], months = new decimal[11] }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/budgets", Bad(Plan(chart["4010"], -5m)))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/budgets", new { id = (Guid?)null, name = "Dup account", year = Now.Year, currency = "USD", lines = new[] { Plan(chart["4010"], 1m), Plan(chart["4010"], 2m) } })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/budgets", new { id = (Guid?)null, name = "Operating plan", year = Now.Year, currency = "USD", lines = new[] { Plan(chart["4010"], 1m) } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.GetAsync($"/api/v1/accounting/budgets/{id}/variance?fromMonth=5&toMonth=2")).StatusCode);

            // another company cannot see, compare or delete it
            var other = await TestKit.NewCompanyAsync(_f, "Budget Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/budgets", J)).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/accounting/budgets/{id}/variance")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.DeleteAsync($"/api/v1/accounting/budgets/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/accounting/budgets/{id}")).StatusCode);
            Assert.Empty((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/budgets", J)).EnumerateArray());
        }
    }
}
