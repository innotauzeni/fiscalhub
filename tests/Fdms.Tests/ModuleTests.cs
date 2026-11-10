using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Fdms.Tests
{
    /// <summary>The fiscalisation modules: customers, items, reports and the compliance centre.</summary>
    public class ModuleTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 23000;
        public ModuleTests(AccessFactory f) => _f = f;

        private static object Customer(string name = "Zeta Traders", string? tin = "2000123456", string? vat = "220054321", string? email = "ap@zeta.co.zw") =>
            new { name, tin, vatNumber = vat, email, phone = "0771234567", address = "1 Main St, Harare", isActive = true };

        [Fact]
        public async Task Customers_are_validated_searchable_private_to_the_company_and_for_staff_who_may_manage_them()
        {
            var a = await TestKit.NewCompanyAsync(_f, "Cust A"); var b = await TestKit.NewCompanyAsync(_f, "Cust B");

            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/customers", Customer(name: "Z"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/customers", Customer(tin: "12345"))).StatusCode);        // a TIN is 10 digits
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/customers", Customer(vat: "12"))).StatusCode);          // a VAT number is 9 digits
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/customers", Customer(email: "nope"))).StatusCode);

            var saved = await a.Admin.PutAsJsonAsync("/api/v1/customers", Customer());
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var id = (await saved.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await a.Admin.PutAsJsonAsync("/api/v1/customers", Customer(name: "Other name"))).StatusCode);   // same TIN twice
            Assert.Equal(HttpStatusCode.OK, (await b.Admin.PutAsJsonAsync("/api/v1/customers", Customer())).StatusCode);                          // another company may hold the same buyer

            // search, update, hide, delete
            Assert.Single((await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/customers?search=zeta", J)).EnumerateArray());
            Assert.Empty((await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/customers?search=nobody", J)).EnumerateArray());
            Assert.Equal(HttpStatusCode.OK, (await a.Admin.PutAsJsonAsync("/api/v1/customers", new { id, name = "Zeta Traders (Pvt) Ltd", tin = "2000123456", isActive = false })).StatusCode);
            Assert.Empty((await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/customers", J)).EnumerateArray());                                   // inactive ones are hidden...
            Assert.Single((await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/customers?includeInactive=true", J)).EnumerateArray());            // ...until asked for

            // another company can neither change nor delete it
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PutAsJsonAsync("/api/v1/customers", new { id, name = "Hijack", isActive = true })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.DeleteAsync($"/api/v1/customers/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await a.Admin.DeleteAsync($"/api/v1/customers/{id}")).StatusCode);

            // a read-only viewer cannot manage customers (but does see the reports)
            var email = TestKit.Unique("viewer") + "@mod.test";
            Assert.True((await a.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Vi", surname = "Ewer", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var viewer = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/customers")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/products")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/compliance")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/reports/sales?from={DateTime.UtcNow:yyyy-MM-dd}&to={DateTime.UtcNow:yyyy-MM-dd}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/v1/customers")).StatusCode);
        }

        [Fact]
        public async Task Items_keep_a_valid_hs_code_and_tax_and_are_private_to_the_company()
        {
            var a = await TestKit.NewCompanyAsync(_f, "Item A"); var b = await TestKit.NewCompanyAsync(_f, "Item B");
            object Item(string name = "Bread 700g", string? code = "BR-700", string? hs = "19059010", decimal price = 1.15m, decimal? tax = 15, bool exempt = false) =>
                new { code, name, hsCode = hs, unit = "each", unitPrice = price, currency = "usd", taxPercent = tax, isExempt = exempt, isActive = true };

            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/products", Item(hs: "1905901"))).StatusCode);      // 7 digits: ZIMRA rejects it (RCPT048)
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/products", Item(price: -1))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/products", Item(tax: 150))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/products", Item(name: "X"))).StatusCode);
            foreach (var ok in new[] { "1905", "190590", "19059010" }) Assert.Equal(HttpStatusCode.OK, (await a.Admin.PutAsJsonAsync("/api/v1/products", Item(name: "Item " + ok, code: "C" + ok, hs: ok))).StatusCode);   // 4, 6 and 8 digits

            var res = await a.Admin.PutAsJsonAsync("/api/v1/products", Item());
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var p = await res.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("USD", p.GetProperty("currency").GetString());                                                                       // the currency is normalised
            Assert.Equal(HttpStatusCode.Conflict, (await a.Admin.PutAsJsonAsync("/api/v1/products", Item(name: "Other", code: "BR-700"))).StatusCode);   // item codes are unique per company
            Assert.Equal(HttpStatusCode.OK, (await b.Admin.PutAsJsonAsync("/api/v1/products", Item())).StatusCode);                          // ...but not across companies

            // an exempt item carries no rate
            var ex = await (await a.Admin.PutAsJsonAsync("/api/v1/products", Item(name: "Unprocessed milk", code: "MILK", hs: "04012000", tax: 15, exempt: true))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.True(ex.GetProperty("isExempt").GetBoolean()); Assert.False(ex.TryGetProperty("taxPercent", out var tp) && tp.ValueKind == JsonValueKind.Number);

            Assert.Single((await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/products?search=BR-700", J)).EnumerateArray());
            Assert.Single((await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/products?search=04012000", J)).EnumerateArray());
            var id = p.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.DeleteAsync($"/api/v1/products/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await a.Admin.DeleteAsync($"/api/v1/products/{id}")).StatusCode);
        }

        private async Task<(TestKit.Company Co, Guid Device, TestKit.Token Token)> SellAsync(string name)
        {
            var co = await TestKit.NewCompanyAsync(_f, name);
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-R");
            var token = await TestKit.NewTokenAsync(_f, co.Admin, dev);
            object Lines(decimal price, int taxId = 3) => new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = price, taxId } };
            var a = await token.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new { externalRef = "R1", invoiceNo = "INV-R1", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, lines = Lines(115m) });
            var b = await token.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new { externalRef = "R2", invoiceNo = "INV-R2", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, lines = Lines(230m) });
            Assert.Equal(HttpStatusCode.Created, a.StatusCode); Assert.Equal(HttpStatusCode.Created, b.StatusCode);
            var idA = (await a.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetGuid();
            var cn = await token.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new { externalRef = "C1", invoiceNo = "CN-R1", receiptType = "CreditNote", currency = "USD", linesTaxInclusive = true, notes = "Returned", originalReceiptId = idA, lines = Lines(115m) });
            Assert.Equal(HttpStatusCode.Created, cn.StatusCode);
            return (co, dev, token);
        }

        private static string Range() => $"from={DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}";

        [Fact]
        public async Task The_sales_report_nets_credit_notes_splits_vat_by_rate_and_exports_csv()
        {
            var (co, dev, token) = await SellAsync("Report Co");
            var r = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/reports/sales?{Range()}", J);
            var usd = r.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("currency").GetString() == "USD");
            Assert.Equal(2, usd.GetProperty("invoices").GetInt32()); Assert.Equal(345m, usd.GetProperty("invoicesTotal").GetDecimal());
            Assert.Equal(1, usd.GetProperty("creditNotes").GetInt32()); Assert.Equal(115m, usd.GetProperty("creditNotesTotal").GetDecimal());
            Assert.Equal(230m, usd.GetProperty("netSales").GetDecimal());
            var tax = usd.GetProperty("taxes").EnumerateArray().Single();
            Assert.Equal("15%", tax.GetProperty("label").GetString());
            Assert.Equal(230m, tax.GetProperty("sales").GetDecimal()); Assert.Equal(30m, tax.GetProperty("tax").GetDecimal());   // 345 - 115 of sales, 45 - 15 of VAT
            Assert.Equal(30m, usd.GetProperty("netTax").GetDecimal());
            Assert.Equal(230m, usd.GetProperty("payments").EnumerateArray().Sum(p => p.GetProperty("amount").GetDecimal()));
            Assert.Equal(3, r.GetProperty("counts").GetProperty("counted").GetInt32());
            Assert.Equal(230m, r.GetProperty("days").EnumerateArray().Sum(d => d.GetProperty("net").GetDecimal()));

            // one device, a wrong device, a bad range
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync($"/api/v1/reports/sales?{Range()}&deviceId={dev}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await co.Admin.GetAsync($"/api/v1/reports/sales?{Range()}&deviceId={Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.GetAsync("/api/v1/reports/sales?from=2026-02-01&to=2026-01-01")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.GetAsync("/api/v1/reports/sales?from=2020-01-01&to=2026-01-01")).StatusCode);

            // CSV: a header and one line per day, device, currency, document and tax rate
            var csv = await (await co.Admin.GetAsync($"/api/v1/reports/sales.csv?{Range()}")).Content.ReadAsStringAsync();
            var lines = csv.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            Assert.Equal("Date,Device,Currency,Document,Tax,Documents,Sales incl tax,Tax amount", lines[0]);
            Assert.Contains(lines, l => l.Contains("FiscalInvoice") && l.Contains("15%") && l.EndsWith("345.00,45.00"));
            Assert.Contains(lines, l => l.Contains("CreditNote") && l.EndsWith("-115.00,-15.00"));

            // fiscal-day totals come from the counters
            var days = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/reports/fiscal-days?{Range()}", J)).EnumerateArray().ToList();
            Assert.Single(days);
            Assert.True(days[0].GetProperty("totals").GetArrayLength() > 0);
            Assert.Contains("SaleByTax", await (await co.Admin.GetAsync($"/api/v1/reports/fiscal-days.csv?{Range()}")).Content.ReadAsStringAsync());

            // another company sees nothing of it; a device token cannot read company reports
            var other = await TestKit.NewCompanyAsync(_f, "Report Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/reports/sales?{Range()}", J)).GetProperty("currencies").EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await token.Http.GetAsync($"/api/v1/reports/sales?{Range()}")).StatusCode);
        }

        [Fact]
        public async Task The_compliance_centre_flags_an_expiring_certificate_an_open_day_and_waiting_receipts()
        {
            var (co, dev, token) = await SellAsync("Compliance Co");
            var clean = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/compliance", J);
            Assert.InRange(clean.GetProperty("score").GetInt32(), 0, 100);
            var ids = clean.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();
            Assert.Contains($"cert:{dev}", ids); Assert.Contains($"day:{dev}", ids); Assert.Contains($"pending:{dev}", ids); Assert.Contains($"rejected:{dev}", ids);
            Assert.All(clean.GetProperty("checks").EnumerateArray(), c => Assert.Contains(c.GetProperty("status").GetString(), new[] { "ok", "info", "warn", "critical" }));

            // the certificate is about to expire, and receipts are waiting
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
                db.Devices.First(d => d.Id == dev).CertificateValidTill = DateTime.UtcNow.AddDays(3);
                var r = db.Receipts.First(x => x.DeviceId == dev); r.Status = Fdms.Domain.Entities.ReceiptStatus.Pending; r.CreatedAt = DateTime.UtcNow.AddHours(-30);
                db.SaveChanges();
            }
            var after = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/compliance", J);
            JsonElement Check(string id) => after.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == id);
            Assert.Equal("critical", Check($"cert:{dev}").GetProperty("status").GetString());
            Assert.Equal("critical", Check($"pending:{dev}").GetProperty("status").GetString());
            Assert.Equal("critical", after.GetProperty("status").GetString());
            Assert.True(after.GetProperty("score").GetInt32() < clean.GetProperty("score").GetInt32());
            Assert.Equal("critical", after.GetProperty("checks")[0].GetProperty("status").GetString());   // worst first

            Assert.Equal(HttpStatusCode.Forbidden, (await token.Http.GetAsync("/api/v1/compliance")).StatusCode);
        }
    }
}
