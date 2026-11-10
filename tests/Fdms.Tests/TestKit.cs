using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Fdms.Tests
{
    /// <summary>
    /// Test plumbing for the real access model: people sign in (JWT) and register devices; integrations call with a per-device API token.
    /// </summary>
    public static class TestKit
    {
        public const string JwtKey = "test-jwt-key-test-jwt-key-test-jwt-key-0123456789";
        public const string AdminEmail = "root@test.local";
        public const string AdminPassword = "Sup3rSecret!pw1";
        public const string UserPassword = "Passw0rd!user1";
        public static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private static int _n;

        public static string Unique(string prefix) => $"{prefix}{Interlocked.Increment(ref _n)}";

        /// <summary>Sign in and return a client that sends the access token.</summary>
        public static async Task<HttpClient> LoginAsync<T>(WebApplicationFactory<T> f, string identifier, string password) where T : class
        {
            var c = f.CreateClient();
            var res = await c.PostAsJsonAsync("/api/v1/auth/login", new { identifier, password });
            Assert.True(res.IsSuccessStatusCode, $"login failed for {identifier}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(J);
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
            return c;
        }

        public static Task<HttpClient> AdminAsync<T>(WebApplicationFactory<T> f) where T : class => LoginAsync(f, AdminEmail, AdminPassword);

        /// <summary>The audit trail of one company, read as the platform operator (the audit log is closed to everyone who belongs to a company).</summary>
        public static async Task<JsonElement> AuditAsync<T>(WebApplicationFactory<T> f, Guid clientId, string query) where T : class =>
            await (await AdminAsync(f)).GetFromJsonAsync<JsonElement>("/api/v1/audit/search?clientId=" + clientId + (query.Length > 0 ? "&" + query : ""), J);

        public sealed record Company(Guid ClientId, string AdminEmail, HttpClient Admin);

        /// <summary>The platform operator creates a company with its first administrator, who then signs in.</summary>
        public static async Task<Company> NewCompanyAsync<T>(WebApplicationFactory<T> f, string name) where T : class
        {
            var op = await AdminAsync(f);
            var email = Unique("admin") + "@" + name.ToLowerInvariant().Replace(" ", "") + ".test";
            var res = await op.PostAsJsonAsync("/api/v1/clients", new { name, adminEmail = email, adminName = "Ada", adminSurname = "Admin", adminPassword = UserPassword });
            Assert.True(res.IsSuccessStatusCode, "create company: " + await res.Content.ReadAsStringAsync());
            var id = (await res.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetGuid();
            return new Company(id, email, await LoginAsync(f, email, UserPassword));
        }

        /// <summary>Switches the Zoho Books integration on for a company, as if the operator approved it and the invoice was paid.</summary>
        public static async Task ActivateIntegrationAsync<T>(WebApplicationFactory<T> f, Guid clientId) where T : class
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
            db.IntegrationAccess.Add(new Fdms.Domain.Entities.IntegrationAccess { ClientId = clientId, Status = "Active", Uses = "fiscalise,own_api,partners", ActivatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        /// <summary>Switches a company to live devices directly (as if it had been through ZIMRA's go-live process).</summary>
        public static async Task ApproveLiveAsync<T>(WebApplicationFactory<T> f, Guid clientId) where T : class
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Fdms.Infrastructure.Persistence.FdmsDbContext>();
            var c = db.Clients.Single(x => x.Id == clientId);
            c.LiveApproved = true; c.LiveApprovedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<Fdms.Application.Abstractions.IBillingService>().ForgetAccess(clientId);
        }

        /// <summary>One fiscal document on a device (credit and debit notes point at an original).</summary>
        public static async Task<Guid> IssueAsync(HttpClient c, Guid device, string currency, string type, string number, Guid? original = null)
        {
            var res = await c.PostAsJsonAsync($"/api/v1/devices/{device}/receipts", new
            {
                externalRef = number, invoiceNo = number, receiptType = type, currency, linesTaxInclusive = true, notes = type == "FiscalInvoice" ? null : "Test " + type, originalReceiptId = original,
                lines = new[] { new { name = "Test item", hsCode = "85456852", quantity = 1, unitPrice = 11.5m, taxPercent = 15 } },
                payments = new[] { new { moneyType = "Cash", amount = 11.5m } },
            });
            Assert.True(res.IsSuccessStatusCode, $"{type} {currency}: " + await res.Content.ReadAsStringAsync());
            return (await res.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetGuid();
        }

        /// <summary>The six documents ZIMRA asks for before a taxpayer goes live: invoice, credit note and debit note in USD and in ZWG, on a test device.</summary>
        public static async Task<Guid> ZimraTestSetAsync(HttpClient c, int zimraId, string[]? only = null)
        {
            var (dev, _) = await RegisterDeviceAsync(c, zimraId, "SN-ZT" + zimraId);
            foreach (var cur in new[] { "USD", "ZWG" })
            {
                var inv = await IssueAsync(c, dev, cur, "FiscalInvoice", $"T-{zimraId}-{cur}-INV");
                foreach (var t in new[] { "CreditNote", "DebitNote" })
                    if (only == null || only.Contains(cur + " " + t)) await IssueAsync(c, dev, cur, t, $"T-{zimraId}-{cur}-{t}", inv);
            }
            return dev;
        }

        public static async Task<(Guid Id, int ZimraId)> RegisterDeviceAsync(HttpClient user, int zimraId, string serial = "SN-1")
        {
            var res = await user.PostAsJsonAsync("/api/v1/devices/register",
                new { zimraDeviceId = zimraId, activationKey = "ABCD1234", serialNo = serial, modelName = "Server", modelVersion = "v1", environment = "Test" });
            Assert.Equal(System.Net.HttpStatusCode.Created, res.StatusCode);
            return ((await res.Content.ReadFromJsonAsync<JsonElement>(J)).GetProperty("id").GetGuid(), zimraId);
        }

        public static readonly string[] AllTokenScopes = Perm.TokenScopes.ToArray();

        public sealed record Token(HttpClient Http, string Secret, Guid TokenId);

        /// <summary>A user mints an API token for ONE device.</summary>
        public static async Task<Token> NewTokenAsync<T>(WebApplicationFactory<T> f, HttpClient user, Guid deviceId, string[]? scopes = null, string name = "test") where T : class
        {
            var res = await user.PostAsJsonAsync($"/api/v1/devices/{deviceId}/tokens", new { name, scopes = scopes ?? AllTokenScopes });
            Assert.True(res.IsSuccessStatusCode, "create token: " + await res.Content.ReadAsStringAsync());
            var body = await res.Content.ReadFromJsonAsync<JsonElement>(J);
            var secret = body.GetProperty("secret").GetString()!;
            var c = f.CreateClient();
            c.DefaultRequestHeaders.Add("X-Api-Key", secret);
            return new Token(c, secret, body.GetProperty("token").GetProperty("id").GetGuid());
        }

        public static async Task<JsonElement> Json(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(J);
    }
}
