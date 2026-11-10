using System.Text.Json;
using Fdms.Api.Security;
using Fdms.Application.Abstractions;
using Fdms.Application.Features;
using Fdms.Application.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fdms.Api.Controllers
{
    /// <summary>The approvals inbox (what waits for me, what I asked for, everything) and the decisions on it.</summary>
    [ApiController, Authorize, HasPermission(Perm.ApprovalsView), Route("api/v1/approvals")]
    public class ApprovalsController : ControllerBase
    {
        private readonly IApprovalService _s;
        public ApprovalsController(IApprovalService s) => _s = s;

        /// <summary>scope: mine (waiting for my decision), requested (asked by me) or all.</summary>
        [HttpGet] public async Task<IActionResult> List([FromQuery] string scope = "mine", [FromQuery] string? status = null, CancellationToken ct = default) => Ok(await _s.ListAsync(User.ClientId(), scope, status, ct));
        [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await _s.GetAsync(User.ClientId(), id, ct));
        [HttpPost("{id:guid}/approve")] public async Task<IActionResult> Approve(Guid id, DecideApprovalRequest body, CancellationToken ct) => Ok(await _s.ApproveAsync(User.ClientId(), id, body.Note, ct));
        [HttpPost("{id:guid}/reject")] public async Task<IActionResult> Reject(Guid id, DecideApprovalRequest body, CancellationToken ct) => Ok(await _s.RejectAsync(User.ClientId(), id, body.Note, ct));
        [HttpPost("{id:guid}/cancel")] public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Ok(await _s.CancelAsync(User.ClientId(), id, ct));

        /// <summary>The approval matrix: every action that can need approval, with its rules (anyone in the inbox may read it).</summary>
        [HttpGet("matrix")] public async Task<IActionResult> Matrix(CancellationToken ct) => Ok(await _s.MatrixAsync(User.ClientId(), ct));
    }

    /// <summary>Editing the approval matrix.</summary>
    [ApiController, Authorize, HasPermission(Perm.ApprovalsManage), Route("api/v1/approvals/rules")]
    public class ApprovalRulesController : ControllerBase
    {
        private readonly IApprovalService _s;
        public ApprovalRulesController(IApprovalService s) => _s = s;
        [HttpPut] public async Task<IActionResult> Save(SaveApprovalRuleRequest body, CancellationToken ct) => Ok(await _s.SaveRuleAsync(User.ClientId(), body, ct));
        [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await _s.DeleteRuleAsync(User.ClientId(), id, ct); return NoContent(); }
    }

    /// <summary>Carries out an approved action with the very request that was held, through the same commands and services the endpoints use.</summary>
    public sealed class ApprovalExecutor : IApprovalExecutor
    {
        private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
        private readonly IMediator _m;
        private readonly IAccountingService _books;
        private readonly IPayablesService _payables;
        public ApprovalExecutor(IMediator m, IAccountingService books, IPayablesService payables) { _m = m; _books = books; _payables = payables; }

        private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, J) ?? throw GatewayException.BadRequest("PAYLOAD_INVALID", "The held request could not be read.");
        private static Guid Id(string? entityId) => Guid.TryParse(entityId, out var g) ? g : throw GatewayException.BadRequest("PAYLOAD_INVALID", "The held request has no record.");

        public async Task<object?> ExecuteAsync(Guid clientId, string action, string? entityId, string payloadJson, CancellationToken ct) => action switch
        {
            "credit_note" => await _m.Send(new CorrectReceiptCommand(clientId, Id(entityId), Read<CorrectReceiptRequest>(payloadJson)), ct),
            "debit_note" => await _m.Send(new IssueDebitNoteCommand(clientId, Id(entityId), Read<DebitNoteRequest>(payloadJson)), ct),
            "receipt_cancel" => await _m.Send(new CancelReceiptCommand(clientId, Id(entityId), Read<ReceiptsCancelPayload>(payloadJson).Reason), ct),
            "bill" => await _payables.CreateBillAsync(clientId, Read<CreateBillRequest>(payloadJson), ct),
            "bill_payment" => await _payables.PayBillAsync(clientId, Id(entityId), Read<PayBillRequest>(payloadJson), ct),
            "journal" => await _books.CreateJournalAsync(clientId, Read<CreateJournalRequest>(payloadJson), ct),
            _ => throw GatewayException.BadRequest("ACTION_INVALID", "Unknown action."),
        };
    }

    /// <summary>What a held receipt cancellation remembers.</summary>
    public sealed record ReceiptsCancelPayload(string? Reason);
}
