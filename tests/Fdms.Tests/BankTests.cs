using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Fdms.Tests
{
    /// <summary>Banks and their accounts per company, and the platform's currency list that feeds every currency pick list.</summary>
    public class BankTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        public BankTests(AccessFactory f) => _f = f;

        [Fact]
        public async Task A_company_keeps_banks_with_their_accounts_in_listed_currencies_and_nobody_else_sees_them()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Bank Co");
            var other = await TestKit.NewCompanyAsync(_f, "Bank Other");

            var nmb = await (await co.Admin.PutAsJsonAsync("/api/v1/banks", new { name = "NMB", code = "NMBLZWHX", isActive = true })).Json();
            var bankId = nmb.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/banks", new { name = "NMB", isActive = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/banks", new { name = "N", isActive = true })).StatusCode);

            object Acc(string number, string cur, bool def = false, Guid? bank = null) => new { bankId = bank ?? bankId, accountName = "Bank Co Pvt Ltd", accountNumber = number, branchName = "Harare", branchCode = "1234", currency = cur, isDefault = def, isActive = true };
            await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", Acc("40123456789", "USD", true));
            var after = await (await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", Acc("40123456790", "USD", true))).Json();
            var accounts = after.GetProperty("accounts").EnumerateArray().ToList();
            Assert.Equal(2, accounts.Count);
            Assert.Single(accounts, a => a.GetProperty("isDefault").GetBoolean());                                   // one default per currency
            Assert.Equal("40123456790", accounts.First(a => a.GetProperty("isDefault").GetBoolean()).GetProperty("accountNumber").GetString());
            await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", Acc("40999999999", "ZWG"));

            // refused: unknown currency, a bad number, a duplicate, a bank that is not theirs
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", Acc("40111111111", "XXX"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", Acc("12", "USD"))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", Acc("40123456789", "USD"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PutAsJsonAsync("/api/v1/banks/accounts", Acc("40222222222", "USD", false, bankId))).StatusCode);

            // the pick list: the bank with its accounts
            var list = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/banks", J);
            Assert.Equal("NMB", list[0].GetProperty("name").GetString()); Assert.Equal(3, list[0].GetProperty("accounts").GetArrayLength());
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/banks", J)).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.DeleteAsync($"/api/v1/banks/{bankId}")).StatusCode);

            // a viewer may read the list (to pick payment details) but not change it
            var viewer = await NewViewerAsync(co);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/banks")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/v1/banks", new { name = "CBZ", isActive = true })).StatusCode);

            // deleting a bank removes its accounts
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/banks/{bankId}")).StatusCode);
            Assert.Empty((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/banks", J)).EnumerateArray());
        }

        private async Task<HttpClient> NewViewerAsync(TestKit.Company co)
        {
            var email = TestKit.Unique("viewer") + "@bank.test";
            var res = await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "Vera", surname = "Viewer", role = "Viewer", password = TestKit.UserPassword });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
        }

        [Fact]
        public async Task The_platform_currency_list_is_seeded_read_by_everyone_and_changed_only_by_the_operator()
        {
            var op = await TestKit.AdminAsync(_f);
            var co = await TestKit.NewCompanyAsync(_f, "Currency Co");
            var codes = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/currencies", J)).EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToList();
            Assert.Contains("USD", codes); Assert.Contains("ZWG", codes);

            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.PutAsJsonAsync("/api/v1/currencies", new { code = "MZN", name = "Metical", symbol = "MT", isActive = true, sortOrder = 9 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/currencies/all")).StatusCode);

            var put = await op.PutAsJsonAsync("/api/v1/currencies", new { code = "mzn", name = "Mozambican Metical", symbol = "MT", isActive = true, sortOrder = 9 }); Assert.True(put.IsSuccessStatusCode, "STATUS " + (int)put.StatusCode + " " + await put.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/currencies", new { code = "TOOLONG", name = "x", isActive = true, sortOrder = 1 })).StatusCode);
            Assert.Contains("MZN", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/currencies", J)).EnumerateArray().Select(c => c.GetProperty("code").GetString()));

            // switched off: no longer offered, and no new account may use it; a currency in use cannot be deleted
            await op.PutAsJsonAsync("/api/v1/currencies", new { code = "MZN", name = "Mozambican Metical", symbol = "MT", isActive = false, sortOrder = 9 });
            Assert.DoesNotContain("MZN", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/currencies", J)).EnumerateArray().Select(c => c.GetProperty("code").GetString()));
            var bank = await (await co.Admin.PutAsJsonAsync("/api/v1/banks", new { name = "CBZ", isActive = true })).Json();
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", new { bankId = bank.GetProperty("id").GetGuid(), accountName = "Currency Co", accountNumber = "5000111122", currency = "MZN", isActive = true })).StatusCode);
            await co.Admin.PutAsJsonAsync("/api/v1/banks/accounts", new { bankId = bank.GetProperty("id").GetGuid(), accountName = "Currency Co", accountNumber = "5000111122", currency = "ZWG", isActive = true });
            Assert.Equal(HttpStatusCode.Conflict, (await op.DeleteAsync("/api/v1/currencies/ZWG")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await op.DeleteAsync("/api/v1/currencies/MZN")).StatusCode);
        }
    }
}
