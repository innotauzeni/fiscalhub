using Fdms.Application.Security;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fdms.Infrastructure.Auth
{
    /// <summary>
    /// Creates the roles, permissions, menus and default grants on startup, and the first SuperAdmin. Idempotent: it only adds what is
    /// missing and gives NEW permissions/menus/roles their default grants, so an operator's later changes in "Roles &amp; access" survive restarts.
    /// </summary>
    public static class DatabaseSeeder
    {
        private sealed record SectionDef(string Name, string Color, int Sort, ModuleDef[] Modules);
        private sealed record ModuleDef(string Name, string Slug, string Icon, string? Access, int Sort, SubDef[] Subs);
        private sealed record SubDef(string Name, string Icon, string Route, string? Access);

        private static readonly SectionDef[] Sections =
        {
            new("Fiscalisation", "#2563eb", 1, new ModuleDef[]
            {
                new("Dashboard", "dashboard", "home", Perm.DevicesView, 1, Array.Empty<SubDef>()),
                new("Devices", "devices", "cpu", Perm.DevicesView, 2, new SubDef[]
                {
                    new("All devices", "cpu", "devices", Perm.DevicesView),
                    new("Register device", "plus", "devices/register", Perm.DevicesManage),
                }),
                new("Receipts", "receipts", "receipt", Perm.ReceiptsView, 3, new SubDef[]
                {
                    new("All receipts", "receipt", "receipts", Perm.ReceiptsView),
                    new("New invoice", "plus", "receipts/new", Perm.ReceiptsSubmit),
                    new("Import Excel / PDF", "upload", "receipts/import", Perm.ReceiptsImport),
                }),
                new("Fiscal days", "days", "calendar", Perm.DayView, 4, Array.Empty<SubDef>()),
                new("Offline files", "offline", "cloud-upload", Perm.DayView, 5, Array.Empty<SubDef>()),
                new("Customers", "customers", "users", Perm.CustomersManage, 6, Array.Empty<SubDef>()),
                new("Items", "products", "stack", Perm.ProductsManage, 7, Array.Empty<SubDef>()),
                new("Reports", "reports", "chart", Perm.ReportsView, 8, Array.Empty<SubDef>()),
                new("Compliance", "compliance", "shield", Perm.ComplianceView, 9, Array.Empty<SubDef>()),
                new("Quick sale", "pos", "bolt", Perm.ReceiptsSubmit, 10, Array.Empty<SubDef>()),
                new("Quotes", "quotes", "clipboard", Perm.QuotesManage, 11, Array.Empty<SubDef>()),
                new("Recurring invoices", "recurring", "refresh", Perm.RecurringManage, 12, Array.Empty<SubDef>()),
                new("Health", "health", "shield", Perm.DevicesView, 13, Array.Empty<SubDef>()),
                new("Alerts", "alerts", "alert", Perm.AlertsManage, 14, Array.Empty<SubDef>()),
                new("AI reports", "ai-reports", "chart", Perm.ReportsAi, 15, Array.Empty<SubDef>()),
            }),
            new("Accounting", "#0d9488", 4, new ModuleDef[]
            {
                new("Journals", "accounting/journals", "clipboard", Perm.AccountingView, 1, Array.Empty<SubDef>()),
                new("Chart of accounts", "accounting/accounts", "stack", Perm.AccountingView, 2, Array.Empty<SubDef>()),
                new("Cashbook", "accounting/cashbook", "card", Perm.AccountingView, 3, new SubDef[]
                {
                    new("Bank ledger", "card", "accounting/cashbook", Perm.AccountingView),
                    new("Spend money", "send", "accounting/spend-money", Perm.AccountingView),
                    new("Receive money", "download", "accounting/receive-money", Perm.AccountingView),
                    new("Bank transfers", "repeat", "accounting/transfers", Perm.AccountingView),
                    new("Post to ledger", "check", "accounting/post-to-ledger", Perm.AccountingView),
                }),
                new("Bank reconciliation", "accounting/reconcile", "check", Perm.AccountingManage, 4, Array.Empty<SubDef>()),
                new("Financial reports", "accounting/reports", "chart", Perm.AccountingView, 5, new SubDef[]
                {
                    new("General ledger", "clipboard", "accounting/reports/ledger", Perm.AccountingView),
                    new("Trial balance", "layers", "accounting/reports/trial-balance", Perm.AccountingView),
                    new("Profit and loss", "chart", "accounting/reports/profit-loss", Perm.AccountingView),
                    new("Balance sheet", "squares", "accounting/reports/balance-sheet", Perm.AccountingView),
                    new("Payables ageing", "calendar", "accounting/reports/payables", Perm.AccountingView),
                    new("Receivables ageing", "calendar", "accounting/reports/receivables", Perm.AccountingView),
                    new("Cash flow", "pulse", "accounting/reports/cash-flow", Perm.AccountingView),
                    new("Profit by cost centre", "squares", "accounting/reports/profit-loss-centres", Perm.AccountingView),
                    new("Changes in equity", "layers", "accounting/reports/changes-in-equity", Perm.AccountingView),
                    new("Financial statements", "file-text", "accounting/reports/financial-statements", Perm.AccountingView),
                }),
                new("Financial dashboard", "accounting/dashboard", "chart", Perm.AccountingView, 0, Array.Empty<SubDef>()),
                new("Cost centres", "accounting/cost-centres", "folder", Perm.AccountingView, 13, Array.Empty<SubDef>()),
                new("Recurring journals", "accounting/recurring", "repeat", Perm.AccountingView, 14, Array.Empty<SubDef>()),
                new("Year end", "accounting/year-end", "lock", Perm.AccountingView, 15, Array.Empty<SubDef>()),
                new("Sales invoices", "accounting/sales", "receipt", Perm.AccountingView, 1, Array.Empty<SubDef>()),
                new("Import balances", "accounting/import", "upload", Perm.AccountingManage, 16, Array.Empty<SubDef>()),
                new("Exchange rates", "accounting/rates", "repeat", Perm.AccountingView, 17, Array.Empty<SubDef>()),
                new("Prepayments and deferrals", "accounting/schedules", "calendar", Perm.AccountingView, 18, Array.Empty<SubDef>()),
                new("Audit trail", "accounting/audit", "shield", Perm.AccountingView, 19, Array.Empty<SubDef>()),
                new("Customers owing", "accounting/receivables", "user-check", Perm.AccountingView, 8, Array.Empty<SubDef>()),
                new("Fixed assets", "accounting/assets", "cpu", Perm.AccountingView, 9, Array.Empty<SubDef>()),
                new("VAT return", "accounting/vat", "shield", Perm.AccountingView, 10, Array.Empty<SubDef>()),
                new("Budgets", "accounting/budgets", "chart", Perm.AccountingView, 11, Array.Empty<SubDef>()),
                new("Documents", "accounting/documents", "file-text", Perm.AccountingView, 12, Array.Empty<SubDef>()),
                new("Suppliers", "accounting/suppliers", "users", Perm.AccountingView, 6, Array.Empty<SubDef>()),
                new("Bills", "accounting/bills", "file-text", Perm.AccountingView, 7, Array.Empty<SubDef>()),
            }),
            new("Inventory", "#b45309", 6, new ModuleDef[]
            {
                new("Stock items", "inventory/items", "cart", Perm.InventoryView, 1, Array.Empty<SubDef>()),
                new("Goods received (GRV)", "inventory/grv", "download", Perm.InventoryView, 2, Array.Empty<SubDef>()),
                new("Stock transfers", "inventory/transfers", "repeat", Perm.InventoryView, 3, Array.Empty<SubDef>()),
                new("Adjustments and stock takes", "inventory/adjustments", "clipboard", Perm.InventoryView, 4, Array.Empty<SubDef>()),
                new("Stock reports", "inventory/reports", "chart", Perm.InventoryView, 5, new SubDef[]
                {
                    new("On hand and valuation", "layers", "inventory/reports/on-hand", Perm.InventoryView),
                    new("Stock card", "clipboard", "inventory/reports/card", Perm.InventoryView),
                    new("Reorder list", "alert", "inventory/reports/reorder", Perm.InventoryView),
                }),
                new("Categories", "inventory/categories", "folder", Perm.InventoryView, 6, Array.Empty<SubDef>()),
                new("Locations", "inventory/locations", "building", Perm.InventoryView, 7, Array.Empty<SubDef>()),
                new("Import items", "inventory/import", "upload", Perm.InventoryManage, 8, Array.Empty<SubDef>()),
            }),
            new("Integration", "#7c3aed", 2, new ModuleDef[]
            {
                new("API tokens", "tokens", "key", Perm.TokensManage, 1, Array.Empty<SubDef>()),
                new("Webhooks", "webhooks", "bolt", Perm.WebhooksManage, 2, Array.Empty<SubDef>()),
                new("Activity", "activity", "chart", Perm.ActivityView, 3, Array.Empty<SubDef>()),
                new("Developer docs", "docs", "clipboard", null, 4, Array.Empty<SubDef>()),
                new("User guide", "guide", "file-text", null, 4, Array.Empty<SubDef>()),
                new("Zoho Books", "integrations", "cloud-upload", Perm.IntegrationsManage, 5, Array.Empty<SubDef>()),
                new("Integrations monitor", "admin/integrations", "chart", Perm.IntegrationsMonitor, 6, Array.Empty<SubDef>()),
                new("WhatsApp & SMS", "messaging", "message", Perm.IntegrationsManage, 7, Array.Empty<SubDef>()),
                new("Accounting connectors", "connectors", "cloud-upload", Perm.IntegrationsManage, 5, Array.Empty<SubDef>()),
            }),
            new("Administration", "#475569", 3, new ModuleDef[]
            {
                new("Overview", "overview", "chart", Perm.ClientsManage, 0, Array.Empty<SubDef>()),
                new("Clients", "admin/clients", "building", Perm.ClientsManage, 1, Array.Empty<SubDef>()),
                new("Branches", "admin/branches", "building", Perm.BranchesManage, 2, Array.Empty<SubDef>()),
                new("Banks", "admin/banks", "card", Perm.BanksManage, 2, Array.Empty<SubDef>()),
                new("Currencies", "admin/currencies", "layers", Perm.CurrenciesManage, 16, Array.Empty<SubDef>()),
                new("Users", "admin/users", "users", Perm.UsersManage, 2, Array.Empty<SubDef>()),
                new("Invoice templates", "admin/templates", "receipt", Perm.TemplatesManage, 3, Array.Empty<SubDef>()),
                new("Roles & access", "admin/roles", "shield", Perm.RolesManage, 3, Array.Empty<SubDef>()),
                new("Menus & modules", "admin/modules", "squares", Perm.ModulesManage, 4, Array.Empty<SubDef>()),
                new("Audit log", "admin/audit", "clipboard", Perm.AuditView, 5, Array.Empty<SubDef>()),
                new("System logs", "admin/logs", "alert", Perm.LogsView, 6, Array.Empty<SubDef>()),
                new("Backups", "admin/backups", "cloud-upload", Perm.BackupsManage, 14, Array.Empty<SubDef>()),
                new("AI assistant", "admin/ai", "bolt", Perm.SettingsManage, 15, Array.Empty<SubDef>()),
                new("Settings", "admin/settings", "toggle", Perm.SettingsManage, 7, Array.Empty<SubDef>()),
                new("Applications", "admin/applications", "user-check", Perm.ApplicationsManage, 8, Array.Empty<SubDef>()),
                new("Plans", "admin/plans", "layers", Perm.PlansManage, 9, Array.Empty<SubDef>()),
                new("Subscriptions", "admin/subscriptions", "chart", Perm.PaymentsManage, 10, Array.Empty<SubDef>()),
                new("Payments", "admin/payments", "card", Perm.PaymentsManage, 11, Array.Empty<SubDef>()),
                new("Billing", "billing", "card", Perm.BillingManage, 12, Array.Empty<SubDef>()),
                new("Go-live requests", "admin/go-live", "bolt", Perm.ApplicationsManage, 13, Array.Empty<SubDef>()),
                new("Usage analytics", "admin/analytics", "chart", Perm.AnalyticsView, 17, Array.Empty<SubDef>()),
                new("Partners", "admin/partners", "users", Perm.PartnersManage, 18, Array.Empty<SubDef>()),
                new("Referral program", "admin/referral-program", "bolt", Perm.PartnersManage, 19, Array.Empty<SubDef>()),
                new("Refer & earn", "referrals", "users", Perm.ReferralsView, 20, Array.Empty<SubDef>()),
                new("Approvals", "approvals", "check", Perm.ApprovalsView, 21, new SubDef[]
                {
                    new("Waiting for me", "check", "approvals", Perm.ApprovalsView),
                    new("Approval matrix", "squares", "approvals/matrix", Perm.ApprovalsView),
                }),
                new("Revenue", "admin/revenue", "chart", Perm.RevenueView, -1, Array.Empty<SubDef>()),
            }),
            new("Partner", "#0d9488", 5, new ModuleDef[]
            {
                new("Overview", "partner/overview", "chart", Perm.PartnerPortal, 1, Array.Empty<SubDef>()),
                new("Our companies", "partner/clients", "building", Perm.PartnerPortal, 2, Array.Empty<SubDef>()),
                new("Commission", "partner/commission", "card", Perm.PartnerPortal, 3, Array.Empty<SubDef>()),
                new("Zoho bridge", "partner/bridge", "link", Perm.PartnerPortal, 4, Array.Empty<SubDef>()),
            }),
        };

        /// <summary>Which menu entries each seeded role starts with (by slug).</summary>
        private static readonly Dictionary<string, string[]> RoleMenus = new()
        {
            [RoleNames.SuperAdmin] = new[] { "guide", "admin/integrations", "admin/revenue", "admin/referral-program", "admin/analytics", "admin/partners", "admin/currencies", "admin/ai", "admin/backups", "activity", "overview", "admin/clients", "admin/users", "admin/roles", "admin/modules", "admin/audit", "admin/logs", "admin/settings", "admin/applications", "admin/plans", "admin/subscriptions", "admin/payments", "admin/go-live", "accounting/dashboard", "accounting/journals", "accounting/accounts", "accounting/cost-centres", "accounting/recurring", "accounting/year-end", "accounting/cashbook", "accounting/reconcile", "accounting/reports", "accounting/suppliers", "accounting/bills", "accounting/receivables", "accounting/assets", "accounting/vat", "accounting/budgets", "accounting/documents", "accounting/sales", "accounting/import", "accounting/rates", "accounting/schedules", "accounting/audit" },
            [RoleNames.ClientAdmin] = new[] { "inventory/items", "inventory/grv", "inventory/transfers", "inventory/adjustments", "inventory/reports", "inventory/categories", "inventory/locations", "inventory/import", "guide", "integrations", "connectors", "messaging", "dashboard", "devices", "receipts", "days", "offline", "tokens", "webhooks", "activity", "docs", "accounting/dashboard", "accounting/journals", "accounting/accounts", "accounting/cost-centres", "accounting/recurring", "accounting/year-end", "accounting/cashbook", "accounting/reconcile", "accounting/reports", "accounting/suppliers", "accounting/bills", "accounting/receivables", "accounting/assets", "accounting/vat", "accounting/budgets", "accounting/documents", "accounting/sales", "accounting/import", "accounting/rates", "accounting/schedules", "accounting/audit", "referrals", "approvals", "admin/branches", "admin/banks", "admin/users", "admin/templates", "billing", "customers", "products", "reports", "compliance", "pos", "quotes", "recurring", "health", "alerts", "ai-reports" },
            [RoleNames.Operator] = new[] { "inventory/items", "inventory/grv", "inventory/transfers", "inventory/adjustments", "inventory/reports", "inventory/categories", "inventory/locations", "inventory/import", "guide", "approvals", "dashboard", "devices", "receipts", "days", "offline", "customers", "products", "reports", "compliance", "pos", "quotes", "health", "ai-reports" },
            [RoleNames.Partner] = new[] { "guide", "partner/overview", "partner/clients", "partner/commission", "partner/bridge" },
            [RoleNames.Viewer] = new[] { "inventory/items", "inventory/reports", "guide", "dashboard", "devices", "receipts", "days", "reports", "health" },
            [RoleNames.Accountant] = new[] { "inventory/items", "inventory/grv", "inventory/transfers", "inventory/adjustments", "inventory/reports", "inventory/categories", "inventory/locations", "inventory/import", "guide", "approvals", "dashboard", "receipts", "customers", "reports", "compliance", "admin/banks", "accounting/dashboard", "accounting/journals", "accounting/accounts", "accounting/cost-centres", "accounting/recurring", "accounting/year-end", "accounting/cashbook", "accounting/reconcile", "accounting/reports", "accounting/suppliers", "accounting/bills", "accounting/receivables", "accounting/assets", "accounting/vat", "accounting/budgets", "accounting/documents", "accounting/sales", "accounting/import", "accounting/rates", "accounting/schedules", "accounting/audit" },
        };

        private static readonly Dictionary<string, (string Level, string Description)> RoleInfo = new()
        {
            [RoleNames.SuperAdmin] = ("platform", "Platform operator: manages companies, users, roles and menus."),
            [RoleNames.ClientAdmin] = ("client", "Company administrator: devices, users, tokens, webhooks and all fiscal work."),
            [RoleNames.Operator] = ("client", "Issues receipts and runs fiscal days."),
            [RoleNames.Viewer] = ("client", "Read-only access to devices, fiscal days and receipts."),
            [RoleNames.Partner] = ("partner", "Reseller or accounting firm: sees the companies it brought and the commission it has earned."),
            [RoleNames.Accountant] = ("client", "Keeps the company's books: journals, cashbook, bank reconciliation, bills, VAT and budgets. Cannot issue receipts or manage devices and people."),
        };

        public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
        {
            using var scope = services.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<FdmsDbContext>();
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder");
            var cfg = sp.GetRequiredService<IConfiguration>();

            // ---- currencies the platform starts with (the operator edits the list afterwards; nothing here is ever overwritten) ----
            var haveCurrencies = await db.Currencies.Select(c => c.Code).ToListAsync(ct);
            var currencyOrder = 0;
            foreach (var (code, name, symbol) in global::Fdms.Infrastructure.Services.CurrencyService.Defaults)
            {
                currencyOrder++;
                if (!haveCurrencies.Contains(code)) db.Currencies.Add(new Currency { Code = code, Name = name, Symbol = symbol, SortOrder = currencyOrder });
            }
            await db.SaveChangesAsync(ct);

            // ---- permissions ----
            var existingPerms = await db.Permissions.ToDictionaryAsync(p => p.Name, ct);
            var newPerms = new HashSet<string>();
            foreach (var d in Perm.All)
            {
                if (existingPerms.TryGetValue(d.Name, out var p)) continue;
                db.Permissions.Add(new Permission { Name = d.Name, DisplayName = d.DisplayName, Module = d.Module });
                newPerms.Add(d.Name);
            }
            await db.SaveChangesAsync(ct);
            var permIds = await db.Permissions.ToDictionaryAsync(p => p.Name, p => p.Id, ct);

            // ---- roles ----
            var roleMgr = sp.GetRequiredService<RoleManager<Role>>();
            var newRoles = new HashSet<string>();
            foreach (var (name, (level, desc)) in RoleInfo)
            {
                if (await roleMgr.FindByNameAsync(name) != null) continue;
                var res = await roleMgr.CreateAsync(new Role { Name = name, Type = "system", Level = level, Description = desc });
                if (!res.Succeeded) throw new InvalidOperationException("Cannot create role " + name + ": " + string.Join("; ", res.Errors.Select(e => e.Description)));
                newRoles.Add(name);
            }
            var roleIds = await db.Roles.Where(r => RoleInfo.Keys.Contains(r.Name!)).ToDictionaryAsync(r => r.Name!, r => r.Id, ct);

            // ---- menus ----
            var newModuleSlugs = new HashSet<string>();
            foreach (var s in Sections)
            {
                var section = await db.SectionModules.FirstOrDefaultAsync(x => x.Name == s.Name, ct);
                if (section == null) { section = new SectionModule { Name = s.Name, Color = s.Color, SortOrder = s.Sort }; db.SectionModules.Add(section); await db.SaveChangesAsync(ct); }
                foreach (var m in s.Modules)
                {
                    var mod = await db.SystemModules.Include(x => x.Children).FirstOrDefaultAsync(x => x.Slugname == m.Slug, ct);
                    if (mod == null)
                    {
                        mod = new SystemModule { SectionModuleId = section.Id, Name = m.Name, Slugname = m.Slug, Icon = m.Icon, AccessName = m.Access, SortOrder = m.Sort };
                        db.SystemModules.Add(mod);
                        newModuleSlugs.Add(m.Slug);
                        await db.SaveChangesAsync(ct);
                    }
                    var order = 0;
                    foreach (var sub in m.Subs)
                    {
                        order++;
                        if (mod.Children.Any(c => c.Route == sub.Route)) continue;
                        db.Submodules.Add(new Submodule { ModuleId = mod.Id, Name = sub.Name, Icon = sub.Icon, Route = sub.Route, AccessName = sub.Access, Order = order });
                    }
                }
            }
            await db.SaveChangesAsync(ct);
            var moduleIds = await db.SystemModules.ToDictionaryAsync(m => m.Slugname, m => m.Id, ct);

            // ---- default grants, only for what is new ----
            var havePerm = (await db.RolePermissions.Select(rp => new { rp.RoleId, rp.PermissionId }).ToListAsync(ct)).Select(x => (x.RoleId, x.PermissionId)).ToHashSet();
            var haveMenu = (await db.RoleMenus.Select(rm => new { rm.RoleId, rm.SystemModuleId }).ToListAsync(ct)).Select(x => (x.RoleId, x.SystemModuleId)).ToHashSet();
            foreach (var (role, perms) in Perm.RoleDefaults)
                foreach (var p in perms)
                    if ((newRoles.Contains(role) || newPerms.Contains(p)) && havePerm.Add((roleIds[role], permIds[p])))
                        db.RolePermissions.Add(new RolePermission { RoleId = roleIds[role], PermissionId = permIds[p] });
            foreach (var (role, slugs) in RoleMenus)
                foreach (var slug in slugs)
                    if ((newRoles.Contains(role) || newModuleSlugs.Contains(slug)) && haveMenu.Add((roleIds[role], moduleIds[slug])))
                        db.RoleMenus.Add(new RoleMenu { RoleId = roleIds[role], SystemModuleId = moduleIds[slug] });
            await db.SaveChangesAsync(ct);

            // ---- once: the operator keeps the platform's own books (billing posts into them), so installs from before get the accounting rights and menus ----
            if (!await db.SystemSettings.AnyAsync(s => s.Key == "Seed.PlatformBooks", ct))
            {
                var superId = roleIds[RoleNames.SuperAdmin];
                foreach (var p in new[] { Perm.AccountingView, Perm.AccountingManage })
                    if (havePerm.Add((superId, permIds[p]))) db.RolePermissions.Add(new RolePermission { RoleId = superId, PermissionId = permIds[p] });
                foreach (var slug in RoleMenus[RoleNames.SuperAdmin].Where(s => s.StartsWith("accounting/")))
                    if (moduleIds.TryGetValue(slug, out var mid) && haveMenu.Add((superId, mid))) db.RoleMenus.Add(new RoleMenu { RoleId = superId, SystemModuleId = mid });
                db.SystemSettings.Add(new SystemSetting { Key = "Seed.PlatformBooks", Value = "done", UpdatedBy = "system" });
                await db.SaveChangesAsync(ct);
            }

            // ---- every company gets a referral code (refer-and-earn)
            {
                var rnd = Random.Shared; var taken = (await db.Clients.Where(c => c.ReferralCode != null).Select(c => c.ReferralCode!).ToListAsync(ct)).Concat(await db.Partners.Select(p => p.Code).ToListAsync(ct)).ToHashSet();
                foreach (var c in await db.Clients.Where(c => c.ReferralCode == null).ToListAsync(ct)) { string code; do { code = global::Fdms.Infrastructure.Services.ReferralService.NewCode(rnd); } while (!taken.Add(code)); c.ReferralCode = code; }
                await db.SaveChangesAsync(ct);
            }

            // ---- every company gets its own sign-in word (/login?company=...), including those created before it existed ----
            foreach (var c in await db.Clients.Where(c => c.Slug == null).ToListAsync(ct)) { c.Slug = await global::Fdms.Infrastructure.Services.BrandService.UniqueSlugAsync(db, c.Name, ct); await db.SaveChangesAsync(ct); }

            // ---- operator-only rights are stripped from every company role, every start: the audit log must never reach a company, whatever was clicked in the role editor ----
            var clientRoleIds = await db.Roles.Where(r => r.Level == "client").Select(r => r.Id).ToListAsync(ct);
            var platformNames = Perm.PlatformOnly.ToList();   // a plain list: EF cannot translate Contains on a set type
            var platformPermIds = await db.Permissions.Where(p => platformNames.Contains(p.Name)).Select(p => p.Id).ToListAsync(ct);
            var platformModuleIds = await db.SystemModules.Where(m => m.AccessName != null && platformNames.Contains(m.AccessName)).Select(m => m.Id).ToListAsync(ct);
            var strayPerms = await db.RolePermissions.Where(rp => clientRoleIds.Contains(rp.RoleId) && platformPermIds.Contains(rp.PermissionId)).ToListAsync(ct);
            var strayMenus = await db.RoleMenus.Where(rm => clientRoleIds.Contains(rm.RoleId) && platformModuleIds.Contains(rm.SystemModuleId)).ToListAsync(ct);
            if (strayPerms.Count + strayMenus.Count > 0)
            {
                db.RolePermissions.RemoveRange(strayPerms); db.RoleMenus.RemoveRange(strayMenus);
                db.AuditLogs.Add(new AuditLog { ActorType = "system", Action = "security.strip-platform-rights", Detail = $"removed {strayPerms.Count} permission(s) and {strayMenus.Count} menu entr(ies) that company roles may not hold" });
                await db.SaveChangesAsync(ct);
                log.LogWarning("Removed {P} operator-only permission(s) and {M} menu entr(ies) from company roles", strayPerms.Count, strayMenus.Count);
            }

            // ---- example plans, only when there are none: the operator edits the names, limits and PRICES in the console (these prices are placeholders) ----
            if (!await db.Plans.AnyAsync(ct))
            {
                db.Plans.AddRange(
                    new Plan { Code = "starter", Name = "Starter", SortOrder = 1, TrialDays = 14, PriceMonthly = 15, PriceYearly = 150, MaxDevices = 1, MaxUsers = 2, MaxBranches = 1, MaxReceiptsPerMonth = 500,
                        Description = "One till, one shop: everything you need to start fiscalising.", Features = "PDF and Excel import\nInvoice PDF with your logo\nEmail support" },
                    new Plan { Code = "business", Name = "Business", SortOrder = 2, TrialDays = 14, PriceMonthly = 45, PriceYearly = 450, MaxDevices = 5, MaxUsers = 10, MaxBranches = 5, MaxReceiptsPerMonth = 5000,
                        Description = "Several tills and branches, with per-branch access for your staff.", Features = "Per-branch access for your staff\nWebhooks and API tokens\nPriority support" },
                    new Plan { Code = "enterprise", Name = "Enterprise", SortOrder = 3, TrialDays = 14, PriceMonthly = 120, PriceYearly = 1200, MaxDevices = 0, MaxUsers = 0, MaxBranches = 0, MaxReceiptsPerMonth = 0,
                        Description = "Many branches and users, unlimited receipts.", Features = "Audit trail and system logs\nIntegration help\nDedicated support" });
                await db.SaveChangesAsync(ct);
            }

            // ---- the Zoho Books connector plan (for companies that only want the connector), added once; the operator sets its price ----
            if (!await db.Plans.AnyAsync(p => p.Code == "zoho-connector", ct))
            {
                db.Plans.Add(new Plan { Code = "zoho-connector", Name = "Zoho Connector", Kind = "connector", SortOrder = 4, TrialDays = 14, PriceMonthly = 15, PriceYearly = 150, MaxDevices = 0, MaxUsers = 3, MaxBranches = 0, MaxReceiptsPerMonth = 0,
                    Description = "Connect your own system to Zoho Books through FiscalHub. No fiscal device needed.",
                    Features = "Push and pull Zoho Books data from your system\nOne API key, Zoho's own format\nA webhook for every change in Zoho\nToken renewal and monitoring handled for you" });
                await db.SaveChangesAsync(ct);
            }

            // ---- the developer / API plan (software houses fiscalising through the API): priced by use, hidden until the operator publishes it ----
            if (!await db.Plans.AnyAsync(p => p.Code == "developer", ct))
            {
                db.Plans.Add(new Plan { Code = "developer", Name = "Developer (API)", Kind = "fiscal", SortOrder = 5, TrialDays = 30, PriceMonthly = 0, PriceYearly = 0, MaxDevices = 0, MaxUsers = 5, MaxBranches = 0,
                    MaxReceiptsPerMonth = 0, PricePerDeviceMonthly = 5, PricePerDeviceYearly = 50, ReceiptPricing = "metered", PricePerThousandReceipts = 10, ReceiptMinimumMonthly = 10, Modules = "api,messaging", IsPublic = false,
                    Description = "For software houses and integrators: fiscalise from your own system through the API and pay only for what you use.",
                    Features = "Unlimited test devices, free\nPay per live device each month\nPay per 1,000 receipts\nAPI tokens, webhooks and SMS" });
                await db.SaveChangesAsync(ct);
            }

            // ---- the accounting-only plan (small businesses that keep their books here without fiscalising): hidden until the operator sets its price and publishes it ----
            if (!await db.Plans.AnyAsync(p => p.Code == "books", ct))
            {
                db.Plans.Add(new Plan { Code = "books", Name = "Books (accounting only)", Kind = "accounting", SortOrder = 6, TrialDays = 30, PriceMonthly = 10, PriceYearly = 100, MaxDevices = 0, MaxUsers = 3, MaxBranches = 1,
                    MaxReceiptsPerMonth = 0, Modules = "accounting,inventory,approvals,quotes", IsPublic = false,
                    Description = "Full accounting for businesses that do not fiscalise: invoices, bills, bank, VAT-free sales, reports and year end.",
                    Features = "Invoices and credit notes from the books\nBring in your balances from Excel\nBank reconciliation and cashbook\nProfit and loss, balance sheet, cash flow and annual financial statements" });
                await db.SaveChangesAsync(ct);
            }

            // ---- once: the books plan made before inventory existed gets it too ----
            if (!await db.SystemSettings.AnyAsync(s => s.Key == "Seed.BooksInventory", ct))
            {
                foreach (var plan in await db.Plans.Where(p => p.Kind == "accounting" && p.Modules != null).ToListAsync(ct))
                    if (!plan.Modules!.Split(',').Contains("inventory")) plan.Modules += ",inventory";
                db.SystemSettings.Add(new SystemSetting { Key = "Seed.BooksInventory", Value = "done", UpdatedBy = "system" });
                await db.SaveChangesAsync(ct);
                Billing.PlanModuleAccess.Changed();
            }

            // ---- first SuperAdmin ----
            var userMgr = sp.GetRequiredService<UserManager<ApplicationUser>>();
            if ((await userMgr.GetUsersInRoleAsync(RoleNames.SuperAdmin)).Count == 0)
            {
                var email = cfg["Seed:AdminEmail"]; var pw = cfg["Seed:AdminPassword"];
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pw))
                {
                    log.LogWarning("No SuperAdmin exists and Seed:AdminEmail / Seed:AdminPassword are not set - nobody can administer the gateway. Set them and restart.");
                    return;
                }
                var admin = new ApplicationUser
                {
                    UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = true, Name = cfg["Seed:AdminName"] ?? "Platform", Surname = "Administrator",
                    MustChangePassword = cfg.GetValue("Seed:ForcePasswordChange", true),
                };
                var res = await userMgr.CreateAsync(admin, pw);
                if (!res.Succeeded) throw new InvalidOperationException("Cannot create the SuperAdmin: " + string.Join("; ", res.Errors.Select(e => e.Description)));
                await userMgr.AddToRoleAsync(admin, RoleNames.SuperAdmin);
                log.LogInformation("Created the first SuperAdmin {Email}", admin.Email);
            }
        }
    }
}
