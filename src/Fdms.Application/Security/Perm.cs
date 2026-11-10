namespace Fdms.Application.Security
{
    /// <summary>Permission names. Controllers use [HasPermission(Perm.X)]; the database holds the same names (table Permission).</summary>
    public static class Perm
    {
        public const string ClientsManage = "clients.manage";
        public const string UsersManage = "users.manage";
        public const string RolesManage = "roles.manage";
        public const string ModulesManage = "modules.manage";
        public const string AuditView = "audit.view";

        public const string DevicesView = "devices.view";
        public const string DevicesManage = "devices.manage";
        public const string DayView = "day.view";
        public const string DayManage = "day.manage";
        public const string ReceiptsView = "receipts.view";
        public const string ReceiptsSubmit = "receipts.submit";
        public const string ReceiptsImport = "receipts.import";
        public const string OfflineManage = "offline.manage";
        public const string StockView = "stock.view";
        public const string TokensManage = "tokens.manage";
        public const string BranchesManage = "branches.manage";
        public const string TemplatesManage = "templates.manage";
        public const string LogsView = "logs.view";
        public const string LogsManage = "logs.manage";
        public const string SettingsManage = "settings.manage";
        public const string PlansManage = "plans.manage";
        public const string ApplicationsManage = "applications.manage";
        public const string PaymentsManage = "payments.manage";
        public const string BillingManage = "billing.manage";
        public const string WebhooksManage = "webhooks.manage";
        public const string CustomersManage = "customers.manage";
        public const string ProductsManage = "products.manage";
        public const string ReportsView = "reports.view";
        public const string ComplianceView = "compliance.view";
        public const string ActivityView = "activity.view";
        public const string BackupsManage = "backups.manage";
        public const string QuotesManage = "quotes.manage";
        public const string RecurringManage = "recurring.manage";
        public const string AlertsManage = "alerts.manage";
        public const string ReportsAi = "reports.ai";
        public const string BanksManage = "banks.manage";
        public const string AccountingView = "accounting.view";
        public const string AccountingManage = "accounting.manage";
        public const string InventoryView = "inventory.view";
        public const string InventoryManage = "inventory.manage";
        public const string CurrenciesManage = "currencies.manage";
        public const string AnalyticsView = "analytics.view";
        public const string PartnersManage = "partners.manage";
        public const string PartnerPortal = "partner.portal";
        public const string ReferralsView = "referrals.view";
        public const string ApprovalsView = "approvals.view";
        public const string ApprovalsManage = "approvals.manage";
        public const string RevenueView = "revenue.view";
        public const string NotificationsView = "notifications.view";
        public const string IntegrationsManage = "integrations.manage";
        public const string IntegrationsMonitor = "integrations.monitor";
        /// <summary>Carried only by a partner's bridge key (never by a person): reach shared companies' Zoho Books.</summary>
        public const string BridgeUse = "bridge.use";

        public sealed record Def(string Name, string DisplayName, string Module);

        public static readonly IReadOnlyList<Def> All = new List<Def>
        {
            new(ClientsManage, "Manage clients (companies)", "Administration"),
            new(UsersManage, "Manage users", "Administration"),
            new(RolesManage, "Manage roles and access", "Administration"),
            new(ModulesManage, "Manage menus and modules", "Administration"),
            new(AuditView, "View audit log", "Administration"),
            new(DevicesView, "View devices", "Devices"),
            new(DevicesManage, "Register and manage devices", "Devices"),
            new(DayView, "View fiscal days", "Fiscal days"),
            new(DayManage, "Open and close fiscal days", "Fiscal days"),
            new(ReceiptsView, "View receipts", "Receipts"),
            new(ReceiptsSubmit, "Issue, release, cancel and retry receipts", "Receipts"),
            new(ReceiptsImport, "Import invoices from Excel or PDF", "Receipts"),
            new(OfflineManage, "Manage offline files", "Offline mode"),
            new(StockView, "View stock held at ZIMRA", "Devices"),
            new(TokensManage, "Create and revoke API tokens", "Integration"),
            new(BranchesManage, "Manage branches and who can use which devices", "Administration"),
            new(TemplatesManage, "Customise invoice templates", "Administration"),
            new(LogsView, "View system error logs", "Administration"),
            new(LogsManage, "Resolve and delete system error logs", "Administration"),
            new(SettingsManage, "Change platform settings (public API address, billing details)", "Administration"),
            new(PlansManage, "Manage subscription plans and prices", "Billing"),
            new(ApplicationsManage, "Review and approve client registrations", "Billing"),
            new(PaymentsManage, "Confirm payments and manage subscriptions", "Billing"),
            new(BillingManage, "View the company's plan and invoices and pay", "Billing"),
            new(WebhooksManage, "Manage webhooks", "Integration"),
            new(CustomersManage, "Manage the customer (buyer) register", "Receipts"),
            new(ProductsManage, "Manage the item catalogue (HS codes and tax)", "Receipts"),
            new(ReportsView, "View sales and VAT reports", "Reports"),
            new(ComplianceView, "View the compliance centre", "Reports"),
            new(QuotesManage, "Create and send quotes, and turn them into invoices", "Receipts"),
            new(RecurringManage, "Set up recurring invoices", "Receipts"),
            new(ReportsAi, "Build reports by asking the AI", "Reports"),
            new(BanksManage, "Manage the company's banks and bank accounts", "Administration"),
            new(AccountingView, "See the books: journals, ledger, trial balance, profit and loss, balance sheet, cashbook", "Accounting"),
            new(AccountingManage, "Keep the books: chart of accounts, journals, cashbook entries and bank reconciliation", "Accounting"),
            new(InventoryView, "See stock: items, quantities and values, GRVs, transfers, stock takes and stock reports", "Inventory"),
            new(InventoryManage, "Keep stock: items, categories, locations, GRVs, transfers, adjustments, stock takes and imports", "Inventory"),
            new(CurrenciesManage, "Manage the list of currencies", "Administration"),
            new(AnalyticsView, "See how the platform is used and which companies are at risk", "Administration"),
            new(PartnersManage, "Manage partners (resellers), their staff, their companies and what they earn", "Administration"),
            new(ReferralsView, "See your referral code, the companies you referred and what you earned", "Administration"),
            new(ApprovalsView, "See the approvals inbox and approve or reject what waits for your role", "Administration"),
            new(ApprovalsManage, "Set up the approval matrix: which actions, from which amount, need whose approval", "Administration"),
            new(RevenueView, "See the platform's revenue: recurring revenue, collections, outstanding, plans and paying companies", "Administration"),
            new(NotificationsView, "See the notification bell: alerts, invoices to pay, approvals and (for the platform) new registrations and payments", "Administration"),
            new(PartnerPortal, "Partner portal: the partner's own companies and commission", "Partner"),
            new(AlertsManage, "Choose who is alerted and about what", "Administration"),
            new(BackupsManage, "Create, download and delete database backups", "Administration"),
            new(ActivityView, "View activity: who and which systems did what on the devices", "Reports"),
            new(IntegrationsManage, "Connect accounting systems (Zoho Books) and fiscalise their invoices", "Integration"),
            new(BridgeUse, "Zoho bridge key: reach the Zoho Books of companies that shared it (keys only, never people)", "Integration"),
            new(IntegrationsMonitor, "Watch every company's accounting connections: tokens, syncs, failures and what they earn", "Administration"),
        };

        /// <summary>
        /// The only permissions an API token may carry (integration work on its own device). Registering devices, tokens, webhooks and
        /// every administration right stay with signed-in users.
        /// </summary>
        public static readonly IReadOnlyList<string> TokenScopes = new[]
        {
            DevicesView, DayView, DayManage, ReceiptsView, ReceiptsSubmit, ReceiptsImport, OfflineManage, StockView,
        };

        /// <summary>
        /// Rights that belong to the platform operator only. A company (client-level) role can never hold them, however the roles are edited: the seeder strips them
        /// at start-up, the role editor refuses them, and the audit log is additionally closed to anyone who belongs to a company.
        /// </summary>
        public static readonly IReadOnlySet<string> PlatformOnly = new HashSet<string>
        {
            RevenueView, ClientsManage, RolesManage, ModulesManage, AuditView, LogsView, LogsManage, SettingsManage, PlansManage, ApplicationsManage, PaymentsManage, BackupsManage, CurrenciesManage, AnalyticsView, PartnersManage, PartnerPortal, IntegrationsMonitor, BridgeUse,
        };

        /// <summary>Default grants for the seeded roles.</summary>
        public static readonly IReadOnlyDictionary<string, string[]> RoleDefaults = new Dictionary<string, string[]>
        {
            ["SuperAdmin"] = new[] { AccountingView, AccountingManage, NotificationsView, IntegrationsMonitor, RevenueView, AnalyticsView, PartnersManage, CurrenciesManage, BackupsManage, ActivityView, ClientsManage, UsersManage, RolesManage, ModulesManage, AuditView, LogsView, LogsManage, SettingsManage, PlansManage, ApplicationsManage, PaymentsManage },
            ["ClientAdmin"] = new[] { InventoryView, InventoryManage, NotificationsView, IntegrationsManage, ApprovalsView, ApprovalsManage, ReferralsView, AccountingView, AccountingManage, BanksManage, ReportsAi, QuotesManage, RecurringManage, AlertsManage, BillingManage, BranchesManage, TemplatesManage, UsersManage, DevicesView, DevicesManage, DayView, DayManage, ReceiptsView, ReceiptsSubmit, ReceiptsImport, OfflineManage, StockView, TokensManage, WebhooksManage, CustomersManage, ProductsManage, ReportsView, ComplianceView, ActivityView },
            ["Operator"] = new[] { InventoryView, InventoryManage, NotificationsView, ApprovalsView, ReportsAi, QuotesManage, DevicesView, DayView, DayManage, ReceiptsView, ReceiptsSubmit, ReceiptsImport, OfflineManage, StockView, CustomersManage, ProductsManage, ReportsView, ComplianceView },
            ["Viewer"] = new[] { InventoryView, NotificationsView, DevicesView, DayView, ReceiptsView, ReportsView },
            // keeps the books (and sees the receipts and reports behind them) without being able to issue receipts, run fiscal days or manage people and devices
            ["Accountant"] = new[] { InventoryView, InventoryManage, NotificationsView, ApprovalsView, AccountingView, AccountingManage, BanksManage, DevicesView, ReceiptsView, ReportsView, ComplianceView, CustomersManage },
            // a reseller or accounting firm: sees only the companies it brought and what it has earned
            ["Partner"] = new[] { NotificationsView, PartnerPortal },
        };

        public const string ClaimType = "permission";
    }

    public static class RoleNames
    {
        public const string SuperAdmin = "SuperAdmin";
        public const string ClientAdmin = "ClientAdmin";
        public const string Operator = "Operator";
        public const string Viewer = "Viewer";
        public const string Accountant = "Accountant";
        public const string Partner = "Partner";
    }
}
