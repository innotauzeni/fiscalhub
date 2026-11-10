// Shapes of the AI report builder, the sign-up assistant, the Gemini settings and "My profile" (see AiContracts.cs on the server).

export interface ReportFilters { deviceIds?: string[] | null; item?: string | null; paymentMethod?: string | null; currency?: string | null; documentType?: string | null; buyer?: string | null }
export interface ReportSpec { title?: string | null; metric: string; groupBy: string; period: string; from?: string | null; to?: string | null; filters?: ReportFilters | null; top: number; order: string; chart: string }
export interface ReportColumn { key: string; label: string; kind: 'text' | 'money' | 'number' | 'percent' }
export interface ReportTotal { currency: string; value: number; label: string }
export interface ReportResult {
  title: string; metric: string; metricLabel: string; groupBy: string; groupLabel: string; from: string; to: string; columns: ReportColumn[]; rows: (string | number | null)[][];
  totals: ReportTotal[]; chart: string; documentsCounted: number; truncated: boolean; notes: string[];
}
export interface AskReportResponse { spec: ReportSpec; result: ReportResult; summary?: string | null; notice?: string | null }
export interface ReportChoice { id: string; label: string; help?: string | null }
export interface ReportMeta { metrics: ReportChoice[]; groupBys: ReportChoice[]; periods: ReportChoice[]; charts: ReportChoice[]; devices: ReportChoice[]; paymentMethods: string[]; aiAvailable: boolean; examples: string[] }
export interface SavedReport { id: string; name: string; question?: string | null; spec: ReportSpec; createdAt: string; createdBy?: string | null }

export interface AiSettings {
  hasKey: boolean; keyHint?: string | null; model: string; chatEnabled: boolean; reportsEnabled: boolean; summariesEnabled: boolean; dailyChatLimit: number; dailyReportLimitPerCompany: number;
  usage: { chats: number; reports: number; failures: number }; updatedAt?: string | null;
}

export interface AssistantFields { companyName?: string | null; tin?: string | null; contactName?: string | null; contactSurname?: string | null; email?: string | null; phone?: string | null; planId?: number | null; billingCycle?: string | null; message?: string | null }
export interface AssistantPlan { id: number; name: string; price: string; summary: string }
export interface AssistantReply { reply: string; fields: AssistantFields; missing: string[]; stage: 'collecting' | 'confirm'; plans: AssistantPlan[] }
export interface AssistantStatus { enabled: boolean; greeting: string }

export interface Profile {
  id: string; email: string; userName: string; name: string; surname: string; phone?: string | null; companyName?: string | null; roles: string[]; lastLoginDate?: string | null; createdAt: string;
  hasAvatar: boolean; accessScope: string; devices: { id: string; zimraDeviceId: number; name?: string | null; branch?: string | null }[];
}
export interface SessionInfo { id: number; createdAt: string; expiresAt: string; ipAddress?: string | null; browser?: string | null; current: boolean }
