using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Fdms.Tests
{
    public sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly string _keys = Path.Combine(Path.GetTempPath(), "fdms-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestKit.JwtKey,
                ["Seed:AdminEmail"] = TestKit.AdminEmail,
                ["Seed:AdminPassword"] = TestKit.AdminPassword,
                ["Seed:ForcePasswordChange"] = "false",
                ["RateLimit:LoginPerMinute"] = "100000",
                ["Auth:ActiveCacheSeconds"] = "0",
                ["DataProtection:KeysPath"] = _keys,
                ["RateLimit:PerMinute"] = "100000",
                ["Fdms:DistributedLocks"] = "false",
            }));
            builder.ConfigureServices(s =>
            {
                TestDb.Use(s, _db);
                s.RemoveAll<IFdmsGateway>();
                s.AddSingleton<IFdmsGateway>(Fdms);
                // keep background jobs out of the way of deterministic tests
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    public class GatewayApiTests : IClassFixture<GatewayFactory>
    {
        private readonly GatewayFactory _f;
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private static int _deviceSeq = 5000;

        public GatewayApiTests(GatewayFactory f) => _f = f;

        /// <summary>A company, a registered device, and an API token bound to that device (what an integration would use).</summary>
        private async Task<(HttpClient Http, Guid Device, HttpClient User)> SetupAsync(string name)
        {
            var co = await TestKit.NewCompanyAsync(_f, name);
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _deviceSeq));
            var token = await TestKit.NewTokenAsync(_f, co.Admin, dev);
            return (token.Http, dev, co.Admin);
        }

        private static object Invoice(string extRef, decimal price = 115m, int taxId = 3) => new
        {
            externalRef = extRef, invoiceNo = "INV-" + extRef, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
            lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = price, taxId } },
        };

        [Fact]
        public async Task Requests_without_valid_credentials_are_rejected()
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/v1/devices")).StatusCode);
            var bad = _f.CreateClient(); bad.DefaultRequestHeaders.Add("X-Api-Key", "fdms_nope");
            Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("/api/v1/devices")).StatusCode);
            var junk = _f.CreateClient(); junk.DefaultRequestHeaders.Authorization = new("Bearer", "not.a.jwt");
            Assert.Equal(HttpStatusCode.Unauthorized, (await junk.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/v1/clients")).StatusCode);
        }

        [Fact]
        public async Task Full_day_register_open_invoice_credit_close()
        {
            var (c, deviceId, _) = await SetupAsync("Full day Co");

            // invoice (auto-opens the fiscal day)
            var r1 = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("A1"));
            Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
            var inv = await r1.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("Submitted", inv.GetProperty("status").GetString());
            Assert.Equal(1, inv.GetProperty("receiptCounter").GetInt32());
            Assert.Equal(115m, inv.GetProperty("total").GetDecimal());
            Assert.StartsWith("https://invoice.zimra.co.zw/", inv.GetProperty("qrUrl").GetString());
            var invId = inv.GetProperty("id").GetGuid();

            // second invoice continues the chain (FDMS verifies counter, global no and previous hash)
            var r2 = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("A2", 23m, 3));
            Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
            Assert.Equal(2, (await r2.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("receiptCounter").GetInt32());

            // credit note against the first invoice
            var cn = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", new
            {
                externalRef = "C1", invoiceNo = "CN-1", receiptType = "CreditNote", currency = "USD", linesTaxInclusive = true,
                notes = "Returned", originalReceiptId = invId,
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115m, taxId = 3 } },
            });
            Assert.Equal(HttpStatusCode.Created, cn.StatusCode);
            Assert.Equal(-115m, (await cn.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("total").GetDecimal());

            // a second credit for the same goods exceeds what is left on the invoice
            var over = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", new
            {
                externalRef = "C2", invoiceNo = "CN-2", receiptType = "CreditNote", currency = "USD", linesTaxInclusive = true,
                notes = "Again", originalReceiptId = invId,
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115m, taxId = 3 } },
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
            Assert.Equal("RCPT035", (await over.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("errorCode").GetString());

            // close: FDMS verifies the fiscal-day signature over our counters
            var close = await c.PostAsync($"/api/v1/devices/{deviceId}/day/close", null);
            Assert.Equal(HttpStatusCode.OK, close.StatusCode);
            Assert.Equal("FiscalDayClosed", (await close.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("status").GetString());

            // QR image
            var qr = await c.GetAsync($"/api/v1/receipts/{invId}/qr");
            Assert.Equal("image/png", qr.Content.Headers.ContentType!.MediaType);
        }

        [Fact]
        public async Task Same_external_ref_returns_the_original_receipt_without_a_new_number()
        {
            var (c, deviceId, _) = await SetupAsync("Idem Co");
            var a = await (await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("X1"))).Content.ReadFromJsonAsync<JsonElement>(J);
            var again = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("X1"));
            var b = await again.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(a.GetProperty("id").GetGuid(), b.GetProperty("id").GetGuid());
            var next = await (await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("X2"))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(a.GetProperty("receiptCounter").GetInt32() + 1, next.GetProperty("receiptCounter").GetInt32());
        }

        [Fact]
        public async Task Fdms_outage_queues_the_receipt_and_the_retry_delivers_it_in_order()
        {
            var (c, deviceId, _) = await SetupAsync("Outage Co");
            await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("O1"));

            _f.Fdms.FailNextSubmitTransiently = true;
            var queued = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("O2"));
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            var q = await queued.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("Pending", q.GetProperty("status").GetString());

            // closing is blocked while something is undelivered
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/v1/devices/{deviceId}/day/close", null)).StatusCode);

            // the retry resends the IDENTICAL receipt (same counter/number), then the chain continues
            var retried = await c.PostAsync($"/api/v1/receipts/{q.GetProperty("id").GetGuid()}/retry", null);
            Assert.Equal("Submitted", (await retried.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("status").GetString());
            var o3 = await (await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("O3"))).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(3, o3.GetProperty("receiptCounter").GetInt32());
        }

        [Fact]
        public async Task Permanent_fdms_rejection_gives_the_numbers_back()
        {
            var (c, deviceId, _) = await SetupAsync("Reject Co");
            await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("R1"));

            _f.Fdms.RejectNextSubmit = true;
            var bad = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("R2"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

            // the next receipt reuses counter 2: the sequence has no hole (FDMS would flag RCPT011 otherwise)
            var ok = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("R3"));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
            Assert.Equal(2, (await ok.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("receiptCounter").GetInt32());
        }

        [Fact]
        public async Task Another_companys_data_is_invisible_to_its_users_and_its_tokens()
        {
            var (a, deviceA, userA) = await SetupAsync("Tenant A");
            var (b, deviceB, userB) = await SetupAsync("Tenant B");
            var rec = await (await a.PostAsJsonAsync($"/api/v1/devices/{deviceA}/receipts", Invoice("T1"))).Content.ReadFromJsonAsync<JsonElement>(J);
            var recId = rec.GetProperty("id").GetGuid();

            foreach (var other in new[] { b, userB })   // B's token and B's signed-in administrator
            {
                Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/devices/{deviceA}")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/devices/{deviceA}/receipts", Invoice("T2"))).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/receipts/{recId}")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/v1/devices/{deviceA}/day/close", null)).StatusCode);
            }
            Assert.Equal(HttpStatusCode.NotFound, (await userB.PostAsJsonAsync($"/api/v1/devices/{deviceA}/tokens", new { name = "x", scopes = new[] { "receipts.view" } })).StatusCode);
            Assert.Equal(1, (await b.GetFromJsonAsync<JsonElement>("/api/v1/devices", J)).GetArrayLength());   // only its own
        }

        [Fact]
        public async Task A_revoked_token_stops_working_at_once()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Revoke Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _deviceSeq));
            var token = await TestKit.NewTokenAsync(_f, co.Admin, dev);
            Assert.Equal(HttpStatusCode.OK, (await token.Http.GetAsync("/api/v1/devices")).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/tokens/{token.TokenId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await token.Http.GetAsync("/api/v1/devices")).StatusCode);
        }

        [Fact]
        public async Task Bad_input_is_reported_with_the_fdms_rule_code()
        {
            var (c, deviceId, user) = await SetupAsync("Validation Co");
            var res = await c.PostAsJsonAsync($"/api/v1/devices/{deviceId}/receipts", Invoice("V1", 10m, taxId: 99));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            Assert.Equal("RCPT025", (await res.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("errorCode").GetString());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await user.PostAsJsonAsync("/api/v1/devices/register",
                new { zimraDeviceId = 9999, activationKey = "BAD", serialNo = "S", modelName = "m", modelVersion = "v" })).StatusCode);
        }
    }
}
