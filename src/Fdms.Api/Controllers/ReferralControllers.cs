using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>A company's refer-and-earn page: its code, the companies it brought, what it earned and the perks it unlocked.</summary>
    [ApiController, Authorize, HasPermission(Perm.ReferralsView), Route("api/v1/referrals")]
    public class ReferralsController : ControllerBase
    {
        private readonly IReferralService _s;
        public ReferralsController(IReferralService s) => _s = s;
        [HttpGet] public async Task<IActionResult> Overview(CancellationToken ct) => Ok(await _s.OverviewAsync(User.ClientId(), ct));

        /// <summary>Spends coins on a reward from the shop (or pays an open invoice with them).</summary>
        [HttpPost("redeem")] public async Task<IActionResult> Redeem(RedeemCoinsRequest body, CancellationToken ct) => Ok(await _s.RedeemAsync(User.ClientId(), body, ct));
    }

    /// <summary>The operator sets the program's rules and pays out the rewards companies have earned.</summary>
    [ApiController, Authorize, HasPermission(Perm.PartnersManage), Route("api/v1/referral-program")]
    public class ReferralProgramController : ControllerBase
    {
        private readonly IReferralService _s;
        public ReferralProgramController(IReferralService s) => _s = s;
        [HttpGet] public async Task<IActionResult> Get([FromQuery] string? status, CancellationToken ct) => Ok(await _s.AdminAsync(status, ct));
        [HttpPut] public async Task<IActionResult> Save(SaveReferralProgramRequest body, CancellationToken ct) => Ok(await _s.SaveProgramAsync(body, ct));
        [HttpPost("rewards/{id:guid}/pay")] public async Task<IActionResult> Pay(Guid id, PayRewardRequest body, CancellationToken ct) => Ok(await _s.PayAsync(id, body.Reference, ct));

        /// <summary>Gives a company coins (or takes some back, with a negative amount).</summary>
        [HttpPost("coins")] public async Task<IActionResult> Adjust(AdjustCoinsRequest body, CancellationToken ct) => Ok(await _s.AdjustCoinsAsync(body, ct));
    }

    /// <summary>Terms and conditions, privacy policy and the developer's details: public to read (they are linked from the registration page), edited by the operator.</summary>
    [ApiController, Route("api/v1")]
    public class LegalController : ControllerBase
    {
        private readonly ILegalService _s;
        private readonly IAppLinksService _links;
        public LegalController(ILegalService s, IAppLinksService links) { _s = s; _links = links; }

        [HttpGet("public/legal"), AllowAnonymous]
        public async Task<IActionResult> Get(CancellationToken ct) { Response.Headers.CacheControl = "public, max-age=300"; return Ok(await _s.GetAsync(ct)); }

        [HttpPut("settings/legal"), Authorize, HasPermission(Perm.SettingsManage)]
        public async Task<IActionResult> Save(SaveLegalRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(body, ct));

        /// <summary>Where to download the mobile app (APKPure, Google Play, App Store). Public: the website shows a button for each link that is set.</summary>
        [HttpGet("public/app-links"), AllowAnonymous]
        public async Task<IActionResult> AppLinks(CancellationToken ct) { Response.Headers.CacheControl = "no-cache"; return Ok(await _links.GetAsync(ct)); }

        [HttpPut("settings/app-links"), Authorize, HasPermission(Perm.SettingsManage)]
        public async Task<IActionResult> SaveAppLinks(AppLinksDto body, CancellationToken ct) => Ok(await _links.SaveAsync(body, ct));
    }
}
