using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Import;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>Send a fiscal invoice to the customer: a link anyone can open, or an email with the PDF attached.</summary>
    [ApiController, Authorize, HasPermission(Perm.ReceiptsView), Route("api/v1/receipts/{id:guid}")]
    public class ReceiptSharingController : ControllerBase
    {
        private readonly IInvoiceShareService _s;
        private readonly ISmsService _sms;
        public ReceiptSharingController(IInvoiceShareService s, ISmsService sms) { _s = s; _sms = sms; }

        /// <summary>A link to a public page showing the invoice, its QR code, the PDF and the ZIMRA verification link. Valid for 90 days.</summary>
        [HttpPost("share")] public async Task<IActionResult> Share(Guid id, CancellationToken ct) => Ok(await _s.CreateLinkAsync(User.ClientId(), id, ct));

        [HttpPost("email")] public async Task<IActionResult> Email(Guid id, SendInvoiceRequest body, CancellationToken ct) => Ok(await _s.EmailAsync(User.ClientId(), id, body, ct));

        /// <summary>Texts the invoice number, total and the view-and-verify link to the customer (the given phone, else the buyer's phone on the invoice).</summary>
        [HttpPost("sms")] public async Task<IActionResult> Sms(Guid id, SendInvoiceSmsRequest body, CancellationToken ct) => Ok(await _sms.SendInvoiceAsync(User.ClientId(), id, body, ct));
    }

    /// <summary>What a customer sees when they open the link a company sent them. No sign-in: the signed link itself is the permission, for that one invoice only.</summary>
    [ApiController, AllowAnonymous, Route("api/v1/public")]
    public class PublicShareController : ControllerBase
    {
        private readonly IInvoiceShareService _share;
        private readonly IBrandService _brand;
        public PublicShareController(IInvoiceShareService share, IBrandService brand) { _share = share; _brand = brand; }

        [HttpGet("invoices/{token}")] public async Task<IActionResult> Invoice(string token, CancellationToken ct) => Ok(await _share.ResolveAsync(token, ct));

        [HttpGet("invoices/{token}/pdf")]
        public async Task<IActionResult> Pdf(string token, [FromQuery] string format = "a4", CancellationToken ct = default)
        {
            var bytes = await _share.PublicPdfAsync(token, !format.Equals("receipt", StringComparison.OrdinalIgnoreCase), ct);
            Response.Headers.CacheControl = "private, no-store";
            return File(bytes, "application/pdf", "invoice.pdf");
        }

        [HttpGet("invoices/{token}/qr")] public async Task<IActionResult> Qr(string token, CancellationToken ct) => File(await _share.PublicQrAsync(token, ct), "image/png");

        [HttpGet("invoices/{token}/logo")]
        public async Task<IActionResult> InvoiceLogo(string token, CancellationToken ct)
        {
            var l = await _share.PublicLogoAsync(token, ct) ?? throw GatewayException.NotFound("Logo");
            return File(l.Bytes, l.ContentType);
        }

        /// <summary>The company's name, colour and whether it has a logo, for its own sign-in page (/login?company=word).</summary>
        [HttpGet("brand/{slug}")] public async Task<IActionResult> Brand(string slug, CancellationToken ct) => Ok(await _brand.PublicAsync(slug, ct) ?? throw GatewayException.NotFound("Company"));

        [HttpGet("brand/{slug}/logo")]
        public async Task<IActionResult> BrandLogo(string slug, CancellationToken ct)
        {
            var l = await _brand.PublicLogoAsync(slug, ct) ?? throw GatewayException.NotFound("Logo");
            Response.Headers.CacheControl = "public, max-age=300";
            return File(l.Bytes, l.ContentType);
        }
    }

    /// <summary>The company's own branding: its sign-in word, colour and logo.</summary>
    [ApiController, Authorize, HasPermission(Perm.DevicesView), Route("api/v1/company")]
    public class CompanyController : ControllerBase
    {
        private readonly IBrandService _s;
        public CompanyController(IBrandService s) => _s = s;

        [HttpGet("brand")] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(User.ClientId(), ct));
        [HttpPut("brand"), HasPermission(Perm.TemplatesManage)] public async Task<IActionResult> Save(SaveBrandRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(User.ClientId(), body, ct));

        /// <summary>The company logo (from its default invoice template) for the sidebar.</summary>
        [HttpGet("logo")]
        public async Task<IActionResult> Logo(CancellationToken ct)
        {
            var l = await _s.LogoAsync(User.ClientId(), ct) ?? throw GatewayException.NotFound("Logo");
            Response.Headers.CacheControl = "private, max-age=60";
            return File(l.Bytes, l.ContentType);
        }
    }

    /// <summary>The bell: things the company should know about, and where its alerts go.</summary>
    [ApiController, Authorize, HasPermission(Perm.NotificationsView), Route("api/v1/notifications")]
    public class NotificationsController : ControllerBase
    {
        private readonly INotificationService _s;
        public NotificationsController(INotificationService s) => _s = s;

        /// <summary>Whose bell: the company's, or (for the platform's own staff) the platform's: new registrations, payments to confirm, go-live requests. Partners have none yet.</summary>
        private Guid? Inbox() => User.ClientIdOrNull() ?? (User.PartnerIdOrNull() == null && (User.HasClaim(Perm.ClaimType, Perm.ApplicationsManage) || User.HasClaim(Perm.ClaimType, Perm.PaymentsManage) || User.HasClaim(Perm.ClaimType, Perm.ClientsManage)) ? Guid.Empty /* NotificationService.Platform */ : null);

        [HttpGet] public async Task<IActionResult> List([FromQuery] bool unreadOnly = false, [FromQuery] int take = 50, CancellationToken ct = default) =>
            Inbox() is { } inbox ? Ok(await _s.ListAsync(inbox, unreadOnly, take, ct)) : Ok(new NotificationList(0, Array.Empty<NotificationDto>()));

        public sealed record NotificationReadBody(IReadOnlyList<Guid>? Ids);
        [HttpPost("read")] public async Task<IActionResult> Read(NotificationReadBody body, CancellationToken ct) { if (Inbox() is { } inbox) await _s.MarkReadAsync(inbox, body.Ids, ct); return NoContent(); }

        /// <summary>Emails waiting to be retried, or recently sent or given up on.</summary>
        [HttpGet("outbox"), HasPermission(Perm.AlertsManage)] public async Task<IActionResult> Outbox([FromServices] IEmailOutbox o, CancellationToken ct) => Ok(await o.ListAsync(User.ClientId(), ct));
        [HttpPost("outbox/{id:guid}/retry"), HasPermission(Perm.AlertsManage)] public async Task<IActionResult> RetryOutbox(Guid id, [FromServices] IEmailOutbox o, CancellationToken ct) { await o.RetryNowAsync(User.ClientId(), id, ct); return NoContent(); }
        [HttpDelete("outbox/{id:guid}"), HasPermission(Perm.AlertsManage)] public async Task<IActionResult> CancelOutbox(Guid id, [FromServices] IEmailOutbox o, CancellationToken ct) { await o.CancelAsync(User.ClientId(), id, ct); return NoContent(); }

        [HttpGet("settings"), HasPermission(Perm.AlertsManage)] public async Task<IActionResult> Settings(CancellationToken ct) => Ok(await _s.GetSettingsAsync(User.ClientId(), ct));
        [HttpPut("settings"), HasPermission(Perm.AlertsManage)] public async Task<IActionResult> Save(SaveAlertSettingsRequest body, CancellationToken ct) => Ok(await _s.SaveSettingsAsync(User.ClientId(), body, ct));
    }

    /// <summary>Is each device healthy, and what fixes it?</summary>
    [ApiController, Authorize, HasPermission(Perm.DevicesView), Route("api/v1/health")]
    public class HealthController : ControllerBase
    {
        private readonly IHealthService _s;
        private readonly IReceiptService _receipts;
        public HealthController(IHealthService s, IReceiptService receipts) { _s = s; _receipts = receipts; }

        /// <summary>live=true also asks ZIMRA whether it can be reached.</summary>
        [HttpGet] public async Task<IActionResult> Check([FromQuery] Guid? deviceId, [FromQuery] bool live = false, CancellationToken ct = default) => Ok(await _s.CheckAsync(User.ClientId(), deviceId, live, ct));

        /// <summary>Tries to deliver every queued receipt of the device now (oldest first).</summary>
        [HttpPost("devices/{id:guid}/retry-pending"), HasPermission(Perm.ReceiptsSubmit)]
        public async Task<IActionResult> RetryPending(Guid id, CancellationToken ct)
        {
            var clientId = User.ClientId();
            var page = await _receipts.ListAsync(clientId, new ReceiptListFilter(id, "Pending", null, null, null, 1, 100, null, null), ct);
            int delivered = 0, stillWaiting = 0;
            foreach (var r in page.Items.OrderBy(r => r.ReceiptGlobalNo))
            {
                try { var res = await _receipts.RetryAsync(clientId, r.Id, ct); if (res.Status == "Pending") { stillWaiting++; break; } delivered++; }   // in order: stop at the first one that still cannot go
                catch (Exception ex) when (ex is GatewayException or Fdms.Application.Fdms.FdmsException) { stillWaiting++; break; }
            }
            return Ok(new { delivered, stillWaiting = Math.Max(stillWaiting, page.Items.Count - delivered) });
        }
    }

    /// <summary>How much of the plan is used.</summary>
    [ApiController, Authorize, HasPermission(Perm.DevicesView), Route("api/v1/usage")]
    public class UsageController : ControllerBase
    {
        private readonly IUsageService _s;
        public UsageController(IUsageService s) => _s = s;
        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(User.ClientId(), ct));
    }

    /// <summary>Items to suggest while typing an invoice.</summary>
    [ApiController, Authorize, HasPermission(Perm.ReceiptsSubmit), Route("api/v1/suggest")]
    public class SuggestController : ControllerBase
    {
        private readonly ISuggestService _s;
        public SuggestController(ISuggestService s) => _s = s;
        [HttpGet("items")] public async Task<IActionResult> Items([FromQuery] string? q, [FromQuery] int take = 12, CancellationToken ct = default) => Ok(await _s.ItemsAsync(User.ClientId(), q, take, ct));
    }

    /// <summary>Quotations that can be emailed and then become fiscal invoices.</summary>
    [ApiController, Authorize, HasPermission(Perm.QuotesManage), Route("api/v1/quotes")]
    public class QuotesController : ControllerBase
    {
        private readonly IQuoteService _s;
        public QuotesController(IQuoteService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? search, CancellationToken ct) => Ok(await _s.ListAsync(User.ClientId(), status, search, ct));
        [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await _s.GetAsync(User.ClientId(), id, ct));
        [HttpPut] public async Task<IActionResult> Save(SaveQuoteRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(User.ClientId(), body, ct));
        [HttpPost("{id:guid}/status")] public async Task<IActionResult> Status(Guid id, QuoteStatusRequest body, CancellationToken ct) => Ok(await _s.SetStatusAsync(User.ClientId(), id, body.Status, ct));
        [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteAsync(User.ClientId(), id, ct); return NoContent(); }

        /// <summary>Issues the quote as a fiscal invoice on the chosen device. Doing it twice returns the same invoice.</summary>
        [HttpPost("{id:guid}/convert"), HasPermission(Perm.ReceiptsSubmit)]
        public async Task<IActionResult> Convert(Guid id, ConvertQuoteRequest body, CancellationToken ct) => Ok(await _s.ConvertAsync(User.ClientId(), id, body, ct));

        [HttpGet("{id:guid}/pdf")] public async Task<IActionResult> Pdf(Guid id, CancellationToken ct) => File(await _s.PdfAsync(User.ClientId(), id, ct), "application/pdf", "quotation.pdf");

        public sealed record QuoteEmailBody(string? To, string? Message);
        [HttpPost("{id:guid}/email")] public async Task<IActionResult> Email(Guid id, QuoteEmailBody body, CancellationToken ct) => Ok(await _s.EmailAsync(User.ClientId(), id, body.To, body.Message, ct));
    }

    /// <summary>Invoices issued again on a schedule.</summary>
    [ApiController, Authorize, HasPermission(Perm.RecurringManage), HasPermission(Perm.ReceiptsSubmit), Route("api/v1/recurring")]
    public class RecurringController : ControllerBase
    {
        private readonly IRecurringService _s;
        public RecurringController(IRecurringService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(User.ClientId(), ct));
        [HttpPut] public async Task<IActionResult> Save(SaveRecurringRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(User.ClientId(), body, ct));

        public sealed record ScheduleActiveBody(bool Active);
        [HttpPost("{id:guid}/active")] public async Task<IActionResult> Active(Guid id, ScheduleActiveBody body, CancellationToken ct) => Ok(await _s.SetActiveAsync(User.ClientId(), id, body.Active, ct));
        [HttpPost("{id:guid}/run")] public async Task<IActionResult> Run(Guid id, CancellationToken ct) => Ok(await _s.RunNowAsync(User.ClientId(), id, ct));
        [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteAsync(User.ClientId(), id, ct); return NoContent(); }
    }

    /// <summary>The VAT summary for a period, as JSON, Excel or PDF.</summary>
    [ApiController, Authorize, HasPermission(Perm.ReportsView), Route("api/v1/reports/vat-return")]
    public class VatReturnController : ControllerBase
    {
        private readonly IVatReturnService _s;
        public VatReturnController(IVatReturnService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Json([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? deviceId, CancellationToken ct) => Ok(await _s.BuildAsync(User.ClientId(), from, to, deviceId, ct));

        [HttpGet("xlsx")]
        public async Task<IActionResult> Xlsx([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? deviceId, CancellationToken ct) =>
            File(await _s.XlsxAsync(User.ClientId(), from, to, deviceId, ct), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"vat-summary-{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx");

        [HttpGet("pdf")]
        public async Task<IActionResult> Pdf([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] Guid? deviceId, CancellationToken ct) =>
            File(await _s.PdfAsync(User.ClientId(), from, to, deviceId, ct), "application/pdf", $"vat-summary-{from:yyyyMMdd}-{to:yyyyMMdd}.pdf");
    }
}
