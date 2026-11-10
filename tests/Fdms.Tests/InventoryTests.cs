using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>Inventory: GRVs, transfers, adjustments and stock takes, sales taking stock out, imports and templates, and every one of them agreeing with the books.</summary>
    public class InventoryTests : IClassFixture<AiFactory>
    {
        private readonly AiFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 61000;
        public InventoryTests(AiFactory f) => _f = f;

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        private static async Task<JsonElement> OkJson(HttpResponseMessage res)
        {
            Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
            return await res.Content.ReadFromJsonAsync<JsonElement>(J);
        }

        private static async Task<Guid> Id(HttpResponseMessage res) => (await OkJson(res)).GetProperty("id").GetGuid();

        private static async Task<JsonElement> Item(HttpClient c, Guid id) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/inventory/items?includeInactive=true", J)).EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);

        private static decimal Level(JsonElement item, string code) => item.GetProperty("levels").EnumerateArray().Where(l => l.GetProperty("location").GetString() == code).Sum(l => l.GetProperty("quantity").GetDecimal());

        private static async Task<JsonElement> PostDoc(HttpClient c, object body)
        {
            var id = await Id(await c.PutAsJsonAsync("/api/v1/inventory/documents", body));
            return await OkJson(await c.PostAsync($"/api/v1/inventory/documents/{id}/post", null));
        }

        private static async Task<decimal> Balance(HttpClient c, string code) =>
            (await c.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/reports/trial-balance?currency=USD&asAt={Today:yyyy-MM-dd}&from=2000-01-01", J)).GetProperty("rows").EnumerateArray()
                .Where(r => r.GetProperty("code").GetString() == code || r.GetProperty("name").GetString() == code).Sum(r => r.GetProperty("closing").GetDecimal());

        [Fact]
        public async Task GRVs_transfers_adjustments_and_stock_takes_move_stock_at_average_cost_and_post_to_the_books()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Stock Co");
            var c = co.Admin;
            var supplier = await Id(await c.PutAsJsonAsync("/api/v1/accounting/suppliers", new { name = "Harare Cement Ltd", termsDays = 30, isActive = true }));
            var main = await Id(await c.PutAsJsonAsync("/api/v1/inventory/locations", new { code = "MAIN", name = "Main store", isDefault = true, isActive = true }));
            var byo = await Id(await c.PutAsJsonAsync("/api/v1/inventory/locations", new { code = "BYO", name = "Bulawayo shop", isDefault = false, isActive = true }));
            var building = await Id(await c.PutAsJsonAsync("/api/v1/inventory/categories", new { name = "Building", isActive = true }));
            var cement = await Id(await c.PutAsJsonAsync("/api/v1/inventory/categories", new { parentId = building, name = "Cement", isActive = true }));
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/v1/inventory/categories", new { parentId = cement, name = "Too deep", isActive = true })).StatusCode);
            var item = await Id(await c.PutAsJsonAsync("/api/v1/inventory/items", new { code = "CEM-50", name = "Cement 50kg", unit = "bag", categoryId = cement, unitPrice = 12.5, currency = "USD", isExempt = false, isActive = true, trackStock = true, reorderLevel = 250, reorderQuantity = 200 }));
            Assert.Equal("Building / Cement", (await Item(c, item)).GetProperty("category").GetString());

            // goods received: stock in, inventory and VAT against the supplier, and a bill to pay
            var grv1 = await PostDoc(c, new { kind = "grv", date = Today, locationId = main, supplierId = supplier, supplierReference = "HC-7781", lines = new[] { new { productId = item, quantity = 100, unitCost = 9, vatPercent = 15 } } });
            Assert.Equal("GRV-000001", grv1.GetProperty("number").GetString());
            Assert.Equal(1035m, grv1.GetProperty("total").GetDecimal());
            var bill = (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/bills?status=open", J)).EnumerateArray().Single(b => b.GetProperty("number").GetString() == "HC-7781");
            Assert.Equal(1035m, bill.GetProperty("total").GetDecimal());
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/v1/accounting/bills/{bill.GetProperty("id").GetGuid()}/void", null)).StatusCode);   // voided with its GRV only
            var grv2 = await PostDoc(c, new { kind = "grv", date = Today, locationId = main, supplierId = supplier, supplierReference = "HC-7790", lines = new[] { new { productId = item, quantity = 100, unitCost = 11, vatPercent = 15 } } });
            var it = await Item(c, item);
            Assert.Equal(200m, it.GetProperty("onHand").GetDecimal()); Assert.Equal(10m, it.GetProperty("averageCost").GetDecimal());

            // a transfer moves stock at average cost, never more than is there
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/v1/inventory/documents/{await Id(await c.PutAsJsonAsync("/api/v1/inventory/documents", new { kind = "transfer", date = Today, locationId = byo, toLocationId = main, lines = new[] { new { productId = item, quantity = 5 } } }))}/post", null)).StatusCode);
            await PostDoc(c, new { kind = "transfer", date = Today, locationId = main, toLocationId = byo, lines = new[] { new { productId = item, quantity = 50 } } });
            it = await Item(c, item);
            Assert.Equal(150m, Level(it, "MAIN")); Assert.Equal(50m, Level(it, "BYO")); Assert.Equal(10m, it.GetProperty("averageCost").GetDecimal());

            // damaged bags written off, and a stock take that found two missing
            await PostDoc(c, new { kind = "adjustment", mode = "adjust", date = Today, locationId = byo, reason = "Water damage", lines = new[] { new { productId = item, quantity = -5 } } });
            var take = await PostDoc(c, new { kind = "adjustment", mode = "count", date = Today, locationId = main, reason = "Month-end count", lines = new[] { new { productId = item, quantity = 0, counted = 148 } } });
            var line = take.GetProperty("lines")[0];
            Assert.Equal(150m, line.GetProperty("expected").GetDecimal()); Assert.Equal(-2m, line.GetProperty("quantity").GetDecimal());
            Assert.Equal("ADJ-000002", take.GetProperty("number").GetString());

            // the stock's value and the books agree
            var onHand = await c.GetFromJsonAsync<JsonElement>("/api/v1/inventory/reports/on-hand", J);
            Assert.Equal(193m, onHand.GetProperty("totalQuantity").GetDecimal()); Assert.Equal(1930m, onHand.GetProperty("totalValue").GetDecimal());
            Assert.Equal(1930m, onHand.GetProperty("ledgerValue").GetDecimal()); Assert.Equal(0m, onHand.GetProperty("difference").GetDecimal());
            Assert.Equal(1930m, await Balance(c, "1200"));
            Assert.Equal(70m, await Balance(c, "Stock adjustments and write-offs"));       // 50 written off + 20 short
            Assert.Equal(300m, await Balance(c, "1310"));      // VAT input on both GRVs: 135 + 165
            Assert.Equal(-2300m, await Balance(c, "2010"));    // owed to the supplier: 1,035 + 1,265
            var reorder = await c.GetFromJsonAsync<JsonElement>("/api/v1/inventory/reports/reorder", J);
            Assert.Contains(reorder.EnumerateArray(), r => r.GetProperty("productId").GetGuid() == item && r.GetProperty("suggested").GetDecimal() == 200 && r.GetProperty("lastSupplier").GetString() == "Harare Cement Ltd");
            var card = await c.GetFromJsonAsync<JsonElement>($"/api/v1/inventory/reports/card?productId={item}&from={Today.AddDays(-1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}", J);
            Assert.Equal(193m, card.GetProperty("closingQuantity").GetDecimal()); Assert.Equal(6, card.GetProperty("lines").GetArrayLength());

            // voiding the second GRV takes its stock back out at its own cost, and voids its bill
            var voided = await OkJson(await c.PostAsync($"/api/v1/inventory/documents/{grv2.GetProperty("id").GetGuid()}/void", null));
            Assert.Equal("Void", voided.GetProperty("status").GetString());
            it = await Item(c, item);
            Assert.Equal(93m, it.GetProperty("onHand").GetDecimal()); Assert.Equal(830m, it.GetProperty("value").GetDecimal(), 0);
            Assert.Equal(830m, await Balance(c, "1200"));
            Assert.DoesNotContain((await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/bills?status=open", J)).EnumerateArray(), b => b.GetProperty("number").GetString() == "HC-7790");
            Assert.Equal("application/pdf", (await c.GetAsync($"/api/v1/inventory/documents/{grv1.GetProperty("id").GetGuid()}/pdf")).Content.Headers.ContentType?.MediaType);
            Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/v1/products/{item}")).StatusCode);   // stock history keeps the item
        }

        [Fact]
        public async Task Fiscal_receipts_and_invoices_from_the_books_take_stock_out_and_post_cost_of_sales()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Till Stock Co");
            var c = co.Admin;
            var (dev, _) = await TestKit.RegisterDeviceAsync(c, Interlocked.Increment(ref _seq), "SN-INV");
            var item = await Id(await c.PutAsJsonAsync("/api/v1/inventory/items", new { name = "Test item", hsCode = "85456852", unitPrice = 11.5, currency = "USD", isExempt = false, isActive = true, trackStock = true, reorderLevel = 0, reorderQuantity = 0 }));
            var loc = await Id(await c.PutAsJsonAsync("/api/v1/inventory/locations", new { code = "SHOP", name = "Shop", isDefault = true, isActive = true }));
            await PostDoc(c, new { kind = "opening", date = Today.AddDays(-1), locationId = loc, lines = new[] { new { productId = item, quantity = 10, unitCost = 4 } } });
            Assert.Equal(10m, (await Item(c, item)).GetProperty("onHand").GetDecimal());

            // the till sells one, then credits it back
            var sale = await TestKit.IssueAsync(c, dev, "USD", "FiscalInvoice", "INV-STK-1");
            Assert.Equal(9m, (await Item(c, item)).GetProperty("onHand").GetDecimal());
            var cogs = (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?source=cogs&from=2000-01-01&to=2100-01-01", J)).EnumerateArray().ToList();
            Assert.Single(cogs);
            Assert.Contains(cogs[0].GetProperty("lines").EnumerateArray(), l => l.GetProperty("accountCode").GetString() == "5010" && l.GetProperty("debit").GetDecimal() == 4);
            await TestKit.IssueAsync(c, dev, "USD", "CreditNote", "CN-STK-1", sale);
            Assert.Equal(10m, (await Item(c, item)).GetProperty("onHand").GetDecimal());
            Assert.Equal(0m, (await c.GetFromJsonAsync<JsonElement>("/api/v1/inventory/reports/on-hand", J)).GetProperty("difference").GetDecimal());
            Assert.Equal(2, (await c.GetFromJsonAsync<JsonElement>("/api/v1/inventory/documents?kind=sale,return", J)).GetArrayLength());
            Assert.Equal(0m, await Balance(c, "5010"));   // sold and credited: no cost left
            Assert.Equal(40m, await Balance(c, "1200"));
        }

        [Fact]
        public async Task Invoices_from_the_books_take_stock_out_and_a_void_puts_it_back()
        {
            var op = await TestKit.AdminAsync(_f);
            var co = await TestKit.NewCompanyAsync(_f, "Books Stock Co");
            var plan = (await op.GetFromJsonAsync<JsonElement>("/api/v1/plans", J)).EnumerateArray().First(p => p.GetProperty("code").GetString() == "books");
            Assert.Contains("inventory", plan.GetProperty("modules").EnumerateArray().Select(m => m.GetString()));
            await OkJson(await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = plan.GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" }));
            var c = await TestKit.LoginAsync(_f, co.AdminEmail, TestKit.UserPassword);
            var item = await Id(await c.PutAsJsonAsync("/api/v1/inventory/items", new { code = "CH-1", name = "Office chair", unitPrice = 85, currency = "USD", isExempt = true, isActive = true, trackStock = true, reorderLevel = 0, reorderQuantity = 0 }));
            var loc = await Id(await c.PutAsJsonAsync("/api/v1/inventory/locations", new { code = "WH", name = "Warehouse", isDefault = true, isActive = true }));
            await PostDoc(c, new { kind = "opening", date = Today, locationId = loc, lines = new[] { new { productId = item, quantity = 10, unitCost = 40 } } });
            var customer = await Id(await c.PutAsJsonAsync("/api/v1/customers", new { name = "Avondale Offices", isActive = true }));
            var inv = await Id(await c.PutAsJsonAsync("/api/v1/accounting/sales", new { customerId = customer, date = Today, currency = "USD", lines = new[] { new { productId = item, description = "Office chair", quantity = 3, unitPrice = 85 } } }));
            await OkJson(await c.PostAsync($"/api/v1/accounting/sales/{inv}/issue", null));
            Assert.Equal(7m, (await Item(c, item)).GetProperty("onHand").GetDecimal());
            Assert.Equal(120m, await Balance(c, "5010"));
            await OkJson(await c.PostAsync($"/api/v1/accounting/sales/{inv}/void", null));
            Assert.Equal(10m, (await Item(c, item)).GetProperty("onHand").GetDecimal());
            Assert.Equal(0m, await Balance(c, "5010"));
            Assert.Equal(400m, await Balance(c, "1200"));
        }

        [Fact]
        public async Task Items_import_with_categories_subcategories_locations_and_opening_stock_and_lines_come_from_files()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Import Stock Co");
            var c = co.Admin;
            object Row(string code, string name, string cat, string? sub, decimal? cost, string? loc, decimal? qty) =>
                new { code, name, category = cat, subcategory = sub, unit = "each", sellingPrice = 10, currency = "USD", taxPercent = 15, cost, reorderLevel = 5, location = loc, openingQuantity = qty };
            var rows = new[] { Row("A1", "Hammer", "Hardware", "Tools", 6, "MAIN", 20), Row("A2", "Spanner set", "Hardware", "Tools", 15, "Harare Depot", 4), Row("B1", "Bucket", "Household", null, 2.5m, null, null) };
            var date = Today.AddDays(-2).ToString("yyyy-MM-dd");
            var bad = await OkJson(await c.PostAsJsonAsync("/api/v1/inventory/import/items/check", new { date, rows = new[] { Row("Z1", "Bad", "X", null, null, "MAIN", 3) } }));
            Assert.False(bad.GetProperty("canPost").GetBoolean());   // opening stock without a cost
            var check = await OkJson(await c.PostAsJsonAsync("/api/v1/inventory/import/items/check", new { date, rows }));
            Assert.True(check.GetProperty("canPost").GetBoolean(), check.GetProperty("problems").ToString());
            Assert.Equal(3, check.GetProperty("newItems").GetInt32()); Assert.Equal(2, check.GetProperty("newCategories").GetInt32()); Assert.Equal(1, check.GetProperty("newSubcategories").GetInt32());
            Assert.Equal(2, check.GetProperty("newLocations").GetInt32()); Assert.Equal(180m, check.GetProperty("openingValue").GetDecimal());
            var done = await OkJson(await c.PostAsJsonAsync("/api/v1/inventory/import/items", new { date, rows }));
            Assert.Equal(3, done.GetProperty("created").GetInt32()); Assert.Equal(2, done.GetProperty("openingDocuments").GetArrayLength());
            var cats = await c.GetFromJsonAsync<JsonElement>("/api/v1/inventory/categories", J);
            Assert.Contains(cats.EnumerateArray(), x => x.GetProperty("path").GetString() == "Hardware / Tools" && x.GetProperty("items").GetInt32() == 2);
            var onHand = await c.GetFromJsonAsync<JsonElement>("/api/v1/inventory/reports/on-hand", J);
            Assert.Equal(180m, onHand.GetProperty("totalValue").GetDecimal()); Assert.Equal(0m, onHand.GetProperty("difference").GetDecimal());
            // a second import updates by code instead of duplicating
            var again = await OkJson(await c.PostAsJsonAsync("/api/v1/inventory/import/items/check", new { date, rows = new[] { Row("A1", "Claw hammer", "Hardware", "Tools", null, null, null) } }));
            Assert.Equal(0, again.GetProperty("newItems").GetInt32()); Assert.Equal(1, again.GetProperty("updatedItems").GetInt32());

            // the templates, a count sheet, and lines read from a file
            foreach (var kind in new[] { "items", "grv", "transfer", "adjustment" })
                Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/inventory/templates/" + kind)).StatusCode);
            var main = (await c.GetFromJsonAsync<JsonElement>("/api/v1/inventory/locations", J)).EnumerateArray().First(l => l.GetProperty("code").GetString() == "MAIN").GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/inventory/templates/count?locationId={main}")).StatusCode);
            var form = new MultipartFormDataContent();
            var csv = new ByteArrayContent(Encoding.UTF8.GetBytes("Code,Name,Quantity,Unit cost,VAT %\nA1,,12,6.50,15\n,Bucket,30,2.40,15\nNOPE,,1,1,0\n"));
            csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv"); form.Add(csv, "file", "grv.csv");
            var lines = await OkJson(await c.PostAsync("/api/v1/inventory/import/lines/grv", form));
            Assert.Equal(2, lines.GetProperty("lines").GetArrayLength()); Assert.Single(lines.GetProperty("problems").EnumerateArray());
            Assert.Equal(6.5m, lines.GetProperty("lines")[0].GetProperty("unitCost").GetDecimal());
        }
    }
}
