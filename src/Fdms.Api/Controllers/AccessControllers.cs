using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fdms.Api.Controllers
{
    /// <summary>Sign in with email/username + password. Returns a JWT, a rotating refresh token, the user's permissions and their menu tree.</summary>
    [ApiController]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _auth;
        public AuthController(IAuthService auth) => _auth = auth;

        [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
        public async Task<IActionResult> Login(LoginRequest body, CancellationToken ct) => Ok(await _auth.LoginAsync(body, ct));

        [HttpPost("refresh"), AllowAnonymous, EnableRateLimiting("login")]
        public async Task<IActionResult> Refresh(RefreshRequest body, CancellationToken ct) => Ok(await _auth.RefreshAsync(body.RefreshToken, ct));

        /// <summary>Second step for someone with two-step sign-in: the MfaToken from /login plus the 6-digit code (or a recovery code).</summary>
        [HttpPost("login/mfa"), AllowAnonymous, EnableRateLimiting("login")]
        public async Task<IActionResult> LoginMfa(MfaLoginRequest body, CancellationToken ct) => Ok(await _auth.LoginMfaAsync(body, ct));

        /// <summary>Passkey sign-in, step 1: a challenge for the browser to sign. No username is needed.</summary>
        [HttpPost("passkey/options"), AllowAnonymous, EnableRateLimiting("login")]
        public async Task<IActionResult> PasskeyOptions([FromServices] ISecurityService security, CancellationToken ct) => Ok(await security.BeginPasskeyLoginAsync(PasskeyOrigins.From(Request), ct));

        /// <summary>Passkey sign-in, step 2: the signed challenge. A passkey counts as both factors, so no code is asked for.</summary>
        [HttpPost("passkey/login"), AllowAnonymous, EnableRateLimiting("login")]
        public async Task<IActionResult> PasskeyLogin(PasskeyLoginRequest body, CancellationToken ct) => Ok(await _auth.LoginPasskeyAsync(body, PasskeyOrigins.From(Request), ct));

        /// <summary>Forgotten password, step 1: emails a 6-digit code (same answer whether or not the account exists).</summary>
        [HttpPost("forgot-password"), AllowAnonymous, EnableRateLimiting("login")]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest body, [FromServices] IPasswordResetService reset, CancellationToken ct) => Ok(await reset.RequestAsync(body, ct));

        /// <summary>Forgotten password, step 2: the code plus the new password. Signs the person out everywhere.</summary>
        [HttpPost("reset-password"), AllowAnonymous, EnableRateLimiting("login")]
        public async Task<IActionResult> ResetPassword(ResetPasswordWithCodeRequest body, [FromServices] IPasswordResetService reset, CancellationToken ct) => Ok(await reset.ResetAsync(body, ct));

        [HttpPost("logout"), AllowAnonymous]
        public async Task<IActionResult> Logout(RefreshRequest body, CancellationToken ct) { await _auth.LogoutAsync(body.RefreshToken, ct); return NoContent(); }

        /// <summary>People only (an API token has no password).</summary>
        [HttpPost("change-password"), Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<IActionResult> ChangePassword(ChangePasswordRequest body, CancellationToken ct) =>
            Ok(await _auth.ChangePasswordAsync(User.UserId()!, body, ct));

        [HttpGet("me"), Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<IActionResult> Me(CancellationToken ct) => Ok(await _auth.MeAsync(User.UserId()!, ct));
    }

    [ApiController, Authorize, Route("api/v1/dashboard")]
    public class DashboardController : ControllerBase
    {
        private readonly IDashboardService _s;
        public DashboardController(IDashboardService s) => _s = s;

        [HttpGet, HasPermission(Perm.DevicesView)]
        public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(User.ClientId(), ct));

        /// <summary>Across all companies (platform operator).</summary>
        [HttpGet("platform"), HasPermission(Perm.ClientsManage)]
        public async Task<IActionResult> Platform(CancellationToken ct) => Ok(await _s.GetPlatformAsync(ct));
    }

    /// <summary>Companies (platform operator).</summary>
    [ApiController, Authorize, HasPermission(Perm.ClientsManage), Route("api/v1/clients")]
    public class ClientsController : ControllerBase
    {
        private readonly IClientAdminService _s;
        public ClientsController(IClientAdminService s) => _s = s;
        public sealed record ActiveBody(bool Active);

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(ct));

        /// <summary>Creates the company and its first administrator, who must change the password at first sign-in.</summary>
        [HttpPost] public async Task<IActionResult> Create(CreateClientRequest body, CancellationToken ct) => Ok(await _s.CreateAsync(body, ct));

        /// <summary>Suspending a company signs its users out and stops its tokens immediately.</summary>
        [HttpPut("{id:guid}/active")]
        public async Task<IActionResult> SetActive(Guid id, ActiveBody body, CancellationToken ct) => Ok(await _s.SetActiveAsync(id, body.Active, ct));
    }

    /// <summary>Users of a company (a company administrator) or of every company (platform operator).</summary>
    [ApiController, Authorize, HasPermission(Perm.UsersManage), Route("api/v1/users")]
    public class UsersController : ControllerBase
    {
        private readonly IUserAdminService _s;
        public UsersController(IUserAdminService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(User.ClientIdOrNull(), ct));
        [HttpGet("roles")] public async Task<IActionResult> Roles(CancellationToken ct) => Ok(await _s.AssignableRolesAsync(User.ClientIdOrNull(), ct));
        [HttpPost] public async Task<IActionResult> Create(CreateUserRequest body, CancellationToken ct) => Ok(await _s.CreateAsync(User.ClientIdOrNull(), body, ct));
        [HttpPut("{id}")] public async Task<IActionResult> Update(string id, UpdateUserRequest body, CancellationToken ct) => Ok(await _s.UpdateAsync(User.ClientIdOrNull(), id, body, ct));

        [HttpGet("{id}/access")]
        public async Task<IActionResult> GetAccess(string id, [FromServices] IBranchService branches, CancellationToken ct) => Ok(await branches.GetUserAccessAsync(User.ClientIdOrNull(), id, ct));

        /// <summary>Limit a user to branches and/or devices (or give them the whole company back).</summary>
        [HttpPut("{id}/access")]
        public async Task<IActionResult> SetAccess(string id, SetUserAccessRequest body, [FromServices] IBranchService branches, CancellationToken ct) => Ok(await branches.SetUserAccessAsync(User.ClientIdOrNull(), id, body, ct));

        /// <summary>A colleague lost their phone: switch their two-step sign-in off, remove their passkeys and sign them out everywhere.</summary>
        [HttpPost("{id}/reset-mfa")]
        public async Task<IActionResult> ResetMfa(string id, [FromServices] ISecurityService security, CancellationToken ct) { await security.ResetForUserAsync(User.ClientIdOrNull(), id, ct); return NoContent(); }

        [HttpPost("{id}/reset-password")]
        public async Task<IActionResult> Reset(string id, ResetPasswordRequest body, CancellationToken ct) { await _s.ResetPasswordAsync(User.ClientIdOrNull(), id, body.NewPassword, ct); return NoContent(); }
    }

    /// <summary>Branches of a company and which devices (tills) belong to each. Users are limited to branches under Users -> Access.</summary>
    [ApiController, Authorize, HasPermission(Perm.BranchesManage), Route("api/v1/branches")]
    public class BranchesController : ControllerBase
    {
        private readonly IBranchService _s;
        public BranchesController(IBranchService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(User.ClientId(), ct));
        [HttpPut] public async Task<IActionResult> Save(SaveBranchRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(User.ClientId(), body, ct));
        [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteAsync(User.ClientId(), id, ct); return NoContent(); }
        [HttpPut("{id:guid}/devices")] public async Task<IActionResult> SetDevices(Guid id, GuidListRequest body, CancellationToken ct) => Ok(await _s.SetDevicesAsync(User.ClientId(), id, body.Ids, ct));
    }

    /// <summary>How the company's printed invoices look (logo, colour, header, columns, bank details, terms, footer), per company and per branch.</summary>
    [ApiController, Authorize, HasPermission(Perm.TemplatesManage), Route("api/v1/templates")]
    public class TemplatesController : ControllerBase
    {
        private readonly ITemplateService _s;
        public TemplatesController(ITemplateService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(User.ClientId(), ct));
        [HttpPut] public async Task<IActionResult> Save(SaveTemplateRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(User.ClientId(), body, ct));
        [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteAsync(User.ClientId(), id, ct); return NoContent(); }

        /// <summary>Upload an existing invoice (PDF, PNG or JPEG): the AI describes its design and the answer is settings to apply in the editor. Nothing is saved or kept.</summary>
        [HttpPost("analyze"), RequestSizeLimit(5_000_000)]
        public async Task<IActionResult> Analyze(IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Choose a PDF, PNG or JPEG of your invoice.");
            if (file.Length > 4_000_000) throw GatewayException.BadRequest("FILE_SIZE", "The file must be at most 4 MB.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return Ok(await _s.AnalyzeAsync(User.ClientId(), ms.ToArray(), file.ContentType, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));
        }

        [HttpPost("{id:guid}/logo")]
        public async Task<IActionResult> SetLogo(Guid id, IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Choose an image file.");
            if (file.Length > Fdms.Infrastructure.Services.InvoiceTemplateService.MaxLogoBytes) throw GatewayException.BadRequest("LOGO_SIZE", "The logo must be an image of at most 300 KB.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return Ok(await _s.SetLogoAsync(User.ClientId(), id, ms.ToArray(), file.ContentType, ct));
        }

        [HttpDelete("{id:guid}/logo")] public async Task<IActionResult> RemoveLogo(Guid id, CancellationToken ct) => Ok(await _s.RemoveLogoAsync(User.ClientId(), id, ct));

        [HttpGet("{id:guid}/logo")]
        public async Task<IActionResult> GetLogo(Guid id, CancellationToken ct)
        {
            var logo = await _s.GetLogoAsync(User.ClientId(), id, ct) ?? throw GatewayException.NotFound("Logo");
            return File(logo.Bytes, logo.ContentType);
        }

        /// <summary>A sample invoice rendered with the settings being edited (page 1 as a PNG).</summary>
        [HttpPost("preview")]
        public async Task<IActionResult> Preview(PreviewTemplateRequest body, CancellationToken ct) => File(await _s.PreviewAsync(User.ClientId(), body, ct), "image/png");

        // ---- the invoice builder ----

        /// <summary>The values an invoice can show (for fields and {placeholders}), and the fonts on offer.</summary>
        [HttpGet("fields")] public IActionResult Fields() => Ok(new { fields = InvoiceFields.All, fonts = Fdms.Infrastructure.Templates.CanvasPdfRenderer.Fonts });
        /// <summary>The sample invoice the builder draws, as data.</summary>
        [HttpGet("sample")] public async Task<IActionResult> Sample(CancellationToken ct) => Ok(await _s.SampleDocumentAsync(User.ClientId(), ct));
        /// <summary>The sample invoice with the settings being edited, as pdf, docx or json.</summary>
        [HttpPost("render")]
        public async Task<IActionResult> Render(RenderTemplateRequest body, CancellationToken ct) { var f = await _s.RenderSampleAsync(User.ClientId(), body, ct); return File(f.Bytes, f.ContentType, f.FileName); }

        [HttpGet("assets")] public async Task<IActionResult> Assets(CancellationToken ct) => Ok(await _s.ListAssetsAsync(User.ClientId(), ct));
        [HttpPost("assets"), RequestSizeLimit(2_200_000)]
        public async Task<IActionResult> UploadAsset(IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Choose a PNG or JPEG picture.");
            if (file.Length > Fdms.Infrastructure.Services.InvoiceTemplateService.MaxAssetBytes) throw GatewayException.BadRequest("ASSET_SIZE", "The picture must be at most 2 MB.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return Ok(await _s.UploadAssetAsync(User.ClientId(), ms.ToArray(), Path.GetFileName(file.FileName), ct));
        }
        [HttpGet("assets/{id:guid}")]
        public async Task<IActionResult> Asset(Guid id, CancellationToken ct) { var a = await _s.GetAssetAsync(User.ClientId(), id, ct) ?? throw GatewayException.NotFound("Picture"); Response.Headers.CacheControl = "private, max-age=86400"; return File(a.Bytes, a.ContentType); }
        [HttpDelete("assets/{id:guid}")] public async Task<IActionResult> DeleteAsset(Guid id, CancellationToken ct) { await _s.DeleteAssetAsync(User.ClientId(), id, ct); return NoContent(); }

        /// <summary>The template as a JSON file (layout, logo and pictures), to keep or to load into another company.</summary>
        [HttpGet("{id:guid}/export")]
        public async Task<IActionResult> Export(Guid id, CancellationToken ct)
        {
            var t = await _s.ExportAsync(User.ClientId(), id, ct);
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(t, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true });
            return File(bytes, "application/json", "invoice-template.json");
        }
        [HttpPost("{id:guid}/import"), RequestSizeLimit(30_000_000)] public async Task<IActionResult> Import(Guid id, TemplateExportDto body, CancellationToken ct) => Ok(await _s.ImportAsync(User.ClientId(), id, body, ct));
    }

    /// <summary>Platform settings (platform operator): the public address of the API shown in the developer documentation.</summary>
    [ApiController, Authorize, HasPermission(Perm.SettingsManage), Route("api/v1/settings")]
    public class SettingsController : ControllerBase
    {
        private readonly ISettingsService _s;
        public SettingsController(ISettingsService s) => _s = s;
        private string RequestBase => $"{Request.Scheme}://{Request.Host}";

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(RequestBase, ct));
        [HttpPut] public async Task<IActionResult> Save(SavePlatformSettingsRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(RequestBase, body, ct));
    }

    /// <summary>System error log: what went wrong inside the gateway, with request, user and stack trace, and a resolve workflow (platform operator).</summary>
    [ApiController, Authorize, Route("api/v1/system-logs")]
    public class SystemLogsController : ControllerBase
    {
        private readonly IErrorLogService _s;
        public SystemLogsController(IErrorLogService s) => _s = s;

        [HttpGet, HasPermission(Perm.LogsView)] public async Task<IActionResult> Search([FromQuery] ErrorLogSearch q, CancellationToken ct) => Ok(await _s.SearchAsync(q, ct));
        [HttpGet("{id:long}"), HasPermission(Perm.LogsView)] public async Task<IActionResult> Get(long id, CancellationToken ct) => Ok(await _s.GetAsync(id, ct));
        [HttpPut("{id:long}/resolve"), HasPermission(Perm.LogsManage)] public async Task<IActionResult> Resolve(long id, ResolveErrorRequest body, CancellationToken ct) => Ok(await _s.ResolveAsync(id, body.Resolution, ct));
        [HttpPost("resolve-all"), HasPermission(Perm.LogsManage)] public async Task<IActionResult> ResolveAll(CancellationToken ct) => Ok(new { resolved = await _s.ResolveAllAsync(ct) });
        [HttpDelete("{id:long}"), HasPermission(Perm.LogsManage)] public async Task<IActionResult> Delete(long id, CancellationToken ct) { await _s.DeleteAsync(id, ct); return NoContent(); }
        /// <summary>Deletes every entry already marked resolved.</summary>
        [HttpDelete("resolved"), HasPermission(Perm.LogsManage)] public async Task<IActionResult> Purge(CancellationToken ct) => Ok(new { deleted = await _s.PurgeResolvedAsync(ct) });
    }

    /// <summary>Roles, their permissions and the menu entries they see (same endpoints as dara's RoleAccess).</summary>
    [ApiController, Authorize, HasPermission(Perm.RolesManage), Route("api/v1/roles")]
    public class RolesController : ControllerBase
    {
        private readonly IRoleAdminService _s;
        public RolesController(IRoleAdminService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListRolesAsync(ct));
        [HttpPost] public async Task<IActionResult> Create(CreateRoleRequest body, CancellationToken ct) => Ok(await _s.CreateRoleAsync(body, ct));
        [HttpPut("{id}")] public async Task<IActionResult> Update(string id, UpdateRoleRequest body, CancellationToken ct) => Ok(await _s.UpdateRoleAsync(id, body, ct));
        [HttpDelete("{id}")] public async Task<IActionResult> Delete(string id, CancellationToken ct) { await _s.DeleteRoleAsync(id, ct); return NoContent(); }
        [HttpGet("permissions")] public async Task<IActionResult> Permissions(CancellationToken ct) => Ok(await _s.ListPermissionsAsync(ct));
        [HttpGet("{id}/access")] public async Task<IActionResult> Access(string id, CancellationToken ct) => Ok(await _s.GetAccessAsync(id, ct));
        [HttpPut("{id}/permissions")] public async Task<IActionResult> AssignPermissions(string id, IdListRequest body, CancellationToken ct) { await _s.AssignPermissionsAsync(id, body.Ids, ct); return NoContent(); }
        [HttpPut("{id}/menus")] public async Task<IActionResult> AssignMenus(string id, IdListRequest body, CancellationToken ct) { await _s.AssignMenusAsync(id, body.Ids, ct); return NoContent(); }
    }

    /// <summary>The menu structure itself: sections, modules and sub-entries.</summary>
    [ApiController, Authorize, HasPermission(Perm.ModulesManage), Route("api/v1/modules")]
    public class ModulesController : ControllerBase
    {
        private readonly IModuleAdminService _s;
        public ModulesController(IModuleAdminService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Tree(CancellationToken ct) => Ok(await _s.TreeAsync(ct));
        [HttpGet("permissions")] public async Task<IActionResult> Permissions(CancellationToken ct) => Ok(await _s.ListPermissionsAsync(ct));
        [HttpPut("permissions")] public async Task<IActionResult> SavePermission(SavePermissionRequest body, CancellationToken ct) => Ok(await _s.SavePermissionAsync(body, ct));
        [HttpPost("permissions/batch")] public async Task<IActionResult> BatchPermissions(BatchPermissionRequest body, CancellationToken ct) => Ok(await _s.BatchPermissionsAsync(body, ct));
        [HttpDelete("permissions/{id:int}")] public async Task<IActionResult> DeletePermission(int id, CancellationToken ct) { await _s.DeletePermissionAsync(id, ct); return NoContent(); }
        [HttpPut("sections")] public async Task<IActionResult> SaveSection(SectionDto body, CancellationToken ct) => Ok(await _s.SaveSectionAsync(body, ct));
        [HttpDelete("sections/{id:int}")] public async Task<IActionResult> DeleteSection(int id, CancellationToken ct) { await _s.DeleteSectionAsync(id, ct); return NoContent(); }
        [HttpPut("modules")] public async Task<IActionResult> SaveModule(ModuleDto body, CancellationToken ct) => Ok(await _s.SaveModuleAsync(body, ct));
        [HttpDelete("modules/{id:int}")] public async Task<IActionResult> DeleteModule(int id, CancellationToken ct) { await _s.DeleteModuleAsync(id, ct); return NoContent(); }
        [HttpPut("submodules")] public async Task<IActionResult> SaveSubmodule(SubmoduleDto body, CancellationToken ct) => Ok(await _s.SaveSubmoduleAsync(body, ct));
        [HttpDelete("submodules/{id:int}")] public async Task<IActionResult> DeleteSubmodule(int id, CancellationToken ct) { await _s.DeleteSubmoduleAsync(id, ct); return NoContent(); }
    }

    [ApiController, Authorize, HasPermission(Perm.AuditView), Route("api/v1/audit")]
    public class AuditController : ControllerBase
    {
        private readonly IAuditService _s;
        public AuditController(IAuditService s) => _s = s;

        /// <summary>The audit log is for the platform operator only. Anyone who belongs to a company is refused here even if some role was given the permission.</summary>
        private void OperatorOnly()
        {
            if (User.ClientIdOrNull() != null) throw new GatewayException(403, "OPERATOR_ONLY", "The audit log is only available to the platform operator.");
        }

        [HttpGet] public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] Guid? clientId, [FromQuery] int take = 100, CancellationToken ct = default) { OperatorOnly(); return Ok(await _s.ListAsync(clientId, search, take, ct)); }

        /// <summary>Filterable, paged audit trail: who (user, token or system) did what, to which record, from where.</summary>
        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] AuditSearch q, CancellationToken ct) { OperatorOnly(); return Ok(await _s.SearchAsync(null, q, ct)); }
    }

    /// <summary>
    /// Integration tokens. A token works for ONE device and only the permissions ticked at creation. People create them here;
    /// the secret is shown once.
    /// </summary>
    [ApiController, Authorize, HasPermission(Perm.TokensManage)]
    public class TokensController : ControllerBase
    {
        private readonly IApiTokenService _s;
        public TokensController(IApiTokenService s) => _s = s;

        /// <summary>The permissions a token may carry.</summary>
        [HttpGet("api/v1/tokens/scopes")] public IActionResult Scopes() => Ok(_s.Scopes());

        [HttpGet("api/v1/tokens")]
        public async Task<IActionResult> List([FromQuery] Guid? deviceId, CancellationToken ct) => Ok(await _s.ListAsync(User.ClientId(), deviceId, ct));

        [HttpPost("api/v1/devices/{deviceId:guid}/tokens")]
        public async Task<IActionResult> Create(Guid deviceId, CreateTokenRequest body, CancellationToken ct) =>
            Ok(await _s.CreateAsync(User.ClientId(), deviceId, body, User.UserId(), ct));

        [HttpDelete("api/v1/tokens/{id:guid}")]
        public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) { await _s.RevokeAsync(User.ClientId(), id, ct); return NoContent(); }
    }
}

namespace Fdms.Api.Controllers
{
    using Fdms.Application.Abstractions;
    using Fdms.Application.Security;
    using Microsoft.AspNetCore.Authentication.JwtBearer;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>Where the page the person is on lives. A passkey is bound to that host, so it is taken from the request (or from Passkeys:RpId / Passkeys:Origin when the API sits behind a proxy).</summary>
    public static class PasskeyOrigins
    {
        public static PasskeyOrigin From(HttpRequest r)
        {
            var cfg = r.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var rp = cfg["Passkeys:RpId"]; var origin = cfg["Passkeys:Origin"];
            if (!string.IsNullOrWhiteSpace(rp) && !string.IsNullOrWhiteSpace(origin)) return new PasskeyOrigin(rp.Trim(), origin.Trim().TrimEnd('/'));
            // the dev console runs on its own port and proxies to the API, so a localhost Origin header is accepted; a console on its own site listed in
            // Cors:Origins is accepted too (its passkeys belong to that site); anywhere else it must match the host called
            if (Uri.TryCreate(r.Headers.Origin.ToString(), UriKind.Absolute, out var u) && (u.Host is "localhost" or "127.0.0.1" || u.Host.Equals(r.Host.Host, StringComparison.OrdinalIgnoreCase) || Fdms.Api.Security.CorsOrigins.Allows(cfg, u)))
                return new PasskeyOrigin(u.Host, u.GetLeftPart(UriPartial.Authority));
            return new PasskeyOrigin(r.Host.Host, $"{r.Scheme}://{r.Host}");
        }
    }

    /// <summary>The signed-in person's own sign-in security: authenticator app (two-step sign-in) and passkeys. People only, never an API token.</summary>
    [ApiController, Route("api/v1/security"), Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class SecurityController : ControllerBase
    {
        private readonly ISecurityService _s;
        public SecurityController(ISecurityService s) => _s = s;
        private string Me => User.UserId() ?? throw new GatewayException(403, "USER_ONLY", "This is only available when signed in as a person.");

        [HttpGet] public async Task<IActionResult> Status(CancellationToken ct) => Ok(await _s.StatusAsync(Me, ct));

        [HttpPost("mfa/setup")] public async Task<IActionResult> BeginMfa(CancellationToken ct) => Ok(await _s.BeginMfaAsync(Me, ct));
        [HttpPost("mfa/enable")] public async Task<IActionResult> EnableMfa(MfaCodeRequest body, CancellationToken ct) => Ok(await _s.EnableMfaAsync(Me, body.Code, ct));
        [HttpPost("mfa/disable")] public async Task<IActionResult> DisableMfa(PasswordRequest body, CancellationToken ct) { await _s.DisableMfaAsync(Me, body.Password, ct); return NoContent(); }
        [HttpPost("mfa/recovery-codes")] public async Task<IActionResult> RecoveryCodes(PasswordRequest body, CancellationToken ct) => Ok(await _s.NewRecoveryCodesAsync(Me, body.Password, ct));

        [HttpPost("passkeys/options")] public async Task<IActionResult> PasskeyBegin(CancellationToken ct) => Ok(await _s.BeginPasskeyRegistrationAsync(Me, PasskeyOrigins.From(Request), ct));
        [HttpPost("passkeys/{sessionId}")] public async Task<IActionResult> PasskeyFinish(string sessionId, PasskeyFinishRequest body, CancellationToken ct) => Ok(await _s.FinishPasskeyRegistrationAsync(Me, sessionId, body, PasskeyOrigins.From(Request), ct));
        [HttpPut("passkeys/{id:guid}")] public async Task<IActionResult> PasskeyRename(Guid id, PasskeyNameRequest body, CancellationToken ct) => Ok(await _s.RenamePasskeyAsync(Me, id, body.Name, ct));
        [HttpPost("passkeys/{id:guid}/remove")] public async Task<IActionResult> PasskeyDelete(Guid id, PasswordRequest body, CancellationToken ct) { await _s.DeletePasskeyAsync(Me, id, body.Password, ct); return NoContent(); }
    }
}

namespace Fdms.Api.Controllers
{
    using Fdms.Application.Abstractions;
    using Fdms.Application.Security;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;

    /// <summary>A company administrator decides whether everyone in the company must use two-step sign-in or a passkey, and sees who has not yet.</summary>
    [ApiController, Authorize, HasPermission(Perm.UsersManage), Route("api/v1/security-policy")]
    public class SecurityPolicyController : ControllerBase
    {
        private readonly ISecurityService _s;
        public SecurityPolicyController(ISecurityService s) => _s = s;
        private Guid Company => User.ClientIdOrNull() ?? throw GatewayException.BadRequest("COMPANY_ONLY", "This belongs to a company. Sign in as one of its administrators.");

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetPolicyAsync(Company, ct));
        [HttpPut] public async Task<IActionResult> Set(SetPolicyRequest body, CancellationToken ct) => Ok(await _s.SetPolicyAsync(Company, body.RequireMfa, ct));
    }
}
