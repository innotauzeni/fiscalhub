using Fdms.Application.Abstractions;
using Fdms.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Fdms.Infrastructure.Persistence
{
    public class FdmsDbContext : IdentityDbContext<ApplicationUser, Role, string>
    {
        private readonly ICallerScope _scope;

        public FdmsDbContext(DbContextOptions<FdmsDbContext> options, ICallerScope? scope = null) : base(options) =>
            _scope = scope ?? new NullCallerScope();

        public DbSet<Client> Clients => Set<Client>();
        public DbSet<Branch> Branches => Set<Branch>();
        public DbSet<InvoiceTemplate> InvoiceTemplates => Set<InvoiceTemplate>();
        public DbSet<TemplateAsset> TemplateAssets => Set<TemplateAsset>();
        public DbSet<IntegrationConnection> IntegrationConnections => Set<IntegrationConnection>();
        public DbSet<IntegrationDocument> IntegrationDocuments => Set<IntegrationDocument>();
        public DbSet<BridgeKey> BridgeKeys => Set<BridgeKey>();
        public DbSet<IntegrationAccess> IntegrationAccess => Set<IntegrationAccess>();
        public DbSet<BridgeGrant> BridgeGrants => Set<BridgeGrant>();
        public DbSet<BridgeEndpoint> BridgeEndpoints => Set<BridgeEndpoint>();
        public DbSet<BridgeEvent> BridgeEvents => Set<BridgeEvent>();
        public DbSet<WhatsAppSession> WhatsAppSessions => Set<WhatsAppSession>();
        public DbSet<WhatsAppMessage> WhatsAppMessages => Set<WhatsAppMessage>();
        public DbSet<WhatsAppAccount> WhatsAppAccounts => Set<WhatsAppAccount>();
        public DbSet<SmsAccount> SmsAccounts => Set<SmsAccount>();
        public DbSet<SmsMessage> SmsMessages => Set<SmsMessage>();
        public DbSet<UserBranch> UserBranches => Set<UserBranch>();
        public DbSet<UserDevice> UserDevices => Set<UserDevice>();
        public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
        public DbSet<Device> Devices => Set<Device>();
        public DbSet<FiscalDay> FiscalDays => Set<FiscalDay>();
        public DbSet<FiscalDayCounterRow> FiscalDayCounters => Set<FiscalDayCounterRow>();
        public DbSet<Receipt> Receipts => Set<Receipt>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
        public DbSet<ApiActivity> ApiActivities => Set<ApiActivity>();
        public DbSet<DatabaseBackup> DatabaseBackups => Set<DatabaseBackup>();
        public DbSet<Notification> Notifications => Set<Notification>();
        public DbSet<EmailOutbox> EmailOutbox => Set<EmailOutbox>();
        public DbSet<SavedReport> SavedReports => Set<SavedReport>();
        public DbSet<Budget> Budgets => Set<Budget>();
        public DbSet<BudgetLine> BudgetLines => Set<BudgetLine>();
        public DbSet<ArPayment> ArPayments => Set<ArPayment>();
        public DbSet<SalesInvoice> SalesInvoices => Set<SalesInvoice>();
        public DbSet<StockCategory> StockCategories => Set<StockCategory>();
        public DbSet<StockLocation> StockLocations => Set<StockLocation>();
        public DbSet<StockLevel> StockLevels => Set<StockLevel>();
        public DbSet<StockDocument> StockDocuments => Set<StockDocument>();
        public DbSet<StockDocumentLine> StockDocumentLines => Set<StockDocumentLine>();
        public DbSet<StockMovement> StockMovements => Set<StockMovement>();
        public DbSet<SalesInvoiceLine> SalesInvoiceLines => Set<SalesInvoiceLine>();
        public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
        public DbSet<AmortisationSchedule> AmortisationSchedules => Set<AmortisationSchedule>();
        public DbSet<AmortisationCharge> AmortisationCharges => Set<AmortisationCharge>();
        public DbSet<FixedAsset> FixedAssets => Set<FixedAsset>();
        public DbSet<DepreciationCharge> DepreciationCharges => Set<DepreciationCharge>();
        public DbSet<AccountingSetting> AccountingSettings => Set<AccountingSetting>();
        public DbSet<Supplier> Suppliers => Set<Supplier>();
        public DbSet<Bill> Bills => Set<Bill>();
        public DbSet<BillPayment> BillPayments => Set<BillPayment>();
        public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
        public DbSet<CostCentre> CostCentres => Set<CostCentre>();
        public DbSet<RecurringJournal> RecurringJournals => Set<RecurringJournal>();
        public DbSet<YearClose> YearCloses => Set<YearClose>();
        public DbSet<CashDocument> CashDocuments => Set<CashDocument>();
        public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
        public DbSet<JournalLine> JournalLines => Set<JournalLine>();
        public DbSet<BankStatementLine> BankStatementLines => Set<BankStatementLine>();
        public DbSet<Currency> Currencies => Set<Currency>();
        public DbSet<Bank> Banks => Set<Bank>();
        public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
        public DbSet<AiUsageLog> AiUsage => Set<AiUsageLog>();
        public DbSet<Quote> Quotes => Set<Quote>();
        public DbSet<RecurringInvoice> RecurringInvoices => Set<RecurringInvoice>();
        public DbSet<RecurringRun> RecurringRuns => Set<RecurringRun>();
        public DbSet<ErrorLog> ErrorLogs => Set<ErrorLog>();
        public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
        public DbSet<Plan> Plans => Set<Plan>();
        public DbSet<ClientApplication> Applications => Set<ClientApplication>();
        public DbSet<Subscription> Subscriptions => Set<Subscription>();
        public DbSet<BillingInvoice> BillingInvoices => Set<BillingInvoice>();
        public DbSet<Payment> Payments => Set<Payment>();
        public DbSet<ActivationCode> ActivationCodes => Set<ActivationCode>();
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<Product> Products => Set<Product>();
        public DbSet<GoLiveRequest> GoLiveRequests => Set<GoLiveRequest>();
        public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
        public DbSet<OfflineFile> OfflineFiles => Set<OfflineFile>();

        public DbSet<Permission> Permissions => Set<Permission>();
        public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
        public DbSet<SectionModule> SectionModules => Set<SectionModule>();
        public DbSet<SystemModule> SystemModules => Set<SystemModule>();
        public DbSet<Submodule> Submodules => Set<Submodule>();
        public DbSet<RoleMenu> RoleMenus => Set<RoleMenu>();
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<Passkey> Passkeys => Set<Passkey>();
        public DbSet<Partner> Partners => Set<Partner>();
        public DbSet<ReferralReward> ReferralRewards => Set<ReferralReward>();
        public DbSet<ReferralPerk> ReferralPerks => Set<ReferralPerk>();
        public DbSet<CoinTransaction> CoinTransactions => Set<CoinTransaction>();
        public DbSet<ApprovalRule> ApprovalRules => Set<ApprovalRule>();
        public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
        public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
        public DbSet<PartnerPayout> PartnerPayouts => Set<PartnerPayout>();
        public DbSet<StoredDocument> Documents => Set<StoredDocument>();

        // Every audit row records who did it (user or API token) without each caller having to remember.
        private void StampAudit()
        {
            foreach (var e in ChangeTracker.Entries<AuditLog>().Where(x => x.State == EntityState.Added))
            {
                e.Entity.UserId ??= _scope.UserId;
                e.Entity.UserName ??= _scope.UserName;
                e.Entity.ApiKeyId ??= _scope.ApiKeyId;
                if (e.Entity.ActorType == "system") e.Entity.ActorType = _scope.ActorType;
                e.Entity.IpAddress ??= _scope.IpAddress;
            }
        }

        /// <summary>
        /// Every DateTime is read back as UTC (so the API sends "...Z" and a browser shows the viewer's own local time), except the few that
        /// hold a Zimbabwe wall-clock reading - those are configured below as wall-clock and are sent without a zone.
        /// </summary>
        protected override void ConfigureConventions(ModelConfigurationBuilder cb)
        {
            cb.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
            cb.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        }
        public override int SaveChanges(bool acceptAllChangesOnSuccess) { StampAudit(); return base.SaveChanges(acceptAllChangesOnSuccess); }
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default) { StampAudit(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }

        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);

            // ---- identity (table names prefixed like the rest) ----
            b.Entity<ApplicationUser>(e =>
            {
                e.ToTable("fdms_users");
                e.Property(x => x.Name).HasMaxLength(100);
                e.Property(x => x.Surname).HasMaxLength(100);
                e.Property(x => x.Status).HasMaxLength(20);
                e.HasIndex(x => x.ClientId);
                e.Property(x => x.WhatsAppNumber).HasMaxLength(20);
                e.Property(x => x.WhatsAppLinkCodeHash).HasMaxLength(64);
                e.HasIndex(x => x.WhatsAppNumber).IsUnique();
            });
            b.Entity<WhatsAppAccount>(e =>
            {
                e.ToTable("fdms_whatsapp_accounts");
                e.HasIndex(x => x.ClientId).IsUnique();
                e.HasIndex(x => x.PhoneNumberId).IsUnique();
                e.Property(x => x.PhoneNumberId).HasMaxLength(40);
                e.Property(x => x.BusinessAccountId).HasMaxLength(40);
                e.Property(x => x.DisplayNumber).HasMaxLength(20);
                e.Property(x => x.AccessTokenProtected).HasMaxLength(4000);
                e.Property(x => x.AppSecretProtected).HasMaxLength(1000);
                e.Property(x => x.VerifyToken).HasMaxLength(80).IsRequired();
                e.Property(x => x.AlertTemplate).HasMaxLength(100);
                e.Property(x => x.AlertLanguage).HasMaxLength(10).IsRequired();
                e.Property(x => x.ApiVersion).HasMaxLength(10).IsRequired();
                e.Property(x => x.CustomersEnabled).HasDefaultValue(true);
                e.Property(x => x.CustomerGreeting).HasMaxLength(300);
                e.Property(x => x.UpdatedBy).HasMaxLength(256);
            });
            b.Entity<SmsAccount>(e =>
            {
                e.ToTable("fdms_sms_accounts");
                e.HasIndex(x => x.ClientId).IsUnique();
                e.Property(x => x.Provider).HasMaxLength(20).IsRequired();
                e.Property(x => x.ApiUrl).HasMaxLength(300);
                e.Property(x => x.Username).HasMaxLength(200);
                e.Property(x => x.ApiKeyProtected).HasMaxLength(2000);
                e.Property(x => x.ApiSecretProtected).HasMaxLength(2000);
                e.Property(x => x.SenderId).HasMaxLength(20);
                e.Property(x => x.AllowCompanies).HasDefaultValue(true);
                e.Property(x => x.UpdatedBy).HasMaxLength(256);
            });
            b.Entity<SmsMessage>(e =>
            {
                e.ToTable("fdms_sms_messages");
                e.HasIndex(x => new { x.Status, x.NextAttemptAt });
                e.HasIndex(x => new { x.ClientId, x.CreatedAt });
                e.Property(x => x.UserId).HasMaxLength(255);
                e.Property(x => x.To).HasMaxLength(20).IsRequired();
                e.Property(x => x.Body).HasMaxLength(1000).IsRequired();
                e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
                e.Property(x => x.Status).HasMaxLength(20).IsRequired();
                e.Property(x => x.ProviderMessageId).HasMaxLength(200);
                e.Property(x => x.Error).HasMaxLength(500);
            });
            b.Entity<WhatsAppSession>(e =>
            {
                e.ToTable("fdms_whatsapp_sessions");
                e.HasKey(x => new { x.Line, x.Phone });
                e.Property(x => x.Line).HasMaxLength(32).HasDefaultValue("");
                e.Property(x => x.Phone).HasMaxLength(20);
                e.Property(x => x.UserId).HasMaxLength(255);
                e.Property(x => x.State).HasMaxLength(40).IsRequired();
                e.Property(x => x.DataJson).HasColumnType("longtext").IsRequired();
                e.HasIndex(x => x.ClientId);
            });
            b.Entity<WhatsAppMessage>(e =>
            {
                e.ToTable("fdms_whatsapp_messages");
                e.HasIndex(x => x.AccountId);
                e.Property(x => x.Direction).HasMaxLength(4).IsRequired();
                e.Property(x => x.Phone).HasMaxLength(20).IsRequired();
                e.Property(x => x.UserId).HasMaxLength(255);
                e.Property(x => x.WaMessageId).HasMaxLength(128);
                e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
                e.Property(x => x.Body).HasMaxLength(2000);
                e.Property(x => x.ReplyId).HasMaxLength(200);
                e.Property(x => x.Status).HasMaxLength(20).IsRequired();
                e.Property(x => x.Error).HasMaxLength(500);
                e.HasIndex(x => x.WaMessageId).IsUnique();
                e.HasIndex(x => new { x.Status, x.NextAttemptAt });
                e.HasIndex(x => x.ClientId);
                e.HasIndex(x => new { x.Phone, x.CreatedAt });
            });
            b.Entity<Role>(e =>
            {
                e.ToTable("fdms_roles");
                e.Property(x => x.Type).HasMaxLength(20);
                e.Property(x => x.Level).HasMaxLength(20);
                e.Property(x => x.Description).HasMaxLength(300);
            });
            b.Entity<IdentityUserRole<string>>().ToTable("fdms_userroles");
            b.Entity<IdentityUserClaim<string>>().ToTable("fdms_userclaims");
            b.Entity<IdentityUserLogin<string>>().ToTable("fdms_userlogins");
            b.Entity<IdentityRoleClaim<string>>().ToTable("fdms_roleclaims");
            b.Entity<IdentityUserToken<string>>().ToTable("fdms_usertokens");

            // ---- access control (dara-style) ----
            b.Entity<Branch>(e =>
            {
                e.ToTable("fdms_branches");
                e.Property(x => x.Code).HasMaxLength(20).IsRequired();
                e.Property(x => x.Name).HasMaxLength(120).IsRequired();
                e.Property(x => x.Address).HasMaxLength(250);
                e.Property(x => x.Phone).HasMaxLength(40);
                e.HasIndex(x => new { x.ClientId, x.Code }).IsUnique();
            });
            b.Entity<InvoiceTemplate>(e =>
            {
                e.ToTable("fdms_invoicetemplates");
                e.Property(x => x.Name).HasMaxLength(80).IsRequired();
                e.Property(x => x.LogoContentType).HasMaxLength(40);
                e.HasIndex(x => new { x.ClientId, x.BranchId });
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
            });
            b.Entity<TemplateAsset>(e =>
            {
                e.ToTable("fdms_template_assets");
                e.Property(x => x.Name).HasMaxLength(120);
                e.Property(x => x.ContentType).HasMaxLength(40);
                e.HasIndex(x => x.ClientId);
            });
            b.Entity<IntegrationConnection>(e =>
            {
                e.ToTable("fdms_integration_connections");
                e.Property(x => x.Provider).HasMaxLength(30).IsRequired();
                e.Property(x => x.Status).HasMaxLength(30).IsRequired();
                e.Property(x => x.AccountsServer).HasMaxLength(120).IsRequired();
                e.Property(x => x.ApiDomain).HasMaxLength(120).IsRequired();
                e.Property(x => x.OrganizationId).HasMaxLength(40);
                e.Property(x => x.OrganizationName).HasMaxLength(200);
                // OAuth tokens (Xero, Sage) are long, more so once encrypted: text keeps them off the row (the row limit is 64 KB)
                e.Property(x => x.RefreshTokenProtected).HasMaxLength(4000).HasColumnType("text");
                e.Property(x => x.AccessTokenProtected).HasMaxLength(6000).HasColumnType("text");
                e.Property(x => x.ExtraJson).HasMaxLength(1000);
                e.Property(x => x.ConnectedBy).HasMaxLength(200);
                e.Property(x => x.LastError).HasMaxLength(1000);
                e.Property(x => x.DefaultHsCode).HasMaxLength(20);
                e.Property(x => x.DefaultMoneyType).HasMaxLength(20);
                e.Property(x => x.WebhookKey).HasMaxLength(64).IsRequired();
                e.Property(x => x.BridgeCursorsJson).HasMaxLength(4000);
                e.HasIndex(x => new { x.ClientId, x.Provider }).IsUnique();
            });
            b.Entity<IntegrationDocument>(e =>
            {
                e.ToTable("fdms_integration_documents");
                e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
                e.Property(x => x.ExternalId).HasMaxLength(60).IsRequired();
                e.Property(x => x.ExternalNumber).HasMaxLength(100).IsRequired();
                e.Property(x => x.CustomerName).HasMaxLength(200);
                e.Property(x => x.Total).HasPrecision(19, 4);
                e.Property(x => x.Currency).HasMaxLength(10);
                e.Property(x => x.ExternalStatus).HasMaxLength(40);
                e.Property(x => x.Status).HasMaxLength(20).IsRequired();
                e.Property(x => x.Error).HasMaxLength(1000);
                e.Property(x => x.PostBackError).HasMaxLength(1000);
                e.HasIndex(x => new { x.ConnectionId, x.Kind, x.ExternalId }).IsUnique();
                e.HasIndex(x => new { x.ClientId, x.Status });
                e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
            });
            b.Entity<IntegrationAccess>(e =>
            {
                e.ToTable("fdms_integration_access");
                e.Property(x => x.Status).HasMaxLength(20).IsRequired();
                e.Property(x => x.Uses).HasMaxLength(100).IsRequired();
                e.Property(x => x.Note).HasMaxLength(1000);
                e.Property(x => x.RequestedBy).HasMaxLength(200);
                e.Property(x => x.DecidedBy).HasMaxLength(200);
                e.Property(x => x.DecisionNote).HasMaxLength(500);
                e.Property(x => x.SetupFee).HasPrecision(18, 2);
                e.Property(x => x.Provider).HasMaxLength(20).HasDefaultValue("zoho");
                e.HasIndex(x => new { x.ClientId, x.Provider }).IsUnique();
                e.HasIndex(x => x.Status);
            });
            b.Entity<BridgeKey>(e =>
            {
                e.ToTable("fdms_bridge_keys");
                e.Property(x => x.Name).HasMaxLength(80).IsRequired();
                e.Property(x => x.Prefix).HasMaxLength(20).IsRequired();
                e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
                e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => x.KeyHash).IsUnique();
                e.HasIndex(x => x.PartnerId);
                e.HasIndex(x => x.ClientId);
                e.Property(x => x.Modules).HasMaxLength(500).IsRequired();
            });
            b.Entity<BridgeGrant>(e =>
            {
                e.ToTable("fdms_bridge_grants");
                e.Property(x => x.Modules).HasMaxLength(500).IsRequired();
                e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.PartnerId });
                e.HasIndex(x => x.PartnerId);
            });
            b.Entity<BridgeEndpoint>(e =>
            {
                e.ToTable("fdms_bridge_endpoints");
                e.Property(x => x.WebhookUrl).HasMaxLength(500);
                e.Property(x => x.SecretProtected).HasMaxLength(1000);
                e.Property(x => x.WatchModules).HasMaxLength(500).IsRequired();
                e.HasIndex(x => x.PartnerId).IsUnique();
                e.HasIndex(x => x.ClientId).IsUnique();
            });
            b.Entity<BridgeEvent>(e =>
            {
                e.ToTable("fdms_bridge_events");
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Module).HasMaxLength(40).IsRequired();
                e.Property(x => x.RecordId).HasMaxLength(60).IsRequired();
                e.Property(x => x.EventType).HasMaxLength(80).IsRequired();
                e.Property(x => x.PayloadJson).HasColumnType("longtext").IsRequired();
                e.Property(x => x.Status).HasMaxLength(20).IsRequired();
                e.Property(x => x.LastError).HasMaxLength(500);
                e.HasIndex(x => new { x.PartnerId, x.Id });
                e.HasIndex(x => new { x.Status, x.NextAttemptAt });
                e.HasIndex(x => x.ClientId);
            });
            b.Entity<UserBranch>(e =>
            {
                e.ToTable("fdms_userbranches");
                e.HasKey(x => new { x.UserId, x.BranchId });
                e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Cascade);
            });
            b.Entity<UserDevice>(e =>
            {
                e.ToTable("fdms_userdevices");
                e.HasKey(x => new { x.UserId, x.DeviceId });
                e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            });
            b.Entity<Device>().HasOne<Branch>().WithMany().HasForeignKey(d => d.BranchId).OnDelete(DeleteBehavior.SetNull);
            b.Entity<Permission>(e =>
            {
                e.ToTable("fdms_permissions");
                e.Property(x => x.Name).HasMaxLength(80).IsRequired();
                e.Property(x => x.DisplayName).HasMaxLength(150);
                e.Property(x => x.Module).HasMaxLength(60);
                e.HasIndex(x => x.Name).IsUnique();
            });
            b.Entity<RolePermission>(e =>
            {
                e.ToTable("fdms_rolepermissions");
                e.HasOne(x => x.Role).WithMany(r => r.RolePermissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.Permission).WithMany(p => p.RolePermissions).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique();
            });
            b.Entity<SectionModule>(e =>
            {
                e.ToTable("fdms_sectionmodules");
                e.Property(x => x.Name).HasMaxLength(80).IsRequired();
                e.Property(x => x.Color).HasMaxLength(20);
            });
            b.Entity<SystemModule>(e =>
            {
                e.ToTable("fdms_systemmodules");
                e.Property(x => x.Name).HasMaxLength(80).IsRequired();
                e.Property(x => x.Slugname).HasMaxLength(80);
                e.Property(x => x.Icon).HasMaxLength(40);
                e.Property(x => x.Description).HasMaxLength(200);
                e.Property(x => x.AccessName).HasMaxLength(80);
                e.HasOne(x => x.SectionModule).WithMany(s => s.Children).HasForeignKey(x => x.SectionModuleId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => x.Slugname).IsUnique();
            });
            b.Entity<Submodule>(e =>
            {
                e.ToTable("fdms_submodules");
                e.Property(x => x.Name).HasMaxLength(80).IsRequired();
                e.Property(x => x.Icon).HasMaxLength(40);
                e.Property(x => x.Route).HasMaxLength(120);
                e.Property(x => x.AccessName).HasMaxLength(80);
                e.HasOne(x => x.Module).WithMany(m => m.Children).HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Cascade);
            });
            b.Entity<RoleMenu>(e =>
            {
                e.ToTable("fdms_rolemenus");
                e.HasOne(x => x.Role).WithMany(r => r.RoleMenus).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.Menu).WithMany(m => m.RoleMenus).HasForeignKey(x => x.SystemModuleId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.RoleId, x.SystemModuleId }).IsUnique();
            });
            b.Entity<RefreshToken>(e =>
            {
                e.ToTable("fdms_refreshtokens");
                e.Property(x => x.UserId).HasMaxLength(450);
                e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
                e.HasIndex(x => x.TokenHash).IsUnique();
                e.HasIndex(x => x.UserId);
            });

            b.Entity<Passkey>(e =>
            {
                e.ToTable("fdms_passkeys");
                e.Property(x => x.UserId).HasMaxLength(450).IsRequired();
                e.Property(x => x.CredentialId).HasMaxLength(512).IsRequired();
                e.Property(x => x.Name).HasMaxLength(80).IsRequired();
                e.HasIndex(x => x.CredentialId).IsUnique();
                e.HasIndex(x => x.UserId);
            });

            b.Entity<Partner>(e =>
            {
                e.ToTable("fdms_partners");
                e.Property(x => x.Name).HasMaxLength(150).IsRequired();
                e.Property(x => x.Code).HasMaxLength(20).IsRequired();
                e.Property(x => x.ContactName).HasMaxLength(120); e.Property(x => x.Email).HasMaxLength(200); e.Property(x => x.Phone).HasMaxLength(40); e.Property(x => x.Notes).HasMaxLength(1000);
                e.Property(x => x.CommissionPercent).HasPrecision(5, 2);
                e.Property(x => x.DiscountPercent).HasPrecision(5, 2);
                e.Property(x => x.Kind).HasMaxLength(20).HasDefaultValue("commission");
                e.HasIndex(x => x.Code).IsUnique();
            });
            b.Entity<PartnerPayout>(e =>
            {
                e.ToTable("fdms_partner_payouts");
                e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Reference).HasMaxLength(100); e.Property(x => x.Notes).HasMaxLength(500); e.Property(x => x.RecordedBy).HasMaxLength(200);
                e.HasIndex(x => x.PartnerId);
            });
            b.Entity<StoredDocument>(e =>
            {
                e.ToTable("fdms_documents");
                e.Property(x => x.Title).HasMaxLength(150).IsRequired(); e.Property(x => x.FileName).HasMaxLength(200).IsRequired(); e.Property(x => x.ContentType).HasMaxLength(100);
                e.Property(x => x.Sha256).HasMaxLength(64); e.Property(x => x.Category).HasMaxLength(30); e.Property(x => x.LinkType).HasMaxLength(10); e.Property(x => x.Notes).HasMaxLength(500); e.Property(x => x.UploadedBy).HasMaxLength(200);
                e.Property(x => x.Data).HasColumnType("longblob");
                e.HasIndex(x => new { x.ClientId, x.LinkType, x.LinkId });
            });
            b.Entity<Client>().Property(x => x.PartnerPercent).HasPrecision(5, 2);
            b.Entity<Client>().Property(x => x.ReferralCode).HasMaxLength(20);
            b.Entity<Client>().HasIndex(x => x.ReferralCode).IsUnique();
            b.Entity<ReferralReward>(e =>
            {
                e.ToTable("fdms_referral_rewards");
                e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Percent).HasPrecision(5, 2); e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.Status).HasMaxLength(10); e.Property(x => x.Reference).HasMaxLength(100); e.Property(x => x.PaidBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ReferredClientId, x.InvoiceId }).IsUnique();
                e.HasIndex(x => x.ReferrerClientId);
            });
            b.Entity<ApprovalRule>(e =>
            {
                e.ToTable("fdms_approval_rules");
                e.Property(x => x.Action).HasMaxLength(30); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.LevelsJson).HasMaxLength(1000); e.Property(x => x.MinAmount).HasPrecision(18, 2);
                e.HasIndex(x => new { x.ClientId, x.Action });
            });
            b.Entity<ApprovalRequest>(e =>
            {
                e.ToTable("fdms_approval_requests");
                e.Property(x => x.Action).HasMaxLength(30); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.LevelsJson).HasMaxLength(1000); e.Property(x => x.Amount).HasPrecision(18, 2);
                e.Property(x => x.Status).HasMaxLength(12); e.Property(x => x.Summary).HasMaxLength(300); e.Property(x => x.EntityId).HasMaxLength(64);
                e.Property(x => x.RequestedById).HasMaxLength(64); e.Property(x => x.RequestedByName).HasMaxLength(200); e.Property(x => x.Error).HasMaxLength(1000);
                e.HasIndex(x => new { x.ClientId, x.Status, x.CreatedAt });
            });
            b.Entity<ApprovalDecision>(e =>
            {
                e.ToTable("fdms_approval_decisions");
                e.Property(x => x.Role).HasMaxLength(100); e.Property(x => x.UserId).HasMaxLength(64); e.Property(x => x.UserName).HasMaxLength(200); e.Property(x => x.Decision).HasMaxLength(10); e.Property(x => x.Note).HasMaxLength(500);
                e.HasIndex(x => x.RequestId);
            });
            b.Entity<CoinTransaction>(e =>
            {
                e.ToTable("fdms_coin_transactions");
                e.Property(x => x.Kind).HasMaxLength(20); e.Property(x => x.Description).HasMaxLength(300); e.Property(x => x.Reference).HasMaxLength(100); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.Kind, x.Reference }).IsUnique();
                e.HasIndex(x => new { x.ClientId, x.CreatedAt });
            });
            b.Entity<ReferralPerk>(e =>
            {
                e.ToTable("fdms_referral_perks");
                e.Property(x => x.TierKey).HasMaxLength(40); e.Property(x => x.Label).HasMaxLength(60);
                e.HasIndex(x => new { x.ClientId, x.TierKey }).IsUnique();
            });

            // ---- fiscal model ----
            b.Entity<Client>(e =>
            {
                e.ToTable("fdms_clients");
                e.Property(x => x.Name).HasMaxLength(200).IsRequired();
                e.Property(x => x.Email).HasMaxLength(200);
                e.Property(x => x.WebhookUrl).HasMaxLength(500);
                e.Property(x => x.WebhookSecretProtected).HasColumnType("text");
                e.Property(x => x.Slug).HasMaxLength(40); e.HasIndex(x => x.Slug).IsUnique();
                e.Property(x => x.AlertEmails).HasMaxLength(500); e.Property(x => x.AlertKinds).HasMaxLength(200);
            });

            b.Entity<WebhookDelivery>(e =>
            {
                e.ToTable("fdms_webhookdeliveries");
                e.Property(x => x.EventType).HasMaxLength(60);
                e.Property(x => x.PayloadJson).HasColumnType("longtext");
                e.Property(x => x.LastError).HasMaxLength(500);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt });
                e.HasIndex(x => new { x.ClientId, x.CreatedAt });
            });

            b.Entity<OfflineFile>(e =>
            {
                e.ToTable("fdms_offlinefiles");
                e.Property(x => x.ContentBase64).HasColumnType("longtext");
                e.Property(x => x.OperationId).HasMaxLength(80);
                e.Property(x => x.ErrorsJson).HasColumnType("text");
                e.Property(x => x.LastError).HasMaxLength(500);
                e.HasIndex(x => new { x.DeviceId, x.FiscalDayNo, x.Sequence }).IsUnique();
                e.HasIndex(x => new { x.Status, x.DeviceId });
            });

            b.Entity<ApiKey>(e =>
            {
                e.ToTable("fdms_apikeys");
                e.Property(x => x.Name).HasMaxLength(100);
                e.Property(x => x.KeyPrefix).HasMaxLength(16);
                e.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
                e.Property(x => x.Scopes).HasMaxLength(500);
                e.Property(x => x.CreatedByUserId).HasMaxLength(450);
                e.HasIndex(x => x.KeyHash).IsUnique();
                e.HasIndex(x => x.ClientId);
                e.HasIndex(x => x.DeviceId);
            });

            b.Entity<Device>(e =>
            {
                e.ToTable("fdms_devices");
                e.Property(x => x.SerialNo).HasMaxLength(50);
                e.Property(x => x.ModelName).HasMaxLength(100);
                e.Property(x => x.ModelVersion).HasMaxLength(50);
                e.Property(x => x.CertificatePem).HasColumnType("text");
                e.Property(x => x.PrivateKeyProtected).HasColumnType("text");
                e.Property(x => x.TaxesJson).HasColumnType("text");
                e.Property(x => x.QrUrl).HasMaxLength(200);
                e.Property(x => x.TaxPayerName).HasMaxLength(250);
                e.Property(x => x.TaxPayerTin).HasMaxLength(20);
                e.Property(x => x.VatNumber).HasMaxLength(20);
                e.Property(x => x.BranchName).HasMaxLength(250);
                // One ZIMRA device can only belong to one tenant per environment.
                e.HasIndex(x => new { x.ZimraDeviceId, x.Environment }).IsUnique();
                e.HasIndex(x => x.ClientId);
            });

            b.Entity<FiscalDay>(e =>
            {
                e.ToTable("fdms_fiscaldays");
                e.Property(x => x.LastReceiptHash).HasMaxLength(64);
                e.Property(x => x.ClosingErrorCode).HasMaxLength(50);
                e.HasIndex(x => new { x.DeviceId, x.FiscalDayNo }).IsUnique();
                e.HasMany(x => x.Counters).WithOne().HasForeignKey(c => c.FiscalDayId).OnDelete(DeleteBehavior.Cascade);
            });

            b.Entity<FiscalDayCounterRow>(e =>
            {
                e.ToTable("fdms_fiscaldaycounters");
                e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.TaxPercent).HasPrecision(5, 2);
                e.Property(x => x.Value).HasPrecision(21, 2);
            });

            b.Entity<Receipt>(e =>
            {
                e.ToTable("fdms_receipts");
                e.Property(x => x.ExternalRef).HasMaxLength(100).IsRequired();
                e.Property(x => x.InvoiceNo).HasMaxLength(50);
                e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.Total).HasPrecision(21, 2);
                e.Property(x => x.Hash).HasMaxLength(64);
                e.Property(x => x.Signature).HasColumnType("text");
                e.Property(x => x.PayloadJson).HasColumnType("longtext");
                e.Property(x => x.ServerSignatureJson).HasColumnType("text");
                e.Property(x => x.RequestJson).HasColumnType("longtext");
                e.Property(x => x.ValidationErrorsJson).HasColumnType("text");
                e.Property(x => x.QrUrl).HasMaxLength(300);
                e.Property(x => x.ErrorCode).HasMaxLength(50);
                e.Property(x => x.ErrorMessage).HasMaxLength(1000);
                e.HasIndex(x => new { x.Status, x.HoldUntil });
                e.HasIndex(x => new { x.ClientId, x.DeviceId, x.ExternalRef }).IsUnique();
                e.HasIndex(x => new { x.DeviceId, x.ReceiptGlobalNo });
                e.HasIndex(x => new { x.ClientId, x.InvoiceNo });
            });

            b.Entity<Receipt>(e =>
            {
                e.Property(x => x.ReceiptDate).HasConversion<WallClockConverter>();           // CAT, as signed and sent to ZIMRA
                e.Property(x => x.ServerDate).HasConversion<NullableWallClockConverter>();    // ZIMRA's clock
            });
            b.Entity<FiscalDay>(e =>
            {
                e.Property(x => x.OpenedAt).HasConversion<WallClockConverter>();
                e.Property(x => x.ClosedAt).HasConversion<NullableWallClockConverter>();
                e.Property(x => x.LastReceiptDate).HasConversion<NullableWallClockConverter>();
            });
            b.Entity<Plan>(e =>
            {
                e.ToTable("fdms_plans");
                e.Property(x => x.Code).HasMaxLength(20).IsRequired();
                e.Property(x => x.Name).HasMaxLength(80).IsRequired();
                e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
                e.Property(x => x.Description).HasMaxLength(500);
                e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.Features).HasMaxLength(2000);
                e.Property(x => x.PriceMonthly).HasPrecision(18, 2);
                e.Property(x => x.PriceYearly).HasPrecision(18, 2);
                e.Property(x => x.PricePerExtraReceipt).HasPrecision(18, 4);
                e.Property(x => x.PricePerDeviceMonthly).HasPrecision(18, 2);
                e.Property(x => x.PricePerThousandReceipts).HasPrecision(18, 4);
                e.Property(x => x.PricePerDeviceYearly).HasPrecision(18, 2);
                e.Property(x => x.ReceiptMinimumMonthly).HasPrecision(18, 2);
                e.Property(x => x.ReceiptCapMonthly).HasPrecision(18, 2);
                e.Property(x => x.ReceiptPricing).HasMaxLength(20).HasDefaultValue("flat");
                e.Property(x => x.ReceiptTiersJson).HasMaxLength(2000);
                e.Property(x => x.Modules).HasMaxLength(500);
                e.HasIndex(x => x.Code).IsUnique();
            });
            b.Entity<ClientApplication>(e =>
            {
                e.ToTable("fdms_applications");
                e.Property(x => x.CompanyName).HasMaxLength(150).IsRequired();
                e.Property(x => x.Tin).HasMaxLength(30);
                e.Property(x => x.ContactName).HasMaxLength(100);
                e.Property(x => x.ContactSurname).HasMaxLength(100);
                e.Property(x => x.Email).HasMaxLength(200).IsRequired();
                e.Property(x => x.Phone).HasMaxLength(40);
                e.Property(x => x.PasswordHash).HasMaxLength(500);
                e.Property(x => x.UserName).HasMaxLength(64);
                e.Property(x => x.VerifyCodeHash).HasMaxLength(100);
                e.Property(x => x.BillingCycle).HasMaxLength(10);
                e.Property(x => x.Status).HasMaxLength(12);
                e.Property(x => x.Message).HasMaxLength(1000);
                e.Property(x => x.DecisionReason).HasMaxLength(500);
                e.Property(x => x.DecidedBy).HasMaxLength(200);
                e.Property(x => x.IpAddress).HasMaxLength(64);
                e.HasIndex(x => x.Email);
                e.HasIndex(x => x.Status);
            });
            b.Entity<Subscription>(e =>
            {
                e.ToTable("fdms_subscriptions");
                e.Property(x => x.Status).HasMaxLength(12);
                e.Property(x => x.BillingCycle).HasMaxLength(10);
                e.HasIndex(x => x.ClientId).IsUnique();
            });
            b.Entity<BillingInvoice>(e =>
            {
                e.ToTable("fdms_billinginvoices");
                e.Property(x => x.AddonsJson).HasMaxLength(200);
                e.Property(x => x.Number).HasMaxLength(40).IsRequired();
                e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
                e.Property(x => x.Description).HasMaxLength(300);
                e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.Status).HasMaxLength(10);
                e.Property(x => x.PaymentMethod).HasMaxLength(30);
                e.Property(x => x.PaymentReference).HasMaxLength(150);
                e.Property(x => x.ChangeToCycle).HasMaxLength(10);
                e.Property(x => x.Subtotal).HasPrecision(18, 2);
                e.Property(x => x.TaxPercent).HasPrecision(5, 2);
                e.Property(x => x.TaxAmount).HasPrecision(18, 2);
                e.Property(x => x.Total).HasPrecision(18, 2);
                e.HasIndex(x => x.Number).IsUnique();
                e.HasIndex(x => new { x.ClientId, x.Status });
            });
            b.Entity<Payment>(e =>
            {
                e.ToTable("fdms_payments");
                e.Property(x => x.Method).HasMaxLength(20);
                e.Property(x => x.Status).HasMaxLength(24);
                e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.Amount).HasPrecision(18, 2);
                e.Property(x => x.Reference).HasMaxLength(150);
                e.Property(x => x.ProviderReference).HasMaxLength(150);
                e.Property(x => x.PollUrl).HasMaxLength(500);
                e.Property(x => x.RedirectUrl).HasMaxLength(500);
                e.Property(x => x.Instructions).HasMaxLength(1000);
                e.Property(x => x.PayerPhone).HasMaxLength(40);
                e.Property(x => x.ProofName).HasMaxLength(200);
                e.Property(x => x.ProofContentType).HasMaxLength(60);
                e.Property(x => x.Notes).HasMaxLength(1000);
                e.Property(x => x.ConfirmedBy).HasMaxLength(200);
                e.Property(x => x.ProviderStatus).HasMaxLength(40);
                e.HasIndex(x => x.InvoiceId);
                e.HasIndex(x => new { x.ClientId, x.Status });
            });
            b.Entity<Customer>(e =>
            {
                e.ToTable("fdms_customers");
                e.Property(x => x.Name).HasMaxLength(150).IsRequired();
                e.Property(x => x.TradeName).HasMaxLength(150);
                e.Property(x => x.Tin).HasMaxLength(20);
                e.Property(x => x.VatNumber).HasMaxLength(20);
                e.Property(x => x.Email).HasMaxLength(200);
                e.Property(x => x.Phone).HasMaxLength(40);
                e.Property(x => x.Address).HasMaxLength(300);
                e.Property(x => x.Notes).HasMaxLength(500);
                e.HasIndex(x => new { x.ClientId, x.Name });
                e.HasIndex(x => new { x.ClientId, x.Tin });
            });
            b.Entity<Product>(e =>
            {
                e.ToTable("fdms_products");
                e.Property(x => x.Code).HasMaxLength(40);
                e.Property(x => x.Name).HasMaxLength(200).IsRequired();
                e.Property(x => x.HsCode).HasMaxLength(8);
                e.Property(x => x.Unit).HasMaxLength(20);
                e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.UnitPrice).HasPrecision(18, 4);
                e.Property(x => x.TaxPercent).HasPrecision(5, 2);
                e.Property(x => x.AverageCost).HasPrecision(18, 4); e.Property(x => x.LastCost).HasPrecision(18, 4);
                e.Property(x => x.ReorderLevel).HasPrecision(18, 4); e.Property(x => x.ReorderQuantity).HasPrecision(18, 4); e.Property(x => x.Barcode).HasMaxLength(60);
                e.HasIndex(x => new { x.ClientId, x.CategoryId });
                e.HasIndex(x => new { x.ClientId, x.Name });
                e.HasIndex(x => new { x.ClientId, x.Code });
            });
            b.Entity<ActivationCode>(e =>
            {
                e.ToTable("fdms_activationcodes");
                e.Property(x => x.Salt).HasMaxLength(64);
                e.Property(x => x.CodeHash).HasMaxLength(128);
                e.Property(x => x.UsedBy).HasMaxLength(200);
                e.Property(x => x.IssuedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.InvoiceId });
            });
            b.Entity<GoLiveRequest>(e =>
            {
                e.ToTable("fdms_golive");
                e.Property(x => x.Status).HasMaxLength(20);
                e.Property(x => x.TestReceiptIdsJson).HasMaxLength(1000); e.Property(x => x.SentToZimraBy).HasMaxLength(200); e.Property(x => x.ZimraEmail).HasMaxLength(300);
                e.Property(x => x.LiveActivationKeyProtected).HasMaxLength(500); e.Property(x => x.LiveSerialNo).HasMaxLength(60);
                e.Property(x => x.ContactName).HasMaxLength(120);
                e.Property(x => x.ContactPhone).HasMaxLength(40);
                e.Property(x => x.ZimraTin).HasMaxLength(30);
                e.Property(x => x.Notes).HasMaxLength(1000);
                e.Property(x => x.DecisionReason).HasMaxLength(500);
                e.Property(x => x.DecidedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.Status });
            });
            b.Entity<SystemSetting>(e =>
            {
                e.ToTable("fdms_systemsettings");
                e.HasKey(x => x.Key);
                e.Property(x => x.Key).HasMaxLength(100);
                // no length limit: the billing profile (terms, logo) is stored here too
                e.Property(x => x.UpdatedBy).HasMaxLength(200);
            });
            b.Entity<ErrorLog>(e =>
            {
                e.ToTable("fdms_errorlogs");
                e.Property(x => x.Uuid).HasMaxLength(16);
                e.Property(x => x.Severity).HasMaxLength(12);
                e.Property(x => x.Source).HasMaxLength(200);
                e.Property(x => x.Message).HasMaxLength(2000);
                e.Property(x => x.ExceptionType).HasMaxLength(200);
                e.Property(x => x.Path).HasMaxLength(300);
                e.Property(x => x.Method).HasMaxLength(10);
                e.Property(x => x.IpAddress).HasMaxLength(64);
                e.Property(x => x.UserName).HasMaxLength(200);
                e.Property(x => x.TraceId).HasMaxLength(64);
                e.Property(x => x.MachineName).HasMaxLength(100);
                e.Property(x => x.Environment).HasMaxLength(30);
                e.Property(x => x.Fingerprint).HasMaxLength(64);
                e.Property(x => x.Resolution).HasMaxLength(1000);
                e.Property(x => x.ResolvedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.IsResolved, x.CreatedAt });
                e.HasIndex(x => x.Fingerprint);
                e.HasIndex(x => x.CreatedAt);
            });
            b.Entity<Device>().Property(x => x.Name).HasMaxLength(80);
            b.Entity<Device>().Property(x => x.BranchAddress).HasMaxLength(500);
            b.Entity<Device>().Property(x => x.BranchPhone).HasMaxLength(50);
            b.Entity<Device>().Property(x => x.BranchEmail).HasMaxLength(200);
            b.Entity<Notification>(e =>
            {
                e.ToTable("fdms_notifications");
                e.Property(x => x.Kind).HasMaxLength(30); e.Property(x => x.Severity).HasMaxLength(8);
                e.Property(x => x.Title).HasMaxLength(200); e.Property(x => x.Body).HasMaxLength(1000); e.Property(x => x.Link).HasMaxLength(200); e.Property(x => x.Key).HasMaxLength(120);
                e.HasIndex(x => new { x.ClientId, x.Key }).IsUnique();
                e.HasIndex(x => new { x.ClientId, x.CreatedAt });
            });
            b.Entity<Budget>(e =>
            {
                e.ToTable("fdms_budgets");
                e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.Year, x.Name }).IsUnique();
            });
            b.Entity<BudgetLine>(e =>
            {
                e.ToTable("fdms_budget_lines");
                e.Property(x => x.Amount).HasPrecision(18, 2);
                e.HasIndex(x => new { x.BudgetId, x.AccountId, x.Month }).IsUnique();
            });
            b.Entity<ArPayment>(e =>
            {
                e.ToTable("fdms_ar_payments");
                e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Reference).HasMaxLength(50);
                e.HasIndex(x => new { x.ClientId, x.ReceiptId });
            });
            b.Entity<FixedAsset>(e =>
            {
                e.ToTable("fdms_fixed_assets");
                e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.Code).HasMaxLength(30); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Method).HasMaxLength(20); e.Property(x => x.Status).HasMaxLength(10); e.Property(x => x.Notes).HasMaxLength(300);
                e.Property(x => x.Cost).HasPrecision(18, 2); e.Property(x => x.ResidualValue).HasPrecision(18, 2); e.Property(x => x.RatePercent).HasPrecision(5, 2); e.Property(x => x.DisposalProceeds).HasPrecision(18, 2);
                e.HasIndex(x => new { x.ClientId, x.Code });
            });
            b.Entity<DepreciationCharge>(e =>
            {
                e.ToTable("fdms_depreciation_charges");
                e.Property(x => x.Amount).HasPrecision(18, 2);
                e.HasIndex(x => new { x.AssetId, x.Period }).IsUnique();
            });
            b.Entity<AccountingSetting>(e =>
            {
                e.ToTable("fdms_accounting_settings"); e.HasKey(x => x.ClientId); e.Property(x => x.UpdatedBy).HasMaxLength(200);
                e.Property(x => x.CodeFormat).HasMaxLength(20).HasDefaultValue("9999"); e.Property(x => x.ClassStartsJson).HasMaxLength(300);
                e.Property(x => x.CodeStep).HasDefaultValue(10); e.Property(x => x.FiscalYearStartMonth).HasDefaultValue(1);
                e.Property(x => x.BaseCurrency).HasMaxLength(3); e.Property(x => x.InvoiceFooter).HasMaxLength(500); e.Property(x => x.PolicyNotes).HasColumnType("text");
            });
            b.Entity<StockCategory>(e =>
            {
                e.ToTable("fdms_stock_categories");
                e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Code).HasMaxLength(20);
                e.HasIndex(x => new { x.ClientId, x.ParentId, x.Name }).IsUnique();
            });
            b.Entity<StockLocation>(e =>
            {
                e.ToTable("fdms_stock_locations");
                e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Name).HasMaxLength(100);
                e.HasIndex(x => new { x.ClientId, x.Code }).IsUnique();
            });
            b.Entity<StockLevel>(e =>
            {
                e.ToTable("fdms_stock_levels"); e.HasKey(x => new { x.ProductId, x.LocationId });
                e.Property(x => x.Quantity).HasPrecision(18, 4);
                e.HasIndex(x => new { x.ClientId, x.LocationId });
            });
            b.Entity<StockDocument>(e =>
            {
                e.ToTable("fdms_stock_documents");
                e.Property(x => x.Kind).HasMaxLength(12); e.Property(x => x.Number).HasMaxLength(30); e.Property(x => x.Status).HasMaxLength(10); e.Property(x => x.SupplierReference).HasMaxLength(50);
                e.Property(x => x.Mode).HasMaxLength(10); e.Property(x => x.Reason).HasMaxLength(200); e.Property(x => x.Notes).HasMaxLength(500); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.Property(x => x.Subtotal).HasPrecision(18, 2); e.Property(x => x.Vat).HasPrecision(18, 2); e.Property(x => x.Total).HasPrecision(18, 2);
                e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.DocumentId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.ClientId, x.Kind, x.Date });
                e.HasIndex(x => new { x.ClientId, x.SourceId }).IsUnique();
            });
            b.Entity<StockDocumentLine>(e =>
            {
                e.ToTable("fdms_stock_document_lines");
                e.Property(x => x.Quantity).HasPrecision(18, 4); e.Property(x => x.Counted).HasPrecision(18, 4); e.Property(x => x.Expected).HasPrecision(18, 4);
                e.Property(x => x.UnitCost).HasPrecision(18, 4); e.Property(x => x.VatPercent).HasPrecision(5, 2); e.Property(x => x.Amount).HasPrecision(18, 2);
                e.HasIndex(x => x.ClientId);
            });
            b.Entity<StockMovement>(e =>
            {
                e.ToTable("fdms_stock_movements");
                e.Property(x => x.Quantity).HasPrecision(18, 4); e.Property(x => x.UnitCost).HasPrecision(18, 4); e.Property(x => x.Value).HasPrecision(18, 2);
                e.Property(x => x.BalanceAfter).HasPrecision(18, 4); e.Property(x => x.AverageCostAfter).HasPrecision(18, 4);
                e.HasIndex(x => new { x.ClientId, x.ProductId, x.Date });
                e.HasIndex(x => x.DocumentId);
            });
            b.Entity<SalesInvoice>(e =>
            {
                e.ToTable("fdms_sales_invoices");
                e.Property(x => x.Kind).HasMaxLength(12); e.Property(x => x.Number).HasMaxLength(50); e.Property(x => x.CustomerName).HasMaxLength(200); e.Property(x => x.CustomerTin).HasMaxLength(20);
                e.Property(x => x.CustomerEmail).HasMaxLength(120); e.Property(x => x.CustomerAddress).HasMaxLength(300); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Reference).HasMaxLength(60);
                e.Property(x => x.Notes).HasMaxLength(1000); e.Property(x => x.Status).HasMaxLength(10); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.Property(x => x.Subtotal).HasPrecision(18, 2); e.Property(x => x.Discount).HasPrecision(18, 2); e.Property(x => x.Total).HasPrecision(18, 2);
                e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.ClientId, x.Kind, x.Seq });
                e.HasIndex(x => new { x.ClientId, x.CustomerId });
                e.HasIndex(x => new { x.ClientId, x.Date });
            });
            b.Entity<SalesInvoiceLine>(e =>
            {
                e.ToTable("fdms_sales_invoice_lines");
                e.HasIndex(x => x.ClientId);
                e.Property(x => x.Description).HasMaxLength(300);
                e.Property(x => x.Quantity).HasPrecision(18, 4); e.Property(x => x.UnitPrice).HasPrecision(18, 4); e.Property(x => x.DiscountPercent).HasPrecision(5, 2); e.Property(x => x.Amount).HasPrecision(18, 2);
            });
            b.Entity<ExchangeRate>(e =>
            {
                e.ToTable("fdms_exchange_rates");
                e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Rate).HasPrecision(18, 8); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.Currency, x.Date }).IsUnique();
            });
            b.Entity<AmortisationSchedule>(e =>
            {
                e.ToTable("fdms_amortisation_schedules");
                e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.Kind).HasMaxLength(20); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Status).HasMaxLength(10);
                e.Property(x => x.Notes).HasMaxLength(300); e.Property(x => x.CreatedBy).HasMaxLength(200); e.Property(x => x.Amount).HasPrecision(18, 2);
                e.HasIndex(x => new { x.ClientId, x.Status });
            });
            b.Entity<AmortisationCharge>(e =>
            {
                e.ToTable("fdms_amortisation_charges");
                e.Property(x => x.Amount).HasPrecision(18, 2);
                e.HasIndex(x => new { x.ScheduleId, x.Period }).IsUnique();
            });
            b.Entity<Supplier>(e =>
            {
                e.ToTable("fdms_suppliers");
                e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.Tin).HasMaxLength(20); e.Property(x => x.Email).HasMaxLength(120); e.Property(x => x.Phone).HasMaxLength(40); e.Property(x => x.Address).HasMaxLength(300);
                e.HasIndex(x => new { x.ClientId, x.Name }).IsUnique();
            });
            b.Entity<Bill>(e =>
            {
                e.ToTable("fdms_bills");
                e.Property(x => x.Number).HasMaxLength(50); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Memo).HasMaxLength(200); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.Property(x => x.Subtotal).HasPrecision(18, 2); e.Property(x => x.Vat).HasPrecision(18, 2); e.Property(x => x.Total).HasPrecision(18, 2);
                e.HasIndex(x => new { x.ClientId, x.SupplierId, x.Date });
            });
            b.Entity<BillPayment>(e =>
            {
                e.ToTable("fdms_bill_payments");
                e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Reference).HasMaxLength(50);
                e.HasIndex(x => new { x.ClientId, x.BillId });
            });
            b.Entity<LedgerAccount>(e =>
            {
                e.ToTable("fdms_ledger_accounts");
                e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Type).HasMaxLength(10); e.Property(x => x.Role).HasMaxLength(20);
                e.Property(x => x.SubType).HasMaxLength(30); e.Property(x => x.Description).HasMaxLength(300);
                e.HasIndex(x => new { x.ClientId, x.Code }).IsUnique();
                e.HasIndex(x => x.ParentId);
            });
            b.Entity<CostCentre>(e =>
            {
                e.ToTable("fdms_cost_centres");
                e.Property(x => x.Code).HasMaxLength(20); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Kind).HasMaxLength(20);
                e.HasIndex(x => new { x.ClientId, x.Code }).IsUnique();
            });
            b.Entity<RecurringJournal>(e =>
            {
                e.ToTable("fdms_recurring_journals");
                e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Memo).HasMaxLength(200); e.Property(x => x.Reference).HasMaxLength(50);
                e.Property(x => x.LinesJson).HasColumnType("text"); e.Property(x => x.Frequency).HasMaxLength(10); e.Property(x => x.LastEntryNumber).HasMaxLength(20);
                e.Property(x => x.LastError).HasMaxLength(500); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.IsActive, x.NextDate });
                e.HasIndex(x => x.ClientId);
            });
            b.Entity<CashDocument>(e =>
            {
                e.ToTable("fdms_cash_documents");
                e.Property(x => x.Kind).HasMaxLength(10); e.Property(x => x.Number).HasMaxLength(20); e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.Contact).HasMaxLength(120); e.Property(x => x.Reference).HasMaxLength(50); e.Property(x => x.Memo).HasMaxLength(200);
                e.Property(x => x.LinesJson).HasColumnType("text"); e.Property(x => x.Status).HasMaxLength(10);
                e.Property(x => x.Subtotal).HasPrecision(18, 2); e.Property(x => x.Vat).HasPrecision(18, 2); e.Property(x => x.Total).HasPrecision(18, 2); e.Property(x => x.Fee).HasPrecision(18, 2);
                e.Property(x => x.CreatedBy).HasMaxLength(200); e.Property(x => x.PostedBy).HasMaxLength(200); e.Property(x => x.VoidedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.Kind, x.Seq }).IsUnique();
                e.HasIndex(x => new { x.ClientId, x.Status, x.Date });
            });
            b.Entity<YearClose>(e =>
            {
                e.ToTable("fdms_year_closes");
                e.Property(x => x.ResultJson).HasMaxLength(2000); e.Property(x => x.ClosedBy).HasMaxLength(200); e.Property(x => x.ReopenedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.YearEnd });
            });
            b.Entity<JournalEntry>(e =>
            {
                e.ToTable("fdms_journal_entries");
                e.Property(x => x.Number).HasMaxLength(20); e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.Memo).HasMaxLength(200); e.Property(x => x.Reference).HasMaxLength(60);
                e.Property(x => x.Source).HasMaxLength(12); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.Seq }).IsUnique();
                e.HasIndex(x => new { x.ClientId, x.SourceId }).IsUnique();   // a fiscal receipt is posted once; NULLs (manual entries) do not collide
                e.HasIndex(x => new { x.ClientId, x.Date });
                e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.EntryId).OnDelete(DeleteBehavior.Cascade);
            });
            b.Entity<JournalLine>(e =>
            {
                e.ToTable("fdms_journal_lines");
                e.Property(x => x.Debit).HasPrecision(18, 2); e.Property(x => x.Credit).HasPrecision(18, 2); e.Property(x => x.Description).HasMaxLength(200); e.Property(x => x.Currency).HasMaxLength(3);
                e.HasIndex(x => new { x.ClientId, x.AccountId, x.Currency, x.Date });
                e.HasIndex(x => x.CostCentreId);
            });
            b.Entity<BankStatementLine>(e =>
            {
                e.ToTable("fdms_bank_statement_lines");
                e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Description).HasMaxLength(200); e.Property(x => x.Reference).HasMaxLength(60); e.Property(x => x.Fingerprint).HasMaxLength(64);
                e.HasIndex(x => new { x.ClientId, x.AccountId, x.Fingerprint }).IsUnique();
                e.HasIndex(x => new { x.ClientId, x.AccountId, x.Date });
            });
            b.Entity<Currency>(e =>
            {
                e.ToTable("fdms_currencies"); e.HasKey(x => x.Code);
                e.Property(x => x.Code).HasMaxLength(3); e.Property(x => x.Name).HasMaxLength(60); e.Property(x => x.Symbol).HasMaxLength(6);
            });
            b.Entity<Bank>(e =>
            {
                e.ToTable("fdms_banks");
                e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Code).HasMaxLength(20);
                e.HasIndex(x => new { x.ClientId, x.Name }).IsUnique();
            });
            b.Entity<BankAccount>(e =>
            {
                e.ToTable("fdms_bank_accounts");
                e.Property(x => x.AccountName).HasMaxLength(100); e.Property(x => x.AccountNumber).HasMaxLength(34); e.Property(x => x.BranchName).HasMaxLength(100);
                e.Property(x => x.BranchCode).HasMaxLength(20); e.Property(x => x.CurrencyCode).HasMaxLength(3);
                e.HasIndex(x => new { x.ClientId, x.BankId });
            });
            b.Entity<SavedReport>(e =>
            {
                e.ToTable("fdms_saved_reports");
                e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.Question).HasMaxLength(600); e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.CreatedAt });
            });
            b.Entity<AiUsageLog>(e =>
            {
                e.ToTable("fdms_ai_usage");
                e.Property(x => x.Kind).HasMaxLength(10); e.Property(x => x.IpAddress).HasMaxLength(64);
                e.HasIndex(x => x.At); e.HasIndex(x => new { x.ClientId, x.Kind, x.At });
            });
            b.Entity<ApplicationUser>().Property(x => x.AvatarContentType).HasMaxLength(20);
            b.Entity<RefreshToken>(e => { e.Property(x => x.IpAddress).HasMaxLength(64); e.Property(x => x.UserAgent).HasMaxLength(200); e.Property(x => x.AuthMethod).HasMaxLength(12).HasDefaultValue("password"); });
            b.Entity<EmailOutbox>(e =>
            {
                e.ToTable("fdms_email_outbox");
                e.Property(x => x.To).HasMaxLength(200); e.Property(x => x.Subject).HasMaxLength(300); e.Property(x => x.Status).HasMaxLength(10); e.Property(x => x.LastError).HasMaxLength(600);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt });
                e.HasIndex(x => new { x.ClientId, x.CreatedAt });
            });
            b.Entity<Quote>(e =>
            {
                e.ToTable("fdms_quotes");
                e.Property(x => x.Number).HasMaxLength(50); e.Property(x => x.CustomerName).HasMaxLength(200); e.Property(x => x.Status).HasMaxLength(12); e.Property(x => x.Currency).HasMaxLength(3);
                e.Property(x => x.Notes).HasMaxLength(1000);
                e.HasIndex(x => new { x.ClientId, x.Number }).IsUnique();
            });
            b.Entity<RecurringInvoice>(e =>
            {
                e.ToTable("fdms_recurring");
                e.Property(x => x.Name).HasMaxLength(150); e.Property(x => x.CustomerName).HasMaxLength(200); e.Property(x => x.Frequency).HasMaxLength(12); e.Property(x => x.Prefix).HasMaxLength(20);
                e.Property(x => x.Currency).HasMaxLength(3); e.Property(x => x.PaymentMethod).HasMaxLength(20); e.Property(x => x.Notes).HasMaxLength(1000);
                e.HasIndex(x => new { x.IsActive, x.NextRunDate });
            });
            b.Entity<RecurringRun>(e =>
            {
                e.ToTable("fdms_recurring_runs");
                e.Property(x => x.Status).HasMaxLength(8); e.Property(x => x.Error).HasMaxLength(500); e.Property(x => x.InvoiceNo).HasMaxLength(50);
                e.HasIndex(x => new { x.RecurringInvoiceId, x.RunDate }).IsUnique();
            });
            b.Entity<DatabaseBackup>(e =>
            {
                e.ToTable("fdms_backups");
                e.Property(x => x.FileName).HasMaxLength(120);
                e.Property(x => x.Kind).HasMaxLength(12);
                e.Property(x => x.Status).HasMaxLength(12);
                e.Property(x => x.Error).HasMaxLength(1000);
                e.Property(x => x.Sha256).HasMaxLength(64);
                e.Property(x => x.CreatedBy).HasMaxLength(200);
                e.HasIndex(x => x.CreatedAt);
            });
            b.Entity<ApiActivity>(e =>
            {
                e.ToTable("fdms_apiactivity");
                e.Property(x => x.ActorType).HasMaxLength(12);
                e.Property(x => x.ActorName).HasMaxLength(200);
                e.Property(x => x.UserId).HasMaxLength(450);
                e.Property(x => x.Method).HasMaxLength(10);
                e.Property(x => x.Path).HasMaxLength(300);
                e.Property(x => x.ErrorCode).HasMaxLength(60);
                e.Property(x => x.IpAddress).HasMaxLength(64);
                e.Property(x => x.UserAgent).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.At });
                e.HasIndex(x => new { x.DeviceId, x.At });
                e.HasIndex(x => x.At);
            });
            b.Entity<AuditLog>(e =>
            {
                e.ToTable("fdms_auditlogs");
                e.Property(x => x.ActorType).HasMaxLength(12);
                e.Property(x => x.IpAddress).HasMaxLength(64);
                e.Property(x => x.Entity).HasMaxLength(60);
                e.Property(x => x.EntityId).HasMaxLength(64);
                e.HasIndex(x => x.At);
                e.Property(x => x.Action).HasMaxLength(100);
                e.Property(x => x.Detail).HasMaxLength(2000);
                e.Property(x => x.UserId).HasMaxLength(450);
                e.Property(x => x.UserName).HasMaxLength(200);
                e.HasIndex(x => new { x.ClientId, x.At });
            });
        }
    }
}
