namespace Fdms.Application.Abstractions
{
    /// <summary>Who is calling (set from the JWT or API token). Background jobs get <see cref="NullCallerScope"/>.</summary>
    public interface ICallerScope
    {
        string? UserId { get; }
        string? UserName { get; }
        /// <summary>Null = every device of the company. Otherwise the ONLY devices the caller may touch (an API token: its one device; a branch-limited user: their branches' and assigned devices).</summary>
        IReadOnlyList<Guid>? AllowedDeviceIds { get; }
        Guid? ApiKeyId { get; }
        /// <summary>Where the request came from (recorded in the audit trail).</summary>
        string? IpAddress { get; }
        /// <summary>user, token or system.</summary>
        string ActorType { get; }
    }

    /// <summary>
    /// A person acting outside an HTTP request (for example through the WhatsApp bot). While set, the caller scope reports this person, so audit rows name them and
    /// their device limits apply exactly as in the console.
    /// </summary>
    public sealed record AmbientCallerInfo(string UserId, string? UserName, IReadOnlyList<Guid>? AllowedDeviceIds, string Channel);

    public static class AmbientCaller
    {
        private static readonly AsyncLocal<AmbientCallerInfo?> _current = new();
        public static AmbientCallerInfo? Current => _current.Value;
        /// <summary>Acts as this person until the returned handle is disposed.</summary>
        public static IDisposable Use(AmbientCallerInfo who) { var before = _current.Value; _current.Value = who; return new Restore(before); }
        private sealed class Restore : IDisposable { private readonly AmbientCallerInfo? _b; public Restore(AmbientCallerInfo? b) => _b = b; public void Dispose() => _current.Value = _b; }
    }

    public sealed class NullCallerScope : ICallerScope
    {
        public string? UserId => AmbientCaller.Current?.UserId;
        public string? UserName => AmbientCaller.Current?.UserName;
        public IReadOnlyList<Guid>? AllowedDeviceIds => AmbientCaller.Current?.AllowedDeviceIds;
        public Guid? ApiKeyId => null;
        public string? IpAddress => AmbientCaller.Current?.Channel;
        public string ActorType => AmbientCaller.Current != null ? "user" : "system";
    }

    // ---- login ---------------------------------------------------------------------------------------------------

    public sealed record LoginRequest(string Identifier, string Password);
    public sealed record RefreshRequest(string RefreshToken);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public sealed record UserInfoDto(string Id, Guid? ClientId, string? ClientName, string Email, string UserName, string Name, string Surname,
        IReadOnlyList<string> Roles, bool MustChangePassword);

    public sealed record SubmoduleNode(int Id, string Name, string Icon, string Route, string? AccessName, int Order);
    public sealed record MenuNode(int Id, int SectionModuleId, string Name, string Slugname, string Icon, string? AccessName, int SortOrder, IReadOnlyList<SubmoduleNode> Children);
    public sealed record SectionNode(int Id, string Name, string Color, int SortOrder, IReadOnlyList<MenuNode> Children);

    /// <summary>Same shape as dara's login response: tokens, user, permissions and the menu tree the user's roles may see.</summary>
    public sealed record LoginResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserInfoDto User,
        IReadOnlyList<string> Permissions, IReadOnlyList<SectionNode> SectionMenus, IReadOnlyList<MenuNode> Menus,
        bool BillingLocked = false, bool DemoOnly = false, bool MfaRequired = false, string? MfaToken = null, bool MfaSetupRequired = false);

    public interface IAuthService
    {
        Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct);
        Task<LoginResponse> RefreshAsync(string refreshToken, CancellationToken ct);
        Task LogoutAsync(string refreshToken, CancellationToken ct);
        Task<LoginResponse> ChangePasswordAsync(string userId, ChangePasswordRequest req, CancellationToken ct);
        Task<UserInfoDto> MeAsync(string userId, CancellationToken ct);
        /// <summary>Second step of a sign-in for someone with MFA: the 6-digit code (or a recovery code) plus the token from the first step.</summary>
        Task<LoginResponse> LoginMfaAsync(MfaLoginRequest req, CancellationToken ct);
        Task<LoginResponse> LoginPasskeyAsync(PasskeyLoginRequest req, PasskeyOrigin where, CancellationToken ct);
    }

    // ---- forgotten password ----------------------------------------------------------------------------------------

    public sealed record ForgotPasswordRequest(string Email);
    public sealed record ResetPasswordWithCodeRequest(string Email, string Code, string NewPassword);
    public sealed record PasswordResetAnswer(string Message);

    /// <summary>
    /// A forgotten password is reset with a 6-digit code emailed to the person (valid a few minutes, single use), so it works the same in the
    /// app and on the web without opening a link. Nothing is stored: the code comes from the account's security stamp.
    /// </summary>
    public interface IPasswordResetService
    {
        /// <summary>Always answers the same, whether or not the email belongs to an account, so accounts cannot be probed.</summary>
        Task<PasswordResetAnswer> RequestAsync(ForgotPasswordRequest req, CancellationToken ct);
        Task<PasswordResetAnswer> ResetAsync(ResetPasswordWithCodeRequest req, CancellationToken ct);
    }

    // ---- clients -------------------------------------------------------------------------------------------------

    public sealed record ClientDto(Guid Id, string Name, string? Email, bool IsActive, int UserCount, int DeviceCount, DateTime CreatedAt);

    public sealed record CreateClientRequest(string Name, string? Email, string AdminEmail, string AdminName, string? AdminSurname, string AdminPassword);

    public interface IClientAdminService
    {
        Task<IReadOnlyList<ClientDto>> ListAsync(CancellationToken ct);
        /// <summary>Creates the company and its first ClientAdmin user (who must change the password at first login).</summary>
        Task<ClientDto> CreateAsync(CreateClientRequest req, CancellationToken ct);
        Task<ClientDto> SetActiveAsync(Guid clientId, bool active, CancellationToken ct);
    }

    // ---- users ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a user name may be: 3 to 32 letters, digits, dots, dashes or underscores, starting with a letter or digit ("Inno", "inno.t"). No @, so a user name
    /// can never be mistaken for somebody else's email address when signing in.
    /// </summary>
    public static class UserNames
    {
        private static readonly System.Text.RegularExpressions.Regex Rx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{2,31}$", System.Text.RegularExpressions.RegexOptions.Compiled);
        /// <summary>The trimmed name, or null when none was given. Throws when it breaks the rules.</summary>
        public static string? Clean(string? userName)
        {
            var u = userName?.Trim();
            if (string.IsNullOrEmpty(u)) return null;
            if (!Rx.IsMatch(u)) throw GatewayException.BadRequest("USERNAME_INVALID", "A user name is 3 to 32 letters or digits (dots, dashes and underscores are allowed too), with no spaces and no @.");
            return u;
        }
    }

    public sealed record UserDto(string Id, string Email, string UserName, string Name, string Surname, Guid? ClientId, string? ClientName,
        IReadOnlyList<string> Roles, string Status, DateTime? LastLoginDate, bool MustChangePassword, string AccessScope = "all");

    public sealed record CreateUserRequest(string Email, string? UserName, string Name, string Surname, string Role, string Password, Guid? ClientId);
    /// <summary>Email and UserName: null = unchanged.</summary>
    public sealed record UpdateUserRequest(string Name, string Surname, string Role, string Status, string? Email = null, string? UserName = null);
    public sealed record ResetPasswordRequest(string NewPassword);

    public interface IUserAdminService
    {
        /// <param name="callerClientId">Null = platform operator (sees every client's users).</param>
        Task<IReadOnlyList<UserDto>> ListAsync(Guid? callerClientId, CancellationToken ct);
        Task<UserDto> CreateAsync(Guid? callerClientId, CreateUserRequest req, CancellationToken ct);
        Task<UserDto> UpdateAsync(Guid? callerClientId, string userId, UpdateUserRequest req, CancellationToken ct);
        Task ResetPasswordAsync(Guid? callerClientId, string userId, string newPassword, CancellationToken ct);
        /// <summary>Roles a caller may hand out (platform roles only for the operator).</summary>
        Task<IReadOnlyList<string>> AssignableRolesAsync(Guid? callerClientId, CancellationToken ct);
    }

    // ---- branches and who may use which devices -----------------------------------------------------------------

    public sealed record BranchDto(Guid Id, string Code, string Name, string? Address, string? Phone, bool IsActive, IReadOnlyList<Guid> DeviceIds, int UserCount);
    public sealed record SaveBranchRequest(Guid? Id, string Code, string Name, string? Address, string? Phone, bool IsActive);
    public sealed record GuidListRequest(IReadOnlyList<Guid> Ids);
    /// <summary>Scope is "all" or "restricted". EffectiveDeviceIds = what the user can actually reach (branch devices + assigned devices).</summary>
    public sealed record UserAccessDto(string Scope, IReadOnlyList<Guid> BranchIds, IReadOnlyList<Guid> DeviceIds, IReadOnlyList<Guid> EffectiveDeviceIds);
    public sealed record SetUserAccessRequest(string Scope, IReadOnlyList<Guid>? BranchIds, IReadOnlyList<Guid>? DeviceIds);

    public interface IBranchService
    {
        Task<IReadOnlyList<BranchDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<BranchDto> SaveAsync(Guid clientId, SaveBranchRequest req, CancellationToken ct);
        Task DeleteAsync(Guid clientId, Guid branchId, CancellationToken ct);
        /// <summary>Makes exactly these devices the branch's devices (devices not listed leave the branch).</summary>
        Task<BranchDto> SetDevicesAsync(Guid clientId, Guid branchId, IReadOnlyList<Guid> deviceIds, CancellationToken ct);
        Task<UserAccessDto> GetUserAccessAsync(Guid? callerClientId, string userId, CancellationToken ct);
        Task<UserAccessDto> SetUserAccessAsync(Guid? callerClientId, string userId, SetUserAccessRequest req, CancellationToken ct);
    }

    // ---- roles, permissions, menus ---------------------------------------------------------------------------------

    public sealed record RoleDto(string Id, string Name, string? Type, string? Level, string? Description, int PermissionCount, int MenuCount, int UserCount);
    public sealed record PermissionDto(int Id, string Name, string DisplayName, string Module);
    public sealed record RoleAccessDto(string RoleId, string RoleName, IReadOnlyList<int> PermissionIds, IReadOnlyList<int> ModuleIds);
    public sealed record CreateRoleRequest(string Name, string Level, string? Description);
    public sealed record IdListRequest(IReadOnlyList<int> Ids);
    public sealed record UpdateRoleRequest(string Name, string Level, string? Description);
    public sealed record SavePermissionRequest(int Id, string Name, string DisplayName, string Module);
    public sealed record BatchPermissionItem(string Action, string? DisplayName);
    /// <summary>Creates prefix.action for every item (dara's "batch add").</summary>
    public sealed record BatchPermissionRequest(string Module, string Prefix, IReadOnlyList<BatchPermissionItem> Items);
    public sealed record BatchPermissionResult(int Created, IReadOnlyList<string> Skipped, IReadOnlyList<PermissionDto> Permissions);

    public interface IRoleAdminService
    {
        Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct);
        Task<RoleDto> CreateRoleAsync(CreateRoleRequest req, CancellationToken ct);
        Task<RoleDto> UpdateRoleAsync(string roleId, UpdateRoleRequest req, CancellationToken ct);
        Task DeleteRoleAsync(string roleId, CancellationToken ct);
        Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct);
        Task<RoleAccessDto> GetAccessAsync(string roleId, CancellationToken ct);
        Task AssignPermissionsAsync(string roleId, IReadOnlyList<int> permissionIds, CancellationToken ct);
        Task AssignMenusAsync(string roleId, IReadOnlyList<int> moduleIds, CancellationToken ct);
    }

    public sealed record SectionDto(int Id, string Name, string Color, int SortOrder, bool Status);
    public sealed record ModuleDto(int Id, int SectionModuleId, string Name, string Slugname, string Icon, string? Description, string? AccessName, int SortOrder, bool IsActive);
    public sealed record SubmoduleDto(int Id, int ModuleId, string Name, string Icon, string Route, string? AccessName, int Order, bool IsActive);
    public sealed record ModuleTreeDto(IReadOnlyList<SectionDto> Sections, IReadOnlyList<ModuleDto> Modules, IReadOnlyList<SubmoduleDto> Submodules);

    public interface IModuleAdminService
    {
        Task<ModuleTreeDto> TreeAsync(CancellationToken ct);
        Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct);
        Task<PermissionDto> SavePermissionAsync(SavePermissionRequest req, CancellationToken ct);
        Task DeletePermissionAsync(int id, CancellationToken ct);
        Task<BatchPermissionResult> BatchPermissionsAsync(BatchPermissionRequest req, CancellationToken ct);
        Task<SectionDto> SaveSectionAsync(SectionDto dto, CancellationToken ct);
        Task DeleteSectionAsync(int id, CancellationToken ct);
        Task<ModuleDto> SaveModuleAsync(ModuleDto dto, CancellationToken ct);
        Task DeleteModuleAsync(int id, CancellationToken ct);
        Task<SubmoduleDto> SaveSubmoduleAsync(SubmoduleDto dto, CancellationToken ct);
        Task DeleteSubmoduleAsync(int id, CancellationToken ct);
    }

    // ---- audit -----------------------------------------------------------------------------------------------------

    public sealed record AuditEntryDto(long Id, Guid? ClientId, string? ClientName, string? UserName, string Action, string? Detail, DateTime At);

    public sealed record AuditRowDto(long Id, Guid? ClientId, string? ClientName, string? UserId, string? UserName, string ActorType, string Action, string? Entity, string? EntityId,
        string? Detail, string? IpAddress, DateTime At);
    public sealed record AuditPageDto(IReadOnlyList<AuditRowDto> Items, int Page, int PageSize, int Total, IReadOnlyList<string> Actions);
    /// <summary>Actor matches the user's name or email; ActorType is user, token or system; From/To are UTC.</summary>
    public sealed record AuditSearch(string? Search, string? Action, string? Actor, Guid? ClientId, string? ActorType, DateTime? From, DateTime? To, int Page = 1, int PageSize = 25);

    // ---- platform settings -----------------------------------------------------------------------------------------

    /// <summary>Source says where EffectiveApiUrl comes from: database (set in the console), config (Hosting:PublicUrl) or request (the address this call arrived on).</summary>
    public sealed record PlatformSettingsDto(string? PublicApiUrl, string EffectiveApiUrl, string Source, string SwaggerUrl, string OpenApiUrl, string GuideUrl, string? PublicAppUrl = null, string EffectiveAppUrl = "");
    /// <summary>PublicAppUrl is the address of the web console (where customers return to after paying, and links in emails), e.g. http://localhost:4200/app in development.</summary>
    public sealed record SavePlatformSettingsRequest(string? PublicApiUrl, string? PublicAppUrl = null);

    public interface ISettingsService
    {
        Task<PlatformSettingsDto> GetAsync(string requestBase, CancellationToken ct);
        Task<PlatformSettingsDto> SaveAsync(string requestBase, SavePlatformSettingsRequest req, CancellationToken ct);
        /// <summary>The address to show developers: the stored setting, else Hosting:PublicUrl, else the address the request arrived on.</summary>
        Task<string> EffectiveApiUrlAsync(string requestBase, CancellationToken ct);
        /// <summary>Where the web console lives: the stored setting, else Hosting:AppUrl, else the API address + /app.</summary>
        Task<string> EffectiveAppUrlAsync(string requestBase, CancellationToken ct);
        /// <summary>Stores the address when nothing is stored yet (first start); never overwrites a choice.</summary>
        Task<bool> EnsureDefaultAsync(string candidate, CancellationToken ct);
    }

    // ---- system (error) logs ---------------------------------------------------------------------------------------

    public sealed record ErrorLogDto(long Id, string Uuid, string Severity, string? Source, string Message, string? ExceptionType, string? StackTrace, string? Path, string? Method,
        int? StatusCode, string? IpAddress, string? UserName, Guid? ClientId, string? ClientName, string? TraceId, string? MachineName, string? Environment, int Occurrences,
        bool IsResolved, string? Resolution, string? ResolvedBy, DateTime? ResolvedAt, DateTime CreatedAt, DateTime LastSeenAt);
    public sealed record ErrorLogStats(int Unresolved, int CriticalUnresolved, int Today, int Total);
    public sealed record ErrorLogPageDto(IReadOnlyList<ErrorLogDto> Items, int Page, int PageSize, int Total, ErrorLogStats Stats);
    public sealed record ErrorLogSearch(string? Search, string? Severity, bool? Resolved, DateTime? From, DateTime? To, int Page = 1, int PageSize = 25);
    public sealed record ResolveErrorRequest(string? Resolution);

    public interface IErrorLogService
    {
        Task<ErrorLogPageDto> SearchAsync(ErrorLogSearch q, CancellationToken ct);
        Task<ErrorLogDto> GetAsync(long id, CancellationToken ct);
        Task<ErrorLogDto> ResolveAsync(long id, string? resolution, CancellationToken ct);
        Task<int> ResolveAllAsync(CancellationToken ct);
        Task DeleteAsync(long id, CancellationToken ct);
        /// <summary>Deletes every resolved entry; returns how many.</summary>
        Task<int> PurgeResolvedAsync(CancellationToken ct);
    }

    public interface IAuditService
    {
        Task<AuditPageDto> SearchAsync(Guid? callerClientId, AuditSearch q, CancellationToken ct);
        /// <param name="callerClientId">Null = platform operator (all clients).</param>
        Task<IReadOnlyList<AuditEntryDto>> ListAsync(Guid? callerClientId, string? search, int take, CancellationToken ct);
    }

    // ---- per-device API tokens -----------------------------------------------------------------------------------------

    public sealed record ApiTokenDto(Guid Id, Guid DeviceId, int ZimraDeviceId, string Name, string Prefix, IReadOnlyList<string> Scopes,
        DateTime CreatedAt, DateTime? ExpiresAt, DateTime? RevokedAt, DateTime? LastUsedAt, string? CreatedBy);

    public sealed record CreateTokenRequest(string Name, IReadOnlyList<string> Scopes, DateTime? ExpiresAt);

    /// <summary>The secret is returned ONCE, at creation.</summary>
    public sealed record CreatedApiToken(ApiTokenDto Token, string Secret);

    public sealed record ScopeDto(string Name, string DisplayName);

    /// <summary>A validated API token: which company, which single device, which permissions.</summary>
    public sealed record TokenPrincipal(Guid ClientId, Guid KeyId, Guid DeviceId, IReadOnlyList<string> Scopes, string Name = "");

    public interface IApiTokenService
    {
        Task<CreatedApiToken> CreateAsync(Guid clientId, Guid deviceId, CreateTokenRequest req, string? userId, CancellationToken ct);
        Task<IReadOnlyList<ApiTokenDto>> ListAsync(Guid clientId, Guid? deviceId, CancellationToken ct);
        Task RevokeAsync(Guid clientId, Guid tokenId, CancellationToken ct);
        IReadOnlyList<ScopeDto> Scopes();
        Task<TokenPrincipal?> ValidateAsync(string token, CancellationToken ct);
    }

    // ---- dashboard -------------------------------------------------------------------------------------------------------

    public sealed record DeviceSummary(Guid Id, int ZimraDeviceId, string? TaxPayerName, string? BranchName, string OperatingMode, string DayStatus,
        int? DayNo, int LastReceiptCounter, DateTime? CertificateValidTill,
        /// <summary>When the open fiscal day was opened (Zimbabwe time), the code ZIMRA gave when a close failed, the day's maximum length in hours, and receipts that are not yet with ZIMRA.</summary>
        DateTime? DayOpenedAt = null, string? ClosingErrorCode = null, int DayMaxHrs = 24, int PendingReceipts = 0, int HeldReceipts = 0, int OfflineWaiting = 0, string? Name = null);

    public sealed record CurrencyTotal(string Currency, decimal Total, int Count);

    /// <summary>One day of the last-week chart: receipts issued (any status that reached a number) and how many were rejected.</summary>
    public sealed record TrendPoint(string Date, int Count, int Rejected);

    public sealed record DashboardDto(int Devices, int OpenDays, int ReceiptsToday, IReadOnlyList<CurrencyTotal> TodayTotals, int Pending, int Held,
        int OfflineIssued, int Rejected, IReadOnlyList<DeviceSummary> DeviceList, IReadOnlyList<ReceiptDto> Recent, IReadOnlyList<string> Alerts,
        IReadOnlyList<TrendPoint> Trend, int Submitted, string? AutoCloseTime = null, ChecklistDto? Checklist = null, UsageMeterDto? Usage = null);

    public sealed record TopClient(Guid Id, string Name, int Devices, int ReceiptsWeek);

    /// <summary>Platform operator overview across every company.</summary>
    public sealed record PlatformDashboardDto(int Clients, int ActiveClients, int Users, int Devices, int OpenDays, int ReceiptsToday, int Rejected, int Pending,
        IReadOnlyList<TrendPoint> Trend, IReadOnlyList<TopClient> TopClients, IReadOnlyList<AuditEntryDto> RecentActivity, IReadOnlyList<string> Alerts,
        IReadOnlyList<AttentionItemDto>? Attention = null);

    public interface IDashboardService
    {
        Task<DashboardDto> GetAsync(Guid clientId, CancellationToken ct);
        Task<PlatformDashboardDto> GetPlatformAsync(CancellationToken ct);
    }
}
