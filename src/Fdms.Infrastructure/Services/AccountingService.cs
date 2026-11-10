using System.Text.Json;
using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Domain.Enums;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>
    /// A company's books: a chart of accounts and double-entry journals. Confirmed fiscal receipts post themselves (sales, VAT, and where the money went); everything else is entered as a manual
    /// journal or a cashbook entry. A posted entry is never edited, only reversed, so the trail stays honest. Books are kept per currency: nothing is converted behind anyone's back.
    /// </summary>
    public sealed partial class AccountingService : IAccountingService
    {
        public static readonly string[] Types = { "Asset", "Liability", "Equity", "Income", "Expense" };
        /// <summary>2 to 20 letters or digits, with - . or / between them (5125, 1-100-01, 4.10).</summary>
        public static readonly Regex CodeRx = new("^[A-Za-z0-9](?:[A-Za-z0-9./-]{0,18}[A-Za-z0-9])$", RegexOptions.Compiled);

        /// <summary>
        /// The standard chart every company starts with (codes in the 1000s are assets, 2000s liabilities, 3000s equity, 4000s income, 5000s expenses). Role: what the system posts to it;
        /// SubType: where it appears in the statements (null: the usual one for its type).
        /// </summary>
        public static readonly (string Code, string Name, string Type, string? Role, string? SubType)[] StandardChart =
        {
            ("1010", "Cash on hand", "Asset", "cash", "cash"), ("1020", "Bank", "Asset", "bank", "cash"), ("1030", "Mobile wallet (EcoCash, OneMoney)", "Asset", "mobile", "cash"), ("1040", "Card settlements", "Asset", "card", "cash"),
            ("1050", "Other payment methods", "Asset", "other", "cash"), ("1100", "Accounts receivable", "Asset", "receivable", "current_asset"), ("1200", "Inventory", "Asset", null, "current_asset"), ("1300", "Prepayments", "Asset", null, "current_asset"),
            ("1310", "VAT input (recoverable)", "Asset", "vat_input", "current_asset"), ("1500", "Equipment and vehicles", "Asset", null, "fixed_asset"), ("1590", "Accumulated depreciation", "Asset", null, "fixed_asset"),
            ("2010", "Accounts payable", "Liability", "payable", "current_liability"), ("2110", "VAT output (collected)", "Liability", "vat_output", "current_liability"), ("2200", "Accrued expenses", "Liability", null, "current_liability"),
            ("2300", "Loans", "Liability", null, "non_current_liability"), ("2400", "Customer deposits", "Liability", null, "current_liability"),
            ("3010", "Owner's capital", "Equity", null, "equity"), ("3020", "Retained earnings", "Equity", "retained", "equity"), ("3030", "Drawings", "Equity", null, "equity"),
            ("4010", "Sales", "Income", "sales", "revenue"), ("4020", "Sales returns and credit notes", "Income", "sales_returns", "revenue"), ("4090", "Other income", "Income", "other_income", "other_income"),
            ("5010", "Cost of sales", "Expense", null, "cost_of_sales"), ("5100", "Salaries and wages", "Expense", null, null), ("5110", "Rent", "Expense", null, null), ("5120", "Utilities", "Expense", null, null),
            ("5130", "Transport and fuel", "Expense", null, null), ("5140", "Marketing", "Expense", null, null), ("5150", "Bank charges", "Expense", "bank_charges", null), ("5160", "Office and stationery", "Expense", null, null),
            ("5170", "Repairs and maintenance", "Expense", null, null), ("5180", "Professional fees", "Expense", null, null), ("5190", "Insurance", "Expense", null, null), ("5900", "Depreciation", "Expense", null, null),
            ("5990", "Other expenses", "Expense", "other_expense", null),
        };

        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        public AccountingService(FdmsDbContext db, ICallerScope scope) { _db = db; _scope = scope; }

        private void RequireWholeCompany()
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
        }

        // ---- chart of accounts ------------------------------------------------------------------------------------------------

        // The console fires several accounting calls at once on first visit; without this they all try to create the same chart and deadlock.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> ChartLocks = new();

        public async Task EnsureChartAsync(Guid clientId, CancellationToken ct)
        {
            var gate = ChartLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!await _db.LedgerAccounts.AnyAsync(a => a.ClientId == clientId, ct))
                {
                    foreach (var (code, name, type, role, subType) in StandardChart)
                        _db.LedgerAccounts.Add(new LedgerAccount { ClientId = clientId, Code = code, Name = name, Type = type, Role = role, SubType = subType, IsSystem = true });
                    try { await _db.SaveChangesAsync(ct); }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // another server instance seeded it first: forget ours and carry on with theirs
                        _db.ChangeTracker.Clear();
                        if (!await _db.LedgerAccounts.AnyAsync(a => a.ClientId == clientId, ct)) throw;
                    }
                }
                await EnsureBankLedgersAsync(clientId, ct);
            }
            finally { gate.Release(); }
        }

        /// <summary>Every bank account listed under Banks gets its own ledger account, so its cashbook and reconciliation exist from the start.</summary>
        private async Task EnsureBankLedgersAsync(Guid clientId, CancellationToken ct)
        {
            var linked = await _db.LedgerAccounts.Where(a => a.ClientId == clientId && a.BankAccountId != null).Select(a => a.BankAccountId!.Value).ToListAsync(ct);
            var banks = await _db.Banks.AsNoTracking().Where(b => b.ClientId == clientId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
            var missing = await _db.BankAccounts.Where(a => a.ClientId == clientId && !linked.Contains(a.Id)).ToListAsync(ct);
            if (missing.Count == 0) return;
            var codes = (await _db.LedgerAccounts.Where(a => a.ClientId == clientId).Select(a => a.Code).ToListAsync(ct)).ToHashSet();
            // each bank account sits under the main Bank account, numbered in the company's own format
            var parent = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && a.Role == "bank" && a.BankAccountId == null).OrderByDescending(a => a.IsSystem).ThenBy(a => a.Code).FirstOrDefaultAsync(ct);
            var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
            var next = 1021;
            foreach (var a in missing)
            {
                var code = ChartCodes.Next("Asset", parent, codes, rules).Code ?? ChartCodes.Next("Asset", null, codes, rules).Code;
                if (code == null) { while (codes.Contains(next.ToString())) next++; code = next.ToString(); }
                codes.Add(code);
                var last4 = a.AccountNumber.Length > 4 ? a.AccountNumber[^4..] : a.AccountNumber;
                _db.LedgerAccounts.Add(new LedgerAccount { ClientId = clientId, Code = code, Name = $"{banks.GetValueOrDefault(a.BankId, "Bank")} {a.CurrencyCode} ····{last4}", Type = "Asset", Role = "bank", SubType = "cash", BankAccountId = a.Id, ParentId = parent?.Id });
            }
            await _db.SaveChangesAsync(ct);
        }

        public static LedgerAccountDto ToDto(LedgerAccount a, bool inUse, int level = 0, bool hasChildren = false) =>
            new(a.Id, a.Code, a.Name, a.Type, a.Role, a.IsActive, a.IsSystem, a.BankAccountId, inUse, a.ParentId, a.IsGroup, ChartCodes.SubTypeOf(a), a.Description, level, hasChildren);

        /// <summary>The chart in tree order: each account followed by its sub-accounts.</summary>
        public async Task<IReadOnlyList<LedgerAccountDto>> ListAccountsAsync(Guid clientId, CancellationToken ct)
        {
            RequireWholeCompany();
            await EnsureChartAsync(clientId, ct);
            var used = (await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId).Select(l => l.AccountId).Distinct().ToListAsync(ct)).ToHashSet();
            var all = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).ToListAsync(ct);
            return ChartCodes.Tree(all).Select(x => ToDto(x.Account, used.Contains(x.Account.Id), x.Level, x.HasChildren)).ToList();
        }

        public async Task<LedgerAccountDto> SaveAccountAsync(Guid clientId, SaveLedgerAccountRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await EnsureChartAsync(clientId, ct);
            var name = (req.Name ?? "").Trim(); var code = (req.Code ?? "").Trim();
            if (name.Length is < 2 or > 100) throw GatewayException.BadRequest("NAME_INVALID", "Give the account a name (2-100 characters).");
            if (!CodeRx.IsMatch(code)) throw GatewayException.BadRequest("CODE_INVALID", "The account code is 2-20 letters or digits, with - . or / between groups if you like (for example 5125 or 5-125).");
            if (!Types.Contains(req.Type)) throw GatewayException.BadRequest("TYPE_INVALID", "The type must be Asset, Liability, Equity, Income or Expense.");
            var subType = string.IsNullOrWhiteSpace(req.SubType) ? null : req.SubType.Trim();
            if (subType != null && !ChartCodes.ValidSubType(subType, req.Type)) throw GatewayException.BadRequest("SUBTYPE_INVALID", $"Choose where a {req.Type} account appears in the statements.");
            var description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
            if (description is { Length: > 300 }) throw GatewayException.BadRequest("DESCRIPTION_INVALID", "Keep the description under 300 characters.");
            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            if (await _db.LedgerAccounts.AnyAsync(a => a.ClientId == clientId && a.Code == code && a.Id != id, ct)) throw GatewayException.Conflict("CODE_EXISTS", "Another account already uses that code.");
            var acc = id == null ? new LedgerAccount { ClientId = clientId } : await _db.LedgerAccounts.FirstOrDefaultAsync(a => a.Id == id && a.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Account");
            if (id != null && (acc.IsSystem || acc.BankAccountId != null) && acc.Type != req.Type) throw GatewayException.Conflict("SYSTEM_ACCOUNT", "The type of a standard or bank account cannot change. You can rename it, renumber it or switch it off.");
            if (id == null || acc.Code != code)
                if (ChartCodes.Problem(code, req.Type, await ChartCodes.RulesAsync(_db, clientId, ct)) is { } bad) throw GatewayException.BadRequest("CODE_FORMAT", bad);
            var inUse = id != null && await _db.JournalLines.AnyAsync(l => l.AccountId == acc.Id, ct);
            if (id != null && acc.Type != req.Type && inUse) throw GatewayException.Conflict("ACCOUNT_IN_USE", "This account has postings, so its type cannot change.");

            // its place in the tree: a parent of the same type, never itself or one of its own sub-accounts
            var parentId = req.ParentId is { } p && p != Guid.Empty ? p : (Guid?)null;
            if (parentId != null)
            {
                var all = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).ToListAsync(ct);
                var parent = all.FirstOrDefault(a => a.Id == parentId) ?? throw GatewayException.BadRequest("PARENT_INVALID", "The parent account is not in your chart.");
                if (parent.Type != req.Type) throw GatewayException.BadRequest("PARENT_INVALID", $"A {req.Type} account can only sit under another {req.Type} account.");
                if (id != null && ChartCodes.WithDescendants(all, id.Value).Contains(parent.Id)) throw GatewayException.BadRequest("PARENT_INVALID", "An account cannot sit under itself or one of its own sub-accounts.");
                var depth = 0; for (var x = parent; x?.ParentId != null && depth < 20; x = all.FirstOrDefault(a => a.Id == x.ParentId)) depth++;
                if (depth >= 5) throw GatewayException.BadRequest("PARENT_INVALID", "Accounts can be nested at most six levels deep.");
            }
            if (id != null && acc.Type != req.Type && await _db.LedgerAccounts.AnyAsync(a => a.ParentId == acc.Id, ct)) throw GatewayException.Conflict("HAS_CHILDREN", "Move or change its sub-accounts first: they must have the same type.");
            if (req.IsGroup && !acc.IsGroup)
            {
                if (acc.Role != null || acc.BankAccountId != null) throw GatewayException.Conflict("SYSTEM_ACCOUNT", "The system posts to this account, so it cannot become a heading.");
                if (inUse) throw GatewayException.Conflict("ACCOUNT_IN_USE", "This account has postings, so it cannot become a heading. Create a heading and put it under that instead.");
            }
            acc.Name = name; acc.Code = code; acc.Type = req.Type; acc.IsActive = req.IsActive; acc.ParentId = parentId; acc.IsGroup = req.IsGroup; acc.SubType = subType; acc.Description = description;
            if (id == null) _db.LedgerAccounts.Add(acc);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = id == null ? "ledger.account.create" : "ledger.account.update", Detail = $"{code} {name}{(req.IsGroup ? " (heading)" : "")}" });
            await _db.SaveChangesAsync(ct);
            return ToDto(acc, inUse);
        }

        public async Task DeleteAccountAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            RequireWholeCompany();
            var acc = await _db.LedgerAccounts.FirstOrDefaultAsync(a => a.Id == id && a.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Account");
            if (acc.IsSystem || acc.BankAccountId != null) throw GatewayException.Conflict("SYSTEM_ACCOUNT", "Standard and bank accounts cannot be deleted. Switch them off instead.");
            if (await _db.JournalLines.AnyAsync(l => l.AccountId == id, ct)) throw GatewayException.Conflict("ACCOUNT_IN_USE", "This account has postings, so it cannot be deleted. Switch it off instead.");
            if (await _db.LedgerAccounts.AnyAsync(a => a.ParentId == id, ct)) throw GatewayException.Conflict("HAS_CHILDREN", "This account has sub-accounts. Move or delete them first.");
            _db.LedgerAccounts.Remove(acc);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.account.delete", Detail = $"{acc.Code} {acc.Name}" });
            await _db.SaveChangesAsync(ct);
        }

        // ---- journals ---------------------------------------------------------------------------------------------------------

        private async Task<JournalDto> MapAsync(JournalEntry e, CancellationToken ct)
        {
            var ids = e.Lines.Select(l => l.AccountId).Distinct().ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            return Map(e, accs, await CentresAsync(e.ClientId, ct));
        }

        private async Task<Dictionary<Guid, string>> CentresAsync(Guid clientId, CancellationToken ct) =>
            await _db.CostCentres.AsNoTracking().Where(c => c.ClientId == clientId).ToDictionaryAsync(c => c.Id, c => c.Code + " " + c.Name, ct);

        private static JournalDto Map(JournalEntry e, IReadOnlyDictionary<Guid, LedgerAccount> accs, IReadOnlyDictionary<Guid, string> centres) =>
            new(e.Id, e.Number, e.Date, e.Currency, e.Memo, e.Reference, e.Source, e.ReversedById != null, e.ReversesId != null, e.CreatedBy, e.Lines.Sum(l => l.Debit),
                e.Lines.OrderByDescending(l => l.Debit > 0).ThenBy(l => accs.GetValueOrDefault(l.AccountId)?.Code).Select(l => new JournalLineDto(l.Id, l.AccountId, accs.GetValueOrDefault(l.AccountId)?.Code ?? "",
                    accs.GetValueOrDefault(l.AccountId)?.Name ?? "", l.Debit, l.Credit, l.Description, l.CostCentreId, l.CostCentreId is { } c ? centres.GetValueOrDefault(c) : null)).ToList());

        public async Task<IReadOnlyList<JournalDto>> ListJournalsAsync(Guid clientId, JournalFilter f, CancellationToken ct)
        {
            RequireWholeCompany();
            await SyncSalesAsync(clientId, ct);
            var q = _db.JournalEntries.AsNoTracking().Include(e => e.Lines).Where(e => e.ClientId == clientId);
            if (f.From != null) q = q.Where(e => e.Date >= f.From.Value.Date);
            if (f.To != null) q = q.Where(e => e.Date < f.To.Value.Date.AddDays(1));
            if (!string.IsNullOrWhiteSpace(f.Source)) q = q.Where(e => e.Source == f.Source);
            if (!string.IsNullOrWhiteSpace(f.Currency)) q = q.Where(e => e.Currency == f.Currency!.ToUpper());
            if (!string.IsNullOrWhiteSpace(f.Q)) { var s = f.Q.Trim(); q = q.Where(e => e.Memo.Contains(s) || e.Number.Contains(s) || (e.Reference != null && e.Reference.Contains(s))); }
            var rows = await q.OrderByDescending(e => e.Date).ThenByDescending(e => e.Seq).Take(Math.Clamp(f.Take, 1, 500)).ToListAsync(ct);
            var ids = rows.SelectMany(e => e.Lines.Select(l => l.AccountId)).Distinct().ToList();
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
            var centres = await CentresAsync(clientId, ct);
            return rows.Select(e => Map(e, accs, centres)).ToList();
        }

        public async Task<JournalDto> GetJournalAsync(Guid clientId, Guid id, CancellationToken ct)
        {
            RequireWholeCompany();
            var e = await _db.JournalEntries.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Journal");
            return await MapAsync(e, ct);
        }

        private async Task<int> NextSeqAsync(Guid clientId, CancellationToken ct) => (await _db.JournalEntries.Where(e => e.ClientId == clientId).MaxAsync(e => (int?)e.Seq, ct) ?? 0) + 1;

        private JournalEntry NewEntry(Guid clientId, int seq, DateTime date, string currency, string memo, string? reference, string source, IEnumerable<(Guid Account, decimal Debit, decimal Credit, string? Text)> lines)
        {
            var e = new JournalEntry { ClientId = clientId, Seq = seq, Number = "JV-" + seq.ToString("D6"), Date = date.Date, Currency = currency, Memo = memo, Reference = reference, Source = source, CreatedBy = _scope.UserName };
            foreach (var (account, debit, credit, text) in lines) e.Lines.Add(new JournalLine { EntryId = e.Id, ClientId = clientId, AccountId = account, Debit = debit, Credit = credit, Description = text, Date = e.Date, Currency = currency });
            return e;
        }

        private async Task<JournalDto> SaveEntryAsync(Guid clientId, Func<int, JournalEntry> build, string audit, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                var e = build(await NextSeqAsync(clientId, ct));
                _db.JournalEntries.Add(e);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = audit, Detail = $"{e.Number} {e.Memo}" });
                try { await _db.SaveChangesAsync(ct); return await MapAsync(e, ct); }
                catch (DbUpdateException) when (attempt < 3)   // two people posted at once and took the same number: take the next one
                {
                    foreach (var entry in _db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
                }
            }
        }

        private async Task<string> CheckCurrencyAsync(string? currency, CancellationToken ct)
        {
            var c = (currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(x => x.Code == c && x.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            return c;
        }

        public async Task<JournalDto> CreateJournalAsync(Guid clientId, CreateJournalRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await EnsureChartAsync(clientId, ct);
            var currency = await CheckCurrencyAsync(req.Currency, ct);
            var memo = (req.Memo ?? "").Trim();
            if (memo.Length is < 3 or > 200) throw GatewayException.BadRequest("MEMO_INVALID", "Describe the entry (3-200 characters).");
            if (req.Date.Year < 2000 || req.Date > DateTime.UtcNow.AddDays(366)) throw GatewayException.BadRequest("DATE_INVALID", "That date is not valid for an entry.");
            await EnsureOpenAsync(clientId, req.Date, ct);
            var reference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim();
            if (reference is { Length: > 50 }) throw GatewayException.BadRequest("REFERENCE_INVALID", "The reference can be at most 50 characters.");
            var lines = req.Lines ?? Array.Empty<JournalLineInput>();
            if (lines.Count < 2 || lines.Count > 60) throw GatewayException.BadRequest("LINES_INVALID", "An entry needs at least two lines.");
            foreach (var l in lines)
            {
                if (l.Debit < 0 || l.Credit < 0 || (l.Debit > 0) == (l.Credit > 0)) throw GatewayException.BadRequest("LINE_INVALID", "Each line has either a debit or a credit amount, not both and not neither.");
                if (l.Debit != Math.Round(l.Debit, 2) || l.Credit != Math.Round(l.Credit, 2) || Math.Max(l.Debit, l.Credit) > 1_000_000_000m) throw GatewayException.BadRequest("AMOUNT_INVALID", "Amounts have at most two decimals.");
            }
            var debit = lines.Sum(l => l.Debit); var credit = lines.Sum(l => l.Credit);
            if (debit != credit) throw GatewayException.BadRequest("UNBALANCED", $"Debits ({debit:N2}) and credits ({credit:N2}) must be equal.");
            var ids = lines.Select(l => l.AccountId).Distinct().ToList();
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && ids.Contains(a.Id)).ToListAsync(ct);
            if (accounts.Count != ids.Count) throw GatewayException.BadRequest("ACCOUNT_INVALID", "One of the accounts is not in your chart of accounts.");
            if (accounts.Any(a => !a.IsActive)) throw GatewayException.BadRequest("ACCOUNT_INACTIVE", "One of the accounts is switched off.");
            if (accounts.FirstOrDefault(a => a.IsGroup) is { } heading) throw GatewayException.BadRequest("ACCOUNT_HEADING", $"{heading.Code} {heading.Name} is a heading: post to one of its sub-accounts.");
            await CheckCentresAsync(clientId, lines.Select(l => l.CostCentreId), ct);
            if (req.ReverseOn is { } rev && rev.Date <= req.Date.Date) throw GatewayException.BadRequest("REVERSE_INVALID", "The reversal date must be after the entry's date.");
            if (req.ReverseOn is { } rev2) await EnsureOpenAsync(clientId, rev2, ct);

            var posted = await SaveEntryAsync(clientId, seq =>
            {
                var e = NewEntry(clientId, seq, req.Date, currency, memo, reference, "manual", lines.Select(l => (l.AccountId, l.Debit, l.Credit, string.IsNullOrWhiteSpace(l.Description) ? null : l.Description.Trim())));
                for (var i = 0; i < lines.Count; i++) e.Lines[i].CostCentreId = lines[i].CostCentreId;
                return e;
            }, "ledger.journal", ct);
            if (req.ReverseOn is { } on) await ReverseAtAsync(clientId, posted.Id, on.Date, ct);
            return posted;
        }

        /// <summary>Cost centres given on lines must be the company's own and in use.</summary>
        public async Task CheckCentresAsync(Guid clientId, IEnumerable<Guid?> ids, CancellationToken ct)
        {
            var wanted = ids.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
            if (wanted.Count == 0) return;
            var ok = await _db.CostCentres.AsNoTracking().CountAsync(c => c.ClientId == clientId && c.IsActive && wanted.Contains(c.Id), ct);
            if (ok != wanted.Count) throw GatewayException.BadRequest("COST_CENTRE_INVALID", "One of the cost centres is not yours or is switched off.");
        }

        public Task<JournalDto> ReverseJournalAsync(Guid clientId, Guid id, CancellationToken ct) => ReverseAtAsync(clientId, id, null, ct);

        /// <summary>Reverses an entry on a date (null: today, or the entry's own date if later). Accruals use it to undo themselves at the start of the next period.</summary>
        public async Task<JournalDto> ReverseAtAsync(Guid clientId, Guid id, DateTime? on, CancellationToken ct)
        {
            RequireWholeCompany();
            var original = await _db.JournalEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == id && e.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Journal");
            if (original.ReversedById != null) throw GatewayException.Conflict("ALREADY_REVERSED", "That entry has already been reversed.");
            if (original.ReversesId != null) throw GatewayException.Conflict("IS_REVERSAL", "A reversal cannot itself be reversed. Post a new entry instead.");
            JournalEntry? created = null;
            var today = DateTime.UtcNow.AddHours(2).Date;
            var revDate = on ?? (today > original.Date ? today : original.Date);   // a reversal is dated today (or later), never back into a period that may be closed
            await EnsureOpenAsync(clientId, revDate, ct);
            var dto = await SaveEntryAsync(clientId, seq =>
            {
                created = NewEntry(clientId, seq, revDate, original.Currency, "Reversal of " + original.Number + ": " + original.Memo, original.Reference, "reversal",
                    original.Lines.Select(l => (l.AccountId, l.Credit, l.Debit, l.Description)));
                for (var i = 0; i < original.Lines.Count; i++) created.Lines[i].CostCentreId = original.Lines[i].CostCentreId;
                created.ReversesId = original.Id; original.ReversedById = created.Id;
                return created;
            }, "ledger.reverse", ct);
            return dto;
        }

        // ---- cashbook entries -------------------------------------------------------------------------------------------------

        public async Task<JournalDto> CashbookEntryAsync(Guid clientId, CashbookEntryRequest req, CancellationToken ct)
        {
            RequireWholeCompany();
            await EnsureChartAsync(clientId, ct);
            var currency = await CheckCurrencyAsync(req.Currency, ct);
            var kind = (req.Kind ?? "").ToLowerInvariant();
            if (kind is not ("receipt" or "payment")) throw GatewayException.BadRequest("KIND_INVALID", "Choose receipt or payment.");
            if (req.Amount <= 0 || req.Amount != Math.Round(req.Amount, 2)) throw GatewayException.BadRequest("AMOUNT_INVALID", "Enter an amount above zero with at most two decimals.");
            var description = (req.Description ?? "").Trim();
            if (description.Length is < 3 or > 200) throw GatewayException.BadRequest("MEMO_INVALID", "Describe it (3-200 characters).");
            if (req.AccountId == req.CounterAccountId) throw GatewayException.BadRequest("ACCOUNT_INVALID", "The two accounts must be different.");
            await EnsureOpenAsync(clientId, req.Date, ct);
            var accs = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && (a.Id == req.AccountId || a.Id == req.CounterAccountId)).ToListAsync(ct);
            var cash = accs.FirstOrDefault(a => a.Id == req.AccountId); var counter = accs.FirstOrDefault(a => a.Id == req.CounterAccountId);
            if (cash == null || counter == null) throw GatewayException.BadRequest("ACCOUNT_INVALID", "Choose accounts from your chart of accounts.");
            if (cash.Type != "Asset") throw GatewayException.BadRequest("ACCOUNT_INVALID", "A cashbook is kept for cash, bank and wallet accounts.");
            if (!cash.IsActive || !counter.IsActive) throw GatewayException.BadRequest("ACCOUNT_INACTIVE", "One of the accounts is switched off.");
            if (cash.IsGroup || counter.IsGroup) throw GatewayException.BadRequest("ACCOUNT_HEADING", "A heading account cannot be posted to: choose one of its sub-accounts.");
            var reference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim();
            if (reference is { Length: > 50 }) throw GatewayException.BadRequest("REFERENCE_INVALID", "The reference can be at most 50 characters.");
            var (dr, cr) = kind == "receipt" ? (cash.Id, counter.Id) : (counter.Id, cash.Id);
            return await SaveEntryAsync(clientId, seq => NewEntry(clientId, seq, req.Date, currency, description, reference, "cashbook",
                new (Guid, decimal, decimal, string?)[] { (dr, req.Amount, 0m, description), (cr, 0m, req.Amount, description) }), "ledger.cashbook", ct);
        }

        // ---- closing a period -------------------------------------------------------------------------------------------------

        public async Task<DateTime?> LockedUntilAsync(Guid clientId, CancellationToken ct) =>
            await _db.AccountingSettings.AsNoTracking().Where(s => s.ClientId == clientId).Select(s => s.LockedUntil).FirstOrDefaultAsync(ct);

        private async Task EnsureOpenAsync(Guid clientId, DateTime date, CancellationToken ct)
        {
            var locked = await LockedUntilAsync(clientId, ct);
            if (locked != null && date.Date <= locked.Value.Date)
                throw GatewayException.Conflict("PERIOD_LOCKED", $"The books are closed up to {locked.Value:d MMM yyyy}. Post this on a later date, or ask an administrator to reopen the period.");
        }

        public async Task<PeriodDto> SetLockAsync(Guid clientId, DateTime? lockedUntil, CancellationToken ct)
        {
            RequireWholeCompany();
            var today = DateTime.UtcNow.AddHours(2).Date;
            if (lockedUntil != null && lockedUntil.Value.Date > today) throw GatewayException.BadRequest("DATE_INVALID", "A period can only be closed up to today.");
            var row = await _db.AccountingSettings.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (row == null) _db.AccountingSettings.Add(row = new AccountingSetting { ClientId = clientId });
            var before = row.LockedUntil;
            row.LockedUntil = lockedUntil?.Date; row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.period", Detail = $"closed up to {(row.LockedUntil == null ? "nothing" : row.LockedUntil.Value.ToString("yyyy-MM-dd"))} (was {(before == null ? "nothing" : before.Value.ToString("yyyy-MM-dd"))})" });
            await _db.SaveChangesAsync(ct);
            return new PeriodDto(row.LockedUntil);
        }

        public async Task<JournalDto> PostEntryAsync(Guid clientId, DateTime date, string currency, string memo, string? reference, string source, Guid? sourceId, IReadOnlyList<(Guid Account, decimal Debit, decimal Credit)> lines, CancellationToken ct,
            (Guid Account, Guid CostCentre)? tag = null)
        {
            RequireWholeCompany();
            await EnsureOpenAsync(clientId, date, ct);
            if (lines.Count < 2 || lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit) || lines.Sum(l => l.Debit) <= 0) throw GatewayException.BadRequest("UNBALANCED", "Debits and credits must be equal.");
            var ids = lines.Select(l => l.Account).Distinct().ToList();
            if (await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && ids.Contains(a.Id) && a.IsGroup).Select(a => a.Code + " " + a.Name).FirstOrDefaultAsync(ct) is { } heading)
                throw GatewayException.BadRequest("ACCOUNT_HEADING", $"{heading} is a heading: choose one of its sub-accounts.");
            return await SaveEntryAsync(clientId, seq =>
            {
                var e = NewEntry(clientId, seq, date, currency, memo, reference, source, lines.Where(l => l.Debit > 0 || l.Credit > 0).Select(l => (l.Account, l.Debit, l.Credit, (string?)null)));
                e.SourceId = sourceId;
                if (tag is { } t) foreach (var l in e.Lines.Where(l => l.AccountId == t.Account)) l.CostCentreId = t.CostCentre;
                return e;
            }, "ledger." + source, ct);
        }

        public async Task<JournalDto> PostLinesAsync(Guid clientId, DateTime date, string currency, string memo, string? reference, string source, Guid? sourceId, IReadOnlyList<JournalLineInput> lines, CancellationToken ct)
        {
            RequireWholeCompany();
            await EnsureOpenAsync(clientId, date, ct);
            var used = lines.Where(l => l.Debit > 0 || l.Credit > 0).ToList();
            if (used.Count < 2 || used.Sum(l => l.Debit) != used.Sum(l => l.Credit)) throw GatewayException.BadRequest("UNBALANCED", "Debits and credits must be equal.");
            var ids = used.Select(l => l.AccountId).Distinct().ToList();
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && ids.Contains(a.Id)).ToListAsync(ct);
            if (accounts.Count != ids.Count) throw GatewayException.BadRequest("ACCOUNT_INVALID", "One of the accounts is not in your chart of accounts.");
            if (accounts.FirstOrDefault(a => a.IsGroup) is { } heading) throw GatewayException.BadRequest("ACCOUNT_HEADING", $"{heading.Code} {heading.Name} is a heading: choose one of its sub-accounts.");
            if (accounts.FirstOrDefault(a => !a.IsActive) is { } off) throw GatewayException.BadRequest("ACCOUNT_INACTIVE", $"{off.Code} {off.Name} is switched off.");
            await CheckCentresAsync(clientId, used.Select(l => l.CostCentreId), ct);
            return await SaveEntryAsync(clientId, seq =>
            {
                var e = NewEntry(clientId, seq, date, currency, memo, reference, source, used.Select(l => (l.AccountId, l.Debit, l.Credit, l.Description)));
                e.SourceId = sourceId;
                for (var i = 0; i < used.Count; i++) e.Lines[i].CostCentreId = used[i].CostCentreId;
                return e;
            }, "ledger." + source, ct);
        }

        // ---- posting fiscal receipts ------------------------------------------------------------------------------------------

        public async Task<SyncResultDto> SyncSalesAsync(Guid clientId, CancellationToken ct)
        {
            await EnsureChartAsync(clientId, ct);
            if (PlatformBooks.Is(clientId)) await SyncPlatformAsync(ct);   // the platform's own books follow billing
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && a.Role != null).ToListAsync(ct);
            LedgerAccount Role(string role) => accounts.Where(a => a.Role == role && a.BankAccountId == null).OrderBy(a => a.Code).FirstOrDefault() ?? accounts.Where(a => a.Role == role).OrderBy(a => a.Code).First();
            var bankLedgers = accounts.Where(a => a.BankAccountId != null).ToList();
            var bankAccounts = await _db.BankAccounts.AsNoTracking().Where(b => b.ClientId == clientId && b.IsActive).ToListAsync(ct);

            Guid BankFor(string currency)
            {
                var b = bankAccounts.Where(x => x.CurrencyCode == currency).OrderByDescending(x => x.IsDefault).FirstOrDefault();
                return bankLedgers.FirstOrDefault(l => b != null && l.BankAccountId == b.Id)?.Id ?? Role("bank").Id;
            }
            Guid PaymentAccount(string code, string currency) => code switch
            {
                "Cash" => Role("cash").Id, "Card" => Role("card").Id, "MobileWallet" => Role("mobile").Id, "BankTransfer" => BankFor(currency), "Credit" => Role("receivable").Id, _ => Role("other").Id,
            };

            var locked = await LockedUntilAsync(clientId, ct);
            // the sales of a branch's devices go to that branch's cost centre
            var branchCentre = await _db.CostCentres.AsNoTracking().Where(c => c.ClientId == clientId && c.IsActive && c.BranchId != null).GroupBy(c => c.BranchId!.Value).Select(g => new { g.Key, Id = g.Min(x => x.Id) }).ToDictionaryAsync(x => x.Key, x => x.Id, ct);
            var deviceCentre = branchCentre.Count == 0 ? new Dictionary<Guid, Guid>() : (await _db.Devices.AsNoTracking().Where(d => d.ClientId == clientId && d.BranchId != null).Select(d => new { d.Id, d.BranchId }).ToListAsync(ct))
                .Where(d => branchCentre.ContainsKey(d.BranchId!.Value)).ToDictionary(d => d.Id, d => branchCentre[d.BranchId!.Value]);
            var total = 0;
            for (var round = 0; round < 10; round++)   // in batches, so one call never has to load a whole history at once
            {
                var todo = await _db.Receipts.AsNoTracking()
                    .Where(r => r.ClientId == clientId && r.Total != 0 && (r.Status == ReceiptStatus.Submitted || r.Status == ReceiptStatus.Pending || r.Status == ReceiptStatus.OfflineIssued)
                        && !_db.JournalEntries.Any(e => e.ClientId == clientId && e.SourceId == r.Id))
                    .OrderBy(r => r.ReceiptDate).Take(500).ToListAsync(ct);
                if (todo.Count == 0) break;

                var seq = await NextSeqAsync(clientId, ct);
                var added = 0;
                foreach (var r in todo)
                {
                    ReceiptPayload? p = null;
                    try { if (!string.IsNullOrEmpty(r.PayloadJson)) p = JsonSerializer.Deserialize<ReceiptPayload>(r.PayloadJson, FdmsJson.Options); } catch (JsonException) { /* posted from the total alone */ }
                    var amount = Math.Abs(r.Total);
                    var vat = p == null ? 0m : p.ReceiptTaxes.Sum(t => Math.Abs(t.TaxAmount));
                    if (vat > amount) vat = 0;
                    var net = amount - vat;
                    var credit = r.Type == ReceiptType.CreditNote;

                    // where the money went: each payment method to its own account, and whatever was not paid yet to receivables
                    var split = new List<(Guid Account, decimal Amount)>();
                    var left = amount;
                    foreach (var pay in p?.ReceiptPayments ?? new())
                    {
                        var a = Math.Min(Math.Abs(pay.PaymentAmount), left);
                        if (a <= 0) continue;
                        split.Add((PaymentAccount(pay.MoneyTypeCode, r.Currency), a)); left -= a;
                    }
                    if (left > 0) split.Add((Role("receivable").Id, left));

                    var lines = new List<(Guid, decimal, decimal, string?)>();
                    var memo = (credit ? "Credit note " : r.Type == ReceiptType.DebitNote ? "Debit note " : "Invoice ") + r.InvoiceNo;
                    foreach (var (acc, a) in split.GroupBy(s => s.Account).Select(g => (g.Key, g.Sum(x => x.Amount)))) lines.Add(credit ? (acc, 0m, a, memo) : (acc, a, 0m, memo));
                    if (net > 0) lines.Add(credit ? (Role("sales_returns").Id, net, 0m, memo) : (Role("sales").Id, 0m, net, memo));
                    if (vat > 0) lines.Add(credit ? (Role("vat_output").Id, vat, 0m, memo) : (Role("vat_output").Id, 0m, vat, memo));
                    if (lines.Count < 2) continue;
                    // a receipt confirmed after its period was closed is posted on the first open day, and says so, instead of changing figures that were already signed off
                    var postDate = locked != null && r.ReceiptDate.Date <= locked.Value.Date ? locked.Value.Date.AddDays(1) : r.ReceiptDate;
                    if (postDate.Date != r.ReceiptDate.Date) memo += $" (dated {r.ReceiptDate:yyyy-MM-dd})";
                    var entry = NewEntry(clientId, seq++, postDate, r.Currency, memo, r.InvoiceNo, "sales", lines);
                    entry.SourceId = r.Id; entry.CreatedBy = "system";
                    if (deviceCentre.TryGetValue(r.DeviceId, out var centre))
                        foreach (var l in entry.Lines.Where(l => l.AccountId == Role("sales").Id || l.AccountId == Role("sales_returns").Id)) l.CostCentreId = centre;
                    _db.JournalEntries.Add(entry); added++;
                }
                if (added == 0) break;
                try { await _db.SaveChangesAsync(ct); total += added; }
                catch (DbUpdateException)   // another request posted at the same moment: forget this batch and look again
                {
                    foreach (var en in _db.ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToList()) en.State = EntityState.Detached;
                }
            }
            try { await SyncStockAsync(clientId, ct); }
            catch (DbUpdateException)   // another request took the same stock out at the same moment: theirs stands
            {
                foreach (var en in _db.ChangeTracker.Entries().Where(x => x.State != EntityState.Unchanged).ToList()) en.State = EntityState.Detached;
            }
            return new SyncResultDto(total);
        }
    }
}
