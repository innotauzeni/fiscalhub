using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>What customers owe (open invoices, payments, credit notes, ageing, statements) and fixed assets with depreciation and disposal.</summary>
    public class ReceivablesAssetsTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 56000;
        public ReceivablesAssetsTests(AiFactory f) => _f = f;

        private static async Task<Dictionary<string, Guid>> ChartAsync(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());
        private static decimal Tb(JsonElement tb, string code, string side) => tb.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("code").GetString() == code).Select(r => r.GetProperty(side).GetDecimal()).FirstOrDefault();
        private static Task<JsonElement> GetTb(HttpClient c, string asAt = "2035-01-01") => c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={asAt}", J);

        private static async Task<JsonElement> SaleAsync(HttpClient c, Guid dev, string no, decimal price, string method, string type = "FiscalInvoice", Guid? original = null, bool buyer = true)
        {
            var res = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = no, invoiceNo = no, receiptType = type, currency = "USD", linesTaxInclusive = true, notes = type == "CreditNote" ? "returned" : null, originalReceiptId = original,
                lines = new[] { new { name = "Goods", hsCode = "85456852", quantity = 1, unitPrice = price, taxPercent = 15 } }, payments = new[] { new { moneyType = method, amount = price } },
                buyer = buyer ? new { registerName = "Moyo Retailers", tin = "2001234567" } : null,
            });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await res.Json();
        }

        [Fact]
        public async Task Invoices_left_on_account_are_followed_until_they_are_paid_or_credited()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Debtors Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-AR");
            var chart = await ChartAsync(co.Admin);
            var a = await SaleAsync(co.Admin, dev, "AR-1", 230m, "Credit"); var b = await SaleAsync(co.Admin, dev, "AR-2", 115m, "Credit"); await SaleAsync(co.Admin, dev, "AR-3", 50m, "Cash");
            var aId = a.GetProperty("id").GetGuid(); var bId = b.GetProperty("id").GetGuid();

            var open = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/receivables", J);
            Assert.Equal(2, open.GetArrayLength());                                                                   // the cash sale is not owed
            Assert.All(open.EnumerateArray(), i => Assert.Equal("Moyo Retailers", i.GetProperty("customer").GetString()));
            Assert.Equal(345m, open.EnumerateArray().Sum(i => i.GetProperty("outstanding").GetDecimal()));

            // a part payment, an overpayment (refused), a payment into an account that cannot take it (refused)
            var received = await co.Admin.PostAsJsonAsync($"/api/v1/accounting/receivables/{aId}/receive", new { date = DateTime.UtcNow.AddHours(2).ToString("yyyy-MM-dd"), amount = 100m, accountId = chart["1010"], reference = "RCPT-1" });
            Assert.True(received.IsSuccessStatusCode, await received.Content.ReadAsStringAsync());
            var after = await received.Json();
            Assert.Equal(130m, after.GetProperty("outstanding").GetDecimal()); Assert.Equal(100m, after.GetProperty("received").GetDecimal());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync($"/api/v1/accounting/receivables/{aId}/receive", new { date = "2026-10-01", amount = 131m, accountId = chart["1010"] })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"/api/v1/accounting/receivables/{aId}/receive", new { date = "2026-10-01", amount = 5m, accountId = chart["4010"] })).StatusCode);

            // a credit note on the second invoice settles it in full
            await SaleAsync(co.Admin, dev, "AR-CN", 115m, "Credit", "CreditNote", bId, buyer: false);
            var left = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/receivables", J);
            Assert.Equal(1, left.GetArrayLength()); Assert.Equal("AR-1", left[0].GetProperty("invoiceNo").GetString());
            var tb = await GetTb(co.Admin);
            Assert.True(tb.GetProperty("balanced").GetBoolean()); Assert.Equal(130m, Tb(tb, "1100", "debit"));         // the books agree with the open items

            // ageing: not yet due today; 45 days on (30-day terms) it is 15 days late
            Assert.Equal(130m, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/reports/receivables?currency=USD", J)).GetProperty("totals").GetProperty("notDue").GetDecimal());
            var later = DateTime.UtcNow.AddHours(2).AddDays(45).ToString("yyyy-MM-dd");
            Assert.Equal(130m, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/receivables?currency=USD&asAt={later}", J)).GetProperty("totals").GetProperty("days1To30").GetDecimal());
            Assert.Equal(0, (await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/receivables/settings", new { termsDays = 0 })).Json()).GetProperty("termsDays").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/receivables/settings", new { termsDays = 999 })).StatusCode);
            await co.Admin.PutAsJsonAsync("/api/v1/accounting/receivables/settings", new { termsDays = 30 });

            // the customer's statement: two invoices, the credit note and the payment, closing at what is owed
            var key = open[0].GetProperty("customerKey").GetString()!;
            var st = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/customers/statement?customer={Uri.EscapeDataString(key)}&currency=USD&from=2020-01-01&to=2035-01-01", J);
            Assert.Equal(new[] { "invoice", "invoice", "credit", "payment" }, st.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("kind").GetString()).ToArray());
            Assert.Equal(130m, st.GetProperty("closing").GetDecimal());

            // voiding the payment puts the debt back
            var payId = after.GetProperty("payments")[0].GetProperty("id").GetGuid();
            Assert.Equal(230m, (await (await co.Admin.PostAsync($"/api/v1/accounting/receivables/payments/{payId}/void", null)).Json()).GetProperty("outstanding").GetDecimal());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/accounting/receivables/payments/{payId}/void", null)).StatusCode);

            // another company sees none of it, and a viewer has no rights
            var other = await TestKit.NewCompanyAsync(_f, "Debtors Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/receivables", J)).EnumerateArray());
            var otherChart = await ChartAsync(other.Admin);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsJsonAsync($"/api/v1/accounting/receivables/{aId}/receive", new { date = "2026-10-01", amount = 10m, accountId = otherChart["1010"] })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/accounting/customers/statement?customer={Uri.EscapeDataString(key)}&currency=USD")).StatusCode);
        }

        private static object Asset(Dictionary<string, Guid> chart, string name, string date, decimal cost, decimal residual, int months, string method = "straight_line", decimal rate = 0, string? code = null, bool paid = true) => new
        {
            name, code, purchaseDate = date, currency = "USD", cost, residualValue = residual, usefulLifeMonths = months, method, ratePercent = rate,
            assetAccountId = chart["1500"], accumulatedAccountId = chart["1590"], expenseAccountId = chart["5900"], paidFromAccountId = paid ? chart["1010"] : (Guid?)null,
        };

        [Fact]
        public async Task Assets_are_depreciated_month_by_month_once_and_disposal_books_the_gain_or_loss()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Assets Co");
            var chart = await ChartAsync(co.Admin);

            // a 12-month straight-line asset from January 2025: 100 a month, exactly its cost by December, never charged twice
            var van = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets", Asset(chart, "Delivery van", "2025-01-15", 1200m, 0m, 12, code: "VAN-1"))).Json();
            Assert.Equal(1200m, van.GetProperty("netBookValue").GetDecimal());
            var run = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets/depreciate", new { upTo = "2025-12-31" })).Json();
            Assert.Equal(12, run.GetProperty("charges").GetInt32()); Assert.Equal(1200m, run.GetProperty("amount").GetDecimal());
            Assert.Equal(0, (await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets/depreciate", new { upTo = "2026-06-30" })).Json()).GetProperty("charges").GetInt32());   // fully depreciated
            Assert.Equal(0, (await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets/depreciate", new { upTo = "2025-12-31" })).Json()).GetProperty("charges").GetInt32());
            var register = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/assets?asAt=2026-10-01", J);
            Assert.Equal(1200m, register.GetProperty("assets")[0].GetProperty("accumulatedDepreciation").GetDecimal()); Assert.Equal(0m, register.GetProperty("assets")[0].GetProperty("netBookValue").GetDecimal());
            var tb = await GetTb(co.Admin, "2025-12-31");   // the van's year
            Assert.True(tb.GetProperty("balanced").GetBoolean()); Assert.Equal(1200m, Tb(tb, "1590", "credit")); Assert.Equal(1200m, Tb(tb, "5900", "debit")); Assert.Equal(1200m, Tb(tb, "1500", "debit"));
            Assert.True((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/reports/balance-sheet?currency=USD&asAt=2035-01-01", J)).GetProperty("balanced").GetBoolean());

            // reducing balance: a yearly percentage of what is left, never below the residual value
            await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets", Asset(chart, "Generator", "2026-01-01", 1000m, 100m, 60, "reducing", 20m, paid: false));
            var red = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets/depreciate", new { upTo = "2026-03-31" })).Json();
            Assert.Equal(3, red.GetProperty("charges").GetInt32()); Assert.InRange(red.GetProperty("amount").GetDecimal(), 49.17m, 49.19m);

            // refused: a residual value at or above the cost, a reducing asset with no rate, a repeated code, a wrong account type
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets", Asset(chart, "Bad", "2026-01-01", 100m, 100m, 12))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets", Asset(chart, "No rate", "2026-01-01", 100m, 0m, 12, "reducing", 0m))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets", Asset(chart, "Twin", "2026-01-01", 100m, 0m, 12, code: "VAN-1"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets", new { name = "Wrong", purchaseDate = "2026-01-01", currency = "USD", cost = 100m, residualValue = 0m, usefulLifeMonths = 12, method = "straight_line", ratePercent = 0m, assetAccountId = chart["4010"], accumulatedAccountId = chart["1590"], expenseAccountId = chart["5900"] })).StatusCode);

            // disposal: sold part-way for less than its book value, so a loss is booked and the asset leaves the books
            var truck = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets", Asset(chart, "Old truck", "2026-01-01", 1200m, 0m, 12, code: "TRK-1"))).Json();
            var truckId = truck.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"/api/v1/accounting/assets/{truckId}/dispose", new { date = "2026-06-15", proceeds = 500m })).StatusCode);   // money needs an account
            var sold = await (await co.Admin.PostAsJsonAsync($"/api/v1/accounting/assets/{truckId}/dispose", new { date = "2026-06-15", proceeds = 500m, receivedIntoAccountId = chart["1010"] })).Json();
            Assert.Equal("Disposed", sold.GetProperty("status").GetString()); Assert.Equal(600m, sold.GetProperty("accumulatedDepreciation").GetDecimal());
            tb = await GetTb(co.Admin, "2026-12-31");   // the year of the sale
            Assert.True(tb.GetProperty("balanced").GetBoolean()); Assert.Equal(100m, Tb(tb, "5990", "debit"));         // sold at 500 against a book value of 600
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync($"/api/v1/accounting/assets/{truckId}/dispose", new { date = "2026-06-20", proceeds = 0m })).StatusCode);

            // a closed period refuses depreciation that falls inside it, and other companies never see these assets
            await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets", Asset(chart, "Laptop", "2026-02-01", 600m, 0m, 12, paid: false));
            await co.Admin.PutAsJsonAsync("/api/v1/accounting/period", new { lockedUntil = "2026-09-30" });
            var locked = await co.Admin.PostAsJsonAsync("/api/v1/accounting/assets/depreciate", new { upTo = "2026-09-30" });
            Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode); Assert.Contains("PERIOD_LOCKED", await locked.Content.ReadAsStringAsync());
            var other = await TestKit.NewCompanyAsync(_f, "Assets Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/assets", J)).GetProperty("assets").EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsJsonAsync($"/api/v1/accounting/assets/{truckId}/dispose", new { date = "2026-07-01", proceeds = 0m })).StatusCode);
        }
    }
}
