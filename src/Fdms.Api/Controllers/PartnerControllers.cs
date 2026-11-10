using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>The operator manages partners: who they are, their staff, which companies they brought, and the commission paid out.</summary>
    [ApiController, Authorize, HasPermission(Perm.PartnersManage), Route("api/v1/partners")]
    public class PartnersAdminController : ControllerBase
    {
        private readonly IPartnerService _s;
        public PartnersAdminController(IPartnerService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(ct));
        [HttpPut] public async Task<IActionResult> Save(SavePartnerRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(body, ct));
        [HttpGet("{id:guid}/staff")] public async Task<IActionResult> Staff(Guid id, CancellationToken ct) => Ok(await _s.StaffAsync(id, ct));
        [HttpPost("{id:guid}/staff")] public async Task<IActionResult> AddStaff(Guid id, AddPartnerStaffRequest body, CancellationToken ct) => Ok(await _s.AddStaffAsync(id, body, ct));
        [HttpGet("{id:guid}/statement")] public async Task<IActionResult> Statement(Guid id, CancellationToken ct) => Ok(await _s.StatementAsync(id, ct));
        [HttpPost("{id:guid}/payouts")] public async Task<IActionResult> Payout(Guid id, RecordPayoutRequest body, CancellationToken ct) => Ok(await _s.RecordPayoutAsync(id, body, ct));
        [HttpGet("clients")] public async Task<IActionResult> Clients(CancellationToken ct) => Ok(await _s.ClientLinksAsync(ct));
        [HttpPut("clients/{clientId:guid}")] public async Task<IActionResult> SetClient(Guid clientId, SetClientPartnerRequest body, CancellationToken ct) => Ok(await _s.SetClientPartnerAsync(clientId, body, ct));
    }

    /// <summary>What a partner's own staff see: the companies it brought, how they are doing, and the commission earned. Never a company's receipts, people or books.</summary>
    [ApiController, Authorize, HasPermission(Perm.PartnerPortal), Route("api/v1/partner")]
    public class PartnerPortalController : ControllerBase
    {
        private readonly IPartnerService _s;
        public PartnerPortalController(IPartnerService s) => _s = s;
        private Guid Mine => User.PartnerIdOrNull() ?? throw new GatewayException(403, "NOT_A_PARTNER", "This is only for the staff of a partner.");

        [HttpGet("summary")] public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await _s.SummaryAsync(Mine, ct));
        [HttpGet("clients")] public async Task<IActionResult> Clients(CancellationToken ct) => Ok(await _s.ClientsAsync(Mine, ct));
        [HttpGet("referrals")] public async Task<IActionResult> Referrals(CancellationToken ct) => Ok(await _s.ReferralsAsync(Mine, ct));
        [HttpGet("statement")] public async Task<IActionResult> Statement(CancellationToken ct) => Ok(await _s.StatementAsync(Mine, ct));
    }

    /// <summary>How the platform is used, for the operator.</summary>
    [ApiController, Authorize, HasPermission(Perm.AnalyticsView), Route("api/v1/analytics")]
    public class AnalyticsController : ControllerBase
    {
        private readonly IAnalyticsService _s;
        public AnalyticsController(IAnalyticsService s) => _s = s;
        [HttpGet("usage")] public async Task<IActionResult> Usage([FromQuery] int days = 30, CancellationToken ct = default) => Ok(await _s.UsageAsync(days, ct));
    }

    public sealed class DocumentUploadForm
    {
        public IFormFile? File { get; set; }
        public string? Title { get; set; }
        public string? Category { get; set; }
        public string? LinkType { get; set; }
        public Guid? LinkId { get; set; }
        public string? Notes { get; set; }
        public DateTime? RetainUntil { get; set; }
    }

    /// <summary>The company's file cabinet for its books: invoices, contracts, tax papers, attached to bills, assets or suppliers.</summary>
    [ApiController, Authorize, Route("api/v1/accounting/documents")]
    public class DocumentsController : ControllerBase
    {
        private readonly IDocumentService _s;
        public DocumentsController(IDocumentService s) => _s = s;

        [HttpGet, HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? category, [FromQuery] string? linkType, [FromQuery] Guid? linkId, CancellationToken ct) =>
            Ok(await _s.ListAsync(User.BooksClientId(), new DocumentFilter(q, category, linkType, linkId), ct));

        [HttpGet("summary"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await _s.SummaryAsync(User.BooksClientId(), ct));

        [HttpPost, HasPermission(Perm.AccountingManage), RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> Upload([FromForm] DocumentUploadForm form, CancellationToken ct)
        {
            if (form.File == null || form.File.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Choose a file to upload.");
            if (form.File.Length > 9_000_000) throw GatewayException.BadRequest("FILE_TOO_BIG", "A file can be at most 8 MB.");
            await using var ms = new MemoryStream();
            await form.File.CopyToAsync(ms, ct);
            return Ok(await _s.UploadAsync(User.BooksClientId(), new UploadDocumentRequest(form.Title ?? "", form.Category ?? "", form.LinkType, form.LinkId, form.Notes, form.RetainUntil, form.File.FileName, form.File.ContentType, ms.ToArray()), ct));
        }

        [HttpPut("{id:guid}"), HasPermission(Perm.AccountingManage)]
        public async Task<IActionResult> Update(Guid id, UpdateDocumentRequest body, CancellationToken ct) => Ok(await _s.UpdateAsync(User.BooksClientId(), id, body, ct));

        /// <summary>The file itself. Pictures and PDFs can be opened in the browser (inline=true); everything is sent with a fixed type so the browser never guesses.</summary>
        [HttpGet("{id:guid}/file"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> File(Guid id, [FromQuery] bool inline = false, CancellationToken ct = default)
        {
            var f = await _s.DownloadAsync(User.BooksClientId(), id, ct);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'";
            var openable = f.ContentType is "application/pdf" or "image/png" or "image/jpeg";
            if (inline && openable) { Response.Headers.ContentDisposition = "inline"; return base.File(f.Data, f.ContentType); }
            return base.File(f.Data, f.ContentType, f.FileName);
        }

        [HttpDelete("{id:guid}"), HasPermission(Perm.AccountingManage)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteAsync(User.BooksClientId(), id, ct); return NoContent(); }
    }
}
