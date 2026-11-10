namespace Fdms.Application.Abstractions
{
    /// <summary>A part of the product a plan can include or leave out: the menus it shows and the API paths it opens.</summary>
    public sealed record PlanModuleDto(string Key, string Label, string Description, IReadOnlyList<string> MenuSlugs, IReadOnlyList<string> ApiPaths);

    /// <summary>
    /// The optional modules. A plan lists the ones it includes (Plan.Modules); a plan that never had a list (made before modules existed) keeps all of them.
    /// Fiscalising itself (devices, receipts, fiscal days, customers, items, reports, billing, users) is never optional.
    /// </summary>
    public static class PlanModules
    {
        public static readonly IReadOnlyList<PlanModuleDto> All = new PlanModuleDto[]
        {
            new("accounting", "Accounting", "Journals, ledger, cashbook, bank reconciliation, bills, suppliers, receivables, assets, VAT return, budgets and documents.",
                new[] { "accounting/" }, new[] { "/api/v1/accounting" }),
            new("inventory", "Inventory", "Stock items, categories, locations, GRVs, transfers, stock takes, stock reports and imports, all linked to the books.", new[] { "inventory/" }, new[] { "/api/v1/inventory" }),
            new("templates", "Invoice designer", "Custom invoice templates and the free-layout invoice builder.", new[] { "admin/templates" }, new[] { "/api/v1/templates" }),
            new("approvals", "Approvals", "Maker-checker approval rules for credit notes, bills and journals.", new[] { "approvals" }, new[] { "/api/v1/approvals" }),
            new("quotes", "Quotes and recurring invoices", "Quotes that turn into fiscal invoices, and invoices issued on a schedule.", new[] { "quotes", "recurring" }, new[] { "/api/v1/quotes", "/api/v1/recurring" }),
            new("pos", "Quick sale (POS)", "The point-of-sale screen for fast counter sales.", new[] { "pos" }, Array.Empty<string>()),
            new("ai", "AI reports", "Ask questions about your sales in plain words.", new[] { "ai-reports" }, new[] { "/api/v1/ai" }),
            new("api", "API tokens and webhooks", "Connect your own software: device API tokens and webhooks.", new[] { "tokens", "webhooks" }, new[] { "/api/v1/tokens", "/api/v1/webhook" }),
            new("messaging", "WhatsApp and SMS", "The WhatsApp assistant, the company's own WhatsApp number and SMS account.", new[] { "messaging" }, new[] { "/api/v1/messaging", "/api/v1/profile/whatsapp" }),
            new("connectors", "Accounting connectors", "QuickBooks Online, Xero, Sage Accounting and Odoo (each also applied for and approved on its own).", new[] { "connectors" }, new[] { "/api/v1/integrations/connectors" }),
            new("zoho", "Zoho Books", "The Zoho Books integration and its API for your own system.", new[] { "integrations" }, new[] { "/api/v1/integrations", "/api/v1/bridge/zoho" }),
        };

        public static PlanModuleDto? Find(string key) => All.FirstOrDefault(m => m.Key == key);

        /// <summary>The module a menu entry belongs to, or null when it is part of the core product.</summary>
        public static PlanModuleDto? ForSlug(string slug) => All.FirstOrDefault(m => m.MenuSlugs.Any(s => s.EndsWith('/') ? slug.StartsWith(s, StringComparison.OrdinalIgnoreCase) : string.Equals(s, slug, StringComparison.OrdinalIgnoreCase)));

        /// <summary>The module an API path belongs to (whole path segments, case-insensitive), or null. Device tokens (/devices/{id}/tokens) belong to "api".</summary>
        public static PlanModuleDto? ForPath(string path)
        {
            var p = path.TrimEnd('/');
            // applying for an integration is open to every plan: the product itself is gated when it is used
            if (p.Equals("/api/v1/integrations/access", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/api/v1/integrations/access/", StringComparison.OrdinalIgnoreCase)) return null;
            var hit = All.FirstOrDefault(m => m.ApiPaths.Any(a => p.Equals(a, StringComparison.OrdinalIgnoreCase) || p.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)));
            if (hit != null) return hit;
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 5 && parts[2].Equals("devices", StringComparison.OrdinalIgnoreCase) && parts[4].Equals("tokens", StringComparison.OrdinalIgnoreCase) ? Find("api") : null;
        }

        /// <summary>Null (never set: plans from before modules existed) = every module; otherwise exactly the listed ones, possibly none (core only).</summary>
        public static IReadOnlySet<string>? Parse(string? csv) =>
            csv == null ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => Find(k) != null).ToHashSet();

        public static bool Includes(IReadOnlySet<string>? modules, string key) => modules == null || modules.Contains(key);
    }
}
