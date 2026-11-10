namespace Fdms.Application.Fiscal
{
    /// <summary>
    /// FDMS wants local Zimbabwe time (CAT, UTC+2, no DST) without a zone. Hosting servers are often on another
    /// clock, which causes RCPT014/RCPT031, so every FDMS timestamp comes from here.
    /// </summary>
    public static class FdmsClock
    {
        /// <summary>
        /// Zimbabwe time (CAT, UTC+2, no daylight saving) taken from the UTC clock, so it never depends on the server's own timezone.
        /// The kind is Unspecified on purpose: it is a wall-clock reading, and must never be mistaken for UTC when it is serialised.
        /// </summary>
        public static DateTime CatNow() => DateTime.SpecifyKind(DateTime.UtcNow.AddHours(2), DateTimeKind.Unspecified);
    }
}
