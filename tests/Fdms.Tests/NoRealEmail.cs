using System.Runtime.CompilerServices;

namespace Fdms.Tests
{
    /// <summary>
    /// Tests must never send real email, whatever mail settings the developer has in appsettings.json or user-secrets. Environment variables beat both, so they are set once, before any test host starts.
    /// (Tests that check emailing replace the sender with a recording fake.)
    /// </summary>
    internal static class NoRealEmail
    {
        [ModuleInitializer]
        internal static void Init()
        {
            Environment.SetEnvironmentVariable("SmtpClientSetting__Enabled", "false");
            Environment.SetEnvironmentVariable("Email__Host", "");
            // most tests drive the operator-approval flow; SignupTests switch self-service sign-up on in their own host
            Environment.SetEnvironmentVariable("Onboarding__SelfService", "false");
        }
    }
}
