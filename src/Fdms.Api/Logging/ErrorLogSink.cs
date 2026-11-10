using System.Security.Claims;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Auth;
using Fdms.Infrastructure.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Fdms.Api.Logging
{
    /// <summary>
    /// Copies every Error/Fatal log event (unhandled exceptions, ZIMRA failures, background-job crashes) into the system error log, with the request
    /// it happened in. It only queues and returns, so logging can never slow down or break a fiscal operation.
    /// </summary>
    public sealed class ErrorLogSink : ILogEventSink
    {
        private volatile IServiceProvider? _services;

        /// <summary>Called once the application is built; until then (startup) nothing is captured.</summary>
        public void Attach(IServiceProvider services) => _services = services;

        public void Emit(LogEvent e)
        {
            if (e.Level < LogEventLevel.Error) return;
            try
            {
                var source = e.Properties.TryGetValue("SourceContext", out var sc) ? sc.ToString().Trim('"') : null;
                if (source != null && source.StartsWith("Fdms.Infrastructure.Logging", StringComparison.Ordinal)) return;   // never feed itself

                var services = _services;
                var queue = services?.GetService<ErrorLogQueue>();
                if (services == null || queue == null) return;   // the host is still starting
                var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
                var message = e.RenderMessage();
                if (message.Length > 2000) message = message[..2000];
                var stack = e.Exception?.ToString();
                if (stack is { Length: > 10_000 }) stack = stack[..10_000];
                var user = http?.User;
                var entry = new ErrorLog
                {
                    Severity = e.Level == LogEventLevel.Fatal ? "Critical" : "Error",
                    Source = source, Message = message, ExceptionType = e.Exception?.GetType().FullName, StackTrace = stack,
                    Path = http?.Request.Path.Value, Method = http?.Request.Method, IpAddress = http?.Connection.RemoteIpAddress?.MapToIPv4().ToString(),
                    UserName = user?.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue(ClaimTypes.Name) : null,
                    ClientId = Guid.TryParse(user?.FindFirstValue(ClaimNames.ClientId), out var c) ? c : null,
                    TraceId = http?.TraceIdentifier, MachineName = System.Environment.MachineName, Environment = services.GetService<IHostEnvironment>()?.EnvironmentName,
                    CreatedAt = e.Timestamp.UtcDateTime,
                };
                entry.Fingerprint = ErrorLogQueue.Fingerprint(entry.ExceptionType, message, entry.Path);
                queue.TryWrite(entry);
            }
            catch { /* a logging problem must never surface */ }
        }
    }
}
