using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Security
{
    /// <summary>Maps business and FDMS failures to RFC 7807 problem responses with a stable <c>errorCode</c>.</summary>
    public sealed class ProblemExceptionHandler : IExceptionHandler
    {
        private readonly ILogger<ProblemExceptionHandler> _log;
        public ProblemExceptionHandler(ILogger<ProblemExceptionHandler> log) => _log = log;

        public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
        {
            ProblemDetails p;
            switch (ex)
            {
                case GatewayException g:
                    p = new ProblemDetails { Status = g.Status, Title = g.Message };
                    p.Extensions["errorCode"] = g.Code;
                    break;
                case FdmsException f:
                    if (f.IsTransient) _log.LogError(f, "ZIMRA FDMS unreachable or failing on {Path}: {Message}", ctx.Request.Path, f.Message);   // shows in System logs; a 422 rejection is a business answer, not a system error
                    // FDMS rejected the request (4xx) -> 422; FDMS/network trouble -> 503 so callers know to retry.
                    p = new ProblemDetails { Status = f.IsTransient ? 503 : 422, Title = f.Message };
                    p.Extensions["errorCode"] = f.ErrorCode ?? (f.IsTransient ? "FDMS_UNAVAILABLE" : "FDMS_ERROR");
                    p.Extensions["fdmsStatus"] = f.HttpStatus;
                    break;
                case BadHttpRequestException b:
                    p = new ProblemDetails { Status = 400, Title = "Malformed request body." };
                    p.Extensions["errorCode"] = "BAD_REQUEST";
                    _log.LogDebug(b, "Bad request");
                    break;
                default:
                    _log.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);
                    p = new ProblemDetails { Status = 500, Title = "Unexpected server error." };
                    p.Extensions["errorCode"] = "INTERNAL_ERROR";
                    break;
            }
            p.Extensions["traceId"] = ctx.TraceIdentifier;
            ctx.Response.StatusCode = p.Status!.Value;
            await ctx.Response.WriteAsJsonAsync(p, ct);
            return true;
        }
    }
}
