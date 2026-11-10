using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Fdms.Tests
{
    /// <summary>Cost centres, accruals and recurring journals, opening balances and closing the year, the cash flow and dashboard, statements and exports.</summary>
    public class AccountingModulesTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        public AccountingModulesTests(AiFactory f) => _f = f;

        private static async Task<Dictionary<string, Guid>> ChartAsync(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());

        private static object Line(Guid account, decimal debit, decimal credit, Guid? centre = null) => new { accountId = account, debit, credit, description = (string?)null, costCentreId = centre };

        private static Task<HttpResponseMessage> JournalAsync(HttpClient c, string date, object[] lines, string memo = "Test entry", string? reverseOn = null) =>
            c.PostAsJsonAsync("/api/v1/accounting/journals", new { date, currency = "USD", memo, reference = (string?)null, lines, reverseOn });

        private static int Year => DateTime.UtcNow.AddHours(2).Year;

        [Fact]
        public async Task Cost_centres_split_profit_and_accruals_reverse_themselves()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Centres Co");
            var chart = await ChartAsync(co.Admin);
            var hre = (await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/cost-centres", new { code = "HRE", name = "Harare shop", kind = "department", isActive = true })).Json()).GetProperty("id").GetGuid();
            var byo = (await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/cost-centres", new { code = "BYO", name = "Bulawayo shop", kind = "project", isActive = true })).Json()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/cost-centres", new { code = "HRE", name = "Twin", kind = "department", isActive = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/cost-centres", new { code = "BR1", name = "Branch", kind = "branch", isActive = true })).StatusCode);   // a branch centre needs its branch

            // income and rent in each shop, and an entry with no cost centre
            Assert.True((await JournalAsync(co.Admin, $"{Year}-03-01", new[] { Line(chart["1010"], 500, 0), Line(chart["4090"], 0, 500, hre) })).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, $"{Year}-03-02", new[] { Line(chart["1010"], 300, 0), Line(chart["4090"], 0, 300, byo) })).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, $"{Year}-03-03", new[] { Line(chart["5110"], 200, 0, hre), Line(chart["1010"], 0, 200) })).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, $"{Year}-03-04", new[] { Line(chart["5160"], 50, 0), Line(chart["1010"], 0, 50) })).IsSuccessStatusCode);
            var other = await TestKit.NewCompanyAsync(_f, "Centres Other");
            var otherChart = await ChartAsync(other.Admin);
            Assert.Equal(HttpStatusCode.BadRequest, (await JournalAsync(other.Admin, $"{Year}-03-04", new[] { Line(otherChart["5160"], 5, 0, hre), Line(otherChart["1010"], 0, 5) })).StatusCode);   // not their cost centre

            var range = $"from={Year}-03-01&to={Year}-03-31";
            var pl = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss?currency=USD&{range}&costCentreId={hre}", J);
            Assert.Equal(300m, pl.GetProperty("netProfit").GetDecimal());   // 500 - 200
            Assert.Equal(-50m, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss?currency=USD&{range}&costCentreId={Guid.Empty}", J)).GetProperty("netProfit").GetDecimal());

            var by = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss-centres?currency=USD&{range}", J);
            var cols = by.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
            Assert.Equal(new[] { "Bulawayo shop", "Harare shop", "Not assigned" }, cols);
            var net = by.GetProperty("summaries").EnumerateArray().Single(s => s.GetProperty("label").GetString() == "Net profit");
            Assert.Equal(new[] { 300m, 300m, -50m }, net.GetProperty("amounts").EnumerateArray().Select(x => x.GetDecimal()).ToArray());
            Assert.Equal(550m, net.GetProperty("total").GetDecimal());
            var journals = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/journals?{range}", J)).EnumerateArray();
            Assert.Contains(journals.SelectMany(j => j.GetProperty("lines").EnumerateArray()), l => l.TryGetProperty("costCentre", out var cc) && cc.GetString() == "HRE Harare shop");
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/accounting/cost-centres/{hre}")).StatusCode);   // it has postings

            // an accrual: posted at month end, reversed on the first of the next month
            var acc = await JournalAsync(co.Admin, $"{Year}-03-31", new[] { Line(chart["5120"], 80, 0), Line(chart["2200"], 0, 80) }, "Electricity accrual", reverseOn: $"{Year}-04-01");
            Assert.True(acc.IsSuccessStatusCode, await acc.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await JournalAsync(co.Admin, $"{Year}-03-31", new[] { Line(chart["5120"], 1, 0), Line(chart["2200"], 0, 1) }, reverseOn: $"{Year}-03-30")).StatusCode);
            var april = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/journals?from={Year}-04-01&to={Year}-04-01", J)).EnumerateArray().ToList();
            Assert.Contains(april, j => j.GetProperty("isReversal").GetBoolean() && j.GetProperty("memo").GetString()!.Contains("Electricity accrual"));
            var tb = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={Year}-04-30", J);
            Assert.DoesNotContain(tb.GetProperty("rows").EnumerateArray(), r => r.GetProperty("code").GetString() == "2200" && r.GetProperty("credit").GetDecimal() != 0);
        }

        [Fact]
        public async Task Recurring_journals_post_on_schedule_and_the_year_can_be_opened_and_closed()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Recurring Co");
            var chart = await ChartAsync(co.Admin);

            // opening balances: cash 1000, loan 400, the rest to opening balance equity
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/opening-balances", new { date = $"{Year - 2}-12-31", currency = "USD", lines = new[] { new { accountId = chart["5110"], debit = 10m, credit = 0m } } })).StatusCode);
            var opening = await co.Admin.PostAsJsonAsync("/api/v1/accounting/opening-balances", new { date = $"{Year - 2}-12-31", currency = "USD",
                lines = new[] { new { accountId = chart["1010"], debit = 1000m, credit = 0m }, new { accountId = chart["2300"], debit = 0m, credit = 400m } } });
            Assert.True(opening.IsSuccessStatusCode, await opening.Content.ReadAsStringAsync());
            var ob = await opening.Json();
            Assert.Contains(ob.GetProperty("lines").EnumerateArray(), l => l.GetProperty("accountName").GetString() == "Opening balance equity" && l.GetProperty("credit").GetDecimal() == 600m);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/opening-balances", new { date = $"{Year - 2}-12-31", currency = "USD",
                lines = new[] { new { accountId = chart["1010"], debit = 1m, credit = 0m } } })).StatusCode);   // only once

            // monthly rent from January last year, ending in March: three postings when the round runs
            var save = await co.Admin.PutAsJsonAsync("/api/v1/accounting/recurring", new { name = "Shop rent", currency = "USD", memo = "Monthly rent", reference = "RENT",
                lines = new[] { Line(chart["5110"], 100, 0), Line(chart["1010"], 0, 100) }, frequency = "monthly", nextDate = $"{Year - 1}-01-31", endDate = $"{Year - 1}-03-31", autoReverse = false, isActive = true });
            Assert.True(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());
            var rentId = (await save.Json()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/recurring", new { name = "Bad", currency = "USD", memo = "Unbalanced",
                lines = new[] { Line(chart["5110"], 100, 0), Line(chart["1010"], 0, 90) }, frequency = "monthly", nextDate = $"{Year}-01-31", autoReverse = false, isActive = true })).StatusCode);
            using (var scope = _f.Services.CreateScope())
                Assert.True(await scope.ServiceProvider.GetRequiredService<IBooksAutomationService>().RunDueAsync(new DateTime(Year - 1, 12, 31), default) >= 3);
            var rent = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/recurring", J)).EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == rentId);
            Assert.Equal(3, rent.GetProperty("runCount").GetInt32());
            Assert.False(rent.GetProperty("isActive").GetBoolean());   // past its end date
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/accounting/recurring/{rentId}/run", null)).StatusCode);

            // last year: 300 of rent. Close it: the period locks, and the loss stays in retained earnings
            var ye = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/year-end?year={Year - 1}", J);
            Assert.False(ye.GetProperty("closed").GetBoolean());
            Assert.Equal(-300m, ye.GetProperty("netProfit").EnumerateArray().Single().GetProperty("amount").GetDecimal());
            Assert.Contains(ye.GetProperty("checks").EnumerateArray(), c => c.GetProperty("label").GetString() == "Trial balance in USD balances" && c.GetProperty("ok").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/year-end/close", new { yearEnd = $"{Year - 1}-06-30", depreciate = false })).StatusCode);
            var closed = await co.Admin.PostAsJsonAsync("/api/v1/accounting/year-end/close", new { yearEnd = $"{Year - 1}-12-31", depreciate = true });
            Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync());
            Assert.True((await closed.Json()).GetProperty("closed").GetBoolean());
            Assert.Equal(HttpStatusCode.Conflict, (await JournalAsync(co.Admin, $"{Year - 1}-12-15", new[] { Line(chart["5110"], 1, 0), Line(chart["1010"], 0, 1) })).StatusCode);   // locked
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/year-end/close", new { yearEnd = $"{Year - 1}-12-31", depreciate = false })).StatusCode);
            var tb = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={Year}-01-31", J);
            Assert.Equal(300m, tb.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == "3020").GetProperty("debit").GetDecimal());

            // reopening unlocks it again
            var reopened = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/year-end/reopen", new { yearEnd = $"{Year - 1}-12-31" })).Json();
            Assert.False(reopened.GetProperty("closed").GetBoolean());
            Assert.True((await JournalAsync(co.Admin, $"{Year - 1}-12-15", new[] { Line(chart["5110"], 1, 0), Line(chart["1010"], 0, 1) })).IsSuccessStatusCode);
        }

        [Fact]
        public async Task The_cash_flow_reconciles_and_the_dashboard_reads_the_ratios()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Cash Flow Co");
            var chart = await ChartAsync(co.Admin);
            var d = $"{Year}-05-";
            Assert.True((await JournalAsync(co.Admin, d + "01", new[] { Line(chart["1020"], 5000, 0), Line(chart["3010"], 0, 5000) }, "Capital")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, d + "02", new[] { Line(chart["1500"], 1200, 0), Line(chart["1020"], 0, 1200) }, "Van")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, d + "03", new[] { Line(chart["1100"], 2000, 0), Line(chart["4010"], 0, 2000) }, "Sales on account")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, d + "04", new[] { Line(chart["1020"], 1500, 0), Line(chart["1100"], 0, 1500) }, "Customer paid")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, d + "05", new[] { Line(chart["5010"], 800, 0), Line(chart["2010"], 0, 800) }, "Stock bought on credit")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, d + "06", new[] { Line(chart["5110"], 300, 0), Line(chart["1020"], 0, 300) }, "Rent")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, d + "07", new[] { Line(chart["1020"], 1000, 0), Line(chart["2300"], 0, 1000) }, "Loan")).IsSuccessStatusCode);

            var cf = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/cash-flow?currency=USD&from={d}01&to={d}31", J);
            Assert.True(cf.GetProperty("reconciles").GetBoolean(), cf.GetRawText());
            Assert.Equal(900m, cf.GetProperty("netProfit").GetDecimal());   // 2000 - 800 - 300
            decimal Section(string key) => cf.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("total").GetDecimal();
            Assert.Equal(1200m, Section("operating"));    // 900 profit - 500 more owed by customers + 800 more owed to suppliers
            Assert.Equal(-1200m, Section("investing"));
            Assert.Equal(6000m, Section("financing"));
            Assert.Equal(6000m, cf.GetProperty("closingCash").GetDecimal());

            var dash = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/dashboard?currency=USD&from={d}01&to={d}31", J);
            decimal? Ratio(string key) { var v = dash.GetProperty("ratios").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key).GetProperty("value"); return v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null; }
            Assert.Equal(60m, Ratio("gross_margin"));   // (2000 - 800) / 2000
            Assert.Equal(45m, Ratio("net_margin"));
            Assert.Equal(8.13m, Ratio("current"));      // (6000 cash + 500 owed) / 800 owed to suppliers
            Assert.Equal(12, dash.GetProperty("trend").GetArrayLength());
            Assert.Equal(6000m, dash.GetProperty("trend")[11].GetProperty("cash").GetDecimal());
        }

        [Fact]
        public async Task Spend_and_receive_money_and_transfers_post_from_drafts_and_void_cleanly()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Cashbook Co");
            var chart = await ChartAsync(co.Admin);
            var day = $"{Year}-06-10";
            var centre = (await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/cost-centres", new { code = "OPS", name = "Operations", kind = "department", isActive = true })).Json()).GetProperty("id").GetGuid();
            object Doc(string kind, Guid account, object[]? lines = null, Guid? to = null, decimal amount = 0, decimal fee = 0, bool post = false, string? contact = null) =>
                new { id = (Guid?)null, kind, date = day, currency = "USD", accountId = account, toAccountId = to, contact, reference = "R-1", memo = "Test", lines, amount, fee, post };

            // receive 1000 into the bank (sale with VAT 15%), kept as a draft first
            var rec = await co.Admin.PutAsJsonAsync("/api/v1/accounting/money", Doc("receipt", chart["1020"], new object[] { new { accountId = chart["4090"], description = "Consulting", amount = 1150m, vatRate = 15m, costCentreId = centre } }, contact: "Glow Salon"));
            Assert.True(rec.IsSuccessStatusCode, await rec.Content.ReadAsStringAsync());
            var r = await rec.Json();
            Assert.Equal("Draft", r.GetProperty("status").GetString());
            Assert.StartsWith("RCT-", r.GetProperty("number").GetString());
            Assert.Equal(150m, r.GetProperty("vat").GetDecimal());
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/money", Doc("payment", chart["5110"], new object[] { new { accountId = chart["5110"], description = "x", amount = 5m } }))).StatusCode);   // money must move through cash or bank
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/money", Doc("transfer", chart["1020"], to: chart["1020"], amount: 5m))).StatusCode);

            // spend 230 on rent (no VAT) from cash, posted at once; and a transfer of 500 from bank to cash with a 2.50 charge, as a draft
            var pay = await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/money", Doc("payment", chart["1010"], new object[] { new { accountId = chart["5110"], description = "June rent", amount = 230m, vatRate = 0m } }, post: true, contact: "Landlord"))).Json();
            Assert.Equal("Posted", pay.GetProperty("status").GetString());
            Assert.StartsWith("JV-", pay.GetProperty("journalNumber").GetString());
            var trf = await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/money", Doc("transfer", chart["1020"], to: chart["1010"], amount: 500m, fee: 2.5m))).Json();

            // post to ledger: the drafts in one batch
            var drafts = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/money?status=Draft", J);
            Assert.Equal(2, drafts.GetProperty("count").GetInt32());
            var batch = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/money/post-batch", new { ids = drafts.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToArray() })).Json();
            Assert.Equal(2, batch.GetProperty("posted").GetInt32());

            var tb = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={Year}-12-31", J);
            decimal Tb(string code, string side) => tb.GetProperty("rows").EnumerateArray().Where(x => x.GetProperty("code").GetString() == code).Select(x => x.GetProperty(side).GetDecimal()).FirstOrDefault();
            Assert.True(tb.GetProperty("balanced").GetBoolean());
            Assert.Equal(647.5m, Tb("1020", "debit"));   // 1150 in, 502.50 out
            Assert.Equal(270m, Tb("1010", "debit"));     // 500 in, 230 out
            Assert.Equal(1000m, Tb("4090", "credit")); Assert.Equal(150m, Tb("2110", "credit")); Assert.Equal(2.5m, Tb("5150", "debit"));
            var pl = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss?currency=USD&from={Year}-06-01&to={Year}-06-30&costCentreId={centre}", J);
            Assert.Equal(1000m, pl.GetProperty("netProfit").GetDecimal());

            // the bank ledger names the other side of each entry
            var book = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/cashbook?accountId={chart["1020"]}&currency=USD&from={Year}-06-01&to={Year}-06-30", J);
            Assert.Contains(book.GetProperty("lines").EnumerateArray(), l => l.GetProperty("otherAccounts").GetString()!.Contains("Cash on hand") && l.GetProperty("source").GetString() == "transfer");

            // a posted document cannot change or be deleted; voiding reverses it
            var trfId = trf.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/accounting/money/{trfId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/accounting/money/{trfId}/post", null)).StatusCode);
            Assert.Equal("Void", (await (await co.Admin.PostAsync($"/api/v1/accounting/money/{trfId}/void", null)).Json()).GetProperty("status").GetString());
            tb = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={Year}-12-31", J);
            Assert.Equal(1150m, Tb("1020", "debit"));
            var list = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/money?kind=payment", J);
            Assert.Equal(230m, list.GetProperty("totals")[0].GetProperty("amount").GetDecimal());
            Assert.Equal(1, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/money?dateBy=created&from={DateTime.UtcNow.AddHours(2):yyyy-MM-dd}&to={DateTime.UtcNow.AddHours(2):yyyy-MM-dd}&kind=transfer", J)).GetProperty("count").GetInt32());
        }

        [Fact]
        public async Task Statements_come_as_pdf_and_by_email_and_any_table_exports()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Statements Co");
            var chart = await ChartAsync(co.Admin);
            var sup = (await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/suppliers", new { id = (Guid?)null, name = "Print Shop", email = "accounts@print.test", termsDays = 30, isActive = true })).Json()).GetProperty("id").GetGuid();
            var today = DateTime.UtcNow.AddHours(2).ToString("yyyy-MM-dd");
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/accounting/bills", new { supplierId = sup, number = "PS-1", date = today, currency = "USD", memo = "Printing", subtotal = 100m, vat = 15m, expenseAccountId = chart["5160"] })).IsSuccessStatusCode);

            var pdf = await co.Admin.GetAsync($"/api/v1/accounting/suppliers/statement/pdf?supplierId={sup}&currency=USD&from={Year}-01-01&to={today}");
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            var bytes = await pdf.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 1000 && bytes[0] == (byte)'%');

            var before = _f.Email.Messages.Count;
            var sent = await co.Admin.PostAsJsonAsync("/api/v1/accounting/statements/email", new { supplierId = sup, currency = "USD", from = $"{Year}-01-01", to = today, message = "As discussed." });
            Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
            Assert.Equal("accounts@print.test", (await sent.Json()).GetProperty("sentTo").GetString());
            var mail = _f.Email.Messages.Skip(before).Single(m => m.To == "accounts@print.test");
            Assert.Contains("As discussed.", mail.Html);
            Assert.True(mail.Files.Single().Bytes[0] == (byte)'%');

            object Doc(string format) => new
            {
                format, title = "Trial balance", subtitle = "As at today · USD", landscape = false,
                columns = new[] { new { header = "Account", kind = "text" }, new { header = "Debit", kind = "money" }, new { header = "Credit", kind = "money" } },
                rows = new[] { new { cells = new[] { "Assets", null, null }, style = "section", indent = 0 }, new { cells = new[] { "1010 Cash, petty", "1,234.50", "" }, style = "normal", indent = 1 },
                               new { cells = new[] { "Totals", "1,234.50", "1,234.50" }, style = "total", indent = 0 } },
            };
            var xlsx = await co.Admin.PostAsJsonAsync("/api/v1/accounting/export", Doc("xlsx"));
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
            var x = await xlsx.Content.ReadAsByteArrayAsync(); Assert.True(x[0] == (byte)'P' && x[1] == (byte)'K');
            using (var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(x)))
            {
                var ws = wb.Worksheets.First();
                Assert.Equal(1234.5, ws.Cell(7, 2).GetDouble());   // a real number, not text
            }
            var csv = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/export", Doc("csv"))).Content.ReadAsStringAsync();
            Assert.Contains("\"  1010 Cash, petty\",\"1,234.50\"", csv);
            var p = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/export", Doc("pdf"))).Content.ReadAsByteArrayAsync();
            Assert.True(p[0] == (byte)'%');
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/export", Doc("docx"))).StatusCode);
        }
    }
}
