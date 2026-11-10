using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
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
    /// <summary>Small offline files (4 KB) so that a handful of receipts is enough to exercise file splitting.</summary>
    public sealed class HoldOfflineFactory : WebApplicationFactory<Program>
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
                ["RateLimit:LoginPerMinute"] = "100000",
                ["Auth:ActiveCacheSeconds"] = "0",
                ["DataProtection:KeysPath"] = _keys,
                ["RateLimit:PerMinute"] = "100000",
                ["Fdms:DistributedLocks"] = "false",
                ["Fdms:OfflineFileBytes"] = "4000",
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

    public class HoldOfflineTests : IClassFixture<HoldOfflineFactory>
    {
        private readonly HoldOfflineFactory _f;
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private static int _seq = 9000;
        public HoldOfflineTests(HoldOfflineFactory f) => _f = f;

        private async Task<(HttpClient Http, Guid Device, int ZimraId)> SetupAsync(string name, string mode = "Online")
        {
            var co = await TestKit.NewCompanyAsync(_f, name);
            var zimra = Interlocked.Increment(ref _seq);
            _f.Fdms.OperatingMode = mode;
            var reg = await co.Admin.PostAsJsonAsync("/api/v1/devices/register", new { zimraDeviceId = zimra, activationKey = "ABCD1234", serialNo = "SN-5", modelName = "Server", modelVersion = "v1", environment = "Test" });
            _f.Fdms.OperatingMode = "Online";   // the device keeps the mode it read at registration
            Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
            var dto = await reg.Content.ReadFromJsonAsync<JsonElement>(J);
            Assert.Equal(mode, dto.GetProperty("operatingMode").GetString());
            var id = dto.GetProperty("id").GetGuid();
            return ((await TestKit.NewTokenAsync(_f, co.Admin, id)).Http, id, zimra);
        }

        private static object Invoice(string extRef, int hold = 0, decimal price = 115m) => new
        {
            externalRef = extRef, invoiceNo = "INV-" + extRef, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, holdMinutes = hold,
            lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = price, taxPercent = 15 } },
        };

        private async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(J);

        // ---- hold window ------------------------------------------------------------------------------------------

        [Fact]
        public async Task Held_receipt_uses_no_number_can_be_cancelled_and_its_reference_reused()
        {
            var (c, dev, _) = await SetupAsync("Hold Co");
            var calls = _f.Fdms.SubmitCalls;

            var held = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("H1", 30));
            Assert.Equal(HttpStatusCode.Accepted, held.StatusCode);
            var h = await Json(held);
            Assert.Equal("Held", h.GetProperty("status").GetString());
            Assert.Equal(0, h.GetProperty("receiptCounter").GetInt32());
            Assert.True(h.GetProperty("holdUntil").GetDateTime() > DateTime.UtcNow);
            Assert.Equal(calls, _f.Fdms.SubmitCalls);                                              // nothing sent to ZIMRA

            // the same reference returns the held receipt, it does not create another
            Assert.Equal(h.GetProperty("id").GetGuid(), (await Json(await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("H1", 30)))).GetProperty("id").GetGuid());

            var cancelled = await Json(await c.PostAsJsonAsync($"/api/v1/receipts/{h.GetProperty("id").GetGuid()}/cancel", new { reason = "typo" }));
            Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());

            // the corrected invoice is the FIRST receipt of the day: the cancelled one burned nothing
            var real = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("H1"));
            Assert.Equal(HttpStatusCode.Created, real.StatusCode);
            Assert.Equal(1, (await Json(real)).GetProperty("receiptCounter").GetInt32());
        }

        [Fact]
        public async Task Release_issues_the_receipt_and_a_released_receipt_can_no_longer_be_cancelled()
        {
            var (c, dev, _) = await SetupAsync("Release Co");
            var h = await Json(await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("R1", 30)));
            var id = h.GetProperty("id").GetGuid();

            var released = await Json(await c.PostAsync($"/api/v1/receipts/{id}/release", null));
            Assert.Equal("Submitted", released.GetProperty("status").GetString());
            Assert.Equal(1, released.GetProperty("receiptCounter").GetInt32());
            Assert.StartsWith("https://invoice.zimra.co.zw/", released.GetProperty("qrUrl").GetString());

            var again = await c.PostAsJsonAsync($"/api/v1/receipts/{id}/cancel", new { reason = "too late" });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("ALREADY_ISSUED", (await Json(again)).GetProperty("errorCode").GetString());
        }

        [Fact]
        public async Task Held_receipts_are_validated_immediately_and_released_by_the_job_when_due_in_order()
        {
            var (c, dev, _) = await SetupAsync("Due Co");
            var bad = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = "B1", invoiceNo = "B-1", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, holdMinutes = 10,
                lines = new[] { new { name = "X", hsCode = "85456852", quantity = 1, unitPrice = 1, taxPercent = 77 } },
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);   // a mistake is reported now, not when the hold ends

            var ids = new List<Guid>();
            foreach (var n in new[] { "D1", "D2", "D3" })
                ids.Add((await Json(await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice(n, 5)))).GetProperty("id").GetGuid());

            using (var scope = _f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
                foreach (var r in db.Receipts.Where(r => ids.Contains(r.Id))) r.HoldUntil = DateTime.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
                Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<IReceiptService>().ReleaseDueHeldAsync(default));
            }
            for (var i = 0; i < 3; i++)
            {
                var r = await Json(await c.GetAsync($"/api/v1/receipts/{ids[i]}"));
                Assert.Equal("Submitted", r.GetProperty("status").GetString());
                Assert.Equal(i + 1, r.GetProperty("receiptCounter").GetInt32());    // creation order is issue order
            }
        }

        [Fact]
        public async Task A_held_receipt_ZIMRA_refuses_is_marked_Rejected_and_gives_its_numbers_back()
        {
            var (c, dev, _) = await SetupAsync("Refuse Co");
            var id = (await Json(await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("F1", 5)))).GetProperty("id").GetGuid();

            _f.Fdms.RejectNextSubmit = true;
            var res = await Json(await c.PostAsync($"/api/v1/receipts/{id}/release", null));
            Assert.Equal("Rejected", res.GetProperty("status").GetString());
            Assert.Equal("RCPT01", res.GetProperty("errorCode").GetString());

            var next = await Json(await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("F2")));
            Assert.Equal(1, next.GetProperty("receiptCounter").GetInt32());        // sequence has no hole
        }

        [Fact]
        public async Task Hold_minutes_outside_the_allowed_range_is_refused()
        {
            var (c, dev, _) = await SetupAsync("Range Co");
            foreach (var minutes in new[] { 999, -1 })
            {
                var res = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("X" + minutes, minutes));
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
                Assert.Equal("HOLD_INVALID", (await Json(res)).GetProperty("errorCode").GetString());
            }
        }

        // ---- offline mode -----------------------------------------------------------------------------------------

        [Fact]
        public async Task Offline_day_is_issued_locally_filed_uploaded_and_confirmed()
        {
            var (c, dev, zimra) = await SetupAsync("Offline Co", "Offline");
            var calls = _f.Fdms.SubmitCalls;

            // 6 receipts: each ~1 KB, so the 4 KB file limit forces several files
            var ids = new List<Guid>();
            for (var i = 1; i <= 6; i++)
            {
                var res = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("O" + i, 0, 10m * i));
                Assert.Equal(HttpStatusCode.Created, res.StatusCode);
                var r = await Json(res);
                Assert.Equal("OfflineIssued", r.GetProperty("status").GetString());
                Assert.Equal(i, r.GetProperty("receiptCounter").GetInt32());
                Assert.StartsWith("https://invoice.zimra.co.zw/", r.GetProperty("qrUrl").GetString());   // printable immediately
                ids.Add(r.GetProperty("id").GetGuid());
            }
            Assert.Equal(calls, _f.Fdms.SubmitCalls);                                                        // no online submitReceipt
            var day = await Json(await c.GetAsync($"/api/v1/devices/{dev}/day"));                          // status works without getStatus
            Assert.Equal("FiscalDayOpened", day.GetProperty("status").GetString());

            var close = await Json(await c.PostAsync($"/api/v1/devices/{dev}/day/close", null));
            Assert.Equal("FiscalDayClosed", close.GetProperty("status").GetString());

            // files: sequential, all within the size limit, footer only on the last, every receipt present once
            var files = _f.Fdms.FilesReceived.Where(x => x.Header.DeviceId == zimra && x.Header.FiscalDayNo == 1).ToList();
            Assert.True(files.Count >= 2, "expected the day to be split into several files");
            Assert.Equal(Enumerable.Range(1, files.Count), files.Select(x => x.Header.FileSequence));
            Assert.All(files.Take(files.Count - 1), x => Assert.Null(x.Footer));
            Assert.NotNull(files[^1].Footer);
            Assert.Equal(Enumerable.Range(1, 6), files.SelectMany(x => x.Content?.Receipts ?? new()).Select(r => r.ReceiptCounter));

            var list = await Json(await c.GetAsync($"/api/v1/devices/{dev}/offline-files"));
            Assert.All(list.EnumerateArray(), f => Assert.Equal("Processed", f.GetProperty("status").GetString()));
            foreach (var id in ids)
                Assert.Equal("Submitted", (await Json(await c.GetAsync($"/api/v1/receipts/{id}"))).GetProperty("status").GetString());
        }

        [Fact]
        public async Task Offline_credit_note_references_the_original_by_device_global_number_and_day_and_counters_still_balance()
        {
            var (c, dev, zimra) = await SetupAsync("Offline CN Co", "Offline");
            var inv = await Json(await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("N1")));
            await c.PostAsync($"/api/v1/devices/{dev}/day/close", null);                                    // day 1 filed and confirmed
            var invId = inv.GetProperty("id").GetGuid();
            Assert.Equal("Submitted", (await Json(await c.GetAsync($"/api/v1/receipts/{invId}"))).GetProperty("status").GetString());

            // day 2: a credit note against the day-1 invoice (no FDMS receiptID exists for offline invoices)
            var cn = await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = "N2", invoiceNo = "CN-N2", receiptType = "CreditNote", currency = "USD", linesTaxInclusive = true, notes = "Returned", originalReceiptId = invId,
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 115, taxPercent = 15 } },
            });
            Assert.Equal(HttpStatusCode.Created, cn.StatusCode);
            var close2 = await Json(await c.PostAsync($"/api/v1/devices/{dev}/day/close", null));          // FDMS (fake) verifies the footer counters incl. credit-note counters
            Assert.Equal("FiscalDayClosed", close2.GetProperty("status").GetString());

            var file2 = _f.Fdms.FilesReceived.Last(x => x.Header.DeviceId == zimra && x.Header.FiscalDayNo == 2);
            var note = file2.Content!.Receipts.Single().CreditDebitNote!;
            Assert.Null(note.ReceiptId);
            Assert.Equal(zimra, note.DeviceId);
            Assert.Equal(1, note.FiscalDayNo);
            Assert.Equal(1, note.ReceiptGlobalNo);
            var files = await Json(await c.GetAsync($"/api/v1/devices/{dev}/offline-files"));
            Assert.All(files.EnumerateArray(), f => Assert.Equal("Processed", f.GetProperty("status").GetString()));   // no CountersMismatch
        }

        [Fact]
        public async Task Mid_day_sync_uploads_content_files_and_the_close_sends_only_the_footer()
        {
            var (c, dev, zimra) = await SetupAsync("Sync Co", "Offline");
            await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("S1"));
            await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("S2"));

            var sync = await Json(await c.PostAsync($"/api/v1/devices/{dev}/offline-files/sync", null));
            Assert.Equal("Processed", sync[0].GetProperty("status").GetString());
            Assert.False(sync[0].GetProperty("hasFooter").GetBoolean());
            Assert.Equal(2, sync[0].GetProperty("receiptCount").GetInt32());

            await c.PostAsync($"/api/v1/devices/{dev}/day/close", null);
            var files = _f.Fdms.FilesReceived.Where(x => x.Header.DeviceId == zimra && x.Header.FiscalDayNo == 1).ToList();
            Assert.Equal(2, files.Count);
            Assert.Null(files[1].Content);                                  // footer-only file: everything was already sent
            Assert.NotNull(files[1].Footer);
            Assert.Equal(2, files[1].Header.FileSequence);
            Assert.Equal(2, files[1].Footer!.ReceiptCounter);
        }

        [Fact]
        public async Task A_failed_upload_stays_queued_and_the_background_job_finishes_it()
        {
            var (c, dev, _) = await SetupAsync("Retry Co", "Offline");
            var id = (await Json(await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("U1")))).GetProperty("id").GetGuid();

            _f.Fdms.FailNextFileUpload = true;
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/v1/devices/{dev}/day/close", null)).StatusCode);   // the close itself succeeds locally
            var files = await Json(await c.GetAsync($"/api/v1/devices/{dev}/offline-files"));
            Assert.Equal("Pending", files[0].GetProperty("status").GetString());
            Assert.Equal("OfflineIssued", (await Json(await c.GetAsync($"/api/v1/receipts/{id}"))).GetProperty("status").GetString());

            using (var scope = _f.Services.CreateScope())
                Assert.True(await scope.ServiceProvider.GetRequiredService<OfflineFileService>().RunAllAsync(default) >= 1);
            Assert.Equal("Submitted", (await Json(await c.GetAsync($"/api/v1/receipts/{id}"))).GetProperty("status").GetString());
        }

        [Fact]
        public async Task Sync_is_refused_for_an_online_device_and_online_mode_never_creates_files()
        {
            var (c, dev, _) = await SetupAsync("Online Co2");
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/v1/devices/{dev}/offline-files/sync", null)).StatusCode);
            await c.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", Invoice("W1"));
            await c.PostAsync($"/api/v1/devices/{dev}/day/close", null);
            Assert.Empty((await Json(await c.GetAsync($"/api/v1/devices/{dev}/offline-files"))).EnumerateArray());
        }

        // ---- other FDMS endpoints ---------------------------------------------------------------------------------

        [Fact]
        public async Task Ping_stock_and_server_certificate_are_exposed()
        {
            var (c, dev, _) = await SetupAsync("Extras Co");
            Assert.Equal(60, (await Json(await c.PostAsync($"/api/v1/devices/{dev}/ping", null))).GetProperty("reportingFrequency").GetInt32());

            var stock = await Json(await c.GetAsync($"/api/v1/devices/{dev}/stock?hsCode=85456852"));
            Assert.Equal(1, stock.GetProperty("total").GetInt32());
            Assert.Equal("Shoes", stock.GetProperty("rows")[0].GetProperty("goodName").GetString());

            var cert = await Json(await c.GetAsync($"/api/v1/devices/{dev}/server-certificate"));
            Assert.StartsWith("-----BEGIN CERTIFICATE-----", cert.GetProperty("certificate")[0].GetString());
        }
    }
}
