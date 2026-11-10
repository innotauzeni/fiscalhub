using Fdms.Application.Abstractions;
using Fdms.Infrastructure.Auth;

namespace Fdms.Api.Security
{
    /// <summary>
    /// When a company requires everyone to use the authenticator app or a passkey, a person who has not yet done so may reach only sign-in, their own profile
    /// and security settings (to set it up). Everything else answers 403 MFA_SETUP_REQUIRED until they have, and the next refresh of their session lifts it.
    /// </summary>
    public sealed class MfaGateMiddleware
    {
        private static readonly string[] Open = { "/api/v1/auth", "/api/v1/security", "/api/v1/profile", "/api/v1/public", "/api/v1/docs" };
        private readonly RequestDelegate _next;
        public MfaGateMiddleware(RequestDelegate next) => _next = next;

        private static bool IsOpen(string path) => Open.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));

        public async Task InvokeAsync(HttpContext ctx)
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && ctx.User.Identity?.IsAuthenticated == true && ctx.User.HasClaim(ClaimNames.MfaSetupRequired, "1") && !IsOpen(path))
                throw new GatewayException(403, "MFA_SETUP_REQUIRED", "Your company requires extra protection. Turn on two-step sign-in or add a passkey under My profile to continue.");
            await _next(ctx);
        }
    }
}
