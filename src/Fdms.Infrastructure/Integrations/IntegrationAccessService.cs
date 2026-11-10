using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Integrations
{
    /// <summary>
    /// Who may use the Zoho Books integration. A company applies (saying what it needs it for), the platform owner approves or rejects; with a set-up fee the approval
    /// raises an invoice and the integration switches on when it is paid. The owner can suspend a company's integration (and revoke any key) at any time.
    /// </summary>
    public sealed class IntegrationAccessService : IIntegrationAccessService
    {
        public static readonly string[] AllUses = { "fiscalise", "own_api", "partners" };
        private readonly FdmsDbContext _db;
        private readonly IBillingService _billing;
        private readonly INotificationService _notify;
        private readonly IntegrationSettings _settings;
        private readonly ICallerScope _scope;
        public IntegrationAccessService(FdmsDbContext db, IBillingService billing, INotificationService notify, IntegrationSettings settings, ICallerScope scope)
        { _db = db; _billing = billing; _notify = notify; _settings = settings; _scope = scope; }

        /// <summary>The one check every Zoho action makes.</summary>
        public static Task<bool> IsActiveAsync(FdmsDbContext db, Guid clientId, CancellationToken ct, string provider = IntegrationProducts.Zoho) =>
            db.IntegrationAccess.AsNoTracking().AnyAsync(a => a.ClientId == clientId && a.Provider == provider && a.Status == "Active", ct);

        /// <summary>Approved (paid or waiting for the set-up invoice): the company may set things up (connect Zoho, settings, keys), but nothing runs until it is Active.</summary>
        public static Task<bool> IsApprovedAsync(FdmsDbContext db, Guid clientId, CancellationToken ct, string provider = IntegrationProducts.Zoho) =>
            db.IntegrationAccess.AsNoTracking().AnyAsync(a => a.ClientId == clientId && a.Provider == provider && (a.Status == "Active" || a.Status == "AwaitingPayment"), ct);

        public static async Task EnsureApprovedAsync(FdmsDbContext db, Guid clientId, CancellationToken ct, string provider = IntegrationProducts.Zoho)
        {
            if (await IsApprovedAsync(db, clientId, ct, provider)) return;
            await EnsureActiveAsync(db, clientId, ct, provider);
        }

        /// <summary>Takes back the notices about an integration invoice that is paid or no longer due.</summary>
        public static async Task ClearInvoiceNoticesAsync(FdmsDbContext db, Guid clientId, Guid accessId, Guid invoiceId, CancellationToken ct)
        {
            var invoiceKey = $"invoice:{invoiceId}"; var approvedKey = $"integration-approved:{accessId}:";
            var stale = await db.Notifications.Where(n => n.ClientId == clientId && (n.Key == invoiceKey || n.Key.StartsWith(approvedKey))).ToListAsync(ct);
            db.Notifications.RemoveRange(stale);
        }

        public static async Task EnsureActiveAsync(FdmsDbContext db, Guid clientId, CancellationToken ct, string provider = IntegrationProducts.Zoho)
        {
            var status = await db.IntegrationAccess.AsNoTracking().Where(a => a.ClientId == clientId && a.Provider == provider).Select(a => a.Status).FirstOrDefaultAsync(ct);
            if (status == "Active") return;
            var name = IntegrationProducts.Label(provider);
            throw new GatewayException(403, "INTEGRATION_NOT_ACTIVE", status switch
            {
                "Requested" => $"Your application for the {name} integration is being reviewed.",
                "AwaitingPayment" => $"Your {name} integration is approved and set up, but stays inactive until its set-up invoice is paid under Billing.",
                "Suspended" => $"The {name} integration is suspended for your company. Contact the platform operator.",
                "Rejected" => $"Your application for the {name} integration was not approved. You can apply again.",
                _ => $"Apply for the {name} integration first.",
            });
        }

        private static string Cut(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max];

        private async Task<IntegrationAccessDto> ToDtoAsync(IntegrationAccess? a, Guid clientId, CancellationToken ct)
        {
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "?";
            var provider = a?.Provider ?? _provider;
            var label = IntegrationProducts.Label(provider);
            var p = await _settings.PricingAsync(provider, ct);
            if (a == null) return new IntegrationAccessDto(null, clientId, company, "None", Array.Empty<string>(), null, null, null, null, null, null, p.SetupFee, p.Currency, null, null, null, null, null, provider, label);
            var inv = a.InvoiceId == null ? null : await _db.BillingInvoices.AsNoTracking().Where(i => i.Id == a.InvoiceId).Select(i => new { i.Number, i.Status, i.Currency }).FirstOrDefaultAsync(ct);
            return new IntegrationAccessDto(a.Id, a.ClientId, company, a.Status, a.Uses.Split(',', StringSplitOptions.RemoveEmptyEntries), a.Note, a.RequestedBy, a.RequestedAt, a.DecidedBy, a.DecidedAt, a.DecisionNote,
                a.Status is "Requested" or "Rejected" ? p.SetupFee : a.SetupFee, inv?.Currency ?? p.Currency, a.InvoiceId, inv?.Number, inv?.Status, a.ActivatedAt, a.SuspendedAt, provider, label);
        }

        private string _provider = IntegrationProducts.Zoho;   // for an application that does not exist yet

        public async Task<IntegrationAccessDto> MineAsync(Guid clientId, CancellationToken ct, string provider = IntegrationProducts.Zoho)
        {
            _provider = IntegrationProducts.Get(provider).Key;
            return await ToDtoAsync(await _db.IntegrationAccess.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == clientId && a.Provider == _provider, ct), clientId, ct);
        }

        public async Task<IntegrationAccessDto> ApplyAsync(Guid clientId, ApplyIntegrationRequest req, CancellationToken ct)
        {
            var product = IntegrationProducts.Get(req.Provider);
            _provider = product.Key;
            var uses = (req.Uses ?? Array.Empty<string>()).Select(u => u.Trim().ToLowerInvariant()).Where(AllUses.Contains).Distinct().ToList();
            if (product.Key != IntegrationProducts.Zoho && uses.Count == 0) uses.Add("fiscalise");   // the connectors fiscalise; nothing else to choose
            if (uses.Count == 0) throw GatewayException.BadRequest("USES_REQUIRED", "Tick what you need the integration for.");
            var note = req.Note?.Trim();
            if (note is { Length: > 1000 }) throw GatewayException.BadRequest("NOTE_TOO_LONG", "Keep the description under 1000 characters.");
            var a = await _db.IntegrationAccess.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == product.Key, ct);
            if (a is { Status: "Active" or "AwaitingPayment" or "Requested" }) throw GatewayException.Conflict("ALREADY_APPLIED", a.Status == "Active" ? "The integration is already on." : "Your application is already in.");
            if (a is { Status: "Suspended" }) throw GatewayException.Conflict("SUSPENDED", "The integration is suspended for your company. Contact the platform operator.");
            if (a == null) { a = new IntegrationAccess { ClientId = clientId, Provider = product.Key }; _db.IntegrationAccess.Add(a); }
            a.Status = "Requested"; a.Uses = string.Join(",", uses); a.Note = string.IsNullOrEmpty(note) ? null : note; a.RequestedBy = Cut(_scope.UserName, 200); a.RequestedAt = DateTime.UtcNow;
            a.DecidedAt = null; a.DecidedBy = null; a.DecisionNote = null; a.InvoiceId = null;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.apply", Entity = "IntegrationAccess", EntityId = a.Id.ToString(), Detail = a.Uses });
            await _db.SaveChangesAsync(ct);
            var company = await _db.Clients.AsNoTracking().Where(c => c.Id == clientId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            await _notify.RaisePlatformAsync("application", "info", $"integration-apply:{a.Id}:{a.RequestedAt:yyyyMMddHHmmss}", $"{company} applied for the {product.Label} integration",
                $"For: {a.Uses.Replace(",", ", ")}. {a.Note}", "/admin/integrations", ct);
            return await ToDtoAsync(a, clientId, ct);
        }

        public async Task<IReadOnlyList<IntegrationAccessDto>> ListAsync(string? status, CancellationToken ct)
        {
            var q = _db.IntegrationAccess.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(a => a.Status == status);
            var rows = await q.OrderBy(a => a.Status == "Requested" ? 0 : a.Status == "AwaitingPayment" ? 1 : 2).ThenByDescending(a => a.RequestedAt).Take(500).ToListAsync(ct);
            var list = new List<IntegrationAccessDto>();
            foreach (var a in rows) list.Add(await ToDtoAsync(a, a.ClientId, ct));
            return list;
        }

        private async Task<IntegrationAccess> FindAsync(Guid id, CancellationToken ct) =>
            await _db.IntegrationAccess.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw GatewayException.NotFound("Application");

        public async Task<IntegrationAccessDto> ApproveAsync(Guid id, ApproveIntegrationRequest req, CancellationToken ct)
        {
            var a = await FindAsync(id, ct);
            if (a.Status is not ("Requested" or "Rejected")) throw GatewayException.Conflict("NOT_WAITING", "This application is not waiting for a decision.");
            var p = await _settings.PricingAsync(a.Provider, ct);
            var label = IntegrationProducts.Label(a.Provider);
            var fee = decimal.Round(req.SetupFee ?? p.SetupFee, 2);
            if (fee < 0 || fee > 1_000_000) throw GatewayException.BadRequest("FEE_INVALID", "The set-up fee must be zero or more.");
            a.DecidedBy = Cut(_scope.UserName, 200); a.DecidedAt = DateTime.UtcNow; a.DecisionNote = string.IsNullOrWhiteSpace(req.Note) ? null : Cut(req.Note.Trim(), 500); a.SetupFee = fee;
            if (fee > 0)
            {
                a.Status = "AwaitingPayment";
                await _db.SaveChangesAsync(ct);
                var inv = await _billing.IssueOneOffAsync(a.ClientId, "integration", $"{label} integration: set-up", fee, p.Currency, ct);
                a.InvoiceId = inv.Id;
            }
            else { a.Status = "Active"; a.ActivatedAt = DateTime.UtcNow; }
            _db.AuditLogs.Add(new AuditLog { ClientId = a.ClientId, Action = "integration.approve", Entity = "IntegrationAccess", EntityId = a.Id.ToString(), Detail = fee > 0 ? $"set-up fee {fee:0.00} {p.Currency}" : "no fee" });
            await _db.SaveChangesAsync(ct);
            await _notify.RaiseAsync(a.ClientId, "integration", "info", $"integration-approved:{a.Id}:{a.DecidedAt:yyyyMMddHHmmss}",
                fee > 0 ? $"Your {label} integration is approved: pay to switch it on" : $"Your {label} integration is on",
                fee > 0 ? $"Pay the set-up invoice ({fee:0.00} {p.Currency} plus VAT) under Billing; the integration switches on as soon as it is paid." : $"Open {label} under Integration to connect.",
                fee > 0 ? "/billing" : IntegrationProducts.Get(a.Provider).Page, ct);
            return await ToDtoAsync(a, a.ClientId, ct);
        }

        public async Task<IntegrationAccessDto> RejectAsync(Guid id, IntegrationDecisionRequest req, CancellationToken ct)
        {
            var a = await FindAsync(id, ct);
            if (a.Status is not ("Requested" or "AwaitingPayment")) throw GatewayException.Conflict("NOT_WAITING", "This application is not waiting for a decision.");
            var reason = Cut(req.Reason?.Trim(), 500);
            if (reason.Length < 3) throw GatewayException.BadRequest("REASON_REQUIRED", "Give the company a short reason.");
            await VoidInvoiceAsync(a, ct);
            a.Status = "Rejected"; a.DecidedBy = Cut(_scope.UserName, 200); a.DecidedAt = DateTime.UtcNow; a.DecisionNote = reason;
            _db.AuditLogs.Add(new AuditLog { ClientId = a.ClientId, Action = "integration.reject", Entity = "IntegrationAccess", EntityId = a.Id.ToString(), Detail = reason });
            await _db.SaveChangesAsync(ct);
            await _notify.RaiseAsync(a.ClientId, "integration", "warn", $"integration-rejected:{a.Id}:{a.DecidedAt:yyyyMMddHHmmss}", $"Your {IntegrationProducts.Label(a.Provider)} integration application was not approved", reason, IntegrationProducts.Get(a.Provider).Page, ct);
            return await ToDtoAsync(a, a.ClientId, ct);
        }

        private async Task VoidInvoiceAsync(IntegrationAccess a, CancellationToken ct)
        {
            if (a.InvoiceId == null) return;
            var inv = await _db.BillingInvoices.FirstOrDefaultAsync(i => i.Id == a.InvoiceId && i.Status == "Open", ct);
            if (inv != null) inv.Status = "Void";
            await ClearInvoiceNoticesAsync(_db, a.ClientId, a.Id, a.InvoiceId.Value, ct);
        }

        public async Task<IntegrationAccessDto> SuspendAsync(Guid clientId, IntegrationDecisionRequest req, CancellationToken ct, string provider = IntegrationProducts.Zoho)
        {
            var product = IntegrationProducts.Get(provider);
            var reason = Cut(req.Reason?.Trim(), 500);
            if (reason.Length < 3) throw GatewayException.BadRequest("REASON_REQUIRED", "Give the company a short reason.");
            var a = await _db.IntegrationAccess.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == product.Key, ct);
            if (a == null) { a = new IntegrationAccess { ClientId = clientId, Provider = product.Key, Uses = "" }; _db.IntegrationAccess.Add(a); }
            await VoidInvoiceAsync(a, ct);
            a.Status = "Suspended"; a.SuspendedAt = DateTime.UtcNow; a.DecisionNote = reason; a.DecidedBy = Cut(_scope.UserName, 200); a.DecidedAt = DateTime.UtcNow;
            // nothing more is pulled, fiscalised or passed through until it is reactivated
            var c = await _db.IntegrationConnections.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == product.ConnectionProvider, ct);
            if (c != null) c.AutoFiscalise = false;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.suspend", Entity = "IntegrationAccess", EntityId = a.Id.ToString(), Detail = reason });
            await _db.SaveChangesAsync(ct);
            await _notify.RaiseAsync(clientId, "integration", "bad", $"integration-suspended:{a.Id}:{a.SuspendedAt:yyyyMMddHHmmss}", $"The {product.Label} integration is suspended", reason, product.Page, ct);
            return await ToDtoAsync(a, clientId, ct);
        }

        public async Task<IntegrationAccessDto> ReactivateAsync(Guid clientId, CancellationToken ct, string provider = IntegrationProducts.Zoho)
        {
            var product = IntegrationProducts.Get(provider);
            var a = await _db.IntegrationAccess.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Provider == product.Key, ct) ?? throw GatewayException.NotFound("Application");
            if (a.Status != "Suspended") throw GatewayException.Conflict("NOT_SUSPENDED", "The integration is not suspended.");
            a.Status = "Active"; a.SuspendedAt = null; a.ActivatedAt ??= DateTime.UtcNow; a.DecidedBy = Cut(_scope.UserName, 200); a.DecidedAt = DateTime.UtcNow; a.DecisionNote = null;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "integration.reactivate", Entity = "IntegrationAccess", EntityId = a.Id.ToString() });
            await _db.SaveChangesAsync(ct);
            await _notify.RaiseAsync(clientId, "integration", "info", $"integration-reactivated:{a.Id}:{a.DecidedAt:yyyyMMddHHmmss}", $"The {product.Label} integration is on again", "Syncing works again.", product.Page, ct);
            return await ToDtoAsync(a, clientId, ct);
        }

        public async Task<IReadOnlyList<OwnerBridgeKeyDto>> KeysAsync(CancellationToken ct)
        {
            var keys = await _db.BridgeKeys.AsNoTracking().Where(k => k.RevokedAt == null).OrderByDescending(k => k.LastUsedAt ?? k.CreatedAt).Take(500).ToListAsync(ct);
            var pids = keys.Where(k => k.PartnerId != null).Select(k => k.PartnerId!.Value).Distinct().ToList();
            var cids = keys.Where(k => k.ClientId != null).Select(k => k.ClientId!.Value).Distinct().ToList();
            var partners = await _db.Partners.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var clients = await _db.Clients.AsNoTracking().Where(c => cids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            return keys.Select(k => k.PartnerId is { } p
                ? new OwnerBridgeKeyDto(k.Id, k.Name, k.Prefix, partners.GetValueOrDefault(p) ?? "Partner", "partner", null, k.CreatedAt, k.LastUsedAt)
                : new OwnerBridgeKeyDto(k.Id, k.Name, k.Prefix, k.ClientId is { } c ? clients.GetValueOrDefault(c) ?? "Company" : "?", "company", k.ClientId, k.CreatedAt, k.LastUsedAt)).ToList();
        }

        public async Task RevokeKeyAsync(Guid keyId, string? reason, CancellationToken ct)
        {
            var k = await _db.BridgeKeys.FirstOrDefaultAsync(x => x.Id == keyId, ct) ?? throw GatewayException.NotFound("Key");
            if (k.RevokedAt != null) return;
            k.RevokedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = k.ClientId, Action = "bridge.key.revoke", Entity = "BridgeKey", EntityId = k.Id.ToString(), Detail = $"{k.Name} ({k.Prefix}…) by the platform operator{(string.IsNullOrWhiteSpace(reason) ? "" : ": " + Cut(reason, 300))}" });
            await _db.SaveChangesAsync(ct);
            if (k.ClientId is { } clientId)
                await _notify.RaiseAsync(clientId, "integration", "warn", $"key-revoked:{k.Id}", $"Your API key \"{k.Name}\" was revoked by the platform operator", reason, "/integrations", ct);
        }
    }
}
