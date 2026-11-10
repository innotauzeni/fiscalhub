// Shapes returned by the gateway API. The server omits null properties, so nullable fields are optional here.

export interface UserInfo {
  id: string;
  clientId?: string;
  clientName?: string;
  email: string;
  userName: string;
  name: string;
  surname: string;
  roles: string[];
  mustChangePassword: boolean;
}

export interface SubmoduleNode { id: number; name: string; icon: string; route: string; accessName?: string; order: number; }
export interface MenuNode { id: number; sectionModuleId: number; name: string; slugname: string; icon: string; accessName?: string; sortOrder: number; children: SubmoduleNode[]; }
export interface SectionNode { id: number; name: string; color: string; sortOrder: number; children: MenuNode[]; }

export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
  expiresAt: string;
  user: UserInfo;
  permissions: string[];
  sectionMenus: SectionNode[];
  menus: MenuNode[];
  /** a new account that has not paid its first invoice: only Billing is available */
  billingLocked?: boolean;
  /** on a trial: demo (Test) devices only */
  demoOnly?: boolean;
  /** the password was right but a code from the authenticator app is still needed: send mfaToken with it to /auth/login/mfa */
  mfaRequired?: boolean;
  mfaToken?: string;
  /** the company requires two-step sign-in or a passkey and this person has not set it up yet: only My profile works until they do */
  mfaSetupRequired?: boolean;
}

export interface Problem { title?: string; status?: number; errorCode?: string; }

// ---- fiscal ----
export interface DeviceTax { taxId: number; taxPercent?: number; taxName: string; validFrom: string; validTill?: string; }
export interface Device {
  id: string; zimraDeviceId: number; environment: string; serialNo: string; taxPayerName?: string; taxPayerTin?: string; vatNumber?: string;
  branchName?: string; operatingMode: string; certificateValidTill?: string; qrUrl?: string; isActive: boolean; taxes: DeviceTax[];
  /** our own branch grouping (branchName above is the name ZIMRA holds) */
  branchId?: string; assignedBranch?: string;
  /** the company's own name for this machine, e.g. "Till 2, front counter" */
  name?: string;
}
export interface FiscalCounterView { type: string; currency: string; taxId?: number; taxPercent?: number; moneyType?: string; value: number; }
export interface FiscalDay {
  deviceId: string; fiscalDayNo: number; status: string; openedAt: string; closedAt?: string; lastReceiptCounter: number;
  closingErrorCode?: string; counters: FiscalCounterView[];
}
export interface ValidationError { code: string; color: string; }
export interface Receipt {
  id: string; deviceId: string; externalRef: string; invoiceNo: string; type: string; currency: string; status: string; fiscalDayNo: number;
  receiptCounter: number; receiptGlobalNo: number; receiptDate: string; total: number; fdmsReceiptId?: number; serverDate?: string; qrUrl?: string;
  deviceSignatureHash: string; validationErrors: ValidationError[]; errorCode?: string; errorMessage?: string; holdUntil?: string;
}
export interface Paged<T> { items: T[]; page: number; pageSize: number; total: number; }
export interface OfflineFile {
  id: number; fiscalDayNo: number; sequence: number; receiptCount: number; hasFooter: boolean; status: string; operationId?: string;
  errors: string[]; lastError?: string; attempts: number; uploadedAt?: string; processedAt?: string;
}
export interface DeviceSummary {
  id: string; zimraDeviceId: number; taxPayerName?: string; branchName?: string; operatingMode: string; dayStatus: string; dayNo?: number;
  lastReceiptCounter: number; certificateValidTill?: string;
  dayOpenedAt?: string; closingErrorCode?: string; dayMaxHrs: number; pendingReceipts: number; heldReceipts: number; offlineWaiting: number; name?: string;
}
export interface TrendPoint { date: string; count: number; rejected: number; }
export interface Dashboard {
  devices: number; openDays: number; receiptsToday: number; todayTotals: { currency: string; total: number; count: number }[];
  pending: number; held: number; offlineIssued: number; rejected: number; submitted: number; deviceList: DeviceSummary[]; recent: Receipt[]; alerts: string[];
  trend: TrendPoint[]; autoCloseTime?: string; checklist?: Checklist; usage?: UsageMeter;
}
export interface PlatformDashboard {
  clients: number; activeClients: number; users: number; devices: number; openDays: number; receiptsToday: number; rejected: number; pending: number;
  trend: TrendPoint[]; topClients: { id: string; name: string; devices: number; receiptsWeek: number }[]; recentActivity: AuditEntry[]; alerts: string[]; attention?: AttentionItem[];
}
export interface LineRequest { name: string; hsCode?: string; quantity: number; unitPrice: number; taxId?: number; taxPercent?: number; isExempt: boolean; isDiscount: boolean; }
export interface SubmitReceiptRequest {
  externalRef: string; invoiceNo: string; receiptType: string; currency: string; linesTaxInclusive: boolean; lines: LineRequest[];
  payments?: { moneyType: string; amount: number }[]; buyer?: { registerName: string; tin: string; vatNumber?: string; email?: string; phone?: string };
  notes?: string; originalReceiptId?: string; autoOpenDay: boolean; holdMinutes: number;
}
export interface PreviewResult {
  type: string; currency: string; linesTaxInclusive: boolean; total: number;
  lines: { lineNo: number; name: string; price: number; quantity: number; total: number; taxId: number; taxPercent?: number }[];
  taxes: { taxId: number; taxPercent?: number; taxAmount: number; salesAmountWithTax: number }[];
  payments: { moneyType: string; amount: number }[];
}
export interface ImportItem { invoiceNo?: string; status: string; receiptId?: string; total?: number; errorCode?: string; error?: string; warnings: string[]; request?: SubmitReceiptRequest; }
export interface ImportResult { total: number; succeeded: number; failed: number; submitted: boolean; items: ImportItem[]; errors: string[]; }

// ---- integration ----
export interface ApiToken {
  id: string; deviceId: string; zimraDeviceId: number; name: string; prefix: string; scopes: string[]; createdAt: string; expiresAt?: string;
  revokedAt?: string; lastUsedAt?: string; createdBy?: string;
}
export interface CreatedToken { token: ApiToken; secret: string; }
export interface Scope { name: string; displayName: string; }
export interface WebhookConfig { url?: string; secret?: string; }
export interface WebhookDelivery { id: string; eventType: string; status: string; attempts: number; nextAttemptAt: string; deliveredAt?: string; lastError?: string; createdAt: string; }

// ---- administration ----
export interface Client { id: string; name: string; email?: string; isActive: boolean; userCount: number; deviceCount: number; createdAt: string; }
export interface UserDto {
  id: string; email: string; userName: string; name: string; surname: string; clientId?: string; clientName?: string; roles: string[];
  status: string; lastLoginDate?: string; mustChangePassword: boolean; accessScope: 'all' | 'restricted';
}
export interface TemplateSettings {
  accent: string; showLogo: boolean; logoAlign: 'left' | 'center' | 'right'; displayName?: string; headerText?: string; invoiceTitle?: string;
  showBuyer: boolean; showPayments: boolean; showHsCode: boolean; showQuantity: boolean; showUnitPrice: boolean; showLineTax: boolean;
  bankDetails?: string; terms?: string; footerText?: string;
  headerStyle?: string; titleStyle?: string; titleAlign?: string; tableStyle?: string; partiesLayout?: string; totalsAlign?: string; qrPosition?: string; logoSize?: string; margins?: string; fontSize?: number;
  columns?: string[] | null;
  labelInvoiceNo?: string; labelDate?: string; labelBuyer?: string; labelItem?: string; labelHsCode?: string; labelQuantity?: string; labelPrice?: string; labelTax?: string; labelTotal?: string;
  address?: string; phone?: string; email?: string; website?: string;
  qrSize?: string; rowSpacing?: string; labelGrandTotal?: string;
  /** A free layout made in the invoice builder (A4); kept as it is by this editor. */
  canvas?: { background: string; elements: unknown[] } | null;
}
export interface TemplateAnalysis { settings: TemplateSettings; notes: string[]; summary?: string | null }
export interface InvoiceTemplate { id: string; branchId?: string; branchName?: string; name: string; settings: TemplateSettings; hasLogo: boolean; updatedAt: string; deviceCount: number; }
export interface Branch { id: string; code: string; name: string; address?: string; phone?: string; isActive: boolean; deviceIds: string[]; userCount: number; }
export interface UserAccess { scope: 'all' | 'restricted'; branchIds: string[]; deviceIds: string[]; effectiveDeviceIds: string[]; }
export interface Role { id: string; name: string; type?: string; level?: string; description?: string; permissionCount: number; menuCount: number; userCount: number; }
export interface Permission { id: number; name: string; displayName: string; module: string; }
export interface RoleAccess { roleId: string; roleName: string; permissionIds: number[]; moduleIds: number[]; }
export interface Section { id: number; name: string; color: string; sortOrder: number; status: boolean; }
export interface ModuleDef { id: number; sectionModuleId: number; name: string; slugname: string; icon: string; description?: string; accessName?: string; sortOrder: number; isActive: boolean; }
export interface SubmoduleDef { id: number; moduleId: number; name: string; icon: string; route: string; accessName?: string; order: number; isActive: boolean; }
export interface ModuleTree { sections: Section[]; modules: ModuleDef[]; submodules: SubmoduleDef[]; }
export interface AuditRow {
  id: number; clientId?: string; clientName?: string; userId?: string; userName?: string; actorType: 'user' | 'token' | 'system'; action: string;
  entity?: string; entityId?: string; detail?: string; ipAddress?: string; at: string;
}
export interface AuditPage { items: AuditRow[]; page: number; pageSize: number; total: number; actions: string[]; }
export interface ErrorLogEntry {
  id: number; uuid: string; severity: 'Critical' | 'Error' | 'Warning' | 'Info'; source?: string; message: string; exceptionType?: string; stackTrace?: string;
  path?: string; method?: string; statusCode?: number; ipAddress?: string; userName?: string; clientId?: string; clientName?: string; traceId?: string;
  machineName?: string; environment?: string; occurrences: number; isResolved: boolean; resolution?: string; resolvedBy?: string; resolvedAt?: string;
  createdAt: string; lastSeenAt: string;
}
export interface ErrorLogPage { items: ErrorLogEntry[]; page: number; pageSize: number; total: number; stats: { unresolved: number; criticalUnresolved: number; today: number; total: number }; }
export interface AuditEntry { id: number; clientId?: string; clientName?: string; userName?: string; action: string; detail?: string; at: string; }

// ---- plans, onboarding, billing ----
export interface Plan {
  id: number; code: string; name: string; description?: string; priceMonthly: number; priceYearly: number; currency: string;
  maxDevices: number; maxUsers: number; maxBranches: number; maxReceiptsPerMonth: number; pricePerExtraReceipt?: number; pricePerDeviceMonthly?: number; pricePerThousandReceipts?: number; pricePerDeviceYearly?: number; receiptPricing?: 'flat' | 'metered' | 'tiered';
  receiptMinimumMonthly?: number; receiptCapMonthly?: number; receiptTiers?: { upTo: number | null; price: number }[]; modules?: string[]; features: string[]; trialDays: number;
  isActive: boolean; isPublic: boolean; sortOrder: number; subscribers: number;
  /** fiscal, or connector (Zoho Books connector only). */
  kind?: string;
}
export interface ClientApplication {
  id: string; companyName: string; tin?: string; contactName: string; contactSurname: string; email: string; phone?: string; planId?: number; planName?: string;
  billingCycle: 'monthly' | 'yearly'; status: 'Unverified' | 'Pending' | 'Approved' | 'Rejected'; message?: string; decisionReason?: string; decidedBy?: string; decidedAt?: string;
  clientId?: string; createdAt: string; ipAddress?: string;
  hasLogo?: boolean;
}
export interface SubscriptionRow {
  id: string; clientId: string; clientName?: string; planId: number; planName: string; status: 'Trial' | 'Active' | 'PastDue' | 'Suspended' | 'Cancelled';
  billingCycle: 'monthly' | 'yearly'; startedAt: string; currentPeriodStart: string; currentPeriodEnd: string; trialEndsAt?: string; outstanding: number;
}
export interface Usage { devices: number; users: number; branches: number; receiptsThisMonth: number; }
export interface BankDetails { bankName?: string; accountName?: string; accountNumber?: string; branchCode?: string; swift?: string; instructions?: string; }
/** One way to pay. kind: redirect (leaves for the gateway's page), mobile (a prompt on the phone), manual (pay, then tell us). */
export interface PayMethod { id: string; name: string; kind: 'redirect' | 'mobile' | 'manual'; automatic: boolean; details?: string; }
export interface PaymentOptions { paynow: boolean; paynowMobile: boolean; bankTransfer: boolean; bank?: BankDetails; currency: string; methods: PayMethod[]; }
export interface PaymentRow {
  id: string; invoiceId: string; invoiceNumber: string; clientId: string; clientName?: string; method: 'Paynow' | 'EcoCash' | 'Stripe' | 'BankTransfer' | 'EcoCashManual' | 'PayPal' | 'Cash' | 'Other';
  status: 'Pending' | 'AwaitingConfirmation' | 'CodeSent' | 'Paid' | 'Failed' | 'Cancelled' | 'Rejected'; amount: number; currency: string; reference?: string;
  redirectUrl?: string; instructions?: string; hasProof: boolean; notes?: string; confirmedBy?: string; createdAt: string; confirmedAt?: string;
}
export interface BillingInvoice {
  id: string; number: string; clientId: string; clientName?: string; description: string; subtotal: number; taxPercent: number; taxAmount: number; total: number;
  currency: string; status: 'Open' | 'Paid' | 'Void'; issuedAt: string; dueAt: string; paidAt?: string; periodStart: string; periodEnd: string;
  paymentMethod?: string; paymentReference?: string; payments: PaymentRow[];
}
/** locked: only Billing works. reason: NEW_ACCOUNT (first invoice unpaid), TRIAL_EXPIRED or EXPIRED. liveApproved: may register live (Production) devices. */
export interface AccessState { locked: boolean; demoOnly: boolean; reason?: 'NEW_ACCOUNT' | 'TRIAL_EXPIRED' | 'EXPIRED'; expiredAt?: string; liveApproved: boolean; }
export interface BillingOverview { subscription?: SubscriptionRow; plan?: Plan; usage: Usage; invoices: BillingInvoice[]; plans: Plan[]; options: PaymentOptions; access?: AccessState; resellerName?: string | null; resellerDiscountPercent?: number; }
export interface IssuedCode { code: string; expiresAt: string; emailed: boolean; }
/** One of the six documents ZIMRA wants before a taxpayer goes live (USD and ZWG invoice, credit note, debit note from a test device). */
export interface GoLiveTestDoc { slot: string; currency: string; type: string; receiptId?: string | null; invoiceNo?: string | null; date?: string | null; total?: number | null; receiptGlobalNo?: number | null; fiscalDayNo?: number | null; zimraDeviceId?: number | null; original?: string | null; problem?: string | null }
export interface GoLiveDevice { deviceId: number; activationKey?: string | null; serialNo?: string | null }
export interface GoLiveRequestRow {
  id: string; clientId: string; clientName?: string; status: 'Pending' | 'SentToZimra' | 'AwaitingPayment' | 'Approved' | 'Rejected'; fee?: number; invoiceId?: string; invoiceNumber?: string; contactName: string; contactPhone: string;
  zimraTin?: string; notes?: string; decisionReason?: string; decidedBy?: string; decidedAt?: string; createdAt: string;
  testDocuments?: GoLiveTestDoc[] | null; sentToZimraAt?: string | null; sentToZimraBy?: string | null; zimraEmail?: string | null; liveDevice?: GoLiveDevice | null;
}
export interface GoLiveStatus { approved: boolean; approvedAt?: string; latest?: GoLiveRequestRow; canRequest: boolean; blocked?: string; testDocuments?: GoLiveTestDoc[]; testSetComplete?: boolean }
export interface BillingProfile {
  companyName: string; address?: string; taxNumber?: string; email?: string; phone?: string; website?: string; accent: string; invoicePrefix: string; taxPercent: number;
  bankName?: string; accountName?: string; accountNumber?: string; branchCode?: string; swift?: string; paymentInstructions?: string; terms?: string; footer?: string;
}
export interface BillingProfileInfo { profile: BillingProfile; hasLogo: boolean; paynowConfigured: boolean; paynowMode: string; }
export interface PublicInfo { name: string; email?: string; phone?: string; website?: string; }

// ---- customers, items, reports, compliance ----
export interface Customer { id: string; name: string; tradeName?: string; tin?: string; vatNumber?: string; email?: string; phone?: string; address?: string; notes?: string; isActive: boolean; createdAt: string; }
export interface Product { id: string; code?: string; name: string; hsCode?: string; unit?: string; unitPrice: number; currency: string; taxPercent?: number; isExempt: boolean; isActive: boolean; createdAt: string; }
export interface TaxLine { label: string; taxPercent?: number; sales: number; tax: number; }
export interface CurrencyReport {
  currency: string; invoices: number; invoicesTotal: number; creditNotes: number; creditNotesTotal: number; debitNotes: number; debitNotesTotal: number;
  netSales: number; netTax: number; taxes: TaxLine[]; payments: { moneyType: string; amount: number }[];
}
export interface SalesReport {
  from: string; to: string; truncated: boolean; counts: { counted: number; pending: number; offlineIssued: number; rejected: number; cancelled: number; held: number };
  currencies: CurrencyReport[]; days: { date: string; currency: string; documents: number; net: number }[];
}
export interface FiscalDayReportRow { deviceZimraId: number; fiscalDayNo: number; status: string; openedAt: string; closedAt?: string; receipts: number; offline: boolean; totals: { currency: string; type: string; value: number }[]; }
export interface ComplianceCheck { id: string; area: string; title: string; status: 'ok' | 'info' | 'warn' | 'critical'; detail: string; device?: string; route?: string; }
export interface ComplianceReport { score: number; status: 'ok' | 'warn' | 'critical'; ok: number; warnings: number; critical: number; checks: ComplianceCheck[]; checkedAt: string; }

export interface ActivityRow {
  id: number; clientId: string; clientName?: string; deviceId?: string; zimraDeviceId?: number; actorType: 'user' | 'token'; actorName?: string;
  method: string; path: string; statusCode: number; durationMs: number; errorCode?: string; ipAddress?: string; userAgent?: string; at: string;
}
export interface ActivityPage { items: ActivityRow[]; total: number; page: number; pageSize: number; }
export interface DeviceActivity {
  deviceId: string; zimraDeviceId: number; name?: string; branch?: string; isActive: boolean; lastSeenAt?: string; lastTokenCallAt?: string;
  calls24h: number; errors24h: number; receipts24h: number; tokenCount: number; lastActor?: string;
}

export interface Backup {
  id: string; fileName: string; sizeBytes: number; kind: 'manual' | 'scheduled'; status: 'Running' | 'Ok' | 'Failed'; error?: string;
  tables: number; rows: number; sha256?: string; createdBy?: string; createdAt: string; finishedAt?: string; fileExists: boolean;
}
export interface BackupInfo { supported: boolean; provider: string; folder: string; scheduleEnabled: boolean; intervalHours: number; keepLast: number; lastOkAt?: string; nextDueAt?: string; }
export interface BackupList { info: BackupInfo; items: Backup[]; }

// ---- sending invoices, alerts, health, usage, quotes, recurring, VAT, branding
export interface ShareLink { url: string; token: string; expiresAt: string; whatsAppText: string; }
export interface SendResult { sent: boolean; emailConfigured: boolean; to: string; message: string; queued?: boolean; }
export interface OutboxItem { id: string; to: string; subject: string; status: 'Pending' | 'Sent' | 'Failed' | 'Cancelled'; attempts: number; nextAttemptAt: string; lastError?: string; createdAt: string; sentAt?: string; }
export interface PublicInvoice {
  companyName: string; slug?: string; hasLogo: boolean; accent: string; invoiceNo: string; type: string; currency: string; total: number; date: string; status: string;
  globalNo?: number; fiscalDayNo?: number; qrUrl?: string; statusText: string;
}
export interface AppNotification { id: string; kind: string; severity: 'info' | 'warn' | 'bad'; title: string; body?: string; link?: string; createdAt: string; read: boolean; }
export interface NotificationList { unread: number; items: AppNotification[]; }
export interface AlertSettings { emails?: string; enabledKinds: string[]; kinds: { id: string; label: string; help: string }[]; emailConfigured: boolean; fallbackNote: string; }
export interface HealthCheck { id: string; title: string; status: 'ok' | 'warn' | 'bad' | 'info'; detail: string; fixAction?: string; fixLabel?: string; }
export interface DeviceHealth { deviceId: string; zimraDeviceId: number; name?: string; status: 'ok' | 'warn' | 'bad'; checks: HealthCheck[]; }
export interface UsageItem { key: string; label: string; used: number; limit: number; percent: number; level: 'ok' | 'warn' | 'over'; }
export interface UsageMeter { plan?: string; periodStart: string; periodEnd: string; limited: boolean; items: UsageItem[]; }
export interface ChecklistStep { id: string; title: string; help: string; done: boolean; route: string; action: string; }
export interface Checklist { done: number; total: number; steps: ChecklistStep[]; }
export interface AttentionItem { kind: string; severity: 'bad' | 'warn' | 'info'; clientId: string; clientName: string; title: string; detail: string; route?: string; }
export interface QuoteLine { name: string; hsCode?: string; quantity: number; unitPrice: number; taxPercent?: number | null; isExempt: boolean; }
export interface Quote {
  id: string; number: string; customerName: string; customerTin?: string; customerVat?: string; customerEmail?: string; customerPhone?: string; currency: string; taxInclusive: boolean;
  validUntil?: string; notes?: string; status: 'Draft' | 'Sent' | 'Accepted' | 'Declined' | 'Invoiced'; lines: QuoteLine[]; total: number; invoiceReceiptId?: string; createdAt: string;
}
export interface RecurringRun { id: string; runDate: string; receiptId?: string; invoiceNo?: string; status: 'Ok' | 'Failed'; error?: string; createdAt: string; }
export interface Recurring {
  id: string; deviceId: string; zimraDeviceId: number; name: string; customerName: string; customerTin?: string; customerVat?: string; customerEmail?: string; customerPhone?: string;
  currency: string; taxInclusive: boolean; paymentMethod: string; notes?: string; lines: QuoteLine[]; frequency: string; nextRunDate: string; endDate?: string; isActive: boolean;
  prefix: string; nextNumber: number; emailCustomer: boolean; total: number; recent: RecurringRun[];
}
export interface VatLine { label: string; taxPercent?: number; salesInclusive: number; vat: number; salesExclusive: number; }
export interface VatCurrency { currency: string; lines: VatLine[]; totalSalesInclusive: number; totalVat: number; totalSalesExclusive: number; invoices: number; invoicesTotal: number; creditNotes: number; creditNotesTotal: number; debitNotes: number; debitNotesTotal: number; }
export interface VatReturn { taxpayerName: string; tin?: string; vatNumber?: string; from: string; to: string; deviceLabel: string; truncated: boolean; pending: number; offlineIssued: number; currencies: VatCurrency[]; generatedAt: string; }
export interface ItemSuggestion { name: string; hsCode?: string; unitPrice: number; currency: string; taxPercent?: number; isExempt: boolean; source: 'catalogue' | 'history'; }
export interface PublicBrand { name: string; slug?: string; accent: string; hasLogo: boolean; }
export interface CompanyBrand { slug?: string; accent: string; hasLogo: boolean; signInUrl?: string; }

// ---- the platform owner's revenue -------------------------------------------------------------------------------------------
export interface Amount { currency: string; amount: number; }
export interface RevenueClient { clientId: string; company: string; plan?: string; status: string; paidAllTime: Amount[]; outstanding: Amount[]; lastPaidAt?: string; }
export interface Revenue {
  mrr: Amount[]; arr: Amount[]; thisMonth: Amount[]; lastMonth: Amount[]; last12Months: Amount[]; allTime: Amount[]; outstanding: Amount[]; overdue: Amount[]; arpuMonthly: Amount[];
  counts: { companies: number; active: number; paying: number; trial: number; pastDue: number; suspended: number; cancelled: number; newThisMonth: number; trialsEndingIn7Days: number; awaitingConfirmation: number };
  trialConversionPercent: number;
  months: { month: string; collected: Amount[]; invoices: number }[];
  plans: { planId: number; plan: string; priceMonthly: number; currency: string; paying: number; trial: number; mrr: number }[];
  methods: { method: string; collected: Amount[]; payments: number }[];
  topClients: RevenueClient[]; owing: RevenueClient[];
  coinsOutstanding: number; coinsLiability: Amount[]; referralMoneyOwed: Amount[]; partnerCommissionOwed: Amount[]; generatedAt: string;
}

// ---- approvals (maker-checker) ------------------------------------------------------------------------------------------------
export interface ApprovalAction { key: string; label: string; description: string; amountMeaning: string; }
export interface ApprovalRule { id: string; action: string; actionLabel: string; minAmount: number; currency?: string; levels: string[]; isActive: boolean; }
export interface ApprovalMatrix { rows: { action: ApprovalAction; rules: ApprovalRule[] }[]; roles: string[]; }
export interface ApprovalDecision { level: number; role: string; userName: string; decision: string; note?: string; at: string; }
export interface ApprovalRequest {
  id: string; action: string; actionLabel: string; status: 'Pending' | 'Approved' | 'Rejected' | 'Cancelled' | 'Failed'; summary: string; amount: number; currency?: string; entityId?: string;
  levels: string[]; currentLevel: number; waitingForRole?: string; requestedBy: string; createdAt: string; decidedAt?: string; error?: string; canDecide: boolean; canCancel: boolean; decisions: ApprovalDecision[];
}
export interface ApprovalList { items: ApprovalRequest[]; waitingForMe: number; }
/** What an action answers (HTTP 202) when it was held for approval instead of being carried out. */
export interface ApprovalRequired { approvalRequired: true; message: string; request: ApprovalRequest; }
export function isApprovalRequired(x: unknown): x is ApprovalRequired { return !!x && typeof x === 'object' && (x as ApprovalRequired).approvalRequired === true; }

// ---- the mail server ------------------------------------------------------------------------------------------------------------
export interface EmailSettings { source: 'database' | 'config' | 'none'; enabled: boolean; host?: string; port: number; enableSsl: boolean; user?: string; hasPassword: boolean; fromAddress?: string; fromName?: string; notifyEmails?: string; configured: boolean; }
/** The toast for an action's answer: "sent for approval" when it was held, otherwise the given success text. */
export function doneOrHeld(res: unknown, done: string): string {
  return isApprovalRequired(res) ? `Sent for approval: waiting for ${res.request.waitingForRole ?? 'an approver'}. You will be told when it is decided (see Approvals).` : done;
}

/** The optional modules a plan can include (the same keys as the API's PlanModules). Fiscalising itself is always included. */
export const PLAN_MODULES: { key: string; label: string; help: string }[] = [
  { key: 'accounting', label: 'Accounting', help: 'Journals, ledger, cashbook, bank reconciliation, bills, assets, VAT return, budgets' },
  { key: 'templates', label: 'Invoice designer', help: 'Custom invoice templates and the free-layout builder' },
  { key: 'approvals', label: 'Approvals', help: 'Maker-checker approval rules' },
  { key: 'quotes', label: 'Quotes and recurring invoices', help: 'Quotes that become invoices; scheduled invoices' },
  { key: 'pos', label: 'Quick sale (POS)', help: 'The point-of-sale screen' },
  { key: 'ai', label: 'AI reports', help: 'Questions about your sales in plain words' },
  { key: 'api', label: 'API tokens and webhooks', help: 'Connect your own software' },
  { key: 'messaging', label: 'WhatsApp and SMS', help: 'WhatsApp assistant, own number and SMS account' },
  { key: 'connectors', label: 'Accounting connectors', help: 'QuickBooks, Xero, Sage and Odoo (each also applied for)' },
  { key: 'zoho', label: 'Zoho Books', help: 'The Zoho Books integration' },
];
export function planModuleLabels(modules: string[] | null | undefined): string[] {
  return PLAN_MODULES.filter(x => !modules || modules.includes(x.key)).map(x => x.label);
}

/** One line describing how a plan charges for receipts and live devices beyond its price (empty for a flat plan). */
export function usagePricingLines(p: Plan): string[] {
  const out: string[] = [];
  const c = p.currency;
  if (p.pricePerDeviceMonthly) out.push(`+ ${p.pricePerDeviceMonthly} ${c} per live device / month${p.pricePerDeviceYearly ? ` (${p.pricePerDeviceYearly} ${c} / year)` : ''}, test devices free`);
  if (p.receiptPricing === 'metered' && p.pricePerThousandReceipts) {
    let l = `+ ${p.pricePerThousandReceipts} ${c} per 1,000 receipts`;
    if (p.receiptMinimumMonthly) l += `, at least ${p.receiptMinimumMonthly} ${c} / month`;
    if (p.receiptCapMonthly) l += `, never more than ${p.receiptCapMonthly} ${c} / month`;
    out.push(l);
  }
  if (p.receiptPricing === 'tiered') for (const t of p.receiptTiers ?? []) out.push(`${t.upTo == null ? 'More receipts' : 'Up to ' + t.upTo.toLocaleString() + ' receipts'} / month: + ${t.price} ${c}`);
  return out;
}
