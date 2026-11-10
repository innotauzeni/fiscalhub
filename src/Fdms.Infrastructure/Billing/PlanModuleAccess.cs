using Fdms.Application.Abstractions;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>Which optional modules a company's plan includes. A company without a subscription (made by hand) has everything.</summary>
    public static class PlanModuleAccess
    {
        private static long _version;
        /// <summary>Called when a plan or a subscription's plan changes, so the cached answers are not used again.</summary>
        public static void Changed() => Interlocked.Increment(ref _version);

        public sealed record Answer(string? PlanName, IReadOnlySet<string>? Modules)
        {
            public bool Includes(string key) => PlanModules.Includes(Modules, key);
        }

        public static async Task<Answer> ForClientAsync(FdmsDbContext db, Guid clientId, CancellationToken ct)
        {
            var row = await (from s in db.Subscriptions.AsNoTracking() join p in db.Plans.AsNoTracking() on s.PlanId equals p.Id where s.ClientId == clientId select new { p.Name, p.Modules }).FirstOrDefaultAsync(ct);
            return row == null ? new Answer(null, null) : new Answer(row.Name, PlanModules.Parse(row.Modules));
        }

        /// <summary>What the company's plan is for (fiscal, connector or accounting). A company without a plan (created by the operator) is fiscal.</summary>
        public static async Task<string> KindAsync(FdmsDbContext db, Guid clientId, CancellationToken ct) =>
            await (from s in db.Subscriptions.AsNoTracking() join p in db.Plans.AsNoTracking() on s.PlanId equals p.Id where s.ClientId == clientId select p.Kind).FirstOrDefaultAsync(ct) ?? PlanKinds.Fiscal;

        /// <summary>The same, remembered for 30 seconds (it is asked on every API call).</summary>
        public static Task<Answer> CachedAsync(IMemoryCache cache, FdmsDbContext db, Guid clientId, CancellationToken ct) =>
            cache.GetOrCreateAsync($"plan-modules:{clientId:N}:{Interlocked.Read(ref _version)}", e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30); return ForClientAsync(db, clientId, ct); })!;
    }
}
