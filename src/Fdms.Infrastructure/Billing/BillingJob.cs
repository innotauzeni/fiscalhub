using Fdms.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>Every two minutes: asks the payment gateways about pending payments, so an invoice shows as paid even when the customer never came back to the page and the webhook was lost.</summary>
    public sealed class PendingPaymentsJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<PendingPaymentsJob> _log;
        public PendingPaymentsJob(IServiceScopeFactory scopes, ILogger<PendingPaymentsJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(2), stop);
                    using var scope = _scopes.CreateScope();
                    var paid = await scope.ServiceProvider.GetRequiredService<IPaymentService>().RefreshPendingAsync(stop);
                    if (paid > 0) _log.LogInformation("{Count} pending payment(s) confirmed by the gateway", paid);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogError(ex, "Checking pending payments failed"); }
            }
        }
    }

    /// <summary>Hourly: renewal invoices, overdue marking and (when switched on) suspension of companies that did not pay.</summary>
    public sealed class BillingJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<BillingJob> _log;
        public BillingJob(IServiceScopeFactory scopes, ILogger<BillingJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(3), stop);
                    using var scope = _scopes.CreateScope();
                    var changed = await scope.ServiceProvider.GetRequiredService<IBillingService>().RunMaintenanceAsync(stop);
                    if (changed > 0) _log.LogInformation("Billing maintenance changed {Count} item(s)", changed);
                    await Task.Delay(TimeSpan.FromMinutes(57), stop);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.LogError(ex, "Billing maintenance failed"); }
            }
        }
    }
}
