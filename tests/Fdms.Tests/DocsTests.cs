using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fdms.Tests
{
    /// <summary>The integration guide is public, shows the API's own address (kept in the database), and does not drift from what the gateway really does.</summary>
    public class DocsTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        public DocsTests(AccessFactory f) => _f = f;

        private async Task<string> GuideAsync()
        {
            var res = await _f.CreateClient().GetAsync("/api/v1/docs/integration");   // no token, no sign-in
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("text/markdown", res.Content.Headers.ContentType!.MediaType);
            return await res.Content.ReadAsStringAsync();
        }

        [Fact]
        public async Task Anyone_can_read_the_user_guide_with_its_pictures_and_download_it_as_PDF()
        {
            var anon = _f.CreateClient();
            foreach (var part in new[] { "company", "owner", "partner" })
            {
                var res = await anon.GetAsync($"/api/v1/docs/guide/{part}");   // no sign-in
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var md = await res.Content.ReadAsStringAsync();
                Assert.StartsWith("# FiscalHub user guide", md);
                Assert.Contains("/api/v1/docs/guide/images/", md);          // pictures point at the API
                Assert.DoesNotContain("](images/", md);

                var pdf = await anon.GetAsync($"/api/v1/docs/guide/{part}/pdf");
                Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
                Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
                Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
            }
            var image = await anon.GetAsync("/api/v1/docs/guide/images/c01-dashboard.png");
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
            // only known parts and plain picture names: nothing else on the server can be read through it
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/docs/guide/secrets")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/docs/guide/images/..%2F..%2Fappsettings.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/docs/guide/images/company.md")).StatusCode);
        }

        [Fact]
        public async Task Anyone_can_read_the_integration_guide_without_signing_in()
        {
            var guide = await GuideAsync();
            Assert.StartsWith("# Integration guide", guide);
            foreach (var topic in new[] { "## Authentication", "## Fiscalise a sale", "## Webhooks", "## Errors", "## Time and timezones", "X-Fdms-Signature", "externalRef",
                "## Zoho Books: fiscalise your Zoho invoices", "## Zoho Books from your own system", "## Zoho bridge for partners", "INTEGRATION_NOT_ACTIVE", "```java", "```go" })
                Assert.Contains(topic, guide);
        }

        [Fact]
        public async Task The_guide_shows_the_public_API_address_stored_in_the_database_and_only_the_operator_can_change_it()
        {
            var op = await TestKit.AdminAsync(_f);
            try
            {
                // nothing stored: the guide falls back to the address the request arrived on, and the setting says so
                var before = await op.GetFromJsonAsync<JsonElement>("/api/v1/settings", J);
                Assert.Equal("request", before.GetProperty("source").GetString());

                // the operator stores the API's address (a trailing slash and a path-less form are tidied)
                var saved = await op.PutAsJsonAsync("/api/v1/settings", new { publicApiUrl = "https://localhost:7167/" });
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                var s = await saved.Json();
                Assert.Equal("https://localhost:7167", s.GetProperty("publicApiUrl").GetString());
                Assert.Equal("database", s.GetProperty("source").GetString());
                Assert.Equal("https://localhost:7167/swagger/index.html", s.GetProperty("swaggerUrl").GetString());
                Assert.Equal("https://localhost:7167/swagger/v1/swagger.json", s.GetProperty("openApiUrl").GetString());

                // every example and link in the public guide now points at the API, not at the web page that happens to show it
                var res = await _f.CreateClient().GetAsync("/api/v1/docs/integration");
                Assert.Equal("https://localhost:7167", res.Headers.GetValues("X-Gateway-Url").Single());
                var guide = await res.Content.ReadAsStringAsync();
                Assert.Contains("https://localhost:7167/api/v1/devices/YOUR_DEVICE_ID/receipts", guide);
                Assert.Contains("https://localhost:7167/swagger/index.html", guide);
                Assert.Contains("https://localhost:7167/swagger/v1/swagger.json", guide);
                Assert.DoesNotContain("your-gateway.example.com", guide);
                Assert.DoesNotContain("localhost:4200", guide);

                // a bad address is refused with a clear message and nothing changes
                foreach (var bad in new[] { "localhost:7167", "ftp://host", "https://host/?x=1", "https://user:pw@host", new string('a', 300) })
                    Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/settings", new { publicApiUrl = bad })).StatusCode);
                Assert.Equal("https://localhost:7167", (await op.GetFromJsonAsync<JsonElement>("/api/v1/settings", J)).GetProperty("publicApiUrl").GetString());

                // company staff cannot see or change platform settings; the change is audited
                var co = await TestKit.NewCompanyAsync(_f, "Settings Co");
                Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/settings")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PutAsJsonAsync("/api/v1/settings", new { publicApiUrl = "https://evil.example" })).StatusCode);
                var trail = (await op.GetFromJsonAsync<JsonElement>("/api/v1/audit/search?action=settings", J)).GetProperty("items").EnumerateArray().Select(r => r.GetProperty("detail").GetString()).ToList();
                Assert.Contains(trail, d => d!.Contains("https://localhost:7167"));
            }
            finally
            {
                // leave the shared test host as found
                await op.PutAsJsonAsync("/api/v1/settings", new { publicApiUrl = "" });
            }
            Assert.Equal("request", (await op.GetFromJsonAsync<JsonElement>("/api/v1/settings", J)).GetProperty("source").GetString());
        }

        [Fact]
        public async Task The_address_is_filled_in_once_on_first_start_and_never_overwritten()
        {
            using var scope = _f.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var op = await TestKit.AdminAsync(_f);
            try
            {
                Assert.False(await settings.EnsureDefaultAsync("not a url", default));                                 // unusable candidates are ignored
                Assert.True(await settings.EnsureDefaultAsync("https://localhost:7167", default));                     // first start: stored
                Assert.False(await settings.EnsureDefaultAsync("https://other.example", default));                     // a stored value is never replaced
                Assert.Equal("https://localhost:7167", (await op.GetFromJsonAsync<JsonElement>("/api/v1/settings", J)).GetProperty("publicApiUrl").GetString());
            }
            finally { await op.PutAsJsonAsync("/api/v1/settings", new { publicApiUrl = "" }); }
        }

        [Fact]
        public async Task Every_webhook_event_the_gateway_can_send_is_documented()
        {
            var guide = await GuideAsync();
            var events = typeof(WebhookEvents).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
            Assert.NotEmpty(events);
            Assert.All(events, e => Assert.True(guide.Contains($"`{e}`"), $"the guide does not mention the webhook event {e}"));
        }

        [Fact]
        public async Task The_api_reference_and_the_openapi_document_are_public_too()
        {
            var http = _f.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/swagger/index.html")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/swagger/v1/swagger.json")).StatusCode);
            var json = await http.GetStringAsync("/swagger/v1/swagger.json");
            Assert.Contains("/api/v1/devices/{id}/receipts", json);
            Assert.Contains("/app/docs", json);   // the API reference points developers at the guide
        }
    }
}
