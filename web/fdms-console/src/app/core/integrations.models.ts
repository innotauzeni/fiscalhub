/** Zoho Books connection, as the API returns it (never carries a token). */
export interface ZohoOrganization { id: string; name: string; currencyCode: string | null; }
export interface IntegrationPricing { charge: boolean; monthlyFee: number; currency: string; includedDocuments: number; pricePerExtraDocument: number; bridgeMonthlyFee: number; setupFee: number; }
export interface IntegrationCounts { pending: number; fiscalised: number; failed: number; skipped: number; fiscalisedThisMonth: number; notPostedBack: number; }

export interface ZohoConnection {
  available: boolean; unavailableReason: string | null; regions: { key: string; label: string }[];
  id: string | null; status: 'NotConnected' | 'ChooseOrganization' | 'Connected' | 'NeedsReconnect' | string;
  organizationId: string | null; organizationName: string | null; organizations: ZohoOrganization[];
  accountsServer: string | null; connectedBy: string | null; connectedAt: string | null; lastTokenRefreshAt: string | null; accessTokenExpiresAt: string | null;
  lastSyncAt: string | null; lastError: string | null; lastErrorAt: string | null; apiCallsToday: number; apiCallsTotal: number;
  autoFiscalise: boolean; deviceId: string | null; defaultHsCode: string | null; defaultMoneyType: string; postBack: boolean; startDate: string | null;
  webhookUrl: string | null; counts: IntegrationCounts; pricing: IntegrationPricing;
  platformApp: boolean; ownClientId: string | null; redirectUri: string | null;
  accessStatus: 'None' | 'Requested' | 'AwaitingPayment' | 'Active' | 'Rejected' | 'Suspended' | string;
}

export interface IntegrationDocument {
  id: string; kind: 'invoice' | 'creditnote'; externalId: string; externalNumber: string; customerName: string | null; documentDate: string | null; total: number; currency: string;
  externalStatus: string | null; status: 'Pending' | 'Fiscalised' | 'Failed' | 'Skipped'; receiptId: string | null; fiscalInvoiceNo: string | null; error: string | null; attempts: number;
  postedBackAt: string | null; postBackError: string | null; changedAfterFiscalised: boolean; createdAt: string; fiscalisedAt: string | null; externalUrl: string | null;
}
export interface IntegrationDocumentPage { items: IntegrationDocument[]; total: number; page: number; pageSize: number; }
export interface ZohoSyncResult { new: number; updated: number; fiscalised: number; failed: number; postedBack: number; error: string | null; }

export interface ZohoAppSettings { clientId: string | null; hasSecret: boolean; redirectUri: string; configured: boolean; }
export interface IntegrationMonitorRow {
  connectionId: string; clientId: string; company: string; provider: string; status: string; organizationName: string | null; lastTokenRefreshAt: string | null;
  accessTokenExpiresAt: string | null; lastSyncAt: string | null; lastError: string | null; apiCallsToday: number; fiscalisedThisMonth: number; failed: number; pending: number; autoFiscalise: boolean;
}
export interface IntegrationMonitor { connections: IntegrationMonitorRow[]; connected: number; needsAttention: number; fiscalisedThisMonth: number; monthlyRevenue: { currency: string; amount: number }[]; }

export const MONEY_TYPES = ['Credit', 'Cash', 'Card', 'MobileWallet', 'BankTransfer', 'Coupon', 'Other'];

// ---- the Zoho bridge ----
export interface BridgeModule { key: string; label: string; path: string; readOnly: boolean; watchable: boolean; }
export interface BridgeGrant { id: string; partnerId: string; partnerName: string; partnerCode: string; modules: string[]; canWrite: boolean; isActive: boolean; createdBy: string | null; createdAt: string; callsThisMonth: number; }
export interface BridgeGrants { grants: BridgeGrant[]; modules: BridgeModule[]; zohoFullAccess: boolean; monthlyFee: number; currency: string; charged: boolean; }
export interface BridgeKey { id: string; name: string; prefix: string; createdBy: string | null; createdAt: string; lastUsedAt: string | null; revokedAt: string | null; callsToday: number; callsTotal: number; modules?: string[] | null; canWrite?: boolean; }
export interface BridgeEndpoint { webhookUrl: string | null; watchModules: string[]; hasSecret: boolean; }
export interface BridgeCompany { companyId: string; name: string; organizationId: string | null; organizationName: string | null; connectionStatus: string; ready: boolean; notReadyReason: string | null; modules: string[]; canWrite: boolean; grantedAt: string; }
export interface BridgeEvent { id: number; companyId: string; module: string; recordId: string; type: string; status: string; attempts: number; createdAt: string; deliveredAt: string | null; lastError: string | null; }
export interface BridgePortal { keys: BridgeKey[]; endpoint: BridgeEndpoint; companies: BridgeCompany[]; recentEvents: BridgeEvent[]; modules: BridgeModule[]; baseUrl: string; partnerCode: string; }
export interface BridgeMonitorRow { partnerId: string; partner: string; activeKeys: number; companies: number; callsToday: number; callsTotal: number; eventsPending: number; eventsFailed: number; webhookHost: string | null; lastUsedAt: string | null; }
export interface BridgeMonitor { partners: BridgeMonitorRow[]; companiesShared: number; callsToday: number; }
