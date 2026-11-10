namespace Fdms.Application.Abstractions
{
    public sealed record CurrencyDto(string Code, string Name, string Symbol, bool IsActive, int SortOrder, int AccountsUsing);
    public sealed record SaveCurrencyRequest(string Code, string Name, string? Symbol, bool IsActive, int SortOrder);

    public sealed record BankAccountDto(Guid Id, Guid BankId, string BankName, string AccountName, string AccountNumber, string? BranchName, string? BranchCode, string Currency, bool IsDefault, bool IsActive);
    public sealed record BankDto(Guid Id, string Name, string? Code, bool IsActive, IReadOnlyList<BankAccountDto> Accounts);
    public sealed record SaveBankRequest(Guid? Id, string Name, string? Code, bool IsActive);
    public sealed record SaveBankAccountRequest(Guid? Id, Guid BankId, string AccountName, string AccountNumber, string? BranchName, string? BranchCode, string Currency, bool IsDefault, bool IsActive);

    public interface ICurrencyService
    {
        /// <summary>All currencies (operator) or only the active ones (everyone else, for pick lists).</summary>
        Task<IReadOnlyList<CurrencyDto>> ListAsync(bool activeOnly, CancellationToken ct);
        Task<CurrencyDto> SaveAsync(SaveCurrencyRequest req, CancellationToken ct);
        Task DeleteAsync(string code, CancellationToken ct);
    }

    public interface IBankService
    {
        Task<IReadOnlyList<BankDto>> ListAsync(Guid clientId, CancellationToken ct);
        Task<BankDto> SaveBankAsync(Guid clientId, SaveBankRequest req, CancellationToken ct);
        Task DeleteBankAsync(Guid clientId, Guid bankId, CancellationToken ct);
        Task<BankDto> SaveAccountAsync(Guid clientId, SaveBankAccountRequest req, CancellationToken ct);
        Task DeleteAccountAsync(Guid clientId, Guid accountId, CancellationToken ct);
    }
}
