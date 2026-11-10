import { Injectable, inject, signal } from '@angular/core';
import { Api } from './api.service';

export interface Currency { code: string; name: string; symbol: string; isActive: boolean; sortOrder: number; accountsUsing: number }
export interface BankAccount { id: string; bankId: string; bankName: string; accountName: string; accountNumber: string; branchName?: string | null; branchCode?: string | null; currency: string; isDefault: boolean; isActive: boolean }
export interface Bank { id: string; name: string; code?: string | null; isActive: boolean; accounts: BankAccount[] }

/** The currency list and the company's banks, loaded once and shared by every pick list. */
@Injectable({ providedIn: 'root' })
export class BankingStore {
  private readonly api = inject(Api);
  readonly currencies = signal<Currency[]>([]);
  readonly banks = signal<Bank[]>([]);
  private currenciesLoaded = false;

  async loadCurrencies(force = false): Promise<void> {
    if (this.currenciesLoaded && !force) return;
    try { this.currencies.set(await this.api.get<Currency[]>('/api/v1/currencies')); this.currenciesLoaded = true; } catch { /* the pick list falls back to the current value */ }
  }

  async loadBanks(): Promise<void> {
    try { this.banks.set(await this.api.get<Bank[]>('/api/v1/banks')); } catch { this.banks.set([]); }
  }

  /** The text printed under "Payment details" on an invoice for this account. */
  describe(a: BankAccount): string {
    return [`Bank: ${a.bankName}`, `Account name: ${a.accountName}`, `Account no: ${a.accountNumber} (${a.currency})`, a.branchName || a.branchCode ? `Branch: ${[a.branchName, a.branchCode].filter(Boolean).join(' ')}` : '']
      .filter(Boolean).join('\n');
  }
}
