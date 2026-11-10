using Microsoft.AspNetCore.Authorization;

namespace Fdms.Application.Security
{
    /// <summary>
    /// [HasPermission("receipts.submit")] on a controller or action. The name must match Permission.Name in the database; it is checked
    /// against the "permission" claims of the caller (a user's role permissions, or an API token's scopes). Same idea as dara ERP.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class HasPermissionAttribute : AuthorizeAttribute
    {
        public const string PolicyPrefix = "Permission:";

        public HasPermissionAttribute(string permission) : base(PolicyPrefix + permission) => Permission = permission;

        public string Permission { get; }
    }
}
