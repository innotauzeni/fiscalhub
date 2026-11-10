using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Fdms.Infrastructure.Billing
{
    /// <summary>
    /// Activation codes: after a cash or bank payment the operator can give the company a one-time code; typing it in settles the invoice and switches the
    /// account on. The code is 60 random bits, stored only as a salted hash, bound to one company and one invoice, single-use, and expires. Wrong guesses are
    /// counted in the audit trail and lock the company out of trying for 15 minutes after five misses, so it cannot be guessed.
    /// </summary>
    public sealed class ActivationService : IActivationService
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";   // Crockford base32: no I, L, O, U
        private const int MaxMisses = 5, LockMinutes = 15;
        private readonly FdmsDbContext _db;
        private readonly IBillingService _billing;
        private readonly IEmailSender _email;
        private readonly IConfiguration _cfg;
        private readonly ICallerScope _scope;

        public ActivationService(FdmsDbContext db, IBillingService billing, IEmailSender email, IConfiguration cfg, ICallerScope scope) { _db = db; _billing = billing; _email = email; _cfg = cfg; _scope = scope; }

        public static string NewCode()
        {
            Span<byte> b = stackalloc byte[12];
            RandomNumberGenerator.Fill(b);
            var chars = new char[12];
            for (var i = 0; i < 12; i++) chars[i] = Alphabet[b[i] & 31];   // 256 is a multiple of 32, so every symbol is equally likely
            return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
        }

        /// <summary>Upper case, dashes and spaces ignored, and the look-alike letters read the way they were meant (O as 0, I and L as 1). Null when it cannot be a code.</summary>
        public static string? Normalise(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 40) return null;
            var sb = new StringBuilder();
            foreach (var ch in raw.ToUpperInvariant())
            {
                if (ch is '-' or ' ' or '_') continue;
                var c = ch switch { 'O' => '0', 'I' => '1', 'L' => '1', _ => ch };
                if (!Alphabet.Contains(c)) return null;
                sb.Append(c);
            }
            return sb.Length == 12 ? sb.ToString() : null;
        }

        private static string Hash(string salt, string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + code)));

        public async Task<IssuedCodeDto> IssueAsync(Guid invoiceId, Guid? paymentId, CancellationToken ct)
        {
            var inv = await _db.BillingInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw GatewayException.NotFound("Invoice");
            if (inv.Status != "Open") throw GatewayException.Conflict("INVOICE_NOT_OPEN", "A code can only be issued for an unpaid invoice.");
            var now = DateTime.UtcNow;
            foreach (var old in await _db.ActivationCodes.Where(c => c.InvoiceId == invoiceId && c.UsedAt == null && c.RevokedAt == null).ToListAsync(ct)) old.RevokedAt = now;   // one live code per invoice

            var code = NewCode();
            var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var days = Math.Clamp(_cfg.GetValue("Billing:CodeValidDays", 14), 1, 90);
            var row = new ActivationCode { ClientId = inv.ClientId, InvoiceId = inv.Id, PaymentId = paymentId, Salt = salt, CodeHash = Hash(salt, Normalise(code)!), ExpiresAt = now.AddDays(days), IssuedBy = _scope.UserName };
            _db.ActivationCodes.Add(row);
            _db.AuditLogs.Add(new AuditLog { ClientId = inv.ClientId, Action = "code.issued", Entity = "BillingInvoice", EntityId = inv.Number, Detail = $"activation code issued, valid until {row.ExpiresAt:yyyy-MM-dd}" });   // never the code
            await _db.SaveChangesAsync(ct);

            var to = await _db.Clients.AsNoTracking().Where(c => c.Id == inv.ClientId).Select(c => c.Email).FirstOrDefaultAsync(ct);
            var mailOn = !string.IsNullOrWhiteSpace(to) && !string.IsNullOrWhiteSpace(_cfg["Email:Host"]);
            if (!string.IsNullOrWhiteSpace(to))
                await _email.SendAsync(to, $"Your activation code for invoice {inv.Number}",
                    $"<p>We received your payment for invoice <b>{inv.Number}</b> ({inv.Total:0.00} {inv.Currency}).</p><p>Sign in, open <b>Billing</b> and enter this activation code:</p><p style=\"font-size:22px;letter-spacing:2px\"><b>{code}</b></p><p>The code can be used once and is valid until {row.ExpiresAt:d MMM yyyy}.</p>", ct);
            return new IssuedCodeDto(code, row.ExpiresAt, mailOn);
        }

        public async Task RevokeForPaymentAsync(Guid paymentId, CancellationToken ct)
        {
            foreach (var c in await _db.ActivationCodes.Where(c => c.PaymentId == paymentId && c.UsedAt == null && c.RevokedAt == null).ToListAsync(ct)) c.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task RedeemAsync(Guid clientId, string? rawCode, string? ip, CancellationToken ct)
        {
            var since = DateTime.UtcNow.AddMinutes(-LockMinutes);
            if (await _db.AuditLogs.CountAsync(a => a.ClientId == clientId && a.Action == "code.failed" && a.At > since, ct) >= MaxMisses)
                throw new GatewayException(429, "CODE_LOCKED", $"Too many wrong codes. Try again in {LockMinutes} minutes, or contact support.");

            var code = Normalise(rawCode);
            var now = DateTime.UtcNow;
            var live = await _db.ActivationCodes.Where(c => c.ClientId == clientId && c.UsedAt == null && c.RevokedAt == null && c.ExpiresAt > now).ToListAsync(ct);
            var match = code == null ? null : live.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(c.Salt, code)), Encoding.ASCII.GetBytes(c.CodeHash)));
            if (match == null)
            {
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, UserName = _scope.UserName, IpAddress = ip, Action = "code.failed", Detail = "wrong, used or expired activation code" });
                await _db.SaveChangesAsync(ct);
                throw GatewayException.BadRequest("CODE_INVALID", "That code is not valid, was already used, or has expired.");   // the same answer for every kind of miss
            }

            var inv = await _db.BillingInvoices.FirstAsync(i => i.Id == match.InvoiceId, ct);
            if (inv.Status != "Open") { match.RevokedAt = now; await _db.SaveChangesAsync(ct); throw GatewayException.BadRequest("CODE_INVALID", "That code is not valid, was already used, or has expired."); }

            match.UsedAt = now; match.UsedBy = _scope.UserName;
            var pay = match.PaymentId == null ? null : await _db.Payments.FirstOrDefaultAsync(p => p.Id == match.PaymentId, ct);
            if (pay != null) { pay.Status = "Paid"; pay.ConfirmedAt = now; }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, UserName = _scope.UserName, IpAddress = ip, Action = "code.redeemed", Entity = "BillingInvoice", EntityId = inv.Number, Detail = "activation code accepted" });
            await _db.SaveChangesAsync(ct);
            await _billing.ApplyInvoicePaidAsync(inv.Id, PaymentService.Label(pay?.Method ?? "Cash"), pay?.Reference ?? "activation code", ct);
        }
    }

    /// <summary>
    /// Going live. Every company starts on ZIMRA's test (demo) environment. To move it to production, ZIMRA wants six documents fiscalised on a test device: an invoice, a credit
    /// note and a debit note in USD and the same in ZWG. The company issues them and asks to go live; the platform operator emails them to ZIMRA (PDFs attached), ZIMRA approves
    /// and issues a live device ID and activation key, and the operator records them here: the company is switched to live and registers that device.
    /// </summary>
    public sealed class GoLiveService : IGoLiveService
    {
        private static readonly (string Currency, string[] Codes, ReceiptType Type, string Label)[] Slots =
        {
            ("USD", new[] { "USD" }, ReceiptType.FiscalInvoice, "invoice"), ("USD", new[] { "USD" }, ReceiptType.CreditNote, "credit note"), ("USD", new[] { "USD" }, ReceiptType.DebitNote, "debit note"),
            ("ZWG", new[] { "ZWG", "ZIG" }, ReceiptType.FiscalInvoice, "invoice"), ("ZWG", new[] { "ZWG", "ZIG" }, ReceiptType.CreditNote, "credit note"), ("ZWG", new[] { "ZWG", "ZIG" }, ReceiptType.DebitNote, "debit note"),
        };
        private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
        private const string ZimraEmailKey = "GoLive.ZimraEmail";

        private readonly FdmsDbContext _db;
        private readonly IBillingService _billing;
        private readonly IEmailSender _email;
        private readonly IConfiguration _cfg;
        private readonly ICallerScope _scope;
        private readonly INotificationService _notify;
        private readonly UsagePricingStore _prices;
        private readonly IReceiptService _receipts;
        private readonly Microsoft.AspNetCore.DataProtection.IDataProtector _keys;
        public GoLiveService(FdmsDbContext db, IBillingService billing, IEmailSender email, IConfiguration cfg, ICallerScope scope, INotificationService notify, UsagePricingStore prices,
            IReceiptService receipts, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dp)
        {
            _db = db; _billing = billing; _email = email; _cfg = cfg; _scope = scope; _notify = notify; _prices = prices; _receipts = receipts;
            _keys = dp.CreateProtector("Fdms.GoLive.ActivationKey.v1");
        }

        private bool RequireTestDocuments => _cfg.GetValue("GoLive:RequireTestDocuments", true);

        // ---- the six test documents ----

        private static List<Guid> IdsOf(GoLiveRequest r)
        {
            if (string.IsNullOrWhiteSpace(r.TestReceiptIdsJson)) return new();
            try { return System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(r.TestReceiptIdsJson, Json) ?? new(); } catch (System.Text.Json.JsonException) { return new(); }
        }

        private static bool Clean(Receipt r) => string.IsNullOrWhiteSpace(r.ValidationErrorsJson) || r.ValidationErrorsJson.Trim() is "[]" or "null";

        /// <summary>
        /// For each of the six slots, the receipt that fills it: accepted by ZIMRA (submitted, no validation errors) on a test device; a credit or debit note must point at an invoice.
        /// Chosen: only those receipts (a request already sent); null: the latest qualifying ones.
        /// </summary>
        private async Task<List<GoLiveTestDocDto>> TestDocsAsync(Guid clientId, IReadOnlyCollection<Guid>? chosen, CancellationToken ct)
        {
            var devices = await _db.Devices.AsNoTracking().Where(d => d.ClientId == clientId && d.Environment == 0).Select(d => new { d.Id, d.ZimraDeviceId }).ToDictionaryAsync(d => d.Id, d => d.ZimraDeviceId, ct);
            var ids = devices.Keys.ToList();
            var codes = Slots.SelectMany(s => s.Codes).Distinct().ToList();
            var q = _db.Receipts.AsNoTracking().Where(r => r.ClientId == clientId && ids.Contains(r.DeviceId) && r.Status == ReceiptStatus.Submitted && codes.Contains(r.Currency));
            if (chosen != null) { var c = chosen.ToList(); q = q.Where(r => c.Contains(r.Id)); }
            var receipts = await q.OrderByDescending(r => r.ReceiptDate).Take(500).ToListAsync(ct);
            var originalIds = receipts.Where(r => r.OriginalReceiptId != null).Select(r => r.OriginalReceiptId!.Value).Distinct().ToList();
            var originals = await _db.Receipts.AsNoTracking().Where(r => originalIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.InvoiceNo, ct);
            // one coherent set per currency: the latest invoice that has both a credit note and a debit note against it, and those notes (else the latest of each)
            var good = receipts.Where(Clean).ToList();
            var anchor = new Dictionary<string, Guid?>();
            foreach (var cur in Slots.Select(s => s.Currency).Distinct())
            {
                var accepted = Slots.First(s => s.Currency == cur).Codes;
                var inCur = good.Where(x => accepted.Contains(x.Currency.ToUpperInvariant())).ToList();
                anchor[cur] = inCur.Where(x => x.Type == ReceiptType.FiscalInvoice)
                    .FirstOrDefault(i => inCur.Any(n => n.OriginalReceiptId == i.Id && n.Type == ReceiptType.CreditNote) && inCur.Any(n => n.OriginalReceiptId == i.Id && n.Type == ReceiptType.DebitNote))?.Id;
            }
            var list = new List<GoLiveTestDocDto>();
            foreach (var (cur, accepted, type, label) in Slots)
            {
                var slot = $"{cur} {label}";
                var a = anchor[cur];
                var r = a is { } inv
                    ? good.FirstOrDefault(x => type == ReceiptType.FiscalInvoice ? x.Id == inv : x.Type == type && x.OriginalReceiptId == inv)
                    : good.FirstOrDefault(x => accepted.Contains(x.Currency.ToUpperInvariant()) && x.Type == type && (type == ReceiptType.FiscalInvoice || x.OriginalReceiptId != null));
                if (r == null)
                {
                    var problem = devices.Count == 0 ? "Register a test (demo) device first."
                        : type == ReceiptType.FiscalInvoice ? $"Issue a {cur} fiscal invoice on your test device."
                        : $"Issue a {cur} {label} against a {cur} invoice on your test device.";
                    list.Add(new GoLiveTestDocDto(slot, cur, type.ToString(), null, null, null, null, null, null, null, null, problem));
                    continue;
                }
                list.Add(new GoLiveTestDocDto(slot, cur, type.ToString(), r.Id, r.InvoiceNo, r.ReceiptDate, r.Total, r.ReceiptGlobalNo, r.FiscalDayNo, devices.GetValueOrDefault(r.DeviceId),
                    r.OriginalReceiptId is { } o ? originals.GetValueOrDefault(o) : null, null));
            }
            return list;
        }

        private GoLiveDeviceDto? Device(GoLiveRequest r)
        {
            if (r.LiveDeviceId is not { } id) return null;
            string? key = null;
            if (!string.IsNullOrEmpty(r.LiveActivationKeyProtected)) try { key = _keys.Unprotect(r.LiveActivationKeyProtected); } catch (CryptographicException) { key = null; }
            return new GoLiveDeviceDto(id, key, r.LiveSerialNo);
        }

        private async Task<GoLiveRequestDto> DtoAsync(GoLiveRequest r, string? company, string? invoiceNumber, CancellationToken ct)
        {
            var ids = IdsOf(r);
            var docs = ids.Count > 0 ? await TestDocsAsync(r.ClientId, ids, ct) : null;
            return new(r.Id, r.ClientId, company, r.Status, r.ContactName, r.ContactPhone, r.ZimraTin, r.Notes, r.DecisionReason, r.DecidedBy, r.DecidedAt, r.CreatedAt,
                r.Fee, r.InvoiceId, invoiceNumber, docs, r.SentToZimraAt, r.SentToZimraBy, r.ZimraEmail, Device(r));
        }

        private async Task<string?> InvoiceNumberAsync(Guid? id, CancellationToken ct) => id == null ? null : await _db.BillingInvoices.AsNoTracking().Where(i => i.Id == id).Select(i => i.Number).FirstOrDefaultAsync(ct);

        public async Task<GoLiveStatusDto> StatusAsync(Guid clientId, CancellationToken ct)
        {
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            var latest = await _db.GoLiveRequests.AsNoTracking().Where(r => r.ClientId == clientId).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
            var access = await _billing.GetAccessAsync(clientId, ct);
            var docs = await TestDocsAsync(clientId, null, ct);
            var complete = docs.All(d => d.ReceiptId != null);
            string? blocked = client.LiveApproved ? "Your company is already approved for live devices."
                : latest?.Status == "Pending" ? "Your request and test documents are waiting to be sent to ZIMRA."
                : latest?.Status == "SentToZimra" ? "Your test documents were sent to ZIMRA. We let you know as soon as ZIMRA issues your live device."
                : latest?.Status == "AwaitingPayment" ? "Your request is approved. Pay the go-live invoice above and live devices switch on at once."
                : access.DemoOnly ? "Pay for your plan first: live devices need a paid subscription."
                : access.Locked ? "Settle your invoice first."
                : RequireTestDocuments && !complete ? "Issue the six ZIMRA test documents on your test device first (see the list)." : null;
            return new GoLiveStatusDto(client.LiveApproved, client.LiveApprovedAt, latest == null ? null : await DtoAsync(latest, client.Name, await InvoiceNumberAsync(latest.InvoiceId, ct), ct), blocked == null, blocked,
                docs, complete);
        }

        public async Task<GoLiveRequestDto> SubmitAsync(Guid clientId, SubmitGoLiveRequest req, CancellationToken ct)
        {
            if (await PlanModuleAccess.KindAsync(_db, clientId, ct) is var kind && kind != PlanKinds.Fiscal)
                throw GatewayException.Conflict("PLAN_NOT_FISCAL", "Your plan does not include fiscal devices. Change to a fiscal plan under Billing to go live with ZIMRA.");
            var name = req.ContactName?.Trim() ?? ""; var phone = req.ContactPhone?.Trim() ?? "";
            if (name.Length < 2 || name.Length > 120) throw GatewayException.BadRequest("NAME_INVALID", "Enter the name of the person ZIMRA/us should contact.");
            if (phone.Length < 7 || phone.Length > 40) throw GatewayException.BadRequest("PHONE_INVALID", "Enter a phone number we can reach.");
            if ((req.ZimraTin?.Length ?? 0) > 30 || (req.Notes?.Length ?? 0) > 1000) throw GatewayException.BadRequest("FIELD_TOO_LONG", "One of the fields is too long.");
            var st = await StatusAsync(clientId, ct);
            List<GoLiveTestDocDto> docs;
            if (req.TestReceiptIds is { Count: > 0 } chosen)
            {
                docs = await TestDocsAsync(clientId, chosen, ct);
                if (RequireTestDocuments && docs.FirstOrDefault(d => d.ReceiptId == null) is { } missing) throw GatewayException.BadRequest("TEST_DOCUMENTS_MISSING", $"{missing.Slot}: {missing.Problem}");
            }
            else docs = st.TestDocuments?.ToList() ?? new();
            if (!st.CanRequest) throw GatewayException.Conflict("GOLIVE_NOT_ALLOWED", st.Blocked ?? "You cannot request going live right now.");
            var r = new GoLiveRequest { ClientId = clientId, ContactName = name, ContactPhone = phone, ZimraTin = string.IsNullOrWhiteSpace(req.ZimraTin) ? null : req.ZimraTin.Trim(), Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
                TestReceiptIdsJson = docs.Any(d => d.ReceiptId != null) ? System.Text.Json.JsonSerializer.Serialize(docs.Where(d => d.ReceiptId != null).Select(d => d.ReceiptId!.Value), Json) : null };
            _db.GoLiveRequests.Add(r);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "golive.request", Entity = "GoLiveRequest", EntityId = r.Id.ToString(), Detail = $"asked to go live with {docs.Count(d => d.ReceiptId != null)} test document(s)" });
            await _db.SaveChangesAsync(ct);
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstAsync(ct);
            await _notify.RaisePlatformAsync("golive", "info", $"golive:{r.Id}", $"Go-live request: {company}",
                $"{company} sent its ZIMRA test documents and asks to go live (contact {name}, {phone}). Email them to ZIMRA from Go-live requests.", "/admin/go-live", ct);
            return await DtoAsync(r, company, null, ct);
        }

        public async Task<IReadOnlyList<GoLiveRequestDto>> ListAsync(string? status, CancellationToken ct)
        {
            var q = _db.GoLiveRequests.AsNoTracking().AsQueryable();
            if (status is "Pending" or "SentToZimra" or "AwaitingPayment" or "Approved" or "Rejected") q = q.Where(r => r.Status == status);
            var rows = await q.OrderBy(r => r.Status == "Pending" ? 0 : r.Status == "SentToZimra" ? 1 : r.Status == "AwaitingPayment" ? 2 : 3).ThenByDescending(r => r.CreatedAt).Take(300).ToListAsync(ct);
            var names = await _db.Clients.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var invIds = rows.Where(r => r.InvoiceId != null).Select(r => r.InvoiceId!.Value).ToList();
            var numbers = await _db.BillingInvoices.AsNoTracking().Where(i => invIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
            var list = new List<GoLiveRequestDto>();
            foreach (var r in rows) list.Add(await DtoAsync(r, names.GetValueOrDefault(r.ClientId), r.InvoiceId is { } iid ? numbers.GetValueOrDefault(iid) : null, ct));
            return list;
        }

        // ---- to ZIMRA ----

        private async Task<GoLiveRequest> RequestAsync(Guid id, CancellationToken ct) => await _db.GoLiveRequests.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Request");

        private async Task<List<(GoLiveTestDocDto Doc, byte[] Pdf, string Name)>> PdfsAsync(GoLiveRequest r, CancellationToken ct)
        {
            var docs = await TestDocsAsync(r.ClientId, IdsOf(r), ct);
            var list = new List<(GoLiveTestDocDto, byte[], string)>();
            var n = 0;
            foreach (var d in docs.Where(d => d.ReceiptId != null))
            {
                n++;
                var pdf = await _receipts.GetPdfAsync(r.ClientId, d.ReceiptId!.Value, true, ct);
                var safe = new string((d.InvoiceNo ?? "receipt").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
                list.Add((d, pdf, $"{n}-{d.Currency}-{d.Type}-{safe}.pdf"));
            }
            return list;
        }

        public async Task<ZimraEmailDto> ZimraEmailAsync(Guid id, CancellationToken ct)
        {
            var r = await RequestAsync(id, ct);
            var me = await global::Fdms.Infrastructure.Services.CompanyIdentity.LoadAsync(_db, r.ClientId, ct);
            var docs = await TestDocsAsync(r.ClientId, IdsOf(r), ct);
            var testDevices = string.Join(", ", docs.Where(d => d.ZimraDeviceId != null).Select(d => d.ZimraDeviceId!.Value).Distinct());
            var platform = _cfg["Platform:Name"] is { Length: > 0 } p ? p : "FiscalHub";
            var to = await _db.SystemSettings.AsNoTracking().Where(s => s.Key == ZimraEmailKey).Select(s => s.Value).FirstOrDefaultAsync(ct) ?? _cfg["GoLive:ZimraEmail"] ?? "";
            var tin = r.ZimraTin ?? me.Tin;
            var sb = new StringBuilder();
            sb.AppendLine("Dear ZIMRA FDMS team,").AppendLine();
            sb.AppendLine($"We kindly request that {me.Name}{(tin != null ? $" (TIN {tin}" + (me.Vat != null ? $", VAT {me.Vat})" : ")") : "")} be moved from the test environment to production.").AppendLine();
            sb.AppendLine($"The taxpayer has completed testing on test device {testDevices}. The six test documents are attached and listed below:").AppendLine();
            var i = 0;
            foreach (var d in docs)
            {
                i++;
                sb.AppendLine(d.ReceiptId == null ? $"{i}. {d.Slot}: not issued"
                    : $"{i}. {d.Slot} {d.InvoiceNo}{(d.Original != null ? $" (against {d.Original})" : "")}: total {d.Total:N2} {d.Currency}, receipt global no. {d.ReceiptGlobalNo}, fiscal day {d.FiscalDayNo}, device {d.ZimraDeviceId}, {d.Date:dd MMM yyyy HH:mm}");
            }
            sb.AppendLine().AppendLine("Please approve the taxpayer for production and issue the production device ID and activation key.").AppendLine();
            sb.AppendLine($"Taxpayer contact: {r.ContactName}, {r.ContactPhone}{(me.Address != null ? $", {me.Address}" : "")}").AppendLine();
            sb.AppendLine("Kind regards,").AppendLine(platform);
            var files = docs.Where(d => d.ReceiptId != null).Select((d, k) => $"{k + 1}-{d.Currency}-{d.Type}-{d.InvoiceNo}.pdf").ToList();
            return new ZimraEmailDto(to, null, $"Request for production device: {me.Name}{(tin != null ? $" (TIN {tin})" : "")}", sb.ToString(), files);
        }

        private static readonly System.Text.RegularExpressions.Regex EmailRx = new(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$");

        private static List<string> Addresses(string? s) => (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        public async Task<GoLiveRequestDto> SendToZimraAsync(Guid id, SendToZimraRequest req, CancellationToken ct)
        {
            var r = await RequestAsync(id, ct);
            if (r.Status is not ("Pending" or "SentToZimra")) throw GatewayException.Conflict("ALREADY_DECIDED", $"This request was already {r.Status.ToLowerInvariant()}.");
            var to = Addresses(req.To); var cc = Addresses(req.Cc);
            if (to.Count == 0 || to.Concat(cc).Any(a => !EmailRx.IsMatch(a)) || to.Count + cc.Count > 10) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter ZIMRA's email address (and any copies), separated by commas.");
            var subject = (req.Subject ?? "").Trim(); var body = (req.Body ?? "").Trim();
            if (subject.Length is < 3 or > 200 || body.Length is < 10 or > 10000) throw GatewayException.BadRequest("EMAIL_INVALID", "Write a subject and a message.");
            if (!_email.Configured) throw GatewayException.Conflict("EMAIL_NOT_SET_UP", "Email is not set up on this server. Download the pack and send it from your own mailbox instead.");
            var pdfs = await PdfsAsync(r, ct);
            if (pdfs.Count == 0) throw GatewayException.Conflict("TEST_DOCUMENTS_MISSING", "This request has no test documents to send.");
            var attachments = pdfs.Select(p => new EmailAttachment(p.Name, p.Pdf, "application/pdf")).ToList();
            var html = "<div style=\"font-family:Arial,sans-serif;font-size:14px\">" + string.Join("", body.Split('\n').Select(l => WebUtility.HtmlEncode(l.TrimEnd('\r')) + "<br/>")) + "</div>";
            foreach (var address in to.Concat(cc).Distinct(StringComparer.OrdinalIgnoreCase))
                if (await _email.TrySendAsync(address, subject, html, attachments, ct) is { } refused)
                    throw GatewayException.Conflict("EMAIL_FAILED", $"The mail server did not take the message for {address}: {refused}");
            r.Status = "SentToZimra"; r.SentToZimraAt = DateTime.UtcNow; r.SentToZimraBy = _scope.UserName; r.ZimraEmail = string.Join(", ", to.Concat(cc));
            var setting = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == ZimraEmailKey, ct);
            if (setting == null) _db.SystemSettings.Add(setting = new SystemSetting { Key = ZimraEmailKey });
            setting.Value = string.Join(", ", to); setting.UpdatedAt = DateTime.UtcNow; setting.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = r.ClientId, Action = "golive.zimra", Entity = "GoLiveRequest", EntityId = r.Id.ToString(), Detail = $"test documents sent to {r.ZimraEmail}" });
            await _db.SaveChangesAsync(ct);
            await _notify.RaiseAsync(r.ClientId, "billing", "info", $"golive-zimra:{r.Id}", "Your test documents were sent to ZIMRA",
                "We sent your six test documents to ZIMRA and asked for your live device. We let you know as soon as ZIMRA issues it.", "/billing", ct);
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == r.ClientId).Select(c => c.Name).FirstAsync(ct);
            return await DtoAsync(r, company, await InvoiceNumberAsync(r.InvoiceId, ct), ct);
        }

        public async Task<(byte[] Zip, string FileName)> PackAsync(Guid id, CancellationToken ct)
        {
            var r = await RequestAsync(id, ct);
            var pdfs = await PdfsAsync(r, ct);
            var mail = await ZimraEmailAsync(id, ct);
            using var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                foreach (var (_, pdf, name) in pdfs) { var e = zip.CreateEntry(name); using var s = e.Open(); s.Write(pdf); }
                var summary = zip.CreateEntry("request-to-zimra.txt");
                using (var w = new StreamWriter(summary.Open(), new UTF8Encoding(false))) { w.WriteLine("Subject: " + mail.Subject); w.WriteLine(); w.Write(mail.Body); }
            }
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == r.ClientId).Select(c => c.Name).FirstAsync(ct);
            var safe = new string(company.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            return (ms.ToArray(), $"zimra-test-documents-{safe}.zip");
        }

        public async Task<(byte[] Pdf, string FileName)> DocumentPdfAsync(Guid id, Guid receiptId, CancellationToken ct)
        {
            var r = await RequestAsync(id, ct);
            if (!IdsOf(r).Contains(receiptId)) throw GatewayException.NotFound("Document");
            var no = await _db.Receipts.AsNoTracking().Where(x => x.Id == receiptId).Select(x => x.InvoiceNo).FirstOrDefaultAsync(ct) ?? "receipt";
            return (await _receipts.GetPdfAsync(r.ClientId, receiptId, true, ct), $"{no}.pdf");
        }

        // ---- the decision ----

        public async Task<GoLiveRequestDto> ApproveAsync(Guid id, string? note, CancellationToken ct, decimal? fee = null, int? liveDeviceId = null, string? activationKey = null, string? serialNo = null)
        {
            var r = await RequestAsync(id, ct);
            if (r.Status is not ("Pending" or "SentToZimra")) throw GatewayException.Conflict("ALREADY_DECIDED", $"This request was already {r.Status.ToLowerInvariant()}.");
            var key = string.IsNullOrWhiteSpace(activationKey) ? null : activationKey.Trim().ToUpperInvariant();
            if (liveDeviceId is { } dev)
            {
                if (dev <= 0) throw GatewayException.BadRequest("DEVICE_INVALID", "Enter the live device ID ZIMRA issued.");
                if (key == null || key.Length != 8 || !key.All(char.IsLetterOrDigit)) throw GatewayException.BadRequest("KEY_INVALID", "Enter the 8-character activation key ZIMRA issued with the device.");
                r.LiveDeviceId = dev; r.LiveActivationKeyProtected = _keys.Protect(key); r.LiveSerialNo = string.IsNullOrWhiteSpace(serialNo) ? null : serialNo.Trim()[..Math.Min(60, serialNo.Trim().Length)];
            }
            else if (key != null) throw GatewayException.BadRequest("DEVICE_INVALID", "Enter the live device ID together with its activation key.");
            var deviceLine = r.LiveDeviceId is { } d1 ? $" ZIMRA issued your live device {d1}: register it under Devices (environment Production) with the activation key shown under Billing." : "";
            var client = await _db.Clients.FirstAsync(c => c.Id == r.ClientId, ct);
            var prices = await _prices.LoadAsync(ct);
            var charge = decimal.Round(fee ?? (prices.Charge ? prices.GoLiveFee : 0), 2);
            if (charge is < 0 or > 1_000_000) throw GatewayException.BadRequest("FEE_INVALID", "The go-live fee must be zero or more.");
            r.DecidedAt = DateTime.UtcNow; r.DecidedBy = _scope.UserName; r.DecisionReason = string.IsNullOrWhiteSpace(note) ? null : note.Trim(); r.Fee = charge;
            if (charge > 0)
            {
                // approved with a fee: live devices switch on when its invoice is paid
                r.Status = "AwaitingPayment";
                await _db.SaveChangesAsync(ct);
                var currency = await (from s in _db.Subscriptions join p in _db.Plans on s.PlanId equals p.Id where s.ClientId == r.ClientId select p.Currency).FirstOrDefaultAsync(ct) ?? prices.Currency;
                var inv = await _billing.IssueOneOffAsync(r.ClientId, "golive", "Go-live: live ZIMRA FDMS devices (set-up and registration)", charge, currency, ct);
                r.InvoiceId = inv.Id;
                _db.AuditLogs.Add(new AuditLog { ClientId = r.ClientId, Action = "golive.approve", Entity = "GoLiveRequest", EntityId = r.Id.ToString(), Detail = $"approved with a go-live fee of {charge:0.00} {currency} ({inv.Number}){(r.LiveDeviceId != null ? $", live device {r.LiveDeviceId}" : "")}" });
                await _db.SaveChangesAsync(ct);
                await _notify.RaiseAsync(r.ClientId, "billing", "info", $"golive-approved:{r.Id}", "Your go-live request is approved: pay to switch live devices on",
                    $"Pay invoice {inv.Number} ({inv.Total:0.00} {inv.Currency}) under Billing and you can register live (Production) devices at once.{deviceLine}", "/billing", ct);
                return await DtoAsync(r, client.Name, inv.Number, ct);
            }
            r.Status = "Approved";
            client.LiveApproved = true; client.LiveApprovedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = r.ClientId, Action = "golive.approve", Entity = "GoLiveRequest", EntityId = r.Id.ToString(), Detail = "live devices enabled" + (r.LiveDeviceId != null ? $", live device {r.LiveDeviceId}" : "") });
            await _db.SaveChangesAsync(ct);
            _billingCacheClear(r.ClientId);
            await _notify.RaiseAsync(r.ClientId, "billing", "ok", $"golive-live:{r.Id}", "You can now use live ZIMRA devices", "Your company is approved for live (Production) devices." + deviceLine, "/billing", ct);
            if (!string.IsNullOrWhiteSpace(client.Email))
                await _email.SendAsync(client.Email, "You can now register live devices",
                    $"<p>Your request was approved: <b>{WebUtility.HtmlEncode(client.Name)}</b> can now register live (Production) FDMS devices.</p>"
                    + (r.LiveDeviceId is { } d2 ? $"<p>ZIMRA issued your live device <b>{d2}</b>. Its activation key is shown under Billing → Go live; register the device under Devices with the environment set to Production.</p>" : ""), ct);
            return await DtoAsync(r, client.Name, null, ct);
        }

        public async Task<GoLiveRequestDto> RejectAsync(Guid id, string reason, CancellationToken ct)
        {
            var r = await RequestAsync(id, ct);
            if (r.Status is not ("Pending" or "SentToZimra" or "AwaitingPayment")) throw GatewayException.Conflict("ALREADY_DECIDED", $"This request was already {r.Status.ToLowerInvariant()}.");
            var why = reason?.Trim() ?? "";
            if (why.Length < 3 || why.Length > 500) throw GatewayException.BadRequest("REASON_REQUIRED", "Give a reason (3-500 characters); the company is told.");
            r.Status = "Rejected"; r.DecidedAt = DateTime.UtcNow; r.DecidedBy = _scope.UserName; r.DecisionReason = why;
            if (r.InvoiceId is { } invoiceId && await _db.BillingInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.Status == "Open", ct) is { } open)
            {
                open.Status = "Void";   // the unpaid go-live invoice goes with the request
                _db.Notifications.RemoveRange(await _db.Notifications.Where(n => n.ClientId == r.ClientId && (n.Key == $"invoice:{invoiceId}" || n.Key == $"golive-approved:{r.Id}")).ToListAsync(ct));
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = r.ClientId, Action = "golive.reject", Entity = "GoLiveRequest", EntityId = r.Id.ToString(), Detail = why });
            await _db.SaveChangesAsync(ct);
            var client = await _db.Clients.AsNoTracking().FirstAsync(c => c.Id == r.ClientId, ct);
            if (!string.IsNullOrWhiteSpace(client.Email))
                await _email.SendAsync(client.Email, "About your go-live request", $"<p>We could not enable live devices yet: {WebUtility.HtmlEncode(why)}</p>", ct);
            return await DtoAsync(r, client.Name, null, ct);
        }

        public async Task<IReadOnlyList<LiveCompanyDto>> LiveCompaniesAsync(CancellationToken ct)
        {
            var clients = await _db.Clients.AsNoTracking().Where(c => c.LiveApproved).OrderBy(c => c.Name).Select(c => new { c.Id, c.Name, c.LiveApprovedAt }).ToListAsync(ct);
            var ids = clients.Select(c => c.Id).ToList();
            var viaZimra = (await _db.GoLiveRequests.AsNoTracking().Where(r => ids.Contains(r.ClientId) && r.Status == "Approved" && (r.LiveDeviceId != null || r.TestReceiptIdsJson != null))
                .Select(r => new { r.ClientId, r.LiveDeviceId, r.DecidedAt }).ToListAsync(ct)).GroupBy(r => r.ClientId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.DecidedAt).First().LiveDeviceId);
            var devices = await _db.Devices.AsNoTracking().Where(d => ids.Contains(d.ClientId) && d.IsActive).GroupBy(d => new { d.ClientId, d.Environment }).Select(g => new { g.Key.ClientId, g.Key.Environment, N = g.Count() }).ToListAsync(ct);
            int Count(Guid id, int env) => devices.Where(d => d.ClientId == id && d.Environment == env).Sum(d => d.N);
            return clients.Select(c => new LiveCompanyDto(c.Id, c.Name, c.LiveApprovedAt, viaZimra.ContainsKey(c.Id), viaZimra.GetValueOrDefault(c.Id), Count(c.Id, 1), Count(c.Id, 0))).ToList();
        }

        public async Task RevokeAsync(Guid clientId, CancellationToken ct)
        {
            var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId, ct) ?? throw GatewayException.NotFound("Client");
            client.LiveApproved = false; client.LiveApprovedAt = null;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "golive.revoke", Entity = "Client", EntityId = clientId.ToString(), Detail = "live devices disabled" });
            await _db.SaveChangesAsync(ct);
            _billingCacheClear(clientId);
        }

        private void _billingCacheClear(Guid clientId) => _billing.ForgetAccess(clientId);
    }
}
