namespace Fdms.Domain.Entities
{
    /// <summary>
    /// A company's own WhatsApp number (Meta Cloud API). The company's staff use the FiscalHub assistant on it, its alerts go out from it, and (when switched on)
    /// its customers can message it to get their own fiscal invoices. Meta calls the company's own webhook address, so each number has its own verify token and app secret.
    /// </summary>
    public class WhatsAppAccount
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public bool Enabled { get; set; }
        /// <summary>Meta's id of the number (WhatsApp → API Setup), digits only. Unique: one number belongs to one company.</summary>
        public string? PhoneNumberId { get; set; }
        public string? BusinessAccountId { get; set; }
        /// <summary>The number people message, digits only with the country code.</summary>
        public string? DisplayNumber { get; set; }
        public string? AccessTokenProtected { get; set; }
        public string? AppSecretProtected { get; set; }
        public string VerifyToken { get; set; } = "";
        public string? AlertTemplate { get; set; }
        public string AlertLanguage { get; set; } = "en";
        public string ApiVersion { get; set; } = "v21.0";
        /// <summary>The company's customers may message the number to get copies of their own invoices.</summary>
        public bool CustomersEnabled { get; set; } = true;
        /// <summary>Optional first line customers see.</summary>
        public string? CustomerGreeting { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }
    }

    /// <summary>An SMS provider account: the platform's own (ClientId null) or a company's. A company's own account is used first; otherwise the platform's, when it allows companies.</summary>
    public class SmsAccount
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>Null = the platform's account.</summary>
        public Guid? ClientId { get; set; }
        public bool Enabled { get; set; }
        /// <summary>NHUME, TWILIO, ESOLUTIONS, BULKSMS, AFRICASTALKING or CUSTOM.</summary>
        public string Provider { get; set; } = "NHUME";
        /// <summary>The provider's address, when it has none built in (eSolutions, Custom) or to override the default.</summary>
        public string? ApiUrl { get; set; }
        /// <summary>User name, Twilio account SID, Africa's Talking user name.</summary>
        public string? Username { get; set; }
        /// <summary>API key / bearer token (encrypted).</summary>
        public string? ApiKeyProtected { get; set; }
        /// <summary>Password / auth token (encrypted).</summary>
        public string? ApiSecretProtected { get; set; }
        /// <summary>The sender ID or number messages come from.</summary>
        public string? SenderId { get; set; }
        /// <summary>Platform account only: companies without their own account may send through it.</summary>
        public bool AllowCompanies { get; set; } = true;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? UpdatedBy { get; set; }
    }

    /// <summary>Every SMS sent or waiting to be sent: invoices to customers, alerts to staff and sign-in codes. Alerts are queued and retried.</summary>
    public class SmsMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>The company it was sent for (null = platform).</summary>
        public Guid? ClientId { get; set; }
        /// <summary>The provider account that sends it.</summary>
        public Guid? AccountId { get; set; }
        public string? UserId { get; set; }
        /// <summary>Digits only with the country code.</summary>
        public string To { get; set; } = "";
        /// <summary>The text; a code is stored masked.</summary>
        public string Body { get; set; } = "";
        /// <summary>invoice, alert, code or test.</summary>
        public string Kind { get; set; } = "alert";
        /// <summary>Pending, Sent or Failed.</summary>
        public string Status { get; set; } = "Pending";
        public string? ProviderMessageId { get; set; }
        public string? Error { get; set; }
        public int Attempts { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
        public DateTime? SentAt { get; set; }
    }
}
