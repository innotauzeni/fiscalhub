using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Fdms.Infrastructure.Auth
{
    /// <summary>
    /// Changing how a person signs in: their email and their user name (both are unique). A person who never chose a user name signs in with the email, so their
    /// user name IS the email; when that email changes, the user name moves with it.
    /// </summary>
    public static class IdentityNames
    {
        private static readonly Regex EmailRx = new(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$", RegexOptions.Compiled);

        public static string CleanEmail(string? email)
        {
            var e = email?.Trim() ?? "";
            if (e.Length is 0 or > 200 || !EmailRx.IsMatch(e)) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter a valid email address.");
            return e;
        }

        /// <summary>Null = leave as it is. An empty user name means "sign in with the email".</summary>
        public static async Task ApplyAsync(UserManager<ApplicationUser> users, ApplicationUser user, string? email, string? userName)
        {
            var usedEmailAsName = string.Equals(user.UserName, user.Email, StringComparison.OrdinalIgnoreCase);
            if (email != null)
            {
                var e = CleanEmail(email);
                if (!string.Equals(e, user.Email, StringComparison.OrdinalIgnoreCase))
                {
                    var other = await users.FindByEmailAsync(e);
                    if (other != null && other.Id != user.Id) throw GatewayException.Conflict("EMAIL_TAKEN", "Another account already uses that email address.");
                    Check(await users.SetEmailAsync(user, e));
                    user.EmailConfirmed = true;
                    if (usedEmailAsName && userName == null) Check(await users.SetUserNameAsync(user, e));
                }
            }
            if (userName != null)
            {
                var u = UserNames.Clean(userName) ?? user.Email ?? "";
                if (!string.Equals(u, user.UserName, StringComparison.Ordinal))
                {
                    var other = await users.FindByNameAsync(u);
                    if (other != null && other.Id != user.Id) throw GatewayException.Conflict("USERNAME_TAKEN", $"The user name \"{u}\" is already taken.");
                    Check(await users.SetUserNameAsync(user, u));
                }
            }
        }

        private static void Check(IdentityResult r)
        {
            if (!r.Succeeded) throw GatewayException.BadRequest("UPDATE_FAILED", string.Join(" ", r.Errors.Select(e => e.Description)));
        }
    }
}
