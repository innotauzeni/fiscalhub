using System.Text.RegularExpressions;
using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Services
{
    /// <summary>The platform's currency list. Everyone reads the active ones; only the operator changes the list.</summary>
    public sealed class CurrencyService : ICurrencyService
    {
        private static readonly Regex CodeRx = new("^[A-Z]{3}$", RegexOptions.Compiled);
        public static readonly (string Code, string Name, string Symbol)[] Defaults =
        {
            ("USD", "US Dollar", "$"), ("ZWG", "Zimbabwe Gold", "ZiG"), ("ZAR", "South African Rand", "R"), ("BWP", "Botswana Pula", "P"), ("GBP", "British Pound", "£"), ("EUR", "Euro", "€"),
        };
        private readonly FdmsDbContext _db;
        public CurrencyService(FdmsDbContext db) => _db = db;

        public async Task<IReadOnlyList<CurrencyDto>> ListAsync(bool activeOnly, CancellationToken ct)
        {
            var q = _db.Currencies.AsNoTracking();
            if (activeOnly) q = q.Where(c => c.IsActive);
            var rows = await q.OrderBy(c => c.SortOrder).ThenBy(c => c.Code).ToListAsync(ct);
            var used = await _db.BankAccounts.AsNoTracking().GroupBy(a => a.CurrencyCode).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
            return rows.Select(c => new CurrencyDto(c.Code, c.Name, c.Symbol, c.IsActive, c.SortOrder, used.GetValueOrDefault(c.Code))).ToList();
        }

        public async Task<CurrencyDto> SaveAsync(SaveCurrencyRequest req, CancellationToken ct)
        {
            var code = (req.Code ?? "").Trim().ToUpperInvariant();
            var name = (req.Name ?? "").Trim();
            if (!CodeRx.IsMatch(code)) throw GatewayException.BadRequest("CODE_INVALID", "A currency code is 3 letters, for example USD.");
            if (name.Length is < 2 or > 60) throw GatewayException.BadRequest("NAME_INVALID", "Give the currency a name (2-60 characters).");
            var symbol = (req.Symbol ?? "").Trim();
            if (symbol.Length > 6) throw GatewayException.BadRequest("SYMBOL_INVALID", "The symbol can be at most 6 characters.");
            var row = await _db.Currencies.FirstOrDefaultAsync(c => c.Code == code, ct);
            if (row == null) _db.Currencies.Add(row = new Currency { Code = code });
            row.Name = name; row.Symbol = symbol.Length == 0 ? code : symbol; row.IsActive = req.IsActive; row.SortOrder = Math.Clamp(req.SortOrder, 0, 9999);
            _db.AuditLogs.Add(new AuditLog { Action = "currency.save", Detail = $"{code} {name}" });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(false, ct)).First(c => c.Code == code);
        }

        public async Task DeleteAsync(string code, CancellationToken ct)
        {
            code = (code ?? "").Trim().ToUpperInvariant();
            var row = await _db.Currencies.FirstOrDefaultAsync(c => c.Code == code, ct) ?? throw GatewayException.NotFound("Currency");
            if (await _db.BankAccounts.AnyAsync(a => a.CurrencyCode == code, ct)) throw GatewayException.Conflict("CURRENCY_IN_USE", "Bank accounts use this currency. Switch it off instead of deleting it.");
            _db.Currencies.Remove(row);
            _db.AuditLogs.Add(new AuditLog { Action = "currency.delete", Detail = code });
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>A company's banks and their accounts. Choosing a bank offers its accounts (and each account knows its currency).</summary>
    public sealed class BankService : IBankService
    {
        private readonly FdmsDbContext _db;
        public BankService(FdmsDbContext db) => _db = db;

        private static string? Clean(string? v, int max, string what)
        {
            v = v?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (v.Length > max) throw GatewayException.BadRequest("TOO_LONG", $"{what} can be at most {max} characters.");
            return v;
        }

        public async Task<IReadOnlyList<BankDto>> ListAsync(Guid clientId, CancellationToken ct)
        {
            var banks = await _db.Banks.AsNoTracking().Where(b => b.ClientId == clientId).OrderBy(b => b.Name).ToListAsync(ct);
            var accounts = await _db.BankAccounts.AsNoTracking().Where(a => a.ClientId == clientId).OrderBy(a => a.CurrencyCode).ThenBy(a => a.AccountName).ToListAsync(ct);
            return banks.Select(b => new BankDto(b.Id, b.Name, b.Code, b.IsActive,
                accounts.Where(a => a.BankId == b.Id).Select(a => new BankAccountDto(a.Id, b.Id, b.Name, a.AccountName, a.AccountNumber, a.BranchName, a.BranchCode, a.CurrencyCode, a.IsDefault, a.IsActive)).ToList())).ToList();
        }

        public async Task<BankDto> SaveBankAsync(Guid clientId, SaveBankRequest req, CancellationToken ct)
        {
            var name = (req.Name ?? "").Trim();
            if (name.Length is < 2 or > 100) throw GatewayException.BadRequest("NAME_INVALID", "Give the bank a name (2-100 characters), for example NMB.");
            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            if (await _db.Banks.AnyAsync(b => b.ClientId == clientId && b.Name == name && b.Id != id, ct)) throw GatewayException.Conflict("BANK_EXISTS", "You already have a bank with that name.");
            var bank = id == null ? new Bank { ClientId = clientId } : await _db.Banks.FirstOrDefaultAsync(b => b.Id == id && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Bank");
            bank.Name = name; bank.Code = Clean(req.Code, 20, "The bank code"); bank.IsActive = req.IsActive;
            if (id == null) _db.Banks.Add(bank);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = id == null ? "bank.create" : "bank.update", Detail = name });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(clientId, ct)).First(b => b.Id == bank.Id);
        }

        public async Task DeleteBankAsync(Guid clientId, Guid bankId, CancellationToken ct)
        {
            var bank = await _db.Banks.FirstOrDefaultAsync(b => b.Id == bankId && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Bank");
            _db.BankAccounts.RemoveRange(await _db.BankAccounts.Where(a => a.BankId == bankId && a.ClientId == clientId).ToListAsync(ct));
            _db.Banks.Remove(bank);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bank.delete", Detail = bank.Name });
            await _db.SaveChangesAsync(ct);
        }

        public async Task<BankDto> SaveAccountAsync(Guid clientId, SaveBankAccountRequest req, CancellationToken ct)
        {
            var bank = await _db.Banks.FirstOrDefaultAsync(b => b.Id == req.BankId && b.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Bank");
            var accName = (req.AccountName ?? "").Trim(); var number = (req.AccountNumber ?? "").Trim();
            if (accName.Length is < 2 or > 100) throw GatewayException.BadRequest("NAME_INVALID", "Give the account a name (2-100 characters), the name it is held in.");
            if (number.Length is < 4 or > 34 || !Regex.IsMatch(number, @"^[A-Za-z0-9 \-]+$")) throw GatewayException.BadRequest("NUMBER_INVALID", "The account number is 4-34 letters, digits, spaces or dashes.");
            var currency = (req.Currency ?? "").Trim().ToUpperInvariant();
            if (!await _db.Currencies.AnyAsync(c => c.Code == currency && c.IsActive, ct)) throw GatewayException.BadRequest("CURRENCY_INVALID", "Choose a currency from the list.");
            var id = req.Id is { } g && g != Guid.Empty ? g : (Guid?)null;
            if (await _db.BankAccounts.AnyAsync(a => a.BankId == bank.Id && a.AccountNumber == number && a.Id != id, ct)) throw GatewayException.Conflict("ACCOUNT_EXISTS", "That account number is already listed under this bank.");
            var acc = id == null ? new BankAccount { ClientId = clientId } : await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == id && a.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Account");
            acc.BankId = bank.Id; acc.AccountName = accName; acc.AccountNumber = number; acc.CurrencyCode = currency; acc.IsActive = req.IsActive;
            acc.BranchName = Clean(req.BranchName, 100, "The branch name"); acc.BranchCode = Clean(req.BranchCode, 20, "The branch code");
            acc.IsDefault = req.IsDefault && req.IsActive;
            if (id == null) _db.BankAccounts.Add(acc);
            if (acc.IsDefault)   // only one default per currency
                foreach (var other in await _db.BankAccounts.Where(a => a.ClientId == clientId && a.CurrencyCode == currency && a.IsDefault && a.Id != acc.Id).ToListAsync(ct)) other.IsDefault = false;
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = id == null ? "bank.account.create" : "bank.account.update", Detail = $"{bank.Name} {number} {currency}" });
            await _db.SaveChangesAsync(ct);
            return (await ListAsync(clientId, ct)).First(b => b.Id == bank.Id);
        }

        public async Task DeleteAccountAsync(Guid clientId, Guid accountId, CancellationToken ct)
        {
            var acc = await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ClientId == clientId, ct) ?? throw GatewayException.NotFound("Account");
            _db.BankAccounts.Remove(acc);
            _db.AuditLogs.Add(new AuditLog { ClientId = clientId, Action = "bank.account.delete", Detail = acc.AccountNumber });
            await _db.SaveChangesAsync(ct);
        }
    }
}
