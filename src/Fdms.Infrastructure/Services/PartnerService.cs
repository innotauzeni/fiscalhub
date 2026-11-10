using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Partners (accounting firms and resellers) and what they earn. A company joins a partner through the partner's referral code or when the operator attaches it; from then on the
    /// partner earns its percentage of every subscription invoice the company pays (before tax). The percentage is copied onto the company when it joins, so changing a partner's
    /// rate later does not rewrite what was already earned. Money paid out is recorded against what is owed, per currency.
    /// </summary>
    public sealed class PartnerService : IPartnerService
    {
        private static readonly Regex CodeRx = new("^[A-Z0-9]{3,20}$", RegexOptions.Compiled);
        private readonly FdmsDbContext _db;
        private readonly UserManager<ApplicationUser> _users;
        private readonly ICallerScope _scope;
        public PartnerService(FdmsDbContext db, UserManager<ApplicationUser> users, ICallerScope scope) { _db = db; _users = users; _scope = scope; }

        private sealed record Earning(Guid PartnerId, Guid ClientId, string Company, string Invoice, string Currency, decimal Amount, decimal Percent, decimal Commission, DateTime Date);

        private async Task<List<Earning>> EarningsAsync(Guid? partnerId, CancellationToken ct)
        {
            var rows = await (from i in _db.BillingInvoices.AsNoTracking()
                              join c in _db.Clients.AsNoTracking() on i.ClientId equals c.Id
                              where i.Status == "Paid" && c.PartnerId != null && (partnerId == null || c.PartnerId == partnerId)
                              select new { PartnerId = c.PartnerId!.Value, ClientId = c.Id, c.Name, c.PartnerPercent, i.Number, i.Subtotal, i.Currency, i.PaidAt, i.IssuedAt }).ToListAsync(ct);
            return rows.Select(r => new Earning(r.PartnerId, r.ClientId, r.Name, r.Number, r.Currency, r.Subtotal, r.PartnerPercent, Math.Round(r.Subtotal * r.PartnerPercent / 100m, 2), r.PaidAt ?? r.IssuedAt))
                       .OrderBy(e => e.Date).ToList();
        }

        private static List<AmountDto> Totals(IEnumerable<(string Currency, decimal Amount)> rows) =>
            rows.GroupBy(r => r.Currency).Select(g => new AmountDto(g.Key, g.Sum(x => x.Amount))).Where(a => a.Amount != 0).OrderBy(a => a.Currency).ToList();

        private static List<AmountDto> Owed(IReadOnlyList<AmountDto> earned, IReadOnlyList<AmountDto> paid) =>
            earned.Select(e => e.Currency).Union(paid.Select(p => p.Currency)).Distinct().OrderBy(c => c)
                .Select(c => new AmountDto(c, earned.Where(e => e.Currency == c).Sum(e => e.Amount) - paid.Where(p => p.Currency == c).Sum(p => p.Amount))).Where(a => a.Amount != 0).ToList();

        // ---- operator ------------------------------------------------------------------------------------------------------

        public async Task<IReadOnlyList<PartnerDto>> ListAsync(CancellationToken ct)
        {
            var partners = await _db.Partners.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            var earnings = await EarningsAsync(null, ct);
            var payouts = await _db.PartnerPayouts.AsNoTracking().ToListAsync(ct);
            var clients = (await _db.Clients.AsNoTracking().Where(c => c.PartnerId != null).Select(c => c.PartnerId!.Value).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var staff = (await _db.Users.AsNoTracking().Where(u => u.PartnerId != null).Select(u => u.PartnerId!.Value).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            return partners.Select(p =>
            {
                var earned = Totals(earnings.Where(e => e.PartnerId == p.Id).Select(e => (e.Currency, e.Commission)));
                var paid = Totals(payouts.Where(x => x.PartnerId == p.Id).Select(x => (x.Currency, x.Amount)));
                return new PartnerDto(p.Id, p.Name, p.Code, p.ContactName, p.Email, p.Phone, p.CommissionPercent, p.IsActive, p.Notes, clients.GetValueOrDefault(p.Id), staff.GetValueOrDefault(p.Id), earned, paid, Owed(earned, paid), p.Kind, p.DiscountPercent);
            }).ToList();
        }

        private static string? Clean(string? v, int max, string what)
        {
            v = v?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (v.Length > max) throw GatewayException.BadRequest("TOO_LONG", $"{what} can be at most {max} characters.");
            return v;
        }

        public async Task<PartnerDto> SaveAsync(SavePartnerRequest req, CancellationToken ct)
        {
            var name = Clean(req.Name, 150, "The name") ?? throw GatewayException.BadRequest("NAME_REQUIRED", "Enter the partner's name.");
            if (req.CommissionPercent is < 0 or > 100) throw GatewayException.BadRequest("PERCENT_INVALID", "The commission is between 0 and 100 percent.");
            if (req.Kind is not (null or "commission" or "reseller")) throw GatewayException.BadRequest("KIND_INVALID", "A partner is a commission partner or a reseller.");
            if (req.DiscountPercent is < 0 or > 90) throw GatewayException.BadRequest("DISCOUNT_INVALID", "The reseller discount is between 0 and 90 percent.");
            var email = Clean(req.Email, 200, "The email");
            if (email != null && !email.Contains('@')) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter a valid email address.");
            var code = (req.Code ?? "").Trim().ToUpperInvariant();
            if (code.Length == 0) code = new string(name.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
            if (!CodeRx.IsMatch(code)) throw GatewayException.BadRequest("CODE_INVALID", "The referral code is 3 to 20 letters or digits.");

            Partner p;
            if (req.Id is { } id) p = await _db.Partners.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw GatewayException.NotFound("Partner");
            else { p = new Partner(); _db.Partners.Add(p); }
            if (await _db.Partners.AnyAsync(x => x.Code == code && x.Id != p.Id, ct)) throw GatewayException.Conflict("CODE_TAKEN", "Another partner already uses that referral code.");
            p.Name = name; p.Code = code; p.ContactName = Clean(req.ContactName, 120, "The contact name"); p.Email = email; p.Phone = Clean(req.Phone, 40, "The phone");
            p.CommissionPercent = req.CommissionPercent; p.IsActive = req.IsActive; p.Notes = Clean(req.Notes, 1000, "The notes");
            if (req.Kind != null) p.Kind = req.Kind;
            if (req.DiscountPercent is { } d) p.DiscountPercent = decimal.Round(d, 2);
            if (p.Kind == "reseller") p.CommissionPercent = 0;   // a reseller earns through its own prices, not from the platform
            _db.AuditLogs.Add(new AuditLog { Action = req.Id == null ? "partner.create" : "partner.update", Entity = "Partner", EntityId = p.Id.ToString(), Detail = p.Kind == "reseller" ? $"{p.Name} ({p.Code}) reseller, {p.DiscountPercent}% off" : $"{p.Name} ({p.Code}) {p.CommissionPercent}%" });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(ct)).First(x => x.Id == p.Id);
        }

        public async Task<IReadOnlyList<PartnerStaffDto>> StaffAsync(Guid partnerId, CancellationToken ct) =>
            (await _db.Users.AsNoTracking().Where(u => u.PartnerId == partnerId).OrderBy(u => u.Name).ToListAsync(ct))
                .Select(u => new PartnerStaffDto(u.Id, (u.Name + " " + u.Surname).Trim(), u.Email ?? "", u.Status, u.LastLoginDate)).ToList();

        public async Task<PartnerStaffDto> AddStaffAsync(Guid partnerId, AddPartnerStaffRequest req, CancellationToken ct)
        {
            if (!await _db.Partners.AnyAsync(p => p.Id == partnerId, ct)) throw GatewayException.NotFound("Partner");
            var email = (req.Email ?? "").Trim();
            if (email.Length > 200 || !email.Contains('@')) throw GatewayException.BadRequest("EMAIL_INVALID", "Enter a valid email address.");
            if (await _users.FindByEmailAsync(email) != null) throw GatewayException.Conflict("USER_EXISTS", "A user with that email already exists.");
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, Name = (req.Name ?? "").Trim(), Surname = (req.Surname ?? "").Trim(), PartnerId = partnerId, MustChangePassword = true };
            var res = await _users.CreateAsync(user, req.Password ?? "");
            if (!res.Succeeded) throw GatewayException.BadRequest("PASSWORD_REJECTED", string.Join(" ", res.Errors.Select(e => e.Description)));
            await _users.AddToRoleAsync(user, RoleNames.Partner);
            _db.AuditLogs.Add(new AuditLog { Action = "partner.staff-add", Entity = "Partner", EntityId = partnerId.ToString(), Detail = email });
            await _db.SaveChangesAsync(ct);
            return new PartnerStaffDto(user.Id, (user.Name + " " + user.Surname).Trim(), email, user.Status, null);
        }

        public async Task<IReadOnlyList<PartnerClientLinkDto>> ClientLinksAsync(CancellationToken ct)
        {
            var names = await _db.Partners.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            return (await _db.Clients.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
                .Select(c => new PartnerClientLinkDto(c.Id, c.Name, c.PartnerId, c.PartnerId is { } id ? names.GetValueOrDefault(id) : null, c.PartnerPercent)).ToList();
        }

        public async Task<PartnerClientLinkDto> SetClientPartnerAsync(Guid clientId, SetClientPartnerRequest req, CancellationToken ct)
        {
            var c = await _db.Clients.FirstOrDefaultAsync(x => x.Id == clientId, ct) ?? throw GatewayException.NotFound("Company");
            if (req.PartnerId is { } pid)
            {
                var p = await _db.Partners.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct) ?? throw GatewayException.NotFound("Partner");
                var pct = p.Kind == "reseller" ? 0 : req.Percent ?? p.CommissionPercent;   // a reseller's companies earn it no commission
                if (pct is < 0 or > 100) throw GatewayException.BadRequest("PERCENT_INVALID", "The commission is between 0 and 100 percent.");
                c.PartnerId = p.Id; c.PartnerPercent = pct;
            }
            else { c.PartnerId = null; c.PartnerPercent = 0; }
            _db.AuditLogs.Add(new AuditLog { ClientId = c.Id, Action = "partner.attach", Entity = "Client", EntityId = c.Id.ToString(), Detail = req.PartnerId == null ? "detached from its partner" : $"attached to a partner at {c.PartnerPercent}%" });
            await _db.SaveChangesAsync(ct);
            return (await ClientLinksAsync(ct)).First(x => x.ClientId == clientId);
        }

        public async Task<PartnerPayoutDto> RecordPayoutAsync(Guid partnerId, RecordPayoutRequest req, CancellationToken ct)
        {
            if (!await _db.Partners.AnyAsync(p => p.Id == partnerId, ct)) throw GatewayException.NotFound("Partner");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (cur.Length != 3) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency.");
            if (req.Amount <= 0 || req.Amount > 1_000_000_000m || decimal.Round(req.Amount, 2) != req.Amount) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter an amount above zero with at most 2 decimals.");
            var earned = (await EarningsAsync(partnerId, ct)).Where(e => e.Currency == cur).Sum(e => e.Commission);
            var paid = await _db.PartnerPayouts.Where(x => x.PartnerId == partnerId && x.Currency == cur).SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
            if (req.Amount > earned - paid) throw GatewayException.Conflict("OVERPAYMENT", $"Only {earned - paid:0.00} {cur} is owed to this partner.");
            var p = new PartnerPayout { PartnerId = partnerId, Date = req.Date.Date, Amount = req.Amount, Currency = cur, Reference = Clean(req.Reference, 100, "The reference"), Notes = Clean(req.Notes, 500, "The notes"), RecordedBy = _scope.UserName };
            _db.PartnerPayouts.Add(p);
            _db.AuditLogs.Add(new AuditLog { Action = "partner.payout", Entity = "Partner", EntityId = partnerId.ToString(), Detail = $"{req.Amount:0.00} {cur}" });
            await _db.SaveChangesAsync(ct);
            return new PartnerPayoutDto(p.Id, p.Date, p.Amount, p.Currency, p.Reference, p.Notes, p.RecordedBy);
        }

        // ---- the partner's own view ------------------------------------------------------------------------------------------

        private async Task<Partner> MineAsync(Guid partnerId, CancellationToken ct) =>
            await _db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partnerId, ct) ?? throw GatewayException.NotFound("Partner");

        public async Task<PartnerSummaryDto> SummaryAsync(Guid partnerId, CancellationToken ct)
        {
            var p = await MineAsync(partnerId, ct);
            var earnings = await EarningsAsync(partnerId, ct);
            var earned = Totals(earnings.Select(e => (e.Currency, e.Commission)));
            var paid = Totals((await _db.PartnerPayouts.AsNoTracking().Where(x => x.PartnerId == partnerId).ToListAsync(ct)).Select(x => (x.Currency, x.Amount)));
            var clients = await _db.Clients.AsNoTracking().Where(c => c.PartnerId == partnerId).Select(c => c.IsActive).ToListAsync(ct);
            var pending = await _db.Applications.CountAsync(a => a.PartnerId == partnerId && a.Status == "Pending", ct);
            return new PartnerSummaryDto(p.Name, p.Code, p.CommissionPercent, "/register?ref=" + p.Code, clients.Count, clients.Count(a => a), pending, earned, paid, Owed(earned, paid), p.Kind, p.DiscountPercent);
        }

        public async Task<IReadOnlyList<PartnerClientDto>> ClientsAsync(Guid partnerId, CancellationToken ct)
        {
            await MineAsync(partnerId, ct);
            var clients = await _db.Clients.AsNoTracking().Where(c => c.PartnerId == partnerId).OrderBy(c => c.Name).ToListAsync(ct);
            var ids = clients.Select(c => c.Id).ToList();
            var since = DateTime.UtcNow.AddDays(-30);
            var devices = (await _db.Devices.AsNoTracking().Where(d => ids.Contains(d.ClientId)).Select(d => d.ClientId).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
            var rec = await _db.Receipts.AsNoTracking().Where(r => ids.Contains(r.ClientId) && r.CreatedAt >= since && r.Status != ReceiptStatus.Cancelled && r.Status != ReceiptStatus.Held)
                .GroupBy(r => r.ClientId).Select(g => new { Id = g.Key, N = g.Count(), Last = g.Max(x => x.CreatedAt) }).ToDictionaryAsync(x => x.Id, ct);
            var subs = (await _db.Subscriptions.AsNoTracking().Where(s => ids.Contains(s.ClientId)).ToListAsync(ct)).GroupBy(s => s.ClientId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedAt).First());
            var plans = await _db.Plans.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var earnings = await EarningsAsync(partnerId, ct);
            return clients.Select(c =>
            {
                subs.TryGetValue(c.Id, out var s);
                var status = !c.IsActive ? "Suspended" : s?.Status ?? "No plan";
                return new PartnerClientDto(c.Name, status, s == null ? null : plans.GetValueOrDefault(s.PlanId), devices.GetValueOrDefault(c.Id), rec.TryGetValue(c.Id, out var r) ? r.N : 0, rec.TryGetValue(c.Id, out var r2) ? r2.Last : null,
                    c.CreatedAt, c.PartnerPercent, Totals(earnings.Where(e => e.ClientId == c.Id).Select(e => (e.Currency, e.Commission))));
            }).ToList();
        }

        public async Task<IReadOnlyList<PartnerReferralDto>> ReferralsAsync(Guid partnerId, CancellationToken ct)
        {
            await MineAsync(partnerId, ct);
            return (await _db.Applications.AsNoTracking().Where(a => a.PartnerId == partnerId).OrderByDescending(a => a.CreatedAt).Take(200).ToListAsync(ct))
                .Select(a => new PartnerReferralDto(a.CompanyName, (a.ContactName + " " + a.ContactSurname).Trim(), a.Status, a.CreatedAt)).ToList();
        }

        public async Task<PartnerStatementDto> StatementAsync(Guid partnerId, CancellationToken ct)
        {
            var p = await MineAsync(partnerId, ct);
            var earnings = await EarningsAsync(partnerId, ct);
            var payouts = (await _db.PartnerPayouts.AsNoTracking().Where(x => x.PartnerId == partnerId).OrderBy(x => x.Date).ToListAsync(ct))
                .Select(x => new PartnerPayoutDto(x.Id, x.Date, x.Amount, x.Currency, x.Reference, x.Notes, x.RecordedBy)).ToList();
            var earned = Totals(earnings.Select(e => (e.Currency, e.Commission)));
            var paid = Totals(payouts.Select(x => (x.Currency, x.Amount)));
            return new PartnerStatementDto(p.Name, earnings.Select(e => new CommissionLineDto(e.Date, e.Company, e.Invoice, e.Currency, e.Amount, e.Percent, e.Commission)).ToList(), payouts, earned, paid, Owed(earned, paid));
        }
    }
}
