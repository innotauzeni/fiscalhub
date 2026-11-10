using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>Referral rewards as coins (earned, spent in the shop, never cash), and manual payments (unique reference, PDF or picture only).</summary>
    public class CoinsAndManualPaymentTests : IClassFixture<BillingFactory>
    {
        private readonly BillingFactory _f;
        public CoinsAndManualPaymentTests(BillingFactory f) => _f = f;

        private async Task<(Guid ClientId, HttpClient Admin)> CompanyAsync(HttpClient op, string name, string? code)
        {
            var anon = _f.CreateClient();
            var plans = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/plans", J);
            var planId = plans.EnumerateArray().First(p => p.GetProperty("code").GetString() == "business").GetProperty("id").GetInt32();
            var email = Unique("coin") + "@coins.test";
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", new { companyName = name, tin = "1234567890", contactName = "Zed", contactSurname = "Moyo", email, phone = "0771234567", password = UserPassword, planId, billingCycle = "monthly", partnerCode = code })).StatusCode);
            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications?status=Pending", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            var res = await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/approve", new { trialDays = 0 });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return ((await res.Json()).GetProperty("clientId").GetGuid(), await LoginAsync(_f, email, UserPassword));
        }

        private static async Task<JsonElement> OpenInvoiceAsync(HttpClient admin) =>
            (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").EnumerateArray().First(i => i.GetProperty("status").GetString() == "Open");

        private static async Task<decimal> MarkPaidAsync(HttpClient op, HttpClient admin)
        {
            var inv = await OpenInvoiceAsync(admin);
            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{inv.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash", reference = Unique("CASH"), notes = (string?)null, sendCode = false })).IsSuccessStatusCode);
            return inv.GetProperty("subtotal").GetDecimal();
        }

        [Fact]
        public async Task Coin_rewards_are_credited_spent_in_the_shop_and_never_paid_out_as_money()
        {
            var op = await AdminAsync(_f);
            var saved = await op.PutAsJsonAsync("/api/v1/referral-program", new
            {
                enabled = true, percent = 10, invoicesRewarded = 1, rewardType = "coins", coinsPerUnit = 10,
                tiers = new[] { new { key = "first", label = "First", referrals = 1, freeMonths = 0, perk = "Welcome bonus", bonusCoins = 40 } },
                shop = new object[]
                {
                    new { key = "week", title = "7 extra days", description = "More time", cost = 50, kind = "free_days", value = 7, enabled = true },
                    new { key = "device", title = "1 more device", description = (string?)null, cost = 100000, kind = "extra_device", value = 1, enabled = true },
                    new { key = "invoice", title = "Pay an invoice", description = (string?)null, cost = 0, kind = "pay_invoice", value = 0, enabled = true },
                },
            });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());

            var (aId, a) = await CompanyAsync(op, "Coin Referrer", null);
            await MarkPaidAsync(op, a);
            var code = (await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J)).GetProperty("code").GetString();
            var (_, b) = await CompanyAsync(op, "Coin Referred", code);
            var subtotal = await MarkPaidAsync(op, b);

            // 10% of the invoice before tax, at 10 coins per 1.00, plus the level bonus; looking again never credits twice
            var expected = (int)Math.Round(Math.Round(subtotal * 0.10m, 2) * 10, MidpointRounding.AwayFromZero) + 40;
            JsonElement o = default;
            for (var i = 0; i < 3; i++) o = await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J);
            Assert.Equal("coins", o.GetProperty("rewardType").GetString());
            Assert.Equal(expected, o.GetProperty("coins").GetInt32());
            Assert.Equal("Credited", o.GetProperty("rewards")[0].GetProperty("status").GetString());
            Assert.Empty(o.GetProperty("owed").EnumerateArray());   // nothing to pay out in money
            Assert.Equal(3, o.GetProperty("shop").GetArrayLength());

            // the operator cannot "pay out" a coin reward
            var reward = (await op.GetFromJsonAsync<JsonElement>("/api/v1/referral-program", J)).GetProperty("rewards").EnumerateArray().First(r => r.GetProperty("referrer").GetString() == "Coin Referrer");
            Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync($"/api/v1/referral-program/rewards/{reward.GetProperty("id").GetGuid()}/pay", new { reference = "x" })).StatusCode);

            // spend 50 coins on a week: the subscription runs 7 days longer and the balance drops
            DateTime End() { using var s = _f.Services.CreateScope(); return s.ServiceProvider.GetRequiredService<FdmsDbContext>().Subscriptions.AsNoTracking().First(x => x.ClientId == aId).CurrentPeriodEnd; }
            var before = End();
            var r = await a.PostAsJsonAsync("/api/v1/referrals/redeem", new { key = "week" });
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
            Assert.Equal(expected - 50, (await r.Json()).GetProperty("balance").GetInt32());
            Assert.Equal(before.AddDays(7), End());

            // too expensive, unknown reward
            var poor = await a.PostAsJsonAsync("/api/v1/referrals/redeem", new { key = "device" });
            Assert.Equal(HttpStatusCode.Conflict, poor.StatusCode);
            Assert.Equal("NOT_ENOUGH_COINS", (await poor.Json()).GetProperty("errorCode").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsJsonAsync("/api/v1/referrals/redeem", new { key = "yacht" })).StatusCode);

            // the operator gives coins; a company cannot; then an open invoice is paid with coins (and the plan limit grows with a bought device)
            Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync("/api/v1/referral-program/coins", new { clientId = aId, amount = 1000, note = "me" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync("/api/v1/referral-program/coins", new { clientId = aId, amount = -500_000, note = "more than they have" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/api/v1/referral-program/coins", new { clientId = aId, amount = 99_000_000, note = "too big" })).StatusCode);
            var give = await op.PostAsJsonAsync("/api/v1/referral-program/coins", new { clientId = aId, amount = 1_000_000, note = "Launch gift" });
            Assert.True(give.IsSuccessStatusCode, await give.Content.ReadAsStringAsync());
            Assert.True((await a.PostAsJsonAsync("/api/v1/referrals/redeem", new { key = "device" })).IsSuccessStatusCode);
            using (var s = _f.Services.CreateScope()) Assert.Equal(1, s.ServiceProvider.GetRequiredService<FdmsDbContext>().Clients.AsNoTracking().First(c => c.Id == aId).BonusDevices);

            var renew = await a.PostAsJsonAsync("/api/v1/billing/renew", new { });
            Assert.True(renew.IsSuccessStatusCode, await renew.Content.ReadAsStringAsync());
            var inv = await OpenInvoiceAsync(a);
            var pay = await a.PostAsJsonAsync("/api/v1/referrals/redeem", new { key = "invoice", invoiceId = inv.GetProperty("id").GetGuid() });
            Assert.True(pay.IsSuccessStatusCode, await pay.Content.ReadAsStringAsync());
            var paid = await a.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{inv.GetProperty("id").GetGuid()}", J);
            Assert.Equal("Paid", paid.GetProperty("status").GetString());
            Assert.Equal("Coins", paid.GetProperty("paymentMethod").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/api/v1/referrals/redeem", new { key = "invoice", invoiceId = inv.GetProperty("id").GetGuid() })).StatusCode);

            // bad shop items are refused
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = true, percent = 10, invoicesRewarded = 1, shop = new[] { new { key = "x", title = "X", cost = 0, kind = "free_days", value = 7, enabled = true } } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = true, percent = 10, invoicesRewarded = 1, rewardType = "gold" })).StatusCode);
        }

        private static MultipartFormDataContent Form(string reference, byte[]? file = null, string name = "proof.pdf", string type = "application/pdf")
        {
            var form = new MultipartFormDataContent { { new StringContent(reference), "Reference" } };
            if (file != null) { var f = new ByteArrayContent(file); f.Headers.ContentType = new MediaTypeHeaderValue(type); form.Add(f, "Proof", name); }
            return form;
        }

        [Fact]
        public async Task A_bank_reference_can_be_used_once_and_only_a_pdf_or_picture_is_accepted()
        {
            var op = await AdminAsync(_f);
            var p = JsonSerializer.Deserialize<Dictionary<string, object?>>((await op.GetFromJsonAsync<JsonElement>("/api/v1/settings/billing", J)).GetProperty("profile").GetRawText(), J)!;
            p["bankName"] = "CBZ"; p["accountName"] = "Gateway"; p["accountNumber"] = "0123456789";
            Assert.True((await op.PutAsJsonAsync("/api/v1/settings/billing", p)).IsSuccessStatusCode);
            var (_, a) = await CompanyAsync(op, "Bank Payer A", null);
            var (_, b) = await CompanyAsync(op, "Bank Payer B", null);
            var invA = (await OpenInvoiceAsync(a)).GetProperty("id").GetGuid();
            var invB = (await OpenInvoiceAsync(b)).GetProperty("id").GetGuid();
            var reference = Unique("FT26-");

            // a reference is required
            var none = await a.PostAsync($"/api/v1/billing/invoices/{invA}/pay/bank", Form(""));
            Assert.Equal("REFERENCE_REQUIRED", (await none.Json()).GetProperty("errorCode").GetString());

            // only a real PDF or picture: a text file, or a file named .exe, is refused
            var txt = await a.PostAsync($"/api/v1/billing/invoices/{invA}/pay/bank", Form(reference, "hello"u8.ToArray(), "proof.txt", "text/plain"));
            Assert.Equal("PROOF_TYPE", (await txt.Json()).GetProperty("errorCode").GetString());
            var exe = await a.PostAsync($"/api/v1/billing/invoices/{invA}/pay/bank", Form(reference, "%PDF-1.4 fake"u8.ToArray(), "proof.exe"));
            Assert.Equal("PROOF_TYPE", (await exe.Json()).GetProperty("errorCode").GetString());

            // a JPEG photo is fine
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46 };
            var ok = await a.PostAsync($"/api/v1/billing/invoices/{invA}/pay/bank", Form(reference, jpeg, "slip.jpg", "image/jpeg"));
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());

            // the same reference again (spaces, dashes and case do not matter), from the same or another company: refused with a clear message
            var again = await b.PostAsync($"/api/v1/billing/invoices/{invB}/pay/bank", Form(reference.ToLowerInvariant().Replace("-", " ")));
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            var body = await again.Json();
            Assert.Equal("REFERENCE_USED", body.GetProperty("errorCode").GetString());
            Assert.Contains("already used", body.GetProperty("title").GetString());

            // once the operator rejects it, the reference is free again
            var pid = (await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/billing/payments?status=AwaitingConfirmation", J)).EnumerateArray().First(x => x.GetProperty("reference").GetString() == reference).GetProperty("id").GetGuid();
            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pid}/reject", new { reason = "Not on the statement" })).IsSuccessStatusCode);
            Assert.True((await b.PostAsync($"/api/v1/billing/invoices/{invB}/pay/bank", Form(reference))).IsSuccessStatusCode);
        }
    }
}
