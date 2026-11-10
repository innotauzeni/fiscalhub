using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using static Fdms.Tests.TestKit;

namespace Fdms.Tests
{
    /// <summary>What a company pays on top of its plan: extra receipts, SMS through the platform, its own WhatsApp number, and paid add-ons. Prices are the owner's.</summary>
    public class UsageBillingTests : IClassFixture<MessagingFactory>
    {
        private readonly MessagingFactory _f;
        private static int _seq = 95000;
        public UsageBillingTests(MessagingFactory f) => _f = f;

        [Fact]
        public async Task A_developer_plan_bills_each_live_device_and_every_receipt_per_thousand_and_test_devices_are_free()
        {
            var op = await AdminAsync(_f);
            // the seeded developer plan exists, hidden until published
            var seeded = (await op.GetFromJsonAsync<JsonElement>("/api/v1/plans", J)).EnumerateArray().Single(p => p.GetProperty("code").GetString() == "developer");
            Assert.False(seeded.GetProperty("isPublic").GetBoolean());
            Assert.Equal(5m, seeded.GetProperty("pricePerDeviceMonthly").GetDecimal());

            var plan = await (await op.PutAsJsonAsync("/api/v1/plans", new { id = 0, code = "devx" + Interlocked.Increment(ref _seq), name = "Dev X", priceMonthly = 0, priceYearly = 0, currency = "USD", maxDevices = 0, maxUsers = 5, maxBranches = 0,
                maxReceiptsPerMonth = 0, pricePerDeviceMonthly = 5, receiptPricing = "metered", pricePerThousandReceipts = 10, features = new string[0], trialDays = 0, isActive = true, isPublic = false, sortOrder = 51 })).Json();
            Assert.Equal(10m, plan.GetProperty("pricePerThousandReceipts").GetDecimal());
            var co = await NewCompanyAsync(_f, "Software House");
            Assert.True((await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = plan.GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" })).IsSuccessStatusCode);
            await ApproveLiveAsync(_f, co.ClientId);   // live devices come after ZIMRA's go-live process

            // two live devices and one test device; three receipts
            Guid first = default;
            foreach (var (serial, env) in new[] { ("SN-D1", "Production"), ("SN-D2", "Production"), ("SN-D3", "Test") })
            {
                var r = await co.Admin.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = Interlocked.Increment(ref _seq), activationKey = "ABCD1234", serialNo = serial, modelName = "Server", modelVersion = "v1", environment = env });
                Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
                if (first == default) first = (await r.Json()).GetProperty("id").GetGuid();
            }
            foreach (var no in new[] { "D-1", "D-2", "D-3" })
                Assert.True((await co.Admin.PostAsJsonAsync($"/api/v1/devices/{first}/receipts", new { externalRef = no, invoiceNo = no, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, autoOpenDay = true,
                    lines = new[] { new { name = "Item", hsCode = "64039900", quantity = 1, unitPrice = 10, taxPercent = 15 } } })).IsSuccessStatusCode);

            // 2 live devices x 5.00 + 3 receipts x 10.00 / 1,000 = 10.03 (the test device is free)
            var invoice = await (await op.PostAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}/invoice", null)).Json();
            Assert.Contains("2 live device(s)", invoice.GetProperty("description").GetString());
            Assert.Contains("3 receipt(s) at 10 per 1,000", invoice.GetProperty("description").GetString());
            Assert.Equal(10.03m, invoice.GetProperty("subtotal").GetDecimal());
        }

        private async Task<(Company Co, Guid Device)> MeteredCompanyAsync(HttpClient op, object plan, int receipts)
        {
            var made = await op.PutAsJsonAsync("/api/v1/plans", plan);
            Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
            var co = await NewCompanyAsync(_f, "Volume Co " + Interlocked.Increment(ref _seq));
            Assert.True((await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = (await made.Json()).GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" })).IsSuccessStatusCode);
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-V");
            for (var i = 0; i < receipts; i++)
                Assert.True((await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new { externalRef = $"V-{i}", invoiceNo = $"V-{i}", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, autoOpenDay = true,
                    lines = new[] { new { name = "Item", hsCode = "64039900", quantity = 1, unitPrice = 10, taxPercent = 15 } } })).IsSuccessStatusCode);
            return (co, dev);
        }

        private static object PlanBody(string code, string pricing, object? tiers = null, decimal perThousand = 0, decimal min = 0, decimal cap = 0) => new
        {
            id = 0, code, name = code, priceMonthly = 0, priceYearly = 0, currency = "USD", maxDevices = 0, maxUsers = 0, maxBranches = 0, maxReceiptsPerMonth = 0,
            receiptPricing = pricing, receiptTiers = tiers, pricePerThousandReceipts = perThousand, receiptMinimumMonthly = min, receiptCapMonthly = cap,
            features = new string[0], trialDays = 0, isActive = true, isPublic = false, sortOrder = 52,
        };

        [Fact]
        public async Task Receipts_can_be_priced_flat_per_thousand_with_a_monthly_minimum_and_cap_or_by_volume_band()
        {
            var op = await AdminAsync(_f);
            // bands must go up, and only the last can be open-ended
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/plans", PlanBody("bad" + _seq, "tiered", new[] { new { upTo = (int?)5000, price = 30m }, new { upTo = (int?)1000, price = 10m } }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/plans", PlanBody("bad2" + _seq, "tiered"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/plans", PlanBody("bad3" + _seq, "metered", perThousand: 10, min: 20, cap: 5))).StatusCode);

            // volume bands: 3 receipts in the month fall in "up to 5" -> 12.00 for the month
            var bands = new[] { new { upTo = (int?)2, price = 7m }, new { upTo = (int?)5, price = 12m }, new { upTo = (int?)null, price = 20m } };
            var (tiered, _) = await MeteredCompanyAsync(op, PlanBody("tier" + Interlocked.Increment(ref _seq), "tiered", bands), 3);
            var t = await (await op.PostAsync($"/api/v1/admin/billing/subscriptions/{tiered.ClientId}/invoice", null)).Json();
            Assert.Equal(12m, t.GetProperty("subtotal").GetDecimal());
            Assert.Contains("band up to 5", t.GetProperty("description").GetString());

            // per 1,000 with a monthly minimum: 3 receipts cost 0.03, so the minimum (15.00) applies
            var (minimum, _) = await MeteredCompanyAsync(op, PlanBody("min" + Interlocked.Increment(ref _seq), "metered", perThousand: 10, min: 15), 3);
            var m = await (await op.PostAsync($"/api/v1/admin/billing/subscriptions/{minimum.ClientId}/invoice", null)).Json();
            Assert.Equal(15m, m.GetProperty("subtotal").GetDecimal());
            Assert.Contains("monthly minimum", m.GetProperty("description").GetString());

            // per 1,000 with a monthly cap: 3 receipts at 1,000 per 1,000 would be 3.00, capped at 2.00
            var (capped, _) = await MeteredCompanyAsync(op, PlanBody("cap" + Interlocked.Increment(ref _seq), "metered", perThousand: 1000, cap: 2), 3);
            Assert.Equal(2m, (await (await op.PostAsync($"/api/v1/admin/billing/subscriptions/{capped.ClientId}/invoice", null)).Json()).GetProperty("subtotal").GetDecimal());

            // flat: receipts cost nothing extra
            var (flat, _) = await MeteredCompanyAsync(op, PlanBody("flat" + Interlocked.Increment(ref _seq), "flat", perThousand: 10), 3);
            Assert.Equal(0m, (await (await op.PostAsync($"/api/v1/admin/billing/subscriptions/{flat.ClientId}/invoice", null)).Json()).GetProperty("subtotal").GetDecimal());

            // the plan says how it charges
            var listed = (await op.GetFromJsonAsync<JsonElement>("/api/v1/plans", J)).EnumerateArray().First(p => p.GetProperty("receiptPricing").GetString() == "tiered");
            Assert.Equal(3, listed.GetProperty("receiptTiers").GetArrayLength());
        }

        [Fact]
        public async Task A_resellers_companies_pay_the_reseller_price_and_the_reseller_earns_no_commission()
        {
            var op = await AdminAsync(_f);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/partners", new { id = (Guid?)null, name = "Too Generous", commissionPercent = 0, isActive = true, kind = "reseller", discountPercent = 95 })).StatusCode);
            var reseller = await (await op.PutAsJsonAsync("/api/v1/partners", new { id = (Guid?)null, name = "Tech Resellers " + Interlocked.Increment(ref _seq), code = (string?)null, contactName = "R", email = "r@reseller.test",
                phone = "0770000000", commissionPercent = 10m, isActive = true, notes = (string?)null, kind = "reseller", discountPercent = 20m })).Json();
            Assert.Equal("reseller", reseller.GetProperty("kind").GetString());
            Assert.Equal(0m, reseller.GetProperty("commissionPercent").GetDecimal());   // a reseller earns through its own prices

            var plan = await (await op.PutAsJsonAsync("/api/v1/plans", new { id = 0, code = "rsl" + Interlocked.Increment(ref _seq), name = "Resold", priceMonthly = 50, priceYearly = 500, currency = "USD", maxDevices = 0, maxUsers = 0, maxBranches = 0,
                maxReceiptsPerMonth = 0, features = new string[0], trialDays = 0, isActive = true, isPublic = false, sortOrder = 53 })).Json();
            var co = await NewCompanyAsync(_f, "Resold Co " + _seq);
            Assert.True((await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = plan.GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" })).IsSuccessStatusCode);
            var link = await (await op.PutAsJsonAsync($"/api/v1/partners/clients/{co.ClientId}", new { partnerId = reseller.GetProperty("id").GetGuid(), percent = 15m })).Json();
            Assert.Equal(0m, link.GetProperty("percent").GetDecimal());

            // 50.00 less 20% = 40.00, and the company sees why
            var invoice = await (await op.PostAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}/invoice", null)).Json();
            Assert.Equal(40m, invoice.GetProperty("subtotal").GetDecimal());
            Assert.Contains("reseller price", invoice.GetProperty("description").GetString());
            var overview = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing", J);
            Assert.Equal(20m, overview.GetProperty("resellerDiscountPercent").GetDecimal());
            Assert.StartsWith("Tech Resellers", overview.GetProperty("resellerName").GetString());

            // paid: the reseller earns nothing from the platform
            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{invoice.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" })).IsSuccessStatusCode);
            var partners = await op.GetFromJsonAsync<JsonElement>("/api/v1/partners", J);
            var p = partners.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == reseller.GetProperty("id").GetGuid());
            Assert.Empty(p.GetProperty("earned").EnumerateArray());
        }

        [Fact]
        public async Task Extra_receipts_SMS_an_own_WhatsApp_number_and_add_ons_are_billed_at_the_owners_prices_exactly_once()
        {
            var op = await AdminAsync(_f);
            // the owner's prices: flexible, validated, and only theirs to set
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/plans/usage-pricing", new { charge = true, currency = "US", smsPrice = 0.05, whatsAppNumberMonthly = 10, extraDeviceMonthly = 4, extraUserMonthly = 2, extraBranchMonthly = 3 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PutAsJsonAsync("/api/v1/plans/usage-pricing", new { charge = true, currency = "USD", smsPrice = -1, whatsAppNumberMonthly = 10, extraDeviceMonthly = 4, extraUserMonthly = 2, extraBranchMonthly = 3 })).StatusCode);
            var prices = await (await op.PutAsJsonAsync("/api/v1/plans/usage-pricing", new { charge = true, currency = "usd", smsPrice = 0.05, whatsAppNumberMonthly = 10, extraDeviceMonthly = 4, extraUserMonthly = 2, extraBranchMonthly = 3 })).Json();
            Assert.Equal("USD", prices.GetProperty("currency").GetString());

            // a plan with 1 receipt a month included and 0.50 for each extra one
            var code = "meter" + Interlocked.Increment(ref _seq);
            var plan = await (await op.PutAsJsonAsync("/api/v1/plans", new { id = 0, code, name = "Metered", priceMonthly = 20, priceYearly = 200, currency = "USD", maxDevices = 1, maxUsers = 5, maxBranches = 1,
                maxReceiptsPerMonth = 1, pricePerExtraReceipt = 0.5, features = new string[0], trialDays = 0, isActive = true, isPublic = false, sortOrder = 50 })).Json();
            Assert.Equal(0.5m, plan.GetProperty("pricePerExtraReceipt").GetDecimal());

            var co = await NewCompanyAsync(_f, "Metered Co");
            Assert.Equal(HttpStatusCode.Forbidden, (await co.Admin.GetAsync("/api/v1/plans/usage-pricing")).StatusCode);
            Assert.True((await op.PutAsJsonAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}", new { planId = plan.GetProperty("id").GetInt32(), status = "Active", billingCycle = "monthly" })).IsSuccessStatusCode);
            var (dev, _) = await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-M1");

            // add-ons: the plan allows 1 device; 2 more are bought, and only work once their invoice is paid
            var addons = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/addons", J);
            Assert.True(addons.GetProperty("available").GetBoolean(), addons.GetRawText());
            Assert.Equal(4m, addons.GetProperty("devicePrice").GetDecimal());
            var bought = await (await co.Admin.PostAsJsonAsync("/api/v1/billing/addons", new { devices = 2, users = 0, branches = 0 })).Json();
            var addonInvoice = bought.GetProperty("pendingInvoiceId").GetGuid();
            Assert.Equal(0, bought.GetProperty("devices").GetInt32());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/billing/addons", new { devices = 1, users = 0, branches = 0 })).StatusCode);   // one waiting invoice at a time
            var second = await co.Admin.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = Interlocked.Increment(ref _seq), activationKey = "ABCD1234", serialNo = "SN-M2", modelName = "Server", modelVersion = "v1", environment = "Test" });
            Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
            var addonInv = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/billing/invoices/{addonInvoice}", J);
            Assert.Equal(8m, addonInv.GetProperty("subtotal").GetDecimal());   // 2 devices x 4.00 x 1 month left
            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{addonInvoice}/mark-paid", new { method = "Cash" })).IsSuccessStatusCode);
            Assert.Equal(2, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/addons", J)).GetProperty("devices").GetInt32());
            await RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-M2");

            // usage: 3 receipts (2 above the allowance), 2 SMS through the platform's account, and the company's own WhatsApp number
            foreach (var no in new[] { "M-1", "M-2", "M-3" })
            {
                var r = await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new { externalRef = no, invoiceNo = no, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, autoOpenDay = true,
                    lines = new[] { new { name = "Item", hsCode = "64039900", quantity = 1, unitPrice = 10, taxPercent = 15 } } });
                Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
            }
            Assert.True((await op.PutAsJsonAsync("/api/v1/admin/sms", new { enabled = true, provider = "NHUME", apiKey = "k", senderId = "FiscalHub", allowCompanies = true })).IsSuccessStatusCode);
            var receipt = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/receipts?search=M-1", J)).GetProperty("items")[0].GetProperty("id").GetGuid();
            for (var i = 0; i < 2; i++) Assert.True((await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{receipt}/sms", new { phone = "+263771000" + i + "00" })).Json()).GetProperty("sent").GetBoolean());
            Assert.True((await co.Admin.PutAsJsonAsync("/api/v1/messaging/whatsapp", new { enabled = true, phoneNumberId = "6660001" + _seq, businessAccountId = "1", displayNumber = "+263 77 666 0001",
                accessToken = "t", appSecret = "s", apiVersion = "v21.0", alertTemplateLanguage = "en", rotateVerifyToken = false, customersEnabled = false })).IsSuccessStatusCode);

            // the renewal invoice: plan 20 + 2 x 0.50 + 2 x 0.05 + 10 + 2 x 4 = 39.10
            var renewal = await (await op.PostAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}/invoice", null)).Json();
            var text = renewal.GetProperty("description").GetString()!;
            Assert.Contains("2 extra receipt(s)", text);
            Assert.Contains("2 SMS", text);
            Assert.Contains("own WhatsApp number", text);
            Assert.Contains("2 extra device(s)", text);
            Assert.Equal(39.10m, renewal.GetProperty("subtotal").GetDecimal());

            // billed once: the next invoice charges the period ahead again, but not the same receipts and SMS
            Assert.True((await op.PostAsJsonAsync($"/api/v1/admin/billing/invoices/{renewal.GetProperty("id").GetGuid()}/mark-paid", new { method = "Cash" })).IsSuccessStatusCode);
            var nextRes = await op.PostAsync($"/api/v1/admin/billing/subscriptions/{co.ClientId}/invoice", null);
            Assert.True(nextRes.IsSuccessStatusCode, await nextRes.Content.ReadAsStringAsync());
            var next = await nextRes.Json();
            Assert.DoesNotContain("extra receipt", next.GetProperty("description").GetString());
            Assert.DoesNotContain("SMS", next.GetProperty("description").GetString());
            Assert.Equal(38m, next.GetProperty("subtotal").GetDecimal());   // 20 + 10 + 8

            // add-ons in use cannot be dropped; one that is free can
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync("/api/v1/billing/addons/remove", new { devices = 2, users = 0, branches = 0 })).StatusCode);
            Assert.Equal(1, (await (await co.Admin.PostAsJsonAsync("/api/v1/billing/addons/remove", new { devices = 1, users = 0, branches = 0 })).Json()).GetProperty("devices").GetInt32());

            // a waiting add-on invoice can be cancelled
            await co.Admin.PostAsJsonAsync("/api/v1/billing/addons", new { devices = 0, users = 1, branches = 0 });
            var cancelled = await (await co.Admin.DeleteAsync("/api/v1/billing/addons/pending")).Json();
            Assert.False(cancelled.TryGetProperty("pendingInvoiceId", out var pending) && pending.ValueKind != JsonValueKind.Null);   // null fields are left out

            // prices switched off: no usage charges, no add-ons on offer
            await op.PutAsJsonAsync("/api/v1/plans/usage-pricing", new { charge = false, currency = "USD", smsPrice = 0.05, whatsAppNumberMonthly = 10, extraDeviceMonthly = 4, extraUserMonthly = 2, extraBranchMonthly = 3 });
            Assert.False((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/billing/addons", J)).GetProperty("available").GetBoolean());
        }
    }
}
