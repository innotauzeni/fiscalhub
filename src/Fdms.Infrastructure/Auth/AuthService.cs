using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Auth
{
    /// <summary>
    /// Login for people (not integrations): identifier + password -> JWT carrying the permissions of the user's roles, a rotating refresh
    /// token, and the menu tree the roles may see. Port of dara's LoginHandler/RefreshTokenHandler.
    /// </summary>
    public sealed class AuthService : IAuthService
    {
        private readonly UserManager<ApplicationUser> _users;
        private readonly RoleManager<Role> _roles;
        private readonly FdmsDbContext _db;
        private readonly JwtService _jwt;
        private readonly ILogger<AuthService> _log;
        private readonly IOnboardingService _onboarding;
        private readonly IBillingService _billing;
        private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _http;
        private readonly SecurityService _security;
        private readonly ITimeLimitedDataProtector _mfaProtector;

        public AuthService(UserManager<ApplicationUser> users, RoleManager<Role> roles, FdmsDbContext db, JwtService jwt, IOnboardingService onboarding, IBillingService billing, ILogger<AuthService> log, Microsoft.AspNetCore.Http.IHttpContextAccessor http, SecurityService security, IDataProtectionProvider dp)
        {
            _users = users; _roles = roles; _db = db; _jwt = jwt; _onboarding = onboarding; _billing = billing; _log = log; _http = http; _security = security;
            _mfaProtector = dp.CreateProtector("fdms.mfa-login").ToTimeLimitedDataProtector();
        }

        private static GatewayException Unauthorized(string code, string msg) => new(401, code, msg);

        private static string Trim(string s) => s.Length <= 80 ? s : s[..80];

        /// <summary>Sign-in events happen before there is an authenticated caller, so the person is named explicitly.</summary>
        private Task Audit(ApplicationUser? user, string action, string detail, CancellationToken ct)
        {
            _db.AuditLogs.Add(new AuditLog
            {
                ClientId = user?.ClientId, UserId = user?.Id, UserName = user?.Email, ActorType = user == null ? "user" : "user", Action = action, Detail = detail,
                Entity = user == null ? null : "User", EntityId = user?.Id,
            });
            return _db.SaveChangesAsync(ct);
        }

        public async Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct)
        {
            var id = req.Identifier?.Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(req.Password))
                throw GatewayException.BadRequest("CREDENTIALS_REQUIRED", "Enter your email (or username) and password.");

            var user = await _users.FindByEmailAsync(id) ?? await _users.FindByNameAsync(id);
            // Same answer for "no such user" and "wrong password" so accounts cannot be probed.
            if (user == null)
            {
                // someone who registered but is not approved yet: tell them where they stand (only when the password they chose matches)
                var app = await _onboarding.FindForSignInAsync(id, req.Password, ct);
                if (app?.Status == "Unverified") throw new GatewayException(403, "EMAIL_NOT_CONFIRMED", "Confirm your email first: enter the 6-digit code we emailed you (or ask for a new one).");
                if (app?.Status == "Pending") throw new GatewayException(403, "APPLICATION_PENDING", "Your registration is waiting for approval. We will email you as soon as it is approved.");
                if (app?.Status == "Rejected") throw new GatewayException(403, "APPLICATION_REJECTED", "Your registration was not approved" + (string.IsNullOrEmpty(app.DecisionReason) ? "." : ": " + app.DecisionReason));
                await Audit(null, "auth.login-failed", $"unknown account \"{Trim(id)}\"", ct); throw Unauthorized("INVALID_CREDENTIALS", "Invalid credentials.");
            }
            if (await _users.IsLockedOutAsync(user)) { await Audit(user, "auth.login-blocked", "account is locked after too many failed attempts", ct); throw Unauthorized("ACCOUNT_LOCKED", "Too many failed attempts. Try again in a few minutes."); }
            if (!await _users.CheckPasswordAsync(user, req.Password))
            {
                await _users.AccessFailedAsync(user);
                _log.LogWarning("Failed login for {User}", user.Email);
                await Audit(user, "auth.login-failed", "wrong password", ct);
                throw Unauthorized("INVALID_CREDENTIALS", "Invalid credentials.");
            }
            await EnsureCanSignInAsync(user, ct);
            if (user.TwoFactorEnabled)
            {
                // the password was right; the account still needs its second step before any session is issued
                var token = _mfaProtector.Protect(user.Id, TimeSpan.FromMinutes(5));
                return new LoginResponse("", "", DateTime.UtcNow, new UserInfoDto("", null, null, "", "", "", "", Array.Empty<string>(), false),
                    Array.Empty<string>(), Array.Empty<SectionNode>(), Array.Empty<MenuNode>(), MfaRequired: true, MfaToken: token);
            }
            await _users.ResetAccessFailedCountAsync(user);

            user.LastLoginDate = DateTime.UtcNow;
            await _users.UpdateAsync(user);
            await Audit(user, "auth.login", "signed in", ct);
            return await IssueAsync(user, "password", ct);
        }

        public async Task<LoginResponse> LoginMfaAsync(MfaLoginRequest req, CancellationToken ct)
        {
            string userId;
            try { userId = _mfaProtector.Unprotect(req.MfaToken ?? ""); }
            catch (Exception) { throw Unauthorized("MFA_EXPIRED", "That sign-in took too long. Enter your password again."); }
            var user = await _users.FindByIdAsync(userId) ?? throw Unauthorized("MFA_EXPIRED", "That sign-in took too long. Enter your password again.");
            if (await _users.IsLockedOutAsync(user)) { await Audit(user, "auth.login-blocked", "account is locked after too many failed attempts", ct); throw Unauthorized("ACCOUNT_LOCKED", "Too many failed attempts. Try again in a few minutes."); }
            var code = (req.Code ?? "").Trim();
            var ok = await _security.VerifyAsync(user, code);
            var usedRecovery = false;
            if (!ok && code.Replace(" ", "").Replace("-", "").Length > 6)
            {
                ok = (await _users.RedeemTwoFactorRecoveryCodeAsync(user, code.Replace(" ", ""))).Succeeded;   // each recovery code works once
                usedRecovery = ok;
            }
            if (!ok)
            {
                await _users.AccessFailedAsync(user);
                await Audit(user, "auth.login-failed", "wrong two-step code", ct);
                throw Unauthorized("INVALID_MFA_CODE", "That code is not right.");
            }
            await _users.ResetAccessFailedCountAsync(user);
            await EnsureCanSignInAsync(user, ct);
            user.LastLoginDate = DateTime.UtcNow;
            await _users.UpdateAsync(user);
            await Audit(user, "auth.login", usedRecovery ? "signed in with a recovery code" : "signed in with a two-step code", ct);
            return await IssueAsync(user, "mfa", ct);
        }

        public async Task<LoginResponse> LoginPasskeyAsync(PasskeyLoginRequest req, PasskeyOrigin where, CancellationToken ct)
        {
            string userId;
            try { userId = await _security.FinishPasskeyLoginAsync(req, where, ct); }
            catch (GatewayException e) { await Audit(null, "auth.login-failed", "passkey not accepted", ct); throw e; }
            var user = await _users.FindByIdAsync(userId) ?? throw Unauthorized("INVALID_PASSKEY", "That passkey was not accepted.");
            if (await _users.IsLockedOutAsync(user)) { await Audit(user, "auth.login-blocked", "account is locked after too many failed attempts", ct); throw Unauthorized("ACCOUNT_LOCKED", "Too many failed attempts. Try again in a few minutes."); }
            await EnsureCanSignInAsync(user, ct);
            user.LastLoginDate = DateTime.UtcNow;
            await _users.UpdateAsync(user);
            await Audit(user, "auth.login", "signed in with a passkey", ct);
            return await IssueAsync(user, "passkey", ct);
        }

        public async Task<LoginResponse> RefreshAsync(string refreshToken, CancellationToken ct)
        {
            var hash = JwtService.Hash(refreshToken ?? "");
            var stored = await _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
            if (stored == null) throw Unauthorized("INVALID_REFRESH", "Your session has expired. Sign in again.");

            if (stored.IsRevoked)
            {
                // A rotated token came back: someone kept a copy. End every session of that user.
                var all = await _db.RefreshTokens.Where(r => r.UserId == stored.UserId && !r.IsRevoked).ToListAsync(ct);
                foreach (var r in all) r.IsRevoked = true;
                await _db.SaveChangesAsync(ct);
                _log.LogWarning("Refresh token reuse detected for user {UserId}; all sessions revoked", stored.UserId);
                throw Unauthorized("INVALID_REFRESH", "Your session has expired. Sign in again.");
            }
            if (stored.ExpiresAt < DateTime.UtcNow) throw Unauthorized("INVALID_REFRESH", "Your session has expired. Sign in again.");

            var user = await _users.FindByIdAsync(stored.UserId) ?? throw Unauthorized("INVALID_REFRESH", "Your session has expired. Sign in again.");
            await EnsureCanSignInAsync(user, ct);
            stored.IsRevoked = true;
            await _db.SaveChangesAsync(ct);
            return await IssueAsync(user, stored.AuthMethod, ct);
        }

        public async Task LogoutAsync(string refreshToken, CancellationToken ct)
        {
            var hash = JwtService.Hash(refreshToken ?? "");
            var stored = await _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash, ct);
            if (stored != null)
            {
                stored.IsRevoked = true;
                var who = await _users.FindByIdAsync(stored.UserId);
                if (who != null) await Audit(who, "auth.logout", "signed out", ct); else await _db.SaveChangesAsync(ct);
            }
        }

        public async Task<LoginResponse> ChangePasswordAsync(string userId, ChangePasswordRequest req, CancellationToken ct)
        {
            var user = await _users.FindByIdAsync(userId) ?? throw GatewayException.NotFound("User");
            var res = await _users.ChangePasswordAsync(user, req.CurrentPassword ?? "", req.NewPassword ?? "");
            if (!res.Succeeded) throw GatewayException.BadRequest("PASSWORD_REJECTED", string.Join(" ", res.Errors.Select(e => e.Description)));
            user.MustChangePassword = false;
            await _users.UpdateAsync(user);
            foreach (var t in await _db.RefreshTokens.Where(r => r.UserId == user.Id && !r.IsRevoked).ToListAsync(ct)) t.IsRevoked = true;   // other devices sign out
            await Audit(user, "auth.password-changed", "changed their own password", ct);
            return await IssueAsync(user, user.TwoFactorEnabled ? "mfa" : "password", ct);
        }

        public async Task<UserInfoDto> MeAsync(string userId, CancellationToken ct)
        {
            var user = await _users.FindByIdAsync(userId) ?? throw GatewayException.NotFound("User");
            return await ToInfoAsync(user, await _users.GetRolesAsync(user), ct);
        }

        // ---- helpers -----------------------------------------------------------------------------------------------

        private async Task EnsureCanSignInAsync(ApplicationUser user, CancellationToken ct)
        {
            if (user.Status != "ACTIVE") throw new GatewayException(403, "ACCOUNT_INACTIVE", "This account is inactive. Contact your administrator.");
            if (user.ClientId.HasValue)
            {
                var active = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == user.ClientId && c.IsActive, ct);
                if (!active) throw new GatewayException(403, "CLIENT_SUSPENDED", "Your organisation is suspended. Contact the platform operator.");
            }
        }

        private async Task<UserInfoDto> ToInfoAsync(ApplicationUser user, IList<string> roleNames, CancellationToken ct)
        {
            string? clientName = user.ClientId.HasValue ? await _db.Clients.AsNoTracking().Where(c => c.Id == user.ClientId).Select(c => c.Name).FirstOrDefaultAsync(ct)
                : user.PartnerId.HasValue ? await _db.Partners.AsNoTracking().Where(p => p.Id == user.PartnerId).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
            return new UserInfoDto(user.Id, user.ClientId, clientName, user.Email ?? "", user.UserName ?? "", user.Name, user.Surname, roleNames.ToList(), user.MustChangePassword);
        }

        /// <param name="method">How this session was opened: password, mfa (password + code) or passkey. It is kept on the refresh token so a company that requires extra protection can tell.</param>
        /// <summary>The menus a company on the Zoho connector plan keeps.</summary>
        public static readonly IReadOnlySet<string> ConnectorSlugs = new HashSet<string> { "integrations", "connectors", "messaging", "activity", "docs", "guide", "billing", "admin/users", "alerts", "referrals", "approvals" };

        /// <summary>The menus a company on an accounting-only plan keeps (besides every accounting page), with the financial dashboard first.</summary>
        public static readonly IReadOnlySet<string> AccountingPlanSlugs = new HashSet<string> { "customers", "products", "quotes", "activity", "guide", "billing", "admin/users", "admin/branches", "admin/banks", "alerts", "referrals", "approvals" };

        private async Task<LoginResponse> IssueAsync(ApplicationUser user, string method, CancellationToken ct)
        {
            var roleNames = await _users.GetRolesAsync(user);
            var roleIds = await _roles.Roles.Where(r => roleNames.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);

            var permissions = await _db.RolePermissions.AsNoTracking()
                .Where(rp => roleIds.Contains(rp.RoleId) && rp.Permission.IsActive)
                .Select(rp => rp.Permission.Name).Distinct().OrderBy(n => n).ToListAsync(ct);
            var (sections, menus) = await BuildMenusAsync(roleIds, permissions.ToHashSet(), ct);
            var access = user.ClientId.HasValue ? await _billing.GetAccessAsync(user.ClientId.Value, ct) : new AccessStateDto(false, false);
            // a company on the Zoho connector plan sees only what the connector needs, with the connector first
            if (user.ClientId.HasValue && await (from s in _db.Subscriptions.AsNoTracking() join p in _db.Plans.AsNoTracking() on s.PlanId equals p.Id where s.ClientId == user.ClientId && p.Kind == "connector" select p.Id).AnyAsync(ct))
            {
                var keep = ConnectorSlugs;
                menus = menus.Where(m => keep.Contains(m.Slugname)).OrderBy(m => m.Slugname == "integrations" ? 0 : 1).ToList();
                sections = sections.Select(s => s with { Children = s.Children.Where(m => keep.Contains(m.Slugname)).ToList() }).Where(s => s.Children.Count > 0)
                    .OrderBy(s => s.Children.Any(m => m.Slugname == "integrations") ? 0 : 1).ToList();
            }
            // a company on an accounting-only plan keeps the books and what they need; invoices from the books are only for such companies
            var kind = user.ClientId.HasValue ? await Billing.PlanModuleAccess.KindAsync(_db, user.ClientId.Value, ct) : PlanKinds.Fiscal;
            if (kind == PlanKinds.Accounting)
            {
                bool Keep(string slug) => slug.StartsWith("accounting/") || slug.StartsWith("inventory/") || AccountingPlanSlugs.Contains(slug);
                int Order(string slug) => slug == "accounting/dashboard" ? 0 : slug == "accounting/sales" ? 1 : slug.StartsWith("accounting/") ? 2 : 3;
                menus = menus.Where(m => Keep(m.Slugname)).OrderBy(m => Order(m.Slugname)).ToList();
                sections = sections.Select(s => s with { Name = s.Name == "Fiscalisation" ? "Customers and products" : s.Name, Children = s.Children.Where(m => Keep(m.Slugname)).OrderBy(m => Order(m.Slugname)).ToList() })
                    .Where(s => s.Children.Count > 0).OrderBy(s => s.Children.Any(m => m.Slugname.StartsWith("accounting/")) ? 0 : 1).ToList();
            }
            else
            {
                menus = menus.Where(m => m.Slugname != "accounting/sales").ToList();
                sections = sections.Select(s => s with { Children = s.Children.Where(m => m.Slugname != "accounting/sales").ToList() }).Where(s => s.Children.Count > 0).ToList();
            }
            // the optional modules the company's plan leaves out are not shown (the API refuses them too)
            if (user.ClientId.HasValue && (await Billing.PlanModuleAccess.ForClientAsync(_db, user.ClientId.Value, ct)).Modules is { } included)
            {
                bool Keep(string slug) => PlanModules.ForSlug(slug) is not { } m || included.Contains(m.Key);
                menus = menus.Where(m => Keep(m.Slugname)).ToList();
                sections = sections.Select(s => s with { Children = s.Children.Where(m => Keep(m.Slugname)).ToList() }).Where(s => s.Children.Count > 0).ToList();
            }
            if (access.Locked)   // nothing but Billing until the first invoice is settled
            {
                menus = menus.Where(m => m.Slugname == "billing").ToList();
                sections = sections.Select(s => s with { Children = s.Children.Where(m => m.Slugname == "billing").ToList() }).Where(s => s.Children.Count > 0).ToList();
            }

            var expires = _jwt.AccessExpiry();
            // a company can require everyone to use the authenticator app or a passkey: until they have, the session may only reach their own security settings
            var setupRequired = user.ClientId.HasValue && !(user.TwoFactorEnabled || method == "passkey") && await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == user.ClientId && c.RequireMfa, ct);
            var accessToken = _jwt.CreateAccessToken(user, roleNames, permissions, expires, setupRequired);
            var refresh = JwtService.NewRefreshToken();
            var ctx = _http.HttpContext;
            var ua = ctx?.Request.Headers.UserAgent.ToString();
            _db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, AuthMethod = method, TokenHash = JwtService.Hash(refresh), ExpiresAt = _jwt.RefreshExpiry(),
                IpAddress = ctx?.Connection.RemoteIpAddress?.MapToIPv4().ToString(), UserAgent = ua is { Length: > 200 } ? ua[..200] : ua });
            await _db.SaveChangesAsync(ct);

            return new LoginResponse(accessToken, refresh, expires, await ToInfoAsync(user, roleNames, ct), permissions, sections, menus, access.Locked, access.DemoOnly, MfaSetupRequired: setupRequired);
        }

        /// <summary>
        /// RoleMenu says which modules a role sees; AccessName on a module/submodule additionally requires that permission, so a menu entry
        /// never leads to a page the user cannot use.
        /// </summary>
        private async Task<(List<SectionNode>, List<MenuNode>)> BuildMenusAsync(List<string> roleIds, HashSet<string> perms, CancellationToken ct)
        {
            var moduleIds = await _db.RoleMenus.AsNoTracking().Where(rm => roleIds.Contains(rm.RoleId)).Select(rm => rm.SystemModuleId).Distinct().ToListAsync(ct);
            var modules = await _db.SystemModules.AsNoTracking().Include(m => m.Children).Include(m => m.SectionModule)
                .Where(m => moduleIds.Contains(m.Id) && m.IsActive && m.SectionModule!.Status).ToListAsync(ct);

            bool Allowed(string? access) => string.IsNullOrEmpty(access) || perms.Contains(access);

            var nodes = modules.Where(m => Allowed(m.AccessName))
                .OrderBy(m => m.SectionModule!.SortOrder).ThenBy(m => m.SortOrder)
                .Select(m => new MenuNode(m.Id, m.SectionModuleId, m.Name, m.Slugname, m.Icon, m.AccessName, m.SortOrder,
                    m.Children.Where(s => s.IsActive && Allowed(s.AccessName)).OrderBy(s => s.Order)
                        .Select(s => new SubmoduleNode(s.Id, s.Name, s.Icon, s.Route, s.AccessName, s.Order)).ToList()))
                .ToList();

            // group by id: AsNoTracking gives every module its own SectionModule instance, so grouping by the object would never merge
            var sections = modules.Where(m => Allowed(m.AccessName)).GroupBy(m => m.SectionModuleId)
                .Select(g => g.First().SectionModule!)
                .OrderBy(sec => sec.SortOrder)
                .Select(sec => new SectionNode(sec.Id, sec.Name, sec.Color, sec.SortOrder, nodes.Where(n => n.SectionModuleId == sec.Id).ToList()))
                .ToList();
            return (sections, nodes);
        }
    }
}
