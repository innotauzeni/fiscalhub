using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Services;

namespace Fdms.Tests
{
    /// <summary>The chart of accounts as a tree with headings and sub-types, the company's own code format, renumbering, CSV import, and the reports built on it.</summary>
    public class ChartOfAccountsTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        public ChartOfAccountsTests(AiFactory f) => _f = f;

        private static async Task<List<JsonElement>> AccountsAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToList();
        private static Guid Id(List<JsonElement> chart, string code) => chart.Single(a => a.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

        private static Task<HttpResponseMessage> SaveAsync(HttpClient c, string code, string name, string type, Guid? parent = null, bool group = false, string? subType = null, Guid? id = null) =>
            c.PutAsJsonAsync("/api/v1/accounting/accounts", new { id, code, name, type, isActive = true, parentId = parent, isGroup = group, subType, description = (string?)null });

        private static Task<HttpResponseMessage> JournalAsync(HttpClient c, string date, Guid debit, Guid credit, decimal amount, string memo = "Test entry") =>
            c.PostAsJsonAsync("/api/v1/accounting/journals", new { date, currency = "USD", memo, reference = (string?)null, lines = new object[] { new { accountId = debit, debit = amount, credit = 0m }, new { accountId = credit, debit = 0m, credit = amount } } });

        [Fact]
        public async Task Accounts_form_a_tree_with_headings_and_the_trial_balance_rolls_up_and_drills_down()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Chart Tree Co");
            var chart = await AccountsAsync(co.Admin);
            Assert.Equal("cash", chart.Single(a => a.GetProperty("code").GetString() == "1010").GetProperty("subType").GetString());
            Assert.Equal("fixed_asset", chart.Single(a => a.GetProperty("code").GetString() == "1500").GetProperty("subType").GetString());

            // a heading for the motor expenses with two sub-accounts; codes are suggested under the heading
            var head = await (await SaveAsync(co.Admin, "5200", "Motor vehicle expenses", "Expense", group: true)).Json();
            var headId = head.GetProperty("id").GetGuid();
            var next = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/chart/next-code?type=Expense&parentId={headId}", J);
            Assert.Equal("5210", next.GetProperty("code").GetString());
            var fuel = (await (await SaveAsync(co.Admin, "5210", "Fuel", "Expense", headId)).Json()).GetProperty("id").GetGuid();
            Assert.Equal("5220", (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/chart/next-code?type=Expense&parentId={headId}", J)).GetProperty("code").GetString());
            var repairs = (await (await SaveAsync(co.Admin, "5220", "Vehicle repairs", "Expense", headId)).Json()).GetProperty("id").GetGuid();
            Assert.Equal("1060", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/chart/next-code?type=Asset", J)).GetProperty("code").GetString());

            // the rules of the tree
            Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(co.Admin, "5230", "Wrong type", "Expense", Id(chart, "1010"))).StatusCode);             // parent of another type
            Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(co.Admin, "5200", "Motor vehicle expenses", "Expense", fuel, true, id: headId)).StatusCode); // under its own child
            Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(co.Admin, "5240", "Bad", "Expense", subType: "revenue")).StatusCode);                     // sub-type of another type
            Assert.Equal(HttpStatusCode.BadRequest, (await JournalAsync(co.Admin, "2026-10-05", headId, Id(chart, "1010"), 10m)).StatusCode);                  // nothing posts to a heading
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/accounting/accounts/{headId}")).StatusCode);                             // it has sub-accounts

            chart = await AccountsAsync(co.Admin);
            var codes = chart.Select(a => a.GetProperty("code").GetString()).ToList();
            Assert.True(codes.IndexOf("5200") < codes.IndexOf("5210") && codes.IndexOf("5210") < codes.IndexOf("5220"));   // tree order
            Assert.Equal(1, chart.Single(a => a.GetProperty("code").GetString() == "5210").GetProperty("level").GetInt32());
            Assert.True(chart.Single(a => a.GetProperty("code").GetString() == "5200").GetProperty("hasChildren").GetBoolean());

            var year = DateTime.UtcNow.AddHours(2).Year;
            Assert.True((await JournalAsync(co.Admin, $"{year}-01-10", fuel, Id(chart, "1010"), 40m, "Diesel")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, $"{year}-02-10", repairs, Id(chart, "1010"), 60m, "Tyres")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, $"{year}-02-11", Id(chart, "1010"), Id(chart, "3010"), 500m, "Capital")).IsSuccessStatusCode);

            // extended trial balance for February: opening, movements and closing, with the heading's subtotal
            var tb = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&from={year}-02-01&asAt={year}-02-28", J);
            Assert.True(tb.GetProperty("balanced").GetBoolean());
            JsonElement Row(string code) => tb.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);
            Assert.Equal(40m, Row("5210").GetProperty("opening").GetDecimal());
            Assert.Equal(60m, Row("5220").GetProperty("periodDebit").GetDecimal());
            Assert.Equal(100m, Row("5200").GetProperty("subtotalClosing").GetDecimal());
            Assert.Equal(0m, Row("5200").GetProperty("closing").GetDecimal());
            Assert.True(Row("5200").GetProperty("isGroup").GetBoolean());
            Assert.Equal(400m, Row("1010").GetProperty("debit").GetDecimal());
            Assert.Equal(tb.GetProperty("totalPeriodDebit").GetDecimal(), tb.GetProperty("totalPeriodCredit").GetDecimal());

            // drill down: the heading's ledger takes in its sub-accounts, and each line opens its journal
            var ledger = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/ledger?accountId={headId}&currency=USD&from={year}-01-01&to={year}-12-31&subAccounts=true", J);
            Assert.True(ledger.GetProperty("includesSubAccounts").GetBoolean());
            Assert.Equal(100m, ledger.GetProperty("closing").GetDecimal());
            Assert.Equal(new[] { "5210", "5220" }, ledger.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("accountCode").GetString()).ToArray());
            var entryId = ledger.GetProperty("lines")[1].GetProperty("entryId").GetGuid();
            Assert.Equal("Tyres", (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/journals/{entryId}", J)).GetProperty("memo").GetString());

            // the statements are classified and roll the heading up
            var pl = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss?currency=USD&from={year}-01-01&to={year}-12-31&compare=year", J);
            var opex = pl.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "operating_expense");
            Assert.Equal(100m, opex.GetProperty("total").GetDecimal());
            Assert.Equal(0m, opex.GetProperty("compareTotal").GetDecimal());
            Assert.Equal(100m, opex.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == "5200").GetProperty("subtotal").GetDecimal());
            Assert.Equal(-100m, pl.GetProperty("netProfit").GetDecimal());
            Assert.Equal(-100m, pl.GetProperty("operatingProfit").GetDecimal());
            var bs = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/balance-sheet?currency=USD&asAt={year}-12-31&compare=year", J);
            Assert.True(bs.GetProperty("balanced").GetBoolean());
            Assert.Equal(400m, bs.GetProperty("totalAssets").GetDecimal());
            var equity = bs.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "equity");
            Assert.Equal(-100m, equity.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Current year earnings").GetProperty("amount").GetDecimal());
        }

        [Fact]
        public async Task Earlier_years_profit_moves_to_retained_earnings()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Chart Years Co");
            var chart = await AccountsAsync(co.Admin);
            var year = DateTime.UtcNow.AddHours(2).Year;
            Assert.True((await JournalAsync(co.Admin, $"{year - 1}-06-01", Id(chart, "1010"), Id(chart, "4090"), 300m, "Last year's income")).IsSuccessStatusCode);
            Assert.True((await JournalAsync(co.Admin, $"{year}-01-05", Id(chart, "1010"), Id(chart, "4090"), 50m, "This year's income")).IsSuccessStatusCode);

            var tb = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={year}-12-31", J);
            JsonElement Row(string code) => tb.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);
            Assert.True(tb.GetProperty("balanced").GetBoolean());
            Assert.Equal(50m, Row("4090").GetProperty("credit").GetDecimal());
            Assert.Equal(300m, Row("3020").GetProperty("credit").GetDecimal());   // retained earnings
            Assert.Equal(350m, Row("1010").GetProperty("debit").GetDecimal());

            var bs = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/balance-sheet?currency=USD&asAt={year}-12-31&compare=year", J);
            Assert.True(bs.GetProperty("balanced").GetBoolean());
            var equity = bs.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "equity");
            Assert.Equal(300m, equity.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == "3020").GetProperty("amount").GetDecimal());
            Assert.Equal(50m, equity.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Current year earnings").GetProperty("amount").GetDecimal());
            Assert.Equal(300m, bs.GetProperty("compareTotalAssets").GetDecimal());

            // a financial year starting in July moves the split
            var settings = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/chart/settings", J);
            var s = settings.GetProperty("settings");
            var put = await co.Admin.PutAsJsonAsync("/api/v1/accounting/chart/settings", new { codeFormat = "9999", classStarts = s.GetProperty("classStarts"), codeStep = 10, enforceCodeFormat = false, fiscalYearStartMonth = 7 });
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
            tb = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={year}-03-31", J);
            Assert.Equal(DateTime.Parse($"{year - 1}-07-01"), tb.GetProperty("from").GetDateTime());
            Assert.Equal(50m, Row("4090").GetProperty("credit").GetDecimal());   // last June is in the previous financial year
            Assert.Equal(300m, Row("3020").GetProperty("credit").GetDecimal());
        }

        [Fact]
        public async Task The_code_format_is_the_companys_choice_and_the_chart_can_be_renumbered_and_imported()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Chart Format Co");
            var chart = await AccountsAsync(co.Admin);
            var cash = Id(chart, "1010");
            object Rules(string format, int step, bool enforce, string asset = "1") =>
                new { codeFormat = format, classStarts = new Dictionary<string, string> { ["Asset"] = asset, ["Liability"] = "2", ["Equity"] = "3", ["Income"] = "4", ["Expense"] = "5" }, codeStep = step, enforceCodeFormat = enforce, fiscalYearStartMonth = 1 };

            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/accounting/chart/settings", Rules("99x9", 10, false))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/accounting/chart/renumber/preview", Rules("999999", 100, true, "4"))).StatusCode);   // assets and income both 4

            // six digits with a dash: 1010 becomes 10-1000, children keep their place, and codes are enforced from now on
            var preview = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/chart/renumber/preview", Rules("99-9999", 100, true))).Json();
            Assert.Equal(0, preview.GetProperty("problems").GetArrayLength());
            Assert.Equal("10-1000", preview.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("oldCode").GetString() == "1010").GetProperty("newCode").GetString());
            Assert.Equal("1010", (await AccountsAsync(co.Admin)).Single(a => a.GetProperty("id").GetGuid() == cash).GetProperty("code").GetString());   // a preview changes nothing
            var done = await co.Admin.PostAsJsonAsync("/api/v1/accounting/chart/renumber", Rules("99-9999", 100, true));
            Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
            chart = await AccountsAsync(co.Admin);
            Assert.Equal("10-1000", chart.Single(a => a.GetProperty("id").GetGuid() == cash).GetProperty("code").GetString());
            Assert.Equal("20-1000", chart.Single(a => a.GetProperty("name").GetString() == "Accounts payable").GetProperty("code").GetString());
            Assert.Equal("10-5100", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/chart/next-code?type=Asset&parentId=" + Id(chart, "10-5000"), J)).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(co.Admin, "5125", "Old style", "Expense")).StatusCode);    // does not follow the format
            Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(co.Admin, "49-9000", "Wrong class", "Expense")).StatusCode); // expense codes start with 5
            Assert.True((await SaveAsync(co.Admin, "51-2500", "Water", "Expense")).IsSuccessStatusCode);
            chart = await AccountsAsync(co.Admin);

            // the system still finds its accounts by role: a bill posts to accounts payable and VAT input after renumbering
            var sup = (await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/suppliers", new { id = (Guid?)null, name = "Water board", termsDays = 30, isActive = true })).Json()).GetProperty("id").GetGuid();
            var bill = await co.Admin.PostAsJsonAsync("/api/v1/accounting/bills", new { supplierId = sup, number = "W-1", date = DateTime.UtcNow.AddHours(2).ToString("yyyy-MM-dd"), currency = "USD", memo = "Water", subtotal = 100m, vat = 15m, expenseAccountId = Id(chart, "51-2500") });
            Assert.True(bill.IsSuccessStatusCode, await bill.Content.ReadAsStringAsync());

            // import: a heading and two sub-accounts, an update, and rows that cannot be used
            var csv = "code,name,type,subtype,parent,heading,description\n"
                    + "52-0000,Staff costs,Expense,operating_expense,,yes,All people costs\n"
                    + "52-1000,Salaries,Expense,,52-0000,,\n"
                    + "52-2000,Pension,Expense,,52-0000,,\n"
                    + "51-2500,Water and sewerage,Expense,,,,\n"
                    + "9999,Bad format,Expense,,,,\n"
                    + "21-9000,Medical aid payable,Liability,,52-0000,,\n"
                    + "52-4000,Nothing,Nonsense,,,,";
            var imported = await (await co.Admin.PostAsJsonAsync("/api/v1/accounting/chart/import", new { csv })).Json();
            Assert.Equal(4, imported.GetProperty("created").GetInt32());
            Assert.Equal(1, imported.GetProperty("updated").GetInt32());
            Assert.Equal(2, imported.GetProperty("skipped").GetInt32());
            chart = await AccountsAsync(co.Admin);
            var staff = chart.Single(a => a.GetProperty("code").GetString() == "52-0000");
            Assert.True(staff.GetProperty("isGroup").GetBoolean());
            Assert.Equal(staff.GetProperty("id").GetGuid(), chart.Single(a => a.GetProperty("code").GetString() == "52-1000").GetProperty("parentId").GetGuid());
            var medical = chart.Single(a => a.GetProperty("code").GetString() == "21-9000");
            Assert.False(medical.TryGetProperty("parentId", out var mp) && mp.ValueKind != JsonValueKind.Null);   // a liability cannot sit under an expense
            Assert.Equal("Water and sewerage", chart.Single(a => a.GetProperty("code").GetString() == "51-2500").GetProperty("name").GetString());
        }

        [Fact]
        public void Codes_follow_the_format_and_suggestions_fill_the_room_under_a_parent()
        {
            var rules = new ChartRules("9999", ChartCodes.DefaultStarts, 10, true, 1);
            Assert.True(ChartCodes.ValidMask("99-999")); Assert.False(ChartCodes.ValidMask("9--9")); Assert.False(ChartCodes.ValidMask("-999"));
            Assert.Equal("10-100", ChartCodes.Format("10100", "99-999"));
            Assert.Null(ChartCodes.Problem("1010", "Asset", rules));
            Assert.NotNull(ChartCodes.Problem("2010", "Asset", rules));
            var parent = new LedgerAccount { Code = "1020" };
            Assert.Equal("1021", ChartCodes.Next("Asset", parent, new[] { "1020" }, rules).Code);
            Assert.Equal("1023", ChartCodes.Next("Asset", parent, new[] { "1020", "1021", "1022" }, rules).Code);
            Assert.NotNull(ChartCodes.Next("Asset", new LedgerAccount { Code = "1025" }, new[] { "1025" }, rules).Problem);
            var six = new ChartRules("999999", ChartCodes.DefaultStarts, 100, true, 1);
            Assert.Equal(("102100", (string?)null), ChartCodes.Convert("1021", "Asset", rules, six));
            Assert.NotNull(ChartCodes.Convert("10215", "Asset", six, rules).Problem);
            Assert.Equal(new DateTime(2025, 7, 1), ChartCodes.YearStart(new DateTime(2026, 3, 1), 7));
        }
    }
}
