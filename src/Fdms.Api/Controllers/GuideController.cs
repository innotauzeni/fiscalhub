using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Fdms.Api.Controllers
{
    /// <summary>
    /// The user guide, public (no sign-in) so anyone can read it before they start: one part for company users, one for the platform owner and one for
    /// partners. The text is the repository's docs/guide/{part}.md with screenshots from docs/guide/images, copied next to the application when it is built;
    /// each part also comes as a ready-made PDF (docs/guide/FiscalHub-guide-{part}.pdf).
    /// </summary>
    [ApiController, AllowAnonymous, Route("api/v1/docs/guide")]
    public class GuideController : ControllerBase
    {
        private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "docs", "guide");
        private static readonly string[] Parts = { "company", "owner", "partner" };
        private static readonly Regex ImageName = new("^[a-z0-9-]{1,80}\\.png$", RegexOptions.Compiled);
        private readonly ISettingsService _settings;
        public GuideController(ISettingsService settings) => _settings = settings;

        /// <summary>One part of the guide as Markdown, with its pictures pointing at this API.</summary>
        [HttpGet("{part}")]
        public async Task<IActionResult> Part(string part, CancellationToken ct)
        {
            if (!Parts.Contains(part)) return NotFound();
            var file = Path.Combine(Folder, part + ".md");
            if (!System.IO.File.Exists(file)) return NotFound();
            var api = (await _settings.EffectiveApiUrlAsync($"{Request.Scheme}://{Request.Host}", ct)).TrimEnd('/');
            Response.Headers[HeaderNames.CacheControl] = "public, max-age=60";
            var md = (await System.IO.File.ReadAllTextAsync(file, ct)).Replace("](images/", $"]({api}/api/v1/docs/guide/images/");
            return Content(md, "text/markdown; charset=utf-8");
        }

        [HttpGet("images/{name}")]
        public IActionResult Image(string name)
        {
            if (!ImageName.IsMatch(name)) return NotFound();
            var file = Path.Combine(Folder, "images", name);
            if (!System.IO.File.Exists(file)) return NotFound();
            Response.Headers[HeaderNames.CacheControl] = "public, max-age=86400";
            return PhysicalFile(file, "image/png");
        }

        /// <summary>The same part as a PDF to download, print or share.</summary>
        [HttpGet("{part}/pdf")]
        public IActionResult Pdf(string part)
        {
            if (!Parts.Contains(part)) return NotFound();
            var file = Path.Combine(Folder, $"FiscalHub-guide-{part}.pdf");
            if (!System.IO.File.Exists(file)) return NotFound();
            return PhysicalFile(file, "application/pdf", $"FiscalHub-user-guide-{part}.pdf");
        }
    }
}
