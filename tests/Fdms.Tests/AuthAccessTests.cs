using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Security;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Auth;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Fdms.Tests
{
    public sealed class AccessFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly string _keys = Path.Combine(Path.GetTempPath(), "fdms-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestKit.JwtKey,
                ["Seed:AdminEmail"] = TestKit.AdminEmail,
                ["Seed:AdminPassword"] = TestKit.AdminPassword,
                ["Seed:ForcePasswordChange"] = "false",
                ["DataProtection:KeysPath"] = _keys,
                ["Backup:Path"] = Path.Combine(Path.GetTempPath(), "fdms-test-backups-" + Guid.NewGuid()),
                ["RateLimit:PerMinute"] = "100000",
                ["RateLimit:LoginPerMinute"] = "100000",
                ["Fdms:DistributedLocks"] = "false",
                ["Auth:ActiveCacheSeconds"] = "0",
            }));
            builder.ConfigureServices(s =>
            {
                TestDb.Use(s, _db);
                s.RemoveAll<IFdmsGateway>();
                s.AddSingleton<IFdmsGateway>(Fdms);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    public class AuthAccessTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 12000;
        public AuthAccessTests(AccessFactory f) => _f = f;

        private async Task<JsonElement> LoginBody(string id, string pw, HttpStatusCode expect = HttpStatusCode.OK)
        {
            var res = await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { identifier = id, password = pw });
            Assert.Equal(expect, res.StatusCode);
            return await res.Json();
        }

        /// <summary>Company administrator creates a user with the given role and signs in as them.</summary>
        private async Task<HttpClient> UserWithRoleAsync(TestKit.Company co, string role)
        {
            var email = TestKit.Unique(role.ToLowerInvariant()) + "@users.test";
            var res = await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = role, surname = "User", role, password = TestKit.UserPassword });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
        }

        private static object Invoice(string extRef, int taxPercent = 15) => new
        {
            externalRef = extRef, invoiceNo = "INV-" + extRef, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
            lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent } },
        };

        private static string[] Slugs(JsonElement login) => login.GetProperty("menus").EnumerateArray().Select(m => m.GetProperty("slugname").GetString()!).ToArray();

        // ---- seeding -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task Seeding_creates_the_access_model_and_is_idempotent_and_respects_later_changes()
        {
            var op = await TestKit.AdminAsync(_f);
            var roles = (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles", J)).EnumerateArray().Select(r => r.GetProperty("name").GetString()).ToList();
            Assert.Contains("SuperAdmin", roles); Assert.Contains("ClientAdmin", roles); Assert.Contains("Operator", roles); Assert.Contains("Viewer", roles);
            Assert.Equal(Perm.All.Count, (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles/permissions", J)).GetArrayLength());
            var tree = await op.GetFromJsonAsync<JsonElement>("/api/v1/modules", J);
            Assert.Equal(6, tree.GetProperty("sections").GetArrayLength());
            Assert.Equal(83, tree.GetProperty("modules").GetArrayLength());

            // the operator removes a permission from Operator; a restart (re-seed) must not put it back
            var operatorRole = (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles", J)).EnumerateArray().First(r => r.GetProperty("name").GetString() == "Operator");
            var rid = operatorRole.GetProperty("id").GetString()!;
            var perms = await op.GetFromJsonAsync<JsonElement>("/api/v1/roles/permissions", J);
            var stock = perms.EnumerateArray().First(p => p.GetProperty("name").GetString() == "stock.view").GetProperty("id").GetInt32();
            var access = await op.GetFromJsonAsync<JsonElement>($"/api/v1/roles/{rid}/access", J);
            var keep = access.GetProperty("permissionIds").EnumerateArray().Select(x => x.GetInt32()).Where(x => x != stock).ToArray();
            Assert.Equal(HttpStatusCode.NoContent, (await op.PutAsJsonAsync($"/api/v1/roles/{rid}/permissions", new { ids = keep })).StatusCode);

            await DatabaseSeeder.SeedAsync(_f.Services);
            await DatabaseSeeder.SeedAsync(_f.Services);
            Assert.Equal(6, (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles", J)).EnumerateArray().Count(r => r.GetProperty("type").GetString() == "system"));
            Assert.Equal(83, (await op.GetFromJsonAsync<JsonElement>("/api/v1/modules", J)).GetProperty("modules").GetArrayLength());
            Assert.DoesNotContain(stock, (await op.GetFromJsonAsync<JsonElement>($"/api/v1/roles/{rid}/access", J)).GetProperty("permissionIds").EnumerateArray().Select(x => x.GetInt32()));
        }

        // ---- permission catalogue + role editing ---------------------------------------------------------------------

        [Fact]
        public async Task Permissions_can_be_added_in_bulk_edited_and_deleted_and_the_built_in_ones_are_protected()
        {
            var op = await TestKit.AdminAsync(_f);
            var prefix = "rep" + Interlocked.Increment(ref _seq);

            var batch = await op.PostAsJsonAsync("/api/v1/modules/permissions/batch", new
            {
                module = "Reports", prefix,
                items = new object[] { new { action = "view", displayName = "View reports" }, new { action = "export", displayName = (string?)null }, new { action = "view", displayName = "again" }, new { action = "  ", displayName = "blank" } },
            });
            Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
            var result = await batch.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(2, result.GetProperty("created").GetInt32());                       // the repeated "view" and the blank row were not created
            Assert.Equal(new[] { $"{prefix}.view" }, result.GetProperty("skipped").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal("Export", result.GetProperty("permissions").EnumerateArray().First(p => p.GetProperty("name").GetString() == $"{prefix}.export").GetProperty("displayName").GetString());

            // running the same batch again creates nothing and fails nothing
            var again = await (await op.PostAsJsonAsync("/api/v1/modules/permissions/batch", new { module = "Reports", prefix, items = new object[] { new { action = "view", displayName = "x" } } })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(0, again.GetProperty("created").GetInt32());

            // invalid names are refused
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/modules/permissions", new { id = 0, name = "NoDots", displayName = "x", module = "m" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/api/v1/modules/permissions/batch", new { module = "Reports", prefix = "", items = new object[] { new { action = "lonely", displayName = "x" } } })).StatusCode);

            // single add, duplicate, rename
            var added = await (await op.PutAsJsonAsync("/api/v1/modules/permissions", new { id = 0, name = $"{prefix}.print", displayName = "Print", module = "Reports" })).Content.ReadFromJsonAsync<JsonElement>(J);
            var id = added.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Conflict, (await op.PutAsJsonAsync("/api/v1/modules/permissions", new { id = 0, name = $"{prefix}.print", displayName = "Print", module = "Reports" })).StatusCode);
            var renamed = await (await op.PutAsJsonAsync("/api/v1/modules/permissions", new { id, name = $"{prefix}.printing", displayName = "Printing", module = "Reports" })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal($"{prefix}.printing", renamed.GetProperty("name").GetString());

            // a role that holds it loses it when it is deleted
            var viewer = (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles", J)).EnumerateArray().First(r => r.GetProperty("name").GetString() == "Viewer").GetProperty("id").GetString()!;
            var held = (await op.GetFromJsonAsync<JsonElement>($"/api/v1/roles/{viewer}/access", J)).GetProperty("permissionIds").EnumerateArray().Select(x => x.GetInt32()).Append(id).ToArray();
            Assert.Equal(HttpStatusCode.NoContent, (await op.PutAsJsonAsync($"/api/v1/roles/{viewer}/permissions", new { ids = held })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await op.DeleteAsync($"/api/v1/modules/permissions/{id}")).StatusCode);
            Assert.DoesNotContain(id, (await op.GetFromJsonAsync<JsonElement>($"/api/v1/roles/{viewer}/access", J)).GetProperty("permissionIds").EnumerateArray().Select(x => x.GetInt32()));

            // permissions the code checks can be reworded but never renamed or deleted
            var receipts = (await op.GetFromJsonAsync<JsonElement>("/api/v1/modules/permissions", J)).EnumerateArray().First(p => p.GetProperty("name").GetString() == "receipts.view");
            var rid = receipts.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.OK, (await op.PutAsJsonAsync("/api/v1/modules/permissions", new { id = rid, name = "receipts.view", displayName = "See receipts", module = "Receipts" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await op.PutAsJsonAsync("/api/v1/modules/permissions", new { id = rid, name = "receipts.look", displayName = "x", module = "Receipts" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await op.DeleteAsync($"/api/v1/modules/permissions/{rid}")).StatusCode);

            // only people allowed to manage modules can touch the catalogue
            var co = await TestKit.NewCompanyAsync(_f, "Perm Co");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsJsonAsync("/api/v1/modules/permissions/batch", new { module = "X", prefix = "x", items = new object[] { new { action = "a", displayName = "A" } } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/modules/permissions")).StatusCode);
        }

        [Fact]
        public async Task Roles_can_be_edited_but_built_in_roles_keep_their_name_and_level()
        {
            var op = await TestKit.AdminAsync(_f);
            var name = "Auditor" + Interlocked.Increment(ref _seq);
            var created = await (await op.PostAsJsonAsync("/api/v1/roles", new { name, level = "client", description = "first" })).Content.ReadFromJsonAsync<JsonElement>(J);
            var id = created.GetProperty("id").GetString()!;

            var edited = await (await op.PutAsJsonAsync($"/api/v1/roles/{id}", new { name = name + "X", level = "platform", description = "second" })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(name + "X", edited.GetProperty("name").GetString());
            Assert.Equal("platform", edited.GetProperty("level").GetString());
            Assert.Equal("second", edited.GetProperty("description").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await op.PutAsJsonAsync($"/api/v1/roles/{id}", new { name = "Viewer", level = "client", description = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync($"/api/v1/roles/{id}", new { name = name + "X", level = "galaxy", description = "x" })).StatusCode);

            var viewer = (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles", J)).EnumerateArray().First(r => r.GetProperty("name").GetString() == "Viewer").GetProperty("id").GetString()!;
            var kept = await (await op.PutAsJsonAsync($"/api/v1/roles/{viewer}", new { name = "Hacked", level = "platform", description = "Read-only access" })).Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal("Viewer", kept.GetProperty("name").GetString());
            Assert.Equal("client", kept.GetProperty("level").GetString());
            Assert.Equal("Read-only access", kept.GetProperty("description").GetString());
        }

        // ---- login -------------------------------------------------------------------------------------------------

        [Fact]
        public async Task Login_returns_tokens_user_permissions_and_the_menu_tree_and_hides_which_part_was_wrong()
        {
            var ok = await LoginBody(TestKit.AdminEmail, TestKit.AdminPassword);
            Assert.False(string.IsNullOrEmpty(ok.GetProperty("accessToken").GetString()));
            Assert.False(string.IsNullOrEmpty(ok.GetProperty("refreshToken").GetString()));
            Assert.Contains("SuperAdmin", ok.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
            Assert.Contains("clients.manage", ok.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
            var sections = ok.GetProperty("sectionMenus");
            Assert.Equal(new[] { "Integration", "Administration", "Accounting" }, sections.EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray());

            var wrong = await LoginBody(TestKit.AdminEmail, "Wrong-Password-1", HttpStatusCode.Unauthorized);
            var nobody = await LoginBody("nobody@test.local", "Wrong-Password-1", HttpStatusCode.Unauthorized);
            Assert.Equal("INVALID_CREDENTIALS", wrong.GetProperty("errorCode").GetString());
            Assert.Equal(wrong.GetProperty("title").GetString(), nobody.GetProperty("title").GetString());   // no account probing
            await LoginBody("", "", HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Lock Co");
            for (var i = 0; i < 5; i++) await LoginBody(co.AdminEmail, "Wrong-Password-1", HttpStatusCode.Unauthorized);
            var locked = await LoginBody(co.AdminEmail, TestKit.UserPassword, HttpStatusCode.Unauthorized);
            Assert.Equal("ACCOUNT_LOCKED", locked.GetProperty("errorCode").GetString());
        }

        [Fact]
        public async Task Refresh_tokens_rotate_and_a_reused_one_ends_the_session()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Refresh Co");
            var first = await LoginBody(co.AdminEmail, TestKit.UserPassword);
            var r1 = first.GetProperty("refreshToken").GetString();

            var c = _f.CreateClient();
            var second = await (await c.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = r1 })).Json();
            var r2 = second.GetProperty("refreshToken").GetString();
            Assert.NotEqual(r1, r2);
            Assert.Equal(HttpStatusCode.OK, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = r2 })).StatusCode);   // r2 -> r3 works once

            // replaying the already-used r1 is theft: it fails AND takes the newer tokens with it
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = r1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = r2 })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "garbage" })).StatusCode);

            var again = await LoginBody(co.AdminEmail, TestKit.UserPassword);
            await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = again.GetProperty("refreshToken").GetString() });
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = again.GetProperty("refreshToken").GetString() })).StatusCode);
        }

        [Fact]
        public async Task New_administrators_must_change_their_password_and_the_policy_is_enforced()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Change Co");
            var login = await LoginBody(co.AdminEmail, TestKit.UserPassword);
            Assert.True(login.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());

            var weak = await co.Admin.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = TestKit.UserPassword, newPassword = "short" });
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "Wrong-One-123", newPassword = "Brand-New-Pass1" })).StatusCode);

            var done = await co.Admin.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = TestKit.UserPassword, newPassword = "Brand-New-Pass1" });
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
            Assert.False((await done.Json()).GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
            await LoginBody(co.AdminEmail, TestKit.UserPassword, HttpStatusCode.Unauthorized);   // old password is dead
            await LoginBody(co.AdminEmail, "Brand-New-Pass1");

            // an API token has no password to change
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var token = await TestKit.NewTokenAsync(_f, co.Admin, dev);
            Assert.Equal(HttpStatusCode.Unauthorized, (await token.Http.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "a", newPassword = "b" })).StatusCode);
        }

        // ---- roles and permissions ---------------------------------------------------------------------------------

        [Fact]
        public async Task Each_role_can_do_only_what_its_permissions_allow()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Roles Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var viewer = await UserWithRoleAsync(co, "Viewer");
            var oper = await UserWithRoleAsync(co, "Operator");

            // viewer: read-only
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/receipts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("V1"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/devices/{dev}/day/open", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/devices/{dev}/tokens", new { name = "x", scopes = new[] { "receipts.view" } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/users")).StatusCode);

            // operator: fiscal work, but no registering devices, tokens or webhooks
            Assert.Equal(HttpStatusCode.Created, (await oper.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("O1"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await oper.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = 1, activationKey = "ABCD1234", serialNo = "s", modelName = "m", modelVersion = "v" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await oper.PostAsJsonAsync($"/api/v1/devices/{dev}/tokens", new { name = "x", scopes = new[] { "receipts.view" } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await oper.GetAsync("/api/v1/webhook")).StatusCode);

            // accountant: keeps the books, reads receipts, cannot issue them or run devices
            var acct = await UserWithRoleAsync(co, "Accountant");
            Assert.Equal(HttpStatusCode.OK, (await acct.GetAsync("/api/v1/accounting/accounts")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await acct.GetAsync("/api/v1/receipts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await acct.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("A1"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await acct.PostAsync($"/api/v1/devices/{dev}/day/open", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await acct.GetAsync("/api/v1/users")).StatusCode);

            // company administrator: tokens and webhooks yes, platform administration no
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync("/api/v1/webhook")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/clients")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/roles")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/modules")).StatusCode);

            // the platform operator runs the platform, not a company's receipts
            var op = await TestKit.AdminAsync(_f);
            Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/v1/clients")).StatusCode);
        }

        [Fact]
        public async Task Custom_roles_drive_permissions_and_the_dynamic_menu_and_cannot_lock_the_operator_out()
        {
            var op = await TestKit.AdminAsync(_f);
            var co = await TestKit.NewCompanyAsync(_f, "Custom Co");

            var created = await op.PostAsJsonAsync("/api/v1/roles", new { name = "Auditor", level = "client", description = "Edits invoice templates" });
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var roleId = (await created.Json()).GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync("/api/v1/roles", new { name = "Auditor", level = "client" })).StatusCode);

            var perms = (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles/permissions", J)).EnumerateArray().ToList();
            int PermId(string n) => perms.First(p => p.GetProperty("name").GetString() == n).GetProperty("id").GetInt32();
            var modules = (await op.GetFromJsonAsync<JsonElement>("/api/v1/modules", J)).GetProperty("modules").EnumerateArray().ToList();
            int ModId(string slug) => modules.First(m => m.GetProperty("slugname").GetString() == slug).GetProperty("id").GetInt32();

            // operator-only rights can never be given to a company role: not the audit log, not the settings or the payments
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync($"/api/v1/roles/{roleId}/permissions", new { ids = new[] { PermId("audit.view") } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync($"/api/v1/roles/{roleId}/permissions", new { ids = new[] { PermId("payments.manage") } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync($"/api/v1/roles/{roleId}/menus", new { ids = new[] { ModId("admin/audit") } })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await op.PutAsJsonAsync($"/api/v1/roles/{roleId}/permissions", new { ids = new[] { PermId("templates.manage") } })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await op.PutAsJsonAsync($"/api/v1/roles/{roleId}/menus", new { ids = new[] { ModId("admin/templates"), ModId("tokens") } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync($"/api/v1/roles/{roleId}/permissions", new { ids = new[] { 99999 } })).StatusCode);

            var email = TestKit.Unique("auditor") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Au", surname = "Ditor", role = "Auditor", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var login = await LoginBody(email, TestKit.UserPassword);
            // 'tokens' was given as a menu entry but the role lacks tokens.manage, so the entry is not shown (menu AND permission)
            Assert.Equal(new[] { "admin/templates" }, Slugs(login));
            Assert.Equal(new[] { "templates.manage" }, login.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray());

            var auditor = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/v1/templates")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/v1/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/v1/receipts")).StatusCode);

            // role in use cannot be deleted; built-in roles never; once empty a custom role can
            Assert.Equal(HttpStatusCode.Conflict, (await op.DeleteAsync($"/api/v1/roles/{roleId}")).StatusCode);
            var superId = (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles", J)).EnumerateArray().First(r => r.GetProperty("name").GetString() == "SuperAdmin").GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.Conflict, (await op.DeleteAsync($"/api/v1/roles/{superId}")).StatusCode);

            // the operator cannot strip their own admin rights
            Assert.Equal(HttpStatusCode.Conflict, (await op.PutAsJsonAsync($"/api/v1/roles/{superId}/permissions", new { ids = new[] { PermId("audit.view") } })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await op.PutAsJsonAsync($"/api/v1/roles/{superId}/menus", new { ids = new[] { ModId("admin/audit") } })).StatusCode);
        }

        [Fact]
        public async Task The_audit_log_is_for_the_platform_operator_only_even_for_a_company_administrator()
        {
            var op = await TestKit.AdminAsync(_f);
            var co = await TestKit.NewCompanyAsync(_f, "Audit Co");
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/v1/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/v1/audit/search")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/audit/search")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _f.CreateClient().GetAsync("/api/v1/audit")).StatusCode);
            var menus = (await LoginBody(co.AdminEmail, TestKit.UserPassword)).GetProperty("menus").EnumerateArray().Select(m => m.GetProperty("slugname").GetString()).ToList();
            Assert.DoesNotContain("admin/audit", menus);
        }

        [Fact]
        public async Task SuperAdmin_can_never_be_offered_or_assigned_from_the_users_screens()
        {
            var op = await TestKit.AdminAsync(_f);
            var co = await TestKit.NewCompanyAsync(_f, "No Escalation Co");
            // the role lists (for the operator and for a company administrator) contain company roles only
            foreach (var who in new[] { op, co.Admin })
            {
                var roles = (await who.GetFromJsonAsync<JsonElement>("/api/v1/users/roles", J)).EnumerateArray().Select(r => r.GetString()).ToList();
                Assert.DoesNotContain("SuperAdmin", roles);
                Assert.Contains("ClientAdmin", roles); Assert.Contains("Viewer", roles);
            }
            // a crafted request cannot create one, for a company or for nobody
            var company = co.ClientId;
            foreach (var clientId in new Guid?[] { company, null })
            {
                var res = await op.PostAsJsonAsync("/api/v1/users", new { email = TestKit.Unique("evil") + "@users.test", name = "E", surname = "Vil", role = "SuperAdmin", password = TestKit.UserPassword, clientId });
                Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
                Assert.Contains("ROLE_FORBIDDEN", await res.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PostAsJsonAsync("/api/v1/users", new { email = TestKit.Unique("evil") + "@users.test", name = "E", surname = "Vil", role = "SuperAdmin", password = TestKit.UserPassword })).StatusCode);

            // a company user cannot be promoted to it, and the platform administrator cannot be edited through the form
            var email = TestKit.Unique("staff") + "@users.test";
            var created = await (await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "S", surname = "T", role = "Viewer", password = TestKit.UserPassword })).Content.ReadFromJsonAsync<JsonElement>(J);
            var id = created.GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsJsonAsync($"/api/v1/users/{id}", new { name = "S", surname = "T", role = "SuperAdmin", status = "ACTIVE" })).StatusCode);
            var root = (await op.GetFromJsonAsync<JsonElement>("/api/v1/users", J)).EnumerateArray().First(u => u.GetProperty("email").GetString() == TestKit.AdminEmail);
            Assert.NotEqual(HttpStatusCode.OK, (await op.PutAsJsonAsync($"/api/v1/users/{root.GetProperty("id").GetString()}", new { name = "X", surname = "Y", role = "Viewer", status = "ACTIVE" })).StatusCode);
        }

        [Fact]
        public async Task The_menu_a_user_gets_follows_their_role()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Menu Co");
            var viewerEmail = TestKit.Unique("viewer") + "@users.test";
            await co.Admin.PostAsJsonAsync("/api/v1/users", new { email = viewerEmail, name = "V", surname = "W", role = "Viewer", password = TestKit.UserPassword });

            var viewer = await LoginBody(viewerEmail, TestKit.UserPassword);
            Assert.Equal(new[] { "dashboard", "devices", "receipts", "days", "reports", "health", "guide", "inventory/items", "inventory/reports" }, Slugs(viewer));
            var receiptsMenu = viewer.GetProperty("menus").EnumerateArray().First(m => m.GetProperty("slugname").GetString() == "receipts");
            Assert.Equal(new[] { "All receipts" }, receiptsMenu.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray());   // no "New invoice"/"Import"
            Assert.DoesNotContain("tokens", Slugs(viewer));

            var admin = await LoginBody(co.AdminEmail, TestKit.UserPassword);
            Assert.Equal(new[] { "dashboard", "devices", "receipts", "days", "offline", "customers", "products", "reports", "compliance", "pos", "quotes", "recurring", "health", "alerts", "ai-reports", "tokens", "webhooks", "activity", "docs", "guide", "integrations", "connectors", "messaging", "admin/branches", "admin/banks", "admin/users", "admin/templates", "billing", "referrals", "approvals", "accounting/dashboard", "accounting/journals", "accounting/accounts", "accounting/cashbook", "accounting/reconcile", "accounting/reports", "accounting/suppliers", "accounting/bills", "accounting/receivables", "accounting/assets", "accounting/vat", "accounting/budgets", "accounting/documents", "accounting/cost-centres", "accounting/recurring", "accounting/year-end", "accounting/import", "accounting/rates", "accounting/schedules", "accounting/audit", "inventory/items", "inventory/grv", "inventory/transfers", "inventory/adjustments", "inventory/reports", "inventory/categories", "inventory/locations", "inventory/import" }, Slugs(admin));
            var adminReceipts = admin.GetProperty("menus").EnumerateArray().First(m => m.GetProperty("slugname").GetString() == "receipts");
            Assert.Equal(3, adminReceipts.GetProperty("children").GetArrayLength());
            Assert.Equal(new[] { "Fiscalisation", "Integration", "Administration", "Accounting", "Inventory" }, admin.GetProperty("sectionMenus").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray());
            Assert.Equal("#2563eb", admin.GetProperty("sectionMenus")[0].GetProperty("color").GetString());
        }

        // ---- users -------------------------------------------------------------------------------------------------

        [Fact]
        public async Task Company_administrators_manage_only_their_own_users_and_cannot_hand_out_platform_roles()
        {
            var a = await TestKit.NewCompanyAsync(_f, "Users A");
            var b = await TestKit.NewCompanyAsync(_f, "Users B");
            var email = TestKit.Unique("staff") + "@users.test";
            var created = await a.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "S", surname = "T", role = "Operator", password = TestKit.UserPassword });
            var uid = (await created.Json()).GetProperty("id").GetString()!;

            Assert.DoesNotContain(email, (await b.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users", J)).EnumerateArray().Select(u => u.GetProperty("email").GetString()));
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PutAsJsonAsync($"/api/v1/users/{uid}", new { name = "x", surname = "y", role = "Viewer", status = "ACTIVE" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.PostAsJsonAsync($"/api/v1/users/{uid}/reset-password", new { newPassword = "Another-Pass-123" })).StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await a.Admin.PostAsJsonAsync("/api/v1/users", new { email = TestKit.Unique("x") + "@users.test", name = "n", surname = "s", role = "SuperAdmin", password = TestKit.UserPassword })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await a.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "n", surname = "s", role = "Viewer", password = TestKit.UserPassword })).StatusCode);   // duplicate
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PostAsJsonAsync("/api/v1/users", new { email = TestKit.Unique("w") + "@users.test", name = "n", surname = "s", role = "Viewer", password = "weak" })).StatusCode);
            var assignable = (await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users/roles", J)).EnumerateArray().Select(r => r.GetString()).ToList();
            Assert.DoesNotContain("SuperAdmin", assignable);

            // administrators cannot demote or disable themselves
            var me = (await a.Admin.GetFromJsonAsync<JsonElement>("/api/v1/users", J)).EnumerateArray().First(u => u.GetProperty("email").GetString() == a.AdminEmail).GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync($"/api/v1/users/{me}", new { name = "x", surname = "y", role = "Viewer", status = "ACTIVE" })).StatusCode);

            // deactivating a user ends their sessions and sign-in
            var staff = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await a.Admin.PutAsJsonAsync($"/api/v1/users/{uid}", new { name = "S", surname = "T", role = "Operator", status = "INACTIVE" })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Equal("ACCOUNT_INACTIVE", (await LoginBody(email, TestKit.UserPassword, HttpStatusCode.Forbidden)).GetProperty("errorCode").GetString());

            // password reset: old password dies, new one forces a change
            Assert.Equal(HttpStatusCode.NoContent, (await a.Admin.PostAsJsonAsync($"/api/v1/users/{uid}/reset-password", new { newPassword = "Another-Pass-123" })).StatusCode);
            await a.Admin.PutAsJsonAsync($"/api/v1/users/{uid}", new { name = "S", surname = "T", role = "Operator", status = "ACTIVE" });
            await LoginBody(email, TestKit.UserPassword, HttpStatusCode.Unauthorized);
            Assert.True((await LoginBody(email, "Another-Pass-123")).GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        }

        // ---- per-device tokens -------------------------------------------------------------------------------------

        [Fact]
        public async Task A_token_is_bound_to_one_device_and_cannot_see_or_touch_the_companys_other_devices()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Two Devices");
            var (d1, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var (d2, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var t1 = await TestKit.NewTokenAsync(_f, co.Admin, d1);
            var t2 = await TestKit.NewTokenAsync(_f, co.Admin, d2);

            var inv1 = await (await t1.Http.PostAsJsonAsync($"/api/v1/devices/{d1}/receipts", Invoice("D1-1"))).Json();
            var inv2 = await (await t2.Http.PostAsJsonAsync($"/api/v1/devices/{d2}/receipts", Invoice("D2-1"))).Json();
            var id1 = inv1.GetProperty("id").GetGuid(); var id2 = inv2.GetProperty("id").GetGuid();

            // what token 1 may see
            var devices = await t1.Http.GetFromJsonAsync<JsonElement>("/api/v1/devices", J);
            Assert.Equal(1, devices.GetArrayLength());
            Assert.Equal(d1, devices[0].GetProperty("id").GetGuid());
            Assert.Equal(1, (await t1.Http.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());
            Assert.Equal(1, (await t1.Http.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", J)).GetProperty("devices").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await t1.Http.GetAsync($"/api/v1/receipts/{id1}")).StatusCode);

            // everything about device 2 is simply not there for token 1
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.GetAsync($"/api/v1/devices/{d2}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.PostAsJsonAsync($"/api/v1/devices/{d2}/receipts", Invoice("X"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.PostAsJsonAsync($"/api/v1/devices/{d2}/receipts/preview", Invoice("X"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.GetAsync($"/api/v1/devices/{d2}/day")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.PostAsync($"/api/v1/devices/{d2}/day/close", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.GetAsync($"/api/v1/devices/{d2}/stock")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.GetAsync($"/api/v1/devices/{d2}/offline-files")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.GetAsync($"/api/v1/receipts/{id2}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.GetAsync($"/api/v1/receipts/{id2}/pdf")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await t1.Http.PostAsync($"/api/v1/receipts/{id2}/cancel", null)).StatusCode);
            Assert.Equal(0, (await t1.Http.GetFromJsonAsync<JsonElement>($"/api/v1/receipts?deviceId={d2}", J)).GetProperty("total").GetInt32());

            // ...and it cannot credit an invoice that belongs to the other device
            var cn = await t1.Http.PostAsJsonAsync($"/api/v1/devices/{d1}/receipts", new
            {
                externalRef = "CN-X", invoiceNo = "CN-X", receiptType = "CreditNote", currency = "USD", linesTaxInclusive = true, notes = "n", originalReceiptId = id2,
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } },
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, cn.StatusCode);
            Assert.Equal("RCPT032", (await cn.Json()).GetProperty("errorCode").GetString());

            // people of the company still see both devices
            Assert.Equal(2, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/devices", J)).GetArrayLength());
            Assert.Equal(2, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/receipts", J)).GetProperty("total").GetInt32());
        }

        [Fact]
        public async Task A_tokens_scopes_limit_what_it_can_do_and_it_can_never_administer()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Scopes Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var readOnly = await TestKit.NewTokenAsync(_f, co.Admin, dev, new[] { "receipts.view", "devices.view" });

            Assert.Equal(HttpStatusCode.OK, (await readOnly.Http.GetAsync("/api/v1/receipts")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("S1"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.Http.PostAsync($"/api/v1/devices/{dev}/day/open", null)).StatusCode);

            var full = await TestKit.NewTokenAsync(_f, co.Admin, dev);
            Assert.Equal(HttpStatusCode.Created, (await full.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("S2"))).StatusCode);
            // even the full token is an integration credential, not an administrator
            Assert.Equal(HttpStatusCode.Forbidden, (await full.Http.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = 5, activationKey = "ABCD1234", serialNo = "s", modelName = "m", modelVersion = "v" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await full.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/tokens", new { name = "mine", scopes = new[] { "receipts.view" } })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await full.Http.GetAsync("/api/v1/tokens")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await full.Http.GetAsync("/api/v1/webhook")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await full.Http.PostAsync($"/api/v1/devices/{dev}/renew-certificate", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await full.Http.GetAsync("/api/v1/users")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await full.Http.GetAsync("/api/v1/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await full.Http.GetAsync("/api/v1/clients")).StatusCode);

            // creating tokens is strict
            async Task<JsonElement> Reject(object body) { var r = await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/tokens", body); Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); return await r.Json(); }
            Assert.Equal("SCOPE_NOT_ALLOWED", (await Reject(new { name = "n", scopes = new[] { "devices.manage" } })).GetProperty("errorCode").GetString());
            Assert.Equal("SCOPE_NOT_ALLOWED", (await Reject(new { name = "n", scopes = new[] { "users.manage" } })).GetProperty("errorCode").GetString());
            Assert.Equal("SCOPES_REQUIRED", (await Reject(new { name = "n", scopes = Array.Empty<string>() })).GetProperty("errorCode").GetString());
            Assert.Equal("NAME_REQUIRED", (await Reject(new { name = " ", scopes = new[] { "receipts.view" } })).GetProperty("errorCode").GetString());
            Assert.Equal("EXPIRY_INVALID", (await Reject(new { name = "n", scopes = new[] { "receipts.view" }, expiresAt = DateTime.UtcNow.AddDays(-1) })).GetProperty("errorCode").GetString());

            // the secret exists only in the creation response; listings never show it
            var list = await co.Admin.GetStringAsync("/api/v1/tokens");
            Assert.DoesNotContain(full.Secret, list);
            Assert.DoesNotContain(full.Secret[5..], list);
            var tokens = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/tokens?deviceId={dev}", J);
            Assert.Equal(2, tokens.GetArrayLength());
            Assert.All(tokens.EnumerateArray(), t => Assert.False(t.TryGetProperty("secret", out _)));
            Assert.Equal("Ada Admin", tokens[0].GetProperty("createdBy").GetString());

            var scopes = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/tokens/scopes", J);
            Assert.Equal(Perm.TokenScopes.Count, scopes.GetArrayLength());

            // an expiring token stops on time
            var shortLived = await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/tokens", new { name = "short", scopes = new[] { "receipts.view" }, expiresAt = DateTime.UtcNow.AddMinutes(5) });
            Assert.Equal(HttpStatusCode.OK, shortLived.StatusCode);
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var created = await shortLived.Json();
            var keyId = created.GetProperty("token").GetProperty("id").GetGuid();
            var secret = created.GetProperty("secret").GetString()!;
            var c = _f.CreateClient(); c.DefaultRequestHeaders.Add("X-Api-Key", secret);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/receipts")).StatusCode);
            (await db.ApiKeys.FirstAsync(k => k.Id == keyId)).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/receipts")).StatusCode);
        }

        [Fact]
        public async Task Suspending_a_company_or_disabling_a_device_cuts_off_people_and_tokens()
        {
            var op = await TestKit.AdminAsync(_f);
            var co = await TestKit.NewCompanyAsync(_f, "Suspend Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var token = await TestKit.NewTokenAsync(_f, co.Admin, dev);
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync("/api/v1/devices")).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await op.PutAsJsonAsync($"/api/v1/clients/{co.ClientId}/active", new { active = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await co.Admin.GetAsync("/api/v1/devices")).StatusCode);          // existing sign-in
            Assert.Equal(HttpStatusCode.Unauthorized, (await token.Http.GetAsync("/api/v1/devices")).StatusCode);        // its device token
            Assert.Equal("CLIENT_SUSPENDED", (await LoginBody(co.AdminEmail, TestKit.UserPassword, HttpStatusCode.Forbidden)).GetProperty("errorCode").GetString());

            Assert.Equal(HttpStatusCode.OK, (await op.PutAsJsonAsync($"/api/v1/clients/{co.ClientId}/active", new { active = true })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync("/api/v1/devices")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await token.Http.GetAsync("/api/v1/devices")).StatusCode);

            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            (await db.Devices.FirstAsync(d => d.Id == dev)).IsActive = false;
            await db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, (await token.Http.GetAsync("/api/v1/devices")).StatusCode);
        }

        // ---- audit, dashboard, menus -------------------------------------------------------------------------------

        [Fact]
        public async Task The_audit_log_records_who_did_it_and_the_operator_can_narrow_it_to_one_company()
        {
            var a = await TestKit.NewCompanyAsync(_f, "Audit A");
            var b = await TestKit.NewCompanyAsync(_f, "Audit B");
            var (dev, _) = await TestKit.RegisterDeviceAsync(a.Admin, Interlocked.Increment(ref _seq));
            await TestKit.NewTokenAsync(_f, a.Admin, dev, name: "erp-prod");
            await TestKit.RegisterDeviceAsync(b.Admin, Interlocked.Increment(ref _seq));

            var op = await TestKit.AdminAsync(_f);
            var rowsA = await op.GetFromJsonAsync<JsonElement>($"/api/v1/audit?take=200&clientId={a.ClientId}", J);
            var create = rowsA.EnumerateArray().First(r => r.GetProperty("action").GetString() == "token.create");
            Assert.Equal(a.AdminEmail, create.GetProperty("userName").GetString());
            Assert.Contains("erp-prod", create.GetProperty("detail").GetString());
            Assert.DoesNotContain(rowsA.EnumerateArray(), r => r.TryGetProperty("clientName", out var cn) && cn.GetString() == "Audit B");

            var all = await op.GetFromJsonAsync<JsonElement>("/api/v1/audit?take=500&search=device", J);
            var names = all.EnumerateArray().Select(r => r.TryGetProperty("clientName", out var n) ? n.GetString() : null).ToList();
            Assert.Contains("Audit A", names); Assert.Contains("Audit B", names);
        }

        [Fact]
        public async Task The_dashboard_summarises_the_companys_devices_and_todays_receipts()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Dash Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var token = await TestKit.NewTokenAsync(_f, co.Admin, dev);
            await token.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("DB1"));
            await token.Http.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("DB2"));

            var d = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", J);
            Assert.Equal(1, d.GetProperty("devices").GetInt32());
            Assert.Equal(1, d.GetProperty("openDays").GetInt32());
            Assert.Equal(2, d.GetProperty("receiptsToday").GetInt32());
            Assert.Equal(230m, d.GetProperty("todayTotals")[0].GetProperty("total").GetDecimal());
            Assert.Equal("FiscalDayOpened", d.GetProperty("deviceList")[0].GetProperty("dayStatus").GetString());
            Assert.Equal(2, d.GetProperty("recent").GetArrayLength());
        }

        [Fact]
        public async Task Menus_can_be_edited_and_validated_and_protected_pages_cannot_be_deleted()
        {
            var op = await TestKit.AdminAsync(_f);
            var tree = await op.GetFromJsonAsync<JsonElement>("/api/v1/modules", J);
            var sectionId = tree.GetProperty("sections")[0].GetProperty("id").GetInt32();

            async Task<HttpResponseMessage> Save(object m) => await op.PutAsJsonAsync("/api/v1/modules/modules", m);
            Assert.Equal(HttpStatusCode.BadRequest, (await Save(new { id = 0, sectionModuleId = sectionId, name = "Bad", slugname = "/Bad Route", icon = "home", sortOrder = 9, isActive = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Save(new { id = 0, sectionModuleId = sectionId, name = "Bad", slugname = "bad-route", icon = "<script>", sortOrder = 9, isActive = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Save(new { id = 0, sectionModuleId = sectionId, name = "Dupe", slugname = "devices", icon = "home", sortOrder = 9, isActive = true })).StatusCode);

            var saved = await Save(new { id = 0, sectionModuleId = sectionId, name = "Insights", slugname = "insights", icon = "chart", accessName = "receipts.view", sortOrder = 9, isActive = true });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var moduleId = (await saved.Json()).GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.OK, (await op.PutAsJsonAsync("/api/v1/modules/submodules", new { id = 0, moduleId, name = "Daily", icon = "calendar", route = "insights/daily", order = 1, isActive = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/modules/sections", new { id = 0, name = "X", color = "blue", sortOrder = 1, status = true })).StatusCode);

            // give it to Viewer: the entry (with its sub-entry) appears in a viewer's menu on the next sign-in
            var co = await TestKit.NewCompanyAsync(_f, "Edit Menu Co");
            var email = TestKit.Unique("viewer") + "@users.test";
            await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "V", surname = "W", role = "Viewer", password = TestKit.UserPassword });
            Assert.DoesNotContain("insights", Slugs(await LoginBody(email, TestKit.UserPassword)));

            var viewerId = (await op.GetFromJsonAsync<JsonElement>("/api/v1/roles", J)).EnumerateArray().First(r => r.GetProperty("name").GetString() == "Viewer").GetProperty("id").GetString()!;
            var current = (await op.GetFromJsonAsync<JsonElement>($"/api/v1/roles/{viewerId}/access", J)).GetProperty("moduleIds").EnumerateArray().Select(x => x.GetInt32()).Append(moduleId).ToArray();
            Assert.Equal(HttpStatusCode.NoContent, (await op.PutAsJsonAsync($"/api/v1/roles/{viewerId}/menus", new { ids = current })).StatusCode);
            var after = await LoginBody(email, TestKit.UserPassword);
            Assert.Contains("insights", Slugs(after));
            var reports = after.GetProperty("menus").EnumerateArray().First(m => m.GetProperty("slugname").GetString() == "insights");
            Assert.Equal("insights/daily", reports.GetProperty("children")[0].GetProperty("route").GetString());

            // deactivating the entry hides it again; deleting it removes it for good
            await Save(new { id = moduleId, sectionModuleId = sectionId, name = "Insights", slugname = "insights", icon = "chart", accessName = "receipts.view", sortOrder = 9, isActive = false });
            Assert.DoesNotContain("insights", Slugs(await LoginBody(email, TestKit.UserPassword)));
            Assert.Equal(HttpStatusCode.NoContent, (await op.DeleteAsync($"/api/v1/modules/modules/{moduleId}")).StatusCode);

            // pages needed to administer access are protected; a section with entries cannot be removed
            var rolesPage = tree.GetProperty("modules").EnumerateArray().First(m => m.GetProperty("slugname").GetString() == "admin/roles").GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Conflict, (await op.DeleteAsync($"/api/v1/modules/modules/{rolesPage}")).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await op.DeleteAsync($"/api/v1/modules/sections/{sectionId}")).StatusCode);
        }
    }
}
