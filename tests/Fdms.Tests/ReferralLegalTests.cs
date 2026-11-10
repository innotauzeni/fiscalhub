using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>Refer-and-earn between companies, and the terms, privacy policy and developer details.</summary>
    public class ReferralLegalTests : IClassFixture<BillingFactory>
    {
        private readonly BillingFactory _f;
        public ReferralLegalTests(BillingFactory f) => _f = f;

        private async Task<(Guid ClientId, HttpClient Admin)> CompanyAsync(HttpClient op, string name, string? code)
        {
            var anon = _f.CreateClient();
            var plans = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/plans", J);
            var planId = plans.EnumerateArray().First(p => p.GetProperty("code").GetString() == "business").GetProperty("id").GetInt32();
            var email = Unique("co") + "@refer.test";
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", new { companyName = name, tin = "1234567890", contactName = "Zed", contactSurname = "Moyo", email, phone = "0771234567", password = UserPassword, planId, billingCycle = "monthly", partnerCode = code })).StatusCode);
            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications?status=Pending", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            var res = await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/approve", new { trialDays = 0 });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var id = (await res.Json()).GetProperty("clientId").GetGuid();
            return (id, await LoginAsync(_f, email, UserPassword));
        }

        /// <summary>The company's first invoice is paid the real way: the operator marks it paid, which also unlocks the account.</summary>
        private async Task<decimal> PayFirstInvoiceAsync(HttpClient op, HttpClient admin)
        {
            var inv = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").EnumerateArray().First(i => i.GetProperty("status").GetString() == "Open");
            var res = await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{inv.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash", reference = "TEST", notes = (string?)null, sendCode = false });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return inv.GetProperty("subtotal").GetDecimal();
        }

        // The company is locked until its first invoice is paid, so the referrer pays its own first (as a real one would) before using the page.
        private async Task<(Guid Id, HttpClient Admin, string Code)> ReferrerAsync(HttpClient op, string name)
        {
            var (id, admin) = await CompanyAsync(op, name, null);
            await PayFirstInvoiceAsync(op, admin);
            var o = await admin.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J);
            return (id, admin, o.GetProperty("code").GetString()!);
        }

        [Fact]
        public async Task A_company_earns_a_share_of_what_the_companies_it_referred_pay_and_the_operator_pays_it_out()
        {
            var op = await AdminAsync(_f);
            Assert.True((await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = true, percent = 10, invoicesRewarded = 1, rewardType = "money" })).IsSuccessStatusCode);   // this test is about money rewards
            var (aId, a, code) = await ReferrerAsync(op, "Referrer One");
            Assert.StartsWith("REF", code);
            var first = await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J);
            Assert.Equal(code, first.GetProperty("code").GetString());   // the same code every time
            Assert.Equal($"/register?ref={code}", first.GetProperty("referralPath").GetString());
            Assert.Equal(0, first.GetProperty("referred").GetInt32());
            Assert.False(first.GetProperty("tiers").EnumerateArray().First().GetProperty("reached").GetBoolean());

            // a company registers with the code; once approved it is the referrer's, and while it has not paid nothing is earned
            var (bId, bAdmin) = await CompanyAsync(op, "Referred Two", code.ToLowerInvariant());
            var o = await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J);
            Assert.Equal(1, o.GetProperty("referred").GetInt32()); Assert.Equal(0, o.GetProperty("paying").GetInt32());
            Assert.Empty(o.GetProperty("rewards").EnumerateArray());

            // it pays: the referrer earns 10% of the invoice before tax, once, however often the page is opened
            var subtotal = await PayFirstInvoiceAsync(op, bAdmin);
            var expected = Math.Round(subtotal * 0.10m, 2);
            for (var i = 0; i < 3; i++) o = await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J);
            Assert.Equal(1, o.GetProperty("rewards").GetArrayLength());
            Assert.Equal(expected, o.GetProperty("rewards")[0].GetProperty("amount").GetDecimal());
            Assert.Equal("Referred Two", o.GetProperty("rewards")[0].GetProperty("company").GetString());
            Assert.Equal(expected, o.GetProperty("owed")[0].GetProperty("amount").GetDecimal());
            Assert.True(o.GetProperty("tiers").EnumerateArray().First().GetProperty("reached").GetBoolean());   // the first tier (one paying company)
            Assert.Equal("Paying", o.GetProperty("companies")[0].GetProperty("status").GetString());

            // the operator sees it and pays it out, once
            var admin = await op.GetFromJsonAsync<JsonElement>("/api/v1/referral-program?status=Earned", J);
            var reward = admin.GetProperty("rewards").EnumerateArray().First(r => r.GetProperty("referrer").GetString() == "Referrer One");
            var rid = reward.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync($"/api/v1/referral-program/rewards/{rid}/pay", new { reference = "x" })).StatusCode);
            Assert.True((await op.PostAsJsonAsync($"/api/v1/referral-program/rewards/{rid}/pay", new { reference = "EFT-77" })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync($"/api/v1/referral-program/rewards/{rid}/pay", new { reference = "again" })).StatusCode);
            o = await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J);
            Assert.Equal("Paid", o.GetProperty("rewards")[0].GetProperty("status").GetString());
            Assert.Empty(o.GetProperty("owed").EnumerateArray());

            // nobody else can read this company's page, and a stranger's page is empty
            var (_, other, _) = await ReferrerAsync(op, "Referrer Stranger");
            Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J)).GetProperty("referred").GetInt32());
            Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v1/referrals")).StatusCode);   // the operator has no company of its own
        }

        [Fact]
        public async Task Milestones_unlock_perks_and_free_months_and_the_operator_controls_the_rules()
        {
            var op = await AdminAsync(_f);
            var (aId, a, code) = await ReferrerAsync(op, "Milestone Referrer");
            // the operator changes the rules: 20%, and one paying company unlocks two free months
            var saved = await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = true, percent = 20, invoicesRewarded = 1, description = "Test rules", tiers = new[] { new { key = "starter", label = "Starter", referrals = 1, freeMonths = 2, perk = "Two free months" } } });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            DateTime End() { using var s = _f.Services.CreateScope(); return s.ServiceProvider.GetRequiredService<FdmsDbContext>().Subscriptions.AsNoTracking().First(x => x.ClientId == aId).CurrentPeriodEnd; }
            var before = End();
            var (_, bAdmin) = await CompanyAsync(op, "Milestone Referred", code);
            var subtotal = await PayFirstInvoiceAsync(op, bAdmin);
            var o = await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J);
            Assert.Equal(Math.Round(subtotal * 0.20m, 2), o.GetProperty("rewards")[0].GetProperty("amount").GetDecimal());
            Assert.Equal("Starter", o.GetProperty("tiers")[0].GetProperty("label").GetString());
            Assert.True(o.GetProperty("tiers")[0].GetProperty("reached").GetBoolean());
            Assert.Equal(before.AddMonths(2), End());
            await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J);
            Assert.Equal(before.AddMonths(2), End());   // granted once

            // refused rules
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = true, percent = 150, invoicesRewarded = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = true, percent = 10, invoicesRewarded = 0 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = true, percent = 10, invoicesRewarded = 1, tiers = new[] { new { key = "a", label = "A", referrals = 0, freeMonths = 1, perk = (string?)null } } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await a.PutAsJsonAsync("/api/v1/referral-program", new { enabled = false, percent = 10, invoicesRewarded = 1 })).StatusCode);

            // switched off: a code is no longer honoured and nothing new is earned
            Assert.True((await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = false, percent = 10, invoicesRewarded = 1, description = "off" })).IsSuccessStatusCode);
            var (_, cAdmin) = await CompanyAsync(op, "Late Referred", code);
            await PayFirstInvoiceAsync(op, cAdmin);
            Assert.Equal(1, (await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J)).GetProperty("rewards").GetArrayLength());
            Assert.Equal(1, (await a.GetFromJsonAsync<JsonElement>("/api/v1/referrals", J)).GetProperty("referred").GetInt32());   // only the first company
            await op.PutAsJsonAsync("/api/v1/referral-program", new { enabled = true, percent = 10, invoicesRewarded = 1 });   // leave the rules sensible for other tests
        }

        [Fact]
        public async Task Terms_privacy_and_developer_details_are_public_and_edited_by_the_operator()
        {
            var anon = _f.CreateClient();
            var legal = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/legal", J);
            Assert.Contains("Terms", legal.GetProperty("terms").GetProperty("title").GetString());
            Assert.Contains("# 1.", legal.GetProperty("terms").GetProperty("body").GetString());
            Assert.Contains("privacy", legal.GetProperty("privacy").GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrEmpty(legal.GetProperty("developer").GetProperty("name").GetString()));

            var op = await AdminAsync(_f);
            var co = await NewCompanyAsync(_f, "Legal Co");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PutAsJsonAsync("/api/v1/settings/legal", new { terms = "mine" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/v1/settings/legal", new { terms = "mine" })).StatusCode);

            var res = await op.PutAsJsonAsync("/api/v1/settings/legal", new { terms = "# Our terms\nBe kind.", privacy = (string?)null, developer = new { name = "Dara ERP", about = "We build it.", email = "support@dara.test", phone = "+263 77 000 0000", whatsApp = "+263 77 000 0000", website = "https://dara.test", address = "Harare" } });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            legal = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/legal", J);
            Assert.Equal("# Our terms\nBe kind.", legal.GetProperty("terms").GetProperty("body").GetString());
            Assert.Equal("Dara ERP", legal.GetProperty("developer").GetProperty("name").GetString());
            Assert.Equal("support@dara.test", legal.GetProperty("developer").GetProperty("email").GetString());

            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/legal", new { developer = new { name = "X", email = "not-an-email" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/legal", new { developer = new { name = "X", website = "javascript:alert(1)" } })).StatusCode);

            // blank puts the starting text back
            await op.PutAsJsonAsync("/api/v1/settings/legal", new { terms = "" });
            Assert.Contains("# 1.", (await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/legal", J)).GetProperty("terms").GetProperty("body").GetString());
        }

        [Fact]
        public async Task The_operator_sets_where_to_download_the_app_and_everyone_can_see_it()
        {
            var anon = _f.CreateClient();
            const string url = "/api/v1/settings/app-links";
            var op = await AdminAsync(_f);
            var co = await NewCompanyAsync(_f, "App Links Co");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PutAsJsonAsync(url, new { apkPure = "https://apkpure.com/x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync(url, new { apkPure = "https://apkpure.com/x" })).StatusCode);

            // only the stores' own https addresses
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync(url, new { apkPure = "https://evil.example/fiscalhub.apk" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync(url, new { apkPure = "http://apkpure.com/fiscalhub" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync(url, new { playStore = "https://play.google.com.evil.example/x" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync(url, new { appStore = "javascript:alert(1)" })).StatusCode);

            var res = await op.PutAsJsonAsync(url, new { apkPure = " https://apkpure.com/fiscalhub/zw.co.daraerp.fdms_mobile ", playStore = "", appStore = (string?)null, version = "1.0.0" });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var links = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/app-links", J);
            Assert.Equal("https://apkpure.com/fiscalhub/zw.co.daraerp.fdms_mobile", links.GetProperty("apkPure").GetString());
            Assert.False(links.TryGetProperty("playStore", out var ps) && ps.ValueKind != JsonValueKind.Null);
            Assert.Equal("1.0.0", links.GetProperty("version").GetString());

            Assert.True((await op.PutAsJsonAsync(url, new { playStore = "https://play.google.com/store/apps/details?id=zw.co.daraerp.fdms_mobile", appStore = "https://apps.apple.com/app/id123456789" })).IsSuccessStatusCode);
            links = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/app-links", J);
            Assert.StartsWith("https://play.google.com/", links.GetProperty("playStore").GetString());
            Assert.StartsWith("https://apps.apple.com/", links.GetProperty("appStore").GetString());
        }
    }
}
