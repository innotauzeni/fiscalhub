using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>Self-service sign-up switched on, with a recording mail server (never a real one).</summary>
    public sealed class SignupFactory : WebApplicationFactory<Program>
    {
        public RecordingEmail Email { get; } = new();
        public FakeFdmsGateway Fdms { get; } = new();
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly string _keys = Path.Combine(Path.GetTempPath(), "fdms-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = JwtKey, ["Seed:AdminEmail"] = AdminEmail, ["Seed:AdminPassword"] = AdminPassword, ["Seed:ForcePasswordChange"] = "false",
                ["DataProtection:KeysPath"] = _keys, ["RateLimit:PerMinute"] = "100000", ["RateLimit:LoginPerMinute"] = "100000", ["Fdms:DistributedLocks"] = "false", ["Auth:ActiveCacheSeconds"] = "0",
                ["Onboarding:SelfService"] = "true", ["Onboarding:DeleteUnpaidAfterDays"] = "7",
            }));
            builder.ConfigureServices(s =>
            {
                TestDb.Use(s, _db);
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(Email);
                s.RemoveAll<IFdmsGateway>(); s.AddSingleton<IFdmsGateway>(Fdms);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    /// <summary>Sign in with a user name, change your email and user name, confirm the email on sign-up and start at once, and unpaid demo accounts being deleted.</summary>
    public class SignupTests : IClassFixture<SignupFactory>
    {
        private readonly SignupFactory _f;
        public SignupTests(SignupFactory f) => _f = f;

        private string CodeFor(string email, string subjectPart)
        {
            var m = _f.Email.Messages.ToList().LastOrDefault(x => x.To == email && x.Subject.Contains(subjectPart)) ?? throw new Xunit.Sdk.XunitException($"no \"{subjectPart}\" email to {email}");
            return Regex.Match(m.Html, @">(\d{6})<").Groups[1].Value;
        }

        private async Task<int> PlanAsync()
        {
            var plans = await _f.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/public/plans", J);
            return plans.EnumerateArray().First(p => p.GetProperty("code").GetString() == "business").GetProperty("id").GetInt32();
        }

        private async Task<HttpResponseMessage> RegisterAsync(string company, string email, string? userName) =>
            await _f.CreateClient().PostAsJsonAsync("/api/v1/public/register", new { companyName = company, tin = "1234567890", contactName = "Inno", contactSurname = "Tau", email, phone = "0771234567", password = UserPassword, planId = await PlanAsync(), billingCycle = "monthly", userName });

        /// <summary>Registers, confirms the email and returns the new company's id and its signed-in administrator.</summary>
        private async Task<(Guid ClientId, HttpClient Admin, string Email, string SignIn)> SignUpAsync(string company, string? userName = null)
        {
            var email = Unique("signup") + "@signup.test";
            Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(company, email, userName)).StatusCode);
            var ok = await _f.CreateClient().PostAsJsonAsync("/api/v1/public/register/verify", new { email, code = CodeFor(email, "confirmation code") });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            var signIn = (await ok.Json()).GetProperty("signInName").GetString()!;
            var admin = await LoginAsync(_f, signIn, UserPassword);
            var id = Guid.Parse((await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", J)).GetProperty("clientId").GetString()!);
            return (id, admin, email, signIn);
        }

        [Fact]
        public async Task Signing_up_confirms_the_email_with_a_code_then_opens_a_demo_with_its_first_invoice_ready()
        {
            var email = Unique("inno") + "@signup.test";
            var userName = Unique("Inno");
            var res = await RegisterAsync("Inno Trading", email, userName);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            var body = await res.Json();
            Assert.Equal("Unverified", body.GetProperty("status").GetString());
            Assert.True(body.GetProperty("verificationRequired").GetBoolean());

            // not yet: the email must be confirmed first
            var early = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = userName, password = UserPassword });
            Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
            Assert.Contains("EMAIL_NOT_CONFIRMED", await early.Content.ReadAsStringAsync());
            // asking again at once is refused; the user name is now reserved for this registration
            Assert.Equal((HttpStatusCode)429, (await _f.CreateClient().PostAsJsonAsync("/api/v1/public/register/resend", new { email })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync("Copycat", Unique("other") + "@signup.test", userName)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync("Bad Name", Unique("bad") + "@signup.test", "no spaces allowed")).StatusCode);

            var code = CodeFor(email, "confirmation code");
            var wrong = code == "000000" ? "111111" : "000000";
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.CreateClient().PostAsJsonAsync("/api/v1/public/register/verify", new { email, code = wrong })).StatusCode);
            var ok = await _f.CreateClient().PostAsJsonAsync("/api/v1/public/register/verify", new { email, code });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            var v = await ok.Json();
            Assert.Equal("Active", v.GetProperty("status").GetString());
            Assert.Equal(userName, v.GetProperty("signInName").GetString());
            Assert.False(string.IsNullOrEmpty(v.GetProperty("invoiceNumber").GetString()));
            // the code works once
            Assert.Equal(HttpStatusCode.BadRequest, (await _f.CreateClient().PostAsJsonAsync("/api/v1/public/register/verify", new { email, code })).StatusCode);

            // signs in with the user name (and with the email), straight into a demo with the invoice waiting under Billing
            var admin = await LoginAsync(_f, userName, UserPassword);
            await LoginAsync(_f, email, UserPassword);
            var billing = await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J);
            Assert.Equal("Trial", billing.GetProperty("subscription").GetProperty("status").GetString());
            var inv = billing.GetProperty("invoices").EnumerateArray().Single();
            Assert.Equal("Open", inv.GetProperty("status").GetString());
            Assert.True(billing.GetProperty("access").GetProperty("demoOnly").GetBoolean());
            Assert.False(billing.GetProperty("access").GetProperty("locked").GetBoolean());

            // the operator is told
            var op = await AdminAsync(_f);
            var bell = await op.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J);
            Assert.Contains(bell.GetProperty("items").EnumerateArray(), n => n.GetProperty("title").GetString() == "New company signed up: Inno Trading");
        }

        [Fact]
        public async Task Too_many_wrong_codes_lock_the_code_and_an_existing_address_reveals_nothing()
        {
            var email = Unique("tries") + "@signup.test";
            Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync("Tries Co", email, null)).StatusCode);
            var code = CodeFor(email, "confirmation code");
            var wrong = code == "000000" ? "111111" : "000000";
            for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await _f.CreateClient().PostAsJsonAsync("/api/v1/public/register/verify", new { email, code = wrong })).StatusCode);
            Assert.Equal((HttpStatusCode)429, (await _f.CreateClient().PostAsJsonAsync("/api/v1/public/register/verify", new { email, code })).StatusCode);

            // registering with an address that already has an account answers the same, and its owner gets a "you already have an account" email instead of a code
            var (_, _, existing, _) = await SignUpAsync("Existing Co");
            var again = await RegisterAsync("Existing Again", existing, null);
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
            Assert.Equal("Unverified", (await again.Json()).GetProperty("status").GetString());
            Assert.Contains(_f.Email.Messages.ToList(), m => m.To == existing && m.Subject.Contains("already have"));
        }

        [Fact]
        public async Task People_choose_a_user_name_and_change_their_email_with_a_code()
        {
            var (_, admin, email, _) = await SignUpAsync("Names Co");
            var name = Unique("Inno");

            // a user name of their own
            var set = await admin.PutAsJsonAsync("/api/v1/profile/username", new { userName = name });
            Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
            Assert.Equal(name, (await set.Json()).GetProperty("userName").GetString());
            await LoginAsync(_f, name, UserPassword);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/profile/username", new { userName = "a@b" })).StatusCode);

            // someone else cannot take it
            var (_, other, _, _) = await SignUpAsync("Other Names Co");
            Assert.Equal(HttpStatusCode.Conflict, (await other.PutAsJsonAsync("/api/v1/profile/username", new { userName = name })).StatusCode);

            // a new email: the password is checked, a code goes to the NEW address, and only that code switches it
            var newEmail = Unique("moved") + "@signup.test";
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/profile/email", new { newEmail, password = "wrong-password-1A" })).StatusCode);
            Assert.True((await admin.PostAsJsonAsync("/api/v1/profile/email", new { newEmail, password = UserPassword })).IsSuccessStatusCode);
            var code = CodeFor(newEmail, "Confirm your new");
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/profile/email/confirm", new { newEmail = Unique("x") + "@signup.test", code })).StatusCode);   // tied to that address
            var done = await admin.PostAsJsonAsync("/api/v1/profile/email/confirm", new { newEmail, code });
            Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
            Assert.Equal(newEmail, (await done.Json()).GetProperty("email").GetString());
            Assert.Contains(_f.Email.Messages.ToList(), m => m.To == email && m.Subject.Contains("email was changed"));
            await LoginAsync(_f, newEmail, UserPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = UserPassword })).StatusCode);

            // an administrator edits a colleague's email and user name
            var colleagueEmail = Unique("col") + "@signup.test";
            var created = await admin.PostAsJsonAsync("/api/v1/users", new { email = colleagueEmail, name = "Col", surname = "League", role = "Operator", password = UserPassword });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            var id = (await created.Json()).GetProperty("id").GetString();
            var colName = Unique("colleague");
            var upd = await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { name = "Col", surname = "League", role = "Operator", status = "ACTIVE", email = Unique("col2") + "@signup.test", userName = colName });
            Assert.True(upd.IsSuccessStatusCode, await upd.Content.ReadAsStringAsync());
            Assert.Equal(colName, (await upd.Json()).GetProperty("userName").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { name = "Col", surname = "League", role = "Operator", status = "ACTIVE", userName = name })).StatusCode);

            // forgot password works with the user name too
            Assert.True((await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = colName })).IsSuccessStatusCode);
        }

        [Fact]
        public async Task Unpaid_demo_accounts_are_warned_then_deleted_with_all_their_data_unless_paid_or_kept()
        {
            var (clientId, admin, email, signIn) = await SignUpAsync("Doomed Demo");
            Assert.True((await admin.PutAsJsonAsync("/api/v1/customers", new { name = "A demo customer", tin = "2000123456", vatNumber = (string?)null, email = "ap@demo.test", phone = "0771234567", address = "1 Main St", isActive = true })).IsSuccessStatusCode);
            // a device that issued a receipt (fiscal day, counters) and books posted from it (journal lines)
            var (dev, _) = await RegisterDeviceAsync(admin, 52000 + Random.Shared.Next(1000), "SN-DEMO");
            var receipt = await admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = "DEMO-1", invoiceNo = "DEMO-1", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                lines = new[] { new { name = "Demo item", hsCode = "85456852", quantity = 1, unitPrice = 115m, taxPercent = 15 } },
                payments = new[] { new { moneyType = "Cash", amount = 115m } },
            });
            Assert.True(receipt.IsSuccessStatusCode, await receipt.Content.ReadAsStringAsync());
            await admin.PostAsync("/api/v1/accounting/sync", null);
            Assert.True((await admin.PostAsJsonAsync("/api/v1/users", new { email = Unique("staff") + "@signup.test", name = "Staff", surname = "Member", role = "Operator", password = UserPassword })).IsSuccessStatusCode);
            var (paidId, paidAdmin, _, _) = await SignUpAsync("Paying Demo");
            var op = await AdminAsync(_f);
            var paidInv = (await paidAdmin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").EnumerateArray().First();
            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{paidInv.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash", reference = Unique("CASH"), notes = (string?)null, sendCode = false })).IsSuccessStatusCode);

            DateTime trialEnd;
            using (var s = _f.Services.CreateScope()) trialEnd = s.ServiceProvider.GetRequiredService<FdmsDbContext>().Subscriptions.AsNoTracking().First(x => x.ClientId == clientId).TrialEndsAt!.Value;
            async Task<int> Run(DateTime now) { using var s = _f.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<IDemoCleanupService>().RunAsync(now, CancellationToken.None); }
            async Task<bool> Exists(Guid id) { using var s = _f.Services.CreateScope(); return await s.ServiceProvider.GetRequiredService<FdmsDbContext>().Clients.AnyAsync(c => c.Id == id); }

            // the operator sees it coming
            var list = await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/billing/demo-accounts", J);
            var row = list.EnumerateArray().Single(x => x.GetProperty("clientId").GetGuid() == clientId);
            Assert.Equal(trialEnd.AddDays(7).Date, row.GetProperty("deleteOn").GetDateTime().Date);
            Assert.True(row.GetProperty("willBeDeleted").GetBoolean());
            Assert.DoesNotContain(list.EnumerateArray(), x => x.GetProperty("clientId").GetGuid() == paidId);   // paid: not a demo any more

            // demo over: warned (bell + email), not deleted; two days before: warned again
            Assert.Equal(0, await Run(trialEnd.AddDays(1)));
            Assert.Equal(0, await Run(trialEnd.AddDays(6)));
            Assert.True(await Exists(clientId));
            var bell = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
            Assert.Contains("Your demo has ended", bell);
            Assert.Contains(bell, t => t!.StartsWith("Your account will be deleted on"));

            // kept by the operator: never deleted
            Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsJsonAsync($"/api/v1/admin/billing/demo-accounts/{clientId}/keep", new { keep = true })).StatusCode);
            Assert.Equal(0, await Run(trialEnd.AddDays(30)));
            Assert.True(await Exists(clientId));

            // keep switched off: after the grace week it is gone, with everything in it; the paying company stays
            Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsJsonAsync($"/api/v1/admin/billing/demo-accounts/{clientId}/keep", new { keep = false })).StatusCode);
            Assert.True(await Run(trialEnd.AddDays(8)) >= 1);
            Assert.False(await Exists(clientId));
            Assert.True(await Exists(paidId));
            using (var s = _f.Services.CreateScope())
            {
                var db = s.ServiceProvider.GetRequiredService<FdmsDbContext>();
                Assert.False(await db.Users.AnyAsync(u => u.ClientId == clientId));
                Assert.False(await db.Customers.AnyAsync(c => c.ClientId == clientId));
                Assert.False(await db.BillingInvoices.AnyAsync(i => i.ClientId == clientId));
                Assert.False(await db.Subscriptions.AnyAsync(x => x.ClientId == clientId));
                Assert.False(await db.Devices.AnyAsync(x => x.ClientId == clientId));
                Assert.False(await db.Receipts.AnyAsync(x => x.ClientId == clientId));
                Assert.False(await db.JournalEntries.AnyAsync(x => x.ClientId == clientId));
                Assert.False(await db.FiscalDays.AnyAsync(x => x.DeviceId == dev));
                Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "demo.deleted" && a.EntityId == clientId.ToString()));
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = signIn, password = UserPassword })).StatusCode);
            Assert.Contains(_f.Email.Messages.ToList(), m => m.To == email && m.Subject.Contains("was deleted"));
            Assert.Contains((await op.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray(), n => n.GetProperty("title").GetString() == "Unpaid demo deleted: Doomed Demo");
        }

        [Fact]
        public void Every_table_is_either_company_owned_or_known_to_the_purger()
        {
            using var s = _f.Services.CreateScope();
            var db = s.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var names = db.Model.GetEntityTypes().Where(e => e.BaseType == null).ToList();
            var unknown = names.Where(e => e.FindProperty("ClientId") == null && !TenantPurger.WithoutClientId.Contains(e.ClrType.Name)).Select(e => e.ClrType.Name).ToList();
            Assert.True(unknown.Count == 0, "Decide how a deleted company's rows in these tables go: " + string.Join(", ", unknown));
            var stale = TenantPurger.WithoutClientId.Where(n => names.All(e => e.ClrType.Name != n)).ToList();
            Assert.True(stale.Count == 0, "No longer in the model: " + string.Join(", ", stale));
        }
    }
}
