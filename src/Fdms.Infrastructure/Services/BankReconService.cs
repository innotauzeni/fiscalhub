using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// Bank reconciliation: bring in the bank's statement, match each line with what the books recorded, and turn what the books missed (charges, interest, deposits nobody entered) into entries.
    /// The books and the bank agree when what is left unmatched on either side explains the whole difference.
    /// </summary>
    public sealed class BankReconService : IBankReconService
    {
        private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d MMM yyyy", "dd MMM yyyy", "dd-MMM-yyyy", "d-MMM-yy", "dd/MM/yy", "yyyy/MM/dd", "yyyyMMdd" };
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IAccountingService _books;
        public BankReconService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
        }

        private async Task<LedgerAccount> BankAccountAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            RequireWholeCompany();
            await _books.EnsureChartAsync(clientId, ct);
            var acc = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Account");
            if (acc.Type != "Asset") throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose a bank account.");
            return acc;
        }

        private static StatementLineDto ToDto(BankStatementLine s) => new(s.Id, s.Date, s.Description, s.Reference, s.Amount, s.JournalLineId != null, s.JournalLineId);
        private static LedgerAccountDto Dto(LedgerAccount a) => new(a.Id, a.Code, a.Name, a.Type, a.Role, a.IsActive, a.IsSystem, a.BankAccountId, true);

        private static string Fingerprint(DateTime date, decimal amount, string description, string? reference, int occurrence)
        {
            var raw = $"{date:yyyyMMdd}|{amount:0.00}|{description.Trim().ToLowerInvariant()}|{(reference ?? "").Trim().ToLowerInvariant()}|{occurrence}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..40];
        }

        // ---- reading a statement ----------------------------------------------------------------------------------------------

        private static List<string> SplitCsv(string line, char sep)
        {
            var cells = new List<string>(); var sb = new StringBuilder(); var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; } else sb.Append(c); }
                else if (c == '"') quoted = true;
                else if (c == sep) { cells.Add(sb.ToString().Trim()); sb.Clear(); }
                else sb.Append(c);
            }
            cells.Add(sb.ToString().Trim());
            return cells;
        }

        public static bool TryDate(string s, out DateTime d) => DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

        public static bool TryAmount(string? s, out decimal v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim().ToUpperInvariant(); var neg = false;
            if (t.EndsWith("DR")) { neg = true; t = t[..^2]; } else if (t.EndsWith("CR")) t = t[..^2];
            if (t.StartsWith("(") && t.EndsWith(")")) { neg = true; t = t[1..^1]; }
            t = new string(t.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
            if (t.StartsWith("-")) { neg = !neg; t = t[1..]; }
            if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out v)) return false;
            if (neg) v = -v;
            v = Math.Round(v, 2);
            return true;
        }

        public async Task<ImportStatementResult> ImportAsync(Guid clientId, ImportStatementRequest req, CancellationToken ct)
        {
            var acc = await BankAccountAsync(clientId, req.AccountId, ct);
            var text = (req.Csv ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim('﻿', '\n', ' ');
            if (text.Length == 0) throw GatewayException.BadRequest("CSV_EMPTY", "Paste or upload the statement as CSV (date, description, amount).");
            if (text.Length > 1_500_000) throw GatewayException.BadRequest("CSV_LARGE", "That statement is too large. Import it in smaller pieces (a month at a time).");
            var rows = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (rows.Count > 5000) throw GatewayException.BadRequest("CSV_LARGE", "At most 5,000 lines at a time.");

            var first = rows[0];
            var sep = new[] { ',', ';', '\t' }.OrderByDescending(c => first.Count(x => x == c)).First();
            var header = SplitCsv(first, sep);
            var hasHeader = !TryDate(header[0], out _);
            int Col(params string[] names) => hasHeader ? header.FindIndex(h => names.Any(n => h.Contains(n, StringComparison.OrdinalIgnoreCase))) : -1;
            int cDate, cDesc, cRef, cAmount, cDebit, cCredit;
            if (hasHeader)
            {
                cDate = Col("date"); cDesc = Col("descr", "narrat", "detail", "particular", "transaction", "memo"); cRef = Col("ref", "cheque", "chq");
                cDebit = Col("debit", "withdraw", "paid out", "money out", "dr"); cCredit = Col("credit", "deposit", "paid in", "money in", "cr");
                cAmount = hasHeader ? header.FindIndex(h => h.Contains("amount", StringComparison.OrdinalIgnoreCase) && !h.Contains("balance", StringComparison.OrdinalIgnoreCase)) : -1;
                if (cDate < 0 || (cAmount < 0 && cDebit < 0 && cCredit < 0)) throw GatewayException.BadRequest("CSV_COLUMNS", "I could not find the columns. The first row should name them: Date, Description and Amount (or Debit and Credit).");
                if (cDesc < 0) cDesc = header.Count > 1 ? (cDate == 1 ? 0 : 1) : 0;
            }
            else { cDate = 0; cDesc = 1; cRef = -1; cDebit = header.Count >= 4 ? 2 : -1; cCredit = header.Count >= 4 ? 3 : -1; cAmount = header.Count >= 4 ? -1 : 2; }

            var problems = new List<string>(); var found = new List<(DateTime Date, string Desc, string? Ref, decimal Amount)>();
            for (var i = hasHeader ? 1 : 0; i < rows.Count; i++)
            {
                var c = SplitCsv(rows[i], sep);
                string? Cell(int idx) => idx >= 0 && idx < c.Count ? c[idx] : null;
                if (!TryDate(Cell(cDate) ?? "", out var date)) { problems.Add($"Line {i + 1}: the date \"{Cell(cDate)}\" was not understood."); continue; }
                decimal amount;
                if (cAmount >= 0) { if (!TryAmount(Cell(cAmount), out amount)) { problems.Add($"Line {i + 1}: the amount \"{Cell(cAmount)}\" was not understood."); continue; } }
                else
                {
                    var hasDebit = TryAmount(Cell(cDebit), out var debit); var hasCredit = TryAmount(Cell(cCredit), out var credit);
                    if (!hasDebit && !hasCredit) { problems.Add($"Line {i + 1}: no amount."); continue; }
                    amount = (hasCredit ? Math.Abs(credit) : 0) - (hasDebit ? Math.Abs(debit) : 0);
                }
                if (amount == 0) continue;
                var desc = (Cell(cDesc) ?? "").Trim(); if (desc.Length == 0) desc = "Bank line";
                found.Add((date.Date, desc.Length > 200 ? desc[..200] : desc, Cell(cRef) is { Length: > 0 } rf ? (rf.Length > 60 ? rf[..60] : rf) : null, amount));
            }

            var existing = (await _db.BankStatementLines.AsNoTracking().Where(s => s.ClientId == clientId && s.AccountId == acc.Id).Select(s => s.Fingerprint).ToListAsync(ct)).ToHashSet();
            var seen = new Dictionary<string, int>(); int imported = 0, dupes = 0;
            foreach (var f in found)
            {
                var key = $"{f.Date:yyyyMMdd}|{f.Amount}|{f.Desc.ToLowerInvariant()}|{f.Ref}";
                var n = seen[key] = seen.GetValueOrDefault(key) + 1;
                var fp = Fingerprint(f.Date, f.Amount, f.Desc, f.Ref, n);
                if (!existing.Add(fp)) { dupes++; continue; }
                _db.BankStatementLines.Add(new BankStatementLine { ClientId = clientId, AccountId = acc.Id, Date = f.Date, Description = f.Desc, Reference = f.Ref, Amount = f.Amount, Fingerprint = fp });
                imported++;
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bank.statement.import", Detail = $"{acc.Code}: {imported} lines" });
            await _db.SaveChangesAsync(ct);
            return new ImportStatementResult(imported, dupes, problems.Count, problems.Take(10).ToList());
        }

        public async Task<StatementLineDto> AddLineAsync(Guid clientId, AddStatementLineRequest req, CancellationToken ct)
        {
            var acc = await BankAccountAsync(clientId, req.AccountId, ct);
            var desc = (req.Description ?? "").Trim();
            if (desc.Length is < 2 or > 200) throw GatewayException.BadRequest("MEMO_INVALID", "Describe the line (2-200 characters).");
            if (req.Amount == 0 || req.Amount != Math.Round(req.Amount, 2)) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter an amount (positive for money in, negative for money out) with at most two decimals.");
            var date = req.Date.Date;
            var n = await _db.BankStatementLines.CountAsync(s => s.ClientId == clientId && s.AccountId == acc.Id && s.Date == date && s.Amount == req.Amount && s.Description == desc, ct) + 1;
            var line = new BankStatementLine { ClientId = clientId, AccountId = acc.Id, Date = date, Description = desc, Reference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim(), Amount = req.Amount, Fingerprint = Fingerprint(date, req.Amount, desc, req.Reference, n) };
            _db.BankStatementLines.Add(line); await _db.SaveChangesAsync(ct);
            return ToDto(line);
        }

        public async Task DeleteLineAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            RequireWholeCompany();
            var s = await _db.BankStatementLines.FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Statement line");
            if (s.JournalLineId != null) throw GatewayException.Conflict("MATCHED", "Un-match this line before deleting it.");
            _db.BankStatementLines.Remove(s); await _db.SaveChangesAsync(ct);
        }

        // ---- reconciling ------------------------------------------------------------------------------------------------------

        private static List<MatchSuggestion> Suggest(IEnumerable<BankStatementLine> statement, IEnumerable<(Guid Id, DateTime Date, decimal Amount)> ledger)
        {
            var free = ledger.ToList(); var result = new List<MatchSuggestion>();
            foreach (var s in statement.OrderBy(x => x.Date))
            {
                var best = free.Where(l => l.Amount == s.Amount && Math.Abs((l.Date - s.Date).TotalDays) <= 7).OrderBy(l => Math.Abs((l.Date - s.Date).TotalDays)).FirstOrDefault();
                if (best == default) continue;
                result.Add(new MatchSuggestion(s.Id, best.Id)); free.Remove(best);
            }
            return result;
        }

        public async Task<ReconciliationDto> ReconciliationAsync(Guid clientId, Guid accountId, DateTime asAt, decimal? statementBalance, CancellationToken ct)
        {
            var acc = await BankAccountAsync(clientId, accountId, ct);
            await _books.SyncSalesAsync(clientId, ct);
            var at = asAt.Date;
            var matchedIds = _db.BankStatementLines.Where(s => s.ClientId == clientId && s.AccountId == accountId && s.JournalLineId != null).Select(s => s.JournalLineId!.Value);

            var lines = await (from l in _db.JournalLines.AsNoTracking() join e in _db.JournalEntries.AsNoTracking() on l.EntryId equals e.Id
                               where l.ClientId == clientId && l.AccountId == accountId && l.Date <= at
                               orderby l.Date descending, e.Seq descending select new { l.Id, EntryId = e.Id, l.Date, e.Number, e.Memo, e.Reference, l.Description, l.Debit, l.Credit }).Take(1500).ToListAsync(ct);
            var matched = (await matchedIds.ToListAsync(ct)).ToHashSet();
            var ledgerBalance = (await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.AccountId == accountId && l.Date <= at).Select(l => new { l.Debit, l.Credit }).ToListAsync(ct)).Sum(x => x.Debit - x.Credit);
            var unmatchedLedger = (await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.AccountId == accountId && l.Date <= at && !matchedIds.Contains(l.Id)).Select(l => new { l.Debit, l.Credit }).ToListAsync(ct)).Sum(x => x.Debit - x.Credit);

            var statement = await _db.BankStatementLines.AsNoTracking().Where(s => s.ClientId == clientId && s.AccountId == accountId && s.Date <= at).OrderByDescending(s => s.Date).ThenByDescending(s => s.CreatedAt).Take(1500).ToListAsync(ct);
            var unmatchedStatement = await _db.BankStatementLines.AsNoTracking().Where(s => s.ClientId == clientId && s.AccountId == accountId && s.Date <= at && s.JournalLineId == null).Select(s => s.Amount).ToListAsync(ct);

            var suggestions = Suggest(statement.Where(s => s.JournalLineId == null), lines.Where(l => !matched.Contains(l.Id)).Select(l => (l.Id, l.Date, l.Debit - l.Credit)));
            var adjusted = ledgerBalance - unmatchedLedger + unmatchedStatement.Sum();
            return new ReconciliationDto(Dto(acc), at, ledgerBalance, statementBalance, unmatchedLedger, unmatchedStatement.Sum(), statementBalance == null ? null : Math.Round(statementBalance.Value - adjusted, 2),
                statement.Select(ToDto).ToList(),
                lines.Select(l => new ReconLedgerLine(l.Id, l.EntryId, l.Date, l.Number, string.IsNullOrWhiteSpace(l.Description) ? l.Memo : l.Description!, l.Reference, l.Debit - l.Credit, matched.Contains(l.Id))).ToList(), suggestions);
        }

        public async Task MatchAsync(Guid clientId, MatchRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var s = await _db.BankStatementLines.FirstOrDefaultAsync(x => x.Id == req.StatementLineId && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Statement line");
            var l = await _db.JournalLines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == req.JournalLineId && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Ledger line");
            if (l.AccountId != s.AccountId) throw GatewayException.BadRequest("ACCOUNT_MISMATCH", "These belong to different bank accounts.");
            if (s.JournalLineId != null) throw GatewayException.Conflict("ALREADY_MATCHED", "That statement line is already matched.");
            if (await _db.BankStatementLines.AnyAsync(x => x.JournalLineId == l.Id, ct)) throw GatewayException.Conflict("ALREADY_MATCHED", "That ledger line is already matched with another statement line.");
            if (l.Debit - l.Credit != s.Amount) throw GatewayException.Conflict("AMOUNT_MISMATCH", "The amounts differ. Only lines with exactly the same amount can be matched; record the difference as its own entry.");
            s.JournalLineId = l.Id; s.MatchedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task UnmatchAsync(Guid clientId, Guid statementLineId, CancellationToken ct)
        {
            RequireWholeCompany();
            var s = await _db.BankStatementLines.FirstOrDefaultAsync(x => x.Id == statementLineId && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Statement line");
            s.JournalLineId = null; s.MatchedAt = null;
            await _db.SaveChangesAsync(ct);
        }

        public async Task<int> AutoMatchAsync(Guid clientId, Guid accountId, CancellationToken ct)
        {
            var acc = await BankAccountAsync(clientId, accountId, ct);
            await _books.SyncSalesAsync(clientId, ct);
            var matchedIds = _db.BankStatementLines.Where(s => s.ClientId == clientId && s.AccountId == acc.Id && s.JournalLineId != null).Select(s => s.JournalLineId!.Value);
            var ledger = await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId && l.AccountId == acc.Id && !matchedIds.Contains(l.Id)).Select(l => new { l.Id, l.Date, l.Debit, l.Credit }).ToListAsync(ct);
            var statement = await _db.BankStatementLines.Where(s => s.ClientId == clientId && s.AccountId == acc.Id && s.JournalLineId == null).ToListAsync(ct);
            var pairs = Suggest(statement, ledger.Select(l => (l.Id, l.Date, l.Debit - l.Credit)));
            foreach (var p in pairs) { var s = statement.First(x => x.Id == p.StatementLineId); s.JournalLineId = p.JournalLineId; s.MatchedAt = DateTime.UtcNow; }
            await _db.SaveChangesAsync(ct);
            return pairs.Count;
        }

        public async Task<JournalDto> CreateEntryAsync(Guid clientId, CreateFromStatementRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            var s = await _db.BankStatementLines.FirstOrDefaultAsync(x => x.Id == req.StatementLineId && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Statement line");
            if (s.JournalLineId != null) throw GatewayException.Conflict("ALREADY_MATCHED", "That statement line is already matched.");
            var acc = await _db.LedgerAccounts.AsNoTracking().FirstAsync(a => a.Id == s.AccountId, ct);
            var currency = acc.BankAccountId is { } bid ? await _db.BankAccounts.AsNoTracking().Where(b => b.Id == bid).Select(b => b.CurrencyCode).FirstOrDefaultAsync(ct) : null;
            var memo = string.IsNullOrWhiteSpace(req.Memo) ? s.Description : req.Memo.Trim();
            var entry = await _books.CashbookEntryAsync(clientId, new CashbookEntryRequest(s.Date, s.AccountId, s.Amount > 0 ? "receipt" : "payment", req.CounterAccountId, Math.Abs(s.Amount), currency ?? "USD", memo, s.Reference), ct);
            var bankLine = entry.Lines.First(l => l.AccountId == s.AccountId);
            s.JournalLineId = bankLine.Id; s.MatchedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return entry;
        }
    }
}
