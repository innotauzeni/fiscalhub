using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fdms.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Fdms.Tests
{
    /// <summary>Correcting an invoice ZIMRA flagged (red validation error): credit note + corrected invoice, and finding the receipts that block a day close.</summary>
    public class ReceiptCorrectionTests : IClassFixture<AccessFactory>
    {
        private readonly AccessFactory _f;
        private static readonly JsonSerializerOptions J = TestKit.J;
        private static int _seq = 20000;
        public ReceiptCorrectionTests(AccessFactory f) => _f = f;

        private static object TwoLines(string extRef, string no) => new
        {
            externalRef = extRef, invoiceNo = no, receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true,
            lines = new object[]
            {
                new { name = "Maize", hsCode = "01019199", quantity = 2, unitPrice = 100, taxPercent = 15 },
                new { name = "Nuts", hsCode = "01019198", quantity = 1, unitPrice = 50, taxPercent = 15 },
            },
            buyer = new { registerName = "Acme", tin = "1234567890" },
        };

        private async Task SetValidationErrors(Guid receiptId, string json)
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FdmsDbContext>();
            var r = await db.Receipts.FindAsync(receiptId);
            r!.ValidationErrorsJson = json;
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task A_flagged_invoice_is_corrected_with_a_credit_note_and_a_replacement_and_repeating_is_safe()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Correct Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var created = await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", TwoLines("C1", "INV-C1"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var original = (await created.Json()).GetProperty("id").GetGuid();
            await SetValidationErrors(original, "[{\"code\":\"RCPT048\",\"color\":\"Red\"}]");   // what ZIMRA said about it

            // the day page can list exactly the receipts that block the close
            var blocking = await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/receipts?fiscalDayNo=1&hasErrors=true&deviceId=" + dev, J);
            Assert.Equal(new[] { original.ToString() }, blocking.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToArray());
            Assert.Equal(0, (await co.Admin.GetFromJsonAsync<JsonElement>("/api/v1/receipts?fiscalDayNo=99&hasErrors=true", J)).GetProperty("items").GetArrayLength());

            // the review shows what was sent, line by line
            var detail = await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{original}/detail", J);
            Assert.Equal(2, detail.GetProperty("lines").GetArrayLength());
            Assert.Equal("01019199", detail.GetProperty("lines")[0].GetProperty("receiptLineHSCode").GetString());
            Assert.Equal("Acme", detail.GetProperty("buyer").GetProperty("buyerRegisterName").GetString());
            Assert.False(detail.TryGetProperty("creditNoteId", out _));

            // validation: a reason is required, fixes must name real lines and be 4/6/8 digits
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{original}/correct", new { reason = " ", issueReplacement = true })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{original}/correct", new { reason = "HS code", fixes = new[] { new { lineNo = 9, hsCode = "01019199" } }, issueReplacement = false })).StatusCode);
            var badFix = await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{original}/correct", new { reason = "HS code", fixes = new[] { new { lineNo = 1, hsCode = "0101919" } }, issueReplacement = false });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, badFix.StatusCode);
            Assert.Equal("RCPT048", (await badFix.Json()).GetProperty("errorCode").GetString());

            // correct: credit note reverses it, a replacement carries the fixed HS code and a new number
            var res = await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{original}/correct",
                new { reason = "HS code had the wrong length", fixes = new[] { new { lineNo = 1, hsCode = "01019100" } }, issueReplacement = true, replacementInvoiceNo = "INV-C1-FIXED" });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Json();
            var credit = body.GetProperty("creditNote");
            var replacement = body.GetProperty("replacement");
            Assert.Equal("CN-INV-C1", credit.GetProperty("invoiceNo").GetString());
            Assert.Equal("CreditNote", credit.GetProperty("type").GetString());
            Assert.Equal(-250m, credit.GetProperty("total").GetDecimal());                               // the whole 250.00 reversed
            Assert.Equal("INV-C1-FIXED", replacement.GetProperty("invoiceNo").GetString());
            Assert.Equal(250m, replacement.GetProperty("total").GetDecimal());
            Assert.NotEqual(original, replacement.GetProperty("id").GetGuid());
            var replacementLines = (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{replacement.GetProperty("id").GetGuid()}/detail", J)).GetProperty("lines");
            Assert.Equal("01019100", replacementLines[0].GetProperty("receiptLineHSCode").GetString());   // fixed
            Assert.Equal("01019198", replacementLines[1].GetProperty("receiptLineHSCode").GetString());   // everything else copied as it was
            Assert.Equal("Acme", (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{replacement.GetProperty("id").GetGuid()}/detail", J)).GetProperty("buyer").GetProperty("buyerRegisterName").GetString());

            // the original now points at its credit note
            Assert.Equal(credit.GetProperty("id").GetGuid(), (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts/{original}/detail", J)).GetProperty("creditNoteId").GetGuid());

            // doing it again changes nothing: no second credit note, no second replacement
            var again = await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{original}/correct", new { reason = "again", issueReplacement = true, replacementInvoiceNo = "INV-C1-FIXED" })).Json();
            Assert.Equal(credit.GetProperty("id").GetGuid(), again.GetProperty("creditNote").GetProperty("id").GetGuid());
            Assert.Equal(replacement.GetProperty("id").GetGuid(), again.GetProperty("replacement").GetProperty("id").GetGuid());
            Assert.Equal(3, (await co.Admin.GetFromJsonAsync<JsonElement>($"/api/v1/receipts?deviceId={dev}", J)).GetProperty("total").GetInt32());   // original + credit + replacement

            // and it is in the audit trail
            var trail = (await TestKit.AuditAsync(_f, co.ClientId, "action=receipt.correct")).GetProperty("items");
            Assert.Contains("INV-C1", trail[0].GetProperty("detail").GetString());
        }

        [Fact]
        public async Task Only_accepted_invoices_can_be_corrected_and_only_by_their_own_company()
        {
            var co = await TestKit.NewCompanyAsync(_f, "Correct Rights Co");
            var other = await TestKit.NewCompanyAsync(_f, "Other Correct Co");
            var (dev, _) = await TestKit.RegisterDeviceAsync(co.Admin, Interlocked.Increment(ref _seq));
            var invoice = (await (await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", TwoLines("C2", "INV-C2"))).Json()).GetProperty("id").GetGuid();

            // another company cannot see or correct it
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/receipts/{invoice}/detail")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsJsonAsync($"/api/v1/receipts/{invoice}/correct", new { reason = "nope", issueReplacement = false })).StatusCode);

            // a credit note cannot itself be corrected; a held receipt has nothing at ZIMRA to correct
            var credit = (await (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{invoice}/correct", new { reason = "customer returned goods", issueReplacement = false })).Json()).GetProperty("creditNote").GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{credit}/correct", new { reason = "again", issueReplacement = false })).StatusCode);
            var held = (await (await co.Admin.PostAsJsonAsync($"/api/v1/devices/{dev}/receipts", new
            {
                externalRef = "C2H", invoiceNo = "INV-C2H", receiptType = "FiscalInvoice", currency = "USD", linesTaxInclusive = true, holdMinutes = 10,
                lines = new[] { new { name = "Shoes", hsCode = "85456852", quantity = 1, unitPrice = 10, taxPercent = 15 } },
            })).Json()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.Conflict, (await co.Admin.PostAsJsonAsync($"/api/v1/receipts/{held}/correct", new { reason = "held", issueReplacement = false })).StatusCode);

            // an operator may correct (receipts.submit); a viewer may only look
            var viewerEmail = TestKit.Unique("viewer") + "@users.test";
            await co.Admin.PostAsJsonAsync("/api/v1/users", new { email = viewerEmail, name = "V", surname = "V", role = "Viewer", password = TestKit.UserPassword });
            var viewer = await TestKit.LoginAsync(_f, viewerEmail, TestKit.UserPassword);
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/receipts/{invoice}/detail")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/receipts/{invoice}/correct", new { reason = "viewer", issueReplacement = false })).StatusCode);
        }
    }
}
