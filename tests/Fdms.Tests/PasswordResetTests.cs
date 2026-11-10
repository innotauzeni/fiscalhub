using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>Forgotten password: a 6-digit code by email, then a new password; the code works once and old sessions end.</summary>
    public class PasswordResetTests : IClassFixture<ExtrasFactory>
    {
        private readonly ExtrasFactory _f;
        public PasswordResetTests(ExtrasFactory f) => _f = f;

        private string CodeFor(string email)
        {
            lock (_f.Email.Messages)
            {
                var m = _f.Email.Messages.Last(x => x.To == email && x.Subject.Contains("password reset"));
                return Regex.Match(m.Html, @">(\d{6})<").Groups[1].Value;
            }
        }

        [Fact]
        public async Task Reset_with_emailed_code_changes_password_once_and_signs_out_everywhere()
        {
            var co = await NewCompanyAsync(_f, "Reset Co");
            var anon = _f.CreateClient();
            var oldSession = await (await anon.PostAsJsonAsync("/api/v1/auth/login", new { identifier = co.AdminEmail, password = UserPassword })).Json();

            var ask = await anon.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = co.AdminEmail });
            Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
            var code = CodeFor(co.AdminEmail);
            Assert.Matches(@"^\d{6}$", code);

            // a wrong code is refused
            var wrong = await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { email = co.AdminEmail, code = code == "000000" ? "111111" : "000000", newPassword = "N3wPassword!x" });
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("INVALID_CODE", (await wrong.Json()).GetProperty("errorCode").GetString());

            // a weak password is refused and the code stays usable
            var weak = await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { email = co.AdminEmail, code, newPassword = "short" });
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

            var ok = await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { email = co.AdminEmail, code, newPassword = "N3wPassword!x" });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            // single use
            var again = await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new { email = co.AdminEmail, code, newPassword = "An0therPass!y" });
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

            // old password gone, new one works, old refresh token no longer works
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login", new { identifier = co.AdminEmail, password = UserPassword })).StatusCode);
            await LoginAsync(_f, co.AdminEmail, "N3wPassword!x");
            var refresh = await anon.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = oldSession.GetProperty("refreshToken").GetString() });
            Assert.False(refresh.IsSuccessStatusCode);
        }

        [Fact]
        public async Task Unknown_email_gets_the_same_answer_and_no_mail()
        {
            var before = _f.Email.Messages.Count;
            var r = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "nobody-" + Guid.NewGuid() + "@nowhere.test" });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("If that email belongs to an account", (await r.Json()).GetProperty("message").GetString());
            Assert.Equal(before, _f.Email.Messages.Count);
        }
    }
}
