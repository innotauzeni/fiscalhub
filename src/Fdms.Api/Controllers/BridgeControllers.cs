using System.Text;
using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>
    /// The Zoho bridge for a partner's system (bridge key only, never a signed-in person). Calls below <c>companies/{id}/zoho/</c> go to that company's Zoho Books
    /// as they are, in Zoho's own format (https://www.zoho.com/books/api/v3/), with the company's organisation filled in; only the modules the company shared are reachable.
    /// </summary>
    [ApiController, Authorize(AuthenticationSchemes = AuthSchemes.Bridge), HasPermission(Perm.BridgeUse), Route("api/v1/bridge")]
    public class BridgeController : ControllerBase
    {
        private readonly IBridgeService _s;
        public BridgeController(IBridgeService s) => _s = s;

        private BridgeCaller Caller => new(User.PartnerIdOrNull(), User.ClientIdOrNull(), Guid.Parse(User.FindFirst(BridgeKeyHandler.KeyClaim)!.Value), User.Identity?.Name ?? "",
            (User.FindFirst(BridgeKeyHandler.ModulesClaim)?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), User.FindFirst(BridgeKeyHandler.WriteClaim)?.Value == "1");

        /// <summary>The Zoho Books modules the bridge offers.</summary>
        [HttpGet("modules")] public IActionResult Modules() => Ok(_s.Modules);

        /// <summary>The companies that shared their Zoho Books with you, what they shared, and whether they are ready.</summary>
        [HttpGet("companies")] public async Task<IActionResult> Companies(CancellationToken ct) => Ok(await _s.CompaniesAsync(Caller, ct));

        /// <summary>Changes in watched modules, oldest first, after the given event id (the same events go to your webhook).</summary>
        [HttpGet("events")]
        public async Task<IActionResult> Events([FromQuery] long after = 0, [FromQuery] int take = 100, CancellationToken ct = default) => Ok(await _s.FeedAsync(Caller, after, take, ct));

        /// <summary>A company's own key: its own Zoho Books, without naming the company.</summary>
        [HttpGet("zoho/{**path}"), HttpPost("zoho/{**path}"), HttpPut("zoho/{**path}")]
        public Task<IActionResult> OwnZoho(string path, CancellationToken ct) =>
            Zoho(Caller.ClientId ?? throw GatewayException.BadRequest("USE_COMPANY_PATH", "A partner key names the company: /api/v1/bridge/companies/{companyId}/zoho/..."), path, ct);

        [HttpGet("companies/{companyId:guid}/zoho/{**path}"), HttpPost("companies/{companyId:guid}/zoho/{**path}"), HttpPut("companies/{companyId:guid}/zoho/{**path}")]
        public async Task<IActionResult> Zoho(Guid companyId, string path, CancellationToken ct)
        {
            string? body = null;
            if (!HttpMethods.IsGet(Request.Method))
            {
                using var reader = new StreamReader(Request.Body, Encoding.UTF8);
                var buffer = new char[1_000_001];
                var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
                if (read > 1_000_000) throw GatewayException.BadRequest("BODY_TOO_LARGE", "The body may be at most 1 MB.");
                body = read == 0 ? null : new string(buffer, 0, read);
            }
            var res = await _s.ProxyAsync(Caller, companyId, Request.Method, path, Request.QueryString.Value, body, ct);
            return new ContentResult { StatusCode = res.Status, Content = res.Body, ContentType = "application/json; charset=utf-8" };
        }
    }

    /// <summary>The partner's own page for the bridge: keys, the webhook for changes, the companies that shared, and recent events.</summary>
    [ApiController, Authorize, HasPermission(Perm.PartnerPortal), Route("api/v1/partner/bridge")]
    public class PartnerBridgeController : ControllerBase
    {
        private readonly IBridgeService _s;
        public PartnerBridgeController(IBridgeService s) => _s = s;
        private Guid Mine => User.PartnerIdOrNull() ?? throw new GatewayException(403, "NOT_A_PARTNER", "This is only for the staff of a partner.");

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.PortalAsync(Mine, ct));
        [HttpPost("keys")] public async Task<IActionResult> CreateKey(CreateBridgeKeyRequest body, CancellationToken ct) => Ok(await _s.CreateKeyAsync(Mine, body, ct));
        [HttpDelete("keys/{id:guid}")] public async Task<IActionResult> RevokeKey(Guid id, CancellationToken ct) { await _s.RevokeKeyAsync(Mine, id, ct); return NoContent(); }
        [HttpPut("endpoint")] public async Task<IActionResult> Endpoint(SaveBridgeEndpointRequest body, CancellationToken ct) => Ok(await _s.SaveEndpointAsync(Mine, body, ct));
    }

    /// <summary>The company decides which partners reach its Zoho Books, which modules, and whether they may write.</summary>
    [ApiController, Authorize, HasPermission(Perm.IntegrationsManage), Route("api/v1/integrations/zoho/bridge")]
    public class ZohoBridgeGrantsController : ControllerBase
    {
        private readonly IBridgeService _s;
        public ZohoBridgeGrantsController(IBridgeService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GrantsAsync(User.ClientId(), ct));
        [HttpPut] public async Task<IActionResult> Save(SaveBridgeGrantRequest body, CancellationToken ct) => Ok(await _s.SaveGrantAsync(User.ClientId(), body, ct));
        [HttpDelete("{id:guid}")] public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) { await _s.RevokeGrantAsync(User.ClientId(), id, ct); return NoContent(); }

        // ---- the company's own system: its keys, its webhook, its recent changes ----
        [HttpGet("own")] public async Task<IActionResult> Own(CancellationToken ct) => Ok(await _s.OwnAsync(User.ClientId(), ct));
        [HttpPost("own/keys")] public async Task<IActionResult> CreateOwnKey(CreateOwnKeyRequest body, CancellationToken ct) => Ok(await _s.CreateOwnKeyAsync(User.ClientId(), body, ct));
        [HttpDelete("own/keys/{id:guid}")] public async Task<IActionResult> RevokeOwnKey(Guid id, CancellationToken ct) { await _s.RevokeOwnKeyAsync(User.ClientId(), id, ct); return NoContent(); }
        [HttpPut("own/endpoint")] public async Task<IActionResult> OwnEndpoint(SaveBridgeEndpointRequest body, CancellationToken ct) => Ok(await _s.SaveOwnEndpointAsync(User.ClientId(), body, ct));
    }

    /// <summary>The platform's view of the bridge: partners, keys, traffic and deliveries.</summary>
    [ApiController, Authorize, HasPermission(Perm.IntegrationsMonitor), Route("api/v1/admin/integrations/bridge")]
    public class BridgeMonitorController : ControllerBase
    {
        private readonly IBridgeService _s;
        public BridgeMonitorController(IBridgeService s) => _s = s;
        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.MonitorAsync(ct));
    }
}
