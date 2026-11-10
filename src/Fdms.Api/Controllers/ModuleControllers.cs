using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>The company's buyer register: pick a customer on an invoice instead of retyping their name, TIN and VAT number.</summary>
    [ApiController, Authorize, HasPermission(Perm.CustomersManage), Route("api/v1/customers")]
    public class CustomersController : ControllerBase
    {
        private readonly ICustomerService _s;
        public CustomersController(ICustomerService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] bool includeInactive = false, CancellationToken ct = default) => Ok(await _s.ListAsync(User.ClientId(), search, includeInactive, ct));
        [HttpPut] public async Task<IActionResult> Save(SaveCustomerRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(User.ClientId(), body, ct));
        [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteAsync(User.ClientId(), id, ct); return NoContent(); }
    }

    /// <summary>The item catalogue: each item's HS code and tax kept once.</summary>
    [ApiController, Authorize, HasPermission(Perm.ProductsManage), Route("api/v1/products")]
    public class ProductsController : ControllerBase
    {
        private readonly IProductService _s;
        public ProductsController(IProductService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] bool includeInactive = false, CancellationToken ct = default) => Ok(await _s.ListAsync(User.ClientId(), search, includeInactive, ct));
        [HttpPut] public async Task<IActionResult> Save(SaveProductRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(User.ClientId(), body, ct));
        [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteAsync(User.ClientId(), id, ct); return NoContent(); }
    }

    /// <summary>Sales and VAT summaries and fiscal-day totals, as JSON or CSV, for a period of up to 400 days (Zimbabwe dates).</summary>
    [ApiController, Authorize, HasPermission(Perm.ReportsView), Route("api/v1/reports")]
    public class ReportsController : ControllerBase
    {
        private readonly IReportService _s;
        public ReportsController(IReportService s) => _s = s;

        [HttpGet("sales")] public async Task<IActionResult> Sales([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? deviceId, CancellationToken ct) => Ok(await _s.SalesAsync(User.ClientId(), from, to, deviceId, ct));
        [HttpGet("sales.csv")]
        public async Task<IActionResult> SalesCsv([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? deviceId, CancellationToken ct) =>
            File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(await _s.SalesCsvAsync(User.ClientId(), from, to, deviceId, ct))).ToArray(), "text/csv", $"sales-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
        [HttpGet("fiscal-days")] public async Task<IActionResult> FiscalDays([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? deviceId, CancellationToken ct) => Ok(await _s.FiscalDaysAsync(User.ClientId(), from, to, deviceId, ct));
        [HttpGet("fiscal-days.csv")]
        public async Task<IActionResult> FiscalDaysCsv([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? deviceId, CancellationToken ct) =>
            File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(await _s.FiscalDaysCsvAsync(User.ClientId(), from, to, deviceId, ct))).ToArray(), "text/csv", $"fiscal-days-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
    }

    /// <summary>One list of everything that makes ZIMRA reject, block or flag the company, with the page that fixes each.</summary>
    [ApiController, Authorize, HasPermission(Perm.ComplianceView), Route("api/v1/compliance")]
    public class ComplianceController : ControllerBase
    {
        private readonly IComplianceService _s;
        public ComplianceController(IComplianceService s) => _s = s;
        [HttpGet] public async Task<IActionResult> Check(CancellationToken ct) => Ok(await _s.CheckAsync(User.ClientId(), ct));
    }
}
