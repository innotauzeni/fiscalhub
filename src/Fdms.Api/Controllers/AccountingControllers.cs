using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>The company's books: chart of accounts, journals and cashbook entries. Fiscal receipts post themselves; everything else is entered here.</summary>
    [ApiController, Authorize, Route("api/v1/accounting")]
    public class AccountingController : ControllerBase
    {
        private readonly IAccountingService _s;
        private readonly IAccountingReports _r;
        public AccountingController(IAccountingService s, IAccountingReports r) { _s = s; _r = r; }

        [HttpGet("meta"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Meta(CancellationToken ct) => Ok(await _r.MetaAsync(User.BooksClientId(), ct));

        [HttpGet("accounts"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Accounts(CancellationToken ct) => Ok(await _s.ListAccountsAsync(User.BooksClientId(), ct));
        [HttpPut("accounts"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveAccount(SaveLedgerAccountRequest body, CancellationToken ct) => Ok(await _s.SaveAccountAsync(User.BooksClientId(), body, ct));
        [HttpDelete("accounts/{id:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> DeleteAccount(Guid id, CancellationToken ct) { await _s.DeleteAccountAsync(User.BooksClientId(), id, ct); return NoContent(); }

        [HttpGet("journals"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Journals([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? source, [FromQuery] string? q, [FromQuery] string? currency, [FromQuery] int take = 100, CancellationToken ct = default) =>
            Ok(await _s.ListJournalsAsync(User.BooksClientId(), new JournalFilter(from, to, source, q, currency, take), ct));
        [HttpGet("journals/{id:guid}"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Journal(Guid id, CancellationToken ct) => Ok(await _s.GetJournalAsync(User.BooksClientId(), id, ct));
        [HttpPost("journals"), HasPermission(Perm.AccountingManage)]
        public async Task<IActionResult> Create(CreateJournalRequest body, [FromServices] IApprovalService approvals, CancellationToken ct)
        {
            var debits = (body.Lines ?? Array.Empty<JournalLineInput>()).Sum(l => l.Debit);
            var held = await approvals.GateAsync(User.BooksClientId(), "journal", debits, body.Currency, $"Journal: {body.Memo}", null, body, ct);
            if (held != null) return Accepted(held);
            return Ok(await _s.CreateJournalAsync(User.BooksClientId(), body, ct));
        }
        [HttpPost("journals/{id:guid}/reverse"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Reverse(Guid id, CancellationToken ct) => Ok(await _s.ReverseJournalAsync(User.BooksClientId(), id, ct));
        [HttpPost("sync"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Sync(CancellationToken ct) => Ok(await _s.SyncSalesAsync(User.BooksClientId(), ct));
        [HttpPost("cashbook-entry"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> CashbookEntry(CashbookEntryRequest body, CancellationToken ct) => Ok(await _s.CashbookEntryAsync(User.BooksClientId(), body, ct));

        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        /// <summary>currency=ALL: every currency together, in the base currency (at the exchange rates kept under Exchange rates).</summary>
        private async Task<string> CurrencyAsync(string? currency, CancellationToken ct) =>
            string.Equals(currency, "ALL", StringComparison.OrdinalIgnoreCase) ? await _r.UseBaseCurrencyAsync(User.BooksClientId(), ct) : currency ?? "";

        /// <summary>subAccounts=true: the account with everything below it.</summary>
        [HttpGet("reports/ledger"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Ledger([FromQuery] Guid accountId, [FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] bool subAccounts = false, [FromQuery] Guid? costCentreId = null, CancellationToken ct = default) =>
            Ok(await _r.LedgerAsync(User.BooksClientId(), accountId, currency, from ?? new DateTime(Today.Year, Today.Month, 1), to ?? Today, ct, subAccounts, costCentreId));
        /// <summary>from: start of the movements shown (default: the start of the financial year); zero=true also lists accounts with nothing on them.</summary>
        [HttpGet("reports/trial-balance"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> TrialBalance([FromQuery] string currency, [FromQuery] DateTime? asAt, [FromQuery] DateTime? from, [FromQuery] bool zero = false, CancellationToken ct = default) =>
            Ok(await _r.TrialBalanceAsync(User.BooksClientId(), await CurrencyAsync(currency, ct), asAt ?? Today, ct, from, zero));
        /// <summary>compare: none, previous or year.</summary>
        [HttpGet("reports/profit-loss"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> ProfitLoss([FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? compare, [FromQuery] Guid? costCentreId, CancellationToken ct) =>
            Ok(await _r.ProfitLossAsync(User.BooksClientId(), await CurrencyAsync(currency, ct), from ?? new DateTime(Today.Year, Today.Month, 1), to ?? Today, ct, compare, costCentreId));
        /// <summary>compare: none, year or a date (yyyy-MM-dd).</summary>
        [HttpGet("reports/balance-sheet"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> BalanceSheet([FromQuery] string currency, [FromQuery] DateTime? asAt, [FromQuery] string? compare, CancellationToken ct) =>
            Ok(await _r.BalanceSheetAsync(User.BooksClientId(), await CurrencyAsync(currency, ct), asAt ?? Today, ct, compare));
        [HttpGet("reports/cashbook"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Cashbook([FromQuery] Guid accountId, [FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
            Ok(await _r.CashbookAsync(User.BooksClientId(), accountId, currency, from ?? new DateTime(Today.Year, Today.Month, 1), to ?? Today, ct));
    }

    /// <summary>How the chart of accounts is numbered (code format, first digits per type, suggested codes), renumbering it, and importing a chart from CSV.</summary>
    [ApiController, Authorize, Route("api/v1/accounting/chart")]
    public class ChartController : ControllerBase
    {
        private readonly IChartService _s;
        public ChartController(IChartService s) => _s = s;

        [HttpGet("settings"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Settings(CancellationToken ct) => Ok(await _s.SetupAsync(User.BooksClientId(), ct));
        [HttpPut("settings"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveSettings(ChartSettingsDto body, CancellationToken ct) => Ok(await _s.SaveSettingsAsync(User.BooksClientId(), body, ct));
        [HttpGet("next-code"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> NextCode([FromQuery] string type, [FromQuery] Guid? parentId, CancellationToken ct) => Ok(await _s.NextCodeAsync(User.BooksClientId(), type, parentId, ct));
        [HttpPost("renumber/preview"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Preview(ChartSettingsDto body, CancellationToken ct) => Ok(await _s.PreviewRenumberAsync(User.BooksClientId(), body, ct));
        [HttpPost("renumber"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Renumber(ChartSettingsDto body, CancellationToken ct) => Ok(await _s.RenumberAsync(User.BooksClientId(), body, ct));
        [HttpPost("import"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Import(ImportChartRequest body, CancellationToken ct) => Ok(await _s.ImportAsync(User.BooksClientId(), body, ct));
    }

    /// <summary>What the company owes: suppliers, their bills, payments and the payables ageing. Bills and payments post to the books by themselves.</summary>
    [ApiController, Authorize, Route("api/v1/accounting")]
    public class PayablesController : ControllerBase
    {
        private readonly IPayablesService _s;
        private readonly IAccountingService _books;
        public PayablesController(IPayablesService s, IAccountingService books) { _s = s; _books = books; }

        [HttpGet("period"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Period(CancellationToken ct) => Ok(new PeriodDto(await _books.LockedUntilAsync(User.BooksClientId(), ct)));
        [HttpPut("period"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SetPeriod(SetPeriodRequest body, CancellationToken ct) => Ok(await _books.SetLockAsync(User.BooksClientId(), body.LockedUntil, ct));

        [HttpGet("suppliers"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Suppliers(CancellationToken ct) => Ok(await _s.ListSuppliersAsync(User.BooksClientId(), ct));
        [HttpPut("suppliers"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveSupplier(SaveSupplierRequest body, CancellationToken ct) => Ok(await _s.SaveSupplierAsync(User.BooksClientId(), body, ct));
        [HttpGet("suppliers/statement"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> SupplierStatement([FromQuery] Guid supplierId, [FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        { var today = DateTime.UtcNow.AddHours(2).Date; return Ok(await _s.StatementAsync(User.BooksClientId(), supplierId, currency, from ?? new DateTime(today.Year, 1, 1), to ?? today, ct)); }

        [HttpGet("bills"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Bills([FromQuery] string? status, [FromQuery] Guid? supplierId, [FromQuery] string? q, [FromQuery] string? currency, CancellationToken ct) => Ok(await _s.ListBillsAsync(User.BooksClientId(), new BillFilter(status, supplierId, q, currency), ct));
        [HttpPost("bills"), HasPermission(Perm.AccountingManage)]
        public async Task<IActionResult> CreateBill(CreateBillRequest body, [FromServices] IApprovalService approvals, CancellationToken ct)
        {
            var held = await approvals.GateAsync(User.BooksClientId(), "bill", body.Subtotal + body.Vat, body.Currency, $"Bill {body.Number}", null, body, ct);
            if (held != null) return Accepted(held);
            return Ok(await _s.CreateBillAsync(User.BooksClientId(), body, ct));
        }
        [HttpPost("bills/{id:guid}/void"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> VoidBill(Guid id, CancellationToken ct) => Ok(await _s.VoidBillAsync(User.BooksClientId(), id, ct));
        [HttpPost("bills/{id:guid}/pay"), HasPermission(Perm.AccountingManage)]
        public async Task<IActionResult> Pay(Guid id, PayBillRequest body, [FromServices] IApprovalService approvals, CancellationToken ct)
        {
            var (currency, number, supplier) = await approvals.BillAsync(User.BooksClientId(), id, ct);
            var held = await approvals.GateAsync(User.BooksClientId(), "bill_payment", body.Amount, currency, $"Pay {supplier}, bill {number}", id.ToString(), body, ct);
            if (held != null) return Accepted(held);
            return Ok(await _s.PayBillAsync(User.BooksClientId(), id, body, ct));
        }
        [HttpPost("bills/payments/{paymentId:guid}/void"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> VoidPayment(Guid paymentId, CancellationToken ct) => Ok(await _s.VoidPaymentAsync(User.BooksClientId(), paymentId, ct));
        [HttpGet("reports/payables"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Ageing([FromQuery] string currency, [FromQuery] DateTime? asAt, CancellationToken ct) => Ok(await _s.AgeingAsync(User.BooksClientId(), currency, asAt ?? DateTime.UtcNow.AddHours(2).Date, ct));
    }

    /// <summary>What customers owe (open fiscal invoices, payments received, ageing, statements) and the register of fixed assets with their depreciation.</summary>
    [ApiController, Authorize, Route("api/v1/accounting")]
    public class ReceivablesAssetsController : ControllerBase
    {
        private readonly IReceivablesService _r;
        private readonly IFixedAssetService _a;
        public ReceivablesAssetsController(IReceivablesService r, IFixedAssetService a) { _r = r; _a = a; }
        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        [HttpGet("receivables"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Open([FromQuery] string? status, [FromQuery] string? customer, [FromQuery] string? q, [FromQuery] string? currency, CancellationToken ct) => Ok(await _r.ListAsync(User.BooksClientId(), new OpenInvoiceFilter(status, customer, q, currency), ct));
        [HttpPost("receivables/{receiptId:guid}/receive"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Receive(Guid receiptId, ReceiveRequest body, CancellationToken ct) => Ok(await _r.ReceiveAsync(User.BooksClientId(), receiptId, body, ct));
        [HttpPost("receivables/payments/{paymentId:guid}/void"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> VoidPayment(Guid paymentId, CancellationToken ct) => Ok(await _r.VoidPaymentAsync(User.BooksClientId(), paymentId, ct));
        [HttpGet("receivables/settings"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Settings(CancellationToken ct) => Ok(await _r.GetSettingsAsync(User.BooksClientId(), ct));
        [HttpPut("receivables/settings"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SetTerms(SetTermsRequest body, CancellationToken ct) => Ok(await _r.SetTermsAsync(User.BooksClientId(), body.TermsDays, ct));
        [HttpGet("reports/receivables"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Ageing([FromQuery] string currency, [FromQuery] DateTime? asAt, CancellationToken ct) => Ok(await _r.AgeingAsync(User.BooksClientId(), currency, asAt ?? Today, ct));
        [HttpGet("customers/statement"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Statement([FromQuery] string customer, [FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
            Ok(await _r.StatementAsync(User.BooksClientId(), customer, currency, from ?? new DateTime(Today.Year, 1, 1), to ?? Today, ct));

        [HttpGet("assets"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Assets([FromQuery] DateTime? asAt, CancellationToken ct) => Ok(await _a.RegisterAsync(User.BooksClientId(), asAt ?? Today, ct));
        [HttpPost("assets"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> CreateAsset(CreateAssetRequest body, CancellationToken ct) => Ok(await _a.CreateAsync(User.BooksClientId(), body, ct));
        [HttpPost("assets/depreciate"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Depreciate(DepreciateRequest body, CancellationToken ct) => Ok(await _a.DepreciateAsync(User.BooksClientId(), body.UpTo, ct));
        [HttpPost("assets/{id:guid}/dispose"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Dispose(Guid id, DisposeAssetRequest body, CancellationToken ct) => Ok(await _a.DisposeAsync(User.BooksClientId(), id, body, ct));
    }

    /// <summary>The VAT return worked out from the books, and budgets compared with what actually happened.</summary>
    [ApiController, Authorize, Route("api/v1/accounting")]
    public class VatBudgetController : ControllerBase
    {
        private readonly IVatBooksService _v;
        private readonly IBudgetService _b;
        public VatBudgetController(IVatBooksService v, IBudgetService b) { _v = v; _b = b; }
        private static DateTime Today => DateTime.UtcNow.AddHours(2).Date;

        [HttpGet("vat/return"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Return([FromQuery] string currency, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
            Ok(await _v.ReturnAsync(User.BooksClientId(), currency, from ?? new DateTime(Today.Year, Today.Month, 1), to ?? Today, ct));
        [HttpPost("vat/settle"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Settle(SettleVatRequest body, CancellationToken ct) => Ok(await _v.SettleAsync(User.BooksClientId(), body, ct));

        [HttpGet("budgets"), HasPermission(Perm.AccountingView)] public async Task<IActionResult> Budgets(CancellationToken ct) => Ok(await _b.ListAsync(User.BooksClientId(), ct));
        [HttpPut("budgets"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> SaveBudget(SaveBudgetRequest body, CancellationToken ct) => Ok(await _b.SaveAsync(User.BooksClientId(), body, ct));
        [HttpDelete("budgets/{id:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> DeleteBudget(Guid id, CancellationToken ct) { await _b.DeleteAsync(User.BooksClientId(), id, ct); return NoContent(); }
        [HttpGet("budgets/{id:guid}/variance"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Variance(Guid id, [FromQuery] int? fromMonth, [FromQuery] int? toMonth, CancellationToken ct) => Ok(await _b.VsActualAsync(User.BooksClientId(), id, fromMonth ?? 1, toMonth ?? Today.Month, ct));
    }

    /// <summary>Bank reconciliation: import the statement, match it with the books, and record what the books missed.</summary>
    [ApiController, Authorize, Route("api/v1/accounting/bank")]
    public class BankReconController : ControllerBase
    {
        private readonly IBankReconService _s;
        public BankReconController(IBankReconService s) => _s = s;

        [HttpGet("reconciliation"), HasPermission(Perm.AccountingView)]
        public async Task<IActionResult> Reconciliation([FromQuery] Guid accountId, [FromQuery] DateTime? asAt, [FromQuery] decimal? statementBalance, CancellationToken ct) =>
            Ok(await _s.ReconciliationAsync(User.BooksClientId(), accountId, asAt ?? DateTime.UtcNow.AddHours(2).Date, statementBalance, ct));
        [HttpPost("statement/import"), HasPermission(Perm.AccountingManage), RequestSizeLimit(2_000_000)] public async Task<IActionResult> Import(ImportStatementRequest body, CancellationToken ct) => Ok(await _s.ImportAsync(User.BooksClientId(), body, ct));
        [HttpPost("statement/line"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> AddLine(AddStatementLineRequest body, CancellationToken ct) => Ok(await _s.AddLineAsync(User.BooksClientId(), body, ct));
        [HttpDelete("statement/{id:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> DeleteLine(Guid id, CancellationToken ct) { await _s.DeleteLineAsync(User.BooksClientId(), id, ct); return NoContent(); }
        [HttpPost("match"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Match(MatchRequest body, CancellationToken ct) { await _s.MatchAsync(User.BooksClientId(), body, ct); return NoContent(); }
        [HttpPost("unmatch/{statementLineId:guid}"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> Unmatch(Guid statementLineId, CancellationToken ct) { await _s.UnmatchAsync(User.BooksClientId(), statementLineId, ct); return NoContent(); }
        [HttpPost("auto-match"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> AutoMatch(AutoMatchBody body, CancellationToken ct) => Ok(new { matched = await _s.AutoMatchAsync(User.BooksClientId(), body.AccountId, ct) });
        [HttpPost("create-entry"), HasPermission(Perm.AccountingManage)] public async Task<IActionResult> CreateEntry(CreateFromStatementRequest body, CancellationToken ct) => Ok(await _s.CreateEntryAsync(User.BooksClientId(), body, ct));

        public sealed record AutoMatchBody(Guid AccountId);
    }
}
