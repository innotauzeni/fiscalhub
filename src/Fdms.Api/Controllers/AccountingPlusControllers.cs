using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>
    /// The books, taken further: cost centres, recurring journals, opening balances and closing the year, the cash flow statement and financial dashboard,
    /// statements as PDF and by email, and exporting any accounting table as PDF, Excel or CSV.
    /// </summary>
    [ApiController, Authorize, Route("api/v1/accounting")]
    public class AccountingPlusController : ControllerBase
    {
        private readonly ICostCentreService _centres;
        private readonly IBooksAutomationService _auto;
        private readonly IAccountingReports _reports;
        private readonly IStatementService _statements;
        private readonly IReportExporter _export;

        public AccountingPlusController(ICostCentreService centres, IBooksAutomationService auto, IAccountingReports reports, IStatementService statements, IReportExporter export)
        { _centres = centres; _auto = auto; _reports = reports; _statements = statements; _export = export; }

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;
        private static DateTime MonthStart => new(Today.Year, Today.Month, 1);

        // ---- cost centres ----
        [HttpGet("cost-centres"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Centres(CancellationToken ct) => Ok(await _centres.ListAsync(User.BooksClientId(), ct));
        [HttpPut("cost-centres"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveCentre(SaveCostCentreRequest body, CancellationToken ct) => Ok(await _centres.SaveAsync(User.BooksClientId(), body, ct));
        [HttpDelete("cost-centres/{id:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> DeleteCentre(Guid id, CancellationToken ct) { await _centres.DeleteAsync(User.BooksClientId(), id, ct); return NoContent(); }

        // ---- more reports ----
        [HttpGet("reports/cash-flow"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> CashFlow([FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
            Ok(await _reports.CashFlowAsync(User.BooksClientId(), currency, from ?? MonthStart, to ?? Today, ct));
        [HttpGet("reports/dashboard"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Dashboard([FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
            Ok(await _reports.DashboardAsync(User.BooksClientId(), currency, from ?? new DateTime(Today.Year, 1, 1), to ?? Today, ct));
        [HttpGet("reports/profit-loss-centres"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> ProfitLossCentres([FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
            Ok(await _reports.ProfitLossByCentreAsync(User.BooksClientId(), currency, from ?? MonthStart, to ?? Today, ct));

        // ---- recurring journals ----
        [HttpGet("recurring"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Recurring(CancellationToken ct) => Ok(await _auto.ListRecurringAsync(User.BooksClientId(), ct));
        [HttpPut("recurring"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveRecurring(SaveRecurringJournalRequest body, CancellationToken ct) => Ok(await _auto.SaveRecurringAsync(User.BooksClientId(), body, ct));
        [HttpDelete("recurring/{id:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> DeleteRecurring(Guid id, CancellationToken ct) { await _auto.DeleteRecurringAsync(User.BooksClientId(), id, ct); return NoContent(); }
        [HttpPost("recurring/{id:guid}/run"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> RunRecurring(Guid id, CancellationToken ct) => Ok(await _auto.RunRecurringAsync(User.BooksClientId(), id, ct));

        // ---- opening balances and year end ----
        [HttpPost("opening-balances"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Opening(OpeningBalancesRequest body, CancellationToken ct) => Ok(await _auto.OpeningBalancesAsync(User.BooksClientId(), body, ct));
        [HttpGet("year-end"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> YearEnd([FromQuery] int? year, CancellationToken ct) => Ok(await _auto.YearEndAsync(User.BooksClientId(), year, ct));
        [HttpPost("year-end/close"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> CloseYear(CloseYearRequest body, CancellationToken ct) => Ok(await _auto.CloseYearAsync(User.BooksClientId(), body, ct));
        public sealed record ReopenYearRequest(DateTime YearEnd);
        [HttpPost("year-end/reopen"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> ReopenYear(ReopenYearRequest body, CancellationToken ct) => Ok(await _auto.ReopenYearAsync(User.BooksClientId(), body.YearEnd, ct));

        // ---- statements ----
        [HttpGet("customers/statement/pdf"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> CustomerPdf([FromQuery] string customer, [FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        {
            var (pdf, name) = await _statements.CustomerPdfAsync(User.BooksClientId(), customer, currency, from ?? new DateTime(Today.Year, 1, 1), to ?? Today, ct);
            return File(pdf, "application/pdf", name);
        }
        [HttpGet("suppliers/statement/pdf"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> SupplierPdf([FromQuery] Guid supplierId, [FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        {
            var (pdf, name) = await _statements.SupplierPdfAsync(User.BooksClientId(), supplierId, currency, from ?? new DateTime(Today.Year, 1, 1), to ?? Today, ct);
            return File(pdf, "application/pdf", name);
        }
        [HttpPost("statements/email"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> EmailStatement(EmailStatementRequest body, CancellationToken ct) => Ok(await _statements.EmailAsync(User.BooksClientId(), body, ct));

        // ---- spend money, receive money, bank transfers, post to ledger ----
        /// <summary>kind: payment, receipt or transfer; status: Draft, Posted or Void; dateBy: transaction or created.</summary>
        [HttpGet("money"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Money([FromQuery] string? kind, [FromQuery] string? status, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? dateBy, [FromQuery] string? q, [FromQuery] Guid? accountId,
            [FromServices] ICashDocumentService docs, CancellationToken ct) => Ok(await docs.ListAsync(User.BooksClientId(), new CashDocumentFilter(kind, status, from, to, dateBy, q, accountId), ct));
        [HttpGet("money/{id:guid}"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> MoneyOne(Guid id, [FromServices] ICashDocumentService docs, CancellationToken ct) => Ok(await docs.GetAsync(User.BooksClientId(), id, ct));
        [HttpPut("money"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveMoney(SaveCashDocumentRequest body, [FromServices] ICashDocumentService docs, CancellationToken ct) => Ok(await docs.SaveAsync(User.BooksClientId(), body, ct));
        [HttpPost("money/{id:guid}/post"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> PostMoney(Guid id, [FromServices] ICashDocumentService docs, CancellationToken ct) => Ok(await docs.PostAsync(User.BooksClientId(), id, ct));
        [HttpPost("money/post-batch"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> PostBatch(PostBatchRequest body, [FromServices] ICashDocumentService docs, CancellationToken ct) => Ok(await docs.PostBatchAsync(User.BooksClientId(), body.Ids, ct));
        [HttpPost("money/{id:guid}/void"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> VoidMoney(Guid id, [FromServices] ICashDocumentService docs, CancellationToken ct) => Ok(await docs.VoidAsync(User.BooksClientId(), id, ct));
        [HttpDelete("money/{id:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> DeleteMoney(Guid id, [FromServices] ICashDocumentService docs, CancellationToken ct) { await docs.DeleteAsync(User.BooksClientId(), id, ct); return NoContent(); }

        // ---- export ----
        /// <summary>Any accounting table as PDF, Excel (xlsx) or CSV, with the company's name on top.</summary>
        [HttpPost("export"), HasPermission(Perm.AccountingView), RequestSizeLimit(8_000_000)]
        public async Task<IActionResult> Export(ExportRequest body, CancellationToken ct)
        {
            var (bytes, type, name) = await _export.ExportAsync(User.BooksClientId(), body, ct);
            return File(bytes, type, name);
        }
    }
}
