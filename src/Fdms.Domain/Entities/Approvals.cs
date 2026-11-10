namespace Fdms.Domain.Entities
{
    /// <summary>
    /// One row of a company's approval matrix: "this action, from this amount (in this currency), needs these approval levels".
    /// The most specific rule that matches wins (the highest starting amount). Levels are approved in order, each by someone holding the level's role.
    /// </summary>
    public class ApprovalRule
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>credit_note, debit_note, receipt_cancel, bill, bill_payment, journal.</summary>
        public string Action { get; set; } = "";
        /// <summary>The rule applies from this amount up (0 = every amount).</summary>
        public decimal MinAmount { get; set; }
        /// <summary>Only for this currency; null = any currency.</summary>
        public string? Currency { get; set; }
        /// <summary>The roles that approve, level by level, as JSON: ["Accountant","ClientAdmin"].</summary>
        public string LevelsJson { get; set; } = "[]";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>An action held for approval. On the last approval the original request is carried out; a rejection or cancellation ends it.</summary>
    public class ApprovalRequest
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Action { get; set; } = "";
        public Guid? RuleId { get; set; }
        /// <summary>The levels as they were when the request was made (a later change to the matrix does not move a request in flight).</summary>
        public string LevelsJson { get; set; } = "[]";
        /// <summary>0-based index of the level waiting for a decision.</summary>
        public int CurrentLevel { get; set; }
        /// <summary>Pending, Approved (and carried out), Rejected, Cancelled, Failed (approved but the action itself failed).</summary>
        public string Status { get; set; } = "Pending";
        public string Summary { get; set; } = "";
        public decimal Amount { get; set; }
        public string? Currency { get; set; }
        /// <summary>The record it is about (receipt id, bill id ...).</summary>
        public string? EntityId { get; set; }
        /// <summary>The original request, kept so it can be carried out exactly as asked.</summary>
        public string PayloadJson { get; set; } = "{}";
        public string RequestedById { get; set; } = "";
        public string RequestedByName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DecidedAt { get; set; }
        /// <summary>What carrying it out returned (the credit note, the payment ...), or why it failed.</summary>
        public string? ResultJson { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>One approval or rejection on a request, by whom and when.</summary>
    public class ApprovalDecision
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid RequestId { get; set; }
        public int Level { get; set; }
        public string Role { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        /// <summary>Approved or Rejected.</summary>
        public string Decision { get; set; } = "";
        public string? Note { get; set; }
        public DateTime At { get; set; } = DateTime.UtcNow;
    }
}
