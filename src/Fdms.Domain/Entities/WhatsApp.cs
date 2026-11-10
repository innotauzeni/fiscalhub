namespace Fdms.Domain.Entities
{
    /// <summary>Where a person is in a conversation with the WhatsApp bot (one per WhatsApp number), and what they have entered so far.</summary>
    public class WhatsAppSession
    {
        /// <summary>Which number the conversation is with: "" = the platform's number, otherwise the company WhatsApp account's id ("N" format).</summary>
        public string Line { get; set; } = "";
        /// <summary>Digits only, with the country code.</summary>
        public string Phone { get; set; } = "";
        public string? UserId { get; set; }
        public Guid? ClientId { get; set; }
        public string State { get; set; } = "menu";
        /// <summary>The invoice being built, the chosen device, the last list shown (JSON).</summary>
        public string DataJson { get; set; } = "{}";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>The person's last message: WhatsApp lets the bot write freely for 24 hours after it; later only approved templates.</summary>
        public DateTime? LastInboundAt { get; set; }
    }

    /// <summary>
    /// Every message to and from the WhatsApp bot. Incoming messages are stored first and answered by a background worker (so WhatsApp gets its answer at once);
    /// outgoing alerts wait here until they are sent, and are retried.
    /// </summary>
    public class WhatsAppMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>in or out.</summary>
        public string Direction { get; set; } = "in";
        public string Phone { get; set; } = "";
        public string? UserId { get; set; }
        public Guid? ClientId { get; set; }
        /// <summary>The company WhatsApp account it came in on or goes out from; null = the platform's number.</summary>
        public Guid? AccountId { get; set; }
        /// <summary>WhatsApp's id of the message (incoming: used to ignore repeats).</summary>
        public string? WaMessageId { get; set; }
        /// <summary>text, button, list, document, template or alert.</summary>
        public string Kind { get; set; } = "text";
        /// <summary>What was said, shortened, for the log.</summary>
        public string? Body { get; set; }
        /// <summary>For a reply chosen from buttons or a list: the option id.</summary>
        public string? ReplyId { get; set; }
        /// <summary>Received, Processed, Ignored, Pending (to send), Sent or Failed.</summary>
        public string Status { get; set; } = "Received";
        public int Attempts { get; set; }
        public string? Error { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAt { get; set; }
    }
}
