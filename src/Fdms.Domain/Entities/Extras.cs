namespace Fdms.Domain.Entities
{
    /// <summary>Something the company should know about (a certificate expiring, a day that would not close, a rejected receipt...). Shown under the bell and emailed.</summary>
    public class Notification
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        /// <summary>certificate, dayclose, rejected, queued, usage, daylong ... (also what a company can switch on or off).</summary>
        public string Kind { get; set; } = "";
        /// <summary>info, warn or bad.</summary>
        public string Severity { get; set; } = "info";
        public string Title { get; set; } = "";
        public string? Body { get; set; }
        /// <summary>Console route that fixes it.</summary>
        public string? Link { get; set; }
        /// <summary>The same condition is raised once: e.g. "cert:{deviceId}:2026-10". Unique per company.</summary>
        public string Key { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ReadAt { get; set; }
        public DateTime? EmailedAt { get; set; }
    }

    /// <summary>
    /// An email the mail server could not take yet ("try later", unreachable). It is kept and tried again on a schedule, like a webhook delivery, instead of being lost.
    /// </summary>
    public class EmailOutbox
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? ClientId { get; set; }
        public string To { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Html { get; set; } = "";
        /// <summary>Attached files as JSON (name, content type, base64).</summary>
        public string? AttachmentsJson { get; set; }
        /// <summary>Pending, Sent, Failed (gave up) or Cancelled.</summary>
        public string Status { get; set; } = "Pending";
        public int Attempts { get; set; }
        public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
        public string? LastError { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? SentAt { get; set; }
    }

    /// <summary>A report the company built (or asked the AI for) and kept, so it can be run again, with fresh dates, without the AI.</summary>
    public class SavedReport
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Name { get; set; } = "";
        public string? Question { get; set; }
        public string SpecJson { get; set; } = "{}";
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>One use of the AI (a chat turn, a report question, a summary): what keeps the daily allowance honest and shows the operator what it costs.</summary>
    public class AiUsageLog
    {
        public long Id { get; set; }
        /// <summary>chat, report, summary or test.</summary>
        public string Kind { get; set; } = "";
        public Guid? ClientId { get; set; }
        public string? IpAddress { get; set; }
        public bool Ok { get; set; } = true;
        public DateTime At { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A price quotation that can be sent to a customer and turned into a fiscal invoice when accepted.</summary>
    public class Quote
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Number { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string? CustomerTin { get; set; }
        public string? CustomerVat { get; set; }
        public string? CustomerEmail { get; set; }
        public string? CustomerPhone { get; set; }
        public string Currency { get; set; } = "USD";
        public bool TaxInclusive { get; set; } = true;
        public DateTime? ValidUntil { get; set; }
        public string? Notes { get; set; }
        /// <summary>Draft, Sent, Accepted, Declined or Invoiced.</summary>
        public string Status { get; set; } = "Draft";
        /// <summary>The lines as JSON (name, hsCode, quantity, unitPrice, taxPercent, isExempt).</summary>
        public string LinesJson { get; set; } = "[]";
        public Guid? InvoiceReceiptId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>An invoice that is issued again on a schedule (rent, subscriptions, retainers). Each run fiscalises a normal invoice on the chosen device.</summary>
    public class RecurringInvoice
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public Guid DeviceId { get; set; }
        public string Name { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string? CustomerTin { get; set; }
        public string? CustomerVat { get; set; }
        public string? CustomerEmail { get; set; }
        public string? CustomerPhone { get; set; }
        public string Currency { get; set; } = "USD";
        public bool TaxInclusive { get; set; } = true;
        public string PaymentMethod { get; set; } = "Cash";
        public string? Notes { get; set; }
        public string LinesJson { get; set; } = "[]";
        /// <summary>Weekly, Monthly, Quarterly or Yearly.</summary>
        public string Frequency { get; set; } = "Monthly";
        /// <summary>The next date (Zimbabwe time) an invoice is due.</summary>
        public DateTime NextRunDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsActive { get; set; } = true;
        /// <summary>Printed before the running number, e.g. "RENT-" gives RENT-0001.</summary>
        public string Prefix { get; set; } = "REC-";
        public int NextNumber { get; set; } = 1;
        /// <summary>Email the fiscal invoice to CustomerEmail after each run.</summary>
        public bool EmailCustomer { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>One attempt to issue a recurring invoice for one due date.</summary>
    public class RecurringRun
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid RecurringInvoiceId { get; set; }
        public Guid ClientId { get; set; }
        public DateTime RunDate { get; set; }
        public Guid? ReceiptId { get; set; }
        public string? InvoiceNo { get; set; }
        /// <summary>Ok or Failed.</summary>
        public string Status { get; set; } = "Ok";
        public string? Error { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
