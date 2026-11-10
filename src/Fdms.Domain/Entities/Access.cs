using Microsoft.AspNetCore.Identity;

namespace Fdms.Domain.Entities
{
    // Access-control model, same shape as dara ERP: users with roles; roles see menus (RoleMenu) and hold permissions (RolePermission).

    public class ApplicationUser : IdentityUser
    {
        public string Name { get; set; } = "";
        public string Surname { get; set; } = "";
        /// <summary>The company (tenant) the user belongs to. Null for platform operators (SuperAdmin).</summary>
        public Guid? ClientId { get; set; }
        /// <summary>ACTIVE or INACTIVE.</summary>
        public string Status { get; set; } = "ACTIVE";
        public bool MustChangePassword { get; set; }
        public DateTime? LastLoginDate { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>"all" = every device of the company; "restricted" = only the devices of the assigned branches plus the assigned devices.</summary>
        public string AccessScope { get; set; } = "all";
        /// <summary>The person's profile picture (a small PNG or JPEG, at most 100 KB). Shown in the header.</summary>
        public byte[]? Avatar { get; set; }
        public string? AvatarContentType { get; set; }
        /// <summary>Set for the staff of a partner (reseller). They belong to no company and see only the companies the partner brought.</summary>
        public Guid? PartnerId { get; set; }

        // ---- WhatsApp (the FiscalHub bot) ----
        /// <summary>The WhatsApp number this person uses with the bot, digits only with the country code (263771234567). Null = not linked.</summary>
        public string? WhatsAppNumber { get; set; }
        public DateTime? WhatsAppLinkedAt { get; set; }
        /// <summary>Also send this person's alerts by WhatsApp.</summary>
        public bool WhatsAppAlerts { get; set; } = true;
        /// <summary>Also send this person's alerts by SMS to their phone number.</summary>
        public bool SmsAlerts { get; set; }
        /// <summary>The device the bot uses for this person's sales unless they choose another.</summary>
        public Guid? WhatsAppDeviceId { get; set; }
        /// <summary>SHA-256 of the one-time code the person sends to the bot to link their number.</summary>
        public string? WhatsAppLinkCodeHash { get; set; }
        public DateTime? WhatsAppLinkExpiresAt { get; set; }
    }

    /// <summary>A shop / depot / office of a company. Devices (tills) are assigned to a branch; users can be limited to branches.</summary>
    public class Branch
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Address { get; set; }
        public string? Phone { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class UserBranch { public string UserId { get; set; } = ""; public Guid BranchId { get; set; } }
    public class UserDevice { public string UserId { get; set; } = ""; public Guid DeviceId { get; set; } }

    public class Role : IdentityRole
    {
        /// <summary>"system" (seeded, cannot be deleted) or "custom".</summary>
        public string? Type { get; set; }
        /// <summary>"platform" roles are for the operator only; "client" roles can be given to a company's users.</summary>
        public string? Level { get; set; }
        public string? Description { get; set; }
        public ICollection<RoleMenu> RoleMenus { get; set; } = new List<RoleMenu>();
        public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
    }

    public class Permission
    {
        public int Id { get; set; }
        /// <summary>e.g. "receipts.submit" - checked by [HasPermission("receipts.submit")].</summary>
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Module { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
    }

    public class RolePermission
    {
        public int Id { get; set; }
        public string RoleId { get; set; } = "";
        public Role Role { get; set; } = null!;
        public int PermissionId { get; set; }
        public Permission Permission { get; set; } = null!;
    }

    /// <summary>A sidebar group (e.g. "Fiscalisation").</summary>
    public class SectionModule
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Color { get; set; } = "";
        public int SortOrder { get; set; }
        public bool Status { get; set; } = true;
        public ICollection<SystemModule> Children { get; set; } = new List<SystemModule>();
    }

    /// <summary>A sidebar item. <see cref="Slugname"/> is the front-end route; <see cref="AccessName"/> the permission needed to see it.</summary>
    public class SystemModule
    {
        public int Id { get; set; }
        public int SectionModuleId { get; set; }
        public SectionModule? SectionModule { get; set; }
        public string Name { get; set; } = "";
        public string Slugname { get; set; } = "";
        public string Icon { get; set; } = "";
        public string? Description { get; set; }
        public string? AccessName { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public ICollection<Submodule> Children { get; set; } = new List<Submodule>();
        public ICollection<RoleMenu> RoleMenus { get; set; } = new List<RoleMenu>();
    }

    public class Submodule
    {
        public int Id { get; set; }
        public int ModuleId { get; set; }
        public SystemModule? Module { get; set; }
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public string Route { get; set; } = "";
        public string? AccessName { get; set; }
        public int Order { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class RoleMenu
    {
        public int Id { get; set; }
        public string RoleId { get; set; } = "";
        public Role Role { get; set; } = null!;
        public int SystemModuleId { get; set; }
        public SystemModule Menu { get; set; } = null!;
    }

    /// <summary>Only a SHA-256 hash of the refresh token is stored. Tokens rotate on every refresh.</summary>
    public class RefreshToken
    {
        public int Id { get; set; }
        public string UserId { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public DateTime ExpiresAt { get; set; }
        public bool IsRevoked { get; set; }
        /// <summary>password, mfa or passkey: how the person proved who they are when this session started.</summary>
        public string AuthMethod { get; set; } = "password";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>Where and with what the person signed in, so "your sessions" can be shown and the unfamiliar one signed out.</summary>
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
    }
}
