using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fdms.Api.Controllers
{
    /// <summary>Connects the platform to Gemini (platform operator). The key is stored encrypted and is never returned; only its last four characters are shown.</summary>
    [ApiController, Authorize, HasPermission(Perm.SettingsManage), Route("api/v1/admin/ai")]
    public class AiSettingsController : ControllerBase
    {
        private readonly IAiSettingsService _s;
        public AiSettingsController(IAiSettingsService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(ct));
        [HttpPut] public async Task<IActionResult> Save(SaveAiSettingsRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(body, ct));
        [HttpPost("test")] public async Task<IActionResult> Test(CancellationToken ct) => Ok(await _s.TestAsync(ct));
    }

    /// <summary>Ask a question about your sales in plain words and get a table and chart. The AI only chooses what to report; the gateway works out every number.</summary>
    [ApiController, Authorize, HasPermission(Perm.ReportsAi), Route("api/v1/ai/reports")]
    public class AiReportsController : ControllerBase
    {
        private readonly IAiReportService _s;
        public AiReportsController(IAiReportService s) => _s = s;
        private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

        [HttpGet("meta")] public async Task<IActionResult> Meta(CancellationToken ct) => Ok(await _s.MetaAsync(User.ClientId(), ct));
        [HttpPost("ask")] public async Task<IActionResult> Ask(AskReportRequest body, CancellationToken ct) => Ok(await _s.AskAsync(User.ClientId(), body.Question, Ip, ct));
        [HttpPost("run")] public async Task<IActionResult> Run(RunReportRequest body, CancellationToken ct) => Ok(await _s.RunAsync(User.ClientId(), body.Spec, ct));
        [HttpGet("saved")] public async Task<IActionResult> Saved(CancellationToken ct) => Ok(await _s.ListSavedAsync(User.ClientId(), ct));
        [HttpPost("saved")] public async Task<IActionResult> Save(SaveReportRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(User.ClientId(), body, ct));
        [HttpDelete("saved/{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteSavedAsync(User.ClientId(), id, ct); return NoContent(); }
        [HttpPost("saved/{id:guid}/run")] public async Task<IActionResult> RunSaved(Guid id, CancellationToken ct) => Ok(await _s.RunSavedAsync(User.ClientId(), id, ct));
    }

    /// <summary>The public sign-up assistant: anyone may chat, nobody can do more than the ordinary registration form allows (it is the form that creates the account).</summary>
    [ApiController, AllowAnonymous, Route("api/v1/public/assistant")]
    public class AssistantController : ControllerBase
    {
        private readonly IAssistantService _s;
        public AssistantController(IAssistantService s) => _s = s;

        [HttpGet("status")] public async Task<IActionResult> Status(CancellationToken ct) => Ok(await _s.StatusAsync(ct));
        [HttpPost("chat"), EnableRateLimiting("assistant")]
        public async Task<IActionResult> Chat(AssistantChatRequest body, CancellationToken ct) => Ok(await _s.ChatAsync(body, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));
    }

    /// <summary>"My profile": the signed-in person's own details, picture and sign-ins. Needs a real sign-in (not an integration token); every action is about the caller only.</summary>
    [ApiController, Authorize, Route("api/v1/profile")]
    public class ProfileController : ControllerBase
    {
        private readonly IProfileService _s;
        public ProfileController(IProfileService s) => _s = s;
        private string Me => User.UserId() ?? throw new GatewayException(403, "USER_ONLY", "This is only available when signed in as a person.");

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(Me, ct));
        [HttpPut] public async Task<IActionResult> Update(UpdateProfileRequest body, CancellationToken ct) => Ok(await _s.UpdateAsync(Me, body, ct));
        [HttpPut("username")] public async Task<IActionResult> UserName(ChangeUserNameRequest body, CancellationToken ct) => Ok(await _s.ChangeUserNameAsync(Me, body, ct));
        [HttpPost("email"), EnableRateLimiting("login")] public async Task<IActionResult> Email(ChangeEmailRequest body, CancellationToken ct) => Ok(await _s.RequestEmailChangeAsync(Me, body, ct));
        [HttpPost("email/confirm"), EnableRateLimiting("login")] public async Task<IActionResult> ConfirmEmail(ConfirmEmailChangeRequest body, CancellationToken ct) => Ok(await _s.ConfirmEmailChangeAsync(Me, body, ct));

        [HttpGet("avatar")]
        public async Task<IActionResult> Avatar(CancellationToken ct)
        {
            var a = await _s.GetAvatarAsync(Me, ct) ?? throw GatewayException.NotFound("Picture");
            Response.Headers.CacheControl = "private, no-cache";
            return File(a.Bytes, a.ContentType);
        }

        [HttpPost("avatar"), RequestSizeLimit(300_000)]
        public async Task<IActionResult> SetAvatar(IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Choose a picture.");
            if (file.Length > 100 * 1024) throw GatewayException.BadRequest("AVATAR_SIZE", "The picture must be smaller than 100 KB. Crop it smaller and try again.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            await _s.SetAvatarAsync(Me, ms.ToArray(), ct);
            return NoContent();
        }

        [HttpDelete("avatar")] public async Task<IActionResult> RemoveAvatar(CancellationToken ct) { await _s.RemoveAvatarAsync(Me, ct); return NoContent(); }

        [HttpGet("sessions")] public async Task<IActionResult> Sessions([FromHeader(Name = "X-Refresh-Token")] string? current, CancellationToken ct) => Ok(await _s.SessionsAsync(Me, current, ct));
        [HttpPost("sessions/revoke-others")] public async Task<IActionResult> RevokeOthers(RevokeOthersRequest body, CancellationToken ct) => Ok(new { revoked = await _s.RevokeOthersAsync(Me, body.CurrentRefreshToken, ct) });
        [HttpDelete("sessions/{id:int}")] public async Task<IActionResult> RevokeSession(int id, CancellationToken ct) { await _s.RevokeSessionAsync(Me, id, ct); return NoContent(); }
    }
}
