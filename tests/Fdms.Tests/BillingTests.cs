using System.Net;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Infrastructure.Billing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fdms.Tests
{
    /// <summary>Paynow stand-in: starts payments instantly and answers polls from a dictionary the test controls. Messages are verified by the real hash code.</summary>
    /// <summary>Fixed credentials for the stand-in, so the real hash code can verify signed messages.</summary>
    public sealed class StaticGatewaySettings : IPaymentGatewaySettings
    {
        private readonly string _key;
        public StaticGatewaySettings(string key) => _key = key;
        public Task<IReadOnlyDictionary<string, string>?> CredentialsAsync(string code, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["integrationId"] = "1", ["integrationKey"] = _key, ["baseUrl"] = "https://www.paynow.co.zw" });
        public Task<IReadOnlyList<PaymentGatewayDto>> ListAsync(string apiBase, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<PayMethodDto>> MethodsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<PaymentGatewayDto> SaveAsync(string code, SaveGatewayRequest req, string apiBase, CancellationToken ct) => throw new NotSupportedException();
    }

    public sealed class FakePaynow : IPaynowGateway
    {
        public const string Key = "paynow-test-key";
        private readonly PaynowGateway _real = new(new HttpClient(), new StaticGatewaySettings(Key), NullLogger<PaynowGateway>.Instance);
        public Dictionary<string, PaynowStatus> Polls { get; } = new();
        public List<(string Reference, decimal Amount, string? Method, string? Phone)> Started { get; } = new();
        public Task<bool> IsConfiguredAsync(CancellationToken ct) => Task.FromResult(true);

        public Task<PaynowInit> InitiateAsync(string reference, decimal amount, string email, string info, string returnUrl, string resultUrl, string? method, string? phone, CancellationToken ct)
        {
            Started.Add((reference, amount, method, phone));
            return Task.FromResult(new PaynowInit(true, method == null ? "https://paynow.test/pay/" + reference : null, "https://paynow.test/poll/" + reference, method == null ? null : "Check your phone to approve", "PN-" + reference, null));
        }

        public Task<PaynowStatus?> PollAsync(string pollUrl, CancellationToken ct) => Task.FromResult(Polls.TryGetValue(pollUrl, out var s) ? s : null);
        public Task<PaynowStatus?> ParseResultAsync(IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken ct) => _real.ParseResultAsync(fields, ct);

        /// <summary>A callback body exactly as Paynow would sign it.</summary>
        public static List<KeyValuePair<string, string>> Signed(string reference, decimal amount, string status, string? key = null)
        {
            var f = new List<KeyValuePair<string, string>> { new("reference", reference), new("paynowreference", "PN-" + reference), new("amount", amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), new("status", status), new("pollurl", "https://paynow.test/poll/" + reference) };
            f.Add(new("hash", PaynowGateway.Hash(f.Select(x => x.Value), key ?? Key)));
            return f;
        }
    }

    public sealed class FakeEcoCash : IEcoCashGateway
    {
        public Dictionary<string, EcoCashStatus> Statuses { get; } = new();
        public List<(string Reference, decimal Amount, string Phone)> Charges { get; } = new();
        public Task<bool> IsConfiguredAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<ChargeResult> ChargeAsync(string reference, decimal amount, string currency, string phone, string description, string notifyUrl, CancellationToken ct)
        { Charges.Add((reference, amount, phone)); return Task.FromResult(new ChargeResult(true, "Pending", "ECO-" + reference, "Approve the prompt on your phone.")); }
        public Task<EcoCashStatus?> StatusAsync(string phone, string reference, CancellationToken ct) => Task.FromResult(Statuses.TryGetValue(reference, out var st) ? st : null);
    }

    public sealed class FakeStripe : IStripeGateway
    {
        public const string WebhookSecret = "whsec_test";
        public Dictionary<string, StripeSession> Sessions { get; } = new();
        public Task<bool> IsConfiguredAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<ChargeResult> CreateCheckoutAsync(string reference, decimal amount, string currency, string email, string description, string successUrl, string cancelUrl, CancellationToken ct) =>
            Task.FromResult(new ChargeResult(true, "Pending", "cs_" + reference, null, "https://checkout.stripe.test/" + reference));
        public Task<StripeSession?> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult(Sessions.TryGetValue(sessionId, out var x) ? x : null);
        public Task<StripeSession?> ParseWebhookAsync(string payload, string signatureHeader, CancellationToken ct)
        {
            if (!StripeGateway.VerifySignature(payload, signatureHeader, WebhookSecret, DateTimeOffset.UtcNow)) return Task.FromResult<StripeSession?>(null);
            using var doc = JsonDocument.Parse(payload); var o = doc.RootElement.GetProperty("data").GetProperty("object");
            return Task.FromResult<StripeSession?>(new StripeSession(o.GetProperty("id").GetString()!, o.GetProperty("payment_status").GetString() == "paid", o.GetProperty("amount_total").GetInt64() / 100m, "USD", o.GetProperty("client_reference_id").GetString()));
        }
        public static string Sign(string payload, string secret = WebhookSecret, long? ts = null)
        {
            var t = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var mac = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes($"{t}.{payload}"))).ToLowerInvariant();
            return $"t={t},v1={mac}";
        }
    }

    public sealed class BillingFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        public FakePaynow Paynow { get; } = new();
        public FakeEcoCash EcoCash { get; } = new();
        public FakeStripe Stripe { get; } = new();
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly string _keys = Path.Combine(Path.GetTempPath(), "fdms-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestKit.JwtKey, ["Seed:AdminEmail"] = TestKit.AdminEmail, ["Seed:AdminPassword"] = TestKit.AdminPassword, ["Seed:ForcePasswordChange"] = "false",
                ["DataProtection:KeysPath"] = _keys, ["RateLimit:PerMinute"] = "100000", ["RateLimit:LoginPerMinute"] = "100000", ["Fdms:DistributedLocks"] = "false", ["Auth:ActiveCacheSeconds"] = "0",
            }));
            builder.ConfigureServices(s =>
            {
                TestDb.Use(s, _db);
                s.RemoveAll<IFdmsGateway>(); s.AddSingleton<IFdmsGateway>(Fdms);
                s.RemoveAll<IPaynowGateway>(); s.AddSingleton<IPaynowGateway>(Paynow);
                s.RemoveAll<IEcoCashGateway>(); s.AddSingleton<IEcoCashGateway>(EcoCash);
                s.RemoveAll<IStripeGateway>(); s.AddSingleton<IStripeGateway>(Stripe);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    /// <summary>Plans, registration with approval, subscriptions, invoices, Paynow and bank payments, plan limits.</summary>
    public class BillingTests : IClassFixture<BillingFactory>
    {
        private readonly BillingFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 21000;
        public BillingTests(BillingFactory f) => _f = f;

        private static object Reg(string email, string company = "Zed Traders", int? planId = null, string? website = null, string password = TestKit.UserPassword) =>
            new { companyName = company, tin = "1234567890", contactName = "Zed", contactSurname = "Moyo", email, phone = "0771234567", password, planId, billingCycle = "monthly", message = "hello", website };

        private async Task<JsonElement> PlanByCode(HttpClient anon, string code) =>
            (await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/plans", J)).EnumerateArray().First(p => p.GetProperty("code").GetString() == code);

        /// <summary>A registered, approved company on a plan, with no trial so its first invoice exists straight away.</summary>
        private async Task<(Guid ClientId, string Email, HttpClient Admin, HttpClient Op)> ApprovedAsync(string code = "business", int trialDays = 0)
        {
            var anon = _f.CreateClient();
            var email = TestKit.Unique("reg") + "@billing.test";
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", Reg(email, planId: (await PlanByCode(anon, code)).GetProperty("id").GetInt32()))).StatusCode);
            var op = await TestKit.AdminAsync(_f);
            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications?status=Pending", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            var res = await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/approve", new { trialDays });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var clientId = (await res.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("clientId").GetGuid();
            return (clientId, email, await TestKit.LoginAsync(_f, email, TestKit.UserPassword), op);
        }

        private static async Task<JsonElement> Open(HttpClient admin) =>
            (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").EnumerateArray().First(i => i.GetProperty("status").GetString() == "Open");

        [Fact]
        public async Task A_logo_sent_with_the_registration_is_checked_shown_to_the_reviewer_and_becomes_the_companys_invoice_logo()
        {
            var anon = _f.CreateClient(); var op = await TestKit.AdminAsync(_f);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");   // a 1x1 PNG
            var data = "data:image/png;base64," + Convert.ToBase64String(png);

            // not a real picture, wrong type, or far too big: refused with a clear message
            foreach (var bad in new[] { "data:image/png;base64,AAAA", "data:image/svg+xml;base64,PHN2Zy8+", "not a data url", "data:image/png;base64," + new string('A', 450_000) })
            {
                var r = await anon.PostAsJsonAsync("/api/v1/public/register", new { companyName = "Logo Bad Co", contactName = "L", contactSurname = "M", email = TestKit.Unique("lb") + "@reg.test", password = TestKit.UserPassword, billingCycle = "monthly", logoData = bad });
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
                var txt = await r.Content.ReadAsStringAsync(); Assert.True(txt.Contains("LOGO_INVALID"), bad[..Math.Min(40, bad.Length)] + " => " + txt);
            }

            var email = TestKit.Unique("logo") + "@reg.test";
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", new { companyName = "Logo Co", contactName = "L", contactSurname = "M", email, password = TestKit.UserPassword, billingCycle = "monthly", logoData = data })).StatusCode);
            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            Assert.True(app.GetProperty("hasLogo").GetBoolean());
            var id = app.GetProperty("id").GetGuid();
            var shown = await op.GetAsync($"/api/v1/applications/{id}/logo");
            Assert.Equal("image/png", shown.Content.Headers.ContentType?.MediaType);
            Assert.Equal(png.Length, (await shown.Content.ReadAsByteArrayAsync()).Length);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/applications/{id}/logo")).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/applications/{id}/approve", new { trialDays = 14 })).StatusCode);
            var co = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            var templates = (await co.GetFromJsonAsync<JsonElement>("/api/v1/templates", J)).EnumerateArray().ToList();
            var def = Assert.Single(templates);
            Assert.True(def.GetProperty("hasLogo").GetBoolean());
            Assert.Equal(png.Length, (await (await co.GetAsync($"/api/v1/templates/{def.GetProperty("id").GetGuid()}/logo")).Content.ReadAsByteArrayAsync()).Length);
        }

        [Fact]
        public async Task Anyone_can_see_the_public_plans_and_who_runs_the_service_but_not_inactive_ones()
        {
            var anon = _f.CreateClient();
            var plans = (await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/plans", J)).EnumerateArray().ToList();
            Assert.True(plans.Count >= 3);
            Assert.All(plans, p => Assert.True(p.GetProperty("isPublic").GetBoolean() && p.GetProperty("isActive").GetBoolean()));
            Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/v1/public/info")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/plans")).StatusCode);
        }

        [Fact]
        public async Task Plans_are_validated_and_a_plan_in_use_cannot_be_deleted()
        {
            var op = await TestKit.AdminAsync(_f);
            var bad = await op.PutAsJsonAsync("/api/v1/plans", new { id = 0, code = "x y", name = "", priceMonthly = -1, priceYearly = 0, currency = "USD", maxDevices = 0, maxUsers = 0, maxBranches = 0, maxReceiptsPerMonth = 0, features = new string[0], trialDays = 0, isActive = true, isPublic = true, sortOrder = 9 });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var code = "t" + Interlocked.Increment(ref _seq);
            var ok = await op.PutAsJsonAsync("/api/v1/plans", new { id = 0, code, name = "Tiny", priceMonthly = 5, priceYearly = 50, currency = "USD", maxDevices = 1, maxUsers = 1, maxBranches = 1, maxReceiptsPerMonth = 10, features = new[] { "One" }, trialDays = 0, isActive = true, isPublic = true, sortOrder = 9 });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            var id = (await ok.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.NoContent, (await op.DeleteAsync($"/api/v1/plans/{id}")).StatusCode);

            var used = await PlanByCode(_f.CreateClient(), "business");
            await ApprovedAsync("business");
            Assert.Equal(HttpStatusCode.Conflict, (await op.DeleteAsync($"/api/v1/plans/{used.GetProperty("id").GetInt32()}")).StatusCode);
        }

        [Fact]
        public async Task Registration_waits_for_approval_and_tells_the_applicant_where_they_stand()
        {
            var anon = _f.CreateClient();
            var email = TestKit.Unique("wait") + "@billing.test";
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", Reg(email))).StatusCode);

            var pending = await anon.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = TestKit.UserPassword });
            Assert.Equal(HttpStatusCode.Forbidden, pending.StatusCode);
            Assert.Contains("APPLICATION_PENDING", await pending.Content.ReadAsStringAsync());
            // a stranger guessing the address learns nothing
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = "Wrong!pass12" })).StatusCode);

            var op = await TestKit.AdminAsync(_f);
            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications?status=Pending", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/reject", new { reason = "" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/reject", new { reason = "Unverifiable company" })).StatusCode);

            var rejected = await anon.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = TestKit.UserPassword });
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            var text = await rejected.Content.ReadAsStringAsync();
            Assert.Contains("APPLICATION_REJECTED", text);
            Assert.Contains("Unverifiable company", text);
            Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/approve", new { })).StatusCode);   // already decided
        }

        [Fact]
        public async Task Registration_rejects_bad_input_ignores_bots_and_does_not_reveal_existing_accounts()
        {
            var anon = _f.CreateClient();
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/v1/public/register", Reg("not-an-email"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/v1/public/register", Reg(TestKit.Unique("w") + "@billing.test", password: "short"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/v1/public/register", Reg(TestKit.Unique("w") + "@billing.test", planId: 99999))).StatusCode);

            var bot = TestKit.Unique("bot") + "@billing.test";
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", Reg(bot, website: "http://spam"))).StatusCode);   // answered like a success...
            var op = await TestKit.AdminAsync(_f);
            Assert.DoesNotContain((await op.GetFromJsonAsync<JsonElement>("/api/v1/applications", J)).EnumerateArray(), a => a.GetProperty("email").GetString() == bot);   // ...but nothing is stored

            var (_, existing, _, _) = await ApprovedAsync();
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", Reg(existing))).StatusCode);
            var twice = TestKit.Unique("dup") + "@billing.test";
            await anon.PostAsJsonAsync("/api/v1/public/register", Reg(twice));
            Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsJsonAsync("/api/v1/public/register", Reg(twice))).StatusCode);
            Assert.Single((await op.GetFromJsonAsync<JsonElement>("/api/v1/applications", J)).EnumerateArray().Where(a => a.GetProperty("email").GetString() == twice));
        }

        [Fact]
        public async Task An_approved_company_signs_in_as_its_administrator_with_a_subscription_and_a_first_invoice()
        {
            var (clientId, _, admin, op) = await ApprovedAsync("business");
            var me = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", J);
            Assert.Equal(clientId, me.GetProperty("clientId").GetGuid());
            Assert.Contains("ClientAdmin", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

            var o = await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J);
            Assert.Equal("Active", o.GetProperty("subscription").GetProperty("status").GetString());
            Assert.Equal("Business", o.GetProperty("plan").GetProperty("name").GetString());
            Assert.Equal(1, o.GetProperty("invoices").GetArrayLength());
            Assert.Equal(1, o.GetProperty("usage").GetProperty("users").GetInt32());
            Assert.Contains(o.GetProperty("invoices").EnumerateArray().Select(i => i.GetProperty("number").GetString()), n => n!.StartsWith("INV-"));
            Assert.Contains((await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/billing/subscriptions", J)).EnumerateArray(), s => s.GetProperty("clientId").GetGuid() == clientId);
        }

        [Fact]
        public async Task A_trial_has_no_invoice_until_it_ends_and_billing_is_only_for_company_administrators()
        {
            var (_, _, admin, op) = await ApprovedAsync("starter", trialDays: 14);
            var o = await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J);
            Assert.Equal("Trial", o.GetProperty("subscription").GetProperty("status").GetString());
            Assert.Equal(0, o.GetProperty("invoices").GetArrayLength());
            Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v1/billing")).StatusCode);   // the platform operator has no company of its own
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/v1/billing")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/applications")).StatusCode);
        }

        [Fact]
        public async Task A_bank_transfer_waits_for_the_operator_then_pays_the_invoice_and_extends_the_period()
        {
            var (_, _, admin, op) = await ApprovedAsync();
            var inv = await Open(admin);
            var id = inv.GetProperty("id").GetGuid();

            var profile = await op.GetFromJsonAsync<JsonElement>("/api/v1/settings/billing", J);
            var p = JsonSerializer.Deserialize<Dictionary<string, object?>>(profile.GetProperty("profile").GetRawText(), J)!;
            p["companyName"] = "Gateway Ltd"; p["bankName"] = "CBZ"; p["accountName"] = "Gateway Ltd"; p["accountNumber"] = "1234567"; p["invoicePrefix"] = "INV";
            var saved = await op.PutAsJsonAsync("/api/v1/settings/billing", p);
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.BadRequest, (await Bank(admin, id, "")).StatusCode);   // needs a reference or a proof
            var before = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("subscription").GetProperty("currentPeriodEnd").GetDateTime();
            var res = await Bank(admin, id, "REF-1");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var payment = await res.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("AwaitingConfirmation", payment.GetProperty("status").GetString());
            Assert.Equal("Open", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());   // not paid until confirmed

            var pid = payment.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pid}/confirm", new { })).StatusCode);   // the company cannot confirm its own payment (a locked account cannot reach it at all)
            Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pid}/confirm", new { reference = "BANK-77" })).StatusCode);

            var paid = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J);
            Assert.Equal("Paid", paid.GetProperty("status").GetString());
            Assert.Equal("Bank transfer", paid.GetProperty("paymentMethod").GetString());
            Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("subscription").GetProperty("currentPeriodEnd").GetDateTime() >= before);
            Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pid}/confirm", new { })).StatusCode);   // only once
            Assert.Equal(HttpStatusCode.Conflict, (await Bank(admin, id, "REF-2")).StatusCode);   // nothing left to pay
        }

        private static async Task<HttpResponseMessage> Bank(HttpClient admin, Guid invoiceId, string reference, byte[]? proof = null)
        {
            var form = new MultipartFormDataContent { { new StringContent(reference), "Reference" } };
            if (proof != null) { var file = new ByteArrayContent(proof); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf"); form.Add(file, "Proof", "proof.pdf"); }
            return await admin.PostAsync($"/api/v1/billing/invoices/{invoiceId}/pay/bank", form);
        }

        [Fact]
        public async Task A_rejected_bank_payment_leaves_the_invoice_open_and_a_proof_is_checked_and_stored()
        {
            var (_, _, admin, op) = await ApprovedAsync();
            var p = JsonSerializer.Deserialize<Dictionary<string, object?>>((await op.GetFromJsonAsync<JsonElement>("/api/v1/settings/billing", J)).GetProperty("profile").GetRawText(), J)!;
            p["companyName"] = "Gateway Ltd"; p["accountNumber"] = "1234567"; p["bankName"] = "CBZ";
            await op.PutAsJsonAsync("/api/v1/settings/billing", p);
            var id = (await Open(admin)).GetProperty("id").GetGuid();

            Assert.Equal(HttpStatusCode.BadRequest, (await Bank(admin, id, "R", System.Text.Encoding.ASCII.GetBytes("not a pdf, png or jpeg"))).StatusCode);
            var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 fake proof");
            var res = await Bank(admin, id, "R-9", pdf);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var pid = (await res.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetGuid();
            Assert.Equal(pdf, await (await op.GetAsync($"/api/v1/admin/billing/payments/{pid}/proof")).Content.ReadAsByteArrayAsync());

            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pid}/reject", new { reason = "" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pid}/reject", new { reason = "No such deposit" })).StatusCode);
            Assert.Equal("Open", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.OK, (await Bank(admin, id, "R-10")).StatusCode);   // may try again
        }

        [Fact]
        public async Task Paynow_web_and_mobile_payments_are_applied_by_polling_and_by_the_signed_callback()
        {
            var (_, _, admin, _) = await ApprovedAsync();
            var inv = await Open(admin);
            var id = inv.GetProperty("id").GetGuid();
            var total = inv.GetProperty("total").GetDecimal();

            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/paynow", new { method = "ecocash", phone = "12" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/paynow", new { method = "bitcoin" })).StatusCode);

            var res = await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/paynow", new { method = "web" });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var pay = await res.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.StartsWith("https://paynow.test/pay/", pay.GetProperty("redirectUrl").GetString());
            var reference = pay.GetProperty("reference").GetString()!;
            Assert.Equal(total, _f.Paynow.Started.Last().Amount);

            // still pending: the customer has not paid yet
            Assert.Equal("Pending", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/payments/{pay.GetProperty("id").GetGuid()}", J)).GetProperty("status").GetString());

            // a message that is not signed by Paynow is refused and changes nothing
            var forged = await _f.CreateClient().PostAsync("/api/v1/payments/paynow/result", new FormUrlEncodedContent(FakePaynow.Signed(reference, total, "Paid", key: "wrong-key")));
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            Assert.Equal("Open", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());

            // the signed callback pays it
            var ok = await _f.CreateClient().PostAsync("/api/v1/payments/paynow/result", new FormUrlEncodedContent(FakePaynow.Signed(reference, total, "Paid")));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var paid = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J);
            Assert.Equal("Paid", paid.GetProperty("status").GetString());
            Assert.Equal("Paynow", paid.GetProperty("paymentMethod").GetString());
            // repeating the callback is harmless
            Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().PostAsync("/api/v1/payments/paynow/result", new FormUrlEncodedContent(FakePaynow.Signed(reference, total, "Paid")))).StatusCode);
            Assert.Equal(1, paid.GetProperty("payments").GetArrayLength());
        }

        [Fact]
        public async Task A_mobile_wallet_payment_is_confirmed_by_polling_and_a_wrong_amount_is_never_applied()
        {
            var (_, _, admin, _) = await ApprovedAsync();
            var inv = await Open(admin);
            var id = inv.GetProperty("id").GetGuid();
            var total = inv.GetProperty("total").GetDecimal();

            var pay = await (await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/paynow", new { method = "ecocash", phone = "0771234567" })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("Check your phone to approve", pay.GetProperty("instructions").GetString());
            Assert.Equal(("ecocash", "0771234567"), (_f.Paynow.Started.Last().Method, _f.Paynow.Started.Last().Phone));
            var reference = pay.GetProperty("reference").GetString()!;

            // Paynow says it was paid, but for the wrong amount: not applied
            _f.Paynow.Polls["https://paynow.test/poll/" + reference] = new PaynowStatus("Paid", reference, "PN-1", total - 1, null);
            var refreshed = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/payments/{pay.GetProperty("id").GetGuid()}", J);
            Assert.Equal("Failed", refreshed.GetProperty("status").GetString());
            Assert.Equal("Open", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());

            // a fresh attempt for the right amount is applied when polled
            var again = await (await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/paynow", new { method = "onemoney", phone = "0711234567" })).Content.ReadFromJsonAsync<JsonElement>(J);
            var reference2 = again.GetProperty("reference").GetString()!;
            _f.Paynow.Polls["https://paynow.test/poll/" + reference2] = new PaynowStatus("Paid", reference2, "PN-2", total, null);
            Assert.Equal("Paid", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/payments/{again.GetProperty("id").GetGuid()}", J)).GetProperty("status").GetString());
            Assert.Equal("Paid", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());
        }

        [Fact]
        public async Task One_company_cannot_see_or_pay_another_companys_invoice()
        {
            var a = await ApprovedAsync(); var b = await ApprovedAsync();
            var id = (await Open(a.Admin)).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/v1/billing/invoices/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync($"/api/v1/billing/invoices/{id}/pdf")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/paynow", new { method = "web" })).StatusCode);
        }

        [Fact]
        public async Task The_operator_can_record_a_payment_made_in_cash_and_download_the_invoice_pdf()
        {
            var (_, _, admin, op) = await ApprovedAsync();
            var id = (await Open(admin)).GetProperty("id").GetGuid();
            var pdf = await admin.GetByteArrayAsync($"/api/v1/billing/invoices/{id}/pdf");
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{id}/mark-paid", new { method = "Barter" })).StatusCode);
            var res = await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{id}/mark-paid", new { method = "Cash", reference = "Receipt 12" });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            Assert.Equal("Paid", (await res.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("invoice").GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{id}/mark-paid", new { method = "Cash" })).StatusCode);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(await admin.GetByteArrayAsync($"/api/v1/billing/invoices/{id}/pdf"), 0, 4));
        }

        [Fact]
        public async Task Changing_to_a_bigger_plan_issues_an_invoice_and_switches_when_it_is_paid()
        {
            var (_, _, admin, op) = await ApprovedAsync("starter");
            var anon = _f.CreateClient();
            var business = await PlanByCode(anon, "business");
            var first = (await Open(admin)).GetProperty("id").GetGuid();
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{first}/mark-paid", new { method = "Cash" });

            var res = await admin.PostAsJsonAsync("/api/v1/billing/change-plan", new { planId = business.GetProperty("id").GetInt32(), billingCycle = "monthly" });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var change = await res.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("Open", change.GetProperty("status").GetString());
            Assert.Equal("Starter", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("plan").GetProperty("name").GetString());   // not switched until paid

            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{change.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" });
            Assert.Equal("Business", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("plan").GetProperty("name").GetString());
        }

        [Fact]
        public async Task A_company_cannot_exceed_the_devices_users_or_branches_of_its_plan()
        {
            var op = await TestKit.AdminAsync(_f);
            var code = "lim" + Interlocked.Increment(ref _seq);
            var made = await op.PutAsJsonAsync("/api/v1/plans", new { id = 0, code, name = "Limited", priceMonthly = 10, priceYearly = 100, currency = "USD", maxDevices = 1, maxUsers = 1, maxBranches = 1, maxReceiptsPerMonth = 0, features = new string[0], trialDays = 0, isActive = true, isPublic = true, sortOrder = 8 });
            Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
            var planId = (await made.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetInt32();

            var anon = _f.CreateClient();
            var email = TestKit.Unique("lim") + "@billing.test";
            await anon.PostAsJsonAsync("/api/v1/public/register", Reg(email, planId: planId));
            var app = (await op.GetFromJsonAsync<JsonElement>("/api/v1/applications?status=Pending", J)).EnumerateArray().First(a => a.GetProperty("email").GetString() == email);
            await op.PostAsJsonAsync($"/api/v1/applications/{app.GetProperty("id").GetGuid()}/approve", new { trialDays = 0 });
            var admin = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{(await Open(admin)).GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" });   // a new account is locked until its first invoice is paid
            admin = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);

            await TestKit.RegisterDeviceAsync(admin, 31000 + Interlocked.Increment(ref _seq));
            var second = await admin.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = 32000 + Interlocked.Increment(ref _seq), activationKey = "ABCD1234", serialNo = "SN-2", modelName = "Server", modelVersion = "v1", environment = "Test" });
            Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
            Assert.Contains("PLAN_LIMIT", await second.Content.ReadAsStringAsync());

            // the company already has its one user (the administrator)
            var user = await admin.PostAsJsonAsync("/api/v1/users", new { email = TestKit.Unique("u") + "@billing.test", name = "U", surname = "U", password = TestKit.UserPassword, role = "Operator" });
            Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
            Assert.Contains("PLAN_LIMIT", await user.Content.ReadAsStringAsync());

            // upgrading frees the room
            var branch = await admin.PutAsJsonAsync("/api/v1/branches", new { id = (Guid?)null, code = "B1", name = "Branch one", isActive = true });
            Assert.True(branch.IsSuccessStatusCode, await branch.Content.ReadAsStringAsync());
            var branch2 = await admin.PutAsJsonAsync("/api/v1/branches", new { id = (Guid?)null, code = "B2", name = "Branch two", isActive = true });
            Assert.Equal(HttpStatusCode.Forbidden, branch2.StatusCode);
        }


        [Fact]
        public async Task A_new_account_with_an_unpaid_invoice_can_only_use_billing_until_it_is_paid()
        {
            var (_, email, _, op) = await ApprovedAsync("business");
            var login = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = TestKit.UserPassword });
            var body = await login.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.True(body.GetProperty("billingLocked").GetBoolean());
            Assert.Equal(new[] { "billing" }, body.GetProperty("menus").EnumerateArray().Select(m => m.GetProperty("slugname").GetString()).ToArray());
            var admin = _f.CreateClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());

            var blocked = await admin.GetAsync("/api/v1/devices");
            Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
            Assert.Contains("PAYMENT_REQUIRED", await blocked.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/billing")).StatusCode);   // billing itself stays open

            var id = (await Open(admin)).GetProperty("id").GetGuid();
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{id}/mark-paid", new { method = "Cash" });
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/devices")).StatusCode);   // opens at once
            var again = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = TestKit.UserPassword })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.False(again.GetProperty("billingLocked").GetBoolean());
            Assert.True(again.GetProperty("menus").GetArrayLength() > 3);
        }

        [Fact]
        public async Task A_trial_may_use_demo_devices_only_and_live_devices_need_a_paid_plan()
        {
            var (_, _, admin, op) = await ApprovedAsync("starter", trialDays: 14);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/devices")).StatusCode);   // a trial is not locked
            var live = await admin.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = 41000 + Interlocked.Increment(ref _seq), activationKey = "ABCD1234", serialNo = "SN-L", modelName = "Server", modelVersion = "v1", environment = "Production" });
            Assert.Equal(HttpStatusCode.Forbidden, live.StatusCode);
            Assert.Contains("TRIAL_DEMO_ONLY", await live.Content.ReadAsStringAsync());
            await TestKit.RegisterDeviceAsync(admin, 42000 + Interlocked.Increment(ref _seq));   // demo (Test) is fine
        }


        [Fact]
        public async Task Gateway_credentials_are_saved_in_the_database_encrypted_and_never_returned()
        {
            var op = await TestKit.AdminAsync(_f);
            var anon = _f.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/settings/gateways")).StatusCode);
            var (_, _, company, _) = await ApprovedAsync("business");
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{(await Open(company)).GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" });
            Assert.Equal(HttpStatusCode.Forbidden, (await company.GetAsync("/api/v1/settings/gateways")).StatusCode);   // a company cannot see them

            var list = await op.GetFromJsonAsync<JsonElement>("/api/v1/settings/gateways", J);
            var paynow = list.EnumerateArray().First(g => g.GetProperty("code").GetString() == "paynow");
            Assert.False(paynow.GetProperty("configured").GetBoolean());

            // enabling without credentials is refused; a bad address is refused
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/gateways/paynow", new { enabled = true, values = new Dictionary<string, string?>() })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/gateways/paynow", new { enabled = true, values = new Dictionary<string, string?> { ["integrationId"] = "12345", ["integrationKey"] = "k", ["baseUrl"] = "https://evil.example.com" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/gateways/paynow", new { enabled = true, values = new Dictionary<string, string?> { ["integrationId"] = "abc", ["integrationKey"] = "k" } })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await op.PutAsJsonAsync("/api/v1/settings/gateways/unknown", new { enabled = true })).StatusCode);

            const string secret = "SUPER-SECRET-INTEGRATION-KEY";
            var saved = await op.PutAsJsonAsync("/api/v1/settings/gateways/paynow", new { enabled = true, values = new Dictionary<string, string?> { ["integrationId"] = "12345", ["integrationKey"] = secret } });
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var body = await saved.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, body);
            var dto = JsonSerializer.Deserialize<JsonElement>(body, J);
            Assert.True(dto.GetProperty("configured").GetBoolean());
            Assert.Equal("database", dto.GetProperty("source").GetString());
            var key = dto.GetProperty("fields").EnumerateArray().First(f => f.GetProperty("key").GetString() == "integrationKey");
            Assert.True(key.GetProperty("hasValue").GetBoolean());
            Assert.False(key.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String);
            Assert.DoesNotContain(secret, await (await op.GetAsync("/api/v1/settings/gateways")).Content.ReadAsStringAsync());

            // stored encrypted, and the audit trail never holds the secret
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                var row = db.SystemSettings.First(x => x.Key == "Gateway:paynow");
                Assert.DoesNotContain(secret, row.Value);
                Assert.DoesNotContain(db.AuditLogs.Where(a => a.Action == "gateway.update").AsEnumerable(), a => (a.Detail ?? "").Contains(secret));

                // the real gateway reads what was saved (no restart) and verifies callbacks with the saved key
                var real = new PaynowGateway(new HttpClient(), scope.ServiceProvider.GetRequiredService<IPaymentGatewaySettings>(), NullLogger<PaynowGateway>.Instance);
                Assert.True(await real.IsConfiguredAsync(CancellationToken.None));
                Assert.NotNull(await real.ParseResultAsync(FakePaynow.Signed("REF-1", 10m, "Paid", key: secret), CancellationToken.None));
                Assert.Null(await real.ParseResultAsync(FakePaynow.Signed("REF-1", 10m, "Paid", key: "other"), CancellationToken.None));
            }

            // saving again with the key left empty keeps the saved key; disabling switches it off
            Assert.True((await op.PutAsJsonAsync("/api/v1/settings/gateways/paynow", new { enabled = true, values = new Dictionary<string, string?> { ["integrationId"] = "99999", ["integrationKey"] = "" } })).IsSuccessStatusCode);
            using (var scope = _f.Services.CreateScope())
            {
                var real = new PaynowGateway(new HttpClient(), scope.ServiceProvider.GetRequiredService<IPaymentGatewaySettings>(), NullLogger<PaynowGateway>.Instance);
                Assert.NotNull(await real.ParseResultAsync(FakePaynow.Signed("REF-1", 10m, "Paid", key: secret), CancellationToken.None));
            }
            var off = await (await op.PutAsJsonAsync("/api/v1/settings/gateways/paynow", new { enabled = false, values = new Dictionary<string, string?>() })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.False(off.GetProperty("configured").GetBoolean());
            var cleared = await (await op.PutAsJsonAsync("/api/v1/settings/gateways/paynow", new { enabled = false, clearSecrets = true })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.False(cleared.GetProperty("fields").EnumerateArray().First(f => f.GetProperty("key").GetString() == "integrationKey").GetProperty("hasValue").GetBoolean());
        }


        private async Task SaveGateway(HttpClient op, string code, object values)
        {
            var res = await op.PutAsJsonAsync($"/api/v1/settings/gateways/{code}", new { enabled = true, values });
            Assert.True(res.IsSuccessStatusCode, code + ": " + await res.Content.ReadAsStringAsync());
        }

        /// <summary>A company whose first invoice is already paid, with a second open invoice (a plan change) to pay in the test.</summary>
        private async Task<(HttpClient Admin, HttpClient Op, Guid InvoiceId, decimal Total)> OpenInvoiceAsync()
        {
            var (_, _, admin, op) = await ApprovedAsync("starter");
            var first = (await Open(admin)).GetProperty("id").GetGuid();
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{first}/mark-paid", new { method = "Cash" });
            admin = await TestKit.LoginAsync(_f, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", J)).GetProperty("email").GetString()!, TestKit.UserPassword);
            var business = await PlanByCode(_f.CreateClient(), "business");
            var change = await (await admin.PostAsJsonAsync("/api/v1/billing/change-plan", new { planId = business.GetProperty("id").GetInt32(), billingCycle = "monthly" })).Content.ReadFromJsonAsync<JsonElement>(J);
            return (admin, op, change.GetProperty("id").GetGuid(), change.GetProperty("total").GetDecimal());
        }

        [Fact]
        public async Task The_checkout_offers_exactly_the_methods_the_operator_has_connected()
        {
            var op = await TestKit.AdminAsync(_f);
            await SaveGateway(op, "ecocash-manual", new Dictionary<string, string?> { ["number"] = "0771234567" });
            await SaveGateway(op, "paypal", new Dictionary<string, string?> { ["email"] = "pay@gateway.test" });
            await SaveGateway(op, "stripe", new Dictionary<string, string?> { ["secretKey"] = "sk_test_abc", ["webhookSecret"] = "whsec_abc" });
            await SaveGateway(op, "ecocash", new Dictionary<string, string?> { ["username"] = "u", ["password"] = "p", ["merchantCode"] = "287164", ["merchantPin"] = "1234", ["merchantNumber"] = "778503033" });
            var (admin, _, _, _) = await OpenInvoiceAsync();
            var methods = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("options").GetProperty("methods").EnumerateArray().ToList();
            var ids = methods.Select(m => m.GetProperty("id").GetString()).ToList();
            Assert.Contains("stripe", ids); Assert.Contains("ecocash", ids); Assert.Contains("ecocash-manual", ids); Assert.Contains("paypal", ids); Assert.Contains("bank", ids);
            Assert.Equal("0771234567", methods.First(m => m.GetProperty("id").GetString() == "ecocash-manual").GetProperty("details").GetString());
            Assert.False(methods.First(m => m.GetProperty("id").GetString() == "paypal").GetProperty("automatic").GetBoolean());

            // disabling one removes it from checkout straight away
            await op.PutAsJsonAsync("/api/v1/settings/gateways/paypal", new { enabled = false, values = new Dictionary<string, string?>() });
            var after = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("options").GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
            Assert.DoesNotContain("paypal", after);
            // the gateway list tells automatic from manual, and rejects bad input
            var all = (await op.GetFromJsonAsync<JsonElement>("/api/v1/settings/gateways", J)).EnumerateArray().ToList();
            Assert.Equal(new[] { "paynow", "ecocash", "stripe", "ecocash-manual", "paypal" }, all.Select(g => g.GetProperty("code").GetString()).ToArray());
            Assert.True(all.First(g => g.GetProperty("code").GetString() == "stripe").GetProperty("automatic").GetBoolean());
            Assert.False(all.First(g => g.GetProperty("code").GetString() == "paypal").GetProperty("automatic").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/gateways/stripe", new { enabled = true, values = new Dictionary<string, string?> { ["secretKey"] = "not-a-stripe-key" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/gateways/paypal", new { enabled = true, values = new Dictionary<string, string?> { ["email"] = "nope" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/gateways/ecocash", new { enabled = true, values = new Dictionary<string, string?> { ["baseUrl"] = "https://evil.example.com" } })).StatusCode);
        }

        [Fact]
        public async Task An_ecocash_push_payment_is_confirmed_by_asking_ecocash_and_never_by_the_notification_body()
        {
            var (admin, _, id, total) = await OpenInvoiceAsync();
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/ecocash", new { phone = "12" })).StatusCode);
            var pay = await (await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/ecocash", new { phone = "0771234567" })).Content.ReadFromJsonAsync<JsonElement>(J);
            var reference = pay.GetProperty("reference").GetString()!;
            Assert.Equal((reference, total, "0771234567"), _f.EcoCash.Charges.Last());

            // a notification claiming success changes nothing: EcoCash itself says it is still pending
            var anon = _f.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/v1/payments/ecocash/notify", new { clientCorrelator = reference, transactionStatus = "COMPLETED" })).StatusCode);
            Assert.Equal("Open", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());

            _f.EcoCash.Statuses[reference] = new EcoCashStatus("Paid", total, "ECO-REF-1");
            Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/v1/payments/ecocash/notify", new { clientCorrelator = reference })).StatusCode);
            var paid = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J);
            Assert.Equal("Paid", paid.GetProperty("status").GetString());
            Assert.Equal("EcoCash", paid.GetProperty("paymentMethod").GetString());
            // an unknown reference gets the same quiet answer
            Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/v1/payments/ecocash/notify", new { clientCorrelator = "nope" })).StatusCode);
        }

        [Fact]
        public async Task A_stripe_card_payment_is_confirmed_by_the_signed_webhook_and_a_forged_one_is_refused()
        {
            var (admin, _, id, total) = await OpenInvoiceAsync();
            var pay = await (await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/stripe", new { })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.StartsWith("https://checkout.stripe.test/", pay.GetProperty("redirectUrl").GetString());
            var reference = pay.GetProperty("reference").GetString()!;
            string Event(decimal amount) => JsonSerializer.Serialize(new { type = "checkout.session.completed", data = new { @object = new { id = "cs_" + reference, payment_status = "paid", amount_total = (long)(amount * 100), client_reference_id = reference } } });
            async Task<HttpStatusCode> Post(string body, string sig) { var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/stripe/webhook") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }; req.Headers.Add("Stripe-Signature", sig); return (await _f.CreateClient().SendAsync(req)).StatusCode; }

            var good = Event(total);
            Assert.Equal(HttpStatusCode.BadRequest, await Post(good, FakeStripe.Sign(good, "whsec_wrong")));            // wrong key
            Assert.Equal(HttpStatusCode.BadRequest, await Post(good, FakeStripe.Sign(good, ts: DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds())));   // too old: replay
            Assert.Equal(HttpStatusCode.BadRequest, await Post(good, ""));
            Assert.Equal("Open", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());

            var wrong = Event(total - 1);   // signed, but for the wrong amount: never applied
            Assert.Equal(HttpStatusCode.OK, await Post(wrong, FakeStripe.Sign(wrong)));
            Assert.Equal("Open", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());

            var retry = await (await admin.PostAsJsonAsync($"/api/v1/billing/invoices/{id}/pay/stripe", new { })).Content.ReadFromJsonAsync<JsonElement>(J);
            var reference2 = retry.GetProperty("reference").GetString()!;
            _f.Stripe.Sessions["cs_" + reference2] = new StripeSession("cs_" + reference2, true, total, "USD", reference2);   // the customer comes back: the page asks Stripe
            Assert.Equal("Paid", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/payments/{retry.GetProperty("id").GetGuid()}", J)).GetProperty("status").GetString());
            var paid = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J);
            Assert.Equal("Card (Stripe)", paid.GetProperty("paymentMethod").GetString());
        }

        [Fact]
        public async Task Manual_methods_wait_for_the_operator_to_confirm_like_a_bank_transfer()
        {
            var op = await TestKit.AdminAsync(_f);
            await SaveGateway(op, "paypal", new Dictionary<string, string?> { ["email"] = "pay@gateway.test" });
            var (admin, _, id, _) = await OpenInvoiceAsync();
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/billing/invoices/{id}/pay/manual/bitcoin", new MultipartFormDataContent { { new StringContent("x"), "Reference" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/billing/invoices/{id}/pay/manual/paypal", new MultipartFormDataContent { { new StringContent(""), "Reference" } })).StatusCode);
            var res = await admin.PostAsync($"/api/v1/billing/invoices/{id}/pay/manual/paypal", new MultipartFormDataContent { { new StringContent("PP-8812"), "Reference" } });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var pay = await res.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("PayPal", pay.GetProperty("method").GetString());
            Assert.Equal("AwaitingConfirmation", pay.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pay.GetProperty("id").GetGuid()}/confirm", new { })).StatusCode);
            Assert.Equal("PayPal", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("paymentMethod").GetString());
        }

        [Fact]
        public void The_adapters_understand_numbers_statuses_and_stripe_signatures()
        {
            Assert.Equal("263771234567", EcoCashGateway.ToMsisdn("0771234567")); Assert.Equal("263771234567", EcoCashGateway.ToMsisdn("+263 77 123 4567")); Assert.Null(EcoCashGateway.ToMsisdn("12"));
            Assert.Equal("Paid", EcoCashGateway.MapStatus("COMPLETED")); Assert.Equal("Failed", EcoCashGateway.MapStatus("declined")); Assert.Equal("Pending", EcoCashGateway.MapStatus("INITIATED"));
            var body = "{\"x\":1}"; var header = FakeStripe.Sign(body, "whsec_k");
            Assert.True(StripeGateway.VerifySignature(body, header, "whsec_k", DateTimeOffset.UtcNow));
            Assert.False(StripeGateway.VerifySignature(body + " ", header, "whsec_k", DateTimeOffset.UtcNow));   // tampered body
            Assert.False(StripeGateway.VerifySignature(body, header, "whsec_other", DateTimeOffset.UtcNow));
        }


        private async Task<(Guid ClientId, string Email, HttpClient Admin, HttpClient Op)> LockedCompanyAsync()
        {
            var (clientId, email, admin, op) = await ApprovedAsync("starter");   // no trial: the first invoice is open, so everything but Billing is closed
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/v1/devices")).StatusCode);
            return (clientId, email, admin, op);
        }

        [Fact]
        public async Task A_cash_payment_can_be_settled_with_a_one_time_activation_code_the_company_types_in()
        {
            var (_, email, admin, op) = await LockedCompanyAsync();
            var id = (await Open(admin)).GetProperty("id").GetGuid();

            var res = await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{id}/mark-paid", new { method = "Cash", reference = "Receipt 77", sendCode = true });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(J);
            var code = body.GetProperty("code").GetProperty("code").GetString()!;
            Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", code);
            Assert.Equal("Open", body.GetProperty("invoice").GetProperty("status").GetString());   // not activated until the code is typed in
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/v1/devices")).StatusCode);

            // the code is stored only as a hash, and never written to the audit trail
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                var raw = code.Replace("-", "");
                Assert.DoesNotContain(db.ActivationCodes.AsEnumerable(), c => c.CodeHash.Contains(raw) || c.Salt.Contains(raw));
                Assert.DoesNotContain(db.AuditLogs.Where(a => a.Action.StartsWith("code.")).AsEnumerable(), a => (a.Detail ?? "").Contains(raw));
            }

            // wrong code, then the right one (typed sloppily: lower case, spaces, O for 0)
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/billing/redeem", new { code = "AAAA-BBBB-CCCC" })).StatusCode);
            var sloppy = " " + code.Replace('0', 'O').ToLowerInvariant().Replace("-", " ") + " ";
            var ok = await admin.PostAsJsonAsync("/api/v1/billing/redeem", new { code = sloppy });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/devices")).StatusCode);   // the system is up
            Assert.Equal("Paid", (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{id}", J)).GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/billing/redeem", new { code })).StatusCode);   // single use
            var login = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = TestKit.UserPassword })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.False(login.GetProperty("billingLocked").GetBoolean());
        }

        [Fact]
        public async Task A_code_works_only_for_its_own_company_and_five_wrong_guesses_lock_the_attempt()
        {
            var (_, _, admin, op) = await LockedCompanyAsync();
            var other = await ApprovedAsync("starter");
            var id = (await Open(admin)).GetProperty("id").GetGuid();
            var code = (await (await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{id}/mark-paid", new { method = "Cash", sendCode = true })).Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("code").GetProperty("code").GetString()!;

            // another company cannot use it
            Assert.Equal(HttpStatusCode.BadRequest, (await other.Admin.PostAsJsonAsync("/api/v1/billing/redeem", new { code })).StatusCode);
            Assert.Equal(HttpStatusCode.PaymentRequired, (await other.Admin.GetAsync("/api/v1/devices")).StatusCode);

            // guessing is counted per company and locks: even the right code is refused while locked
            for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/billing/redeem", new { code = $"AAAA-BBBB-CC{i}{i}" })).StatusCode);
            var locked = await admin.PostAsJsonAsync("/api/v1/billing/redeem", new { code });
            Assert.Equal((HttpStatusCode)429, locked.StatusCode);
            Assert.Contains("CODE_LOCKED", await locked.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/v1/devices")).StatusCode);
        }

        [Fact]
        public async Task A_bank_payment_with_proof_is_confirmed_by_sending_a_code_and_a_rejected_one_cancels_it()
        {
            var op = await TestKit.AdminAsync(_f);
            var p = JsonSerializer.Deserialize<Dictionary<string, object?>>((await op.GetFromJsonAsync<JsonElement>("/api/v1/settings/billing", J)).GetProperty("profile").GetRawText(), J)!;
            p["companyName"] = "Gateway Ltd"; p["accountNumber"] = "1234567"; p["bankName"] = "CBZ";
            await op.PutAsJsonAsync("/api/v1/settings/billing", p);
            var (_, _, admin, _) = await LockedCompanyAsync();
            var id = (await Open(admin)).GetProperty("id").GetGuid();
            var pay = await (await Bank(admin, id, "CBZ-5521", System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 proof"))).Content.ReadFromJsonAsync<JsonElement>(J);
            var pid = pay.GetProperty("id").GetGuid();

            var sent = await (await op.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pid}/confirm", new { sendCode = true })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("CodeSent", sent.GetProperty("payment").GetProperty("status").GetString());
            var code1 = sent.GetProperty("code").GetProperty("code").GetString()!;
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/v1/devices")).StatusCode);

            // rejecting after the code was sent revokes the code
            Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/admin/billing/payments/{pid}/reject", new { reason = "Deposit reversed" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/billing/redeem", new { code = code1 })).StatusCode);
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/v1/devices")).StatusCode);
        }

        [Fact]
        public async Task A_company_cannot_issue_confirm_or_skip_payment_itself()
        {
            var (_, _, admin, _) = await LockedCompanyAsync();
            var id = (await Open(admin)).GetProperty("id").GetGuid();
            foreach (var path in new[] { $"/api/v1/admin/billing/invoices/{id}/mark-paid", $"/api/v1/admin/billing/payments/{Guid.NewGuid()}/confirm", "/api/v1/admin/go-live", "/api/v1/plans", "/api/v1/settings/gateways" })
            {
                var r = await admin.PostAsJsonAsync(path, new { method = "Cash", sendCode = false });
                Assert.Contains(r.StatusCode, new[] { HttpStatusCode.PaymentRequired, HttpStatusCode.Forbidden, HttpStatusCode.MethodNotAllowed });
                Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
            }
            // case changes and look-alike prefixes do not slip past the lock
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/API/V1/DEVICES")).StatusCode);
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/v1/billingx/devices")).StatusCode);
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/v1/authx")).StatusCode);
        }

        [Fact]
        public async Task An_expired_subscription_locks_everything_but_billing_until_the_renewal_invoice_is_settled()
        {
            var (clientId, email, admin, op) = await ApprovedAsync("starter");
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{(await Open(admin)).GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" });
            admin = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/devices")).StatusCode);

            // the paid period ran out more than the grace period ago
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                var sub = db.Subscriptions.First(x => x.ClientId == clientId); sub.CurrentPeriodStart = DateTime.UtcNow.AddDays(-40); sub.CurrentPeriodEnd = DateTime.UtcNow.AddDays(-10); db.SaveChanges();
                scope.ServiceProvider.GetRequiredService<IBillingService>().ForgetAccess(clientId);
            }
            var blocked = await admin.GetAsync("/api/v1/devices");
            Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
            Assert.Contains("expired", await blocked.Content.ReadAsStringAsync());
            var login = await (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = TestKit.UserPassword })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.True(login.GetProperty("billingLocked").GetBoolean());
            Assert.Equal(new[] { "billing" }, login.GetProperty("menus").EnumerateArray().Select(m => m.GetProperty("slugname").GetString()).ToArray());
            Assert.Equal("EXPIRED", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("access").GetProperty("reason").GetString());

            // renewing makes the invoice (a fresh period from today), and paying it opens everything up
            var inv = await (await admin.PostAsJsonAsync("/api/v1/billing/renew", new { })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("Open", inv.GetProperty("status").GetString());
            Assert.True(inv.GetProperty("periodEnd").GetDateTime() > DateTime.UtcNow.AddDays(20));
            Assert.Equal(inv.GetProperty("id").GetGuid(), (await (await admin.PostAsJsonAsync("/api/v1/billing/renew", new { })).Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetGuid());   // idempotent
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{inv.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" });
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/devices")).StatusCode);
        }

        [Fact]
        public async Task A_trial_that_has_ended_locks_the_account_until_a_plan_is_paid()
        {
            var (clientId, _, admin, op) = await ApprovedAsync("starter", trialDays: 14);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/devices")).StatusCode);
            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
                var sub = db.Subscriptions.First(x => x.ClientId == clientId); sub.TrialEndsAt = DateTime.UtcNow.AddDays(-1); sub.CurrentPeriodEnd = sub.TrialEndsAt.Value; db.SaveChanges();
                scope.ServiceProvider.GetRequiredService<IBillingService>().ForgetAccess(clientId);
            }
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Equal("TRIAL_EXPIRED", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("access").GetProperty("reason").GetString());
            var inv = await (await admin.PostAsJsonAsync("/api/v1/billing/renew", new { })).Content.ReadFromJsonAsync<JsonElement>(J);
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{inv.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" });
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/devices")).StatusCode);
        }

        [Fact]
        public async Task Live_devices_need_a_paid_plan_and_the_operators_approval_of_a_go_live_request()
        {
            var (clientId, email, admin, op) = await ApprovedAsync("business");
            await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{(await Open(admin)).GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" });
            admin = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            object Live(int n) => new { zimraDeviceId = n, activationKey = "ABCD1234", serialNo = "SN-L", modelName = "Server", modelVersion = "v1", environment = "Production" };

            // paid, but not approved for live yet
            var refused = await admin.PostAsJsonAsync("/api/v1/devices/register", Live(51000 + Interlocked.Increment(ref _seq)));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("LIVE_NOT_APPROVED", await refused.Content.ReadAsStringAsync());
            await TestKit.RegisterDeviceAsync(admin, 52000 + Interlocked.Increment(ref _seq));   // demo is always fine

            var status = await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J);
            Assert.False(status.GetProperty("canRequest").GetBoolean());   // ZIMRA's six test documents come first
            await TestKit.ZimraTestSetAsync(admin, 54000 + Interlocked.Increment(ref _seq));
            status = await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J);
            Assert.True(status.GetProperty("canRequest").GetBoolean()); Assert.False(status.GetProperty("approved").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "", contactPhone = "1" })).StatusCode);
            var req = await admin.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "Tendai Moyo", contactPhone = "0771234567", zimraTin = "1234567890", notes = "3 tills" });
            Assert.True(req.IsSuccessStatusCode, await req.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "Tendai", contactPhone = "0771234567" })).StatusCode);   // one at a time

            // a company cannot approve itself
            var rid = (await req.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"/api/v1/admin/go-live/{rid}/approve", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/admin/go-live")).StatusCode);

            Assert.Contains((await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/go-live?status=Pending", J)).EnumerateArray(), r => r.GetProperty("id").GetGuid() == rid);
            Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/admin/go-live/{rid}/approve", new { note = "Welcome" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync($"/api/v1/admin/go-live/{rid}/approve", new { })).StatusCode);
            var live = await admin.PostAsJsonAsync("/api/v1/devices/register", Live(53000 + Interlocked.Increment(ref _seq)));
            Assert.Equal(HttpStatusCode.Created, live.StatusCode);

            // the operator can switch it off again
            Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/v1/admin/go-live/clients/{clientId}/revoke", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/v1/devices/register", Live(54000 + Interlocked.Increment(ref _seq)))).StatusCode);
        }

        [Fact]
        public async Task Going_live_cannot_be_requested_while_on_a_trial_or_unpaid()
        {
            var (_, _, trial, _) = await ApprovedAsync("starter", trialDays: 14);
            var s = await trial.GetFromJsonAsync<JsonElement>("/api/v1/billing/go-live", J);
            Assert.False(s.GetProperty("canRequest").GetBoolean());
            Assert.Equal(HttpStatusCode.Conflict, (await trial.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "Tendai Moyo", contactPhone = "0771234567" })).StatusCode);
            var (_, _, locked, _) = await LockedCompanyAsync();
            Assert.Equal(HttpStatusCode.Conflict, (await locked.PostAsJsonAsync("/api/v1/billing/go-live", new { contactName = "Tendai Moyo", contactPhone = "0771234567" })).StatusCode);
        }

        [Fact]
        public async Task The_billing_invoice_template_previews_as_an_image_and_rejects_a_bad_colour()
        {
            var op = await TestKit.AdminAsync(_f);
            var profile = (await op.GetFromJsonAsync<JsonElement>("/api/v1/settings/billing", J)).GetProperty("profile");
            var p = JsonSerializer.Deserialize<Dictionary<string, object?>>(profile.GetRawText(), J)!;
            p["companyName"] = "Gateway Ltd"; p["accent"] = "#0f766e";
            var png = await op.PostAsJsonAsync("/api/v1/settings/billing/preview", p);
            Assert.Equal(HttpStatusCode.OK, png.StatusCode);
            Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
            Assert.Equal(0x89, (await png.Content.ReadAsByteArrayAsync())[0]);

            p["accent"] = "red; drop table";
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings/billing", p)).StatusCode);
        }

        [Fact]
        public async Task Maintenance_issues_the_renewal_invoice_before_a_period_ends_and_only_once()
        {
            var (clientId, _, admin, op) = await ApprovedAsync("starter", trialDays: 3);   // the trial ends inside the renewal window
            Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").GetArrayLength());
            using var scope = _f.Services.CreateScope();
            var billing = scope.ServiceProvider.GetRequiredService<Fdms.Application.Abstractions.IBillingService>();
            Assert.True(await billing.RunMaintenanceAsync(CancellationToken.None) >= 1);
            var invoices = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices");
            Assert.Equal(1, invoices.GetArrayLength());
            await billing.RunMaintenanceAsync(CancellationToken.None);
            Assert.Equal(1, (await admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J)).GetProperty("invoices").GetArrayLength());
            Assert.Equal(clientId, (await op.GetFromJsonAsync<JsonElement>("/api/v1/admin/billing/subscriptions", J)).EnumerateArray().First(s => s.GetProperty("clientId").GetGuid() == clientId).GetProperty("clientId").GetGuid());
        }
    }
}
