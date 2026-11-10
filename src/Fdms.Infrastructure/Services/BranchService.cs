using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Branches of a company, the devices assigned to them, and per-user access limits. A user is either unrestricted ("all": every device of
    /// the company) or restricted to the devices of their assigned branches plus individually assigned devices. The limit is enforced by the same
    /// allowed-device filter an API token already uses, so a restricted user cannot see, fiscalise on or list anything of another device.
    /// </summary>
    public sealed class BranchService : IBranchService
    {
        private static readonly Regex Code = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,19}$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IBillingService _billing;
        public BranchService(FdmsDbContext db, ICallerScope scope, IBillingService billing) { _db = db; _scope = scope; _billing = billing; }

        /// <summary>Someone limited to certain devices must not manage branches or other people's access (they could widen their own).</summary>
        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot manage branches or user access.");
        }

        public async Task<IReadOnlyList<BranchDto>> ListAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            var branches = await _db.Branches.AsNoTracking().Where(b => b.ClientId == clientId).OrderBy(b => b.Name).ToListAsync(ct);
            var devices = await _db.Devices.AsNoTracking().Where(d => d.ClientId == clientId && d.BranchId != null).Select(d => new { d.Id, d.BranchId }).ToListAsync(ct);
            var users = await _db.UserBranches.AsNoTracking().GroupBy(u => u.BranchId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            return branches.Select(b => ToDto(b, devices.Where(d => d.BranchId == b.Id).Select(d => d.Id).ToList(), users.GetValueOrDefault(b.Id))).ToList();
        }

        public async Task<BranchDto> SaveAsync(Guid clientId, SaveBranchRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var name = req.Name?.Trim() ?? "";
            var code = req.Code?.Trim() ?? "";
            if (name.Length < 2 || name.Length > 120) throw GatewayException.BadRequest("NAME_INVALID", "Give the branch a name (2-120 characters).");
            if (!Code.IsMatch(code)) throw GatewayException.BadRequest("CODE_INVALID", "The branch code is 1-20 letters, digits, - or _ (e.g. HRE-01).");
            var id = req.Id is { } given && given != Guid.Empty ? given : (Guid?)null;
            if (await _db.Branches.AnyAsync(b => b.ClientId == clientId && b.Code == code && b.Id != id, ct)) throw GatewayException.Conflict("BRANCH_CODE_EXISTS", "Another branch already uses that code.");

            if (id == null) await _billing.EnsureWithinPlanAsync(clientId, "branches", ct);
            var branch = id == null ? new Branch { ClientId = clientId } : await _db.Branches.FirstOrDefaultAsync(b => b.Id == id && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Branch");
            branch.Code = code; branch.Name = name; branch.IsActive = req.IsActive;
            branch.Address = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim();
            branch.Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
            if (id == null) _db.Branches.Add(branch);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = id == null ? "branch.create" : "branch.update", Detail = $"{code} {name}" });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(clientId, ct)).First(b => b.Id == branch.Id);
        }

        public async Task DeleteAsync(Guid clientId, Guid branchId, CancellationToken ct)
        {
            RequireWholeCompany();
            var branch = await _db.Branches.FirstOrDefaultAsync(b => b.Id == branchId && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Branch");
            if (await _db.Devices.AnyAsync(d => d.BranchId == branchId, ct)) throw GatewayException.Conflict("BRANCH_HAS_DEVICES", "Move this branch's devices to another branch first.");
            _db.Branches.Remove(branch);   // user assignments cascade
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "branch.delete", Detail = $"{branch.Code} {branch.Name}" });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<BranchDto> SetDevicesAsync(Guid clientId, Guid branchId, IReadOnlyList<Guid> deviceIds, CancellationToken ct)
        {
            RequireWholeCompany();
            var branch = await _db.Branches.FirstOrDefaultAsync(b => b.Id == branchId && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Branch");
            var wanted = (deviceIds ?? Array.Empty<Guid>()).Distinct().ToList();
            var own = await _db.Devices.Where(d => d.ClientId == clientId && (wanted.Contains(d.Id) || d.BranchId == branchId)).ToListAsync(ct);
            if (wanted.Any(w => own.All(d => d.Id != w))) throw GatewayException.BadRequest("DEVICE_INVALID", "One of those devices does not belong to this company.");
            foreach (var d in own) d.BranchId = wanted.Contains(d.Id) ? branchId : null;   // devices not listed leave the branch
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "branch.devices", Detail = $"{branch.Code}: {wanted.Count} device(s)" });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(clientId, ct)).First(b => b.Id == branchId);
        }

        // ---- per-user access ----------------------------------------------------------------------------------------

        public async Task<UserAccessDto> GetUserAccessAsync(Guid? callerClientId, string userId, CancellationToken ct)
        {
            RequireWholeCompany();
            var user = await FindUser(callerClientId, userId, ct);
            return await Describe(user, ct);
        }

        public async Task<UserAccessDto> SetUserAccessAsync(Guid? callerClientId, string userId, SetUserAccessRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var user = await FindUser(callerClientId, userId, ct);
            if (user.Id == _scope.UserId) throw GatewayException.BadRequest("SELF_CHANGE", "You cannot change your own access.");
            if (user.ClientId == null) throw GatewayException.BadRequest("PLATFORM_USER", "Platform operators are not tied to a company's devices.");
            if (req.Scope is not ("all" or "restricted")) throw GatewayException.BadRequest("SCOPE_INVALID", "Scope must be all or restricted.");

            var branchIds = (req.BranchIds ?? Array.Empty<Guid>()).Distinct().ToList();
            var deviceIds = (req.DeviceIds ?? Array.Empty<Guid>()).Distinct().ToList();
            if (req.Scope == "restricted")
            {
                if (branchIds.Count + deviceIds.Count == 0) throw GatewayException.BadRequest("ACCESS_EMPTY", "Choose at least one branch or device, or give the user access to everything.");
                if (await _db.Branches.CountAsync(b => b.ClientId == user.ClientId && branchIds.Contains(b.Id), ct) != branchIds.Count) throw GatewayException.BadRequest("BRANCH_INVALID", "One of those branches does not belong to this company.");
                if (await _db.Devices.CountAsync(d => d.ClientId == user.ClientId && deviceIds.Contains(d.Id), ct) != deviceIds.Count) throw GatewayException.BadRequest("DEVICE_INVALID", "One of those devices does not belong to this company.");
            }

            _db.UserBranches.RemoveRange(await _db.UserBranches.Where(x => x.UserId == userId).ToListAsync(ct));
            _db.UserDevices.RemoveRange(await _db.UserDevices.Where(x => x.UserId == userId).ToListAsync(ct));
            user.AccessScope = req.Scope;
            if (req.Scope == "restricted")
            {
                _db.UserBranches.AddRange(branchIds.Select(b => new UserBranch { UserId = userId, BranchId = b }));
                _db.UserDevices.AddRange(deviceIds.Select(d => new UserDevice { UserId = userId, DeviceId = d }));
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = user.ClientId, Action = "user.access", Detail = $"{user.Email}: {req.Scope} ({branchIds.Count} branches, {deviceIds.Count} devices)" });
            await _db.SaveChangesAsync(ct);
            return await Describe(user, ct);
        }

        private async Task<ApplicationUser> FindUser(Guid? callerClientId, string userId, CancellationToken ct) =>
            await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && (callerClientId == null || u.ClientId == callerClientId), ct) ?? throw GatewayException.NotFound("User");

        private async Task<UserAccessDto> Describe(ApplicationUser user, CancellationToken ct)
        {
            var branchIds = await _db.UserBranches.AsNoTracking().Where(x => x.UserId == user.Id).Select(x => x.BranchId).ToListAsync(ct);
            var deviceIds = await _db.UserDevices.AsNoTracking().Where(x => x.UserId == user.Id).Select(x => x.DeviceId).ToListAsync(ct);
            var effective = user.AccessScope == "restricted" && user.ClientId != null
                ? await _db.Devices.AsNoTracking().Where(d => d.ClientId == user.ClientId && ((d.BranchId != null && branchIds.Contains(d.BranchId.Value)) || deviceIds.Contains(d.Id))).Select(d => d.Id).ToListAsync(ct)
                : new List<Guid>();
            return new UserAccessDto(user.AccessScope == "restricted" ? "restricted" : "all", branchIds, deviceIds, effective);
        }

        private static BranchDto ToDto(Branch b, IReadOnlyList<Guid> deviceIds, int users) => new(b.Id, b.Code, b.Name, b.Address, b.Phone, b.IsActive, deviceIds, users);
    }
}
