using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>A company can require everyone to use two-step sign-in or a passkey.</summary>
    public class MfaPolicyTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static int _seq = 31000;
        public MfaPolicyTests(AccessFactory f) => _f = f;

        private async Task<(string Email, HttpClient Me)> PersonAsync(TestKit.Company co, string role = "Operator")
        {
            var email = Unique("pol") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Pat", surname = "Policy", role, password = UserPassword })).IsSuccessStatusCode);
            return (email, await LoginAsync(_f, email, UserPassword));
        }

        private static HttpClient Client(AccessFactory f, string token) { var c = f.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); return c; }

        [Fact]
        public async Task When_required_a_person_without_extra_protection_can_only_reach_their_security_settings()
        {
            var co = await NewCompanyAsync(_f, "Policy Co");
            var (email, me) = await PersonAsync(co);
            Assert.Equal(HttpStatusCode.OK, (await me.GetAsync("/api/v1/devices")).StatusCode);   // optional by default

            // only an administrator can change it
            Assert.Equal(HttpStatusCode.Forbidden, (await me.PutAsJsonAsync("/api/v1/security-policy", new { requireMfa = true })).StatusCode);
            var set = await co.Admin.PutAsJsonAsync("/api/v1/security-policy", new { requireMfa = true });
            Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
            var policy = await set.Json();
            Assert.True(policy.GetProperty("requireMfa").GetBoolean());
            Assert.All(policy.GetProperty("users").EnumerateArray(), u => Assert.False(u.GetProperty("compliant").GetBoolean()));

            // a new sign-in is flagged and locked down to the security pages
            var login = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = UserPassword })).Json();
            Assert.True(login.GetProperty("mfaSetupRequired").GetBoolean());
            var token = login.GetProperty("accessToken").GetString()!;
            var c = Client(_f, token);
            var blocked = await c.GetAsync("/api/v1/devices");
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Contains("MFA_SETUP_REQUIRED", await blocked.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/security")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/profile")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/auth/me")).StatusCode);

            // turning on the authenticator app and refreshing lifts it
            var setup = await (await c.PostAsync("/api/v1/security/mfa/setup", null)).Json();
            Assert.True((await c.PostAsJsonAsync("/api/v1/security/mfa/enable", new { code = MfaPasskeyTests.Totp(setup.GetProperty("secret").GetString()!) })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/devices")).StatusCode);   // the old session still carries the flag
            var refreshed = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = login.GetProperty("refreshToken").GetString() })).Json();
            Assert.False(refreshed.GetProperty("mfaSetupRequired").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await Client(_f, refreshed.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/devices")).StatusCode);

            // from now on the person signs in with the code, and that session is not flagged
            var again = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = UserPassword })).Json();
            Assert.True(again.GetProperty("mfaRequired").GetBoolean());
            var done = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login/mfa", new { mfaToken = again.GetProperty("mfaToken").GetString(), code = MfaPasskeyTests.Totp(setup.GetProperty("secret").GetString()!) })).Json();
            Assert.False(done.GetProperty("mfaSetupRequired").GetBoolean());
            Assert.True((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/security-policy", J)).GetProperty("users").EnumerateArray().Any(u => u.GetProperty("email").GetString() == email && u.GetProperty("compliant").GetBoolean()));

            // switching the rule off frees everyone again
            Assert.False((await (await co.Admin.PutAsJsonAsync("/api/v1/security-policy", new { requireMfa = false })).Json()).GetProperty("requireMfa").GetBoolean());
            var (email2, _) = await PersonAsync(co);
            Assert.False((await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email2, password = UserPassword })).Json()).GetProperty("mfaSetupRequired").GetBoolean());
        }

        [Fact]
        public async Task A_passkey_satisfies_the_rule_and_integration_tokens_are_not_affected()
        {
            var co = await NewCompanyAsync(_f, "Policy Key Co");
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var token = await NewTokenAsync(_f, co.Admin, dev);
            var (email, me) = await PersonAsync(co);
            Assert.True((await co.Admin.PutAsJsonAsync("/api/v1/security-policy", new { requireMfa = true })).IsSuccessStatusCode);

            // the person adds a passkey while their session is limited to the security pages, then signs in with it
            var login = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = UserPassword })).Json();
            var c = Client(_f, login.GetProperty("accessToken").GetString()!);
            var begin = await (await c.PostAsync("/api/v1/security/passkeys/options", null)).Json();
            var key = new MfaPasskeyTests.Authenticator();
            var added = await c.PostAsJsonAsync("/api/v1/security/passkeys/" + begin.GetProperty("sessionId").GetString(), new { name = "Laptop", credential = key.Create(begin.GetProperty("options")) });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            var userId = Encoding.UTF8.GetString(MfaPasskeyTests.FromB64(begin.GetProperty("options").GetProperty("user").GetProperty("id").GetString()!));

            var anon = _f.CreateClient();
            var pk = await (await anon.PostAsync("/api/v1/auth/passkey/options", null)).Json();
            var res = await anon.PostAsJsonAsync("/api/v1/auth/passkey/login", new { sessionId = pk.GetProperty("sessionId").GetString(), credential = key.Get(pk.GetProperty("options"), userId) });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var session = await res.Json();
            Assert.False(session.GetProperty("mfaSetupRequired").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await Client(_f, session.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/devices")).StatusCode);

            // ... and so does a refresh of that session (the method is remembered)
            var refreshed = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = session.GetProperty("refreshToken").GetString() })).Json();
            Assert.False(refreshed.GetProperty("mfaSetupRequired").GetBoolean());

            // a device token has no person behind it and keeps working
            var t = token.Http;
            Assert.Equal(HttpStatusCode.OK, (await t.GetAsync($"/api/v1/devices/{dev}")).StatusCode);
        }
    }

    /// <summary>The operator's usage analytics, and the company's document store.</summary>
    public class AnalyticsAndDocumentTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static int _seq = 32000;
        public AnalyticsAndDocumentTests(AccessFactory f) => _f = f;

        private static object Invoice(string extRef) => new
        {
            externalRef = extRef, invoiceNo = "INV-" + extRef, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
            lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } },
        };

        private async Task WithDb(Func<FdmsDbContext, Task> work)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            await work(db);
            await db.SaveChangesAsync();
        }

        // ---- analytics ------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task Usage_analytics_count_receipts_and_flag_companies_at_risk_with_reasons()
        {
            var co = await NewCompanyAsync(_f, "Usage Co");
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            Assert.Equal(HttpStatusCode.Created, (await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("U1"))).StatusCode);
            var quiet = await NewCompanyAsync(_f, "Quiet Co");
            await WithDb(async db => { var c = await db.Clients.FirstAsync(x => x.Id == quiet.ClientId); c.CreatedAt = DateTime.UtcNow.AddDays(-40); });

            var op = await AdminAsync(_f);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/analytics/usage")).StatusCode);   // the operator only
            var usage = await op.GetFromJsonAsync<JsonElement>("/api/v1/analytics/usage?days=30", J);
            JsonElement Row(Guid id) => usage.GetProperty("companies").EnumerateArray().First(r => r.GetProperty("id").GetGuid() == id);
            Assert.Equal(1, Row(co.ClientId).GetProperty("receipts").GetInt32());
            Assert.Equal(1, Row(co.ClientId).GetProperty("devices").GetInt32());
            Assert.True(usage.GetProperty("totals").GetProperty("receipts").GetInt32() >= 1);
            Assert.Equal(30, usage.GetProperty("daily").GetArrayLength());
            Assert.True(usage.GetProperty("daily").EnumerateArray().Last().GetProperty("receipts").GetInt32() >= 1);
            Assert.Equal("Medium", Row(quiet.ClientId).GetProperty("risk").GetString());
            Assert.Contains("No device registered yet", Row(quiet.ClientId).GetProperty("reasons").EnumerateArray().Select(x => x.GetString()));

            // a company that was busy before and is silent now is the one to phone
            await WithDb(async db => { foreach (var r in await db.Receipts.Where(r => r.ClientId == co.ClientId).ToListAsync()) r.CreatedAt = DateTime.UtcNow.AddDays(-45); });
            usage = await op.GetFromJsonAsync<JsonElement>("/api/v1/analytics/usage?days=30", J);
            Assert.Equal(0, Row(co.ClientId).GetProperty("receipts").GetInt32());
            Assert.Equal(1, Row(co.ClientId).GetProperty("receiptsPrevious").GetInt32());
            Assert.Equal("High", Row(co.ClientId).GetProperty("risk").GetString());
            Assert.Contains(Row(co.ClientId).GetProperty("reasons").EnumerateArray().Select(x => x.GetString()!), r => r.StartsWith("No receipts in the last 30 days"));
            Assert.Equal("High", usage.GetProperty("companies")[0].GetProperty("risk").GetString());   // the risky ones come first
            Assert.True(usage.GetProperty("totals").GetProperty("atRisk").GetInt32() >= 2);
        }

        // ---- documents ------------------------------------------------------------------------------------------------------

        private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF");

        private static MultipartFormDataContent Form(string fileName, byte[] data, string category = "Supplier invoice", string? linkType = null, Guid? linkId = null, string? title = null, string? retainUntil = null)
        {
            var f = new MultipartFormDataContent();
            var file = new ByteArrayContent(data); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            f.Add(file, "File", fileName);
            f.Add(new StringContent(category), "Category");
            if (title != null) f.Add(new StringContent(title), "Title");
            if (linkType != null) f.Add(new StringContent(linkType), "LinkType");
            if (linkId != null) f.Add(new StringContent(linkId.ToString()!), "LinkId");
            if (retainUntil != null) f.Add(new StringContent(retainUntil), "RetainUntil");
            return f;
        }

        [Fact]
        public async Task Documents_are_checked_attached_kept_for_years_and_private_to_the_company()
        {
            var co = await NewCompanyAsync(_f, "Docs Co");
            var supplier = await (await co.Admin.PutAsJsonAsync("/api/v1/accounting/suppliers", new { id = (Guid?)null, name = "Paper Mill", termsDays = 30, isActive = true })).Json();
            var supplierId = supplier.GetProperty("id").GetGuid();

            var up = await co.Admin.PostAsync("/api/v1/accounting/documents", Form("PM-0001.pdf", Pdf, linkType: "supplier", linkId: supplierId, title: "Paper Mill invoice 1"));
            Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
            var doc = await up.Json();
            var id = doc.GetProperty("id").GetGuid();
            Assert.Equal("Supplier Paper Mill", doc.GetProperty("linkLabel").GetString());
            Assert.False(doc.GetProperty("canDelete").GetBoolean());
            Assert.True(doc.GetProperty("retainUntil").GetDateTime() >= DateTime.UtcNow.Date.AddYears(7));   // kept 7 years by default

            // listing, filtering and downloading (the bytes come back untouched, with a fixed type)
            Assert.Single((await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/accounting/documents?linkType=supplier&linkId={supplierId}", J)).EnumerateArray());
            Assert.Empty((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/documents?q=nothing-like-this", J)).EnumerateArray());
            var file = await co.Admin.GetAsync($"/api/v1/accounting/documents/{id}/file");
            Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Pdf, await file.Content.ReadAsByteArrayAsync());
            Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
            var summary = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/documents/summary", J);
            Assert.Equal(1, summary.GetProperty("files").GetInt32()); Assert.Equal(Pdf.Length, summary.GetProperty("totalBytes").GetInt64());

            // refused: a program renamed to .pdf, a type that is not allowed, an empty file, the same file twice, a bad category, an unknown supplier
            var exe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };
            Assert.Contains("CONTENT_MISMATCH", await (await co.Admin.PostAsync("/api/v1/accounting/documents", Form("run.pdf", exe))).Content.ReadAsStringAsync());
            Assert.Contains("TYPE_NOT_ALLOWED", await (await co.Admin.PostAsync("/api/v1/accounting/documents", Form("run.exe", exe))).Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync("/api/v1/accounting/documents", Form("empty.pdf", Array.Empty<byte>()))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync("/api/v1/accounting/documents", Form("again.pdf", Pdf, linkType: "supplier", linkId: supplierId))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync("/api/v1/accounting/documents", Form("x.pdf", Pdf, category: "Nonsense"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await co.Admin.PostAsync("/api/v1/accounting/documents", Form("y.pdf", Pdf.Concat(new byte[] { 1 }).ToArray(), linkType: "supplier", linkId: Guid.NewGuid()))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsync("/api/v1/accounting/documents", Form("z.pdf", Pdf.Concat(new byte[] { 2 }).ToArray(), retainUntil: DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd")))).StatusCode);   // shorter than the legal minimum

            // retention: cannot be deleted yet, can be extended but never shortened, and can be deleted once it has passed
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/accounting/documents/{id}")).StatusCode);
            var until = doc.GetProperty("retainUntil").GetDateTime();
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync($"/api/v1/accounting/documents/{id}", new { title = "T", category = "Tax", notes = (string?)null, retainUntil = until.AddDays(-1) })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.PutAsJsonAsync($"/api/v1/accounting/documents/{id}", new { title = "Renamed", category = "Tax", notes = "kept", retainUntil = until.AddYears(1) })).StatusCode);
            await WithDb(async db => { (await db.Documents.FirstAsync(d => d.Id == id)).RetainUntil = DateTime.UtcNow.AddDays(-1); });
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/accounting/documents/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await co.Admin.GetAsync($"/api/v1/accounting/documents/{id}/file")).StatusCode);
        }

        [Fact]
        public async Task Another_company_and_roles_without_accounting_cannot_reach_documents()
        {
            var co = await NewCompanyAsync(_f, "Docs Private Co");
            var id = (await (await co.Admin.PostAsync("/api/v1/accounting/documents", Form("secret.pdf", Pdf))).Json()).GetProperty("id").GetGuid();
            var other = await NewCompanyAsync(_f, "Docs Other Co");
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/accounting/documents/{id}/file")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.DeleteAsync($"/api/v1/accounting/documents/{id}")).StatusCode);
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/accounting/documents", J)).EnumerateArray());

            async Task<HttpClient> Person(string role)
            {
                var email = Unique(role.ToLowerInvariant()) + "@users.test";
                Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = role, surname = "U", role, password = UserPassword })).IsSuccessStatusCode);
                return await LoginAsync(_f, email, UserPassword);
            }
            var viewer = await Person("Viewer");
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/accounting/documents")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/accounting/documents/{id}/file")).StatusCode);
            var accountant = await Person("Accountant");
            Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/v1/accounting/documents/{id}/file")).StatusCode);
            Assert.True((await accountant.PostAsync("/api/v1/accounting/documents", Form("acct.pdf", Pdf.Concat(new byte[] { 9 }).ToArray()))).IsSuccessStatusCode);
        }
    }

    /// <summary>Partners (resellers): referral codes, their own staff and portal, commission, payouts, and what they can never see.</summary>
    public class PartnerTests : IClassFixture<BillingFactory>
    {
        private readonly BillingFactory _f;
        public PartnerTests(BillingFactory f) => _f = f;

        private async Task<(Guid Id, string Code)> NewPartnerAsync(HttpClient op, string name, decimal percent = 10)
        {
            var res = await op.PutAsJsonAsync("/api/v1/partners", new { id = (Guid?)null, name, code = (string?)null, contactName = "Pia", email = "pia@partner.test", phone = "0770000000", commissionPercent = percent, isActive = true, notes = (string?)null });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var p = await res.Json();
            return (p.GetProperty("id").GetGuid(), p.GetProperty("code").GetString()!);
        }

        private async Task<HttpClient> StaffAsync(HttpClient op, Guid partnerId)
        {
            var email = Unique("staff") + "@partner.test";
            var res = await op.PostAsJsonAsync($"/api/v1/partners/{partnerId}/staff", new { email, name = "Sipho", surname = "Staff", password = UserPassword });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await LoginAsync(_f, email, UserPassword);
        }

        private async Task<Guid> RegisterAndApproveAsync(HttpClient op, string? partnerCode, string company)
        {
            var anon = _f.CreateClient();
            var plans = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/plans", J);
            var planId = plans.EnumerateArray().First(p => p.GetProperty("code").GetString() == "business").GetProperty("id").GetInt32();
            var email = Unique("ref") + "@referred.test";
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", new { companyName = company, tin = "1234567890", contactName = "Zed", contactSurname = "Moyo", email, phone = "0771234567", password = UserPassword, planId, billingCycle = "monthly", partnerCode })).StatusCode);
            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications?status=Pending", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            var res = await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/approve", new { trialDays = 0 });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return (await res.Json()).GetProperty("clientId").GetGuid();
        }

        private async Task<decimal> PayFirstInvoiceAsync(Guid clientId)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var inv = await db.BillingInvoices.Where(i => i.ClientId == clientId).OrderBy(i => i.IssuedAt).FirstAsync();
            inv.Status = "Paid"; inv.PaidAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return inv.Subtotal;
        }

        [Fact]
        public async Task A_referred_company_earns_its_partner_commission_which_is_paid_out_against_what_is_owed()
        {
            var op = await AdminAsync(_f);
            var (pid, code) = await NewPartnerAsync(op, "Acme Accountants", 10);
            Assert.Equal("ACMEACCO", code);
            Assert.Equal(HttpStatusCode.Conflict, (await op.PutAsJsonAsync("/api/v1/partners", new { id = (Guid?)null, name = "Copycat", code, commissionPercent = 5, isActive = true })).StatusCode);   // codes are unique
            var staff = await StaffAsync(op, pid);

            var summary = await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/summary", J);
            Assert.Equal("/register?ref=ACMEACCO", summary.GetProperty("referralPath").GetString());
            Assert.Equal(0, summary.GetProperty("clients").GetInt32());

            // a company registers with the code (lower case is fine), the operator approves it, and it joins the partner at the partner's rate
            var clientId = await RegisterAndApproveAsync(op, "acmeacco", "Referred Traders");
            Assert.Equal(1, (await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/summary", J)).GetProperty("clients").GetInt32());
            var clients = await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/clients", J);
            Assert.Equal("Referred Traders", clients[0].GetProperty("name").GetString());
            Assert.Equal(10m, clients[0].GetProperty("percent").GetDecimal());
            Assert.Equal("Approved", (await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/referrals", J))[0].GetProperty("status").GetString());

            // changing the partner's rate later does not rewrite what the company already agreed
            await op.PutAsJsonAsync("/api/v1/partners", new { id = pid, name = "Acme Accountants", code, commissionPercent = 20, isActive = true });
            Assert.Equal(10m, (await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/clients", J))[0].GetProperty("percent").GetDecimal());

            // nothing is earned until the company pays; then 10% of the invoice before tax
            Assert.Empty((await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/statement", J)).GetProperty("lines").EnumerateArray());
            var subtotal = await PayFirstInvoiceAsync(clientId);
            var expected = Math.Round(subtotal * 0.10m, 2);
            var statement = await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/statement", J);
            Assert.Equal(expected, statement.GetProperty("lines")[0].GetProperty("commission").GetDecimal());
            Assert.Equal(expected, statement.GetProperty("owed")[0].GetProperty("amount").GetDecimal());

            // payouts: never more than is owed
            var over = await op.PostAsJsonAsync($"/api/v1/partners/{pid}/payouts", new { date = DateTime.UtcNow.ToString("yyyy-MM-dd"), amount = expected + 1, currency = "USD", reference = "EFT-1" });
            Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
            var half = Math.Round(expected / 2, 2);
            Assert.True((await op.PostAsJsonAsync($"/api/v1/partners/{pid}/payouts", new { date = DateTime.UtcNow.ToString("yyyy-MM-dd"), amount = half, currency = "USD", reference = "EFT-2" })).IsSuccessStatusCode);
            statement = await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/statement", J);
            Assert.Equal(expected - half, statement.GetProperty("owed")[0].GetProperty("amount").GetDecimal());
            Assert.Equal("EFT-2", statement.GetProperty("payouts")[0].GetProperty("reference").GetString());
            var listed = (await op.GetFromJsonAsync<JsonElement>("/api/v1/partners", J)).EnumerateArray().First(p => p.GetProperty("id").GetGuid() == pid);
            Assert.Equal(1, listed.GetProperty("clients").GetInt32()); Assert.Equal(1, listed.GetProperty("staff").GetInt32());
        }

        [Fact]
        public async Task Unknown_or_inactive_codes_are_ignored_and_the_operator_can_attach_and_detach_companies()
        {
            var op = await AdminAsync(_f);
            var (pid, code) = await NewPartnerAsync(op, "Sleepy Partners", 5);
            var staff = await StaffAsync(op, pid);
            await RegisterAndApproveAsync(op, "NOSUCHCODE", "Unreferred Co");
            Assert.Empty((await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/clients", J)).EnumerateArray());
            await op.PutAsJsonAsync("/api/v1/partners", new { id = pid, name = "Sleepy Partners", code, commissionPercent = 5, isActive = false });
            await RegisterAndApproveAsync(op, code, "Inactive Code Co");
            Assert.Empty((await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/clients", J)).EnumerateArray());

            var co = await NewCompanyAsync(_f, "Attach Me Co");
            await op.PutAsJsonAsync("/api/v1/partners", new { id = pid, name = "Sleepy Partners", code, commissionPercent = 5, isActive = true });
            var attached = await op.PutAsJsonAsync($"/api/v1/partners/clients/{co.ClientId}", new { partnerId = pid, percent = 7.5m });
            Assert.True(attached.IsSuccessStatusCode, await attached.Content.ReadAsStringAsync());
            var mine = await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/clients", J);
            Assert.Equal("Attach Me Co", mine[0].GetProperty("name").GetString()); Assert.Equal(7.5m, mine[0].GetProperty("percent").GetDecimal());
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync($"/api/v1/partners/clients/{co.ClientId}", new { partnerId = pid, percent = 101m })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await op.PutAsJsonAsync($"/api/v1/partners/clients/{co.ClientId}", new { partnerId = Guid.NewGuid() })).StatusCode);
            await op.PutAsJsonAsync($"/api/v1/partners/clients/{co.ClientId}", new { partnerId = (Guid?)null });
            Assert.Empty((await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/clients", J)).EnumerateArray());
        }

        [Fact]
        public async Task A_partner_sees_only_its_own_companies_and_nothing_else_in_the_system()
        {
            var op = await AdminAsync(_f);
            var (pid, _) = await NewPartnerAsync(op, "Walled Partners");
            var (pid2, _) = await NewPartnerAsync(op, "Other Partners");
            var staff = await StaffAsync(op, pid);
            var staff2 = await StaffAsync(op, pid2);
            var co = await NewCompanyAsync(_f, "Walled Co");
            await op.PutAsJsonAsync($"/api/v1/partners/clients/{co.ClientId}", new { partnerId = pid });
            Assert.Single((await staff.GetFromJsonAsync<JsonElement>("/api/v1/partner/clients", J)).EnumerateArray());
            Assert.Empty((await staff2.GetFromJsonAsync<JsonElement>("/api/v1/partner/clients", J)).EnumerateArray());

            // partner staff belong to no company: every company, platform and accounting door stays shut
            foreach (var path in new[] { "/api/v1/clients", "/api/v1/users", "/api/v1/devices", "/api/v1/receipts", "/api/v1/accounting/accounts", "/api/v1/accounting/documents", "/api/v1/analytics/usage", "/api/v1/partners", "/api/v1/audit/search", "/api/v1/roles", "/api/v1/settings/billing", "/api/v1/security-policy" })
                Assert.True((await staff.GetAsync(path)).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, path + " should be closed to partner staff");
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync($"/api/v1/partners/{pid}/payouts", new { date = "2026-01-01", amount = 1, currency = "USD" })).StatusCode);

            // a company administrator cannot reach the partner portal, nor hand out the Partner role
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/partner/summary")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsJsonAsync("/api/v1/users", new { email = Unique("sneak") + "@x.test", name = "S", surname = "N", role = "Partner", password = UserPassword })).StatusCode);

            // the menu a partner gets is only its own
            var login = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = (await op.GetFromJsonAsync<JsonElement>($"/api/v1/partners/{pid}/staff", J))[0].GetProperty("email").GetString(), password = UserPassword })).Json();
            var slugs = login.GetProperty("menus").EnumerateArray().Select(m => m.GetProperty("slugname").GetString()).ToArray();
            Assert.Equal(new[] { "guide", "partner/bridge", "partner/clients", "partner/commission", "partner/overview" }, slugs.OrderBy(x => x).ToArray());
            Assert.Equal("Walled Partners", login.GetProperty("user").GetProperty("clientName").GetString());
        }
    }
}
