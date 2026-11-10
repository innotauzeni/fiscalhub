using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Spend money, receive money and bank transfers. A document can be kept as a draft and posted later (alone or in a batch under Post to ledger); posting writes one journal
    /// (bank against the lines, VAT split out, cost centres kept); voiding a posted document reverses its journal. Drafts can be changed or deleted, posted ones only voided.
    /// </summary>
    public sealed class CashDocumentService : ICashDocumentService
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly string[] Kinds = { "payment", "receipt", "transfer" };
        private readonly FdmsDbContext _db; private readonly ICallerScope _scope; private readonly IAccountingService _books;
        public CashDocumentService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private static string Prefix(string kind) => kind switch { "receipt" => "RCT-", "transfer" => "TRF-", _ => "PAY-" };
        private static List<CashLineInput> LinesOf(CashDocument d) { try { return JsonSerializer.Deserialize<List<CashLineInput>>(d.LinesJson, Json) ?? new(); } catch (JsonException) { return new(); } }
        private static decimal VatOf(CashLineInput l) => l.VatRate <= 0 ? 0 : decimal.Round(l.Amount * l.VatRate / (100 + l.VatRate), 2, MidpointRounding.AwayFromZero);
        private static string? Clean(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

        // ---- reading ----

        private async Task<List<CashDocumentDto>> MapAsync(Guid clientId, List<CashDocument> docs, CancellationToken ct)
        {
            var accountIds = docs.SelectMany(d => LinesOf(d).Select(l => l.AccountId).Append(d.AccountId).Concat(d.ToAccountId is { } t ? new[] { t } : Array.Empty<Guid>())).Distinct().ToList();
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            var centres = await _db.CostCentres.AsNoTracking().Where(c => c.ClientId == clientId).ToDictionaryAsync(c => c.Id, c => c.Code + " " + c.Name, ct);
            var entryIds = docs.Where(d => d.JournalEntryId != null).Select(d => d.JournalEntryId!.Value).ToList();
            var numbers = await _db.JournalEntries.AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
            string Name(Guid id) => accounts.TryGetValue(id, out var a) ? a.Code + " " + a.Name : "";
            return docs.Select(d => new CashDocumentDto(d.Id, d.Kind, d.Number, d.Date, d.Currency, d.AccountId, Name(d.AccountId), d.ToAccountId, d.ToAccountId is { } t ? Name(t) : null,
                d.Contact, d.Reference, d.Memo,
                LinesOf(d).Select(l => new CashLineView(l.AccountId, accounts.GetValueOrDefault(l.AccountId)?.Code ?? "", accounts.GetValueOrDefault(l.AccountId)?.Name ?? "", l.Description, l.Amount, l.VatRate, VatOf(l),
                    l.CostCentreId, l.CostCentreId is { } c ? centres.GetValueOrDefault(c) : null)).ToList(),
                d.Subtotal, d.Vat, d.Total, d.Fee, d.Status, d.JournalEntryId, d.JournalEntryId is { } j ? numbers.GetValueOrDefault(j) : null, d.CreatedBy, d.CreatedAt, d.PostedBy, d.PostedAt)).ToList();
        }

        public async Task<CashDocumentPage> ListAsync(Guid clientId, CashDocumentFilter f, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var q = _db.CashDocuments.AsNoTracking().Where(d => d.ClientId == clientId);
            if (!string.IsNullOrWhiteSpace(f.Kind)) q = q.Where(d => d.Kind == f.Kind);
            if (!string.IsNullOrWhiteSpace(f.Status)) q = q.Where(d => d.Status == f.Status);
            if (f.AccountId is { } acc) q = q.Where(d => d.AccountId == acc || d.ToAccountId == acc);
            var byCreated = string.Equals(f.DateBy, "created", StringComparison.OrdinalIgnoreCase);
            // created dates are kept in UTC; the company's day is Zimbabwe time (UTC+2)
            if (f.From is { } from) q = byCreated ? q.Where(d => d.CreatedAt >= from.Date.AddHours(-2)) : q.Where(d => d.Date >= from.Date);
            if (f.To is { } to) q = byCreated ? q.Where(d => d.CreatedAt < to.Date.AddDays(1).AddHours(-2)) : q.Where(d => d.Date <= to.Date);
            if (!string.IsNullOrWhiteSpace(f.Q)) { var s = f.Q.Trim(); q = q.Where(d => d.Number.Contains(s) || (d.Reference != null && d.Reference.Contains(s)) || (d.Contact != null && d.Contact.Contains(s)) || (d.Memo != null && d.Memo.Contains(s))); }
            var rows = await q.OrderByDescending(d => d.Date).ThenByDescending(d => d.Seq).Take(500).ToListAsync(ct);
            var totals = rows.Where(d => d.Status != "Void").GroupBy(d => d.Currency).Select(g => new AmountDto(g.Key, g.Sum(x => x.Total))).OrderBy(x => x.Currency).ToList();
            return new CashDocumentPage(await MapAsync(clientId, rows, ct), totals, rows.Count);
        }

        private async Task<CashDocument> LoadAsync(Guid clientId, Guid id, CancellationToken ct) =>
            await _db.CashDocuments.FirstOrDefaultAsync(d => d.Id == id && d.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Document");

        public async Task<CashDocumentDto> GetAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            return (await MapAsync(clientId, new List<CashDocument> { await LoadAsync(clientId, id, ct) }, ct))[0];
        }

        // ---- saving ----

        private async Task<LedgerAccount> CashAccountAsync(Guid clientId, Guid id, string what, CancellationToken ct)
        {
            var a = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct);
            if (a == null || a.Type != "Asset" || a.IsGroup || ChartCodes.SubTypeOf(a) != "cash") throw GatewayException.BadRequest("ACCOUNT_INVALID", $"Choose the cash or bank account {what}.");
            if (!a.IsActive) throw GatewayException.BadRequest("ACCOUNT_INACTIVE", $"{a.Code} {a.Name} is switched off.");
            return a;
        }

        public async Task<CashDocumentDto> SaveAsync(Guid clientId, SaveCashDocumentRequest req, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var kind = (req.Kind ?? "").Trim().ToLowerInvariant();
            if (!Kinds.Contains(kind)) throw GatewayException.BadRequest("KIND_INVALID", "Choose a payment, a receipt or a transfer.");
            var cur = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == cur && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            if (req.Date.Year < 2000 || req.Date > DateTime.UtcNow.AddDays(366)) throw GatewayException.BadRequest("DATE_INVALID", "That date is not valid.");
            var bank = await CashAccountAsync(clientId, req.AccountId, kind == "receipt" ? "the money came into" : "the money left", ct);

            List<CashLineInput> lines = new(); decimal subtotal, vat, total, fee = 0;
            Guid? to = null;
            if (kind == "transfer")
            {
                if (req.ToAccountId is not { } toId || toId == req.AccountId) throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose a different account to move the money to.");
                to = (await CashAccountAsync(clientId, toId, "the money goes to", ct)).Id;
                if (req.Amount <= 0 || req.Amount != decimal.Round(req.Amount, 2)) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter the amount moved (two decimals at most).");
                if (req.Fee < 0 || req.Fee != decimal.Round(req.Fee, 2)) throw GatewayException.BadRequest("AMOUNT_INVALID", "The bank charge cannot be negative.");
                subtotal = total = req.Amount; vat = 0; fee = req.Fee;
            }
            else
            {
                lines = (req.Lines ?? Array.Empty<CashLineInput>()).Where(l => l.Amount != 0).Select(l => l with { Description = Clean(l.Description, 200) }).ToList();
                if (lines.Count is < 1 or > 50) throw GatewayException.BadRequest("LINES_INVALID", "Add at least one line with an amount.");
                if (lines.Any(l => l.Amount < 0 || l.Amount != decimal.Round(l.Amount, 2))) throw GatewayException.BadRequest("AMOUNT_INVALID", "Line amounts are above zero with at most two decimals.");
                if (lines.Any(l => l.VatRate is < 0 or > 50)) throw GatewayException.BadRequest("VAT_INVALID", "The VAT rate is between 0 and 50%.");
                var ids = lines.Select(l => l.AccountId).Distinct().ToList();
                var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && ids.Contains(a.Id)).ToListAsync(ct);
                if (accounts.Count != ids.Count || accounts.Any(a => a.IsGroup || !a.IsActive)) throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose accounts in use from your chart (not headings).");
                if (ids.Contains(bank.Id)) throw GatewayException.BadRequest("ACCOUNT_INVALID", "A line cannot use the same account the money moves through.");
                await _books.CheckCentresAsync(clientId, lines.Select(l => l.CostCentreId), ct);
                total = lines.Sum(l => l.Amount); vat = lines.Sum(VatOf); subtotal = total - vat;
            }

            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            CashDocument d;
            if (id == null)
            {
                var seq = (await _db.CashDocuments.Where(x => x.ClientId == clientId && x.Kind == kind).MaxAsync(x => (int?)x.Seq, ct) ?? 0) + 1;
                d = new CashDocument { ClientId = clientId, Kind = kind, Seq = seq, Number = Prefix(kind) + seq.ToString("D6"), CreatedBy = _scope.UserName };
                _db.CashDocuments.Add(d);
            }
            else
            {
                d = await LoadAsync(clientId, id.Value, ct);
                if (d.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", "Only a draft can be changed. Void the posted one and enter it again.");
                if (d.Kind != kind) throw GatewayException.BadRequest("KIND_INVALID", "A document keeps its kind.");
            }
            d.Date = req.Date.Date; d.Currency = cur; d.AccountId = bank.Id; d.ToAccountId = to; d.Contact = Clean(req.Contact, 120); d.Reference = Clean(req.Reference, 50); d.Memo = Clean(req.Memo, 200);
            d.LinesJson = JsonSerializer.Serialize(lines, Json); d.Subtotal = subtotal; d.Vat = vat; d.Total = total; d.Fee = fee;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.cashdoc.save", Detail = $"{d.Number} {total:N2} {cur}" });
            for (var attempt = 0; ; attempt++)
            {
                try { await _db.SaveChangesAsync(ct); break; }
                catch (DbUpdateException) when (attempt < 3 && id == null)   // two people took the same number at once: take the next
                {
                    d.Seq = (await _db.CashDocuments.AsNoTracking().Where(x => x.ClientId == clientId && x.Kind == kind).MaxAsync(x => (int?)x.Seq, ct) ?? 0) + 1 + attempt;
                    d.Number = Prefix(kind) + d.Seq.ToString("D6");
                }
            }
            if (req.Post) return await PostAsync(clientId, d.Id, ct);
            return await GetAsync(clientId, d.Id, ct);
        }

        // ---- posting and voiding ----

        public async Task<CashDocumentDto> PostAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var d = await LoadAsync(clientId, id, ct);
            if (d.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", $"{d.Number} is already {d.Status.ToLowerInvariant()}.");
            var lines = new List<JournalLineInput>();
            var memo = string.Join(" · ", new[] { d.Number, d.Contact, d.Memo }.Where(x => !string.IsNullOrWhiteSpace(x)));
            switch (d.Kind)
            {
                case "transfer":
                    lines.Add(new JournalLineInput(d.ToAccountId!.Value, d.Total, 0, d.Memo));
                    lines.Add(new JournalLineInput(d.AccountId, 0, d.Total + d.Fee, d.Memo));
                    if (d.Fee > 0)
                    {
                        var charges = await ChartCodes.ByRoleAsync(_db, clientId, "bank_charges", "5150", ct) ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The bank charges account is missing from your chart.");
                        lines.Add(new JournalLineInput(charges.Id, d.Fee, 0, "Bank charge"));
                    }
                    break;
                case "payment":
                case "receipt":
                {
                    var pay = d.Kind == "payment";
                    LedgerAccount? vatAccount = null;
                    if (d.Vat > 0)
                        vatAccount = await ChartCodes.ByRoleAsync(_db, clientId, pay ? "vat_input" : "vat_output", pay ? "1310" : "2110", ct)
                                     ?? throw GatewayException.Conflict("ACCOUNT_MISSING", "The VAT account is missing from your chart.");
                    foreach (var l in LinesOf(d))
                    {
                        var net = l.Amount - VatOf(l);
                        lines.Add(pay ? new JournalLineInput(l.AccountId, net, 0, l.Description, l.CostCentreId) : new JournalLineInput(l.AccountId, 0, net, l.Description, l.CostCentreId));
                    }
                    if (vatAccount != null) lines.Add(pay ? new JournalLineInput(vatAccount.Id, d.Vat, 0, "VAT") : new JournalLineInput(vatAccount.Id, 0, d.Vat, "VAT"));
                    lines.Add(pay ? new JournalLineInput(d.AccountId, 0, d.Total, d.Contact) : new JournalLineInput(d.AccountId, d.Total, 0, d.Contact));
                    break;
                }
            }
            var entry = await _books.PostLinesAsync(clientId, d.Date, d.Currency, memo.Length > 200 ? memo[..200] : memo, d.Reference ?? d.Number, d.Kind, d.Id, lines, ct);
            d.Status = "Posted"; d.JournalEntryId = entry.Id; d.PostedAt = DateTime.UtcNow; d.PostedBy = _scope.UserName;
            await _db.SaveChangesAsync(ct);
            return await GetAsync(clientId, d.Id, ct);
        }

        public async Task<PostBatchResult> PostBatchAsync(Guid clientId, IReadOnlyList<Guid> ids, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var list = (ids ?? Array.Empty<Guid>()).Distinct().Take(200).ToList();
            var posted = 0; var problems = new List<string>();
            foreach (var id in list)
            {
                try { await PostAsync(clientId, id, ct); posted++; }
                catch (GatewayException ex)
                {
                    _db.ChangeTracker.Clear();
                    var number = await _db.CashDocuments.AsNoTracking().Where(d => d.Id == id && d.ClientId == clientId).Select(d => d.Number).FirstOrDefaultAsync(ct) ?? id.ToString();
                    problems.Add($"{number}: {ex.Message}");
                }
            }
            return new PostBatchResult(posted, problems);
        }

        public async Task<CashDocumentDto> VoidAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var d = await LoadAsync(clientId, id, ct);
            if (d.Status != "Posted") throw GatewayException.Conflict("NOT_POSTED", d.Status == "Draft" ? "A draft is not in the books: delete it instead." : "It is void already.");
            await _books.ReverseAtAsync(clientId, d.JournalEntryId!.Value, null, ct);
            d.Status = "Void"; d.VoidedAt = DateTime.UtcNow; d.VoidedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.cashdoc.void", Detail = d.Number });
            await _db.SaveChangesAsync(ct);
            return await GetAsync(clientId, d.Id, ct);
        }

        public async Task DeleteAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            Guard.WholeCompany(_scope);
            var d = await LoadAsync(clientId, id, ct);
            if (d.Status != "Draft") throw GatewayException.Conflict("NOT_DRAFT", "Only a draft can be deleted. Void a posted document instead.");
            _db.CashDocuments.Remove(d);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.cashdoc.delete", Detail = d.Number });
            await _db.SaveChangesAsync(ct);
        }
    }
}
