using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Maker-checker approvals. A company sets up its approval matrix (action × amount × currency → levels of roles); an action that matches a rule is not carried out but
    /// held as a request; each level is approved by someone holding that level's role; on the last approval the original request is carried out exactly as asked.
    /// Segregation of duties: the person who asked can never approve, and one person approves at most one level of a request.
    /// </summary>
    public sealed class ApprovalService : IApprovalService
    {
        public static readonly IReadOnlyList<ApprovalActionDto> Actions = new[]
        {
            new ApprovalActionDto("credit_note", "Credit note", "Reversing a fiscalised invoice with a credit note.", "the invoice total"),
            new ApprovalActionDto("debit_note", "Debit note", "Adding charges to a fiscalised invoice.", "the extra charges"),
            new ApprovalActionDto("receipt_cancel", "Cancel a held receipt", "Cancelling a receipt before it is sent to ZIMRA.", "the receipt total"),
            new ApprovalActionDto("bill", "Supplier bill", "Entering a supplier's invoice into the books.", "the bill total"),
            new ApprovalActionDto("bill_payment", "Bill payment", "Paying a supplier bill.", "the amount paid"),
            new ApprovalActionDto("journal", "Manual journal", "Posting a manual journal entry.", "the total debits"),
        };
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly INotificationService _notify;
        private readonly IApprovalExecutor _executor;
        private readonly DeviceLocks _locks;
        private readonly ILogger<ApprovalService> _log;

        public ApprovalService(FdmsDbContext db, ICallerScope scope, INotificationService notify, IApprovalExecutor executor, DeviceLocks locks, ILogger<ApprovalService> log)
        { _db = db; _scope = scope; _notify = notify; _executor = executor; _locks = locks; _log = log; }

        private static string Label(string action) => Actions.FirstOrDefault(a => a.Key == action)?.Label ?? action;
        private static List<string> Levels(string json) { try { return JsonSerializer.Deserialize<List<string>>(json, J) ?? new(); } catch (JsonException) { return new(); } }

        private string Me => _scope.UserId ?? throw new GatewayException(403, "USER_ONLY", "Only a signed-in person can do this.");

        private async Task<HashSet<string>> RolesOfAsync(string userId, CancellationToken ct) =>
            (await (from ur in _db.UserRoles.AsNoTracking() join r in _db.Roles.AsNoTracking() on ur.RoleId equals r.Id where ur.UserId == userId select r.Name!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ---- the gate -----------------------------------------------------------------------------------------------------

        public async Task<ApprovalRequiredDto?> GateAsync(Guid clientId, string action, decimal amount, string? currency, string summary, string? entityId, object payload, CancellationToken ct)
        {
            var cur = currency?.Trim().ToUpperInvariant();
            var rules = await _db.ApprovalRules.AsNoTracking().Where(r => r.ClientId == clientId && r.Action == action && r.IsActive && r.MinAmount <= amount && (r.Currency == null || r.Currency == cur)).ToListAsync(ct);
            // the most specific rule: the highest starting amount, a currency rule before an any-currency rule
            var rule = rules.OrderByDescending(r => r.MinAmount).ThenByDescending(r => r.Currency != null).FirstOrDefault();
            if (rule == null || Levels(rule.LevelsJson).Count == 0) return null;
            if (_scope.ActorType != "user" || _scope.UserId == null)
                throw new GatewayException(403, "APPROVAL_REQUIRED", $"{Label(action)} of this size needs approval, which an API token cannot ask for. A person must do it in the console.");

            var req = new ApprovalRequest
            {
                ClientId = clientId, Action = action, RuleId = rule.Id, LevelsJson = rule.LevelsJson, Summary = summary.Length > 300 ? summary[..300] : summary, Amount = amount, Currency = cur, EntityId = entityId,
                PayloadJson = JsonSerializer.Serialize(payload, J), RequestedById = _scope.UserId, RequestedByName = _scope.UserName ?? "someone",
            };
            _db.ApprovalRequests.Add(req);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "approval.request", Entity = "ApprovalRequest", EntityId = req.Id.ToString(), Detail = $"{Label(action)}: {req.Summary} ({amount:0.00} {cur})" });
            await _db.SaveChangesAsync(ct);
            await NotifyLevelAsync(req, ct);
            var dto = await DtoAsync(req, ct);
            return new ApprovalRequiredDto(true, $"{Label(action)} sent for approval: waiting for {dto.WaitingForRole}.", dto);
        }

        private async Task NotifyLevelAsync(ApprovalRequest req, CancellationToken ct)
        {
            var levels = Levels(req.LevelsJson);
            if (req.CurrentLevel >= levels.Count) return;
            try { await _notify.RaiseAsync(req.ClientId, "approval", "warn", $"approval:{req.Id}:{req.CurrentLevel}", $"Approval needed: {Label(req.Action)}", $"{req.Summary} ({req.Amount:0.00} {req.Currency}). Waiting for {levels[req.CurrentLevel]}; asked by {req.RequestedByName}.", "/approvals", ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not notify approvers of {Request}", req.Id); }
        }

        // ---- the matrix ---------------------------------------------------------------------------------------------------

        private static ApprovalRuleDto RuleDto(ApprovalRule r) => new(r.Id, r.Action, Label(r.Action), r.MinAmount, r.Currency, Levels(r.LevelsJson), r.IsActive);

        public async Task<ApprovalMatrixDto> MatrixAsync(Guid clientId, CancellationToken ct)
        {
            var rules = await _db.ApprovalRules.AsNoTracking().Where(r => r.ClientId == clientId).ToListAsync(ct);
            var roles = await _db.Roles.AsNoTracking().Where(r => r.Level == "client").OrderBy(r => r.Name).Select(r => r.Name!).ToListAsync(ct);
            return new ApprovalMatrixDto(Actions.Select(a => new ApprovalMatrixRowDto(a, rules.Where(r => r.Action == a.Key).OrderBy(r => r.Currency ?? "").ThenBy(r => r.MinAmount).Select(RuleDto).ToList())).ToList(), roles);
        }

        public async Task<ApprovalRuleDto> SaveRuleAsync(Guid clientId, SaveApprovalRuleRequest req, CancellationToken ct)
        {
            if (Actions.All(a => a.Key != req.Action)) throw GatewayException.BadRequest("ACTION_INVALID", "Choose one of the actions in the matrix.");
            if (req.MinAmount is < 0 or > 1_000_000_000) throw GatewayException.BadRequest("AMOUNT_INVALID", "The starting amount is 0 or more.");
            var cur = string.IsNullOrWhiteSpace(req.Currency) ? null : req.Currency.Trim().ToUpperInvariant();
            if (cur != null && (cur.Length != 3 || !cur.All(char.IsAsciiLetterUpper))) throw GatewayException.BadRequest("CURRENCY_INVALID", "Use a 3-letter currency code, or leave it empty for every currency.");
            var levels = (req.Levels ?? Array.Empty<string>()).Select(l => (l ?? "").Trim()).Where(l => l.Length > 0).ToList();
            if (levels.Count is 0 or > 5) throw GatewayException.BadRequest("LEVELS_INVALID", "Give 1 to 5 approval levels.");
            var known = (await _db.Roles.AsNoTracking().Where(r => r.Level == "client").Select(r => r.Name!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = levels.FirstOrDefault(l => !known.Contains(l));
            if (unknown != null) throw GatewayException.BadRequest("ROLE_INVALID", $"\"{unknown}\" is not a role of your company.");

            ApprovalRule rule;
            if (req.Id is { } id)
            {
                rule = await _db.ApprovalRules.FirstOrDefaultAsync(r => r.Id == id && r.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Rule");
                rule.Action = req.Action;
            }
            else _db.ApprovalRules.Add(rule = new ApprovalRule { ClientId = clientId, Action = req.Action });
            if (await _db.ApprovalRules.AnyAsync(r => r.ClientId == clientId && r.Id != rule.Id && r.Action == req.Action && r.MinAmount == req.MinAmount && r.Currency == cur, ct))
                throw GatewayException.Conflict("RULE_DUPLICATE", "There is already a rule for this action from this amount and currency. Edit that one.");
            rule.MinAmount = req.MinAmount; rule.Currency = cur; rule.LevelsJson = JsonSerializer.Serialize(levels, J); rule.IsActive = req.IsActive; rule.UpdatedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "approval.rule", Entity = "ApprovalRule", EntityId = rule.Id.ToString(), Detail = $"{Label(req.Action)} from {req.MinAmount:0.00} {cur ?? "any currency"}: {string.Join(" → ", levels)}{(req.IsActive ? "" : " (off)")}" });
            await _db.SaveChangesAsync(ct);
            return RuleDto(rule);
        }

        public async Task DeleteRuleAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var rule = await _db.ApprovalRules.FirstOrDefaultAsync(r => r.Id == id && r.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Rule");
            _db.ApprovalRules.Remove(rule);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "approval.rule-delete", Entity = "ApprovalRule", EntityId = id.ToString(), Detail = Label(rule.Action) });
            await _db.SaveChangesAsync(ct);
        }

        // ---- requests -----------------------------------------------------------------------------------------------------

        private async Task<ApprovalRequestDto> DtoAsync(ApprovalRequest r, CancellationToken ct, HashSet<string>? myRoles = null, List<ApprovalDecision>? decisions = null)
        {
            var levels = Levels(r.LevelsJson);
            decisions ??= await _db.ApprovalDecisions.AsNoTracking().Where(d => d.RequestId == r.Id).OrderBy(d => d.At).ToListAsync(ct);
            var me = _scope.UserId;
            myRoles ??= me == null ? new HashSet<string>() : await RolesOfAsync(me, ct);
            var waiting = r.Status == "Pending" && r.CurrentLevel < levels.Count ? levels[r.CurrentLevel] : null;
            var canDecide = waiting != null && me != null && me != r.RequestedById && myRoles.Contains(waiting) && decisions.All(d => d.UserId != me);
            return new ApprovalRequestDto(r.Id, r.Action, Label(r.Action), r.Status, r.Summary, r.Amount, r.Currency, r.EntityId, levels, r.CurrentLevel, waiting, r.RequestedByName, r.CreatedAt, r.DecidedAt, r.Error,
                canDecide, r.Status == "Pending" && me == r.RequestedById,
                decisions.Select(d => new ApprovalDecisionDto(d.Level, d.Role, d.UserName, d.Decision, d.Note, d.At)).ToList());
        }

        public async Task<ApprovalListDto> ListAsync(Guid clientId, string scope, string? status, CancellationToken ct)
        {
            var me = Me;
            var myRoles = await RolesOfAsync(me, ct);
            var q = _db.ApprovalRequests.AsNoTracking().Where(r => r.ClientId == clientId);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
            if (scope == "requested") q = q.Where(r => r.RequestedById == me);
            var rows = await q.OrderByDescending(r => r.CreatedAt).Take(scope == "mine" ? 500 : 200).ToListAsync(ct);
            var ids = rows.Select(r => r.Id).ToList();
            var decisions = (await _db.ApprovalDecisions.AsNoTracking().Where(d => ids.Contains(d.RequestId)).OrderBy(d => d.At).ToListAsync(ct)).GroupBy(d => d.RequestId).ToDictionary(g => g.Key, g => g.ToList());
            var items = new List<ApprovalRequestDto>();
            foreach (var r in rows) items.Add(await DtoAsync(r, ct, myRoles, decisions.GetValueOrDefault(r.Id) ?? new()));
            var waitingForMe = items.Count(i => i.CanDecide);
            if (scope == "mine") items = items.Where(i => i.CanDecide).ToList();
            return new ApprovalListDto(items.Take(200).ToList(), waitingForMe);
        }

        public async Task<ApprovalRequestDto> GetAsync(Guid clientId, Guid id, CancellationToken ct) =>
            await DtoAsync(await _db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Approval"), ct);

        public async Task<ApprovalRequestDto> ApproveAsync(Guid clientId, Guid id, string? note, CancellationToken ct) => await DecideAsync(clientId, id, true, note, ct);
        public async Task<ApprovalRequestDto> RejectAsync(Guid clientId, Guid id, string? note, CancellationToken ct) => await DecideAsync(clientId, id, false, note, ct);

        private async Task<ApprovalRequestDto> DecideAsync(Guid clientId, Guid id, bool approve, string? note, CancellationToken ct)
        {
            var me = Me;
            if ((note?.Length ?? 0) > 500) throw GatewayException.BadRequest("TOO_LONG", "The note can be at most 500 characters.");
            if (!approve && string.IsNullOrWhiteSpace(note)) throw GatewayException.BadRequest("REASON_REQUIRED", "Say why it is rejected.");
            // one decision at a time per request: two approvers pressing at once can never carry the action out twice
            await using var _ = await _locks.AcquireAsync(id, ct);
            var r = await _db.ApprovalRequests.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Approval");
            if (r.Status != "Pending") throw GatewayException.Conflict("APPROVAL_CLOSED", $"This request is already {r.Status.ToLowerInvariant()}.");
            var levels = Levels(r.LevelsJson);
            var role = levels[r.CurrentLevel];
            if (r.RequestedById == me) throw new GatewayException(403, "OWN_REQUEST", "You cannot approve or reject your own request. Someone else must decide.");
            if (!(await RolesOfAsync(me, ct)).Contains(role)) throw new GatewayException(403, "NOT_YOUR_LEVEL", $"This level must be decided by a {role}.");
            if (await _db.ApprovalDecisions.AnyAsync(d => d.RequestId == id && d.UserId == me, ct)) throw new GatewayException(403, "ALREADY_DECIDED", "You already decided an earlier level of this request; another person must decide this one.");

            _db.ApprovalDecisions.Add(new ApprovalDecision { RequestId = id, Level = r.CurrentLevel, Role = role, UserId = me, UserName = _scope.UserName ?? "someone", Decision = approve ? "Approved" : "Rejected", Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = approve ? "approval.approve" : "approval.reject", Entity = "ApprovalRequest", EntityId = id.ToString(), Detail = $"{Label(r.Action)}, level {r.CurrentLevel + 1} ({role}): {r.Summary}{(string.IsNullOrWhiteSpace(note) ? "" : " - " + note.Trim())}" });
            if (!approve) { r.Status = "Rejected"; r.DecidedAt = DateTime.UtcNow; }
            else if (r.CurrentLevel + 1 < levels.Count) r.CurrentLevel++;
            else r.Status = "Approved";
            await _db.SaveChangesAsync(ct);

            if (r.Status == "Approved")
            {
                // the last level: carry the original request out now
                try
                {
                    var result = await _executor.ExecuteAsync(clientId, r.Action, r.EntityId, r.PayloadJson, ct);
                    r.ResultJson = result == null ? null : JsonSerializer.Serialize(result, J);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _db.ChangeTracker.Clear();
                    r = await _db.ApprovalRequests.FirstAsync(x => x.Id == id, ct);
                    r.Status = "Failed";
                    r.Error = ex is GatewayException g ? g.Message : "The action could not be carried out: " + ex.Message;
                    _log.LogWarning(ex, "Approved request {Request} failed when carried out", id);
                }
                r.DecidedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            if (r.Status == "Pending") await NotifyLevelAsync(r, ct);
            else
            {
                try { await _notify.RaiseAsync(clientId, "approval", r.Status == "Approved" ? "info" : "bad", $"approval:{r.Id}:done", $"{Label(r.Action)} {r.Status.ToLowerInvariant()}", $"{r.Summary}{(r.Error != null ? ": " + r.Error : "")}", "/approvals", ct); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not notify about {Request}", id); }
            }
            return await DtoAsync(r, ct);
        }

        public async Task<ApprovalRequestDto> CancelAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            var me = Me;
            await using var _ = await _locks.AcquireAsync(id, ct);
            var r = await _db.ApprovalRequests.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Approval");
            if (r.Status != "Pending") throw GatewayException.Conflict("APPROVAL_CLOSED", $"This request is already {r.Status.ToLowerInvariant()}.");
            if (r.RequestedById != me) throw new GatewayException(403, "NOT_YOURS", "Only the person who asked can withdraw the request.");
            r.Status = "Cancelled"; r.DecidedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "approval.cancel", Entity = "ApprovalRequest", EntityId = id.ToString(), Detail = r.Summary });
            await _db.SaveChangesAsync(ct);
            return await DtoAsync(r, ct);
        }

        // ---- amounts for the gate ------------------------------------------------------------------------------------------

        public async Task<(decimal Total, string Currency, string Number)> ReceiptAsync(Guid clientId, Guid receiptId, CancellationToken ct)
        {
            var r = await _db.Receipts.AsNoTracking().Where(x => x.Id == receiptId && x.ClientId == clientId).Select(x => new { x.Total, x.Currency, x.InvoiceNo }).FirstOrDefaultAsync(ct)
                ?? throw GatewayException.NotFound("Receipt");
            return (Math.Abs(r.Total), r.Currency, r.InvoiceNo);
        }

        public async Task<(string Currency, string Number, string Supplier)> BillAsync(Guid clientId, Guid billId, CancellationToken ct)
        {
            var b = await (from bill in _db.Bills.AsNoTracking() join s in _db.Suppliers.AsNoTracking() on bill.SupplierId equals s.Id where bill.Id == billId && bill.ClientId == clientId select new { bill.Currency, bill.Number, s.Name }).FirstOrDefaultAsync(ct)
                ?? throw GatewayException.NotFound("Bill");
            return (b.Currency, b.Number, b.Name);
        }
    }
}
