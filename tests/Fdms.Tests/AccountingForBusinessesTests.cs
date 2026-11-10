using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>
    /// The books for businesses that keep them here: the accounting-only plan, invoices from the books, bringing in balances when starting, combining currencies,
    /// prepayments, changes in equity, the annual financial statements and the audit trail.
    /// </summary>
    public class AccountingForBusinessesTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        public AccountingForBusinessesTests(AiFactory f) => _f = f;

        private static int Year => DateTime.UtcNow.AddHours(2).Year;
        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        private static async Task<Dictionary<string, Guid>> ChartAsync(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());

        private static async Task<JsonElement> OkJson(HttpResponseMessage res)
        {
            Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
            return await res.Content.ReadFromJsonAsync<JsonElement>(J);
        }

        private static async Task<Guid> CustomerAsync(HttpClient c, string name, string? email = null) =>
            (await OkJson(await c.PutAsJsonAsync("/api/v1/customers", new { name, tin = (string?)null, vatNumber = (string?)null, email, phone = (string?)null, address = "12 Samora Machel Ave, Harare", isActive = true })))
                .GetProperty("id").GetGuid();

        /// <summary>A company moved onto the accounting-only plan, signed in again so its menus follow the plan.</summary>
        private async Task<(TestKit.Company Co, HttpClient Admin, JsonElement Login)> BooksCompanyAsync(string name)
        {
            var op = await TestKit.AdminAsync(_f);
            var co = await TestKit.NewCompanyAsync(_f, name);
            var plan = (await op.GetFromJsonAsync<JsonElement>("/api/v1/plans", J)).EnumerateArray().First(p => p.GetProperty("code").GetString() == "books");
            Assert.Equal("accounting", plan.GetProperty("kind").GetString());
            await OkJson(await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = plan.GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" }));
            var login = await OkJson(await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = co.AdminEmail, password = TestKit.UserPassword }));
            return (co, await TestKit.LoginAsync(_f, co.AdminEmail, TestKit.UserPassword), login);
        }

        [Fact]
        public async Task An_accounting_only_company_invoices_from_the_books_receives_payment_and_credits_without_VAT()
        {
            var (co, admin, login) = await BooksCompanyAsync("Books Only Co");
            // the menu is the books, the financial dashboard first; no devices or fiscal receipts
            var slugs = login.GetProperty("menus").EnumerateArray().Select(m => m.GetProperty("slugname").GetString()).ToList();
            Assert.Equal("accounting/dashboard", slugs[0]);
            Assert.Contains("accounting/sales", slugs); Assert.Contains("accounting/import", slugs); Assert.Contains("customers", slugs);
            Assert.DoesNotContain("devices", slugs); Assert.DoesNotContain("receipts", slugs);
            // and it cannot ask to go live with ZIMRA
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "Ada Admin", contactPhone = "0771234567" })).StatusCode);

            Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/sales/setup", J)).GetProperty("allowed").GetBoolean());
            var chart = await ChartAsync(admin);
            var customer = await CustomerAsync(admin, "Moyo Traders", "accounts@moyo.test");

            // a draft, changed, then issued: numbered and posted (receivables against sales)
            var draft = await OkJson(await admin.PutAsJsonAsync("/api/v1/accounting/sales", new
            {
                customerId = customer, date = Today.ToString("yyyy-MM-dd"), currency = "USD", reference = "PO-77",
                lines = new object[] { new { description = "Consulting (hours)", quantity = 10, unitPrice = 50, discountPercent = 10 }, new { description = "Travel", quantity = 1, unitPrice = 30 } },
            }));
            Assert.Equal("Draft", draft.GetProperty("status").GetString());
            Assert.Equal(480m, draft.GetProperty("total").GetDecimal());
            Assert.Equal(50m, draft.GetProperty("discount").GetDecimal());
            var id = draft.GetProperty("id").GetGuid();
            var inv = await OkJson(await admin.PostAsync($"/api/v1/accounting/sales/{id}/issue", null));
            Assert.Equal("Issued", inv.GetProperty("status").GetString());
            Assert.Equal("INV-000001", inv.GetProperty("number").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/accounting/sales", new { id, customerId = customer, date = Today, currency = "USD", lines = new object[] { new { description = "x", quantity = 1, unitPrice = 1 } } })).StatusCode);
            var journal = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?source=invoice&from=2000-01-01&to=2100-01-01", J))[0];
            Assert.Contains(journal.GetProperty("lines").EnumerateArray(), l => l.GetProperty("accountCode").GetString() == "1100" && l.GetProperty("debit").GetDecimal() == 480);
            Assert.Equal(480m, journal.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("accountCode").GetString() == "4010").Sum(l => l.GetProperty("credit").GetDecimal()));

            // it is owed like any invoice: part paid under Customers owing
            var open = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/receivables?status=open", J)).EnumerateArray().Single(r => r.GetProperty("receiptId").GetGuid() == id);
            Assert.Equal("invoice", open.GetProperty("source").GetString());
            await OkJson(await admin.PostAsJsonAsync($"/api/v1/accounting/receivables/{id}/receive", new { date = Today, amount = 200, accountId = chart["1020"], reference = "EFT-1" }));
            Assert.Equal(280m, (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/sales/{id}", J)).GetProperty("outstanding").GetDecimal());

            // a credit note cannot be for more than is still owing; a smaller one reduces the invoice
            var cn = await OkJson(await admin.PutAsJsonAsync("/api/v1/accounting/sales", new { kind = "credit_note", originalId = id, customerId = customer, date = Today, currency = "USD", lines = new object[] { new { description = "Hours not worked", quantity = 1, unitPrice = 300 } } }));
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/accounting/sales/{cn.GetProperty("id").GetGuid()}/issue", null)).StatusCode);
            await OkJson(await admin.PutAsJsonAsync("/api/v1/accounting/sales", new { id = cn.GetProperty("id").GetGuid(), kind = "credit_note", originalId = id, customerId = customer, date = Today, currency = "USD", lines = new object[] { new { description = "Hours not worked", quantity = 1, unitPrice = 80 } } }));
            Assert.Equal("CN-000001", (await OkJson(await admin.PostAsync($"/api/v1/accounting/sales/{cn.GetProperty("id").GetGuid()}/issue", null))).GetProperty("number").GetString());
            var after = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/sales/{id}", J);
            Assert.Equal(80m, after.GetProperty("credited").GetDecimal()); Assert.Equal(200m, after.GetProperty("outstanding").GetDecimal());
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/accounting/sales/{id}/void", null)).StatusCode);   // payments stand against it

            // the customer's statement includes it, and the document is a PDF that says it is not a fiscal tax invoice
            var st = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/customers/statement?customer={Uri.EscapeDataString(open.GetProperty("customerKey").GetString()!)}&currency=USD&from={Year - 1}-01-01&to={Year}-12-31", J);
            Assert.Equal(200m, st.GetProperty("closing").GetDecimal());
            var pdf = await admin.GetAsync($"/api/v1/accounting/sales/{id}/pdf");
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);

            // a draft from a quote; drafts are deleted, issued documents only voided
            var fromQuoteless = await admin.PutAsJsonAsync("/api/v1/accounting/sales", new { customerId = customer, date = Today, currency = "USD", lines = new object[] { new { description = "Spare", quantity = 2, unitPrice = 5 } } });
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/accounting/sales/{(await OkJson(fromQuoteless)).GetProperty("id").GetGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/v1/accounting/sales/{id}")).StatusCode);

            // the audit trail of the books shows who issued what
            var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/audit", J);
            Assert.Contains(audit.EnumerateArray(), a => a.GetProperty("action").GetString() == "sales.invoice.issue" && a.GetProperty("detail").GetString()!.Contains("INV-000001"));
            Assert.Contains(audit.EnumerateArray(), a => a.GetProperty("what").GetString() == "Invoice posted");

            // a company that fiscalises issues fiscal invoices instead
            var fiscal = await TestKit.NewCompanyAsync(_f, "Fiscal Books Co");
            Assert.False((await fiscal.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/sales/setup", J)).GetProperty("allowed").GetBoolean());
            var theirs = await CustomerAsync(fiscal.Admin, "Someone");
            Assert.Equal(HttpStatusCode.Conflict, (await fiscal.Admin.PutAsJsonAsync("/api/v1/accounting/sales", new { customerId = theirs, date = Today, currency = "USD", lines = new object[] { new { description = "x", quantity = 1, unitPrice = 1 } } })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await fiscal.Admin.GetAsync($"/api/v1/accounting/sales/{id}")).StatusCode);   // never another company's
        }

        [Fact]
        public async Task Balances_customers_and_suppliers_are_brought_in_checked_and_posted_once()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Convert Co");
            var c = co.Admin;
            var date = new DateTime(Year, 1, 31).ToString("yyyy-MM-dd");
            object Bal(string code, decimal dr, decimal cr, string? name = null, string? type = null, string? subType = null) => new { code, name, type, debit = dr, credit = cr, subType };
            object Party(string name, string number, decimal amount) => new { name, number, date = $"{Year}-01-10", dueDate = (string?)null, amount };
            var balances = new[] { Bal("1020", 5000, 0), Bal("1100", 1500, 0), Bal("1200", 700, 0), Bal("1650", 8000, 0, "Motor vehicles", "Asset"), Bal("2010", 0, 900), Bal("2500", 0, 4000, "Bank loan", "Liability", "non_current_liability"), Bal("3010", 0, 10300) };
            var customers = new[] { Party("Moyo Traders", "INV-1", 1000), Party("Chipo's Salon", "INV-2", 500) };
            var suppliers = new[] { Party("Harare Office Supplies", "B-77", 900) };

            // checked first: it must balance, and receivables/payables must agree with their detail
            var bad = await OkJson(await c.PostAsJsonAsync("/api/v1/accounting/conversion/check", new { date, currency = "USD", balances = new[] { Bal("1020", 5000, 0), Bal("3010", 0, 4000) } }));
            Assert.False(bad.GetProperty("canPost").GetBoolean());
            Assert.Contains(bad.GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("does not balance"));
            var mismatch = await OkJson(await c.PostAsJsonAsync("/api/v1/accounting/conversion/check", new { date, currency = "USD", balances, customers = new[] { Party("Moyo Traders", "INV-1", 1000) }, suppliers }));
            Assert.Contains(mismatch.GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("must agree"));
            var ok = await OkJson(await c.PostAsJsonAsync("/api/v1/accounting/conversion/check", new { date, currency = "USD", balances, customers, suppliers }));
            Assert.True(ok.GetProperty("canPost").GetBoolean(), ok.GetProperty("problems").ToString());
            Assert.Equal(2, ok.GetProperty("newAccounts").GetInt32());
            Assert.Equal(2, ok.GetProperty("newCustomers").GetInt32());

            var done = await OkJson(await c.PostAsJsonAsync("/api/v1/accounting/conversion/post", new { date, currency = "USD", balances, customers, suppliers }));
            Assert.Equal(2, done.GetProperty("accountsCreated").GetInt32());
            Assert.Equal(2, done.GetProperty("customerInvoices").GetInt32());
            Assert.Equal(1, done.GetProperty("supplierBills").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/accounting/conversion/post", new { date, currency = "USD", balances, customers, suppliers })).StatusCode);   // only once

            // the trial balance has them, balanced, with the opening equity netting to nothing
            var tb = await c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={date}&from={Year}-01-01", J);
            Assert.True(tb.GetProperty("balanced").GetBoolean());
            decimal Closing(string code) => tb.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("code").GetString() == code).Sum(r => r.GetProperty("closing").GetDecimal());
            Assert.Equal(1500m, Closing("1100")); Assert.Equal(-900m, Closing("2010")); Assert.Equal(8000m, Closing("1650")); Assert.Equal(-4000m, Closing("2500"));
            Assert.DoesNotContain(tb.GetProperty("rows").EnumerateArray(), r => r.GetProperty("name").GetString() == "Opening balance equity" && r.GetProperty("closing").GetDecimal() != 0);
            // a new account shows where it was told to, or like the account just before it (1650 after 1590 accumulated depreciation: a fixed asset)
            var accounts = (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToList();
            Assert.Equal("fixed_asset", accounts.Single(a => a.GetProperty("code").GetString() == "1650").GetProperty("subType").GetString());
            Assert.Equal("non_current_liability", accounts.Single(a => a.GetProperty("code").GetString() == "2500").GetProperty("subType").GetString());

            // each customer invoice and supplier bill can be paid and aged
            var owing = (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/receivables?status=open", J)).EnumerateArray().Single(r => r.GetProperty("invoiceNo").GetString() == "INV-1");
            Assert.Equal("opening", owing.GetProperty("source").GetString()); Assert.Equal(1000m, owing.GetProperty("outstanding").GetDecimal());
            var bill = (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/bills?status=open", J)).EnumerateArray().Single(b => b.GetProperty("number").GetString() == "B-77");
            Assert.Equal(900m, bill.GetProperty("outstanding").GetDecimal());
            Assert.Single((await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/conversion", J)).EnumerateArray());

            // files: an Excel template to fill in, and a CSV read back into rows
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", (await c.GetAsync("/api/v1/accounting/conversion/template/balances")).Content.Headers.ContentType?.MediaType);
            var form = new MultipartFormDataContent();
            var csv = new ByteArrayContent(Encoding.UTF8.GetBytes("Account code,Account name,Debit,Credit\n1020,Bank,\"1,250.50\",\n2010,Accounts payable,,300\nTotal,,1250.50,300\n"));
            csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            form.Add(csv, "file", "tb.csv");
            var read = await OkJson(await c.PostAsync("/api/v1/accounting/conversion/read/balances", form));
            var rows = read.GetProperty("balances").EnumerateArray().ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(1250.50m, rows[0].GetProperty("debit").GetDecimal()); Assert.Equal(300m, rows[1].GetProperty("credit").GetDecimal());
        }

        [Fact]
        public async Task Currencies_combine_prepayments_release_and_the_statements_agree()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Standards Co");
            var c = co.Admin;
            var chart = await ChartAsync(c);
            object Line(Guid a, decimal dr, decimal cr) => new { accountId = a, debit = dr, credit = cr, description = (string?)null };
            await OkJson(await c.PostAsJsonAsync("/api/v1/accounting/journals", new { date = $"{Year}-01-10", currency = "USD", memo = "Capital", lines = new[] { Line(chart["1020"], 1000, 0), Line(chart["3010"], 0, 1000) } }));
            await OkJson(await c.PostAsJsonAsync("/api/v1/accounting/journals", new { date = $"{Year}-02-01", currency = "ZWG", memo = "Sale in ZiG", lines = new[] { Line(chart["1020"], 26000, 0), Line(chart["4090"], 0, 26000) } }));

            // without rates the combined view says what is missing; with them it balances, the difference in a translation reserve
            Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/v1/accounting/reports/balance-sheet?currency=ALL&asAt={Year}-03-31")).StatusCode);
            await OkJson(await c.PutAsJsonAsync("/api/v1/accounting/rates", new { currency = "ZWG", date = $"{Year}-01-01", rate = 0.04 }));
            var rates = await OkJson(await c.PutAsJsonAsync("/api/v1/accounting/rates", new { currency = "ZWG", date = $"{Year}-03-01", rate = 0.03 }));
            Assert.Equal("USD", rates.GetProperty("baseCurrency").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/accounting/rates", new { currency = "USD", date = $"{Year}-01-01", rate = 1 })).StatusCode);
            var bs = await c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/balance-sheet?currency=ALL&asAt={Year}-03-31", J);
            Assert.Equal("USD", bs.GetProperty("currency").GetString());
            Assert.True(bs.GetProperty("balanced").GetBoolean());
            Assert.Equal(1780m, bs.GetProperty("totalAssets").GetDecimal());   // 1,000 USD + 26,000 ZiG at 0.03
            var reserve = bs.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "equity").GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Foreign currency translation reserve");
            Assert.Equal(-260m, reserve.GetProperty("amount").GetDecimal());   // the sale was worth 1,040 on its day
            var pl = await c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/profit-loss?currency=ALL&from={Year}-01-01&to={Year}-03-31", J);
            Assert.Equal(1040m, pl.GetProperty("netProfit").GetDecimal());
            Assert.True((await c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=ALL&asAt={Year}-03-31", J)).GetProperty("balanced").GetBoolean());

            // a year's insurance paid in advance, released month by month once each month has ended
            var start = new DateTime(Today.Year, Today.Month, 1).AddMonths(-3);
            var sch = await OkJson(await c.PutAsJsonAsync("/api/v1/accounting/schedules", new { name = "Vehicle insurance", kind = "prepayment", currency = "USD", amount = 1200, startMonth = start.ToString("yyyy-MM-dd"), months = 12, balanceAccountId = chart["1300"], pnlAccountId = chart["5190"] }));
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/accounting/schedules", new { name = "Wrong way", kind = "prepayment", currency = "USD", amount = 100, startMonth = start, months = 2, balanceAccountId = chart["5190"], pnlAccountId = chart["1300"] })).StatusCode);
            Assert.Equal(3, (await OkJson(await c.PostAsync("/api/v1/accounting/schedules/run", null))).GetProperty("posted").GetInt32());
            Assert.Equal(0, (await OkJson(await c.PostAsync("/api/v1/accounting/schedules/run", null))).GetProperty("posted").GetInt32());
            var after = (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/schedules", J)).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == sch.GetProperty("id").GetGuid());
            Assert.Equal(300m, after.GetProperty("released").GetDecimal()); Assert.Equal(900m, after.GetProperty("remaining").GetDecimal());

            // changes in equity end where the balance sheet's equity does
            var soce = await c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/changes-in-equity?currency=USD&from={Year}-01-01&to={Today:yyyy-MM-dd}", J);
            var usdBs = await c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/balance-sheet?currency=USD&asAt={Today:yyyy-MM-dd}", J);
            Assert.Equal(usdBs.GetProperty("totalEquity").GetDecimal(), soce.GetProperty("total").GetProperty("closing").GetDecimal());
            Assert.Contains(soce.GetProperty("rows").EnumerateArray(), r => r.GetProperty("component").GetString() == "Owner's capital" && r.GetProperty("other").GetDecimal() == 1000);

            // the accounting policies can be reworded, and the annual financial statements come as one PDF
            Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/policies", J)).GetProperty("isStandard").GetBoolean());
            Assert.False((await OkJson(await c.PutAsJsonAsync("/api/v1/accounting/policies", new { text = "Basis of preparation\nOur own words." }))).GetProperty("isStandard").GetBoolean());
            var pack = await c.GetAsync($"/api/v1/accounting/reports/financial-statements?currency=USD&from={Year}-01-01&to={Year}-12-31");
            Assert.Equal(HttpStatusCode.OK, pack.StatusCode);
            Assert.Equal("application/pdf", pack.Content.Headers.ContentType?.MediaType);
            Assert.True((await pack.Content.ReadAsByteArrayAsync()).Length > 5000);
        }
    }
}
