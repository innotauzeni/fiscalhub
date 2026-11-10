using System.Text;
using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>
    /// Meta's WhatsApp webhook. GET is Meta's one-time check (the verify token from Settings); POST carries the messages people send, signed with the app secret
    /// (X-Hub-Signature-256): unsigned or wrongly signed calls are refused. Messages are stored and answered in the background, so Meta gets its answer at once.
    /// </summary>
    [ApiController, AllowAnonymous, Route("api/v1/whatsapp/webhook")]
    public class WhatsAppWebhookController : ControllerBase
    {
        private readonly IWhatsAppService _s;
        public WhatsAppWebhookController(IWhatsAppService s) => _s = s;

        [HttpGet]
        public async Task<IActionResult> Verify([FromQuery(Name = "hub.mode")] string? mode, [FromQuery(Name = "hub.verify_token")] string? token, [FromQuery(Name = "hub.challenge")] string? challenge, CancellationToken ct)
        {
            var answer = await _s.VerifyWebhookAsync(mode, token, challenge, ct);
            return answer == null ? StatusCode(403) : Content(answer, "text/plain");
        }

        [HttpPost]
        public async Task<IActionResult> Receive(CancellationToken ct)
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            if (body.Length > 1_000_000) return StatusCode(413);
            return await _s.ReceiveAsync(body, Request.Headers["X-Hub-Signature-256"].FirstOrDefault(), ct) ? Ok() : Unauthorized();
        }

        /// <summary>Meta's check of a company's own number (that company's verify token).</summary>
        [HttpGet("{accountId:guid}")]
        public async Task<IActionResult> VerifyAccount(Guid accountId, [FromQuery(Name = "hub.mode")] string? mode, [FromQuery(Name = "hub.verify_token")] string? token, [FromQuery(Name = "hub.challenge")] string? challenge, CancellationToken ct)
        {
            var answer = await _s.VerifyAccountWebhookAsync(accountId, mode, token, challenge, ct);
            return answer == null ? StatusCode(403) : Content(answer, "text/plain");
        }

        /// <summary>Messages to a company's own number, signed with that company's app secret.</summary>
        [HttpPost("{accountId:guid}")]
        public async Task<IActionResult> ReceiveAccount(Guid accountId, CancellationToken ct)
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            if (body.Length > 1_000_000) return StatusCode(413);
            return await _s.ReceiveAccountAsync(accountId, body, Request.Headers["X-Hub-Signature-256"].FirstOrDefault(), ct) ? Ok() : Unauthorized();
        }
    }

    /// <summary>The platform's WhatsApp number (Meta Cloud API). The access token and app secret are write-only.</summary>
    [ApiController, Authorize, HasPermission(Perm.SettingsManage), Route("api/v1/admin/whatsapp")]
    public class WhatsAppSettingsController : ControllerBase
    {
        private readonly IWhatsAppService _s;
        public WhatsAppSettingsController(IWhatsAppService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.SettingsAsync(ct));
        [HttpPut] public async Task<IActionResult> Save(SaveWhatsAppSettingsRequest body, CancellationToken ct) => Ok(await _s.SaveSettingsAsync(body, ct));
        [HttpPost("test")] public async Task<IActionResult> Test(WhatsAppTestRequest body, CancellationToken ct) => Ok(new { message = await _s.TestAsync(body.To, ct) });
    }

    /// <summary>A person links their own WhatsApp number to their FiscalHub user, chooses alerts and the device the bot uses.</summary>
    [ApiController, Authorize, Route("api/v1/profile/whatsapp")]
    public class WhatsAppProfileController : ControllerBase
    {
        private readonly IWhatsAppService _s;
        public WhatsAppProfileController(IWhatsAppService s) => _s = s;
        private string Me => User.UserId() ?? throw new GatewayException(403, "USER_ONLY", "Sign in as a person.");

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.MyLinkAsync(Me, ct));
        /// <summary>A fresh one-time code to send from WhatsApp (LINK 123456).</summary>
        [HttpPost("link")] public async Task<IActionResult> Link(CancellationToken ct) => Ok(await _s.StartLinkAsync(Me, ct));
        [HttpPut] public async Task<IActionResult> Prefs(SaveWhatsAppPrefsRequest body, CancellationToken ct) => Ok(await _s.SavePrefsAsync(Me, body, ct));
        [HttpDelete] public async Task<IActionResult> Unlink(CancellationToken ct) => Ok(await _s.UnlinkAsync(Me, ct));
    }

    /// <summary>A company's own WhatsApp number and SMS provider account (Integration → Messaging). Secrets are write-only.</summary>
    [ApiController, Authorize, HasPermission(Perm.IntegrationsManage), Route("api/v1/messaging")]
    public class CompanyMessagingController : ControllerBase
    {
        private readonly IWhatsAppService _wa;
        private readonly ISmsService _sms;
        public CompanyMessagingController(IWhatsAppService wa, ISmsService sms) { _wa = wa; _sms = sms; }

        [HttpGet("whatsapp")] public async Task<IActionResult> WhatsApp(CancellationToken ct) => Ok(await _wa.CompanyAsync(User.ClientId(), ct));
        [HttpPut("whatsapp")] public async Task<IActionResult> SaveWhatsApp(SaveCompanyWhatsAppRequest body, CancellationToken ct) => Ok(await _wa.SaveCompanyAsync(User.ClientId(), body, ct));
        [HttpDelete("whatsapp")] public async Task<IActionResult> RemoveWhatsApp(CancellationToken ct) => Ok(await _wa.RemoveCompanyAsync(User.ClientId(), ct));
        [HttpPost("whatsapp/test")] public async Task<IActionResult> TestWhatsApp(WhatsAppTestRequest body, CancellationToken ct) => Ok(new { message = await _wa.TestCompanyAsync(User.ClientId(), body.To, ct) });

        [HttpGet("sms")] public async Task<IActionResult> Sms(CancellationToken ct) => Ok(await _sms.CompanyAsync(User.ClientId(), ct));
        [HttpPut("sms")] public async Task<IActionResult> SaveSms(SaveSmsSettingsRequest body, CancellationToken ct) => Ok(await _sms.SaveCompanyAsync(User.ClientId(), body, ct));
        [HttpPost("sms/test")] public async Task<IActionResult> TestSms(SmsTestRequest body, CancellationToken ct) => Ok(await _sms.TestAsync(User.ClientId(), body.To, ct));
        [HttpGet("sms/log")] public async Task<IActionResult> SmsLog([FromQuery] int take = 50, CancellationToken ct = default) => Ok(await _sms.LogAsync(User.ClientId(), take, ct));
    }

    /// <summary>The platform's own SMS provider account: platform alerts, sign-up codes, and (when allowed) companies without their own account.</summary>
    [ApiController, Authorize, HasPermission(Perm.SettingsManage), Route("api/v1/admin/sms")]
    public class SmsSettingsController : ControllerBase
    {
        private readonly ISmsService _s;
        public SmsSettingsController(ISmsService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.PlatformAsync(ct));
        [HttpPut] public async Task<IActionResult> Save(SaveSmsSettingsRequest body, CancellationToken ct) => Ok(await _s.SavePlatformAsync(body, ct));
        [HttpPost("test")] public async Task<IActionResult> Test(SmsTestRequest body, CancellationToken ct) => Ok(await _s.TestAsync(null, body.To, ct));
        [HttpGet("log")] public async Task<IActionResult> Log([FromQuery] int take = 50, CancellationToken ct = default) => Ok(await _s.LogAsync(null, take, ct));
    }

    /// <summary>A person's SMS alerts (to the phone number on their profile).</summary>
    [ApiController, Authorize, Route("api/v1/profile/sms")]
    public class SmsProfileController : ControllerBase
    {
        private readonly ISmsService _s;
        public SmsProfileController(ISmsService s) => _s = s;
        private string Me => User.UserId() ?? throw new GatewayException(403, "USER_ONLY", "Sign in as a person.");

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.MyPrefsAsync(Me, ct));
        [HttpPut] public async Task<IActionResult> Save(SaveSmsPrefsRequest body, CancellationToken ct) => Ok(await _s.SavePrefsAsync(Me, body, ct));
    }
}
