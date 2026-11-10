using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Import;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Fdms.Api.Controllers
{
    /// <summary>What a visitor without an account may see and do: the plans, who runs the service, and registering a company (an operator approves it).</summary>
    [ApiController, AllowAnonymous, Route("api/v1/public")]
    public class PublicController : ControllerBase
    {
        private readonly IPlanService _plans;
        private readonly IOnboardingService _onboarding;
        private readonly IBillingProfileService _profile;
        public PublicController(IPlanService plans, IOnboardingService onboarding, IBillingProfileService profile) { _plans = plans; _onboarding = onboarding; _profile = profile; }

        [HttpGet("plans")]
        public async Task<IActionResult> Plans(CancellationToken ct) { Response.Headers.CacheControl = "public, max-age=60"; return Ok(await _plans.ListAsync(true, ct)); }

        /// <summary>The optional modules a plan can include, to name them on the pricing cards.</summary>
        [HttpGet("plan-modules")]
        public IActionResult PlanModuleList() { Response.Headers.CacheControl = "public, max-age=300"; return Ok(PlanModules.All.Select(m => new { m.Key, m.Label, m.Description })); }

        /// <summary>The Word tax-invoice template, free for anyone: a ready-made invoice with the fields ZIMRA fiscal invoices carry.</summary>
        [HttpGet("templates/tax-invoice.docx")]
        public IActionResult TaxInvoiceTemplate([FromServices] IInvoiceImportService import, [FromQuery] bool sample = false)
        {
            Response.Headers.CacheControl = "public, max-age=3600";
            return File(import.BuildWordTemplate(sample), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", sample ? "tax-invoice-example.docx" : "tax-invoice-template.docx");
        }

        [HttpGet("info")]
        public async Task<IActionResult> Info(CancellationToken ct)
        {
            var p = await _profile.LoadAsync(ct);
            return Ok(new { name = string.IsNullOrWhiteSpace(p.CompanyName) ? "FDMS Gateway" : p.CompanyName, email = p.Email, phone = p.Phone, website = p.Website });
        }

        /// <summary>Always the same answer, whether or not the address is already known, so addresses cannot be probed.</summary>
        [HttpPost("register"), EnableRateLimiting("login")]
        public async Task<IActionResult> Register(RegisterRequest body, CancellationToken ct) =>
            Accepted(await _onboarding.RegisterAsync(body, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));

        /// <summary>The 6-digit code from the confirmation email. With self-service sign-up the account opens at once (trial + first invoice).</summary>
        [HttpPost("register/verify"), EnableRateLimiting("login")]
        public async Task<IActionResult> Verify(VerifyRegistrationRequest body, CancellationToken ct) => Ok(await _onboarding.VerifyAsync(body, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));

        [HttpPost("register/resend"), EnableRateLimiting("login")]
        public async Task<IActionResult> Resend(ResendCodeRequest body, CancellationToken ct) => Ok(await _onboarding.ResendAsync(body, ct));
    }

    /// <summary>
    /// Gateways report the outcome of a payment here. Nothing in a message is trusted by itself: Paynow's is verified by its hash, Stripe's by its signature,
    /// and an EcoCash notification only makes us ask EcoCash directly.
    /// </summary>
    [ApiController, AllowAnonymous, Route("api/v1/payments")]
    public class PaymentCallbacksController : ControllerBase
    {
        private readonly IPaymentService _pay;
        public PaymentCallbacksController(IPaymentService pay) => _pay = pay;

        [HttpPost("paynow/result"), Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> PaynowResult(CancellationToken ct)
        {
            var fields = Request.Form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString())).ToList();
            return await _pay.HandlePaynowResultAsync(fields, ct) ? Ok() : BadRequest();
        }

        [HttpPost("stripe/webhook"), RequestSizeLimit(1_000_000)]
        public async Task<IActionResult> StripeWebhook(CancellationToken ct)
        {
            using var reader = new StreamReader(Request.Body);
            var payload = await reader.ReadToEndAsync(ct);
            return await _pay.HandleStripeWebhookAsync(payload, Request.Headers["Stripe-Signature"].ToString(), ct) ? Ok() : BadRequest();
        }

        [HttpPost("ecocash/notify"), RequestSizeLimit(100_000)]
        public async Task<IActionResult> EcoCashNotify(CancellationToken ct)
        {
            string? reference = null;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
                foreach (var name in new[] { "clientCorrelator", "referenceCode" })
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String) { reference = p.GetString(); break; }
            }
            catch (System.Text.Json.JsonException) { /* not JSON: nothing to look up */ }
            await _pay.HandleEcoCashNotifyAsync(reference, ct);
            return Ok();   // always the same answer: the caller learns nothing about which references exist
        }
    }

    /// <summary>A company's own subscription: usage, invoices, changing plan and paying.</summary>
    [ApiController, Authorize, HasPermission(Perm.BillingManage), Route("api/v1/billing")]
    public class BillingController : ControllerBase
    {
        private readonly IBillingService _billing;
        private readonly IPaymentService _pay;
        public BillingController(IBillingService billing, IPaymentService pay) { _billing = billing; _pay = pay; }
        public sealed record BankPaymentForm(string? Reference, string? Notes, IFormFile? Proof);

        [HttpGet] public async Task<IActionResult> Overview(CancellationToken ct) => Ok(await _billing.OverviewAsync(User.ClientId(), ct));

        /// <summary>Typing in the activation code the operator gave after a cash or bank payment: settles the invoice and switches the account on.</summary>
        [HttpPost("redeem"), EnableRateLimiting("login")]
        public async Task<IActionResult> Redeem(RedeemCodeRequest body, [FromServices] IActivationService codes, CancellationToken ct)
        {
            await codes.RedeemAsync(User.ClientId(), body.Code, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            return Ok(await _billing.OverviewAsync(User.ClientId(), ct));
        }

        /// <summary>An expired subscription: creates the invoice to renew it (or returns the open one).</summary>
        [HttpPost("renew")] public async Task<IActionResult> Renew(CancellationToken ct) => Ok(await _billing.RenewAsync(User.ClientId(), ct));

        /// <summary>Asking to use live (Production) FDMS devices. Demo (Test) devices need no request.</summary>
        [HttpGet("go-live")] public async Task<IActionResult> GoLive([FromServices] IGoLiveService s, CancellationToken ct) => Ok(await s.StatusAsync(User.ClientId(), ct));
        [HttpPost("go-live")] public async Task<IActionResult> RequestGoLive(SubmitGoLiveRequest body, [FromServices] IGoLiveService s, CancellationToken ct) => Ok(await s.SubmitAsync(User.ClientId(), body, ct));

        [HttpPost("change-plan")]
        public async Task<IActionResult> ChangePlan(ChangePlanRequest body, CancellationToken ct) => Ok(await _billing.ChangePlanAsync(User.ClientId(), body, ct));

        /// <summary>Extra devices, users and branches on top of the plan, paid monthly with the subscription.</summary>
        [HttpGet("addons")] public async Task<IActionResult> Addons([FromServices] IAddonService s, CancellationToken ct) => Ok(await s.MineAsync(User.ClientId(), ct));
        /// <summary>Raises an invoice for the rest of the current period; the add-ons switch on when it is paid.</summary>
        [HttpPost("addons")] public async Task<IActionResult> BuyAddons(ChangeAddonsRequest body, [FromServices] IAddonService s, CancellationToken ct) => Ok(await s.BuyAsync(User.ClientId(), body, ct));
        [HttpPost("addons/remove")] public async Task<IActionResult> RemoveAddons(ChangeAddonsRequest body, [FromServices] IAddonService s, CancellationToken ct) => Ok(await s.RemoveAsync(User.ClientId(), body, ct));
        [HttpDelete("addons/pending")] public async Task<IActionResult> CancelAddons([FromServices] IAddonService s, CancellationToken ct) => Ok(await s.CancelPendingAsync(User.ClientId(), ct));

        [HttpGet("invoices/{id:guid}")]
        public async Task<IActionResult> Invoice(Guid id, CancellationToken ct) => Ok(await _billing.GetInvoiceAsync(User.ClientId(), id, ct));

        [HttpGet("invoices/{id:guid}/pdf")]
        public async Task<IActionResult> InvoicePdf(Guid id, CancellationToken ct) => File(await _billing.InvoicePdfAsync(User.ClientId(), id, ct), "application/pdf", $"invoice-{id:N}.pdf");

        [HttpPost("invoices/{id:guid}/pay/paynow")]
        public async Task<IActionResult> PayPaynow(Guid id, PayPaynowRequest body, CancellationToken ct) => Ok(await _pay.PayWithPaynowAsync(User.ClientId(), id, body, ct));

        /// <summary>Pays with an EcoCash prompt on the phone (EcoCash direct).</summary>
        [HttpPost("invoices/{id:guid}/pay/ecocash")]
        public async Task<IActionResult> PayEcoCash(Guid id, PayEcoCashRequest body, CancellationToken ct) => Ok(await _pay.PayWithEcoCashAsync(User.ClientId(), id, body, ct));

        [HttpPost("invoices/{id:guid}/pay/stripe")]
        public async Task<IActionResult> PayStripe(Guid id, CancellationToken ct) => Ok(await _pay.PayWithStripeAsync(User.ClientId(), id, ct));

        [HttpPost("invoices/{id:guid}/pay/bank"), RequestSizeLimit(6_000_000)]
        public Task<IActionResult> PayBank(Guid id, [FromForm] BankPaymentForm form, CancellationToken ct) => PayManual(id, "bank", form, ct);

        /// <summary>Pay outside any gateway (bank, ecocash-manual, paypal), then tell us: the reference is required and must not have been used before; a proof of payment (PDF or picture, 5 MB) is optional. The operator confirms it.</summary>
        [HttpPost("invoices/{id:guid}/pay/manual/{provider}"), RequestSizeLimit(6_000_000)]
        public async Task<IActionResult> PayManual(Guid id, string provider, [FromForm] BankPaymentForm form, CancellationToken ct)
        {
            byte[]? bytes = null;
            if (form.Proof is { Length: > 0 })
            {
                if (form.Proof.Length > 5_000_000) throw GatewayException.BadRequest("PROOF_SIZE", "The proof of payment may be at most 5 MB.");
                using var ms = new MemoryStream();
                await form.Proof.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            return Ok(await _pay.PayManualAsync(User.ClientId(), id, provider, form.Reference, form.Notes, bytes, form.Proof?.FileName, form.Proof?.ContentType, ct));
        }

        /// <summary>Checks on a payment; asks Paynow when it is still pending. The page calls this when the customer returns from the payment page.</summary>
        [HttpGet("payments/{id:guid}")]
        public async Task<IActionResult> Payment(Guid id, CancellationToken ct) => Ok(await _pay.RefreshAsync(User.ClientId(), id, ct));

        [HttpGet("payments/{id:guid}/proof")]
        public async Task<IActionResult> Proof(Guid id, CancellationToken ct)
        {
            var p = await _pay.GetProofAsync(User.ClientId(), id, ct) ?? throw GatewayException.NotFound("Proof");
            return File(p.Bytes, p.ContentType, p.Name);
        }
    }

    /// <summary>Plans (platform operator). The public price list is GET /api/v1/public/plans.</summary>
    [ApiController, Authorize, HasPermission(Perm.PlansManage), Route("api/v1/plans")]
    public class PlansController : ControllerBase
    {
        private readonly IPlanService _s;
        public PlansController(IPlanService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(false, ct));
        [HttpPut] public async Task<IActionResult> Save(SavePlanRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(body, ct));
        [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await _s.DeleteAsync(id, ct); return NoContent(); }

        /// <summary>The owner's prices for usage (SMS, own WhatsApp number) and add-ons (extra devices, users, branches). Changes apply from the next invoice.</summary>
        /// <summary>The optional modules a plan can include (for the plan editor and the pricing cards).</summary>
        [HttpGet("modules")] public IActionResult Modules() => Ok(PlanModules.All);
        [HttpGet("usage-pricing")] public async Task<IActionResult> UsagePricing([FromServices] IAddonService s, CancellationToken ct) => Ok(await s.PricingAsync(ct));
        [HttpPut("usage-pricing")] public async Task<IActionResult> SaveUsagePricing(UsagePricingDto body, [FromServices] IAddonService s, CancellationToken ct) => Ok(await s.SavePricingAsync(body, ct));
    }

    /// <summary>Companies that asked to join (platform operator).</summary>
    [ApiController, Authorize, HasPermission(Perm.ApplicationsManage), Route("api/v1/applications")]
    public class ApplicationsController : ControllerBase
    {
        private readonly IOnboardingService _s;
        public ApplicationsController(IOnboardingService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct) => Ok(await _s.ListAsync(status, ct));
        [HttpGet("{id:guid}/logo")]
        public async Task<IActionResult> Logo(Guid id, CancellationToken ct)
        {
            var logo = await _s.GetLogoAsync(id, ct) ?? throw GatewayException.NotFound("Logo");
            return File(logo.Bytes, logo.ContentType);
        }
        [HttpPost("{id:guid}/approve")] public async Task<IActionResult> Approve(Guid id, ApproveApplicationRequest body, CancellationToken ct) => Ok(await _s.ApproveAsync(id, body, ct));
        [HttpPost("{id:guid}/reject")] public async Task<IActionResult> Reject(Guid id, RejectApplicationRequest body, CancellationToken ct) => Ok(await _s.RejectAsync(id, body.Reason, ct));
    }

    /// <summary>Subscriptions, invoices and payments of all companies (platform operator).</summary>
    [ApiController, Authorize, HasPermission(Perm.PaymentsManage), Route("api/v1/admin/billing")]
    public class BillingAdminController : ControllerBase
    {
        private readonly IBillingService _billing;
        private readonly IPaymentService _pay;
        public BillingAdminController(IBillingService billing, IPaymentService pay) { _billing = billing; _pay = pay; }

        [HttpGet("subscriptions")] public async Task<IActionResult> Subscriptions(CancellationToken ct) => Ok(await _billing.ListSubscriptionsAsync(ct));
        [HttpPut("subscriptions/{clientId:guid}")] public async Task<IActionResult> UpdateSubscription(Guid clientId, SubscriptionAdminRequest body, CancellationToken ct) => Ok(await _billing.AdminUpdateAsync(clientId, body, ct));
        [HttpPost("subscriptions/{clientId:guid}/invoice")] public async Task<IActionResult> NewInvoice(Guid clientId, CancellationToken ct) => Ok(await _billing.CreateInvoiceAsync(clientId, ct));

        /// <summary>Demo companies that never paid, and when each will be deleted. Keep = never delete it automatically.</summary>
        [HttpGet("demo-accounts")] public async Task<IActionResult> DemoAccounts([FromServices] IDemoCleanupService demo, CancellationToken ct) => Ok(await demo.ListAsync(ct));
        [HttpPost("demo-accounts/{clientId:guid}/keep")] public async Task<IActionResult> KeepDemo(Guid clientId, KeepDemoRequest body, [FromServices] IDemoCleanupService demo, CancellationToken ct) { await demo.SetKeepAsync(clientId, body.Keep, ct); return NoContent(); }

        [HttpGet("invoices")] public async Task<IActionResult> Invoices([FromQuery] string? status, CancellationToken ct) => Ok(await _billing.ListInvoicesAsync(status, ct));
        [HttpGet("invoices/{id:guid}/pdf")] public async Task<IActionResult> InvoicePdf(Guid id, CancellationToken ct) => File(await _billing.InvoicePdfAsync(null, id, ct), "application/pdf", $"invoice-{id:N}.pdf");
        [HttpPost("invoices/{id:guid}/mark-paid")] public async Task<IActionResult> MarkPaid(Guid id, MarkPaidRequest body, CancellationToken ct) => Ok(await _pay.MarkInvoicePaidAsync(id, body, ct));

        [HttpGet("payments")] public async Task<IActionResult> Payments([FromQuery] string? status, CancellationToken ct) => Ok(await _pay.ListAsync(status, ct));
        [HttpGet("payments/{id:guid}/proof")]
        public async Task<IActionResult> Proof(Guid id, CancellationToken ct)
        {
            var p = await _pay.GetProofAsync(null, id, ct) ?? throw GatewayException.NotFound("Proof");
            return File(p.Bytes, p.ContentType, p.Name);
        }
        [HttpPost("payments/{id:guid}/confirm")] public async Task<IActionResult> Confirm(Guid id, ConfirmPaymentRequest body, CancellationToken ct) => Ok(await _pay.ConfirmAsync(id, body.Reference, body.SendCode, ct));
        [HttpPost("payments/{id:guid}/reject")] public async Task<IActionResult> Reject(Guid id, RejectPaymentRequest body, CancellationToken ct) => Ok(await _pay.RejectAsync(id, body.Reason, ct));
        [HttpPost("payments/{id:guid}/refresh")] public async Task<IActionResult> Refresh(Guid id, CancellationToken ct) => Ok(await _pay.RefreshAsync(null, id, ct));
    }

    /// <summary>How the platform's own invoices look (company details, logo, colour, bank details, terms), plus whether Paynow is connected (platform operator).</summary>
    [ApiController, Authorize, HasPermission(Perm.SettingsManage), Route("api/v1/settings/billing")]
    public class BillingProfileController : ControllerBase
    {
        private readonly IBillingProfileService _s;
        public BillingProfileController(IBillingProfileService s) => _s = s;

        [HttpGet] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _s.GetAsync(ct));
        [HttpPut] public async Task<IActionResult> Save(BillingProfile body, CancellationToken ct) => Ok(await _s.SaveAsync(body, ct));

        [HttpPost("logo")]
        public async Task<IActionResult> SetLogo(IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0) throw GatewayException.BadRequest("FILE_REQUIRED", "Choose an image file.");
            if (file.Length > 300_000) throw GatewayException.BadRequest("LOGO_SIZE", "The logo must be an image of at most 300 KB.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            await _s.SetLogoAsync(ms.ToArray(), ct);
            return Ok(await _s.GetAsync(ct));
        }

        [HttpDelete("logo")] public async Task<IActionResult> RemoveLogo(CancellationToken ct) { await _s.RemoveLogoAsync(ct); return Ok(await _s.GetAsync(ct)); }

        [HttpGet("logo")]
        public async Task<IActionResult> GetLogo(CancellationToken ct)
        {
            var logo = await _s.GetLogoAsync(ct) ?? throw GatewayException.NotFound("Logo");
            return File(logo.Bytes, logo.ContentType);
        }

        [HttpPost("preview")] public async Task<IActionResult> Preview(BillingProfile body, CancellationToken ct) => File(await _s.PreviewAsync(body, ct), "image/png");
    }

    /// <summary>Payment gateway credentials (platform operator). Secrets are accepted and stored encrypted, and are never returned.</summary>
    [ApiController, Authorize, HasPermission(Perm.SettingsManage), Route("api/v1/settings/gateways")]
    public class PaymentGatewaysController : ControllerBase
    {
        private readonly IPaymentGatewaySettings _s;
        private readonly ISettingsService _settings;
        public PaymentGatewaysController(IPaymentGatewaySettings s, ISettingsService settings) { _s = s; _settings = settings; }
        private Task<string> Api(CancellationToken ct) => _settings.EffectiveApiUrlAsync($"{Request.Scheme}://{Request.Host}", ct);

        [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(await Api(ct), ct));
        [HttpPut("{code}")] public async Task<IActionResult> Save(string code, SaveGatewayRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(code, body, await Api(ct), ct));
    }

    /// <summary>Requests to go live (platform operator): FDMS has a demo and a live environment, and live devices are switched on here.</summary>
    [ApiController, Authorize, HasPermission(Perm.ApplicationsManage), Route("api/v1/admin/go-live")]
    public class GoLiveAdminController : ControllerBase
    {
        private readonly IGoLiveService _s;
        public GoLiveAdminController(IGoLiveService s) => _s = s;

        [HttpGet] public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct) => Ok(await _s.ListAsync(status, ct));
        /// <summary>Approve, with the live device ZIMRA issued (device ID, activation key) when there is one: the company sees it and registers it.</summary>
        [HttpPost("{id:guid}/approve")] public async Task<IActionResult> Approve(Guid id, GoLiveDecisionRequest body, CancellationToken ct) => Ok(await _s.ApproveAsync(id, body.Note, ct, body.Fee, body.LiveDeviceId, body.ActivationKey, body.SerialNo));
        /// <summary>The email to ZIMRA, written out with the company's six test documents (edit it, then send).</summary>
        [HttpGet("{id:guid}/zimra-email")] public async Task<IActionResult> ZimraEmail(Guid id, CancellationToken ct) => Ok(await _s.ZimraEmailAsync(id, ct));
        [HttpPost("{id:guid}/send-to-zimra")] public async Task<IActionResult> SendToZimra(Guid id, SendToZimraRequest body, CancellationToken ct) => Ok(await _s.SendToZimraAsync(id, body, ct));
        [HttpGet("{id:guid}/pack")] public async Task<IActionResult> Pack(Guid id, CancellationToken ct) { var (zip, name) = await _s.PackAsync(id, ct); return File(zip, "application/zip", name); }
        [HttpGet("{id:guid}/documents/{receiptId:guid}/pdf")] public async Task<IActionResult> DocumentPdf(Guid id, Guid receiptId, CancellationToken ct) { var (pdf, name) = await _s.DocumentPdfAsync(id, receiptId, ct); return File(pdf, "application/pdf", name); }
        [HttpPost("{id:guid}/reject")] public async Task<IActionResult> Reject(Guid id, RejectApplicationRequest body, CancellationToken ct) => Ok(await _s.RejectAsync(id, body.Reason, ct));
        /// <summary>Companies allowed to use live devices, and whether they went through the ZIMRA process.</summary>
        [HttpGet("live-companies")] public async Task<IActionResult> LiveCompanies(CancellationToken ct) => Ok(await _s.LiveCompaniesAsync(ct));
        [HttpPost("clients/{clientId:guid}/revoke")] public async Task<IActionResult> Revoke(Guid clientId, CancellationToken ct) { await _s.RevokeAsync(clientId, ct); return NoContent(); }
    }
}
