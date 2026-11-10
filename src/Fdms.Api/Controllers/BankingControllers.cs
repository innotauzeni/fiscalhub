using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>The currencies every pick list offers. Anyone signed in reads the active ones; only the platform operator changes the list.</summary>
    [ApiController, Authorize, Route("api/v1/currencies")]
    public class CurrenciesController : ControllerBase
    {
        private readonly ICurrencyService _s;
        public CurrenciesController(ICurrencyService s) => _s = s;

        [HttpGet, HasPermission(Perm.DevicesView)] public async Task<IActionResult> Active(CancellationToken ct) => Ok(await _s.ListAsync(true, ct));
        [HttpGet("all"), HasPermission(Perm.CurrenciesManage)] public async Task<IActionResult> All(CancellationToken ct) => Ok(await _s.ListAsync(false, ct));
        [HttpPut, HasPermission(Perm.CurrenciesManage)] public async Task<IActionResult> Save(SaveCurrencyRequest body, CancellationToken ct) => Ok(await _s.SaveAsync(body, ct));
        [HttpDelete("{code}"), HasPermission(Perm.CurrenciesManage)] public async Task<IActionResult> Delete(string code, CancellationToken ct) { await _s.DeleteAsync(code, ct); return NoContent(); }
    }

    /// <summary>A company's banks and their accounts. Anyone who can see devices can read them (to pick payment details); changing them needs banks.manage.</summary>
    [ApiController, Authorize, Route("api/v1/banks")]
    public class BanksController : ControllerBase
    {
        private readonly IBankService _s;
        public BanksController(IBankService s) => _s = s;

        [HttpGet, HasPermission(Perm.DevicesView)] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _s.ListAsync(User.ClientId(), ct));
        [HttpPut, HasPermission(Perm.BanksManage)] public async Task<IActionResult> SaveBank(SaveBankRequest body, CancellationToken ct) => Ok(await _s.SaveBankAsync(User.ClientId(), body, ct));
        [HttpDelete("{id:guid}"), HasPermission(Perm.BanksManage)] public async Task<IActionResult> DeleteBank(Guid id, CancellationToken ct) { await _s.DeleteBankAsync(User.ClientId(), id, ct); return NoContent(); }
        [HttpPut("accounts"), HasPermission(Perm.BanksManage)] public async Task<IActionResult> SaveAccount(SaveBankAccountRequest body, CancellationToken ct) => Ok(await _s.SaveAccountAsync(User.ClientId(), body, ct));
        [HttpDelete("accounts/{id:guid}"), HasPermission(Perm.BanksManage)] public async Task<IActionResult> DeleteAccount(Guid id, CancellationToken ct) { await _s.DeleteAccountAsync(User.ClientId(), id, ct); return NoContent(); }
    }
}
