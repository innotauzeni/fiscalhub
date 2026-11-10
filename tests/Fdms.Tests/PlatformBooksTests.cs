using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>The operator's own books: billing posts into them by itself (income, VAT, money in, partner commission), and they never mix with a company's.</summary>
    public class PlatformBooksTests : IClassFixture<ExtrasFactory>
    {
        private readonly ExtrasFactory _f;
        public PlatformBooksTests(ExtrasFactory f) => _f = f;

        private async Task<JsonElement[]> EntriesAsync(HttpClient op, string number) =>
            (await op.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/journals?q={number}&from=2000-01-01&to=2100-01-01&take=500", J)).EnumerateArray()
                .Where(e => e.GetProperty("reference").GetString() == number || e.GetProperty("memo").GetString()!.Contains(number)).ToArray();

        private static decimal Line(JsonElement entry, string accountName, string side) =>
            entry.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("accountName").GetString() == accountName).Sum(l => l.GetProperty(side).GetDecimal());

        [Fact]
        public async Task Billing_posts_into_the_platform_books_once_with_payment_and_partner_commission()
        {
            var op = await AdminAsync(_f);
            var anon = _f.CreateClient();
            var planId = (await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/plans", J)).EnumerateArray().First(p => p.GetProperty("code").GetString() == "business").GetProperty("id").GetInt32();
            var email = Unique("books") + "@platbooks.test";
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", new { companyName = Unique("Books Co "), tin = "1234567890", contactName = "Pat", contactSurname = "Moyo", email, phone = "0771234567", password = UserPassword, planId, billingCycle = "monthly" })).StatusCode);
            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications?status=Pending", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            Assert.True((await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/approve", new { trialDays = 0 })).IsSuccessStatusCode);
            var admin = await LoginAsync(_f, email, UserPassword);
            var inv = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").EnumerateArray().First(i => i.GetProperty("status").GetString() == "Open");
            var number = inv.GetProperty("number").GetString()!;
            var total = inv.GetProperty("total").GetDecimal();

            // the company was brought by a partner on 20%
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                var clientId = await db.BillingInvoices.Where(i => i.Number == number).Select(i => i.ClientId).FirstAsync();
                var c = await db.Clients.FirstAsync(x => x.Id == clientId);
                c.PartnerId = Guid.NewGuid(); c.PartnerPercent = 20;
                await db.SaveChangesAsync();
            }

            // issued: the company owes it, the income is in by kind
            Assert.True((await op.PostAsync("/api/v1/accounting/sync", null)).IsSuccessStatusCode);
            var issued = Assert.Single(await EntriesAsync(op, number));
            Assert.Equal("billing", issued.GetProperty("source").GetString());
            Assert.Equal(total, Line(issued, "Accounts receivable", "debit"));
            Assert.True(Line(issued, "Subscription income", "credit") > 0);

            // paid in cash: money in, receivable cleared, the partner's commission is a cost owed to them
            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{inv.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash", reference = Unique("CASH"), notes = (string?)null, sendCode = false })).IsSuccessStatusCode);
            Assert.True((await op.PostAsync("/api/v1/accounting/sync", null)).IsSuccessStatusCode);
            Assert.True((await op.PostAsync("/api/v1/accounting/sync", null)).IsSuccessStatusCode);   // twice: nothing posted twice
            var all = await EntriesAsync(op, number);
            Assert.Equal(3, all.Length);
            var paid = Assert.Single(all, e => e.GetProperty("source").GetString() == "billing_paid");
            Assert.Equal(total, Line(paid, "Accounts receivable", "credit"));
            var commission = Assert.Single(all, e => e.GetProperty("source").GetString() == "commission");
            Assert.Equal(Math.Round((total - issued.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("accountName").GetString()!.Contains("VAT")).Sum(l => l.GetProperty("credit").GetDecimal())) * 0.2m, 2),
                Line(commission, "Partner commissions", "debit"));
            Assert.Equal(Line(commission, "Partner commissions", "debit"), Line(commission, "Partner commissions payable", "credit"));

            // the trial balance of the platform balances, and the company never sees any of it
            var tb = await op.GetFromJsonAsync<JsonElement>("/api/v1/accounting/reports/trial-balance?currency=USD", J);
            Assert.True(tb.GetProperty("balanced").GetBoolean());
            Assert.Contains(tb.GetProperty("rows").EnumerateArray(), r => r.GetProperty("name").GetString() == "Subscription income");
            Assert.True((await admin.PostAsync("/api/v1/accounting/sync", null)).IsSuccessStatusCode);
            Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?from=2000-01-01&to=2100-01-01&take=500", J)).EnumerateArray(), e => e.GetProperty("reference").GetString() == number);
            Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray(), a => a.GetProperty("name").GetString() == "Subscription income");
        }
    }
}
