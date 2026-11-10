using Fdms.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Fdms.Api.Controllers
{
    /// <summary>
    /// The integration guide, public (no sign-in) so any developer can read it. It is the repository's docs/INTEGRATION.md, copied next to the application
    /// when it is built, so the page in the console, the repository and this endpoint can never drift apart. The example address in it is replaced with the
    /// public API address stored in the database (Settings page), so the examples and links point at the API and work as written.
    /// </summary>
    [ApiController, Route("api/v1/docs")]
    public class DocsController : ControllerBase
    {
        public const string Placeholder = "https://your-gateway.example.com";
        private static readonly string File = Path.Combine(AppContext.BaseDirectory, "docs", "INTEGRATION.md");
        private readonly ISettingsService _settings;
        public DocsController(ISettingsService settings) => _settings = settings;

        /// <summary>The integration guide as Markdown (also rendered at /app/docs). The header X-Gateway-Url carries the API address used in the examples.</summary>
        [HttpGet("integration"), AllowAnonymous]
        public async Task<IActionResult> Integration(CancellationToken ct)
        {
            if (!System.IO.File.Exists(File)) return NotFound();
            var baseUrl = await _settings.EffectiveApiUrlAsync($"{Request.Scheme}://{Request.Host}", ct);
            Response.Headers["X-Gateway-Url"] = baseUrl;
            Response.Headers[HeaderNames.CacheControl] = "public, max-age=60";
            Response.Headers[HeaderNames.Vary] = "Host";
            return Content((await System.IO.File.ReadAllTextAsync(File, ct)).Replace(Placeholder, baseUrl), "text/markdown; charset=utf-8");
        }
    }
}
