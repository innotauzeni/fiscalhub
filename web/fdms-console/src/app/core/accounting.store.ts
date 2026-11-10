import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from './api.service';

export interface LedgerAccount {
  id: string; code: string; name: string; type: string; role?: string | null; isActive: boolean; isSystem: boolean; bankAccountId?: string | null; inUse: boolean;
  parentId?: string | null; isGroup?: boolean; subType?: string; description?: string | null; level?: number; hasChildren?: boolean;
}
export interface SubType { key: string; label: string; type: string }
export interface ChartSettings { codeFormat: string; classStarts: Record<string, string>; codeStep: number; enforceCodeFormat: boolean; fiscalYearStartMonth: number }
export interface ChartSetup { settings: ChartSettings; subTypes: SubType[]; example: string }
export interface AccountingMeta { currencies: string[]; defaultCurrency: string; cashAccounts: LedgerAccount[]; firstEntry: string }
export interface JournalLine { id: string; accountId: string; accountCode: string; accountName: string; debit: number; credit: number; description?: string | null; costCentreId?: string | null; costCentre?: string | null }
export interface CostCentre { id: string; code: string; name: string; kind: string; branchId?: string | null; branchName?: string | null; isActive: boolean; inUse: boolean }
export interface Journal { id: string; number: string; date: string; currency: string; memo: string; reference?: string | null; source: string; reversed: boolean; isReversal: boolean; createdBy?: string | null; total: number; lines: JournalLine[] }

export const money = (v: number | null | undefined): string => (v == null ? '' : v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }));
export const isoDate = (d: Date): string => d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
export const monthStart = (): string => { const d = new Date(); return isoDate(new Date(d.getFullYear(), d.getMonth(), 1)); };

/** The chart of accounts and the books' meta (currencies in use, cash and bank accounts), loaded once and shared by the accounting pages. */
@Injectable({ providedIn: 'root' })
export class AccountingStore {
  private readonly api = inject(Api);
  readonly accounts = signal<LedgerAccount[]>([]);
  readonly meta = signal<AccountingMeta | null>(null);
  readonly setup = signal<ChartSetup | null>(null);
  readonly centres = signal<CostCentre[]>([]);
  readonly activeCentres = computed(() => this.centres().filter(c => c.isActive));
  async loadCentres(): Promise<CostCentre[]> { const c = await this.api.get<CostCentre[]>('/api/v1/accounting/cost-centres'); this.centres.set(c); return c; }
  /** The accounts entries can be posted to (headings only group others). */
  readonly postable = computed(() => this.accounts().filter(a => !a.isGroup));

  async load(): Promise<void> {
    const [accounts, meta] = await Promise.all([this.api.get<LedgerAccount[]>('/api/v1/accounting/accounts'), this.api.get<AccountingMeta>('/api/v1/accounting/meta')]);
    this.accounts.set(accounts); this.meta.set(meta);
    if (!this.setup()) void this.loadSetup().catch(() => undefined);
  }

  async loadSetup(): Promise<ChartSetup> { const s = await this.api.get<ChartSetup>('/api/v1/accounting/chart/settings'); this.setup.set(s); return s; }

  /** The first day of the financial year a date (yyyy-mm-dd) falls in. */
  yearStart(date: string): string {
    const m = this.setup()?.settings.fiscalYearStartMonth ?? 1; const d = new Date(date + 'T00:00:00');
    const y = d.getMonth() + 1 >= m ? d.getFullYear() : d.getFullYear() - 1;
    return y + '-' + String(m).padStart(2, '0') + '-01';
  }
  subTypeLabel(key?: string | null): string { return this.setup()?.subTypes.find(s => s.key === key)?.label ?? ''; }

  label(a: LedgerAccount): string { return a.code + ' · ' + a.name; }
  byType(type: string): LedgerAccount[] { return this.postable().filter(a => a.type === type); }
}
