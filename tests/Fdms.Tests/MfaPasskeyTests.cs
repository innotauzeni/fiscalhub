using System.Formats.Cbor;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>Two-step sign-in with an authenticator app, recovery codes, and passkeys (with a software authenticator standing in for the browser).</summary>
    public class MfaPasskeyTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        public MfaPasskeyTests(AccessFactory f) => _f = f;

        // ---- helpers ------------------------------------------------------------------------------------------------------

        internal static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        internal static byte[] FromB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }

        /// <summary>RFC 6238: what an authenticator app shows for this secret right now.</summary>
        internal static string Totp(string spacedSecret)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var s = spacedSecret.Replace(" ", "").ToUpperInvariant();
            var bytes = new List<byte>(); int bits = 0, acc = 0;
            foreach (var ch in s) { acc = (acc << 5) | alphabet.IndexOf(ch); bits += 5; if (bits >= 8) { bits -= 8; bytes.Add((byte)((acc >> bits) & 0xFF)); } }
            var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            var msg = BitConverter.GetBytes(step); if (BitConverter.IsLittleEndian) Array.Reverse(msg);
            var h = new HMACSHA1(bytes.ToArray()).ComputeHash(msg);
            var o = h[^1] & 0xF;
            var bin = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
            return (bin % 1_000_000).ToString("D6");
        }

        private async Task<(TestKit.Company Co, string Email, HttpClient Me)> UserAsync()
        {
            var co = await NewCompanyAsync(_f, "Mfa Co");
            var email = Unique("sec") + "@users.test";
            var res = await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Sam", surname = "Secure", role = "Operator", password = UserPassword });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return (co, email, await LoginAsync(_f, email, UserPassword));
        }

        private Task<HttpResponseMessage> RawLogin(string email, string password = UserPassword) => _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password });

        private async Task<string[]> TurnOnMfaAsync(HttpClient me)
        {
            var setup = await (await me.PostAsync("/api/v1/security/mfa/setup", null)).Json();
            Assert.StartsWith("otpauth://totp/", setup.GetProperty("uri").GetString());
            Assert.StartsWith("data:image/png;base64,", setup.GetProperty("qrImage").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/v1/security/mfa/enable", new { code = "000000" })).StatusCode);
            var on = await me.PostAsJsonAsync("/api/v1/security/mfa/enable", new { code = Totp(setup.GetProperty("secret").GetString()!) });
            Assert.True(on.IsSuccessStatusCode, await on.Content.ReadAsStringAsync());
            MfaSecret = setup.GetProperty("secret").GetString()!;
            return (await on.Json()).GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        }
        private string MfaSecret = "";

        // ---- authenticator app ----------------------------------------------------------------------------------------------

        [Fact]
        public async Task Two_step_sign_in_needs_the_code_and_recovery_codes_work_once()
        {
            var (co, email, me) = await UserAsync();
            Assert.False((await me.GetFromJsonAsync<JsonElement>("/api/v1/security", J)).GetProperty("mfaEnabled").GetBoolean());

            var recovery = await TurnOnMfaAsync(me);
            Assert.Equal(10, recovery.Length);
            Assert.Equal(HttpStatusCode.Conflict, (await me.PostAsync("/api/v1/security/mfa/setup", null)).StatusCode);   // already on
            var status = await me.GetFromJsonAsync<JsonElement>("/api/v1/security", J);
            Assert.True(status.GetProperty("mfaEnabled").GetBoolean()); Assert.Equal(10, status.GetProperty("recoveryCodesLeft").GetInt32());

            // the password alone no longer opens a session
            var first = await (await RawLogin(email)).Json();
            Assert.True(first.GetProperty("mfaRequired").GetBoolean());
            Assert.True(string.IsNullOrEmpty(first.GetProperty("accessToken").GetString()));
            var token = first.GetProperty("mfaToken").GetString()!;
            Assert.Equal(HttpStatusCode.Unauthorized, (await RawLogin(email, "wrong-Password1")).StatusCode);

            var anon = _f.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/mfa", new { mfaToken = token, code = "123456" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/mfa", new { mfaToken = "not-a-token", code = Totp(MfaSecret) })).StatusCode);
            var ok = await anon.PostAsJsonAsync("/api/v1/auth/login/mfa", new { mfaToken = token, code = Totp(MfaSecret) });
            Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
            var session = await ok.Json();
            Assert.False(string.IsNullOrEmpty(session.GetProperty("accessToken").GetString()));
            Assert.False(session.GetProperty("mfaRequired").GetBoolean());

            // a recovery code signs in once and is then spent
            var t2 = (await (await RawLogin(email)).Json()).GetProperty("mfaToken").GetString()!;
            Assert.True((await anon.PostAsJsonAsync("/api/v1/auth/login/mfa", new { mfaToken = t2, code = recovery[0] })).IsSuccessStatusCode);
            var t3 = (await (await RawLogin(email)).Json()).GetProperty("mfaToken").GetString()!;
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/mfa", new { mfaToken = t3, code = recovery[0] })).StatusCode);
            Assert.Equal(9, (await me.GetFromJsonAsync<JsonElement>("/api/v1/security", J)).GetProperty("recoveryCodesLeft").GetInt32());

            // new recovery codes and switching off both ask for the password
            Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/v1/security/mfa/recovery-codes", new { password = "nope" })).StatusCode);
            Assert.Equal(10, (await (await me.PostAsJsonAsync("/api/v1/security/mfa/recovery-codes", new { password = UserPassword })).Json()).GetProperty("recoveryCodes").GetArrayLength());
            Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/v1/security/mfa/disable", new { password = "nope" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await me.PostAsJsonAsync("/api/v1/security/mfa/disable", new { password = UserPassword })).StatusCode);
            Assert.False((await (await RawLogin(email)).Json()).GetProperty("mfaRequired").GetBoolean());

            // it is all in the audit trail
            var audit = await AuditAsync(_f, co.ClientId, "action=auth.mfa-enabled");
            Assert.True(audit.GetProperty("items").GetArrayLength() >= 1);
        }

        [Fact]
        public async Task Wrong_codes_lock_the_account_like_wrong_passwords()
        {
            var (_, email, me) = await UserAsync();
            await TurnOnMfaAsync(me);
            var anon = _f.CreateClient();
            for (var i = 0; i < 5; i++)
            {
                var t = (await (await RawLogin(email)).Json()).GetProperty("mfaToken").GetString()!;
                Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/login/mfa", new { mfaToken = t, code = "000000" })).StatusCode);
            }
            var after = await RawLogin(email);   // the 6th try is refused outright, even with the right password
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
            Assert.Contains("ACCOUNT_LOCKED", await after.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task A_company_administrator_can_reset_a_colleagues_two_step_sign_in_but_not_another_companys()
        {
            var (co, email, me) = await UserAsync();
            await TurnOnMfaAsync(me);
            var users = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users", J);
            var id = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetString();

            var other = await NewCompanyAsync(_f, "Other Mfa Co");
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsync($"/api/v1/users/{id}/reset-mfa", null)).StatusCode);
            Assert.True((await (await RawLogin(email)).Json()).GetProperty("mfaRequired").GetBoolean());

            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.PostAsync($"/api/v1/users/{id}/reset-mfa", null)).StatusCode);
            Assert.False((await (await RawLogin(email)).Json()).GetProperty("mfaRequired").GetBoolean());
            Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsync($"/api/v1/users/{id}/reset-mfa", null)).StatusCode);   // an operator may not
        }

        // ---- passkeys -------------------------------------------------------------------------------------------------------

        /// <summary>A software authenticator: makes the same bytes a phone or security key would.</summary>
        internal sealed class Authenticator
        {
            private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            public readonly byte[] CredentialId = RandomNumberGenerator.GetBytes(32);
            private uint _counter;
            private const string Origin = "http://localhost";

            private static byte[] Sha(byte[] b) => SHA256.HashData(b);

            private static byte[] ClientData(string type, string challenge) =>
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin = Origin, crossOrigin = false }));

            public object Create(JsonElement options)
            {
                var challenge = options.GetProperty("challenge").GetString()!;
                var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
                var p = _key.ExportParameters(false);
                var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
                cose.WriteStartMap(5);
                cose.WriteInt32(1); cose.WriteInt32(2);          // kty EC2
                cose.WriteInt32(3); cose.WriteInt32(-7);         // alg ES256
                cose.WriteInt32(-1); cose.WriteInt32(1);         // crv P-256
                cose.WriteInt32(-2); cose.WriteByteString(p.Q.X!);
                cose.WriteInt32(-3); cose.WriteByteString(p.Q.Y!);
                cose.WriteEndMap();
                var auth = new List<byte>();
                auth.AddRange(Sha(Encoding.UTF8.GetBytes(rpId)));
                auth.Add(0x45);                                   // user present + verified + attested credential data
                auth.AddRange(new byte[4]);                       // counter 0
                auth.AddRange(new byte[16]);                      // aaguid
                auth.Add(0); auth.Add((byte)CredentialId.Length);
                auth.AddRange(CredentialId);
                auth.AddRange(cose.Encode());
                var att = new CborWriter(CborConformanceMode.Ctap2Canonical);
                att.WriteStartMap(3);
                att.WriteTextString("fmt"); att.WriteTextString("none");
                att.WriteTextString("attStmt"); att.WriteStartMap(0); att.WriteEndMap();
                att.WriteTextString("authData"); att.WriteByteString(auth.ToArray());
                att.WriteEndMap();
                return new { id = B64(CredentialId), rawId = B64(CredentialId), type = "public-key", response = new { attestationObject = B64(att.Encode()), clientDataJSON = B64(ClientData("webauthn.create", challenge)) } };
            }

            public object Get(JsonElement options, string userId, bool tamper = false)
            {
                var challenge = options.GetProperty("challenge").GetString()!;
                var rpId = options.GetProperty("rpId").GetString()!;
                _counter++;
                var counter = BitConverter.GetBytes(_counter); if (BitConverter.IsLittleEndian) Array.Reverse(counter);
                var auth = Sha(Encoding.UTF8.GetBytes(rpId)).Concat(new byte[] { 0x05 }).Concat(counter).ToArray();   // present + verified
                var cd = ClientData("webauthn.get", challenge);
                var sig = _key.SignData(auth.Concat(Sha(cd)).ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                if (tamper) sig[^1] ^= 0xFF;
                return new { id = B64(CredentialId), rawId = B64(CredentialId), type = "public-key", response = new { authenticatorData = B64(auth), clientDataJSON = B64(cd), signature = B64(sig), userHandle = B64(Encoding.UTF8.GetBytes(userId)) } };
            }
        }

        private static string UserIdOf(JsonElement registrationOptions) => Encoding.UTF8.GetString(FromB64(registrationOptions.GetProperty("user").GetProperty("id").GetString()!));

        private async Task<(Authenticator Key, string UserId)> RegisterAsync(HttpClient me, string name = "Work laptop")
        {
            var begin = await (await me.PostAsync("/api/v1/security/passkeys/options", null)).Json();
            var options = begin.GetProperty("options");
            var key = new Authenticator();
            var res = await me.PostAsJsonAsync("/api/v1/security/passkeys/" + begin.GetProperty("sessionId").GetString(), new { name, credential = key.Create(options) });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            Assert.Equal(name, (await res.Json()).GetProperty("name").GetString());
            return (key, UserIdOf(options));
        }

        private async Task<HttpResponseMessage> PasskeyLoginAsync(Authenticator key, string userId, bool tamper = false, string? sessionOverride = null)
        {
            var anon = _f.CreateClient();
            var begin = await (await anon.PostAsync("/api/v1/auth/passkey/options", null)).Json();
            return await anon.PostAsJsonAsync("/api/v1/auth/passkey/login", new { sessionId = sessionOverride ?? begin.GetProperty("sessionId").GetString(), credential = key.Get(begin.GetProperty("options"), userId, tamper) });
        }

        [Fact]
        public async Task A_passkey_signs_in_without_a_password_or_code()
        {
            var (co, email, me) = await UserAsync();
            var (key, userId) = await RegisterAsync(me);
            var list = (await me.GetFromJsonAsync<JsonElement>("/api/v1/security", J)).GetProperty("passkeys");
            Assert.Equal(1, list.GetArrayLength());
            var passkeyId = list[0].GetProperty("id").GetGuid();

            var res = await PasskeyLoginAsync(key, userId);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var session = await res.Json();
            Assert.Equal(email, session.GetProperty("user").GetProperty("email").GetString());
            var c = _f.CreateClient(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
            Assert.NotNull((await me.GetFromJsonAsync<JsonElement>("/api/v1/security", J)).GetProperty("passkeys")[0].GetProperty("lastUsedAt").GetString());

            // it also gets past two-step sign-in, since the device already checked who you are
            await TurnOnMfaAsync(me);
            Assert.True((await PasskeyLoginAsync(key, userId)).IsSuccessStatusCode);

            // a forged signature, a challenge used twice and an unknown challenge are all refused
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(key, userId, tamper: true)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await PasskeyLoginAsync(key, userId, sessionOverride: "nope")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(new Authenticator(), userId)).StatusCode);   // never registered

            // rename, then remove (the password is asked for)
            Assert.Equal("Phone", (await (await me.PutAsJsonAsync("/api/v1/security/passkeys/" + passkeyId, new { name = "Phone" })).Json()).GetProperty("name").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync($"/api/v1/security/passkeys/{passkeyId}/remove", new { password = "nope" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await me.PostAsJsonAsync($"/api/v1/security/passkeys/{passkeyId}/remove", new { password = UserPassword })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(key, userId)).StatusCode);

            var audit = await AuditAsync(_f, co.ClientId, "action=auth.passkey-added");
            Assert.True(audit.GetProperty("items").GetArrayLength() >= 1);
        }

        [Fact]
        public async Task A_disabled_person_cannot_use_a_passkey_and_someone_elses_passkey_is_not_theirs()
        {
            var (co, email, me) = await UserAsync();
            var (key, userId) = await RegisterAsync(me);
            var other = await UserAsync();
            var (otherKey, otherId) = await RegisterAsync(other.Me, "Other");
            // the passkey proves the person who owns it: handing it someone else's user handle is refused
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(key, otherId)).StatusCode);
            Assert.True((await PasskeyLoginAsync(otherKey, otherId)).IsSuccessStatusCode);

            var users = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users", J);
            var id = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetString();
            var upd = await co.Admin.PutAsJsonAsync($"/api/v1/users/{id}", new { name = "Sam", surname = "Secure", role = "Operator", status = "INACTIVE" });
            Assert.True(upd.IsSuccessStatusCode, await upd.Content.ReadAsStringAsync());
            Assert.False((await PasskeyLoginAsync(key, userId)).IsSuccessStatusCode);
        }

        [Fact]
        public async Task Resetting_a_user_removes_their_passkeys()
        {
            var (co, email, me) = await UserAsync();
            var (key, userId) = await RegisterAsync(me);
            var users = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users", J);
            var id = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.PostAsync($"/api/v1/users/{id}/reset-mfa", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeyLoginAsync(key, userId)).StatusCode);
        }
    }
}
