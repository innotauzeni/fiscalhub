namespace Fdms.Domain.Entities
{
    /// <summary>A passkey (WebAuthn credential) a person registered: a fingerprint, face, PIN or security key that signs them in without a password.</summary>
    public class Passkey
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string UserId { get; set; } = "";
        /// <summary>The authenticator's credential id, base64url.</summary>
        public string CredentialId { get; set; } = "";
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        public long SignCount { get; set; }
        public string Name { get; set; } = "Passkey";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastUsedAt { get; set; }
    }
}
