using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>The approval matrix (maker-checker), the platform's revenue, the mail server set in Settings and the operator's own bell.</summary>
    public class ApprovalsAndPlatformTests : IClassFixture<ExtrasFactory>
    {
        private readonly ExtrasFactory _f;
        public ApprovalsAndPlatformTests(ExtrasFactory f) => _f = f;

        private async Task<HttpClient> UserAsync(HttpClient admin, string role)
        {
            var email = Unique(role.ToLowerInvariant()) + "@approvals.test";
            var res = await admin.PostAsJsonAsync("/api/v1/users", new { email, name = role, surname = "User", role, password = UserPassword });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await LoginAsync(_f, email, UserPassword);
        }

        private static async Task<Dictionary<string, Guid>> ChartAsync(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/accounts", J)).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("id").GetGuid());

        private static object Journal(Dictionary<string, Guid> chart, string memo, decimal amount) => new
        {
            date = "2026-10-05", currency = "USD", memo, reference = memo.ToUpperInvariant(),
            lines = new object[] { new { accountId = chart["5110"], debit = amount, credit = 0m }, new { accountId = chart["1010"], debit = 0m, credit = amount } },
        };

        private static async Task<bool> JournalExistsAsync(HttpClient c, string memo) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/accounting/journals?from=2020-01-01&to=2035-01-01", J)).EnumerateArray().Any(j => j.GetProperty("memo").GetString() == memo);

        [Fact]
        public async Task Big_journals_wait_for_two_levels_of_approval_by_other_people_and_are_then_posted()
        {
            var co = await NewCompanyAsync(_f, "Maker Checker");
            var maker = await UserAsync(co.Admin, "Accountant");
            var checker = await UserAsync(co.Admin, "Accountant");
            var viewer = await UserAsync(co.Admin, "Viewer");
            var chart = await ChartAsync(co.Admin);

            // the matrix: journals from 100 need an accountant, then the company administrator
            var rule = await co.Admin.PutAsJsonAsync("/api/v1/approvals/rules", new { action = "journal", minAmount = 100m, currency = (string?)null, levels = new[] { "Accountant", "ClientAdmin" }, isActive = true });
            Assert.True(rule.IsSuccessStatusCode, await rule.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/approvals/rules", new { action = "journal", minAmount = 10m, levels = new[] { "Nobody" } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await maker.PutAsJsonAsync("/api/v1/approvals/rules", new { action = "journal", minAmount = 1m, levels = new[] { "Accountant" } })).StatusCode);
            var matrix = await maker.GetFromJsonAsync<JsonElement>("/api/v1/approvals/matrix", J);
            var journalRow = matrix.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("action").GetProperty("key").GetString() == "journal");
            Assert.Equal(new[] { "Accountant", "ClientAdmin" }, journalRow.GetProperty("rules")[0].GetProperty("levels").EnumerateArray().Select(l => l.GetString()).ToArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/approvals")).StatusCode);

            // below the threshold it is posted at once
            Assert.Equal(HttpStatusCode.OK, (await maker.PostAsJsonAsync("/api/v1/accounting/journals", Journal(chart, "Small", 50m))).StatusCode);
            Assert.True(await JournalExistsAsync(co.Admin, "Small"));

            // above it, it is held
            var held = await maker.PostAsJsonAsync("/api/v1/accounting/journals", Journal(chart, "Big", 150m));
            Assert.Equal(HttpStatusCode.Accepted, held.StatusCode);
            var req = (await held.Json()).GetProperty("request");
            var id = req.GetProperty("id").GetGuid();
            Assert.Equal("Pending", req.GetProperty("status").GetString());
            Assert.Equal("Accountant", req.GetProperty("waitingForRole").GetString());
            Assert.False(await JournalExistsAsync(co.Admin, "Big"));

            // the checker sees it in the inbox; the maker cannot decide their own; the administrator is not the first level
            var inbox = await checker.GetFromJsonAsync<JsonElement>("/api/v1/approvals?scope=mine", J);
            Assert.True(inbox.GetProperty("waitingForMe").GetInt32() >= 1);
            Assert.Contains(inbox.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id && i.GetProperty("canDecide").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await maker.PostAsJsonAsync($"/api/v1/approvals/{id}/approve", new { note = "mine" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsJsonAsync($"/api/v1/approvals/{id}/approve", new { note = "too early" })).StatusCode);

            var first = await checker.PostAsJsonAsync($"/api/v1/approvals/{id}/approve", new { note = "checked" });
            Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
            var afterFirst = await first.Json();
            Assert.Equal("Pending", afterFirst.GetProperty("status").GetString());
            Assert.Equal("ClientAdmin", afterFirst.GetProperty("waitingForRole").GetString());
            Assert.False(await JournalExistsAsync(co.Admin, "Big"));

            var last = await co.Admin.PostAsJsonAsync($"/api/v1/approvals/{id}/approve", new { note = "ok" });
            Assert.True(last.IsSuccessStatusCode, await last.Content.ReadAsStringAsync());
            var done = await last.Json();
            Assert.Equal("Approved", done.GetProperty("status").GetString());
            Assert.Equal(2, done.GetProperty("decisions").GetArrayLength());
            Assert.True(await JournalExistsAsync(co.Admin, "Big"));   // carried out on the last approval
            Assert.Equal(HttpStatusCode.Conflict, (await checker.PostAsJsonAsync($"/api/v1/approvals/{id}/reject", new { note = "late" })).StatusCode);

            // a rejection needs a reason, and nothing is posted
            var other = (await (await maker.PostAsJsonAsync("/api/v1/accounting/journals", Journal(chart, "Rejected one", 500m))).Json()).GetProperty("request").GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.BadRequest, (await checker.PostAsJsonAsync($"/api/v1/approvals/{other}/reject", new { note = "" })).StatusCode);
            Assert.Equal("Rejected", (await (await checker.PostAsJsonAsync($"/api/v1/approvals/{other}/reject", new { note = "No receipt attached" })).Json()).GetProperty("status").GetString());
            Assert.False(await JournalExistsAsync(co.Admin, "Rejected one"));

            // the maker can withdraw a pending request; another company never sees it
            var third = (await (await maker.PostAsJsonAsync("/api/v1/accounting/journals", Journal(chart, "Withdrawn", 300m))).Json()).GetProperty("request").GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await checker.PostAsync($"/api/v1/approvals/{third}/cancel", null)).StatusCode);
            Assert.Equal("Cancelled", (await (await maker.PostAsync($"/api/v1/approvals/{third}/cancel", null)).Json()).GetProperty("status").GetString());
            var stranger = await NewCompanyAsync(_f, "Approval Stranger");
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.Admin.GetAsync($"/api/v1/approvals/{id}")).StatusCode);

            // the people who must decide hear about it in the bell
            var bell = await checker.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J);
            Assert.Contains(bell.GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "approval");
        }

        [Fact]
        public async Task Only_the_platform_sees_revenue()
        {
            var op = await AdminAsync(_f);
            var res = await op.GetAsync("/api/v1/admin/revenue");
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            // every answer carries the hardening headers, and company data is never cached by a proxy
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
            Assert.True(res.Headers.CacheControl?.NoStore);
            var r = await res.Json();
            Assert.True(r.TryGetProperty("mrr", out _));
            Assert.Equal(12, r.GetProperty("months").GetArrayLength());
            Assert.True(r.GetProperty("counts").GetProperty("companies").GetInt32() >= 0);

            var co = await NewCompanyAsync(_f, "Revenue Snoop");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/admin/revenue")).StatusCode);
        }

        [Fact]
        public async Task The_operator_sets_the_mail_server_and_alert_addresses_and_the_password_never_comes_back()
        {
            var op = await AdminAsync(_f);
            object Body(string notify, string? password) => new { enabled = true, host = "mail.example.test", port = 587, enableSsl = true, user = "mailer", password, clearPassword = false, fromAddress = "noreply@example.test", fromName = "FDMS", notifyEmails = notify };

            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/email", Body("owner@example.test, not-an-email", "pw-for-tests"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/email", new { enabled = true, host = "bad host!", port = 587, enableSsl = true })).StatusCode);
            var saved = await op.PutAsJsonAsync("/api/v1/settings/email", Body("owner@example.test; finance@example.test", "pw-for-tests"));
            var text = await saved.Content.ReadAsStringAsync();
            Assert.True(saved.IsSuccessStatusCode, text);
            Assert.DoesNotContain("pw-for-tests", text);
            var s = JsonDocument.Parse(text).RootElement;
            Assert.Equal("database", s.GetProperty("source").GetString());
            Assert.True(s.GetProperty("hasPassword").GetBoolean());
            Assert.Equal("owner@example.test, finance@example.test", s.GetProperty("notifyEmails").GetString());

            // saving again without a password keeps the old one
            Assert.True((await (await op.PutAsJsonAsync("/api/v1/settings/email", Body("owner@example.test", null))).Json()).GetProperty("hasPassword").GetBoolean());

            // a test message goes out (to the recording fake: tests never use a real mail server)
            var test = await (await op.PostAsJsonAsync("/api/v1/settings/email/test", new { to = "check@example.test" })).Json();
            Assert.True(test.GetProperty("ok").GetBoolean());
            Assert.Contains(_f.Email.Messages.ToList(), m => m.To == "check@example.test");

            // platform alerts land in the operator's bell and are emailed to the addresses set here, once per key
            using (var scope = _f.Services.CreateScope())
            {
                var n = scope.ServiceProvider.GetRequiredService<INotificationService>();
                Assert.True(await n.RaisePlatformAsync("application", "info", "test:platform-alert", "New registration: Alert Test Co", "Someone registered.", "/admin/applications", CancellationToken.None));
                Assert.False(await n.RaisePlatformAsync("application", "info", "test:platform-alert", "New registration: Alert Test Co", "Again.", "/admin/applications", CancellationToken.None));
            }
            Assert.Single(_f.Email.Messages.ToList(), m => m.To == "owner@example.test" && m.Subject.Contains("Alert Test Co"));
            var bell = await op.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J);
            Assert.Contains(bell.GetProperty("items").EnumerateArray(), x => x.GetProperty("title").GetString() == "New registration: Alert Test Co" && x.GetProperty("link").GetString() == "/admin/applications");

            // companies cannot read or change the mail server, and never see the platform's bell
            var co = await NewCompanyAsync(_f, "Mail Snoop");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/settings/email")).StatusCode);
            var theirs = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J);
            Assert.DoesNotContain(theirs.GetProperty("items").EnumerateArray(), x => x.GetProperty("title").GetString()!.Contains("Alert Test Co"));
        }
    }

    /// <summary>The operator hears about new registrations and payments; the company hears that an invoice is ready and that its payment was approved.</summary>
    public class BillingNotificationTests : IClassFixture<BillingFactory>
    {
        private readonly BillingFactory _f;
        public BillingNotificationTests(BillingFactory f) => _f = f;

        private static async Task<List<JsonElement>> BellAsync(HttpClient c) =>
            (await c.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray().ToList();

        [Fact]
        public async Task Registrations_invoices_and_payments_reach_the_right_bell()
        {
            var op = await AdminAsync(_f);
            var anon = _f.CreateClient();
            var plans = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/plans", J);
            var planId = plans.EnumerateArray().First(p => p.GetProperty("code").GetString() == "business").GetProperty("id").GetInt32();
            var email = Unique("bell") + "@bell.test";
            var company = Unique("Bell Co ");
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", new { companyName = company, tin = "1234567890", contactName = "Zed", contactSurname = "Moyo", email, phone = "0771234567", password = UserPassword, planId, billingCycle = "monthly" })).StatusCode);

            Assert.Contains(await BellAsync(op), n => n.GetProperty("title").GetString() == $"New registration: {company}" && n.GetProperty("link").GetString() == "/admin/applications");

            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications?status=Pending", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            var approved = await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/approve", new { trialDays = 0 });
            Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
            var admin = await LoginAsync(_f, email, UserPassword);

            var inv = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").EnumerateArray().First(i => i.GetProperty("status").GetString() == "Open");
            var number = inv.GetProperty("number").GetString();
            Assert.Contains(await BellAsync(admin), n => n.GetProperty("kind").GetString() == "billing" && n.GetProperty("title").GetString() == $"Invoice {number} is ready to pay");

            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{inv.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash", reference = Unique("CASH"), notes = (string?)null, sendCode = false })).IsSuccessStatusCode);
            Assert.Contains(await BellAsync(admin), n => n.GetProperty("title").GetString() == $"Payment for {number} approved");
            Assert.Contains(await BellAsync(op), n => n.GetProperty("title").GetString()!.StartsWith("Paid: ") && n.GetProperty("body").GetString()!.Contains(number!));

            // the company's bell never shows the platform's own alerts, and marking read works on each inbox
            Assert.DoesNotContain(await BellAsync(admin), n => n.GetProperty("title").GetString()!.StartsWith("New registration"));
            Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsJsonAsync("/api/v1/notifications/read", new { ids = (Guid[]?)null })).StatusCode);
            Assert.Equal(0, (await op.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("unread").GetInt32());
            Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("unread").GetInt32() > 0);
        }
    }
}
