using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Auth
{
    internal static class IdentityErrors
    {
        public static string Join(IdentityResult r) => string.Join(" ", r.Errors.Select(e => e.Description));
    }

    // ================================================================================================================
    public sealed class ClientAdminService : IClientAdminService
    {
        private readonly FdmsDbContext _db;
        private readonly UserManager<ApplicationUser> _users;
        public ClientAdminService(FdmsDbContext db, UserManager<ApplicationUser> users) { _db = db; _users = users; }

        public async Task<IReadOnlyList<ClientDto>> ListAsync(CancellationToken ct)
        {
            var users = await _db.Users.AsNoTracking().Where(u => u.ClientId != null).GroupBy(u => u.ClientId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
            var devs = await _db.Devices.AsNoTracking().GroupBy(d => d.ClientId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
            return (await _db.Clients.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
                .Select(c => new ClientDto(c.Id, c.Name, c.Email, c.IsActive, users.GetValueOrDefault(c.Id), devs.GetValueOrDefault(c.Id), c.CreatedAt)).ToList();
        }

        public async Task<ClientDto> CreateAsync(CreateClientRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Name)) throw GatewayException.BadRequest("NAME_REQUIRED", "Company name is required.");
            if (string.IsNullOrWhiteSpace(req.AdminEmail) || !req.AdminEmail.Contains('@')) throw GatewayException.BadRequest("EMAIL_INVALID", "A valid administrator email is required.");
            if (await _users.FindByEmailAsync(req.AdminEmail.Trim()) != null) throw GatewayException.Conflict("USER_EXISTS", "A user with that email already exists.");

            var client = new Client { Name = req.Name.Trim(), Email = req.Email?.Trim(), LiveApproved = false, Slug = await BrandService.UniqueSlugAsync(_db, req.Name.Trim(), ct) };   // starts on ZIMRA's demo environment like every company; live comes through the go-live process
            _db.Clients.Add(client);
            await _db.SaveChangesAsync(ct);

            var admin = new ApplicationUser
            {
                UserName = req.AdminEmail.Trim(), Email = req.AdminEmail.Trim(), EmailConfirmed = true, Name = req.AdminName?.Trim() ?? "", Surname = req.AdminSurname?.Trim() ?? "",
                ClientId = client.Id, MustChangePassword = true,
            };
            var res = await _users.CreateAsync(admin, req.AdminPassword ?? "");
            if (!res.Succeeded)
            {
                _db.Clients.Remove(client);   // nothing half-created
                await _db.SaveChangesAsync(ct);
                throw GatewayException.BadRequest("PASSWORD_REJECTED", IdentityErrors.Join(res));
            }
            await _users.AddToRoleAsync(admin, RoleNames.ClientAdmin);
            _db.AuditLogs.Add(new AuditLog { ClientId = client.Id, Action = "client.create", Detail = client.Name });
            await _db.SaveChangesAsync(ct);
            return new ClientDto(client.Id, client.Name, client.Email, client.IsActive, 1, 0, client.CreatedAt);
        }

        public async Task<ClientDto> SetActiveAsync(Guid clientId, bool active, CancellationToken ct)
        {
            var c = await _db.Clients.FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            c.IsActive = active;
            _db.AuditLogs.Add(new AuditLog { ClientId = c.Id, Action = active ? "client.activate" : "client.suspend", Detail = c.Name });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(ct)).First(x => x.Id == clientId);
        }
    }

    // ================================================================================================================
    public sealed class UserAdminService : IUserAdminService
    {
        private readonly FdmsDbContext _db;
        private readonly UserManager<ApplicationUser> _users;
        private readonly RoleManager<Role> _roles;
        private readonly ICallerScope _scope;
        private readonly IBillingService _billing;
        public UserAdminService(FdmsDbContext db, UserManager<ApplicationUser> users, RoleManager<Role> roles, ICallerScope scope, IBillingService billing) { _db = db; _users = users; _roles = roles; _scope = scope; _billing = billing; }

        /// <summary>
        /// Only company roles can be handed out here, to everyone including the platform operator: a platform role such as SuperAdmin is never offered or accepted by the
        /// Users screens, so nobody can be promoted to it by a click, a crafted request or a mistake. Platform administrators come from the start-up seed.
        /// </summary>
        public async Task<IReadOnlyList<string>> AssignableRolesAsync(Guid? callerClientId, CancellationToken ct) =>
            await _roles.Roles.Where(r => r.Level == "client").OrderBy(r => r.Name).Select(r => r.Name!).ToListAsync(ct);

        public async Task<IReadOnlyList<UserDto>> ListAsync(Guid? callerClientId, CancellationToken ct)
        {
            var users = await _db.Users.AsNoTracking().Where(u => callerClientId == null || u.ClientId == callerClientId).OrderBy(u => u.Email).ToListAsync(ct);
            var ids = users.Select(u => u.Id).ToList();
            var roleRows = await (from ur in _db.UserRoles.AsNoTracking() join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.Id where ids.Contains(ur.UserId) select new { ur.UserId, r.Name }).ToListAsync(ct);
            var clients = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            return users.Select(u => ToDto(u, roleRows.Where(r => r.UserId == u.Id).Select(r => r.Name!).ToList(), clients)).ToList();
        }

        public async Task<UserDto> CreateAsync(Guid? callerClientId, CreateUserRequest req, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@')) throw GatewayException.BadRequest("EMAIL_INVALID", "A valid email is required.");
            if (await _users.FindByEmailAsync(req.Email.Trim()) != null) throw GatewayException.Conflict("USER_EXISTS", "A user with that email already exists.");
            var role = await RoleFor(callerClientId, req.Role);

            Guid? clientId = callerClientId ?? req.ClientId;
            if (clientId == null) throw GatewayException.BadRequest("CLIENT_REQUIRED", "Choose the company this user belongs to.");
            if (clientId != null && !await _db.Clients.AnyAsync(c => c.Id == clientId, ct)) throw GatewayException.NotFound("Client");
            if (clientId != null) await _billing.EnsureWithinPlanAsync(clientId.Value, "users", ct);

            var userName = UserNames.Clean(req.UserName);
            if (userName != null && await _users.FindByNameAsync(userName) != null) throw GatewayException.Conflict("USERNAME_TAKEN", $"The user name \"{userName}\" is already taken.");
            var user = new ApplicationUser
            {
                UserName = userName ?? req.Email.Trim(), Email = req.Email.Trim(), EmailConfirmed = true,
                Name = req.Name?.Trim() ?? "", Surname = req.Surname?.Trim() ?? "", ClientId = clientId, MustChangePassword = true,
            };
            var res = await _users.CreateAsync(user, req.Password ?? "");
            if (!res.Succeeded) throw GatewayException.BadRequest("USER_REJECTED", IdentityErrors.Join(res));
            await _users.AddToRoleAsync(user, role.Name!);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "user.create", Detail = $"{user.Email} role={role.Name}" });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(callerClientId, ct)).First(u => u.Id == user.Id);
        }

        public async Task<UserDto> UpdateAsync(Guid? callerClientId, string userId, UpdateUserRequest req, CancellationToken ct)
        {
            var user = await Find(callerClientId, userId);
            if (user.Id == _scope.UserId) throw GatewayException.BadRequest("SELF_CHANGE", "You cannot change your own role or status.");
            if (req.Status is not ("ACTIVE" or "INACTIVE")) throw GatewayException.BadRequest("STATUS_INVALID", "Status must be ACTIVE or INACTIVE.");

            var role = await RoleFor(callerClientId, req.Role);
            // a platform administrator cannot be changed through this form (not their role, not their status), and a person outside any company cannot be given a company role
            if ((await _users.GetRolesAsync(user)).Any(r => r == RoleNames.SuperAdmin)) throw new GatewayException(403, "PLATFORM_USER_PROTECTED", "Platform administrators cannot be changed here.");
            if (user.ClientId == null) throw GatewayException.BadRequest("CLIENT_REQUIRED", "This person belongs to no company, so a company role cannot be given.");
            user.Name = req.Name?.Trim() ?? user.Name; user.Surname = req.Surname?.Trim() ?? user.Surname; user.Status = req.Status;
            await IdentityNames.ApplyAsync(_users, user, req.Email, req.UserName);
            var current = await _users.GetRolesAsync(user);
            if (!current.Contains(role.Name!)) { await _users.RemoveFromRolesAsync(user, current); await _users.AddToRoleAsync(user, role.Name!); }
            await _users.UpdateAsync(user);
            if (req.Status == "INACTIVE") await RevokeSessions(user.Id, ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = user.ClientId, Action = "user.update", Detail = $"{user.Email} role={role.Name} status={req.Status}" });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(callerClientId, ct)).First(u => u.Id == user.Id);
        }

        public async Task ResetPasswordAsync(Guid? callerClientId, string userId, string newPassword, CancellationToken ct)
        {
            var user = await Find(callerClientId, userId);
            var token = await _users.GeneratePasswordResetTokenAsync(user);
            var res = await _users.ResetPasswordAsync(user, token, newPassword ?? "");
            if (!res.Succeeded) throw GatewayException.BadRequest("PASSWORD_REJECTED", IdentityErrors.Join(res));
            user.MustChangePassword = true;
            await _users.UpdateAsync(user);
            await _users.ResetAccessFailedCountAsync(user);
            await _users.SetLockoutEndDateAsync(user, null);
            await RevokeSessions(user.Id, ct);
            _db.AuditLogs.Add(new AuditLog { ClientId = user.ClientId, Action = "user.reset-password", Detail = user.Email });
            await _db.SaveChangesAsync(ct);
        }

        private async Task<ApplicationUser> Find(Guid? callerClientId, string userId) =>
            await _db.Users.FirstOrDefaultAsync(u => u.Id == userId && (callerClientId == null || u.ClientId == callerClientId))
            ?? throw GatewayException.NotFound("User");

        private async Task<Role> RoleFor(Guid? callerClientId, string? name)
        {
            var role = string.IsNullOrWhiteSpace(name) ? null : await _roles.FindByNameAsync(name.Trim());
            if (role == null) throw GatewayException.BadRequest("ROLE_INVALID", "Choose a valid role.");
            if (role.Level != "client") throw new GatewayException(403, "ROLE_FORBIDDEN", "Platform roles such as SuperAdmin cannot be assigned here.");
            return role;
        }

        private async Task RevokeSessions(string userId, CancellationToken ct)
        {
            foreach (var t in await _db.RefreshTokens.Where(r => r.UserId == userId && !r.IsRevoked).ToListAsync(ct)) t.IsRevoked = true;
        }

        private static UserDto ToDto(ApplicationUser u, List<string> roles, Dictionary<Guid, string> clients) =>
            new(u.Id, u.Email ?? "", u.UserName ?? "", u.Name, u.Surname, u.ClientId, u.ClientId.HasValue ? clients.GetValueOrDefault(u.ClientId.Value) : null, roles, u.Status, u.LastLoginDate, u.MustChangePassword, u.AccessScope == "restricted" ? "restricted" : "all");
    }

    // ================================================================================================================
    public sealed class RoleAdminService : IRoleAdminService
    {
        private readonly FdmsDbContext _db;
        private readonly RoleManager<Role> _roles;
        public RoleAdminService(FdmsDbContext db, RoleManager<Role> roles) { _db = db; _roles = roles; }

        public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct)
        {
            var perms = await _db.RolePermissions.GroupBy(x => x.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var menus = await _db.RoleMenus.GroupBy(x => x.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            var users = await _db.UserRoles.GroupBy(x => x.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            return (await _roles.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct))
                .Select(r => new RoleDto(r.Id, r.Name!, r.Type, r.Level, r.Description, perms.GetValueOrDefault(r.Id), menus.GetValueOrDefault(r.Id), users.GetValueOrDefault(r.Id))).ToList();
        }

        public async Task<RoleDto> CreateRoleAsync(CreateRoleRequest req, CancellationToken ct)
        {
            var name = req.Name?.Trim() ?? "";
            if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9 _\-]{2,40}$")) throw GatewayException.BadRequest("ROLE_NAME_INVALID", "Role names are 3-41 characters: letters, digits, space, - and _.");
            if (req.Level is not ("client" or "platform")) throw GatewayException.BadRequest("ROLE_LEVEL_INVALID", "Level must be client or platform.");
            if (await _roles.FindByNameAsync(name) != null) throw GatewayException.Conflict("ROLE_EXISTS", "A role with that name already exists.");
            var role = new Role { Name = name, Type = "custom", Level = req.Level, Description = req.Description?.Trim() };
            var res = await _roles.CreateAsync(role);
            if (!res.Succeeded) throw GatewayException.BadRequest("ROLE_REJECTED", IdentityErrors.Join(res));
            _db.AuditLogs.Add(new AuditLog { Action = "role.create", Detail = name });
            await _db.SaveChangesAsync(ct);
            return (await ListRolesAsync(ct)).First(r => r.Id == role.Id);
        }

        public async Task<RoleDto> UpdateRoleAsync(string roleId, UpdateRoleRequest req, CancellationToken ct)
        {
            var role = await _roles.FindByIdAsync(roleId) ?? throw GatewayException.NotFound("Role");
            role.Description = req.Description?.Trim();
            if (role.Type != "system")   // built-in roles keep their name and level (the code and the menus rely on them)
            {
                var name = req.Name?.Trim() ?? "";
                if (!Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9 _\-]{2,40}$")) throw GatewayException.BadRequest("ROLE_NAME_INVALID", "Role names are 3-41 characters: letters, digits, space, - and _.");
                if (req.Level is not ("client" or "platform")) throw GatewayException.BadRequest("ROLE_LEVEL_INVALID", "Level must be client or platform.");
                var other = await _roles.FindByNameAsync(name);
                if (other != null && other.Id != role.Id) throw GatewayException.Conflict("ROLE_EXISTS", "A role with that name already exists.");
                role.Name = name; role.Level = req.Level;
            }
            var res = await _roles.UpdateAsync(role);
            if (!res.Succeeded) throw GatewayException.BadRequest("ROLE_REJECTED", IdentityErrors.Join(res));
            _db.AuditLogs.Add(new AuditLog { Action = "role.update", Detail = role.Name });
            await _db.SaveChangesAsync(ct);
            return (await ListRolesAsync(ct)).First(r => r.Id == role.Id);
        }

        public async Task DeleteRoleAsync(string roleId, CancellationToken ct)
        {
            var role = await _roles.FindByIdAsync(roleId) ?? throw GatewayException.NotFound("Role");
            if (role.Type == "system") throw GatewayException.Conflict("ROLE_SYSTEM", "Built-in roles cannot be deleted.");
            if (await _db.UserRoles.AnyAsync(ur => ur.RoleId == roleId, ct)) throw GatewayException.Conflict("ROLE_IN_USE", "Move the users of this role to another role first.");
            await _roles.DeleteAsync(role);
            _db.AuditLogs.Add(new AuditLog { Action = "role.delete", Detail = role.Name });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct) =>
            await _db.Permissions.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Module).ThenBy(p => p.Name).Select(p => new PermissionDto(p.Id, p.Name, p.DisplayName, p.Module)).ToListAsync(ct);

        public async Task<RoleAccessDto> GetAccessAsync(string roleId, CancellationToken ct)
        {
            var role = await _roles.FindByIdAsync(roleId) ?? throw GatewayException.NotFound("Role");
            return new RoleAccessDto(role.Id, role.Name!,
                await _db.RolePermissions.AsNoTracking().Where(x => x.RoleId == roleId).Select(x => x.PermissionId).ToListAsync(ct),
                await _db.RoleMenus.AsNoTracking().Where(x => x.RoleId == roleId).Select(x => x.SystemModuleId).ToListAsync(ct));
        }

        public async Task AssignPermissionsAsync(string roleId, IReadOnlyList<int> permissionIds, CancellationToken ct)
        {
            var role = await _roles.FindByIdAsync(roleId) ?? throw GatewayException.NotFound("Role");
            var ids = permissionIds.Distinct().ToList();
            var valid = await _db.Permissions.Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
            if (valid.Count != ids.Count) throw GatewayException.BadRequest("PERMISSION_INVALID", "Unknown permission id.");
            if (role.Level == "client" && valid.FirstOrDefault(v => Perm.PlatformOnly.Contains(v.Name)) is { } forbidden)
                throw GatewayException.BadRequest("PLATFORM_ONLY_PERMISSION", $"\"{forbidden.Name}\" belongs to the platform operator and cannot be given to a company role.");

            if (role.Name == RoleNames.SuperAdmin)   // the operator must never be able to lock themselves out
            {
                var need = new[] { Perm.RolesManage, Perm.ModulesManage, Perm.UsersManage, Perm.ClientsManage };
                var missing = need.Where(n => valid.All(v => v.Name != n)).ToList();
                if (missing.Count > 0) throw GatewayException.Conflict("SUPERADMIN_LOCKOUT", "SuperAdmin must keep: " + string.Join(", ", missing));
            }
            _db.RolePermissions.RemoveRange(await _db.RolePermissions.Where(x => x.RoleId == roleId).ToListAsync(ct));
            _db.RolePermissions.AddRange(ids.Select(id => new RolePermission { RoleId = roleId, PermissionId = id }));
            _db.AuditLogs.Add(new AuditLog { Action = "role.permissions", Detail = $"{role.Name}: {valid.Count} permissions" });
            await _db.SaveChangesAsync(ct);
        }

        public async Task AssignMenusAsync(string roleId, IReadOnlyList<int> moduleIds, CancellationToken ct)
        {
            var role = await _roles.FindByIdAsync(roleId) ?? throw GatewayException.NotFound("Role");
            var ids = moduleIds.Distinct().ToList();
            var valid = await _db.SystemModules.Where(m => ids.Contains(m.Id)).Select(m => new { m.Id, m.Slugname, m.AccessName }).ToListAsync(ct);
            if (valid.Count != ids.Count) throw GatewayException.BadRequest("MODULE_INVALID", "Unknown menu id.");
            if (role.Level == "client" && valid.FirstOrDefault(v => v.AccessName != null && Perm.PlatformOnly.Contains(v.AccessName)) is { } page)
                throw GatewayException.BadRequest("PLATFORM_ONLY_MENU", $"The page \"{page.Slugname}\" is for the platform operator and cannot be given to a company role.");
            if (role.Name == RoleNames.SuperAdmin && !(valid.Any(v => v.Slugname == "admin/roles") && valid.Any(v => v.Slugname == "admin/modules")))
                throw GatewayException.Conflict("SUPERADMIN_LOCKOUT", "SuperAdmin must keep the Roles & access and Menus & modules pages.");
            _db.RoleMenus.RemoveRange(await _db.RoleMenus.Where(x => x.RoleId == roleId).ToListAsync(ct));
            _db.RoleMenus.AddRange(ids.Select(id => new RoleMenu { RoleId = roleId, SystemModuleId = id }));
            _db.AuditLogs.Add(new AuditLog { Action = "role.menus", Detail = $"{role.Name}: {valid.Count} menu entries" });
            await _db.SaveChangesAsync(ct);
        }
    }

    // ================================================================================================================
    public sealed class ModuleAdminService : IModuleAdminService
    {
        private static readonly Regex Slug = new(@"^[a-z0-9][a-z0-9\-/]{0,78}$", RegexOptions.Compiled);
        private static readonly Regex IconName = new(@"^[a-z0-9\-]{1,40}$", RegexOptions.Compiled);
        private static readonly Regex Colour = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        public ModuleAdminService(FdmsDbContext db) => _db = db;

        public async Task<ModuleTreeDto> TreeAsync(CancellationToken ct) => new(
            await _db.SectionModules.AsNoTracking().OrderBy(s => s.SortOrder).Select(s => new SectionDto(s.Id, s.Name, s.Color, s.SortOrder, s.Status)).ToListAsync(ct),
            await _db.SystemModules.AsNoTracking().OrderBy(m => m.SortOrder).Select(m => new ModuleDto(m.Id, m.SectionModuleId, m.Name, m.Slugname, m.Icon, m.Description, m.AccessName, m.SortOrder, m.IsActive)).ToListAsync(ct),
            await _db.Submodules.AsNoTracking().OrderBy(s => s.Order).Select(s => new SubmoduleDto(s.Id, s.ModuleId, s.Name, s.Icon, s.Route, s.AccessName, s.Order, s.IsActive)).ToListAsync(ct));

        // ---- permission catalogue (dara: submodule permissions + batch add) -----------------------------------------
        private static readonly Regex PermName = new(@"^[a-z][a-z0-9_]{0,30}(\.[a-z0-9_]{1,30})+$", RegexOptions.Compiled);
        private static bool BuiltIn(string name) => Perm.All.Any(d => d.Name == name);

        public async Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct) =>
            await _db.Permissions.AsNoTracking().OrderBy(p => p.Name).Select(p => new PermissionDto(p.Id, p.Name, p.DisplayName, p.Module)).ToListAsync(ct);

        public async Task<PermissionDto> SavePermissionAsync(SavePermissionRequest req, CancellationToken ct)
        {
            var name = (req.Name ?? "").Trim().ToLowerInvariant();
            if (!PermName.IsMatch(name)) throw GatewayException.BadRequest("PERMISSION_NAME_INVALID", "Use lowercase words joined by dots, e.g. reports.export.");
            if (string.IsNullOrWhiteSpace(req.DisplayName) || string.IsNullOrWhiteSpace(req.Module)) throw GatewayException.BadRequest("PERMISSION_FIELDS", "Display name and module are required.");
            if (await _db.Permissions.AnyAsync(p => p.Name == name && p.Id != req.Id, ct)) throw GatewayException.Conflict("PERMISSION_EXISTS", "A permission with that name already exists.");
            var p = req.Id == 0 ? new Permission() : await _db.Permissions.FirstOrDefaultAsync(x => x.Id == req.Id, ct) ?? throw GatewayException.NotFound("Permission");
            if (req.Id != 0 && p.Name != name && BuiltIn(p.Name)) throw GatewayException.Conflict("PERMISSION_BUILTIN", "The code checks this permission by name, so it cannot be renamed.");
            p.Name = name; p.DisplayName = req.DisplayName.Trim(); p.Module = req.Module.Trim();
            if (req.Id == 0) _db.Permissions.Add(p);
            _db.AuditLogs.Add(new AuditLog { Action = req.Id == 0 ? "permission.create" : "permission.update", Detail = name });
            await _db.SaveChangesAsync(ct);
            return new PermissionDto(p.Id, p.Name, p.DisplayName, p.Module);
        }

        public async Task DeletePermissionAsync(int id, CancellationToken ct)
        {
            var p = await _db.Permissions.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Permission");
            if (BuiltIn(p.Name)) throw GatewayException.Conflict("PERMISSION_BUILTIN", "The code checks this permission, so it cannot be deleted.");
            _db.RolePermissions.RemoveRange(await _db.RolePermissions.Where(x => x.PermissionId == id).ToListAsync(ct));
            _db.Permissions.Remove(p);
            _db.AuditLogs.Add(new AuditLog { Action = "permission.delete", Detail = p.Name });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<BatchPermissionResult> BatchPermissionsAsync(BatchPermissionRequest req, CancellationToken ct)
        {
            var module = req.Module?.Trim() ?? "";
            if (module.Length == 0) throw GatewayException.BadRequest("PERMISSION_FIELDS", "Module is required.");
            var prefix = Regex.Replace((req.Prefix ?? "").Trim().ToLowerInvariant(), "[^a-z0-9_]", "");
            if (req.Items is null || req.Items.Count == 0) throw GatewayException.BadRequest("PERMISSION_FIELDS", "Add at least one action.");
            if (req.Items.Count > 50) throw GatewayException.BadRequest("PERMISSION_FIELDS", "At most 50 permissions per batch.");

            var existing = (await _db.Permissions.Select(p => p.Name).ToListAsync(ct)).ToHashSet();
            var created = new List<Permission>(); var skipped = new List<string>();
            foreach (var item in req.Items)
            {
                var action = Regex.Replace((item.Action ?? "").Trim().ToLowerInvariant(), "[^a-z0-9._]", "");
                if (action.Length == 0) continue;
                var name = action.Contains('.') ? action : prefix.Length > 0 ? prefix + "." + action : action;
                if (!PermName.IsMatch(name)) throw GatewayException.BadRequest("PERMISSION_NAME_INVALID", $"\"{name}\" is not a valid permission name (needs a prefix, e.g. reports.export).");
                if (!existing.Add(name)) { skipped.Add(name); continue; }
                var display = string.IsNullOrWhiteSpace(item.DisplayName) ? char.ToUpperInvariant(action[0]) + action[1..].Replace('.', ' ') : item.DisplayName.Trim();
                created.Add(new Permission { Name = name, DisplayName = display, Module = module });
            }
            _db.Permissions.AddRange(created);
            if (created.Count > 0) _db.AuditLogs.Add(new AuditLog { Action = "permission.batch", Detail = $"{module}: {string.Join(", ", created.Select(c => c.Name))}" });
            await _db.SaveChangesAsync(ct);
            return new BatchPermissionResult(created.Count, skipped, created.Select(c => new PermissionDto(c.Id, c.Name, c.DisplayName, c.Module)).ToList());
        }

        public async Task<SectionDto> SaveSectionAsync(SectionDto dto, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(dto.Name)) throw GatewayException.BadRequest("NAME_REQUIRED", "Name is required.");
            if (!Colour.IsMatch(dto.Color ?? "")) throw GatewayException.BadRequest("COLOR_INVALID", "Colour must look like #2563eb.");
            var s = dto.Id == 0 ? new SectionModule() : await _db.SectionModules.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw GatewayException.NotFound("Section");
            s.Name = dto.Name.Trim(); s.Color = dto.Color!; s.SortOrder = dto.SortOrder; s.Status = dto.Status;
            if (dto.Id == 0) _db.SectionModules.Add(s);
            await _db.SaveChangesAsync(ct);
            return new SectionDto(s.Id, s.Name, s.Color, s.SortOrder, s.Status);
        }

        public async Task DeleteSectionAsync(int id, CancellationToken ct)
        {
            var s = await _db.SectionModules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Section");
            if (await _db.SystemModules.AnyAsync(m => m.SectionModuleId == id, ct)) throw GatewayException.Conflict("SECTION_NOT_EMPTY", "Move or delete the menu entries in this section first.");
            _db.SectionModules.Remove(s);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<ModuleDto> SaveModuleAsync(ModuleDto dto, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(dto.Name)) throw GatewayException.BadRequest("NAME_REQUIRED", "Name is required.");
            if (!Slug.IsMatch(dto.Slugname ?? "")) throw GatewayException.BadRequest("SLUG_INVALID", "Route must be lowercase letters, digits, - and / (no leading slash).");
            if (!IconName.IsMatch(dto.Icon ?? "")) throw GatewayException.BadRequest("ICON_INVALID", "Icon must be a short lowercase name.");
            if (!await _db.SectionModules.AnyAsync(s => s.Id == dto.SectionModuleId, ct)) throw GatewayException.BadRequest("SECTION_INVALID", "Choose a section.");
            if (await _db.SystemModules.AnyAsync(m => m.Slugname == dto.Slugname && m.Id != dto.Id, ct)) throw GatewayException.Conflict("SLUG_EXISTS", "Another menu entry already uses that route.");
            var m = dto.Id == 0 ? new SystemModule() : await _db.SystemModules.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw GatewayException.NotFound("Menu entry");
            m.SectionModuleId = dto.SectionModuleId; m.Name = dto.Name.Trim(); m.Slugname = dto.Slugname!; m.Icon = dto.Icon!; m.Description = dto.Description;
            m.AccessName = string.IsNullOrWhiteSpace(dto.AccessName) ? null : dto.AccessName.Trim(); m.SortOrder = dto.SortOrder; m.IsActive = dto.IsActive;
            if (dto.Id == 0) _db.SystemModules.Add(m);
            await _db.SaveChangesAsync(ct);
            return new ModuleDto(m.Id, m.SectionModuleId, m.Name, m.Slugname, m.Icon, m.Description, m.AccessName, m.SortOrder, m.IsActive);
        }

        public async Task DeleteModuleAsync(int id, CancellationToken ct)
        {
            var m = await _db.SystemModules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Menu entry");
            if (m.Slugname is "admin/roles" or "admin/modules") throw GatewayException.Conflict("MODULE_PROTECTED", "This page is needed to administer access and cannot be deleted.");
            _db.SystemModules.Remove(m);   // sub-entries and role assignments cascade
            await _db.SaveChangesAsync(ct);
        }

        public async Task<SubmoduleDto> SaveSubmoduleAsync(SubmoduleDto dto, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(dto.Name)) throw GatewayException.BadRequest("NAME_REQUIRED", "Name is required.");
            if (!Slug.IsMatch(dto.Route ?? "")) throw GatewayException.BadRequest("SLUG_INVALID", "Route must be lowercase letters, digits, - and / (no leading slash).");
            if (!IconName.IsMatch(dto.Icon ?? "")) throw GatewayException.BadRequest("ICON_INVALID", "Icon must be a short lowercase name.");
            if (!await _db.SystemModules.AnyAsync(x => x.Id == dto.ModuleId, ct)) throw GatewayException.BadRequest("MODULE_INVALID", "Choose a menu entry.");
            var s = dto.Id == 0 ? new Submodule() : await _db.Submodules.FirstOrDefaultAsync(x => x.Id == dto.Id, ct) ?? throw GatewayException.NotFound("Sub-entry");
            s.ModuleId = dto.ModuleId; s.Name = dto.Name.Trim(); s.Icon = dto.Icon!; s.Route = dto.Route!;
            s.AccessName = string.IsNullOrWhiteSpace(dto.AccessName) ? null : dto.AccessName.Trim(); s.Order = dto.Order; s.IsActive = dto.IsActive;
            if (dto.Id == 0) _db.Submodules.Add(s);
            await _db.SaveChangesAsync(ct);
            return new SubmoduleDto(s.Id, s.ModuleId, s.Name, s.Icon, s.Route, s.AccessName, s.Order, s.IsActive);
        }

        public async Task DeleteSubmoduleAsync(int id, CancellationToken ct)
        {
            var s = await _db.Submodules.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Sub-entry");
            _db.Submodules.Remove(s);
            await _db.SaveChangesAsync(ct);
        }
    }

    // ================================================================================================================
    public sealed class AuditService : IAuditService
    {
        private readonly FdmsDbContext _db;
        public AuditService(FdmsDbContext db) => _db = db;

        public async Task<AuditPageDto> SearchAsync(Guid? callerClientId, AuditSearch f, CancellationToken ct)
        {
            // a company sees only its own trail; the platform operator may narrow to one company
            var scope = callerClientId ?? f.ClientId;
            var q = _db.AuditLogs.AsNoTracking().Where(a => scope == null || a.ClientId == scope);
            if (!string.IsNullOrWhiteSpace(f.Search)) { var s = f.Search.Trim(); q = q.Where(a => a.Action.Contains(s) || (a.Detail != null && a.Detail.Contains(s)) || (a.UserName != null && a.UserName.Contains(s)) || (a.EntityId != null && a.EntityId.Contains(s))); }
            if (!string.IsNullOrWhiteSpace(f.Action)) { var act = f.Action.Trim(); q = q.Where(a => a.Action == act || a.Action.StartsWith(act + ".")); }
            if (!string.IsNullOrWhiteSpace(f.Actor)) { var who = f.Actor.Trim(); q = q.Where(a => a.UserName != null && a.UserName.Contains(who)); }
            if (f.ActorType is "user" or "token" or "system") q = q.Where(a => a.ActorType == f.ActorType);
            if (f.From.HasValue) q = q.Where(a => a.At >= f.From.Value);
            if (f.To.HasValue) q = q.Where(a => a.At < f.To.Value);

            var size = Math.Clamp(f.PageSize, 1, 200);
            var page = Math.Max(1, f.Page);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
            var clients = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var actions = await _db.AuditLogs.AsNoTracking().Where(a => scope == null || a.ClientId == scope).Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(ct);
            return new AuditPageDto(rows.Select(a => new AuditRowDto(a.Id, a.ClientId, a.ClientId.HasValue ? clients.GetValueOrDefault(a.ClientId.Value) : null, a.UserId, a.UserName, a.ActorType,
                a.Action, a.Entity, a.EntityId, a.Detail, a.IpAddress, a.At)).ToList(), page, size, total, actions);
        }

        public async Task<IReadOnlyList<AuditEntryDto>> ListAsync(Guid? callerClientId, string? search, int take, CancellationToken ct)
        {
            var q = _db.AuditLogs.AsNoTracking().Where(a => callerClientId == null || a.ClientId == callerClientId);
            if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); q = q.Where(a => a.Action.Contains(s) || (a.Detail != null && a.Detail.Contains(s)) || (a.UserName != null && a.UserName.Contains(s))); }
            var rows = await q.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
            var clients = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            return rows.Select(a => new AuditEntryDto(a.Id, a.ClientId, a.ClientId.HasValue ? clients.GetValueOrDefault(a.ClientId.Value) : null, a.UserName, a.Action, a.Detail, a.At)).ToList();
        }
    }
}
