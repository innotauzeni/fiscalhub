using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fdms.Api.Controllers
{
    /// <summary>The company's Zoho Books connection: sign in to Zoho, choose the organisation, settings, the documents pulled from Zoho, and fiscalising them.</summary>
    [ApiController, Authorize, HasPermission(Perm.IntegrationsManage), Route("api/v1/integrations/zoho")]
    public class ZohoIntegrationController : ControllerBase
    {
        private readonly IIntegrationService _s;
        public ZohoIntegrationController(IIntegrationService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.ZohoAsync(User.ClientId(), ct));
        /// <summary>Returns the Zoho sign-in address to send the browser to.</summary>
        [HttpPost("connect")] public async Task<IActionResult> Connect(StartZohoRequest body, CancellationToken ct) => Ok(await _s.StartZohoAsync(User.ClientId(), body, ct));
        [HttpPost("organization")] public async Task<IActionResult> Organization(ChooseOrganizationRequest body, CancellationToken ct) => Ok(await _s.ChooseOrganizationAsync(User.ClientId(), body.OrganizationId, ct));
        [HttpPut("settings")] public async Task<IActionResult> Settings(ZohoSettingsRequest body, CancellationToken ct) => Ok(await _s.SaveZohoSettingsAsync(User.ClientId(), body, ct));
        /// <summary>Connect with the company's own Zoho app: with a Self Client code it connects at once; without one the answer carries the Zoho sign-in address.</summary>
        [HttpPost("own-app")] public async Task<IActionResult> OwnApp(ConnectOwnZohoRequest body, CancellationToken ct) => Ok(await _s.ConnectOwnAsync(User.ClientId(), body, ct));
        [HttpDelete("own-app")] public async Task<IActionResult> ForgetOwnApp(CancellationToken ct) => Ok(await _s.ForgetOwnAppAsync(User.ClientId(), ct));
        [HttpDelete] public async Task<IActionResult> Disconnect(CancellationToken ct) { await _s.DisconnectZohoAsync(User.ClientId(), ct); return NoContent(); }
        [HttpPost("sync")] public async Task<IActionResult> Sync(CancellationToken ct) => Ok(await _s.SyncNowAsync(User.ClientId(), ct));

        /// <summary>status: Pending, Fiscalised, Failed, Skipped, Changed (edited in Zoho after fiscalising) or NotPostedBack.</summary>
        [HttpGet("documents")]
        public async Task<IActionResult> Documents([FromQuery] string? status, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
            Ok(await _s.DocumentsAsync(User.ClientId(), status, search, page, pageSize, ct));
        [HttpPost("documents/{id:guid}/fiscalise")] public async Task<IActionResult> Fiscalise(Guid id, CancellationToken ct) => Ok(await _s.FiscaliseAsync(User.ClientId(), id, ct));
        [HttpPost("documents/{id:guid}/skip")] public async Task<IActionResult> Skip(Guid id, CancellationToken ct) => Ok(await _s.SkipAsync(User.ClientId(), id, ct));
        [HttpPost("documents/{id:guid}/post-back")] public async Task<IActionResult> PostBack(Guid id, CancellationToken ct) => Ok(await _s.PostBackAsync(User.ClientId(), id, ct));
    }

    /// <summary>
    /// What Zoho calls without a session: the end of the sign-in (the signed, time-limited state proves which company started it) and the workflow webhook
    /// (its secret key is in the address; the body is ignored and the documents are fetched from Zoho itself).
    /// </summary>
    [ApiController, AllowAnonymous, Route("api/v1/integrations/zoho")]
    public class ZohoCallbackController : ControllerBase
    {
        private readonly IIntegrationService _s;
        public ZohoCallbackController(IIntegrationService s) => _s = s;

        [HttpGet("callback"), EnableRateLimiting("login")]
        public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery(Name = "accounts-server")] string? accountsServer, [FromQuery] string? error, CancellationToken ct)
        {
            var r = await _s.CompleteZohoAsync(code, state, accountsServer, error, ct);
            return Redirect(r.RedirectTo);
        }

        [HttpGet("hook/{connectionId:guid}/{key}"), HttpPost("hook/{connectionId:guid}/{key}"), EnableRateLimiting("login")]
        public async Task<IActionResult> Hook(Guid connectionId, string key, CancellationToken ct) =>
            await _s.WebhookAsync(connectionId, key, ct) ? Ok(new { queued = true }) : NotFound();
    }

    /// <summary>The platform owner's view: every company's connections (token health, last sync, failures, usage) and what they earn.</summary>
    [ApiController, Authorize, Route("api/v1/admin/integrations")]
    public class IntegrationMonitorController : ControllerBase
    {
        private readonly IIntegrationService _s;
        public IntegrationMonitorController(IIntegrationService s) => _s = s;

        [HttpGet, HasPermission(Perm.IntegrationsMonitor)] public async Task<IActionResult> Monitor(CancellationToken ct) => Ok(await _s.MonitorAsync(ct));
        /// <summary>The Zoho app registered at Zoho's API console (the secret is write-only).</summary>
        [HttpGet("zoho-app"), HasPermission(Perm.SettingsManage)] public async Task<IActionResult> App(CancellationToken ct) => Ok(await _s.ZohoAppAsync(ct));
        [HttpPut("zoho-app"), HasPermission(Perm.SettingsManage)] public async Task<IActionResult> SaveApp(SaveZohoAppRequest body, CancellationToken ct) => Ok(await _s.SaveZohoAppAsync(body, ct));
        [HttpGet("pricing"), HasPermission(Perm.SettingsManage)] public async Task<IActionResult> Pricing(CancellationToken ct) => Ok(await _s.PricingAsync(ct));
        [HttpPut("pricing"), HasPermission(Perm.SettingsManage)] public async Task<IActionResult> SavePricing(IntegrationPricingDto body, CancellationToken ct) => Ok(await _s.SavePricingAsync(body, ct));
    }

    /// <summary>A company applies for an accounting integration (Zoho Books, QuickBooks, Xero, Sage or Odoo; ?provider=, Zoho when left out) and sees where its application stands.</summary>
    [ApiController, Authorize, HasPermission(Perm.IntegrationsManage), Route("api/v1/integrations/access")]
    public class IntegrationAccessController : ControllerBase
    {
        private readonly IIntegrationAccessService _s;
        public IntegrationAccessController(IIntegrationAccessService s) => _s = s;
        [HttpGet] public async Task<IActionResult> Mine([FromQuery] string? provider, CancellationToken ct) => Ok(await _s.MineAsync(User.ClientId(), ct, IntegrationProducts.Get(provider).Key));
        [HttpPost] public async Task<IActionResult> Apply(ApplyIntegrationRequest body, CancellationToken ct) => Ok(await _s.ApplyAsync(User.ClientId(), body, ct));
    }

    /// <summary>The platform owner decides: approve (with a set-up fee invoice), reject, suspend, reactivate, and revoke any key.</summary>
    [ApiController, Authorize, HasPermission(Perm.IntegrationsMonitor), Route("api/v1/admin/integrations/access")]
    public class IntegrationAccessAdminController : ControllerBase
    {
        private readonly IIntegrationAccessService _s;
        public IntegrationAccessAdminController(IIntegrationAccessService s) => _s = s;
        [HttpGet] public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct) => Ok(await _s.ListAsync(status, ct));
        [HttpPost("{id:guid}/approve")] public async Task<IActionResult> Approve(Guid id, ApproveIntegrationRequest body, CancellationToken ct) => Ok(await _s.ApproveAsync(id, body, ct));
        [HttpPost("{id:guid}/reject")] public async Task<IActionResult> Reject(Guid id, IntegrationDecisionRequest body, CancellationToken ct) => Ok(await _s.RejectAsync(id, body, ct));
        [HttpPost("companies/{clientId:guid}/suspend")] public async Task<IActionResult> Suspend(Guid clientId, IntegrationDecisionRequest body, [FromQuery] string? provider, CancellationToken ct) => Ok(await _s.SuspendAsync(clientId, body, ct, IntegrationProducts.Get(provider).Key));
        [HttpPost("companies/{clientId:guid}/reactivate")] public async Task<IActionResult> Reactivate(Guid clientId, [FromQuery] string? provider, CancellationToken ct) => Ok(await _s.ReactivateAsync(clientId, ct, IntegrationProducts.Get(provider).Key));
        [HttpGet("keys")] public async Task<IActionResult> Keys(CancellationToken ct) => Ok(await _s.KeysAsync(ct));
        [HttpPost("keys/{keyId:guid}/revoke")] public async Task<IActionResult> RevokeKey(Guid keyId, IntegrationDecisionRequest body, CancellationToken ct) { await _s.RevokeKeyAsync(keyId, body.Reason, ct); return NoContent(); }
    }
}
