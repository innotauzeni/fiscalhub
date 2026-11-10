namespace Fdms.Application.Abstractions
{
    /// <summary>
    /// An accounting product a company can connect, each sold separately on the same model (apply, approve with a set-up fee, pay, then a monthly fee and documents).
    /// ConnectionProvider: the value in IntegrationConnection.Provider. Auth: oauth (sign in at the provider) or apikey (Odoo: the company's own address, database, user and API key).
    /// </summary>
    public sealed record IntegrationProductDto(string Key, string Label, string ConnectionProvider, string Page, string Auth);

    public static class IntegrationProducts
    {
        public const string Zoho = "zoho", QuickBooks = "quickbooks", Xero = "xero", Sage = "sage", Odoo = "odoo";

        public static readonly IReadOnlyList<IntegrationProductDto> All = new IntegrationProductDto[]
        {
            new(Zoho, "Zoho Books", "zoho-books", "/integrations", "oauth"),
            new(QuickBooks, "QuickBooks Online", QuickBooks, "/connectors?product=quickbooks", "oauth"),
            new(Xero, "Xero", Xero, "/connectors?product=xero", "oauth"),
            new(Sage, "Sage Accounting", Sage, "/connectors?product=sage", "oauth"),
            new(Odoo, "Odoo", Odoo, "/connectors?product=odoo", "apikey"),
        };

        /// <summary>The products other than Zoho, which run on the shared connector engine.</summary>
        public static IEnumerable<IntegrationProductDto> Connectors => All.Where(p => p.Key != Zoho);

        public static IntegrationProductDto? Find(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        public static IntegrationProductDto Get(string? key) => Find(key ?? Zoho) ?? throw GatewayException.BadRequest("PRODUCT_INVALID", "Unknown accounting product.");
        public static string Label(string? key) => Find(key)?.Label ?? "Zoho Books";
    }
}
