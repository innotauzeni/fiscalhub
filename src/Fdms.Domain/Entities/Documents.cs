namespace Fdms.Domain.Entities
{
    /// <summary>
    /// A file the company keeps with its books: a supplier invoice, a contract, a tax certificate. It can be attached to a bill, an asset or a supplier. Records must be kept for a
    /// number of years (RetainUntil), and until then the file cannot be deleted.
    /// </summary>
    public class StoredDocument
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ClientId { get; set; }
        public string Title { get; set; } = "";
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "application/octet-stream";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
        public byte[] Data { get; set; } = Array.Empty<byte>();
        /// <summary>Supplier invoice, Contract, Tax, Bank, Payroll or Other.</summary>
        public string Category { get; set; } = "Other";
        /// <summary>none, bill, asset or supplier.</summary>
        public string LinkType { get; set; } = "none";
        public Guid? LinkId { get; set; }
        public string? Notes { get; set; }
        public DateTime RetainUntil { get; set; }
        public string? UploadedBy { get; set; }
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    }
}
