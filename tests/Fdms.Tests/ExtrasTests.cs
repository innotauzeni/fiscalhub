using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Application.Fiscal;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Fdms.Tests
{
    public sealed class RecordingEmail : IEmailSender
    {
        public sealed record Sent(string To, string Subject, string Html, IReadOnlyList<EmailAttachment> Files);
        public List<Sent> Messages { get; } = new();
        public bool IsConfigured { get; set; } = true;
        /// <summary>When set, the mail server "refuses" every message with this text.</summary>
        public string? FailWith { get; set; }
        /// <summary>With FailWith: true = a temporary refusal ("try later"), false = a final one.</summary>
        public bool FailTemporarily { get; set; }
        public Task<EmailResult> DeliverAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct)
        {
            if (FailWith != null) return Task.FromResult(new EmailResult(false, FailTemporarily, FailWith));
            lock (Messages) Messages.Add(new Sent(to, subject, htmlBody, attachments)); return Task.FromResult(new EmailResult(true, false, null));
        }
        public async Task<string?> TrySendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct) => (await DeliverAsync(to, subject, htmlBody, attachments, ct)).Error;
        public bool Configured => IsConfigured;
        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct) { lock (Messages) Messages.Add(new Sent(to, subject, htmlBody, Array.Empty<EmailAttachment>())); return Task.CompletedTask; }
        public Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct) { lock (Messages) Messages.Add(new Sent(to, subject, htmlBody, attachments)); return Task.CompletedTask; }
    }

    public sealed class ExtrasFactory : WebApplicationFactory<Program>
    {
        public FakeFdmsGateway Fdms { get; } = new();
        public RecordingEmail Email { get; } = new();
        private readonly string _db = Guid.NewGuid().ToString();
        private readonly string _keys = Path.Combine(Path.GetTempPath(), "fdms-test-keys-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestKit.JwtKey, ["Seed:AdminEmail"] = TestKit.AdminEmail, ["Seed:AdminPassword"] = TestKit.AdminPassword, ["Seed:ForcePasswordChange"] = "false",
                ["DataProtection:KeysPath"] = _keys, ["RateLimit:PerMinute"] = "100000", ["RateLimit:LoginPerMinute"] = "100000", ["Fdms:DistributedLocks"] = "false", ["Auth:ActiveCacheSeconds"] = "0",
            }));
            builder.ConfigureServices(s =>
            {
                TestDb.Use(s, _db);
                s.RemoveAll<IFdmsGateway>(); s.AddSingleton<IFdmsGateway>(Fdms);
                s.RemoveAll<IEmailSender>(); s.AddSingleton<IEmailSender>(Email);
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            });
        }
    }

    /// <summary>Sending invoices, alerts, health, quotes, recurring invoices, the VAT export, plan usage, suggestions, branding and the getting-started list.</summary>
    public class ExtrasTests : IClassFixture<ExtrasFactory>
    {
        private readonly ExtrasFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 31000;
        public ExtrasTests(ExtrasFactory f) => _f = f;

        private static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

        private async Task<(TestKit.Company Co, Guid Dev)> SetupAsync(string name)
        {
            var co = await TestKit.NewCompanyAsync(_f, name);
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-X");
            return (co, dev);
        }

        private static async Task<JsonElement> InvoiceAsync(HttpClient c, Guid dev, string no, decimal price = 115, string? buyerEmail = null)
        {
            var res = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = no, invoiceNo = no, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = price, taxPercent = 15 } },
                buyer = buyerEmail == null ? null : new { registerName = "Moyo Retailers", tin = "2001234567", email = buyerEmail },
            });
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            return await res.Json();
        }

        private async Task DbAsync(Func<FdmsDbContext, Task> work)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            await work(db);
            await db.SaveChangesAsync();
        }

        // ---- send an invoice to the customer ----------------------------------------------------------------------------------

        [Fact]
        public async Task An_invoice_can_be_shared_by_link_and_emailed_and_the_link_shows_only_that_invoice()
        {
            var (co, dev) = await SetupAsync("Share Co");
            var inv = await InvoiceAsync(co.Admin, dev, "SH-1", buyerEmail: "buyer@moyo.test");
            var id = inv.GetProperty("id").GetGuid();

            var link = await (await co.Admin.PostAsync($"/api/v1/receipts/{id}/share", null)).Json();
            var token = link.GetProperty("token").GetString()!;
            Assert.Contains("/invoice/", link.GetProperty("url").GetString());
            Assert.Contains(link.GetProperty("url").GetString()!, link.GetProperty("whatsAppText").GetString());

            // no sign-in needed
            var anon = _f.CreateClient();
            var page = await anon.GetFromJsonAsync<JsonElement>($"/api/v1/public/invoices/{token}", J);
            Assert.Equal("SH-1", page.GetProperty("invoiceNo").GetString());
            Assert.Equal("Submitted", page.GetProperty("status").GetString());
            Assert.Equal("Share Co", page.GetProperty("companyName").GetString());
            Assert.True(Has(page, "qrUrl"));
            var pdf = await anon.GetAsync($"/api/v1/public/invoices/{token}/pdf");
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
            Assert.Equal("image/png", (await anon.GetAsync($"/api/v1/public/invoices/{token}/qr")).Content.Headers.ContentType?.MediaType);

            // a damaged or invented link opens nothing
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/public/invoices/{token[..^6]}XXXXXX")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/public/invoices/not-a-token")).StatusCode);

            // another company cannot make a link for it
            var other = await TestKit.NewCompanyAsync(_f, "Share Other");
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsync($"/api/v1/receipts/{id}/share", null)).StatusCode);

            // email: goes to the buyer's address by default, with the PDF attached
            var sent = await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{id}/email", new { message = "Thank you" })).Json();
            Assert.True(sent.GetProperty("sent").GetBoolean());
            var mail = _f.Email.Messages.Last();
            Assert.Equal("buyer@moyo.test", mail.To);
            Assert.Contains("SH-1", mail.Subject);
            Assert.Contains("/invoice/", mail.Html);
            Assert.Single(mail.Files); Assert.EndsWith(".pdf", mail.Files[0].FileName); Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(mail.Files[0].Bytes.Take(4).ToArray()));

            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{id}/email", new { email = "not-an-email" })).StatusCode);
            var noBuyer = await InvoiceAsync(co.Admin, dev, "SH-2");
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{noBuyer.GetProperty("id").GetGuid()}/email", new { })).StatusCode);   // nobody to send it to

            // the mail server refuses it: the answer says so (and why) instead of claiming it was sent
            _f.Email.FailWith = "Temporary local problem - please try later";
            try
            {
                var refused = await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{id}/email", new { email = "x@y.test" })).Json();
                Assert.False(refused.GetProperty("sent").GetBoolean()); Assert.Contains("Temporary local problem", refused.GetProperty("message").GetString());
            }
            finally { _f.Email.FailWith = null; }

            // email not set up on the server: said plainly, nothing pretended
            _f.Email.IsConfigured = false;
            try
            {
                var no = await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{id}/email", new { email = "x@y.test" })).Json();
                Assert.False(no.GetProperty("sent").GetBoolean()); Assert.False(no.GetProperty("emailConfigured").GetBoolean());
            }
            finally { _f.Email.IsConfigured = true; }
        }

        [Fact]
        public async Task An_email_the_mail_server_cannot_take_yet_is_queued_retried_on_a_schedule_and_given_up_on_with_an_alert()
        {
            var (co, dev) = await SetupAsync("Outbox Co");
            var inv = await InvoiceAsync(co.Admin, dev, "OB-1", buyerEmail: "buyer@outbox.test");
            var id = inv.GetProperty("id").GetGuid();
            var outbox = _f.Services.CreateScope().ServiceProvider.GetRequiredService<IEmailOutbox>();
            async Task<JsonElement[]> Waiting() => (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications/outbox", J)).EnumerateArray().ToArray();

            // a final refusal (bad mailbox) is reported and not retried
            _f.Email.FailWith = "550 No such mailbox"; _f.Email.FailTemporarily = false;
            var final = await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{id}/email", new { })).Json();
            Assert.False(final.GetProperty("sent").GetBoolean()); Assert.False(Has(final, "queued") && final.GetProperty("queued").GetBoolean());
            Assert.Empty(await Waiting());

            // a temporary one ("try later") is queued
            _f.Email.FailTemporarily = true; _f.Email.FailWith = "Temporary local problem - please try later";
            var queued = await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{id}/email", new { })).Json();
            Assert.False(queued.GetProperty("sent").GetBoolean()); Assert.True(queued.GetProperty("queued").GetBoolean());
            var row = Assert.Single(await Waiting());
            Assert.Equal("Pending", row.GetProperty("status").GetString()); Assert.Equal("buyer@outbox.test", row.GetProperty("to").GetString());
            var before = _f.Email.Messages.Count;

            // not due yet: nothing happens; due but the server is still busy: it waits longer (attempt counted)
            Assert.Equal(0, await outbox.RunDueAsync(DateTime.UtcNow, CancellationToken.None));
            Assert.Equal(0, await outbox.RunDueAsync(DateTime.UtcNow.AddMinutes(3), CancellationToken.None));
            row = Assert.Single(await Waiting());
            Assert.Equal(1, row.GetProperty("attempts").GetInt32()); Assert.Equal("Pending", row.GetProperty("status").GetString());

            // the server recovers: the next due pass delivers it, with the PDF still attached
            _f.Email.FailWith = null;
            Assert.Equal(1, await outbox.RunDueAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None));
            row = Assert.Single(await Waiting());
            Assert.Equal("Sent", row.GetProperty("status").GetString());
            var mail = _f.Email.Messages.Skip(before).Single();
            Assert.Equal("buyer@outbox.test", mail.To); Assert.Single(mail.Files); Assert.EndsWith(".pdf", mail.Files[0].FileName);
            Assert.Equal(0, await outbox.RunDueAsync(DateTime.UtcNow.AddDays(2), CancellationToken.None));   // never sent twice

            // a server that never recovers: after the last try it is given up on, and the company is alerted
            _f.Email.FailWith = "Temporary local problem - please try later";
            Assert.Equal("true", (await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{id}/email", new { email = "stuck@outbox.test" })).Json()).GetProperty("queued").GetBoolean().ToString().ToLowerInvariant());
            for (var i = 1; i <= 10; i++) await outbox.RunDueAsync(DateTime.UtcNow.AddDays(i), CancellationToken.None);
            var failed = (await Waiting()).First(r => r.GetProperty("to").GetString() == "stuck@outbox.test");
            Assert.Equal("Failed", failed.GetProperty("status").GetString());
            Assert.Contains((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "email");

            // "try now" puts it back in line; cancel removes it; another company cannot touch it
            _f.Email.FailWith = null;
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.PostAsync($"/api/v1/notifications/outbox/{failed.GetProperty("id").GetGuid()}/retry", null)).StatusCode);
            Assert.Equal(1, await outbox.RunDueAsync(DateTime.UtcNow.AddDays(30), CancellationToken.None));
            var other = await TestKit.NewCompanyAsync(_f, "Outbox Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications/outbox", J)).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.DeleteAsync($"/api/v1/notifications/outbox/{failed.GetProperty("id").GetGuid()}")).StatusCode);
            _f.Email.FailTemporarily = false; _f.Email.FailWith = null;   // the fake is shared with the other tests of this class
        }

        [Fact]
        public async Task A_machine_can_be_given_the_companys_own_name_so_the_machines_of_a_branch_can_be_told_apart()
        {
            var (co, dev) = await SetupAsync("Names Co");
            var (dev2, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-N2");
            var named = await (await co.Admin.PutAsJsonAsync($"/api/v1/devices/{dev}/name", new { name = "  Till 1,   front counter " })).Json();
            Assert.Equal("Till 1, front counter", named.GetProperty("name").GetString());   // tidied
            var list = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/devices", J)).EnumerateArray().ToList();
            Assert.Equal("Till 1, front counter", list.First(d => d.GetProperty("id").GetGuid() == dev).GetProperty("name").GetString());
            Assert.False(Has(list.First(d => d.GetProperty("id").GetGuid() == dev2), "name"));

            // the name shows on the pages that list machines
            var dash = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", J)).GetProperty("deviceList").EnumerateArray().First(d => d.GetProperty("id").GetGuid() == dev);
            Assert.Equal("Till 1, front counter", dash.GetProperty("name").GetString());
            Assert.Equal("Till 1, front counter", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/health", J)).EnumerateArray().First(d => d.GetProperty("deviceId").GetGuid() == dev).GetProperty("name").GetString());

            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync($"/api/v1/devices/{dev2}/name", new { name = "Till 1, front counter" })).StatusCode);   // two machines never share a name
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync($"/api/v1/devices/{dev2}/name", new { name = new string('x', 81) })).StatusCode);
            Assert.False(Has(await (await co.Admin.PutAsJsonAsync($"/api/v1/devices/{dev}/name", new { name = "  " })).Json(), "name"));   // blank clears it

            var opEmail = TestKit.Unique("oper") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email = opEmail, name = "O", surname = "P", role = "Operator", password = TestKit.UserPassword })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await (await TestKit.LoginAsync(_f, opEmail, TestKit.UserPassword)).PutAsJsonAsync($"/api/v1/devices/{dev}/name", new { name = "Mine" })).StatusCode);   // cashiers cannot rename machines
            var other = await TestKit.NewCompanyAsync(_f, "Names Other");
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PutAsJsonAsync($"/api/v1/devices/{dev}/name", new { name = "Stolen" })).StatusCode);
        }

        // ---- alerts ---------------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task Alerts_are_raised_once_emailed_to_the_administrators_and_can_be_switched_off_by_kind()
        {
            var (co, dev) = await SetupAsync("Alert Co");
            await DbAsync(async db => { var d = await db.Devices.FirstAsync(x => x.Id == dev); d.CertificateValidTill = DateTime.UtcNow.AddDays(10); });

            var scan = _f.Services.CreateScope().ServiceProvider.GetRequiredService<INotificationService>();
            var before = _f.Email.Messages.Count;
            Assert.True(await scan.ScanAsync(DateTime.UtcNow, CancellationToken.None) >= 1);
            var list = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J);
            var cert = list.GetProperty("items").EnumerateArray().First(n => n.GetProperty("kind").GetString() == "certificate");
            Assert.Equal("warn", cert.GetProperty("severity").GetString());
            Assert.True(list.GetProperty("unread").GetInt32() >= 1);
            Assert.Contains(_f.Email.Messages.Skip(before), m => m.To == co.AdminEmail && m.Subject.Contains("[FDMS Gateway]"));

            // the same condition is not raised again
            var count = list.GetProperty("items").GetArrayLength();
            Assert.Equal(0, await _f.Services.CreateScope().ServiceProvider.GetRequiredService<INotificationService>().ScanAsync(DateTime.UtcNow, CancellationToken.None) is var again && again >= 0 ? 0 : 1);
            Assert.Equal(count, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").GetArrayLength());

            // read it
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.PostAsJsonAsync("/api/v1/notifications/read", new { })).StatusCode);
            Assert.Equal(0, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("unread").GetInt32());

            // nobody else sees them
            var other = await TestKit.NewCompanyAsync(_f, "Alert Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray());

            // queued for over an hour and rejected: two more kinds
            _f.Fdms.FailNextSubmitTransiently = true;
            var queued = await InvoiceAsync(co.Admin, dev, "AL-Q");
            Assert.Equal("Pending", queued.GetProperty("status").GetString());
            await DbAsync(async db => { var r = await db.Receipts.FirstAsync(x => x.InvoiceNo == "AL-Q"); r.CreatedAt = DateTime.UtcNow.AddHours(-3); });
            await scan.ScanAsync(DateTime.UtcNow, CancellationToken.None);
            Assert.Contains((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "queued");

            // settings: validation, then a kind switched off is not raised
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/notifications/settings", new { emails = "nope", enabledKinds = new[] { "rejected" } })).StatusCode);
            var saved = await (await co.Admin.PutAsJsonAsync("/api/v1/notifications/settings", new { emails = "ops@alert.test, boss@alert.test", enabledKinds = new[] { "rejected" } })).Json();
            Assert.Equal("ops@alert.test, boss@alert.test", saved.GetProperty("emails").GetString());
            Assert.Single(saved.GetProperty("enabledKinds").EnumerateArray());
            var (co2, dev2) = await SetupAsync("Alert Quiet Co");
            await DbAsync(async db => { var d = await db.Devices.FirstAsync(x => x.Id == dev2); d.CertificateValidTill = DateTime.UtcNow.AddDays(5); var c = await db.Clients.FirstAsync(x => x.Id == co2.ClientId); c.AlertKinds = "rejected"; c.AlertEmails = "quiet@alert.test"; });
            await scan.ScanAsync(DateTime.UtcNow, CancellationToken.None);
            Assert.DoesNotContain((await co2.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "certificate");

            // only people allowed to manage alerts can change them
            var email = TestKit.Unique("view") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "V", surname = "W", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var viewer = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/notifications")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/v1/notifications/settings", new { emails = "a@b.test" })).StatusCode);
        }

        // ---- health ---------------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_health_page_tells_the_truth_about_each_device_and_offers_the_fix()
        {
            var (co, dev) = await SetupAsync("Health Co");
            var ok = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/health", J)).EnumerateArray().Single();
            Assert.Equal("ok", ok.GetProperty("status").GetString());
            Assert.Equal("info", ok.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == "connection").GetProperty("status").GetString());   // not tested until asked

            var live = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/health?live=true", J)).EnumerateArray().Single();
            Assert.Equal("ok", live.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == "connection").GetProperty("status").GetString());

            await DbAsync(async db => { var d = await db.Devices.FirstAsync(x => x.Id == dev); d.CertificateValidTill = DateTime.UtcNow.AddDays(-2); });
            var bad = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/health", J)).EnumerateArray().Single();
            Assert.Equal("bad", bad.GetProperty("status").GetString());
            var cert = bad.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == "certificate");
            Assert.Equal("renew-certificate", cert.GetProperty("fixAction").GetString());
            await DbAsync(async db => { var d = await db.Devices.FirstAsync(x => x.Id == dev); d.CertificateValidTill = DateTime.UtcNow.AddYears(1); });

            // a queued receipt shows as a warning, and one press delivers it
            _f.Fdms.FailNextSubmitTransiently = true;
            var q = await InvoiceAsync(co.Admin, dev, "HL-Q");
            Assert.Equal("Pending", q.GetProperty("status").GetString());
            var warn = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/health", J)).EnumerateArray().Single();
            var queue = warn.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == "queue");
            Assert.Equal("warn", queue.GetProperty("status").GetString()); Assert.Equal("retry-pending", queue.GetProperty("fixAction").GetString());
            var fixedUp = await (await co.Admin.PostAsync($"/api/v1/health/devices/{dev}/retry-pending", null)).Json();
            Assert.Equal(1, fixedUp.GetProperty("delivered").GetInt32());
            Assert.Equal("Submitted", (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{q.GetProperty("id").GetGuid()}", J)).GetProperty("status").GetString());

            // another company's device is invisible
            var other = await TestKit.NewCompanyAsync(_f, "Health Other");
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/health?deviceId={dev}")).StatusCode);
        }

        // ---- quotes ----------------------------------------------------------------------------------------------------------------

        private static object Quote(string customer = "Moyo Retailers", string? number = null, string? hs = "85456852", decimal? tax = 15m) => new
        {
            number, customerName = customer, customerTin = "2001234567", customerVat = "220054321", customerEmail = "buyer@moyo.test", currency = "USD", taxInclusive = true, validUntil = DateTime.Today.AddDays(14),
            notes = "Delivery included", lines = new[] { new { name = "Shoes", hsCode = hs, quantity = 2, unitPrice = 57.5m, taxPercent = tax, isExempt = false } },
        };

        [Fact]
        public async Task A_quote_is_sent_accepted_and_turned_into_one_fiscal_invoice()
        {
            var (co, dev) = await SetupAsync("Quote Co");
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/quotes", new { customerName = "X Ltd", currency = "USD", taxInclusive = true, lines = Array.Empty<object>() })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/quotes", Quote(hs: "123"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/quotes", Quote(customer: "X"))).StatusCode);

            var saved = await co.Admin.PutAsJsonAsync("/api/v1/quotes", Quote());
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var q = await saved.Json(); var id = q.GetProperty("id").GetGuid();
            Assert.Equal("Q-0001", q.GetProperty("number").GetString()); Assert.Equal(115m, q.GetProperty("total").GetDecimal()); Assert.Equal("Draft", q.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/quotes", Quote(number: "Q-0001"))).StatusCode);   // numbers are unique

            var pdf = await co.Admin.GetAsync($"/api/v1/quotes/{id}/pdf");
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);

            var before = _f.Email.Messages.Count;
            Assert.True((await (await co.Admin.PostAsJsonAsync($"/api/v1/quotes/{id}/email", new { })).Json()).GetProperty("sent").GetBoolean());
            Assert.Equal("buyer@moyo.test", _f.Email.Messages.Last().To); Assert.Equal(before + 1, _f.Email.Messages.Count);
            Assert.Equal("Sent", (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/quotes/{id}", J)).GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"/api/v1/quotes/{id}/status", new { status = "Invoiced" })).StatusCode);   // only invoicing makes it Invoiced
            Assert.Equal("Accepted", (await (await co.Admin.PostAsJsonAsync($"/api/v1/quotes/{id}/status", new { status = "Accepted" })).Json()).GetProperty("status").GetString());

            // becomes a fiscal invoice, once
            var conv = await co.Admin.PostAsJsonAsync($"/api/v1/quotes/{id}/convert", new { deviceId = dev, paymentMethod = "Card" });
            Assert.True(conv.IsSuccessStatusCode, await conv.Content.ReadAsStringAsync());
            var receipt = await conv.Json();
            Assert.Equal("Submitted", receipt.GetProperty("status").GetString()); Assert.Equal(115m, receipt.GetProperty("total").GetDecimal()); Assert.Equal("INV-0001", receipt.GetProperty("invoiceNo").GetString());
            var again = await (await co.Admin.PostAsJsonAsync($"/api/v1/quotes/{id}/convert", new { deviceId = dev })).Json();
            Assert.Equal(receipt.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
            var done = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/quotes/{id}", J);
            Assert.Equal("Invoiced", done.GetProperty("status").GetString()); Assert.Equal(receipt.GetProperty("id").GetGuid(), done.GetProperty("invoiceReceiptId").GetGuid());
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PutAsJsonAsync("/api/v1/quotes", new { id, customerName = "Changed", currency = "USD", taxInclusive = true, lines = new[] { new { name = "A", quantity = 1, unitPrice = 1 } } })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.DeleteAsync($"/api/v1/quotes/{id}")).StatusCode);

            // draft quotes can be deleted; another company sees none of this
            var q2 = await (await co.Admin.PutAsJsonAsync("/api/v1/quotes", Quote(customer: "Second Customer"))).Json();
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/quotes/{q2.GetProperty("id").GetGuid()}")).StatusCode);
            var other = await TestKit.NewCompanyAsync(_f, "Quote Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/quotes", J)).EnumerateArray());
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/quotes/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsJsonAsync($"/api/v1/quotes/{id}/convert", new { deviceId = dev })).StatusCode);
        }

        // ---- recurring invoices -------------------------------------------------------------------------------------------------------

        private static object Recurring(Guid dev, string name = "Office rent", DateTime? next = null, string frequency = "Monthly", decimal? tax = 15m, DateTime? end = null) => new
        {
            deviceId = dev, name, customerName = "Moyo Retailers", customerTin = "2001234567", currency = "USD", taxInclusive = true, paymentMethod = "Cash", frequency, prefix = "RENT-",
            nextRunDate = (next ?? FdmsClock.CatNow().Date), endDate = end, emailCustomer = false, lines = new[] { new { name = "Monthly rent", hsCode = "99001000", quantity = 1, unitPrice = 230m, taxPercent = tax, isExempt = false } },
        };

        [Fact]
        public async Task A_recurring_invoice_issues_on_schedule_moves_on_pauses_and_reports_failures()
        {
            var (co, dev) = await SetupAsync("Recurring Co");
            var today = FdmsClock.CatNow().Date;
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PutAsJsonAsync("/api/v1/recurring", Recurring(dev, frequency: "Daily"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await co.Admin.PutAsJsonAsync("/api/v1/recurring", Recurring(Guid.NewGuid()))).StatusCode);

            var saved = await (await co.Admin.PutAsJsonAsync("/api/v1/recurring", Recurring(dev, next: today.AddDays(5)))).Json();
            var id = saved.GetProperty("id").GetGuid();
            Assert.Equal(230m, saved.GetProperty("total").GetDecimal());

            // not due yet: the scheduler leaves it; "issue now" does it on request and moves the date on
            var svc = _f.Services.CreateScope().ServiceProvider.GetRequiredService<IRecurringService>();
            Assert.Equal(0, await svc.RunDueAsync(today, CancellationToken.None));
            var ran = await (await co.Admin.PostAsync($"/api/v1/recurring/{id}/run", null)).Json();
            Assert.Equal(today.AddDays(5).AddMonths(1), ran.GetProperty("nextRunDate").GetDateTime().Date);
            Assert.Equal("RENT-0002", ran.GetProperty("prefix").GetString() + ran.GetProperty("nextNumber").GetInt32().ToString("0000"));
            var run = ran.GetProperty("recent")[0];
            Assert.Equal("Ok", run.GetProperty("status").GetString()); Assert.Equal("RENT-0001", run.GetProperty("invoiceNo").GetString());
            Assert.Equal("Submitted", (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{run.GetProperty("receiptId").GetGuid()}", J)).GetProperty("status").GetString());

            // overdue: the scheduler issues it (and never twice for the same date)
            var due = await (await co.Admin.PutAsJsonAsync("/api/v1/recurring", Recurring(dev, "Retainer", next: today.AddDays(-1), frequency: "Weekly"))).Json();
            Assert.True(await svc.RunDueAsync(today, CancellationToken.None) >= 1);
            Assert.Equal(0, await svc.RunDueAsync(today, CancellationToken.None));
            var list = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/recurring", J)).EnumerateArray().ToList();
            var retainer = list.First(r => r.GetProperty("name").GetString() == "Retainer");
            Assert.Equal(today.AddDays(6), retainer.GetProperty("nextRunDate").GetDateTime().Date);

            // paused schedules are skipped
            var paused = await (await co.Admin.PutAsJsonAsync("/api/v1/recurring", Recurring(dev, "Paused one", next: today.AddDays(-2)))).Json();
            Assert.False((await (await co.Admin.PostAsJsonAsync($"/api/v1/recurring/{paused.GetProperty("id").GetGuid()}/active", new { active = false })).Json()).GetProperty("isActive").GetBoolean());
            Assert.Equal(0, await svc.RunDueAsync(today, CancellationToken.None));
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/recurring/{paused.GetProperty("id").GetGuid()}/run", null)).StatusCode);

            // a schedule that ends stops itself after its last invoice
            var ending = await (await co.Admin.PutAsJsonAsync("/api/v1/recurring", Recurring(dev, "Ends today", next: today, end: today))).Json();
            Assert.True(await svc.RunDueAsync(today, CancellationToken.None) >= 1);
            Assert.False((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/recurring", J)).EnumerateArray().First(r => r.GetProperty("id").GetGuid() == ending.GetProperty("id").GetGuid()).GetProperty("isActive").GetBoolean());

            // a schedule that cannot be issued fails visibly, keeps its date, and raises an alert
            var broken = await (await co.Admin.PutAsJsonAsync("/api/v1/recurring", Recurring(dev, "Broken tax", next: today, tax: 99m))).Json();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsync($"/api/v1/recurring/{broken.GetProperty("id").GetGuid()}/run", null)).StatusCode);
            var after = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/recurring", J)).EnumerateArray().First(r => r.GetProperty("id").GetGuid() == broken.GetProperty("id").GetGuid());
            Assert.Equal("Failed", after.GetProperty("recent")[0].GetProperty("status").GetString()); Assert.Equal(today, after.GetProperty("nextRunDate").GetDateTime().Date);
            Assert.Contains((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "recurring");

            // deleting keeps the invoices already issued
            Assert.Equal(HttpStatusCode.NoContent, (await co.Admin.DeleteAsync($"/api/v1/recurring/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await co.Admin.GetAsync($"/api/v1/receipts/{run.GetProperty("receiptId").GetGuid()}")).StatusCode);

            var other = await TestKit.NewCompanyAsync(_f, "Recurring Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/recurring", J)).EnumerateArray());
        }

        // ---- VAT summary ----------------------------------------------------------------------------------------------------------------

        [Fact]
        public async Task The_vat_summary_adds_up_nets_off_credit_notes_and_exports_to_excel_and_pdf()
        {
            var (co, dev) = await SetupAsync("Vat Co");
            var first = await InvoiceAsync(co.Admin, dev, "V-1", 115);
            await InvoiceAsync(co.Admin, dev, "V-2", 230);
            var today = FdmsClock.CatNow().Date; var q = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";

            var v = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/reports/vat-return?{q}", J);
            var usd = v.GetProperty("currencies").EnumerateArray().Single();
            Assert.Equal(345m, usd.GetProperty("totalSalesInclusive").GetDecimal()); Assert.Equal(45m, usd.GetProperty("totalVat").GetDecimal()); Assert.Equal(300m, usd.GetProperty("totalSalesExclusive").GetDecimal());
            var line = usd.GetProperty("lines").EnumerateArray().Single();
            Assert.Equal("15%", line.GetProperty("label").GetString());

            // a credit note is deducted
            Assert.True((await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{first.GetProperty("id").GetGuid()}/correct", new { reason = "Returned", issueReplacement = false })).IsSuccessStatusCode);
            var net = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/reports/vat-return?{q}", J)).GetProperty("currencies").EnumerateArray().Single();
            Assert.Equal(230m, net.GetProperty("totalSalesInclusive").GetDecimal()); Assert.Equal(30m, net.GetProperty("totalVat").GetDecimal());
            Assert.Equal(1, net.GetProperty("creditNotes").GetInt32());

            var xlsx = await co.Admin.GetAsync($"/api/v1/reports/vat-return/xlsx?{q}");
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);
            Assert.Equal("PK", System.Text.Encoding.ASCII.GetString((await xlsx.Content.ReadAsByteArrayAsync()).Take(2).ToArray()));
            var pdf = await co.Admin.GetAsync($"/api/v1/reports/vat-return/pdf?{q}");
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync()).Take(4).ToArray()));

            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.GetAsync($"/api/v1/reports/vat-return?from={today:yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}")).StatusCode);
            var other = await TestKit.NewCompanyAsync(_f, "Vat Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/reports/vat-return?{q}", J)).GetProperty("currencies").EnumerateArray());
        }

        // ---- plan usage, checklist, suggestions ---------------------------------------------------------------------------------------

        [Fact]
        public async Task Usage_and_the_getting_started_list_follow_what_the_company_really_has()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Usage Co");
            var d0 = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", J);
            var steps = d0.GetProperty("checklist").GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("done").GetBoolean());
            Assert.False(steps["device"]); Assert.False(steps["invoice"]); Assert.Equal(7, d0.GetProperty("checklist").GetProperty("total").GetInt32());
            Assert.False(d0.GetProperty("usage").GetProperty("limited").GetBoolean());   // no plan: not limited

            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq), "SN-U");
            await DbAsync(async db =>
            {
                var plan = new Plan { Code = "tiny-" + Guid.NewGuid().ToString("N")[..6], Name = "Tiny", MaxDevices = 2, MaxUsers = 5, MaxBranches = 2, MaxReceiptsPerMonth = 5, IsActive = true, IsPublic = false };
                db.Plans.Add(plan); await db.SaveChangesAsync();
                db.Subscriptions.Add(new Subscription { ClientId = co.ClientId, PlanId = plan.Id, Status = "Active" });
            });
            for (var i = 1; i <= 4; i++) await InvoiceAsync(co.Admin, dev, "U-" + i);
            var u = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/usage", J));
            Assert.True(u.GetProperty("limited").GetBoolean()); Assert.Equal("Tiny", u.GetProperty("plan").GetString());
            var rec = u.GetProperty("items").EnumerateArray().First(i => i.GetProperty("key").GetString() == "receipts");
            Assert.Equal(4, rec.GetProperty("used").GetInt32()); Assert.Equal(80, rec.GetProperty("percent").GetInt32()); Assert.Equal("warn", rec.GetProperty("level").GetString());

            await _f.Services.CreateScope().ServiceProvider.GetRequiredService<INotificationService>().ScanAsync(DateTime.UtcNow, CancellationToken.None);
            Assert.Contains((await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications", J)).GetProperty("items").EnumerateArray(), n => n.GetProperty("kind").GetString() == "usage");

            // reaching the limit never stops a sale: fiscalising is a legal duty
            await InvoiceAsync(co.Admin, dev, "U-5"); await InvoiceAsync(co.Admin, dev, "U-6");
            Assert.Equal("over", (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/usage", J)).GetProperty("items").EnumerateArray().First(i => i.GetProperty("key").GetString() == "receipts").GetProperty("level").GetString());

            var d1 = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", J);
            steps = d1.GetProperty("checklist").GetProperty("steps").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("done").GetBoolean());
            Assert.True(steps["device"]); Assert.True(steps["invoice"]); Assert.False(steps["day"]);
            Assert.True(d1.GetProperty("checklist").GetProperty("done").GetInt32() >= 2);
        }

        [Fact]
        public async Task Item_suggestions_come_from_the_catalogue_first_then_from_past_invoices()
        {
            var (co, dev) = await SetupAsync("Suggest Co");
            Assert.True((await co.Admin.PutAsJsonAsync("/api/v1/products", new { name = "Shovel", hsCode = "82013000", unitPrice = 12.5m, currency = "USD", taxPercent = 15m, isActive = true })).IsSuccessStatusCode);
            await InvoiceAsync(co.Admin, dev, "SG-1", 115);   // sells "Shoes"

            var shoes = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/suggest/items?q=shoe", J)).EnumerateArray().ToList();
            var s = Assert.Single(shoes);
            Assert.Equal("Shoes", s.GetProperty("name").GetString()); Assert.Equal("85456852", s.GetProperty("hsCode").GetString()); Assert.Equal(115m, s.GetProperty("unitPrice").GetDecimal()); Assert.Equal("history", s.GetProperty("source").GetString());

            var all = (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/suggest/items", J)).EnumerateArray().ToList();
            Assert.Equal("catalogue", all[0].GetProperty("source").GetString()); Assert.Equal("Shovel", all[0].GetProperty("name").GetString());
            Assert.Contains(all, a => a.GetProperty("name").GetString() == "Shoes");

            var other = await TestKit.NewCompanyAsync(_f, "Suggest Other");
            Assert.Empty((await other.Admin.GetFromJsonAsync<JsonElement>("/api/v1/suggest/items", J)).EnumerateArray());
        }

        // ---- branding and the operator's attention list ---------------------------------------------------------------------------------

        [Fact]
        public async Task A_company_gets_its_own_sign_in_word_with_its_logo_and_cannot_take_anothers()
        {
            var a = await TestKit.NewCompanyAsync(_f, "Brand Alpha"); var b = await TestKit.NewCompanyAsync(_f, "Brand Beta");
            var anon = _f.CreateClient();
            var mine = await co(a);
            Assert.Equal("brand-alpha", mine.GetProperty("slug").GetString());
            Assert.EndsWith("/login?company=brand-alpha", mine.GetProperty("signInUrl").GetString());

            var pub = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/brand/brand-alpha", J);
            Assert.Equal("Brand Alpha", pub.GetProperty("name").GetString()); Assert.False(pub.GetProperty("hasLogo").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/public/brand/no-such-company")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/public/brand/brand-alpha/logo")).StatusCode);

            foreach (var bad in new[] { "ab", "-bad-", "Has Spaces", "api", "login" })
                Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.PutAsJsonAsync("/api/v1/company/brand", new { slug = bad })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await a.Admin.PutAsJsonAsync("/api/v1/company/brand", new { slug = "brand-beta" })).StatusCode);
            Assert.Equal("alpha-shop", (await (await a.Admin.PutAsJsonAsync("/api/v1/company/brand", new { slug = "alpha-shop" })).Json()).GetProperty("slug").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/v1/public/brand/brand-alpha")).StatusCode);   // the old word stops working

            // the logo of the default invoice template becomes the sign-in logo
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
            var tpl = await (await a.Admin.PutAsJsonAsync("/api/v1/templates", new { name = "Default", settings = new { accent = "#aa3300", showLogo = true, logoAlign = "left", showBuyer = true, showPayments = true, showHsCode = false, showQuantity = true, showUnitPrice = true, showLineTax = true } })).Json();
            var form = new MultipartFormDataContent(); var part = new ByteArrayContent(png); part.Headers.ContentType = new MediaTypeHeaderValue("image/png"); form.Add(part, "file", "logo.png");
            Assert.True((await a.Admin.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/logo", form)).IsSuccessStatusCode);
            var withLogo = await anon.GetFromJsonAsync<JsonElement>("/api/v1/public/brand/alpha-shop", J);
            Assert.True(withLogo.GetProperty("hasLogo").GetBoolean()); Assert.Equal("#aa3300", withLogo.GetProperty("accent").GetString());
            Assert.Equal("image/png", (await anon.GetAsync("/api/v1/public/brand/alpha-shop/logo")).Content.Headers.ContentType?.MediaType);
            Assert.Equal(HttpStatusCode.OK, (await a.Admin.GetAsync("/api/v1/company/logo")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.GetAsync("/api/v1/company/logo")).StatusCode);

            static async Task<JsonElement> co(TestKit.Company c) => await c.Admin.GetFromJsonAsync<JsonElement>("/api/v1/company/brand", J);
        }

        [Fact]
        public async Task The_operator_sees_which_companies_need_attention_first()
        {
            var (co, dev) = await SetupAsync("Attention Co");
            await DbAsync(async db => { var d = await db.Devices.FirstAsync(x => x.Id == dev); d.CertificateValidTill = DateTime.UtcNow.AddDays(3); });
            var op = await TestKit.AdminAsync(_f);
            var d0 = await op.GetFromJsonAsync<JsonElement>("/api/v1/dashboard/platform", J);
            var items = d0.GetProperty("attention").EnumerateArray().ToList();
            var mine = items.First(i => i.GetProperty("clientId").GetGuid() == co.ClientId);
            Assert.Equal("certificate", mine.GetProperty("kind").GetString()); Assert.Equal("Attention Co", mine.GetProperty("clientName").GetString()); Assert.Equal("warn", mine.GetProperty("severity").GetString());
            // worst first
            var order = new[] { "bad", "warn", "info" };
            Assert.Equal(items.Select(i => Array.IndexOf(order, i.GetProperty("severity").GetString())).OrderBy(x => x), items.Select(i => Array.IndexOf(order, i.GetProperty("severity").GetString())));
        }

        [Fact]
        public async Task The_new_screens_are_closed_to_people_without_the_right_and_open_to_those_with_it()
        {
            var (co, dev) = await SetupAsync("Rights Co");
            var email = TestKit.Unique("viewer") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email, name = "V", surname = "W", role = "Viewer", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var viewer = await TestKit.LoginAsync(_f, email, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/quotes")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/recurring")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/suggest/items")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/v1/company/brand", new { slug = "viewer-try" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/usage")).StatusCode);

            var opEmail = TestKit.Unique("oper") + "@users.test";
            Assert.True((await co.Admin.PostAsJsonAsync("/api/v1/users", new { email = opEmail, name = "O", surname = "P", role = "Operator", password = TestKit.UserPassword })).IsSuccessStatusCode);
            var oper = await TestKit.LoginAsync(_f, opEmail, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.OK, (await oper.GetAsync("/api/v1/quotes")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await oper.GetAsync("/api/v1/recurring")).StatusCode);   // set up by administrators
        }
    }
}
