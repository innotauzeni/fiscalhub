using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>
    /// The books for businesses that keep them here: invoices from the books (accounting-only plans), bringing in balances when starting, exchange rates, prepayments and deferred
    /// income, the statement of changes in equity, the annual financial statements, and the audit trail of the books.
    /// </summary>
    [ApiController, Authorize, Route("api/v1/accounting")]
    public class AccountingProController : ControllerBase
    {
        private readonly ISalesInvoiceService _sales; private readonly IConversionService _conversion; private readonly IExchangeRateService _rates;
        private readonly IScheduleService _schedules; private readonly IFinancialStatementsService _statements;

        public AccountingProController(ISalesInvoiceService sales, IConversionService conversion, IExchangeRateService rates, IScheduleService schedules, IFinancialStatementsService statements)
        { _sales = sales; _conversion = conversion; _rates = rates; _schedules = schedules; _statements = statements; }

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        // ---- sales invoices ----
        [HttpGet("sales/setup"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> SalesSetup(CancellationToken ct) => Ok(await _sales.SetupAsync(User.BooksClientId(), ct));
        [HttpPut("sales/footer"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SalesFooter(SaveInvoiceFooterRequest body, CancellationToken ct) => Ok(await _sales.SaveFooterAsync(User.BooksClientId(), body, ct));
        [HttpGet("sales"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Sales([FromQuery] string? status, [FromQuery] string? kind, [FromQuery] Guid? customerId, [FromQuery] string? q, [FromQuery] string? currency,
            [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int take = 200, CancellationToken ct = default) =>
            Ok(await _sales.ListAsync(User.BooksClientId(), new SalesInvoiceFilter(status, kind, customerId, q, currency, from, to, take), ct));
        [HttpGet("sales/{id:guid}"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> SalesOne(Guid id, CancellationToken ct) => Ok(await _sales.GetAsync(User.BooksClientId(), id, ct));
        [HttpPut("sales"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveSales(SaveSalesInvoiceRequest body, CancellationToken ct) => Ok(await _sales.SaveAsync(User.BooksClientId(), body, ct));
        [HttpDelete("sales/{id:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> DeleteSales(Guid id, CancellationToken ct) { await _sales.DeleteDraftAsync(User.BooksClientId(), id, ct); return NoContent(); }
        [HttpPost("sales/{id:guid}/issue"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Issue(Guid id, CancellationToken ct) => Ok(await _sales.IssueAsync(User.BooksClientId(), id, ct));
        [HttpPost("sales/{id:guid}/void"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> VoidSales(Guid id, CancellationToken ct) => Ok(await _sales.VoidAsync(User.BooksClientId(), id, ct));
        [HttpPost("sales/from-quote/{quoteId:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> FromQuote(Guid quoteId, CancellationToken ct) => Ok(await _sales.FromQuoteAsync(User.BooksClientId(), quoteId, ct));
        [HttpGet("sales/{id:guid}/pdf"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> SalesPdf(Guid id, CancellationToken ct) { var (pdf, name) = await _sales.PdfAsync(User.BooksClientId(), id, ct); return File(pdf, "application/pdf", name); }
        [HttpPost("sales/{id:guid}/email"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> EmailSales(Guid id, EmailInvoiceRequest body, CancellationToken ct) => Ok(await _sales.EmailAsync(User.BooksClientId(), id, body, ct));

        // ---- bringing in balances ----
        [HttpGet("conversion"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Conversion(CancellationToken ct) => Ok(await _conversion.StatusAsync(User.BooksClientId(), ct));
        [HttpGet("conversion/template/{kind}"), HasPermission(Perm.AccountingView)]
        public IActionResult Template(string kind) { var (bytes, name) = _conversion.Template(kind); return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name); }
        [HttpPost("conversion/read/{kind}"), HasPermission(Perm.AccountingManage), RequestSizeLimit(10_000_000)]
        public async Task<IActionResult> ReadFile(string kind, IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Choose a file.");
            await using var s = file.OpenReadStream();
            using var ms = new MemoryStream(); await s.CopyToAsync(ms, ct); ms.Position = 0;
            return Ok(await _conversion.ReadAsync(User.BooksClientId(), kind, file.FileName, ms, ct));
        }
        [HttpPost("conversion/check"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Check(ConversionRequest body, CancellationToken ct) => Ok(await _conversion.CheckAsync(User.BooksClientId(), body, ct));
        [HttpPost("conversion/post"), HasPermission(Perm.AccountingManage), RequestSizeLimit(10_000_000)] public async Task<IActionResult> PostConversion(ConversionRequest body, CancellationToken ct) => Ok(await _conversion.PostAsync(User.BooksClientId(), body, ct));

        // ---- exchange rates ----
        [HttpGet("rates"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Rates(CancellationToken ct) => Ok(await _rates.ListAsync(User.BooksClientId(), ct));
        [HttpPut("rates"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveRate(SaveExchangeRateRequest body, CancellationToken ct) => Ok(await _rates.SaveAsync(User.BooksClientId(), body, ct));
        [HttpDelete("rates/{id:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> DeleteRate(Guid id, CancellationToken ct) => Ok(await _rates.DeleteAsync(User.BooksClientId(), id, ct));
        [HttpPut("rates/base"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SetBase(SetBaseCurrencyRequest body, CancellationToken ct) => Ok(await _rates.SetBaseAsync(User.BooksClientId(), body, ct));

        // ---- prepayments and deferred income ----
        [HttpGet("schedules"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Schedules(CancellationToken ct) => Ok(await _schedules.ListAsync(User.BooksClientId(), ct));
        [HttpPut("schedules"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveSchedule(SaveScheduleRequest body, CancellationToken ct) => Ok(await _schedules.SaveAsync(User.BooksClientId(), body, ct));
        [HttpPost("schedules/{id:guid}/cancel"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> CancelSchedule(Guid id, CancellationToken ct) => Ok(await _schedules.CancelAsync(User.BooksClientId(), id, ct));
        [HttpPost("schedules/run"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> RunSchedules(CancellationToken ct) => Ok(await _schedules.RunAsync(User.BooksClientId(), ct));

        // ---- statements, notes, audit trail ----
        [HttpGet("reports/changes-in-equity"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> ChangesInEquity([FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
            Ok(await _statements.ChangesInEquityAsync(User.BooksClientId(), currency, from ?? new DateTime(Today.Year, 1, 1), to ?? Today, ct));
        [HttpGet("reports/financial-statements"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Pack([FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        {
            var (pdf, name) = await _statements.PackAsync(User.BooksClientId(), currency, from ?? new DateTime(Today.Year - 1, 1, 1), to ?? new DateTime(Today.Year - 1, 12, 31), ct);
            return File(pdf, "application/pdf", name);
        }
        [HttpGet("policies"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Policies(CancellationToken ct) => Ok(await _statements.PolicyNotesAsync(User.BooksClientId(), ct));
        [HttpPut("policies"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SavePolicies(SavePolicyNotesRequest body, CancellationToken ct) => Ok(await _statements.SavePolicyNotesAsync(User.BooksClientId(), body, ct));
        [HttpGet("audit"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Audit([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? q, [FromQuery] string? user, [FromQuery] int take = 300, CancellationToken ct = default) =>
            Ok(await _statements.AuditAsync(User.BooksClientId(), new BooksAuditFilter(from, to, q, user, take), ct));
    }
}
