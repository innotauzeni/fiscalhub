namespace Fdms.Application.Abstractions
{
    /// <summary>An action that can be put behind approval, as shown in the matrix.</summary>
    public sealed record ApprovalActionDto(string Key, string Label, string Description, string AmountMeaning);
    public sealed record ApprovalRuleDto(Guid Id, string Action, string ActionLabel, decimal MinAmount, string? Currency, IReadOnlyList<string> Levels, bool IsActive);
    public sealed record SaveApprovalRuleRequest(Guid? Id, string Action, decimal MinAmount, string? Currency, IReadOnlyList<string> Levels, bool IsActive = true);

    /// <summary>The matrix for one action: its rules from the smallest amount up, so the screen can draw "from X: level 1 → level 2".</summary>
    public sealed record ApprovalMatrixRowDto(ApprovalActionDto Action, IReadOnlyList<ApprovalRuleDto> Rules);
    public sealed record ApprovalMatrixDto(IReadOnlyList<ApprovalMatrixRowDto> Rows, IReadOnlyList<string> Roles);

    public sealed record ApprovalDecisionDto(int Level, string Role, string UserName, string Decision, string? Note, DateTime At);
    public sealed record ApprovalRequestDto(Guid Id, string Action, string ActionLabel, string Status, string Summary, decimal Amount, string? Currency, string? EntityId,
        IReadOnlyList<string> Levels, int CurrentLevel, string? WaitingForRole, string RequestedBy, DateTime CreatedAt, DateTime? DecidedAt, string? Error,
        bool CanDecide, bool CanCancel, IReadOnlyList<ApprovalDecisionDto> Decisions);
    public sealed record ApprovalListDto(IReadOnlyList<ApprovalRequestDto> Items, int WaitingForMe);
    public sealed record DecideApprovalRequest(string? Note);

    /// <summary>The answer when an action was held for approval instead of being carried out (HTTP 202).</summary>
    public sealed record ApprovalRequiredDto(bool ApprovalRequired, string Message, ApprovalRequestDto Request);

    public interface IApprovalService
    {
        /// <summary>
        /// Whether the action needs approval. When a rule matches, the request is stored and returned (the caller answers 202 and does nothing else);
        /// null means "no rule: go ahead".
        /// </summary>
        Task<ApprovalRequiredDto?> GateAsync(Guid clientId, string action, decimal amount, string? currency, string summary, string? entityId, object payload, CancellationToken ct);
        Task<ApprovalMatrixDto> MatrixAsync(Guid clientId, CancellationToken ct);
        Task<ApprovalRuleDto> SaveRuleAsync(Guid clientId, SaveApprovalRuleRequest req, CancellationToken ct);
        Task DeleteRuleAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<ApprovalListDto> ListAsync(Guid clientId, string scope, string? status, CancellationToken ct);
        Task<ApprovalRequestDto> GetAsync(Guid clientId, Guid id, CancellationToken ct);
        Task<ApprovalRequestDto> ApproveAsync(Guid clientId, Guid id, string? note, CancellationToken ct);
        Task<ApprovalRequestDto> RejectAsync(Guid clientId, Guid id, string? note, CancellationToken ct);
        Task<ApprovalRequestDto> CancelAsync(Guid clientId, Guid id, CancellationToken ct);
        /// <summary>The receipt's total, currency and number (credit notes and cancellations are measured by the receipt).</summary>
        Task<(decimal Total, string Currency, string Number)> ReceiptAsync(Guid clientId, Guid receiptId, CancellationToken ct);
        /// <summary>The bill's currency and number.</summary>
        Task<(string Currency, string Number, string Supplier)> BillAsync(Guid clientId, Guid billId, CancellationToken ct);
    }

    /// <summary>Carries out an approved action (implemented where the commands live). Returns what the action returned.</summary>
    public interface IApprovalExecutor
    {
        Task<object?> ExecuteAsync(Guid clientId, string action, string? entityId, string payloadJson, CancellationToken ct);
    }
}
