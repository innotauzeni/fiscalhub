using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fdms.Api.Controllers
{
    /// <summary>The company's QuickBooks Online, Xero, Sage Accounting and Odoo connections (product: quickbooks, xero, sage or odoo).</summary>
    [ApiController, Authorize, HasPermission(Perm.IntegrationsManage), Route("api/v1/integrations/connectors")]
    public class ConnectorController : ControllerBase
    {
        private readonly IConnectorService _s;
        public ConnectorController(IConnectorService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(User.ClientId(), ct));
        [HttpGet("{product}")] public async Task<IActionResult> Get(string product, CancellationToken ct) => Ok(await _s.GetAsync(User.ClientId(), product, ct));
        /// <summary>Returns the product's sign-in address to send the browser to (QuickBooks, Xero, Sage).</summary>
        [HttpPost("{product}/connect")] public async Task<IActionResult> Connect(string product, CancellationToken ct) => Ok(await _s.StartAsync(User.ClientId(), product, ct));
        /// <summary>Odoo: the company's Odoo address, database, user and API key (checked at once).</summary>
        [HttpPost("odoo/key")] public async Task<IActionResult> ConnectOdoo(ConnectOdooRequest body, CancellationToken ct) => Ok(await _s.ConnectOdooAsync(User.ClientId(), body, ct));
        [HttpPost("{product}/organization")] public async Task<IActionResult> Organization(string product, ChooseOrganizationRequest body, CancellationToken ct) => Ok(await _s.ChooseOrganizationAsync(User.ClientId(), product, body.OrganizationId, ct));
        [HttpPut("{product}/settings")] public async Task<IActionResult> Settings(string product, ZohoSettingsRequest body, CancellationToken ct) => Ok(await _s.SaveSettingsAsync(User.ClientId(), product, body, ct));
        [HttpDelete("{product}")] public async Task<IActionResult> Disconnect(string product, CancellationToken ct) { await _s.DisconnectAsync(User.ClientId(), product, ct); return NoContent(); }
        [HttpPost("{product}/sync")] public async Task<IActionResult> Sync(string product, CancellationToken ct) => Ok(await _s.SyncNowAsync(User.ClientId(), product, ct));

        /// <summary>status: Pending, Fiscalised, Failed, Skipped, Changed or NotPostedBack.</summary>
        [HttpGet("{product}/documents")]
        public async Task<IActionResult> Documents(string product, [FromQuery] string? status, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
            Ok(await _s.DocumentsAsync(User.ClientId(), product, status, search, page, pageSize, ct));
        [HttpPost("{product}/documents/{id:guid}/fiscalise")] public async Task<IActionResult> Fiscalise(string product, Guid id, CancellationToken ct) => Ok(await _s.FiscaliseAsync(User.ClientId(), product, id, ct));
        [HttpPost("{product}/documents/{id:guid}/skip")] public async Task<IActionResult> Skip(string product, Guid id, CancellationToken ct) => Ok(await _s.SkipAsync(User.ClientId(), product, id, ct));
        [HttpPost("{product}/documents/{id:guid}/post-back")] public async Task<IActionResult> PostBack(string product, Guid id, CancellationToken ct) => Ok(await _s.PostBackAsync(User.ClientId(), product, id, ct));
    }

    /// <summary>The end of a QuickBooks, Xero or Sage sign-in, called without a session: the signed, time-limited state proves which company started it.</summary>
    [ApiController, AllowAnonymous, Route("api/v1/integrations/connectors")]
    public class ConnectorCallbackController : ControllerBase
    {
        private readonly IConnectorService _s;
        public ConnectorCallbackController(IConnectorService s) => _s = s;

        [HttpGet("{product}/callback"), EnableRateLimiting("login")]
        public async Task<IActionResult> Callback(string product, [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
        {
            var query = Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString());
            var r = await _s.CallbackAsync(product, code, state, error, query, ct);
            return Redirect(r.RedirectTo);
        }
    }

    /// <summary>The platform owner: each product's app at the provider (the secret is write-only) and its prices.</summary>
    [ApiController, Authorize, HasPermission(Perm.SettingsManage), Route("api/v1/admin/integrations/connectors")]
    public class ConnectorAdminController : ControllerBase
    {
        private readonly IConnectorService _s;
        public ConnectorAdminController(IConnectorService s) => _s = s;

        [HttpGet("apps")] public async Task<IActionResult> Apps(CancellationToken ct) => Ok(await _s.AppsAsync(ct));
        [HttpPut("{product}/app")] public async Task<IActionResult> SaveApp(string product, SaveConnectorAppRequest body, CancellationToken ct) => Ok(await _s.SaveAppAsync(product, body, ct));
        [HttpGet("{product}/pricing")] public async Task<IActionResult> Pricing(string product, CancellationToken ct) => Ok(await _s.PricingAsync(product, ct));
        [HttpPut("{product}/pricing")] public async Task<IActionResult> SavePricing(string product, IntegrationPricingDto body, CancellationToken ct) => Ok(await _s.SavePricingAsync(product, body, ct));
    }
}
