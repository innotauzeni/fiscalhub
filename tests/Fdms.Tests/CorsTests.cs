using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Fdms.Tests
{
    /// <summary>The web console on its own site (Cors:Origins) can call the API from the browser, error answers included; other sites cannot.</summary>
    public class CorsTests : IClassFixture<ExtrasFactory>
    {
        private const string Console = "https://fiscal.example.com";
        private readonly HttpClient _c;

        public CorsTests(ExtrasFactory f) => _c = f.WithWebHostBuilder(b => b.UseSetting("Cors:Origins", Console + "/")).CreateClient();

        private static HttpRequestMessage Preflight(string origin) => new(HttpMethod.Options, "/api/v1/auth/login")
        {
            Headers = { { "Origin", origin }, { "Access-Control-Request-Method", "POST" }, { "Access-Control-Request-Headers", "content-type,authorization" } },
        };

        [Fact]
        public async Task Listed_site_gets_preflight_and_error_answers_with_cors_headers()
        {
            var pre = await _c.SendAsync(Preflight(Console));
            Assert.True(pre.IsSuccessStatusCode, ((int)pre.StatusCode).ToString());
            Assert.Equal(Console, pre.Headers.GetValues("Access-Control-Allow-Origin").Single());

            // a refused sign-in (thrown inside the app, turned into a problem answer) still carries the header, so the page can show the message
            var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { identifier = "nobody@example.com", password = "wrong-password-1" }) };
            login.Headers.Add("Origin", Console);
            var r = await _c.SendAsync(login);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal(Console, r.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }

        [Fact]
        public async Task Other_sites_get_no_cors_headers()
        {
            var pre = await _c.SendAsync(Preflight("https://evil.example.net"));
            Assert.False(pre.Headers.Contains("Access-Control-Allow-Origin"));
        }
    }
}
