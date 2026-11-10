using System.Linq.Expressions;
using System.Net;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Billing;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Removes one company and everything that belongs to it, in one transaction: every table with a ClientId column, plus the rows that belong to it
    /// through its users, devices, fiscal days and approval requests. Links from OTHER companies (a company it referred, a registration that used its code) are cut, not deleted.
    /// </summary>
    public static class TenantPurger
    {
        /// <summary>
        /// Tables without a ClientId column, and why they are safe: either platform-wide (plans, roles, menus, settings ...) or handled below through the user, device,
        /// fiscal day or approval request they hang off. A test fails when a new table appears that is in neither group, so it is never silently left behind.
        /// </summary>
        public static readonly IReadOnlySet<string> WithoutClientId = new HashSet<string>
        {
            // platform-wide
            "Client", "Currency", "DatabaseBackup", "Partner", "PartnerPayout", "Permission", "Plan", "Role", "RoleMenu", "RolePermission", "SectionModule", "Submodule", "SystemModule",
            "SystemSetting", "IdentityRoleClaim`1",
            // handled through their parent
            "FiscalDay", "FiscalDayCounterRow", "IdentityUserClaim`1", "IdentityUserLogin`1", "IdentityUserRole`1", "IdentityUserToken`1", "Passkey", "RefreshToken",
            "UserBranch", "UserDevice", "ApprovalDecision", "ReferralReward",
        };

        public static async Task<int> PurgeAsync(FdmsDbContext db, Guid clientId, CancellationToken ct)
        {
            var n = 0;
            void Kill<T>(IEnumerable<T> rows) where T : class { foreach (var r in rows) { db.Remove(r); n++; } }

            var userIds = await db.Users.Where(u => u.ClientId == clientId).Select(u => u.Id).ToListAsync(ct);
            var deviceIds = await db.Devices.Where(d => d.ClientId == clientId).Select(d => d.Id).ToListAsync(ct);
            var dayIds = await db.FiscalDays.Where(f => deviceIds.Contains(f.DeviceId)).Select(f => f.Id).ToListAsync(ct);
            var requestIds = await db.ApprovalRequests.Where(r => r.ClientId == clientId).Select(r => r.Id).ToListAsync(ct);

            Kill(await db.FiscalDayCounters.Where(x => dayIds.Contains(x.FiscalDayId)).ToListAsync(ct));
            Kill(await db.FiscalDays.Where(x => deviceIds.Contains(x.DeviceId)).ToListAsync(ct));
            Kill(await db.ApprovalDecisions.Where(x => requestIds.Contains(x.RequestId)).ToListAsync(ct));
            Kill(await db.UserBranches.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct));
            Kill(await db.UserDevices.Where(x => userIds.Contains(x.UserId) || deviceIds.Contains(x.DeviceId)).ToListAsync(ct));
            Kill(await db.UserRoles.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct));
            Kill(await db.UserClaims.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct));
            Kill(await db.UserLogins.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct));
            Kill(await db.UserTokens.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct));
            Kill(await db.RefreshTokens.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct));
            Kill(await db.Passkeys.Where(x => userIds.Contains(x.UserId)).ToListAsync(ct));
            Kill(await db.ReferralRewards.Where(x => x.ReferredClientId == clientId || x.ReferrerClientId == clientId).ToListAsync(ct));

            // other companies keep their data; only the link to this one goes
            foreach (var c in await db.Clients.Where(c => c.ReferredByClientId == clientId).ToListAsync(ct)) c.ReferredByClientId = null;
            foreach (var a in await db.Applications.Where(a => a.ReferrerClientId == clientId && a.ClientId != clientId).ToListAsync(ct)) a.ReferrerClientId = null;

            // every table with a ClientId column
            foreach (var et in db.Model.GetEntityTypes().Where(e => e.BaseType == null && !e.IsOwned() && !e.HasSharedClrType && e.FindProperty("ClientId") is not null))
            {
                var prop = et.FindProperty("ClientId")!;
                var method = typeof(TenantPurger).GetMethod(prop.ClrType == typeof(Guid) ? nameof(ByClient) : nameof(ByClientNullable), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                    .MakeGenericMethod(et.ClrType);
                var rows = await (Task<List<object>>)method.Invoke(null, new object[] { db, clientId, ct })!;
                foreach (var r in rows) { db.Remove(r); n++; }
            }
            Kill(await db.Clients.Where(c => c.Id == clientId).ToListAsync(ct));

            await db.SaveChangesAsync(ct);
            return n;
        }

        private static async Task<List<object>> ByClient<T>(FdmsDbContext db, Guid id, CancellationToken ct) where T : class
        {
            var e = Expression.Parameter(typeof(T), "e");
            var body = Expression.Equal(Expression.Call(typeof(EF), nameof(EF.Property), new[] { typeof(Guid) }, e, Expression.Constant("ClientId")), Expression.Constant(id));
            return (await db.Set<T>().Where(Expression.Lambda<Func<T, bool>>(body, e)).ToListAsync(ct)).Cast<object>().ToList();
        }

        private static async Task<List<object>> ByClientNullable<T>(FdmsDbContext db, Guid id, CancellationToken ct) where T : class
        {
            var e = Expression.Parameter(typeof(T), "e");
            var body = Expression.Equal(Expression.Call(typeof(EF), nameof(EF.Property), new[] { typeof(Guid?) }, e, Expression.Constant("ClientId")), Expression.Constant((Guid?)id, typeof(Guid?)));
            return (await db.Set<T>().Where(Expression.Lambda<Func<T, bool>>(body, e)).ToListAsync(ct)).Cast<object>().ToList();
        }
    }

    /// <summary>
    /// Demo accounts that never paid. When the demo ends the company is told (bell + email) that its invoice must be paid within the grace period
    /// (Onboarding:DeleteUnpaidAfterDays, default 7), reminded two days before, and then the company and all its data are deleted.
    /// Never touched: a company that ever paid an invoice, one approved for live devices, or one the operator marked "keep".
    /// Unconfirmed registrations (the email code was never entered) are removed after the same number of days.
    /// </summary>
    public sealed class DemoCleanupService : IDemoCleanupService
    {
        private readonly FdmsDbContext _db;
        private readonly INotificationService _notify;
        private readonly IEmailSender _email;
        private readonly IConfiguration _cfg;
        private readonly ILogger<DemoCleanupService> _log;
        public DemoCleanupService(FdmsDbContext db, INotificationService notify, IEmailSender email, IConfiguration cfg, ILogger<DemoCleanupService> log) { _db = db; _notify = notify; _email = email; _cfg = cfg; _log = log; }

        private bool Enabled => _cfg.GetValue("Onboarding:DeleteUnpaidDemo", true);
        private int GraceDays => Math.Clamp(_cfg.GetValue("Onboarding:DeleteUnpaidAfterDays", 7), 1, 365);

        private sealed record Candidate(Client Client, Subscription Sub, string? Plan, DateTime DeleteOn);

        private async Task<List<Candidate>> CandidatesAsync(bool includeKept, CancellationToken ct)
        {
            var paid = (await _db.BillingInvoices.AsNoTracking().Where(i => i.Status == "Paid").Select(i => i.ClientId).Distinct().ToListAsync(ct)).ToHashSet();
            var subs = await _db.Subscriptions.AsNoTracking().Where(s => s.TrialEndsAt != null && (s.Status == "Trial" || s.Status == "PastDue" || s.Status == "Suspended")).ToListAsync(ct);
            var ids = subs.Select(s => s.ClientId).ToList();
            var clients = await _db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id) && !c.LiveApproved).ToDictionaryAsync(c => c.Id, ct);
            var plans = await _db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            return subs.Where(s => clients.ContainsKey(s.ClientId) && !paid.Contains(s.ClientId) && (includeKept || !clients[s.ClientId].KeepWhenUnpaid))
                .Select(s => new Candidate(clients[s.ClientId], s, plans.GetValueOrDefault(s.PlanId), s.TrialEndsAt!.Value.AddDays(GraceDays))).OrderBy(c => c.DeleteOn).ToList();
        }

        public async Task<IReadOnlyList<DemoAccountDto>> ListAsync(CancellationToken ct)
        {
            var list = await CandidatesAsync(true, ct);
            var ids = list.Select(c => c.Client.Id).ToList();
            var devices = await _db.Devices.AsNoTracking().Where(d => ids.Contains(d.ClientId)).GroupBy(d => d.ClientId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var receipts = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.ClientId)).GroupBy(r => r.ClientId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            return list.Select(c => new DemoAccountDto(c.Client.Id, c.Client.Name, c.Client.Email, c.Plan, c.Sub.Status, c.Sub.TrialEndsAt!.Value, c.DeleteOn, c.Client.KeepWhenUnpaid,
                devices.GetValueOrDefault(c.Client.Id), receipts.GetValueOrDefault(c.Client.Id), Enabled && !c.Client.KeepWhenUnpaid)).ToList();
        }

        public async Task SetKeepAsync(Guid clientId, bool keep, CancellationToken ct)
        {
            var c = await _db.Clients.FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Company");
            c.KeepWhenUnpaid = keep;
            _db.AuditLogs.Add(new AuditLog { ClientId = c.Id, Action = keep ? "demo.keep" : "demo.keep-off", Entity = "Client", EntityId = c.Id.ToString(), Detail = keep ? "never deleted automatically" : "deleted when the demo ends unpaid" });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<int> RunAsync(DateTime now, CancellationToken ct)
        {
            if (!Enabled) return 0;
            var done = 0;
            foreach (var c in await CandidatesAsync(false, ct))
            {
                var trialEnd = c.Sub.TrialEndsAt!.Value;
                if (now < trialEnd) continue;
                var invoice = await _db.BillingInvoices.AsNoTracking().Where(i => i.ClientId == c.Client.Id && i.Status == "Open").OrderBy(i => i.IssuedAt).Select(i => i.Number).FirstOrDefaultAsync(ct);
                var pay = invoice == null ? "Choose a plan and pay under Billing" : $"Pay invoice {invoice} under Billing";
                if (now < c.DeleteOn)
                {
                    // told once when the demo ends, and once more two days before the data goes
                    await _notify.RaiseAsync(c.Client.Id, "billing", "bad", $"demo-ended:{c.Client.Id}", "Your demo has ended",
                        $"{pay} by {c.DeleteOn:d MMM yyyy} to keep your account. Demo accounts that are not paid are deleted with all their data on that day.", "/billing", ct);
                    if (now >= c.DeleteOn.AddDays(-2))
                        await _notify.RaiseAsync(c.Client.Id, "billing", "bad", $"demo-delete-soon:{c.Client.Id}", $"Your account will be deleted on {c.DeleteOn:d MMM yyyy}",
                            $"{pay} before then, or everything in this account (devices, receipts, customers, users) is deleted and cannot be brought back.", "/billing", ct);
                    continue;
                }
                try
                {
                    var name = c.Client.Name; var email = c.Client.Email;
                    var rows = await TenantPurger.PurgeAsync(_db, c.Client.Id, ct);
                    _db.ChangeTracker.Clear();
                    _db.AuditLogs.Add(new AuditLog { Action = "demo.deleted", Entity = "Client", EntityId = c.Client.Id.ToString(), Detail = $"{name} ({email}): demo ended {trialEnd:d MMM yyyy}, unpaid after {GraceDays} days; {rows} records deleted" });
                    await _db.SaveChangesAsync(ct);
                    await _notify.RaisePlatformAsync("demo", "info", $"demo-deleted:{c.Client.Id}", $"Unpaid demo deleted: {name}",
                        $"{name} ({email}) did not pay within {GraceDays} days after the demo ended; the account and its {rows} records were deleted.", "/admin/subscriptions", ct);
                    if (!string.IsNullOrWhiteSpace(email))
                        await _email.SendAsync(email, "Your FiscalHub demo account was deleted", $"<p>The demo account of <b>{WebUtility.HtmlEncode(name)}</b> ended on {trialEnd:d MMM yyyy} and was not paid, so it and its demo data have been deleted.</p><p>You are welcome to register again at any time.</p>", ct);
                    done++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _db.ChangeTracker.Clear();
                    _log.LogError(ex, "Deleting the unpaid demo {Client} failed", c.Client.Id);
                }
            }

            // registrations whose email was never confirmed
            var stale = await _db.Applications.Where(a => a.Status == "Unverified" && a.CreatedAt < now.AddDays(-GraceDays)).ToListAsync(ct);
            if (stale.Count > 0) { _db.Applications.RemoveRange(stale); await _db.SaveChangesAsync(ct); }
            return done;
        }
    }

    /// <summary>Every few hours: warn and then delete demo accounts that ended unpaid.</summary>
    public sealed class DemoCleanupJob : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<DemoCleanupJob> _log;
        public DemoCleanupJob(IServiceScopeFactory scopes, ILogger<DemoCleanupJob> log) { _scopes = scopes; _log = log; }

        protected override async Task ExecuteAsync(CancellationToken stop)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(10), stop); } catch (OperationCanceledException) { return; }
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<IDemoCleanupService>().RunAsync(DateTime.UtcNow, stop);
                    if (n > 0) _log.LogInformation("Deleted {Count} unpaid demo account(s)", n);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log.LogError(ex, "Demo clean-up failed"); }
                try { await Task.Delay(TimeSpan.FromHours(3), stop); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
