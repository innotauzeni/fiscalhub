using Fdms.Application.Abstractions;
using Fdms.Infrastructure.Auth;
using Fdms.Infrastructure.Billing;
using Fdms.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace Fdms.Api.Security
{
    /// <summary>
    /// A company can only use the optional modules its plan includes (accounting, invoice designer, approvals, quotes, AI reports, API tokens and webhooks,
    /// WhatsApp and SMS, Zoho Books). Anything else answers 403 PLAN_FEATURE with the plan's name, the console and API tokens alike; a device API token itself
    /// needs the "API tokens and webhooks" module. Paths are matched by whole segments, case-insensitively.
    /// </summary>
    public sealed class PlanModuleMiddleware
    {
        private readonly RequestDelegate _next;
        public PlanModuleMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext ctx, FdmsDbContext db, IMemoryCache cache)
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && ctx.User.Identity?.IsAuthenticated == true && ctx.User.ClientIdOrNull() is { } clientId)
            {
                var token = ctx.User.HasClaim(c => c.Type == ClaimNames.KeyId);
                var module = token ? PlanModules.Find("api") : PlanModules.ForPath(path);
                var also = token ? PlanModules.ForPath(path) : null;
                if (module != null || also != null)
                {
                    var plan = await PlanModuleAccess.CachedAsync(cache, db, clientId, ctx.RequestAborted);
                    foreach (var m in new[] { module, also })
                        if (m != null && !plan.Includes(m.Key))
                            throw new GatewayException(403, "PLAN_FEATURE", $"Your {plan.PlanName ?? "current"} plan does not include {m.Label}. Upgrade your plan under Billing to use it.");
                }
            }
            await _next(ctx);
        }
    }
}
