using System.Net;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Services;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>The two addresses other people need: the API (payment callbacks) and the web app (links in emails, payment return page).</summary>
    public sealed class PublicUrls
    {
        private readonly ISettingsService _settings;
        private readonly IHttpContextAccessor _http;
        private readonly IConfiguration _cfg;
        public PublicUrls(ISettingsService settings, IHttpContextAccessor http, IConfiguration cfg) { _settings = settings; _http = http; _cfg = cfg; }

        public async Task<string> ApiAsync(CancellationToken ct)
        {
            var r = _http.HttpContext?.Request;
            return await _settings.EffectiveApiUrlAsync(r == null ? "http://localhost" : $"{r.Scheme}://{r.Host}", ct);
        }

        /// <summary>Where the web console lives (set in Settings); payment gateways send customers back here.</summary>
        public async Task<string> AppAsync(CancellationToken ct)
        {
            var r = _http.HttpContext?.Request;
            return await _settings.EffectiveAppUrlAsync(r == null ? "http://localhost" : $"{r.Scheme}://{r.Host}", ct);
        }
    }

    /// <summary>
    /// Self-service sign-up with operator approval. An application is just a row (the password stored hashed): no company and no user exist
    /// until the operator approves, so a stranger can never reach anything by registering.
    /// </summary>
    public sealed class OnboardingService : IOnboardingService
    {
        private static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        private readonly UserManager<ApplicationUser> _users;
        private readonly IPasswordHasher<ApplicationUser> _hasher;
        private readonly IBillingService _billing;
        private readonly IEmailSender _email;
        private readonly PublicUrls _urls;
        private readonly IConfiguration _cfg;
        private readonly ICallerScope _scope;
        private readonly INotificationService _notify;
        private readonly ISmsDispatcher _sms;

        public OnboardingService(FdmsDbContext db, UserManager<ApplicationUser> users, IPasswordHasher<ApplicationUser> hasher, IBillingService billing, IEmailSender email, PublicUrls urls, IConfiguration cfg, ICallerScope scope, INotificationService notify, ISmsDispatcher sms)
        { _db = db; _users = users; _hasher = hasher; _billing = billing; _email = email; _urls = urls; _cfg = cfg; _scope = scope; _notify = notify; _sms = sms; }

        /// <summary>Self-service sign-up (the default): the applicant confirms the email with a code and the company opens at once, on a trial with its first invoice ready.
        /// Off (Onboarding:SelfService = false): every registration waits for the operator, as before.</summary>
        private bool SelfService => _cfg.GetValue("Onboarding:SelfService", true);
        private const int CodeMinutes = 15;
        private const int MaxCodeTries = 5;
        private const string Sent = "We emailed a 6-digit code to {0}. Enter it to confirm your email and open your account. It works for 15 minutes.";

        private static string HashCode(Guid appId, string code) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(appId.ToString("N") + ":" + code.Trim()))).ToLowerInvariant();

        private async Task SendCodeAsync(ClientApplication app, CancellationToken ct)
        {
            var code = System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");
            app.VerifyCodeHash = HashCode(app.Id, code); app.VerifyExpiresAt = DateTime.UtcNow.AddMinutes(CodeMinutes); app.VerifySentAt = DateTime.UtcNow; app.VerifyAttempts = 0;
            await _db.SaveChangesAsync(ct);
            var html = $@"<div style=""font-family:Segoe UI,Arial,sans-serif;max-width:480px;margin:auto;color:#0f172a"">
<h2 style=""margin:0 0 12px"">Confirm your email</h2>
<p>Hello {WebUtility.HtmlEncode(app.ContactName)},</p>
<p>Use this code to confirm your email and open <b>{WebUtility.HtmlEncode(app.CompanyName)}</b>:</p>
<p style=""font-size:32px;font-weight:700;letter-spacing:8px;background:#f1f5f9;border-radius:12px;padding:16px;text-align:center"">{code}</p>
<p>It works for {CodeMinutes} minutes. If you did not register, ignore this email.</p>
</div>";
            await _email.SendAsync(app.Email, "Your FiscalHub confirmation code", html, ct);
            if (!string.IsNullOrWhiteSpace(app.Phone))
                await _sms.SendNowAsync(null, app.Phone, $"Your FiscalHub confirmation code is {code}. It works for {CodeMinutes} minutes.", "code", null, "Sign-up code ******", ct);
        }

        public async Task<RegisterResultDto> RegisterAsync(RegisterRequest r, string? ip, CancellationToken ct)
        {
            var email = r.Email?.Trim().ToLowerInvariant() ?? "";
            var answer = SelfService ? new RegisterResultDto("Unverified", true, email, string.Format(Sent, email))
                                     : new RegisterResultDto("Pending", false, email, "Thank you. We review every registration and will email you as soon as it is approved.");
            if (!string.IsNullOrWhiteSpace(r.Website)) return answer;   // the hidden field: only bots fill it. Answer as if it worked.

            var company = r.CompanyName?.Trim() ?? "";
            if (company.Length < 2 || company.Length > 150) throw GatewayException.BadRequest("COMPANY_INVALID", "Enter your company name.");
            var first = r.ContactName?.Trim() ?? ""; var last = r.ContactSurname?.Trim() ?? "";
            if (first.Length < 1 || first.Length > 100 || last.Length > 100) throw GatewayException.BadRequest("NAME_INVALID", "Enter the contact person's name.");
            if (email.Length > 200 || !EmailRx.IsMatch(email)) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter a valid email address.");
            if ((r.Phone?.Length ?? 0) > 40 || (r.Tin?.Length ?? 0) > 30 || (r.Message?.Length ?? 0) > 1000) throw GatewayException.BadRequest("FIELD_TOO_LONG", "One of the fields is too long.");
            var userName = UserNames.Clean(r.UserName);
            foreach (var v in _users.PasswordValidators)
            {
                var res = await v.ValidateAsync(_users, new ApplicationUser(), r.Password ?? "");
                if (!res.Succeeded) throw GatewayException.BadRequest("PASSWORD_REJECTED", string.Join(" ", res.Errors.Select(e => e.Description)));
            }
            var (logo, logoType) = ParseLogo(r.LogoData);
            int? planId = null;
            if (r.PlanId.HasValue)
            {
                if (!await _db.Plans.AnyAsync(p => p.Id == r.PlanId && p.IsActive && p.IsPublic, ct)) throw GatewayException.BadRequest("PLAN_INVALID", "Choose one of the listed plans.");
                planId = r.PlanId;
            }

            // an address that already has an account is answered exactly like a new one (nothing is revealed); its owner is told by email instead
            if (await _users.FindByEmailAsync(email) != null)
            {
                var url = await _urls.AppAsync(ct);
                await _email.SendAsync(email, "You already have a FiscalHub account", $"<p>Someone (probably you) tried to register again with this email address. You already have an account: <a href=\"{url}/login\">sign in</a>, or use <i>Forgot password</i> on the sign-in page.</p><p>If it was not you, ignore this email.</p>", ct);
                return answer;
            }
            if (await _db.Applications.AnyAsync(a => a.Email == email && a.Status == "Pending", ct)) return answer;
            // a user name is public anyway (people type it to sign in), so saying it is taken reveals nothing secret
            if (userName != null && (await _users.FindByNameAsync(userName) != null || await _db.Applications.AnyAsync(a => a.UserName == userName && a.Email != email && (a.Status == "Unverified" || a.Status == "Pending"), ct)))
                throw GatewayException.Conflict("USERNAME_TAKEN", $"The user name \"{userName}\" is taken. Choose another one, or leave it empty to sign in with your email.");

            Guid? partnerId = null, referrerId = null;
            if (!string.IsNullOrWhiteSpace(r.PartnerCode))
            {
                var code = r.PartnerCode.Trim().ToUpper();
                partnerId = await _db.Partners.AsNoTracking().Where(p => p.IsActive && p.Code == code).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);   // an unknown code is simply ignored
                if (partnerId == null && await global::Fdms.Infrastructure.Services.ReferralService.EnabledAsync(_db, ct)) referrerId = await _db.Clients.AsNoTracking().Where(c => c.IsActive && c.ReferralCode == code).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);   // or another company's refer-and-earn code
            }

            // registering again before confirming replaces the earlier, unconfirmed attempt
            var app = await _db.Applications.Where(a => a.Email == email && a.Status == "Unverified").OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
            if (app == null) { app = new ClientApplication(); _db.Applications.Add(app); }
            else if (app.VerifySentAt > DateTime.UtcNow.AddSeconds(-30)) throw new GatewayException(429, "CODE_TOO_SOON", "We just sent you a code. Wait a moment before asking for another one.");
            app.PartnerId = partnerId; app.ReferrerClientId = referrerId;
            app.CompanyName = company; app.Tin = string.IsNullOrWhiteSpace(r.Tin) ? null : r.Tin.Trim(); app.ContactName = first; app.ContactSurname = last; app.Email = email;
            app.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim(); app.UserName = userName;
            app.PasswordHash = _hasher.HashPassword(new ApplicationUser(), r.Password!); app.PlanId = planId; app.BillingCycle = BillingMap.Cycle(r.BillingCycle);
            app.Message = string.IsNullOrWhiteSpace(r.Message) ? null : r.Message.Trim(); app.IpAddress = ip; app.Logo = logo; app.LogoContentType = logoType;
            app.Status = SelfService ? "Unverified" : "Pending"; app.CreatedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ActorType = "user", UserName = email, IpAddress = ip, Action = "application.submit", Entity = "Application", EntityId = app.Id.ToString(), Detail = company });
            await _db.SaveChangesAsync(ct);

            if (SelfService) { await SendCodeAsync(app, ct); return answer; }

            await _email.SendAsync(email, "We received your registration", $"<p>Hello {WebUtility.HtmlEncode(first)},</p><p>Thank you for registering <b>{WebUtility.HtmlEncode(company)}</b>. We review every registration and will email you as soon as it is approved.</p>", ct);
            // the operator's bell and alert emails (Settings, Email)
            await _notify.RaisePlatformAsync("application", "info", $"application:{app.Id}", $"New registration: {company}",
                $"{first} ({email}) registered {company}. Review it and approve or reject it.", "/admin/applications", ct);
            return answer;
        }

        public async Task<RegisterResultDto> ResendAsync(ResendCodeRequest req, CancellationToken ct)
        {
            var email = req.Email?.Trim().ToLowerInvariant() ?? "";
            var answer = new RegisterResultDto("Unverified", true, email, string.Format(Sent, email));
            var app = await _db.Applications.Where(a => a.Email == email && a.Status == "Unverified").OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
            if (app == null) return answer;
            if (app.VerifySentAt > DateTime.UtcNow.AddSeconds(-60)) throw new GatewayException(429, "CODE_TOO_SOON", "We sent a code less than a minute ago. Check your inbox (and the spam folder), or try again in a minute.");
            await SendCodeAsync(app, ct);
            return answer;
        }

        public async Task<VerifyRegistrationResultDto> VerifyAsync(VerifyRegistrationRequest req, string? ip, CancellationToken ct)
        {
            var bad = GatewayException.BadRequest("INVALID_CODE", "That code is wrong or has expired. Check the latest email, or ask for a new code.");
            var email = req.Email?.Trim().ToLowerInvariant() ?? "";
            var code = req.Code?.Trim() ?? "";
            if (email.Length == 0 || code.Length == 0) throw GatewayException.BadRequest("CODE_REQUIRED", "Enter the 6-digit code from the email.");
            var app = await _db.Applications.Where(a => a.Email == email && a.Status == "Unverified").OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct) ?? throw bad;
            if (app.VerifyAttempts >= MaxCodeTries) throw new GatewayException(429, "TOO_MANY_TRIES", "Too many wrong codes. Ask for a new code.");
            if (app.VerifyExpiresAt == null || app.VerifyExpiresAt < DateTime.UtcNow || app.VerifyCodeHash == null) throw bad;
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(HashCode(app.Id, code)), System.Text.Encoding.ASCII.GetBytes(app.VerifyCodeHash)))
            {
                app.VerifyAttempts++;
                await _db.SaveChangesAsync(ct);
                throw bad;
            }
            // the code is used up whatever happens next
            app.VerifyCodeHash = null; app.VerifyExpiresAt = null;
            _db.AuditLogs.Add(new AuditLog { ActorType = "user", UserName = email, IpAddress = ip, Action = "application.email-verified", Entity = "Application", EntityId = app.Id.ToString(), Detail = app.CompanyName });

            if (!SelfService)
            {
                app.Status = "Pending";
                await _db.SaveChangesAsync(ct);
                return new VerifyRegistrationResultDto("Pending", "Thank you, your email is confirmed. We review every registration and will email you as soon as it is approved.", null, null, null);
            }

            // open the company now: trial (demo), and the first invoice ready to pay whenever they are
            app.Status = "Pending";
            await _db.SaveChangesAsync(ct);
            var plan = await PlanForAsync(app.PlanId, ct);
            var trialDays = _cfg.GetValue<int?>("Onboarding:TrialDays") ?? (plan.TrialDays > 0 ? plan.TrialDays : 14);
            var (client, sub) = await OpenCompanyAsync(app, plan, app.BillingCycle, trialDays, "Self-service (email confirmed)", null, ct);
            var invoice = await _billing.IssueOpeningInvoiceAsync(client.Id, ct);

            var url = await _urls.AppAsync(ct);
            var signIn = app.UserName ?? app.Email;
            var deleteAfter = _cfg.GetValue("Onboarding:DeleteUnpaidAfterDays", 7);
            await _email.SendAsync(app.Email, "Welcome to FiscalHub", $"<p>Hello {WebUtility.HtmlEncode(app.ContactName)},</p><p><b>{WebUtility.HtmlEncode(app.CompanyName)}</b> is ready on the <b>{WebUtility.HtmlEncode(plan.Name)}</b> plan"
                + (sub.TrialEndsAt != null ? $", with a free demo until <b>{sub.TrialEndsAt:d MMM yyyy}</b>" : "") + ".</p>"
                + $"<p>Sign in as <b>{WebUtility.HtmlEncode(signIn)}</b>: <a href=\"{url}/login\">{url}/login</a></p>"
                + (invoice != null ? $"<p>Your first invoice ({WebUtility.HtmlEncode(invoice)}) is under <b>Billing</b>. Pay it whenever you are ready to go live: the paid period starts when the demo ends. If it is still unpaid {deleteAfter} days after the demo ends, the account and its demo data are deleted.</p>" : ""), ct);
            await _notify.RaisePlatformAsync("application", "info", $"application:{app.Id}", $"New company signed up: {app.CompanyName}",
                $"{app.ContactName} ({app.Email}) confirmed their email and started a demo on {plan.Name}" + (sub.TrialEndsAt != null ? $" until {sub.TrialEndsAt:d MMM yyyy}." : "."), "/admin/subscriptions", ct);
            return new VerifyRegistrationResultDto("Active", "Your email is confirmed and your account is ready. Sign in to start.", signIn, sub.TrialEndsAt, invoice);
        }

        /// <summary>Accepts only a PNG or JPEG that decodes, of at most 300 KB, sent as a data URL. The console shrinks pictures to fit before sending.</summary>
        private static (byte[]? Bytes, string? Type) ParseLogo(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return (null, null);
            var m = Regex.Match(data, @"^data:image/(png|jpeg);base64,([A-Za-z0-9+/=]+)$");
            if (!m.Success || data.Length > 420_000) throw GatewayException.BadRequest("LOGO_INVALID", "The logo must be a PNG or JPEG image of at most 300 KB.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(m.Groups[2].Value); } catch (FormatException) { throw GatewayException.BadRequest("LOGO_INVALID", "The logo could not be read."); }
            var png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            var jpg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
            if (bytes.Length == 0 || bytes.Length > InvoiceTemplateService.MaxLogoBytes || !(png || jpg) || !ReceiptPdfRenderer.CanDecode(bytes))
                throw GatewayException.BadRequest("LOGO_INVALID", "The logo must be a PNG or JPEG image of at most 300 KB.");
            return (bytes, png ? "image/png" : "image/jpeg");
        }

        public async Task<TemplateLogo?> GetLogoAsync(Guid id, CancellationToken ct)
        {
            var a = await _db.Applications.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Logo, x.LogoContentType }).FirstOrDefaultAsync(ct);
            return a?.Logo == null ? null : new TemplateLogo(a.Logo, a.LogoContentType ?? "image/png");
        }

        public async Task<IReadOnlyList<ApplicationDto>> ListAsync(string? status, CancellationToken ct)
        {
            var q = _db.Applications.AsNoTracking().AsQueryable();
            if (status is "Unverified" or "Pending" or "Approved" or "Rejected") q = q.Where(a => a.Status == status);
            // the logo picture itself is not loaded for the list: only whether there is one
            var rows = await q.OrderBy(a => a.Status == "Pending" ? 0 : 1).ThenByDescending(a => a.CreatedAt).Take(300).Select(a => new ClientApplication
            {
                Id = a.Id, CompanyName = a.CompanyName, Tin = a.Tin, ContactName = a.ContactName, ContactSurname = a.ContactSurname, Email = a.Email, Phone = a.Phone, PlanId = a.PlanId, BillingCycle = a.BillingCycle,
                Status = a.Status, Message = a.Message, DecisionReason = a.DecisionReason, DecidedBy = a.DecidedBy, DecidedAt = a.DecidedAt, ClientId = a.ClientId, IpAddress = a.IpAddress, CreatedAt = a.CreatedAt,
            }).ToListAsync(ct);
            var withLogo = (await _db.Applications.AsNoTracking().Where(a => a.Logo != null).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            var plans = await _db.Plans.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            return rows.Select(a => ToDto(a, a.PlanId.HasValue ? plans.GetValueOrDefault(a.PlanId.Value) : null) with { HasLogo = withLogo.Contains(a.Id) }).ToList();
        }

        private async Task<Plan> PlanForAsync(int? planId, CancellationToken ct) =>
            await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, ct)
            ?? await _db.Plans.Where(p => p.IsActive && p.IsPublic).OrderBy(p => p.SortOrder).FirstOrDefaultAsync(ct)
            ?? throw GatewayException.Conflict("NO_PLAN", "There is no active plan to put the company on. Create one under Plans first.");

        /// <summary>Creates the company, its administrator (with the applicant's own password and chosen user name) and the subscription, and marks the application approved.</summary>
        private async Task<(Client Client, SubscriptionDto Sub)> OpenCompanyAsync(ClientApplication app, Plan plan, string? cycle, int? trialDays, string decidedBy, string? note, CancellationToken ct)
        {
            if (await _users.FindByEmailAsync(app.Email) != null) throw GatewayException.Conflict("USER_EXISTS", "A user with that email already exists.");
            if (app.UserName != null && await _users.FindByNameAsync(app.UserName) != null) throw GatewayException.Conflict("USERNAME_TAKEN", $"The user name \"{app.UserName}\" is already taken.");

            var client = new Client { Name = app.CompanyName, Email = app.Email, Slug = await BrandService.UniqueSlugAsync(_db, app.CompanyName, ct) };
            if (app.PartnerId is { } pid && await _db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid && p.IsActive, ct) is { } partner) { client.PartnerId = partner.Id; client.PartnerPercent = partner.Kind == "reseller" ? 0 : partner.CommissionPercent; }
            else if (app.ReferrerClientId is { } rid) client.ReferredByClientId = rid;
            _db.Clients.Add(client);
            await _db.SaveChangesAsync(ct);
            // the logo they sent becomes the logo on the invoices the company prints, until they design their own template
            if (app.Logo != null)
                _db.InvoiceTemplates.Add(new InvoiceTemplate { ClientId = client.Id, Name = "Company default", SettingsJson = System.Text.Json.JsonSerializer.Serialize(new InvoiceTemplateSettings(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), Logo = app.Logo, LogoContentType = app.LogoContentType });
            var admin = new ApplicationUser { UserName = app.UserName ?? app.Email, Email = app.Email, EmailConfirmed = true, Name = app.ContactName, Surname = app.ContactSurname, PhoneNumber = app.Phone, ClientId = client.Id, MustChangePassword = false };
            var created = await _users.CreateAsync(admin);   // no password here: the applicant's own (already hashed) password is attached below
            if (!created.Succeeded)
            {
                _db.Clients.Remove(client); await _db.SaveChangesAsync(ct);
                throw GatewayException.Conflict("USER_REJECTED", string.Join(" ", created.Errors.Select(e => e.Description)));
            }
            admin.PasswordHash = app.PasswordHash;
            await _users.UpdateAsync(admin);
            await _users.AddToRoleAsync(admin, RoleNames.ClientAdmin);

            var sub = await _billing.StartSubscriptionAsync(client.Id, plan.Id, cycle, trialDays, ct);
            app.Status = "Approved"; app.DecidedAt = DateTime.UtcNow; app.DecidedBy = decidedBy; app.ClientId = client.Id; app.DecisionReason = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            _db.AuditLogs.Add(new AuditLog { ClientId = client.Id, Action = "application.approve", Entity = "Application", EntityId = app.Id.ToString(), Detail = $"{app.CompanyName} on {plan.Name} ({sub.Status}) by {decidedBy}" });
            await _db.SaveChangesAsync(ct);
            return (client, sub);
        }

        public async Task<ApplicationDto> ApproveAsync(Guid id, ApproveApplicationRequest req, CancellationToken ct)
        {
            var app = await _db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw GatewayException.NotFound("Application");
            if (app.Status is not ("Pending" or "Unverified")) throw GatewayException.Conflict("ALREADY_DECIDED", $"This application was already {app.Status.ToLowerInvariant()}.");
            var plan = await PlanForAsync(req.PlanId ?? app.PlanId, ct);
            var (_, sub) = await OpenCompanyAsync(app, plan, req.BillingCycle ?? app.BillingCycle, req.TrialDays, _scope.UserName ?? "Operator", req.Note, ct);

            var url = await _urls.AppAsync(ct);
            await _email.SendAsync(app.Email, "Your registration is approved", $"<p>Hello {WebUtility.HtmlEncode(app.ContactName)},</p><p><b>{WebUtility.HtmlEncode(app.CompanyName)}</b> is approved on the <b>{WebUtility.HtmlEncode(plan.Name)}</b> plan"
                + (sub.Status == "Trial" ? $" with a free trial until {sub.TrialEndsAt:d MMM yyyy}" : "") + $".</p><p>Sign in as <b>{WebUtility.HtmlEncode(app.UserName ?? app.Email)}</b> with the password you registered with: <a href=\"{url}/login\">{url}/login</a></p>", ct);
            return ToDto(app, plan.Name);
        }

        public async Task<ApplicationDto> RejectAsync(Guid id, string reason, CancellationToken ct)
        {
            var app = await _db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw GatewayException.NotFound("Application");
            if (app.Status != "Pending") throw GatewayException.Conflict("ALREADY_DECIDED", $"This application was already {app.Status.ToLowerInvariant()}.");
            var why = reason?.Trim() ?? "";
            if (why.Length < 3 || why.Length > 500) throw GatewayException.BadRequest("REASON_REQUIRED", "Give a reason (3-500 characters); the applicant is told.");
            app.Status = "Rejected"; app.DecisionReason = why; app.DecidedAt = DateTime.UtcNow; app.DecidedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { Action = "application.reject", Entity = "Application", EntityId = app.Id.ToString(), Detail = $"{app.CompanyName}: {why}" });
            await _db.SaveChangesAsync(ct);
            await _email.SendAsync(app.Email, "About your registration", $"<p>Hello {WebUtility.HtmlEncode(app.ContactName)},</p><p>We could not approve the registration of <b>{WebUtility.HtmlEncode(app.CompanyName)}</b>: {WebUtility.HtmlEncode(why)}</p>", ct);
            return ToDto(app, null);
        }

        public async Task<ApplicationDto?> FindForSignInAsync(string identifier, string password, CancellationToken ct)
        {
            var id = identifier?.Trim() ?? "";
            if (id.Length == 0) return null;
            var email = id.ToLowerInvariant();
            var app = await _db.Applications.AsNoTracking().Where(a => (a.Email == email || a.UserName == id) && a.Status != "Approved").OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
            if (app == null) return null;
            var ok = _hasher.VerifyHashedPassword(new ApplicationUser(), app.PasswordHash, password ?? "");
            return ok == PasswordVerificationResult.Failed ? null : ToDto(app, null);   // only the person who chose the password is told where it stands
        }

        private static ApplicationDto ToDto(ClientApplication a, string? plan) => new(a.Id, a.CompanyName, a.Tin, a.ContactName, a.ContactSurname, a.Email, a.Phone, a.PlanId, plan, a.BillingCycle, a.Status, a.Message,
            a.DecisionReason, a.DecidedBy, a.DecidedAt, a.ClientId, a.CreatedAt, a.IpAddress, a.Logo != null);
    }

    /// <summary>Paying invoices: Paynow (cards, ZimSwitch, EcoCash, OneMoney...), bank transfer confirmed by the operator, and payments the operator records by hand.</summary>
    public sealed class PaymentService : IPaymentService
    {
        private readonly FdmsDbContext _db;
        private readonly IPaynowGateway _paynow;
        private readonly IEcoCashGateway _ecocash;
        private readonly IStripeGateway _stripe;
        private readonly IPaymentGatewaySettings _gateways;
        private readonly IActivationService _activation;
        private readonly IBillingService _billing;
        private readonly IBillingProfileService _profiles;
        private readonly IEmailSender _email;
        private readonly PublicUrls _urls;
        private readonly IConfiguration _cfg;
        private readonly ICallerScope _scope;
        private readonly INotificationService _notify;

        public PaymentService(FdmsDbContext db, IPaynowGateway paynow, IEcoCashGateway ecocash, IStripeGateway stripe, IPaymentGatewaySettings gateways, IActivationService activation, IBillingService billing, IBillingProfileService profiles, IEmailSender email, PublicUrls urls, IConfiguration cfg, ICallerScope scope, INotificationService notify)
        { _notify = notify; _activation = activation; _ecocash = ecocash; _stripe = stripe; _gateways = gateways; _db = db; _paynow = paynow; _billing = billing; _profiles = profiles; _email = email; _urls = urls; _cfg = cfg; _scope = scope; }

        private async Task<BillingInvoice> OpenInvoiceAsync(Guid clientId, Guid invoiceId, CancellationToken ct)
        {
            var inv = await _db.BillingInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Invoice");
            if (inv.Status != "Open") throw GatewayException.Conflict("INVOICE_NOT_OPEN", inv.Status == "Paid" ? "That invoice is already paid." : "That invoice was cancelled.");
            return inv;
        }

        public async Task<PaymentDto> PayWithPaynowAsync(Guid clientId, Guid invoiceId, PayPaynowRequest req, CancellationToken ct)
        {
            if (!await _paynow.IsConfiguredAsync(ct)) throw GatewayException.Conflict("PAYNOW_NOT_CONFIGURED", "Online payment is not available yet. Pay by bank transfer, or contact support.");
            var method = (req.Method ?? "web").Trim().ToLowerInvariant();
            if (method is not ("web" or "ecocash" or "onemoney")) throw GatewayException.BadRequest("METHOD_INVALID", "Choose web, ecocash or onemoney.");
            var inv = await OpenInvoiceAsync(clientId, invoiceId, ct);
            string? phone = null;
            if (method != "web")
            {
                phone = new string((req.Phone ?? "").Where(char.IsDigit).ToArray());
                if (phone.Length is < 9 or > 13) throw GatewayException.BadRequest("PHONE_INVALID", "Enter the mobile number that will approve the payment, e.g. 0771234567.");
            }
            var client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == clientId, ct);

            foreach (var old in await _db.Payments.Where(p => p.InvoiceId == inv.Id && p.Method == "Paynow" && p.Status == "Pending").ToListAsync(ct)) old.Status = "Cancelled";   // only the latest attempt counts
            var pay = new Payment { InvoiceId = inv.Id, ClientId = clientId, Method = "Paynow", Status = "Pending", Amount = inv.Total, Currency = inv.Currency, PayerPhone = phone };
            pay.Reference = $"{inv.Number}-{pay.Id.ToString("N")[..6]}";
            var api = await _urls.ApiAsync(ct);
            var app = await _urls.AppAsync(ct);
            var init = await _paynow.InitiateAsync(pay.Reference, inv.Total, client.Email ?? _scope.UserName ?? "billing@localhost", $"Subscription invoice {inv.Number}", $"{app}/billing?payment={pay.Id}",
                $"{api}/api/v1/payments/paynow/result", method == "web" ? null : method, phone, ct);
            if (!init.Ok)
            {
                pay.Status = "Failed"; pay.Notes = init.Error;
                _db.Payments.Add(pay); await _db.SaveChangesAsync(ct);
                throw new GatewayException(422, "PAYNOW_ERROR", init.Error ?? "Paynow could not start the payment.");
            }
            pay.RedirectUrl = init.RedirectUrl; pay.PollUrl = init.PollUrl; pay.ProviderReference = init.PaynowReference; pay.Instructions = init.Instructions;
            _db.Payments.Add(pay);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "payment.start", Entity = "Payment", EntityId = pay.Id.ToString(), Detail = $"{inv.Number} via Paynow ({method})" });
            await _db.SaveChangesAsync(ct);
            return BillingMap.ToDto(pay, inv.Number, client.Name);
        }

        public async Task<PaymentDto> PayWithEcoCashAsync(Guid clientId, Guid invoiceId, PayEcoCashRequest req, CancellationToken ct)
        {
            if (!await _ecocash.IsConfiguredAsync(ct)) throw GatewayException.Conflict("ECOCASH_NOT_CONFIGURED", "EcoCash payment is not available yet.");
            var phone = new string((req.Phone ?? "").Where(char.IsDigit).ToArray());
            if (phone.Length is < 9 or > 13) throw GatewayException.BadRequest("PHONE_INVALID", "Enter the EcoCash number that will approve the payment, e.g. 0771234567.");
            var inv = await OpenInvoiceAsync(clientId, invoiceId, ct);
            var client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == clientId, ct);
            foreach (var old in await _db.Payments.Where(p => p.InvoiceId == inv.Id && p.Method == "EcoCash" && p.Status == "Pending").ToListAsync(ct)) old.Status = "Cancelled";
            var pay = new Payment { InvoiceId = inv.Id, ClientId = clientId, Method = "EcoCash", Status = "Pending", Amount = inv.Total, Currency = inv.Currency, PayerPhone = phone };
            pay.Reference = $"{inv.Number}-{pay.Id.ToString("N")[..6]}";
            var r = await _ecocash.ChargeAsync(pay.Reference, inv.Total, inv.Currency, phone, $"Subscription invoice {inv.Number}", (await _urls.ApiAsync(ct)) + "/api/v1/payments/ecocash/notify", ct);
            if (!r.Ok) { pay.Status = "Failed"; pay.Notes = r.Message; _db.Payments.Add(pay); await _db.SaveChangesAsync(ct); throw new GatewayException(422, "ECOCASH_ERROR", r.Message ?? "EcoCash could not start the payment."); }
            pay.ProviderReference = r.ProviderReference; pay.Instructions = r.Message; pay.ProviderStatus = r.Status;
            _db.Payments.Add(pay);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "payment.start", Entity = "Payment", EntityId = pay.Id.ToString(), Detail = $"{inv.Number} via EcoCash" });
            await _db.SaveChangesAsync(ct);
            if (r.Status == "Paid") await SettleAsync(pay, 0m, r.ProviderReference, ct);
            return BillingMap.ToDto(pay, inv.Number, client.Name);
        }

        public async Task<PaymentDto> PayWithStripeAsync(Guid clientId, Guid invoiceId, CancellationToken ct)
        {
            if (!await _stripe.IsConfiguredAsync(ct)) throw GatewayException.Conflict("STRIPE_NOT_CONFIGURED", "Card payment is not available yet.");
            var inv = await OpenInvoiceAsync(clientId, invoiceId, ct);
            var client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == clientId, ct);
            foreach (var old in await _db.Payments.Where(p => p.InvoiceId == inv.Id && p.Method == "Stripe" && p.Status == "Pending").ToListAsync(ct)) old.Status = "Cancelled";
            var pay = new Payment { InvoiceId = inv.Id, ClientId = clientId, Method = "Stripe", Status = "Pending", Amount = inv.Total, Currency = inv.Currency };
            pay.Reference = $"{inv.Number}-{pay.Id.ToString("N")[..6]}";
            var app = await _urls.AppAsync(ct);
            var r = await _stripe.CreateCheckoutAsync(pay.Reference, inv.Total, inv.Currency, client.Email ?? _scope.UserName ?? "billing@localhost", $"Subscription invoice {inv.Number}", $"{app}/billing?payment={pay.Id}", $"{app}/billing", ct);
            if (!r.Ok) { pay.Status = "Failed"; pay.Notes = r.Message; _db.Payments.Add(pay); await _db.SaveChangesAsync(ct); throw new GatewayException(422, "STRIPE_ERROR", r.Message ?? "Stripe could not start the payment."); }
            pay.ProviderReference = r.ProviderReference; pay.RedirectUrl = r.RedirectUrl;
            _db.Payments.Add(pay);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "payment.start", Entity = "Payment", EntityId = pay.Id.ToString(), Detail = $"{inv.Number} via Stripe" });
            await _db.SaveChangesAsync(ct);
            return BillingMap.ToDto(pay, inv.Number, client.Name);
        }

        /// <summary>Marks a gateway payment paid and applies the invoice. A reported amount that differs from the invoice is never applied (0 = the gateway did not say).</summary>
        private async Task SettleAsync(Payment pay, decimal paidAmount, string? providerRef, CancellationToken ct)
        {
            if (pay.Status == "Paid") return;
            var inv = await _db.BillingInvoices.FirstAsync(i => i.Id == pay.InvoiceId, ct);
            if (paidAmount > 0 && Math.Abs(paidAmount - inv.Total) > 0.01m)
            {
                pay.Status = "Failed"; pay.Notes = $"{Label(pay.Method)} reported {paidAmount:0.00} but the invoice is {inv.Total:0.00}: not applied. Check it with the gateway.";
                _db.AuditLogs.Add(new AuditLog { ClientId = pay.ClientId, Action = "payment.mismatch", Entity = "Payment", EntityId = pay.Id.ToString(), Detail = pay.Notes });
                await _db.SaveChangesAsync(ct);
                return;
            }
            pay.Status = "Paid"; pay.ConfirmedAt = DateTime.UtcNow; pay.ProviderReference = providerRef ?? pay.ProviderReference;
            await _db.SaveChangesAsync(ct);
            await _billing.ApplyInvoicePaidAsync(inv.Id, Label(pay.Method), pay.ProviderReference ?? pay.Reference, ct);
        }

        public async Task<bool> HandleStripeWebhookAsync(string payload, string signature, CancellationToken ct)
        {
            var s = await _stripe.ParseWebhookAsync(payload, signature, ct);
            if (s == null) return false;
            var pay = s.Reference == null ? null : await _db.Payments.FirstOrDefaultAsync(p => p.Reference == s.Reference && p.Method == "Stripe", ct);
            if (pay != null && s.Paid) await SettleAsync(pay, s.Amount, s.Id, ct);
            return true;
        }

        public async Task<bool> HandleEcoCashNotifyAsync(string? reference, CancellationToken ct)
        {
            var pay = string.IsNullOrWhiteSpace(reference) ? null : await _db.Payments.FirstOrDefaultAsync(p => p.Reference == reference && p.Method == "EcoCash", ct);
            if (pay == null) return false;
            await RefreshAsync(null, pay.Id, ct);   // never trust the body of a notification: ask EcoCash itself
            return true;
        }

        /// <summary>Methods the operator confirms by hand: they pay outside the system and tell us.</summary>
        public static readonly string[] ManualMethods = { "BankTransfer", "EcoCashManual", "PayPal" };

        public static string Label(string method) => method switch
        {
            "BankTransfer" => "Bank transfer", "EcoCashManual" => "EcoCash (Send Money)", "EcoCash" => "EcoCash", "Stripe" => "Card (Stripe)", "PayPal" => "PayPal", "Paynow" => "Paynow", _ => method,
        };

        public async Task<PaymentDto> PayManualAsync(Guid clientId, Guid invoiceId, string provider, string? reference, string? notes, byte[]? proof, string? proofName, string? proofContentType, CancellationToken ct)
        {
            var method = provider switch { "bank" => "BankTransfer", "ecocash-manual" => "EcoCashManual", "paypal" => "PayPal", _ => throw GatewayException.BadRequest("METHOD_INVALID", "Choose bank, ecocash-manual or paypal.") };
            var inv = await OpenInvoiceAsync(clientId, invoiceId, ct);
            if (method == "BankTransfer")
            {
                var profile = await _profiles.LoadAsync(ct);
                if (string.IsNullOrEmpty(profile.AccountNumber)) throw GatewayException.Conflict("BANK_NOT_CONFIGURED", "Bank transfer is not available yet.");
            }
            else if (await _gateways.CredentialsAsync(method == "PayPal" ? PaymentGatewaySettings.PayPalCode : PaymentGatewaySettings.EcoCashManualCode, ct) == null)
                throw GatewayException.Conflict("METHOD_NOT_AVAILABLE", Label(method) + " is not available yet.");
            var refText = string.Join(' ', (reference ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (refText.Length == 0) throw GatewayException.BadRequest("REFERENCE_REQUIRED", "Enter the reference of your payment (from the bank slip or the EcoCash message).");
            if (refText.Length > 150 || (notes?.Length ?? 0) > 1000) throw GatewayException.BadRequest("FIELD_TOO_LONG", "The reference or note is too long.");
            // one payment per reference: a reference that is waiting for confirmation or already paid cannot be used again (by anyone)
            var key = NormaliseReference(refText);
            if (await _db.Payments.AnyAsync(p => p.Reference != null && ManualMethods.Contains(p.Method) && (p.Status == "AwaitingConfirmation" || p.Status == "Paid" || p.Status == "CodeSent")
                    && p.Reference.ToUpper().Replace(" ", "").Replace("-", "") == key, ct))
                throw GatewayException.Conflict("REFERENCE_USED", $"The reference {refText} was already used for a payment. Check the reference on your proof of payment, or contact support if you think this is a mistake.");
            var type = ProofType(proof, proofName);
            var client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == clientId, ct);
            var pay = new Payment { InvoiceId = inv.Id, ClientId = clientId, Method = method, Status = "AwaitingConfirmation", Amount = inv.Total, Currency = inv.Currency, Reference = refText, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                Proof = proof is { Length: > 0 } ? proof : null, ProofContentType = type, ProofName = proof is { Length: > 0 } ? Path.GetFileName(proofName ?? "proof")[..Math.Min(200, Path.GetFileName(proofName ?? "proof").Length)] : null };
            _db.Payments.Add(pay);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "payment.manual-submitted", Entity = "Payment", EntityId = pay.Id.ToString(), Detail = $"{inv.Number}: {inv.Total:0.00} {inv.Currency} via {Label(method)}, ref {refText}" });
            await _db.SaveChangesAsync(ct);
            await _notify.RaisePlatformAsync("payment", "warn", $"payment-confirm:{pay.Id}", $"{Label(method)} payment to confirm: {inv.Number}",
                $"{client.Name} says it paid {inv.Total:0.00} {inv.Currency} by {Label(method)} (reference {refText}). Check the proof, then confirm or reject it.", "/admin/payments", ct);
            await _notify.RaiseAsync(clientId, "billing", "info", $"payment-submitted:{pay.Id}", $"Payment for {inv.Number} received and being checked",
                $"We received your {Label(method).ToLowerInvariant()} payment of {inv.Total:0.00} {inv.Currency} (reference {refText}). You will be told as soon as it is confirmed.", "/billing", ct);
            return BillingMap.ToDto(pay, inv.Number, client.Name);
        }

        /// <summary>The form a reference is compared in: upper case, no spaces or dashes ("mp 251009.1234-ab" and "MP251009.1234AB" are the same).</summary>
        public static string NormaliseReference(string reference) => reference.ToUpperInvariant().Replace(" ", "").Replace("-", "");

        /// <summary>The proof of payment must really be a PDF or a picture (PNG, JPEG, WebP), up to 5 MB: the first bytes are checked, not just the name.</summary>
        public static string? ProofType(byte[]? proof, string? name)
        {
            if (proof is not { Length: > 0 }) return null;
            if (proof.Length > 5_000_000) throw GatewayException.BadRequest("PROOF_SIZE", "The proof of payment may be at most 5 MB.");
            var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            if (ext.Length > 0 && ext is not (".pdf" or ".png" or ".jpg" or ".jpeg" or ".webp")) throw GatewayException.BadRequest("PROOF_TYPE", "Only a PDF or a picture (PNG, JPEG, WebP) can be uploaded as proof of payment.");
            var type = proof.Length > 4 && proof[0] == 0x25 && proof[1] == 0x50 && proof[2] == 0x44 && proof[3] == 0x46 ? "application/pdf"
                : proof.Length > 8 && proof[0] == 0x89 && proof[1] == 0x50 && proof[2] == 0x4E && proof[3] == 0x47 ? "image/png"
                : proof.Length > 3 && proof[0] == 0xFF && proof[1] == 0xD8 && proof[2] == 0xFF ? "image/jpeg"
                : proof.Length > 12 && proof[0] == 0x52 && proof[1] == 0x49 && proof[2] == 0x46 && proof[3] == 0x46 && proof[8] == 0x57 && proof[9] == 0x45 && proof[10] == 0x42 && proof[11] == 0x50 ? "image/webp" : null;
            return type ?? throw GatewayException.BadRequest("PROOF_TYPE", "Only a PDF or a picture (PNG, JPEG, WebP) can be uploaded as proof of payment.");
        }

        // ---- reading and refreshing ------------------------------------------------------------------------------------------

        private async Task<PaymentDto> DtoAsync(Payment p, CancellationToken ct) =>
            BillingMap.ToDto(p, await _db.BillingInvoices.AsNoTracking().Where(i => i.Id == p.InvoiceId).Select(i => i.Number).FirstOrDefaultAsync(ct) ?? "", await _db.Clients.AsNoTracking().Where(c => c.Id == p.ClientId).Select(c => c.Name).FirstOrDefaultAsync(ct));

        public async Task<PaymentDto> RefreshAsync(Guid? clientId, Guid paymentId, CancellationToken ct)
        {
            var pay = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId && (clientId == null || p.ClientId == clientId), ct) ?? throw GatewayException.NotFound("Payment");
            if (pay.Method == "Paynow" && pay.Status == "Pending" && !string.IsNullOrEmpty(pay.PollUrl))
            {
                var st = await _paynow.PollAsync(pay.PollUrl, ct);
                if (st != null) await ApplyPaynowAsync(pay, st, ct);
            }
            else if (pay.Method == "EcoCash" && pay.Status == "Pending" && !string.IsNullOrEmpty(pay.PayerPhone))
            {
                var st = await _ecocash.StatusAsync(pay.PayerPhone, pay.Reference ?? "", ct);
                if (st != null)
                {
                    pay.ProviderStatus = st.Status;
                    if (st.Status == "Paid") await SettleAsync(pay, st.Amount, st.EcoCashReference, ct);
                    else if (st.Status is "Failed" or "Cancelled") { pay.Status = st.Status; await _db.SaveChangesAsync(ct); }
                }
            }
            else if (pay.Method == "Stripe" && pay.Status == "Pending" && !string.IsNullOrEmpty(pay.ProviderReference))
            {
                var s = await _stripe.GetSessionAsync(pay.ProviderReference, ct);
                if (s is { Paid: true }) await SettleAsync(pay, s.Amount, s.Id, ct);
            }
            return await DtoAsync(pay, ct);
        }

        public async Task<int> RefreshPendingAsync(CancellationToken ct)
        {
            var since = DateTime.UtcNow.AddDays(-2);
            var pending = await _db.Payments.AsNoTracking().Where(p => p.Status == "Pending" && p.CreatedAt >= since && (p.Method == "Paynow" || p.Method == "EcoCash" || p.Method == "Stripe")).Select(p => p.Id).Take(50).ToListAsync(ct);
            var paid = 0;
            foreach (var id in pending)
            {
                try { if ((await RefreshAsync(null, id, ct)).Status == "Paid") paid++; }
                catch (Exception) when (!ct.IsCancellationRequested) { /* the gateway did not answer: try again next round */ }
            }
            return paid;
        }

        public async Task<bool> HandlePaynowResultAsync(IReadOnlyList<KeyValuePair<string, string>> fields, CancellationToken ct)
        {
            var st = await _paynow.ParseResultAsync(fields, ct);
            if (st == null) return false;   // not from Paynow
            var pay = st.Reference == null ? null : await _db.Payments.FirstOrDefaultAsync(p => p.Reference == st.Reference, ct);
            if (pay != null) await ApplyPaynowAsync(pay, st, ct);
            return true;
        }

        private async Task ApplyPaynowAsync(Payment pay, PaynowStatus st, CancellationToken ct)
        {
            pay.ProviderStatus = st.Status;
            if (pay.Status == "Paid") { await _db.SaveChangesAsync(ct); return; }
            if (st.IsPaid)
            {
                var inv = await _db.BillingInvoices.FirstAsync(i => i.Id == pay.InvoiceId, ct);
                if (Math.Abs(st.Amount - inv.Total) > 0.01m)
                {
                    pay.Status = "Failed"; pay.Notes = $"Paynow reported {st.Amount:0.00} but the invoice is {inv.Total:0.00}: not applied. Check it with Paynow.";
                    _db.AuditLogs.Add(new AuditLog { ClientId = pay.ClientId, Action = "payment.mismatch", Entity = "Payment", EntityId = pay.Id.ToString(), Detail = pay.Notes });
                    await _db.SaveChangesAsync(ct);
                    return;
                }
                pay.Status = "Paid"; pay.ConfirmedAt = DateTime.UtcNow; pay.ProviderReference = st.PaynowReference ?? pay.ProviderReference;
                await _db.SaveChangesAsync(ct);
                await _billing.ApplyInvoicePaidAsync(inv.Id, "Paynow", pay.ProviderReference ?? pay.Reference, ct);
                return;
            }
            if (st.IsFailed) pay.Status = st.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ? "Cancelled" : "Failed";
            await _db.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyList<PaymentDto>> ListAsync(string? status, CancellationToken ct)
        {
            var q = _db.Payments.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(p => p.Status == status);
            var rows = await q.OrderBy(p => p.Status == "AwaitingConfirmation" ? 0 : 1).ThenByDescending(p => p.CreatedAt).Take(300).ToListAsync(ct);
            var inv = await _db.BillingInvoices.AsNoTracking().ToDictionaryAsync(i => i.Id, i => i.Number, ct);
            var names = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            return rows.Select(p => BillingMap.ToDto(p, inv.GetValueOrDefault(p.InvoiceId) ?? "", names.GetValueOrDefault(p.ClientId))).ToList();
        }

        // ---- the operator -----------------------------------------------------------------------------------------------------

        public async Task<ConfirmResultDto> ConfirmAsync(Guid paymentId, string? reference, bool sendCode, CancellationToken ct)
        {
            var pay = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct) ?? throw GatewayException.NotFound("Payment");
            if (!ManualMethods.Contains(pay.Method) || pay.Status is not ("AwaitingConfirmation" or "CodeSent")) throw GatewayException.Conflict("NOT_AWAITING", "Only a payment that is waiting for confirmation can be confirmed.");
            pay.ConfirmedBy = _scope.UserName;
            if (!string.IsNullOrWhiteSpace(reference)) pay.Reference = reference.Trim();
            if (sendCode)
            {
                // confirmed, but the account is switched on only when the company types the code in
                pay.Status = "CodeSent"; pay.ConfirmedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                var code = await _activation.IssueAsync(pay.InvoiceId, pay.Id, ct);
                return new ConfirmResultDto(await DtoAsync(pay, ct), code);
            }
            pay.Status = "Paid"; pay.ConfirmedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _billing.ApplyInvoicePaidAsync(pay.InvoiceId, Label(pay.Method), pay.Reference, ct);
            return new ConfirmResultDto(await DtoAsync(pay, ct), null);
        }

        public async Task<PaymentDto> RejectAsync(Guid paymentId, string reason, CancellationToken ct)
        {
            var pay = await _db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct) ?? throw GatewayException.NotFound("Payment");
            if (pay.Status is not ("AwaitingConfirmation" or "CodeSent")) throw GatewayException.Conflict("NOT_AWAITING", "Only a payment that is waiting for confirmation can be rejected.");
            var why = reason?.Trim() ?? "";
            if (why.Length < 3 || why.Length > 500) throw GatewayException.BadRequest("REASON_REQUIRED", "Give a reason (3-500 characters); the company is told.");
            await _activation.RevokeForPaymentAsync(pay.Id, ct);   // a code issued for this payment stops working
            pay.Status = "Rejected"; pay.Notes = (pay.Notes == null ? "" : pay.Notes + "\n") + "Rejected: " + why; pay.ConfirmedBy = _scope.UserName; pay.ConfirmedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = pay.ClientId, Action = "payment.reject", Entity = "Payment", EntityId = pay.Id.ToString(), Detail = why });
            await _db.SaveChangesAsync(ct);
            // the bell, and an email to the company's alert addresses (or its administrators)
            await _notify.RaiseAsync(pay.ClientId, "billing", "bad", $"payment-rejected:{pay.Id}", "Your payment could not be confirmed",
                $"We could not confirm your {Label(pay.Method).ToLowerInvariant()} payment of {pay.Amount:0.00} {pay.Currency}: {why} Check the details and submit it again under Billing.", "/billing", ct);
            return await DtoAsync(pay, ct);
        }

        public async Task<MarkPaidResultDto> MarkInvoicePaidAsync(Guid invoiceId, MarkPaidRequest req, CancellationToken ct)
        {
            var method = req.Method switch { "Cash" => "Cash", "BankTransfer" => "BankTransfer", "Other" => "Other", _ => throw GatewayException.BadRequest("METHOD_INVALID", "Method must be Cash, BankTransfer or Other.") };
            var inv = await _db.BillingInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw GatewayException.NotFound("Invoice");
            if (inv.Status != "Open") throw GatewayException.Conflict("INVOICE_NOT_OPEN", "Only an unpaid invoice can be marked paid.");
            if ((req.Reference?.Length ?? 0) > 150 || (req.Notes?.Length ?? 0) > 1000) throw GatewayException.BadRequest("FIELD_TOO_LONG", "The reference or note is too long.");
            var pay = new Payment { InvoiceId = inv.Id, ClientId = inv.ClientId, Method = method, Status = req.SendCode ? "CodeSent" : "Paid", Amount = inv.Total, Currency = inv.Currency, Reference = req.Reference?.Trim(), Notes = req.Notes?.Trim(), ConfirmedBy = _scope.UserName, ConfirmedAt = DateTime.UtcNow };
            _db.Payments.Add(pay);
            _db.AuditLogs.Add(new AuditLog { ClientId = inv.ClientId, Action = "payment.recorded", Entity = "Payment", EntityId = pay.Id.ToString(), Detail = $"{inv.Number}: {inv.Total:0.00} {inv.Currency} via {Label(method)} recorded by {_scope.UserName}" + (req.SendCode ? ", activation code sent" : "") });
            await _db.SaveChangesAsync(ct);
            if (req.SendCode)
            {
                var code = await _activation.IssueAsync(inv.Id, pay.Id, ct);   // the invoice stays open until the company types the code in
                return new MarkPaidResultDto(await _billing.GetInvoiceAsync(null, inv.Id, ct), code);
            }
            await _billing.ApplyInvoicePaidAsync(inv.Id, Label(method), req.Reference?.Trim(), ct);
            return new MarkPaidResultDto(await _billing.GetInvoiceAsync(null, inv.Id, ct), null);
        }

        public async Task<(byte[] Bytes, string ContentType, string Name)?> GetProofAsync(Guid? clientId, Guid paymentId, CancellationToken ct)
        {
            var p = await _db.Payments.AsNoTracking().Where(x => x.Id == paymentId && (clientId == null || x.ClientId == clientId)).Select(x => new { x.Proof, x.ProofContentType, x.ProofName }).FirstOrDefaultAsync(ct);
            return p?.Proof == null ? null : (p.Proof, p.ProofContentType ?? "application/octet-stream", p.ProofName ?? "proof");
        }
    }
}
