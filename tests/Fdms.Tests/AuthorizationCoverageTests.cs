using System.Reflection;
using Fdms.Api.Controllers;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Fdms.Tests
{
    /// <summary>A forgotten attribute is a security hole, so every endpoint must state its rule; this fails the build when one does not.</summary>
    public class AuthorizationCoverageTests
    {
        private static readonly HashSet<string> JwtOnly = new() { "AuthController.ChangePassword", "AuthController.Me", "SecurityController.Status", "SecurityController.BeginMfa", "SecurityController.EnableMfa", "SecurityController.DisableMfa", "SecurityController.RecoveryCodes", "SecurityController.PasskeyBegin", "SecurityController.PasskeyFinish", "SecurityController.PasskeyRename", "SecurityController.PasskeyDelete", "ProfileController.Get", "ProfileController.Update", "ProfileController.UserName", "ProfileController.Email", "ProfileController.ConfirmEmail", "ProfileController.Avatar", "ProfileController.SetAvatar", "ProfileController.RemoveAvatar", "ProfileController.Sessions", "ProfileController.RevokeOthers", "ProfileController.RevokeSession", "WhatsAppProfileController.Get", "WhatsAppProfileController.Link", "WhatsAppProfileController.Prefs", "WhatsAppProfileController.Unlink", "SmsProfileController.Get", "SmsProfileController.Save" };

        private static IEnumerable<(Type Controller, MethodInfo Action)> Actions() =>
            typeof(DevicesController).Assembly.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.GetCustomAttributes().Any(a => a is HttpMethodAttribute)).Select(m => (t, m)));

        [Fact]
        public void Every_endpoint_is_either_explicitly_anonymous_or_requires_a_named_permission()
        {
            var problems = new List<string>();
            foreach (var (c, m) in Actions())
            {
                var name = c.Name + "." + m.Name;
                var anonymous = m.GetCustomAttribute<AllowAnonymousAttribute>() != null || c.GetCustomAttribute<AllowAnonymousAttribute>() != null;
                var perms = m.GetCustomAttributes<HasPermissionAttribute>().Concat(c.GetCustomAttributes<HasPermissionAttribute>()).ToList();
                var authorized = m.GetCustomAttributes<AuthorizeAttribute>().Concat(c.GetCustomAttributes<AuthorizeAttribute>()).Any();

                // the only anonymous endpoints: sign-in, the public integration guide (documentation, no data), and Zoho's sign-in return and workflow webhook (a signed state / a secret key), the customer-facing invoice link and sign-in branding (a signed, expiring token for ONE invoice; a company name, colour and logo)
                if (anonymous) { if (c.Name is not ("AuthController" or "PublicController" or "PaymentCallbacksController" or "PublicShareController" or "AssistantController" or "ZohoCallbackController" or "ConnectorCallbackController" or "WhatsAppWebhookController" or "GuideController") && name != "DocsController.Integration" && name != "LegalController.Get" && name != "LegalController.AppLinks") problems.Add(name + " is anonymous but is not part of sign-in or the public docs"); continue; }
                if (!authorized) problems.Add(name + " has no [Authorize]");
                else if (perms.Count == 0 && !JwtOnly.Contains(name)) problems.Add(name + " has no [HasPermission]");
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public void Every_permission_used_on_an_endpoint_exists_in_the_catalogue()
        {
            var known = Perm.All.Select(p => p.Name).ToHashSet();
            var used = Actions().SelectMany(a => a.Action.GetCustomAttributes<HasPermissionAttribute>().Concat(a.Controller.GetCustomAttributes<HasPermissionAttribute>())).Select(p => p.Permission).Distinct();
            Assert.Empty(used.Where(p => !known.Contains(p)));
        }

        [Fact]
        public void Tokens_can_only_ever_be_given_catalogued_integration_permissions_and_no_admin_rights()
        {
            var known = Perm.All.Select(p => p.Name).ToHashSet();
            Assert.All(Perm.TokenScopes, s => Assert.Contains(s, known));
            string[] forbidden = { Perm.ClientsManage, Perm.UsersManage, Perm.RolesManage, Perm.ModulesManage, Perm.AuditView, Perm.DevicesManage, Perm.TokensManage, Perm.WebhooksManage };
            Assert.Empty(Perm.TokenScopes.Intersect(forbidden));
        }
    }
}
