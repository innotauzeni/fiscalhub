using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>The platform owner's money: recurring revenue, collections, outstanding, plans, payment methods and who pays.</summary>
    [ApiController, Authorize, HasPermission(Perm.RevenueView), Route("api/v1/admin/revenue")]
    public class RevenueController : ControllerBase
    {
        private readonly IRevenueService _s;
        public RevenueController(IRevenueService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(ct));
    }

    /// <summary>The mail server the gateway sends with, and who gets the platform's own alerts. The password is write-only.</summary>
    [ApiController, Authorize, HasPermission(Perm.SettingsManage), Route("api/v1/settings/email")]
    public class EmailSettingsController : ControllerBase
    {
        private readonly IEmailSettingsService _s;
        public EmailSettingsController(IEmailSettingsService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(ct));
        [HttpPut] public async Task<IActionResult> Save(SaveEmailSettingsRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(body, ct));
        [HttpPost("test")] public async Task<IActionResult> Test(TestEmailRequest body, CancellationToken ct) => Ok(await _s.TestAsync(body.To, ct));
    }
}
