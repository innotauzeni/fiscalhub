using System.Security.Claims;
using System.Text.Encodings.Web;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Fdms.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Fdms.Api.Security
{
    public static class AuthSchemes
    {
        /// <summary>Picks the API-token scheme or the JWT scheme per request.</summary>
        public const string Smart = "Smart";
        public const string ApiToken = "ApiToken";
        /// <summary>A partner's Zoho bridge key (X-Bridge-Key: fhb_... or Bearer fhb_...).</summary>
        public const string Bridge = "BridgeKey";
    }

    public static class UserExtensions
    {
        public static Guid? ClientIdOrNull(this ClaimsPrincipal user) =>
            Guid.TryParse(user.FindFirstValue(ClaimNames.ClientId), out var id) ? id : null;

        /// <summary>The company of the caller. Platform operators have none and cannot do company work.</summary>
        public static Guid ClientId(this ClaimsPrincipal user) =>
            user.ClientIdOrNull() ?? throw new GatewayException(403, "NO_CLIENT", "This action needs a company account; platform operators manage companies, not their receipts.");

        /// <summary>
        /// The books a caller works in: its own company's, or, for the platform operator (no company, not a partner), the platform's own books.
        /// The accounting permissions still decide who may see or change them.
        /// </summary>
        public static Guid BooksClientId(this ClaimsPrincipal user) =>
            user.ClientIdOrNull() ?? (user.PartnerIdOrNull() == null ? PlatformBooks.ClientId
                : throw new GatewayException(403, "NO_CLIENT", "Partners do not keep books here."));

        /// <summary>The partner (reseller) a staff member belongs to; null for everyone else.</summary>
        public static Guid? PartnerIdOrNull(this ClaimsPrincipal user) =>
            Guid.TryParse(user.FindFirstValue(ClaimNames.PartnerId), out var id) ? id : null;

        public static string? UserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    /// <summary>
    /// Integration token (<c>X-Api-Key: fdms_...</c> or <c>Authorization: Bearer fdms_...</c>). The principal carries the token's company, its ONE
    /// device and its scopes as permission claims, so the same [HasPermission] attributes protect both people and tokens.
    /// </summary>
    public sealed class ApiTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly IApiTokenService _tokens;

        public ApiTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e, IApiTokenService tokens) : base(o, l, e) => _tokens = tokens;

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(token))
            {
                var auth = Request.Headers.Authorization.FirstOrDefault();
                if (auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = auth[7..].Trim();
            }
            if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();

            var p = await _tokens.ValidateAsync(token, Context.RequestAborted);
            if (p == null) return AuthenticateResult.Fail("Invalid, revoked or expired API token.");

            var claims = new List<Claim>
            {
                new(ClaimNames.ClientId, p.ClientId.ToString()),
                new(ClaimNames.KeyId, p.KeyId.ToString()),
                new(ClaimNames.DeviceId, p.DeviceId.ToString()),
                new(ClaimNames.Restricted, "1"),
                new(ClaimTypes.Name, string.IsNullOrEmpty(p.Name) ? "API token" : $"API token \"{p.Name}\""),
            };
            claims.AddRange(p.Scopes.Select(s => new Claim(Perm.ClaimType, s)));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
        }
    }

    /// <summary>A partner's bridge key. The principal carries the partner and the one permission bridge.use; no company, no device.</summary>
    public sealed class BridgeKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string KeyClaim = "fdms_bridge_key";
        public const string ModulesClaim = "fdms_bridge_modules";
        public const string WriteClaim = "fdms_bridge_write";
        private readonly IBridgeService _bridge;
        public BridgeKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e, IBridgeService bridge) : base(o, l, e) => _bridge = bridge;

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var key = Request.Headers["X-Bridge-Key"].FirstOrDefault();
            var auth = Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(key) && auth != null && auth.StartsWith("Bearer fhb_", StringComparison.OrdinalIgnoreCase)) key = auth[7..].Trim();
            if (string.IsNullOrEmpty(key)) return AuthenticateResult.NoResult();
            var caller = await _bridge.ValidateKeyAsync(key, Context.RequestAborted);
            if (caller == null) return AuthenticateResult.Fail("Invalid or revoked bridge key.");
            var claims = new List<Claim>
            {
                new(KeyClaim, caller.KeyId.ToString()),
                new(ModulesClaim, string.Join(",", caller.Modules)),
                new(WriteClaim, caller.CanWrite ? "1" : "0"),
                new(ClaimTypes.Name, caller.KeyName),
                new(ClaimNames.Restricted, "1"),
                new(Perm.ClaimType, Perm.BridgeUse),
            };
            // a company's own key carries the company, so an unpaid subscription locks it like the console
            if (caller.PartnerId is { } partnerId) claims.Add(new Claim(ClaimNames.PartnerId, partnerId.ToString()));
            if (caller.ClientId is { } clientId) claims.Add(new Claim(ClaimNames.ClientId, clientId.ToString()));
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
        }
    }

    /// <summary>Who is calling, for the data layer: a token's single allowed device, and the actor recorded in the audit log.</summary>
    public sealed class HttpCallerScope : ICallerScope
    {
        private readonly IHttpContextAccessor _http;
        public HttpCallerScope(IHttpContextAccessor http) => _http = http;

        private ClaimsPrincipal? User => _http.HttpContext?.User;
        // outside a request, a person acting through another channel (the WhatsApp bot)
        private static AmbientCallerInfo? Ambient => AmbientCaller.Current;
        public string? UserId => Ambient?.UserId ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);
        public string? UserName => Ambient != null ? Ambient.UserName : User?.Identity?.IsAuthenticated == true ? User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue(ClaimTypes.Name) : null;
        public IReadOnlyList<Guid>? AllowedDeviceIds
        {
            get
            {
                if (Ambient != null) return Ambient.AllowedDeviceIds;
                if (User?.FindFirstValue(ClaimNames.Restricted) != "1") return null;
                return User.FindAll(ClaimNames.DeviceId).Select(c => Guid.TryParse(c.Value, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
            }
        }
        public Guid? ApiKeyId => Guid.TryParse(User?.FindFirstValue(ClaimNames.KeyId), out var k) ? k : null;
        public string? IpAddress => Ambient?.Channel ?? _http.HttpContext?.Connection.RemoteIpAddress?.MapToIPv4().ToString();
        public string ActorType => Ambient != null ? "user" : ApiKeyId != null ? "token" : UserId != null ? "user" : "system";
    }

    // ---- [HasPermission("x.y")] -> policy "Permission:x.y" -> "permission" claim ----------------------------------------------

    public sealed class PermissionRequirement : IAuthorizationRequirement
    {
        public string Permission { get; }
        public PermissionRequirement(string permission) => Permission = permission;
    }

    public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            if (context.User.HasClaim(Perm.ClaimType, requirement.Permission)) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    /// <summary>Builds the policy for any "Permission:..." name on demand, so new permissions need no registration.</summary>
    public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider _fallback;
        public PermissionPolicyProvider(IOptions<AuthorizationOptions> options) => _fallback = new DefaultAuthorizationPolicyProvider(options);

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            if (policyName.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
            {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(policyName[HasPermissionAttribute.PolicyPrefix.Length..]))
                    .Build();
                return Task.FromResult<AuthorizationPolicy?>(policy);
            }
            return _fallback.GetPolicyAsync(policyName);
        }
    }
}
