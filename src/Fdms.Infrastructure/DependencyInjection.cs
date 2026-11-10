using Fdms.Infrastructure.Logging;
using Fdms.Infrastructure.Billing;
using Fdms.Application.Abstractions;
using Fdms.Application.Fdms;
using Fdms.Domain.Entities;
using Fdms.Infrastructure.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Fdms.Application.Import;
using Fdms.Infrastructure.Import;
using Fdms.Infrastructure.Fdms;
using Fdms.Infrastructure.Persistence;
using Fdms.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fdms.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
        {
            var cs = config.GetConnectionString("Default")
                     ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
            // Fixed server version: no database round-trip at startup and `dotnet ef` works offline.
            services.AddDbContext<FdmsDbContext>(o => o.UseMySql(cs, new MySqlServerVersion(new Version(8, 0, 36))));

            services.Configure<FdmsOptions>(config.GetSection(FdmsOptions.Section));
            services.AddSingleton<IFdmsGateway, FdmsGateway>();
            services.AddSingleton<IDeviceSigner, DeviceSigner>();
            services.AddSingleton<ICsrGenerator, CsrGenerator>();
            services.AddSingleton<DeviceLocks>();

            services.AddScoped<DeviceAccessor>();
            services.AddScoped<IDeviceService, DeviceService>();
            services.AddScoped<FiscalDayService>();
            services.AddScoped<IFiscalDayService>(sp => sp.GetRequiredService<FiscalDayService>());
            services.AddScoped<OfflineFileService>();
            services.AddScoped<IOfflineFileService>(sp => sp.GetRequiredService<OfflineFileService>());
            services.AddScoped<IReceiptService, ReceiptService>();
            // people: ASP.NET Identity users/roles (same model as dara) + JWT
            services.AddIdentityCore<ApplicationUser>(o =>
                {
                    o.User.RequireUniqueEmail = true;
                    o.Password.RequiredLength = config.GetValue("Identity:MinPasswordLength", 10);
                    o.Password.RequireDigit = true; o.Password.RequireLowercase = true; o.Password.RequireUppercase = true; o.Password.RequireNonAlphanumeric = false;
                    o.Lockout.MaxFailedAccessAttempts = 5; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15); o.Lockout.AllowedForNewUsers = true;
                })
                .AddRoles<Role>()
                .AddEntityFrameworkStores<FdmsDbContext>()
                .AddDefaultTokenProviders();   // password-reset tokens
            services.Configure<JwtSettings>(config.GetSection(JwtSettings.Section));
            services.AddSingleton<JwtKeyProvider>();
            services.AddSingleton<JwtService>();
            services.TryAddScoped<ICallerScope, NullCallerScope>();   // the API replaces this with the real caller
            services.AddScoped<IPartnerService, PartnerService>();
            services.AddScoped<IReferralService, ReferralService>();
            services.AddScoped<ILegalService, LegalService>();
            services.AddScoped<IAppLinksService, AppLinksService>();
            services.AddHttpClient<IWhatsAppApi, WhatsApp.WhatsAppApi>(c => c.Timeout = TimeSpan.FromSeconds(30));
            services.AddScoped<WhatsApp.WhatsAppSettingsStore>();
            services.AddScoped<WhatsApp.WhatsAppBot>();
            services.AddScoped<IWhatsAppService, WhatsApp.WhatsAppService>();
            services.AddSingleton<WhatsApp.WhatsAppSignal>();
            services.AddHostedService<WhatsApp.WhatsAppWorker>();
            // SMS: the platform's provider account and each company's own (Nhume, Twilio, eSolutions, BulkSMS, Africa's Talking, any HTTPS address)
            services.AddSingleton<ISmsApi, Messaging.HttpSmsApi>();
            services.AddScoped<Messaging.SmsAccountStore>();
            services.AddScoped<Messaging.SmsDispatcher>();
            services.AddScoped<ISmsDispatcher>(sp => sp.GetRequiredService<Messaging.SmsDispatcher>());
            services.AddScoped<ISmsService, Messaging.SmsService>();
            services.AddHostedService<Messaging.SmsWorker>();
            services.AddScoped<IDocumentService, DocumentService>();
            services.AddScoped<IAnalyticsService, AnalyticsService>();
            services.AddScoped<SecurityService>();
            services.AddScoped<ISecurityService>(sp => sp.GetRequiredService<SecurityService>());
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IPasswordResetService, PasswordResetService>();
            services.AddScoped<IApprovalService, ApprovalService>();
            services.AddScoped<IRevenueService, RevenueService>();
            services.AddScoped<IApiTokenService, ApiTokenService>();
            services.AddScoped<IBranchService, BranchService>();
            services.AddScoped<ISettingsService, SettingsService>();
            services.AddScoped<InvoiceTemplateService>();
            services.AddScoped<ITemplateService>(sp => sp.GetRequiredService<InvoiceTemplateService>());
            services.AddScoped<IClientAdminService, ClientAdminService>();
            services.AddScoped<IUserAdminService, UserAdminService>();
            services.AddScoped<IRoleAdminService, RoleAdminService>();
            services.AddScoped<IModuleAdminService, ModuleAdminService>();
            services.AddScoped<IAuditService, AuditService>();
            services.AddScoped<IDashboardService, DashboardService>();
            services.AddScoped<IWebhookService, WebhookService>();
            services.AddScoped<IInvoiceImportService, InvoiceImportService>();
            services.AddScoped<WebhookQueue>();

            // plans, onboarding, subscriptions and payments
            services.AddMemoryCache();
            services.AddHttpContextAccessor();
            services.AddScoped<PublicUrls>();
            services.AddScoped<IPlanService, PlanService>();
            services.AddScoped<IOnboardingService, OnboardingService>();
            services.AddScoped<IBillingService, BillingService>();
            // usage charges (extra receipts, SMS, own WhatsApp number) and paid add-ons, priced by the owner
            services.AddScoped<Billing.UsagePricingStore>();
            services.AddScoped<IUsageBilling, Billing.UsageBilling>();
            services.AddScoped<IAddonService, Billing.AddonService>();
            services.AddScoped<IPaymentService, PaymentService>();
            services.AddScoped<IActivationService, ActivationService>();
            services.AddScoped<ICustomerService, CustomerService>();
            services.AddScoped<IProductService, ProductService>();
            services.AddScoped<IReportService, ReportService>();
            services.AddScoped<IActivityService, ActivityService>();
            services.AddScoped<IBackupService, BackupService>();
            services.AddScoped<IEmailOutbox, EmailOutboxService>();
            services.AddHostedService<EmailOutboxJob>();
            services.AddScoped<IInvoiceShareService, InvoiceShareService>();
            services.AddScoped<IBrandService, BrandService>();
            services.AddScoped<IUsageService, UsageService>();
            services.AddScoped<ISuggestService, SuggestService>();
            services.AddScoped<INotificationService, NotificationService>();
            services.AddScoped<IHealthService, HealthService>();
            services.AddScoped<IQuoteService, QuoteService>();
            services.AddScoped<IRecurringService, RecurringService>();
            services.AddScoped<IVatReturnService, VatReturnService>();
            services.AddHostedService<AlertJob>();
            services.AddHostedService<RecurringJob>();
            services.AddHostedService<BackupJob>();
            services.AddScoped<IComplianceService, ComplianceService>();
            services.AddScoped<IGoLiveService, GoLiveService>();
            services.AddScoped<IBillingProfileService, BillingProfileService>();
            services.AddSingleton<EmailSettingsStore>();
            services.AddHostedService<EmailSettingsLoader>();
            services.AddScoped<IEmailSettingsService, EmailSettingsService>();
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
            services.AddScoped<IPaymentGatewaySettings, PaymentGatewaySettings>();
            services.AddScoped<IAccountingService, AccountingService>();
            services.AddScoped<IAccountingReports, AccountingReports>();
            services.AddScoped<IChartService, ChartService>();
            services.AddScoped<ICostCentreService, CostCentreService>();
            services.AddScoped<IBooksAutomationService, BooksAutomationService>();
            services.AddScoped<IStatementService, StatementService>();
            services.AddScoped<ISalesInvoiceService, SalesInvoiceService>();
            services.AddScoped<IInventoryService, InventoryService>();
            services.AddScoped<IInventoryImportService, InventoryImportService>();
            services.AddScoped<IConversionService, ConversionService>();
            services.AddScoped<IExchangeRateService, ExchangeRateService>();
            services.AddScoped<IScheduleService, ScheduleService>();
            services.AddScoped<IFinancialStatementsService, FinancialStatementsService>();
            services.AddScoped<IReportExporter, ReportExporter>();
            services.AddScoped<ICashDocumentService, CashDocumentService>();
            services.AddHostedService<RecurringJournalJob>();
            services.AddScoped<IPayablesService, PayablesService>();
            services.AddScoped<IReceivablesService, ReceivablesService>();
            services.AddScoped<IFixedAssetService, FixedAssetService>();
            services.AddScoped<IVatBooksService, VatBooksService>();
            services.AddScoped<IBudgetService, BudgetService>();
            services.AddScoped<IBankReconService, BankReconService>();
            services.AddScoped<ICurrencyService, CurrencyService>();
            services.AddScoped<IBankService, BankService>();
            services.AddScoped<IAiSettingsService, AiSettingsService>();
            services.AddHttpClient<IGeminiClient, GeminiClient>(c => c.Timeout = TimeSpan.FromSeconds(40));
            services.AddScoped<AiReportEngine>();
            services.AddScoped<IAiReportService, AiReportService>();
            services.AddScoped<IAssistantService, AssistantService>();
            services.AddScoped<IProfileService, ProfileService>();
            services.AddHttpClient<IPaynowGateway, PaynowGateway>();
            services.AddHttpClient<IEcoCashGateway, EcoCashGateway>(c => c.Timeout = TimeSpan.FromSeconds(30));
            services.AddHttpClient<IStripeGateway, StripeGateway>();
            services.AddHostedService<BillingJob>();
            services.AddHostedService<PendingPaymentsJob>();
            services.AddScoped<IDemoCleanupService, DemoCleanupService>();
            services.AddHostedService<DemoCleanupJob>();
            services.AddHttpClient<IZohoApi, Integrations.ZohoApi>(c => c.Timeout = TimeSpan.FromSeconds(60));
            services.AddScoped<Integrations.IntegrationSettings>();
            services.AddScoped<Integrations.IntegrationService>();
            services.AddScoped<IIntegrationService>(sp => sp.GetRequiredService<Integrations.IntegrationService>());
            services.AddScoped<IBridgeService, Integrations.BridgeService>();
            services.AddSingleton<IBridgeSender, Integrations.HttpBridgeSender>();
            services.AddScoped<IIntegrationBilling, Integrations.IntegrationBilling>();
            services.AddScoped<IIntegrationAccessService, Integrations.IntegrationAccessService>();
            services.AddHostedService<Integrations.IntegrationSyncJob>();
            // QuickBooks, Xero, Sage and Odoo on one engine; each adapter has its own HTTP client
            services.AddHttpClient<Integrations.Connectors.QuickBooksConnector>(c => c.Timeout = TimeSpan.FromSeconds(60));
            services.AddHttpClient<Integrations.Connectors.XeroConnector>(c => c.Timeout = TimeSpan.FromSeconds(60));
            services.AddHttpClient<Integrations.Connectors.SageConnector>(c => c.Timeout = TimeSpan.FromSeconds(60));
            services.AddHttpClient<Integrations.Connectors.OdooConnector>(c => c.Timeout = TimeSpan.FromSeconds(60))
                .ConfigurePrimaryHttpMessageHandler(sp =>
                {
                    // Odoo's address is the company's: connect only to public addresses, checked on the address actually dialled
                    var allowPrivate = sp.GetRequiredService<IConfiguration>().GetValue("Connectors:Odoo:AllowPrivateAddresses", false);
                    return new System.Net.Http.SocketsHttpHandler
                    {
                        AllowAutoRedirect = false,
                        ConnectCallback = async (ctx, ct) =>
                        {
                            var ips = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                            var ip = ips.FirstOrDefault(a => allowPrivate || Integrations.Connectors.OdooConnector.IsPublic(a))
                                     ?? throw new HttpRequestException($"{ctx.DnsEndPoint.Host} is not a public internet address.");
                            var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                            try { await socket.ConnectAsync(new System.Net.IPEndPoint(ip, ctx.DnsEndPoint.Port), ct); return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true); }
                            catch { socket.Dispose(); throw; }
                        },
                    };
                });
            services.AddScoped<IAccountingConnector>(sp => sp.GetRequiredService<Integrations.Connectors.QuickBooksConnector>());
            services.AddScoped<IAccountingConnector>(sp => sp.GetRequiredService<Integrations.Connectors.XeroConnector>());
            services.AddScoped<IAccountingConnector>(sp => sp.GetRequiredService<Integrations.Connectors.SageConnector>());
            services.AddScoped<IAccountingConnector>(sp => sp.GetRequiredService<Integrations.Connectors.OdooConnector>());
            services.AddScoped<IConnectorService, Integrations.Connectors.ConnectorService>();

            services.AddScoped<IErrorLogService, ErrorLogService>();
            services.AddSingleton<ErrorLogQueue>();
            services.AddSingleton<ErrorLogFlusher>();
            services.AddHostedService(sp => sp.GetRequiredService<ErrorLogFlusher>());
            services.AddHostedService<LogRetentionService>();
            services.AddHostedService<PendingReceiptRetryJob>();
            services.AddHostedService<DeviceMaintenanceJob>();
            services.AddHostedService<FiscalDayAutoCloseJob>();
            services.AddHostedService<WebhookDeliveryJob>();
            services.AddHostedService<OfflineFileJob>();
            return services;
        }
    }
}
