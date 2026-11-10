using System.Diagnostics;
using Fdms.Application.Abstractions;

namespace Fdms.Api.Security
{
    /// <summary>
    /// Records every authenticated API call made for a company (a signed-in person or an integrating token): who, which device, what, the outcome and the time taken.
    /// It sits before the billing gate and the authorization step so refused calls (402 / 403) are recorded too. Reading the activity itself is not recorded.
    /// </summary>
    public sealed class ActivityLogMiddleware
    {
        private readonly RequestDelegate _next;
        public ActivityLogMiddleware(RequestDelegate next) => _next = next;

        private static bool Skip(string path) =>
            !path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/activity", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/auth/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/public", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/docs", StringComparison.OrdinalIgnoreCase);

        public async Task InvokeAsync(HttpContext ctx)
        {
            var path = ctx.Request.Path.Value ?? "";
            if (Skip(path) || ctx.User.Identity?.IsAuthenticated != true || ctx.User.ClientIdOrNull() is not { } clientId) { await _next(ctx); return; }

            var sw = Stopwatch.StartNew();
            int? status = null; string? code = null;
            try { await _next(ctx); }
            catch (GatewayException g) { status = g.Status; code = g.Code; throw; }
            catch { status = 500; code = "ERROR"; throw; }
            finally
            {
                sw.Stop();
                var scope = ctx.RequestServices.GetRequiredService<ICallerScope>();
                var device = scope.AllowedDeviceIds is { Count: 1 } one ? one[0] : DeviceFromPath(path);
                var ua = ctx.Request.Headers.UserAgent.ToString();
                // a fresh scope: the request's own services may already be disposed or hold a failed unit of work
                using var s = ctx.RequestServices.CreateScope();
                await s.ServiceProvider.GetRequiredService<IActivityService>().RecordAsync(new ApiActivityEntry(clientId, device, scope.ApiKeyId, scope.UserId, scope.UserName ?? ctx.User.Identity?.Name,
                    scope.ActorType == "token" ? "token" : "user", ctx.Request.Method, path, status ?? ctx.Response.StatusCode, (int)sw.ElapsedMilliseconds, code, scope.IpAddress, ua), CancellationToken.None);
            }
        }

        private static Guid? DeviceFromPath(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var i = Array.FindIndex(parts, p => p.Equals("devices", StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < parts.Length && Guid.TryParse(parts[i + 1], out var g) ? g : null;
        }
    }
}
