using System.Text;
using System.Text.Json;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>A company's numbering rules, read from its books settings.</summary>
    public sealed record ChartRules(string Mask, IReadOnlyDictionary<string, string> Starts, int Step, bool Enforce, int FiscalYearStartMonth);

    /// <summary>
    /// The chart of accounts' structure and numbering: where each account sits in the statements (its sub-type), the parent/child tree, code formats
    /// (9 = a digit, anything else a separator) and suggested codes. Codes are labels only: everything else finds accounts by id or role, so a chart can be renumbered.
    /// </summary>
    public static class ChartCodes
    {
        public static readonly string[] Types = { "Asset", "Liability", "Equity", "Income", "Expense" };
        public static readonly string[] CashRoles = { "cash", "bank", "mobile", "card", "other" };
        public static readonly IReadOnlyDictionary<string, string> DefaultStarts = new Dictionary<string, string> { ["Asset"] = "1", ["Liability"] = "2", ["Equity"] = "3", ["Income"] = "4", ["Expense"] = "5" };
        private const string Separators = "-./";

        public static readonly IReadOnlyList<SubTypeDto> SubTypes = new SubTypeDto[]
        {
            new("cash", "Cash and cash equivalents", "Asset"), new("current_asset", "Current assets", "Asset"), new("fixed_asset", "Property, plant and equipment", "Asset"),
            new("non_current_asset", "Other non-current assets", "Asset"),
            new("current_liability", "Current liabilities", "Liability"), new("non_current_liability", "Non-current liabilities", "Liability"),
            new("equity", "Equity", "Equity"),
            new("revenue", "Revenue", "Income"), new("other_income", "Other income", "Income"),
            new("cost_of_sales", "Cost of sales", "Expense"), new("operating_expense", "Operating expenses", "Expense"), new("finance_cost", "Finance costs", "Expense"), new("income_tax", "Income tax", "Expense"),
        };

        public static bool ValidSubType(string? subType, string type) => subType != null && SubTypes.Any(s => s.Key == subType && s.Type == type);

        /// <summary>Where the account appears in the statements: its chosen sub-type, or the usual one for its type.</summary>
        public static string SubTypeOf(LedgerAccount a)
        {
            if (ValidSubType(a.SubType, a.Type)) return a.SubType!;
            return a.Type switch
            {
                "Asset" => a.BankAccountId != null || (a.Role != null && CashRoles.Contains(a.Role)) ? "cash" : "current_asset",
                "Liability" => "current_liability",
                "Equity" => "equity",
                "Income" => "revenue",
                _ => a.Code.StartsWith("501") ? "cost_of_sales" : "operating_expense",
            };
        }

        public static bool IsDebitNormal(string type) => type is "Asset" or "Expense";

        // ---- the tree ---------------------------------------------------------------------------------------------------------

        /// <summary>The accounts in tree order (each parent followed by its children, by code), with their depth and whether they have children. A broken link counts as a top account.</summary>
        public static List<(LedgerAccount Account, int Level, bool HasChildren)> Tree(IEnumerable<LedgerAccount> accounts)
        {
            var all = accounts.ToList();
            var ids = all.Select(a => a.Id).ToHashSet();
            var children = all.Where(a => a.ParentId != null && ids.Contains(a.ParentId.Value) && a.ParentId != a.Id).GroupBy(a => a.ParentId!.Value).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Code, StringComparer.Ordinal).ToList());
            var result = new List<(LedgerAccount, int, bool)>();
            var seen = new HashSet<Guid>();
            void Walk(LedgerAccount a, int level)
            {
                if (!seen.Add(a.Id)) return;
                var kids = children.GetValueOrDefault(a.Id);
                result.Add((a, level, kids is { Count: > 0 }));
                if (kids != null) foreach (var k in kids) Walk(k, Math.Min(level + 1, 9));
            }
            foreach (var root in all.Where(a => a.ParentId == null || !ids.Contains(a.ParentId.Value) || a.ParentId == a.Id).OrderBy(a => Array.IndexOf(Types, a.Type)).ThenBy(a => a.Code, StringComparer.Ordinal)) Walk(root, 0);
            foreach (var a in all.Where(a => !seen.Contains(a.Id)).OrderBy(a => a.Code, StringComparer.Ordinal)) Walk(a, 0);   // only a cycle can leave one out
            return result;
        }

        /// <summary>The account and every account below it.</summary>
        public static HashSet<Guid> WithDescendants(IEnumerable<LedgerAccount> accounts, Guid id)
        {
            var byParent = accounts.Where(a => a.ParentId != null).GroupBy(a => a.ParentId!.Value).ToDictionary(g => g.Key, g => g.Select(a => a.Id).ToList());
            var set = new HashSet<Guid> { id };
            var stack = new Stack<Guid>(); stack.Push(id);
            while (stack.Count > 0) foreach (var c in byParent.GetValueOrDefault(stack.Pop()) ?? new List<Guid>()) if (set.Add(c)) stack.Push(c);
            return set;
        }

        // ---- codes ------------------------------------------------------------------------------------------------------------

        public static int Digits(string mask) => mask.Count(c => c == '9');

        public static bool ValidMask(string? mask) =>
            mask is { Length: >= 2 and <= 20 } && mask[0] == '9' && mask[^1] == '9' && mask.All(c => c == '9' || Separators.Contains(c)) && Digits(mask) is >= 2 and <= 12
            && !mask.Zip(mask.Skip(1)).Any(p => p.First != '9' && p.Second != '9');

        /// <summary>The code without its separators.</summary>
        public static string Strip(string code) => new(code.Where(c => !Separators.Contains(c)).ToArray());

        public static bool Fits(string code, string mask) =>
            code.Length == mask.Length && code.Zip(mask).All(p => p.Second == '9' ? char.IsDigit(p.First) : p.First == p.Second);

        /// <summary>Digits laid into the format (9999 + "1010" = 1010; 99-999 + "10100" = 10-100).</summary>
        public static string Format(string digits, string mask)
        {
            var sb = new StringBuilder(); var i = 0;
            foreach (var m in mask) sb.Append(m == '9' ? (i < digits.Length ? digits[i++] : '0') : m);
            return sb.ToString();
        }

        public static string StartOf(ChartRules r, string type) => r.Starts.TryGetValue(type, out var s) ? s : DefaultStarts[type];

        /// <summary>Why a code does not follow the company's rules, or null.</summary>
        public static string? Problem(string code, string type, ChartRules r)
        {
            if (!r.Enforce) return null;
            if (!Fits(code, r.Mask)) return $"Account codes follow the format {Format(StartOf(r, "Asset"), r.Mask)} ({r.Mask.Replace('9', '0')}).";
            var start = StartOf(r, type);
            if (!Strip(code).StartsWith(start)) return $"{type} account codes start with {start}.";
            return null;
        }

        /// <summary>
        /// The next free code: under a parent, in the room its trailing zeros leave (1000 → 1010, 1020 …; 1020 → 1021 …); at the top, after the type's first digits (1000 → 1010 …).
        /// </summary>
        public static NextCodeDto Next(string type, LedgerAccount? parent, IEnumerable<string> existingCodes, ChartRules r)
        {
            var d = Digits(r.Mask);
            var taken = existingCodes.Select(Strip).ToHashSet();
            string baseDigits; long span;
            if (parent != null)
            {
                baseDigits = Strip(parent.Code);
                if (baseDigits.Length != d || !baseDigits.All(char.IsDigit)) return new NextCodeDto(null, $"The parent's code {parent.Code} does not follow the format, so no code can be suggested. Type one.");
                var zeros = baseDigits.Reverse().TakeWhile(c => c == '0').Count();
                if (zeros == 0) return new NextCodeDto(null, $"There is no room below {parent.Code} (it does not end in 0). Type a code.");
                span = Pow10(Math.Min(zeros, d - 1));
            }
            else
            {
                var start = StartOf(r, type);
                if (start.Length >= d) return new NextCodeDto(null, "The format is too short for the type's first digits.");
                baseDigits = start.PadRight(d, '0');
                span = Pow10(d - start.Length);
            }
            var step = r.Step > 0 && r.Step < span ? r.Step : Math.Max(1, span / 10);
            var b = long.Parse(baseDigits);
            for (var k = 1L; k * step < span && k < 100_000; k++)
            {
                var c = (b + k * step).ToString().PadLeft(d, '0');
                if (!taken.Contains(c)) return new NextCodeDto(Format(c, r.Mask), null);
            }
            if (step > 1)
                for (var k = 1L; k < span && k < 100_000; k++) { var c = (b + k).ToString().PadLeft(d, '0'); if (!taken.Contains(c)) return new NextCodeDto(Format(c, r.Mask), null); }
            return new NextCodeDto(null, "Every code in that range is taken. Type one.");
        }

        private static long Pow10(int n) { long v = 1; for (var i = 0; i < n; i++) v *= 10; return v; }

        /// <summary>A code under new rules: the type's first digits swapped, then zeros added or removed at the end to fit the new length (so 1020 → 102000 keeps its place and its children).</summary>
        public static (string? Code, string? Problem) Convert(string code, string type, ChartRules from, ChartRules to)
        {
            var d = Strip(code);
            if (!d.All(char.IsDigit) || d.Length == 0) return (null, $"{code} has letters in it; change it by hand first.");
            var oldStart = StartOf(from, type); var newStart = StartOf(to, type);
            if (d.StartsWith(oldStart)) d = newStart + d[oldStart.Length..];
            var n = Digits(to.Mask);
            if (d.Length < n) d = d.PadRight(n, '0');
            else if (d.Length > n)
            {
                if (d[n..].Any(c => c != '0')) return (null, $"{code} does not fit in {n} digits.");
                d = d[..n];
            }
            return (Format(d, to.Mask), null);
        }

        // ---- reading the settings and finding accounts by role -------------------------------------------------------------------

        public static ChartRules Rules(AccountingSetting? s)
        {
            Dictionary<string, string>? starts = null;
            if (!string.IsNullOrWhiteSpace(s?.ClassStartsJson))
                try { starts = JsonSerializer.Deserialize<Dictionary<string, string>>(s.ClassStartsJson); } catch (JsonException) { }
            var merged = Types.ToDictionary(t => t, t => starts != null && starts.TryGetValue(t, out var v) && !string.IsNullOrWhiteSpace(v) ? v : DefaultStarts[t]);
            return new ChartRules(ValidMask(s?.CodeFormat) ? s!.CodeFormat : "9999", merged, s?.CodeStep is > 0 ? s.CodeStep : 10, s?.EnforceCodeFormat ?? false,
                s?.FiscalYearStartMonth is >= 1 and <= 12 ? s.FiscalYearStartMonth : 1);
        }

        public static async Task<ChartRules> RulesAsync(FdmsDbContext db, Guid clientId, CancellationToken ct) =>
            Rules(await db.AccountingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.ClientId == clientId, ct));

        /// <summary>The account the system posts to for a role (accounts payable, VAT input …); the standard code for charts from before roles existed.</summary>
        public static async Task<LedgerAccount?> ByRoleAsync(FdmsDbContext db, Guid clientId, string role, string standardCode, CancellationToken ct) =>
            await db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId && a.Role == role && a.BankAccountId == null).OrderByDescending(a => a.IsSystem).ThenBy(a => a.Code).FirstOrDefaultAsync(ct)
            ?? await db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.ClientId == clientId && a.Code == standardCode, ct);

        /// <summary>The first day of the financial year a date falls in.</summary>
        public static DateTime YearStart(DateTime date, int startMonth)
        {
            var y = date.Month >= startMonth ? date.Year : date.Year - 1;
            return new DateTime(y, startMonth, 1);
        }
    }

    public sealed class ChartService : IChartService
    {
        private readonly FdmsDbContext _db;
        private readonly ICallerScope _scope;
        private readonly IAccountingService _books;
        public ChartService(FdmsDbContext db, ICallerScope scope, IAccountingService books) { _db = db; _scope = scope; _books = books; }

        private async Task PrepareAsync(Guid clientId, CancellationToken ct)
        {
            if (_scope.AllowedDeviceIds != null) throw new GatewayException(403, "SCOPE_RESTRICTED", "Your access is limited to specific devices, so you cannot work with the company's books.");
            await _books.EnsureChartAsync(clientId, ct);
        }

        private static ChartSettingsDto Dto(ChartRules r) => new(r.Mask, r.Starts, r.Step, r.Enforce, r.FiscalYearStartMonth);

        private static ChartSetupDto Setup(ChartRules r) =>
            new(Dto(r), ChartCodes.SubTypes, string.Join(", ", ChartCodes.Types.Select(t => $"{t} {ChartCodes.Format(ChartCodes.StartOf(r, t), r.Mask)}")));

        public async Task<ChartSetupDto> SetupAsync(Guid clientId, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            return Setup(await ChartCodes.RulesAsync(_db, clientId, ct));
        }

        /// <summary>Checks a requested set of rules and returns them cleaned.</summary>
        private static ChartRules Check(ChartSettingsDto req)
        {
            var mask = (req.CodeFormat ?? "").Trim().Replace('0', '9');   // 0000 means the same as 9999
            if (!ChartCodes.ValidMask(mask))
                throw GatewayException.BadRequest("FORMAT_INVALID", "Write the format with 9 (or 0) for each digit and -, . or / between groups, for example 9999, 999999, 99-999 or 9.99.999 (2 to 12 digits).");
            var digits = ChartCodes.Digits(mask);
            var starts = new Dictionary<string, string>();
            foreach (var t in ChartCodes.Types)
            {
                var v = (req.ClassStarts != null && req.ClassStarts.TryGetValue(t, out var x) ? x : ChartCodes.DefaultStarts[t])?.Trim() ?? "";
                if (v.Length is < 1 or > 4 || !v.All(char.IsDigit) || v.Length >= digits) throw GatewayException.BadRequest("START_INVALID", $"{t} codes need first digits (1 to 4 digits, shorter than the code).");
                starts[t] = v;
            }
            foreach (var a in starts) foreach (var b in starts)
                if (a.Key != b.Key && b.Value.StartsWith(a.Value)) throw GatewayException.BadRequest("START_INVALID", $"{a.Key} and {b.Key} codes cannot start the same way ({a.Value} / {b.Value}).");
            if (req.CodeStep is < 1 or > 100000) throw GatewayException.BadRequest("STEP_INVALID", "The gap between codes is 1 to 100000.");
            if (req.FiscalYearStartMonth is < 1 or > 12) throw GatewayException.BadRequest("MONTH_INVALID", "Choose the month the financial year starts.");
            return new ChartRules(mask, starts, req.CodeStep, req.EnforceCodeFormat, req.FiscalYearStartMonth);
        }

        private async Task<AccountingSetting> RowAsync(Guid clientId, CancellationToken ct)
        {
            var row = await _db.AccountingSettings.FirstOrDefaultAsync(s => s.ClientId == clientId, ct);
            if (row == null) _db.AccountingSettings.Add(row = new AccountingSetting { ClientId = clientId });
            return row;
        }

        private void Apply(AccountingSetting row, ChartRules r)
        {
            row.CodeFormat = r.Mask; row.ClassStartsJson = JsonSerializer.Serialize(r.Starts); row.CodeStep = r.Step; row.EnforceCodeFormat = r.Enforce; row.FiscalYearStartMonth = r.FiscalYearStartMonth;
            row.UpdatedAt = DateTime.UtcNow; row.UpdatedBy = _scope.UserName;
        }

        public async Task<ChartSetupDto> SaveSettingsAsync(Guid clientId, ChartSettingsDto req, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var r = Check(req);
            Apply(await RowAsync(clientId, ct), r);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.chart-settings", Detail = $"format {r.Mask}, starts {string.Join(" ", r.Starts.Select(s => s.Key[0] + s.Value))}, step {r.Step}, enforce {(r.Enforce ? "on" : "off")}, year from month {r.FiscalYearStartMonth}" });
            await _db.SaveChangesAsync(ct);
            return Setup(r);
        }

        public async Task<NextCodeDto> NextCodeAsync(Guid clientId, string type, Guid? parentId, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            if (!ChartCodes.Types.Contains(type)) throw GatewayException.BadRequest("TYPE_INVALID", "Choose the account type.");
            var r = await ChartCodes.RulesAsync(_db, clientId, ct);
            LedgerAccount? parent = null;
            if (parentId is { } pid && pid != Guid.Empty)
                parent = await _db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == pid && a.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Account");
            var codes = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).Select(a => a.Code).ToListAsync(ct);
            return ChartCodes.Next(type, parent, codes, r);
        }

        private async Task<(RenumberPreviewDto Preview, ChartRules To, Dictionary<Guid, string> Map)> PlanAsync(Guid clientId, ChartSettingsDto req, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var from = await ChartCodes.RulesAsync(_db, clientId, ct);
            var to = Check(req);
            var accounts = await _db.LedgerAccounts.AsNoTracking().Where(a => a.ClientId == clientId).ToListAsync(ct);
            var rows = new List<RenumberRow>(); var problems = new List<string>(); var map = new Dictionary<Guid, string>();
            foreach (var (a, _, _) in ChartCodes.Tree(accounts))
            {
                var (code, problem) = ChartCodes.Convert(a.Code, a.Type, from, to);
                if (problem != null) { problems.Add(problem); rows.Add(new RenumberRow(a.Id, a.Name, a.Type, a.Code, a.Code)); continue; }
                rows.Add(new RenumberRow(a.Id, a.Name, a.Type, a.Code, code!));
                if (code != a.Code) map[a.Id] = code!;
            }
            foreach (var dup in rows.GroupBy(r => r.NewCode).Where(g => g.Count() > 1))
                problems.Add($"{string.Join(" and ", dup.Select(x => $"{x.OldCode} {x.Name}"))} would all become {dup.Key}.");
            return (new RenumberPreviewDto(rows, problems, map.Count), to, map);
        }

        public async Task<RenumberPreviewDto> PreviewRenumberAsync(Guid clientId, ChartSettingsDto req, CancellationToken ct) => (await PlanAsync(clientId, req, ct)).Preview;

        public async Task<RenumberPreviewDto> RenumberAsync(Guid clientId, ChartSettingsDto req, CancellationToken ct)
        {
            var (preview, to, map) = await PlanAsync(clientId, req, ct);
            if (preview.Problems.Count > 0) throw GatewayException.Conflict("RENUMBER_PROBLEMS", "The chart cannot be renumbered yet: " + preview.Problems[0]);
            var ids = map.Keys.ToList();
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync(ct) : null;
                var accounts = await _db.LedgerAccounts.Where(a => a.ClientId == clientId && ids.Contains(a.Id)).ToListAsync(ct);
                // two steps, so swapping codes never trips the unique code index on the way
                foreach (var a in accounts) a.Code = "~" + a.Id.ToString("N")[..12];
                await _db.SaveChangesAsync(ct);
                foreach (var a in accounts) a.Code = map[a.Id];
                Apply(await RowAsync(clientId, ct), to);
                _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.renumber", Detail = $"{map.Count} account(s) renumbered to the format {to.Mask}" });
                await _db.SaveChangesAsync(ct);
                if (tx != null) await tx.CommitAsync(ct);
            });
            return preview;
        }

        // ---- import ----------------------------------------------------------------------------------------------------------

        internal static List<string> SplitCsv(string line)
        {
            var cells = new List<string>(); var sb = new StringBuilder(); var quoted = false;
            var sep = line.Count(c => c == ';') > line.Count(c => c == ',') ? ';' : ',';
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (c == '"') quoted = false; else sb.Append(c); }
                else if (c == '"') quoted = true;
                else if (c == sep) { cells.Add(sb.ToString().Trim()); sb.Clear(); }
                else sb.Append(c);
            }
            cells.Add(sb.ToString().Trim());
            return cells;
        }

        internal static string? TypeOf(string v) => v.Trim().ToLowerInvariant() switch
        {
            "asset" or "assets" => "Asset",
            "liability" or "liabilities" => "Liability",
            "equity" or "capital" => "Equity",
            "income" or "revenue" or "sales" => "Income",
            "expense" or "expenses" or "cost" or "costs" => "Expense",
            _ => null,
        };

        internal static string? SubTypeOf(string v, string type)
        {
            var x = v.Trim().ToLowerInvariant();
            if (x.Length == 0) return null;
            return ChartCodes.SubTypes.FirstOrDefault(s => s.Type == type && (s.Key == x.Replace(' ', '_') || s.Label.ToLowerInvariant() == x))?.Key;
        }

        /// <summary>
        /// CSV columns: code, name, type, sub-type, parent code, heading (yes/no), description (the first line may be a header). Accounts are matched by code: existing ones get the new
        /// name, sub-type, heading flag, description and parent; new ones are created. A row that cannot be used is skipped and says why.
        /// </summary>
        public async Task<ImportChartResult> ImportAsync(Guid clientId, ImportChartRequest req, CancellationToken ct)
        {
            await PrepareAsync(clientId, ct);
            var text = req.Csv ?? "";
            if (text.Length > 500_000) throw GatewayException.BadRequest("FILE_TOO_LARGE", "Keep the file under 500 KB (about 5000 accounts).");
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count > 0 && SplitCsv(lines[0]).FirstOrDefault()?.Trim().ToLowerInvariant() is "code" or "account code" or "account" or "number") lines.RemoveAt(0);
            if (lines.Count == 0) throw GatewayException.BadRequest("FILE_EMPTY", "The file has no accounts. Columns: code, name, type, sub-type, parent code, heading, description.");
            if (lines.Count > 5000) throw GatewayException.BadRequest("FILE_TOO_LARGE", "At most 5000 accounts at a time.");

            var rules = await ChartCodes.RulesAsync(_db, clientId, ct);
            var accounts = await _db.LedgerAccounts.Where(a => a.ClientId == clientId).ToListAsync(ct);
            var used = (await _db.JournalLines.AsNoTracking().Where(l => l.ClientId == clientId).Select(l => l.AccountId).Distinct().ToListAsync(ct)).ToHashSet();
            var byCode = accounts.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>(); int created = 0, updated = 0, skipped = 0;
            var parents = new List<(LedgerAccount Account, string ParentCode, int Line)>();

            for (var i = 0; i < lines.Count; i++)
            {
                var n = i + 1; var c = SplitCsv(lines[i]);
                string Cell(int k) => k < c.Count ? c[k] : "";
                var code = Cell(0); var name = Cell(1);
                var type = TypeOf(Cell(2));
                if (!AccountingService.CodeRx.IsMatch(code)) { problems.Add($"Line {n}: \"{code}\" is not a valid code."); skipped++; continue; }
                if (name.Length is < 2 or > 100) { problems.Add($"Line {n} ({code}): the name must be 2-100 characters."); skipped++; continue; }
                byCode.TryGetValue(code, out var acc);
                type ??= acc?.Type;
                if (type == null) { problems.Add($"Line {n} ({code}): the type must be Asset, Liability, Equity, Income or Expense."); skipped++; continue; }
                if (acc == null && ChartCodes.Problem(code, type, rules) is { } bad) { problems.Add($"Line {n} ({code}): {bad}"); skipped++; continue; }
                var sub = SubTypeOf(Cell(3), type);
                if (Cell(3).Length > 0 && sub == null) problems.Add($"Line {n} ({code}): sub-type \"{Cell(3)}\" is not known for {type}; the usual one is used.");
                var heading = Cell(5).Trim().ToLowerInvariant() is "yes" or "y" or "true" or "1" or "heading" or "group";
                var description = Cell(6).Length > 0 ? (Cell(6).Length > 300 ? Cell(6)[..300] : Cell(6)) : null;

                if (acc == null)
                {
                    acc = new LedgerAccount { ClientId = clientId, Code = code, Name = name, Type = type, SubType = sub, IsGroup = heading, Description = description };
                    _db.LedgerAccounts.Add(acc); accounts.Add(acc); byCode[code] = acc; created++;
                }
                else
                {
                    if (acc.Type != type) { problems.Add($"Line {n} ({code}): it is already a {acc.Type} account; its type was kept."); }
                    acc.Name = name; acc.SubType = sub ?? acc.SubType; acc.Description = description ?? acc.Description;
                    if (heading != acc.IsGroup)
                    {
                        if (heading && (used.Contains(acc.Id) || acc.Role != null || acc.BankAccountId != null)) problems.Add($"Line {n} ({code}): it has postings or is used by the system, so it stays a posting account.");
                        else acc.IsGroup = heading;
                    }
                    updated++;
                }
                if (Cell(4).Length > 0) parents.Add((acc, Cell(4), n));
            }

            foreach (var (acc, parentCode, n) in parents)
            {
                if (!byCode.TryGetValue(parentCode, out var parent)) { problems.Add($"Line {n} ({acc.Code}): parent {parentCode} was not found."); continue; }
                if (parent.Type != acc.Type) { problems.Add($"Line {n} ({acc.Code}): parent {parentCode} is a {parent.Type} account, not {acc.Type}."); continue; }
                if (ChartCodes.WithDescendants(accounts, acc.Id).Contains(parent.Id)) { problems.Add($"Line {n} ({acc.Code}): {parentCode} is below it, so it cannot be its parent."); continue; }
                acc.ParentId = parent.Id;
            }
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "ledger.chart-import", Detail = $"{created} created, {updated} updated, {skipped} skipped" });
            await _db.SaveChangesAsync(ct);
            return new ImportChartResult(created, updated, skipped, problems.Take(200).ToList());
        }
    }
}
