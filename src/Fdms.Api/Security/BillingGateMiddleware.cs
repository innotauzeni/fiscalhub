using Fdms.Application.Abstractions;

namespace Fdms.Api.Security
{
    /// <summary>
    /// A company that has not settled its invoice may only use Billing (and sign-in, profile, documentation): a new account whose first invoice is unpaid, a trial
    /// that ended, or a subscription that ran out. Everything else - the console and API tokens alike - answers 402 until the invoice is paid, then opens up at once.
    /// Paths are compared whole-segment and case-insensitively, so nothing can slip through by naming a look-alike path.
    /// </summary>
    public sealed class BillingGateMiddleware
    {
        private static readonly string[] Open = { "/api/v1/billing", "/api/v1/auth", "/api/v1/public", "/api/v1/docs", "/api/v1/payments" };
        /// <summary>Exact paths that stay open too: the bell (it is where "your invoice is ready to pay" shows), not the alert settings behind it.</summary>
        private static readonly string[] OpenExact = { "/api/v1/notifications", "/api/v1/notifications/read" };
        private readonly RequestDelegate _next;
        public BillingGateMiddleware(RequestDelegate next) => _next = next;

        private static bool IsOpen(string path) => OpenExact.Any(p => path.TrimEnd('/').Equals(p, StringComparison.OrdinalIgnoreCase))
            || Open.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));

        public async Task InvokeAsync(HttpContext ctx, IBillingService billing)
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && ctx.User.Identity?.IsAuthenticated == true && !IsOpen(path) && ctx.User.ClientIdOrNull() is { } clientId)
            {
                var a = await billing.GetAccessAsync(clientId, ctx.RequestAborted);
                if (a.Locked)
                    throw new GatewayException(402, "PAYMENT_REQUIRED", a.Reason switch
                    {
                        "EXPIRED" => "Your subscription has expired. Settle your invoice under Billing to unlock everything.",
                        "TRIAL_EXPIRED" => "Your trial has ended. Choose and pay for a plan under Billing to continue.",
                        _ => "Your account is waiting for its first payment. Settle your invoice under Billing to unlock everything.",
                    });
            }
            await _next(ctx);
        }
    }
}
