import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { Bank, BankAccount, BankingStore } from '../../core/banking.store';
import { DialogService, ToastService } from '../../core/ui.services';
import { CurrencySelectComponent } from '../../shared/currency-select.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface BankForm { id?: string; name: string; code: string; isActive: boolean }
interface AccountForm { id?: string; bankId: string; accountName: string; accountNumber: string; branchName: string; branchCode: string; currency: string; isDefault: boolean; isActive: boolean }

/** The company's banks, each with its accounts. Wherever payment details are needed, picking a bank offers exactly these accounts. */
@Component({
  selector: 'app-banks',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent, CurrencySelectComponent],
  template: `
    <app-page-header title="Banks and accounts" subtitle="Add the banks you use and their accounts. When you choose a bank, only its accounts are offered.">
      <button class="btn" (click)="newBank()"><app-icon name="plus" size="h-4 w-4" />Add bank</button>
    </app-page-header>

    <div class="mb-5 grid gap-4 sm:grid-cols-3">
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Banks</div><div class="text-2xl font-semibold">{{ store.banks().length }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Accounts</div><div class="text-2xl font-semibold">{{ accountCount() }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Currencies covered</div><div class="text-2xl font-semibold">{{ currencyCount() }}</div></div>
    </div>

    <div class="grid gap-5 lg:grid-cols-2">
      @for (b of store.banks(); track b.id) {
        <article class="card flex flex-col p-0">
          <header class="flex items-center gap-3 border-b border-slate-100 p-5 dark:border-slate-800">
            <span class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-500/15"><app-icon name="card" size="h-6 w-6" /></span>
            <div class="min-w-0 flex-1"><h2 class="truncate text-base font-semibold">{{ b.name }}</h2><p class="text-xs text-slate-500">{{ b.code || 'No code' }} · {{ b.accounts.length }} account{{ b.accounts.length === 1 ? '' : 's' }}</p></div>
            @if (!b.isActive) { <span class="badge badge-warn">Inactive</span> }
            <button class="icon-btn" (click)="editBank(b)" aria-label="Edit bank"><app-icon name="edit" size="h-4 w-4" /></button>
            <button class="icon-btn icon-btn-danger" (click)="removeBank(b)" aria-label="Delete bank"><app-icon name="trash" size="h-4 w-4" /></button>
          </header>
          <ul class="flex-1 divide-y divide-slate-100 dark:divide-slate-800">
            @for (a of b.accounts; track a.id) {
              <li class="flex items-center gap-3 px-5 py-3" [class.opacity-60]="!a.isActive">
                <span class="badge badge-info shrink-0">{{ a.currency }}</span>
                <div class="min-w-0 flex-1"><div class="truncate text-sm font-medium tabular-nums">{{ a.accountNumber }}@if (a.isDefault) { <span class="badge badge-ok ml-2">Default</span> }</div>
                  <div class="truncate text-xs text-slate-500">{{ a.accountName }}{{ a.branchName ? ' · ' + a.branchName : '' }}</div></div>
                <button class="icon-btn" (click)="editAccount(a)" aria-label="Edit account"><app-icon name="edit" size="h-4 w-4" /></button>
                <button class="icon-btn icon-btn-danger" (click)="removeAccount(a)" aria-label="Delete account"><app-icon name="trash" size="h-4 w-4" /></button>
              </li>
            } @empty { <li class="px-5 py-6 text-center text-sm text-slate-500">No accounts yet.</li> }
          </ul>
          <footer class="border-t border-slate-100 p-3 dark:border-slate-800"><button class="btn-ghost btn-sm w-full" (click)="newAccount(b)"><app-icon name="plus" size="h-4 w-4" />Add account to {{ b.name }}</button></footer>
        </article>
      } @empty {
        <div class="card text-center lg:col-span-2">
          <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800"><app-icon name="card" size="h-6 w-6" /></div>
          <h2 class="mt-3 text-base font-semibold">No banks yet</h2><p class="text-sm text-slate-500">Add a bank such as NMB or CBZ, then its accounts.</p>
          <button class="btn mt-4" (click)="newBank()">Add your first bank</button>
        </div>
      }
    </div>

    @if (bank(); as f) {
      <app-modal [title]="f.id ? 'Edit bank' : 'Add bank'" width="max-w-md" (closed)="bank.set(null)">
        <form (ngSubmit)="saveBank()">
          <div class="space-y-4">
            <label class="block"><span class="label">Bank name</span><input class="input" name="bn" [(ngModel)]="f.name" required maxlength="100" placeholder="NMB" /></label>
            <label class="block"><span class="label">Code or SWIFT (optional)</span><input class="input font-mono uppercase" name="bc" [(ngModel)]="f.code" maxlength="20" placeholder="NMBLZWHX" /></label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="ba" [(ngModel)]="f.isActive" /> Active</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="bank.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }

    @if (account(); as f) {
      <app-modal [title]="f.id ? 'Edit account' : 'Add account'" [subtitle]="'At ' + bankName(f.bankId)" width="max-w-lg" (closed)="account.set(null)">
        <form (ngSubmit)="saveAccount()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Account name</span><input class="input" name="an" [(ngModel)]="f.accountName" required maxlength="100" placeholder="Your company (Pvt) Ltd" /></label>
            <label class="block"><span class="label">Account number</span><input class="input font-mono" name="anum" [(ngModel)]="f.accountNumber" required maxlength="34" /></label>
            <label class="block"><span class="label">Currency</span><app-currency-select [(value)]="f.currency" label="Account currency" /></label>
            <label class="block"><span class="label">Branch (optional)</span><input class="input" name="abr" [(ngModel)]="f.branchName" maxlength="100" placeholder="Harare Main" /></label>
            <label class="block"><span class="label">Branch code (optional)</span><input class="input font-mono" name="abc" [(ngModel)]="f.branchCode" maxlength="20" /></label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="adef" [(ngModel)]="f.isDefault" /> Default for this currency</label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="aact" [(ngModel)]="f.isActive" /> Active</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="account.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class BanksComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(BankingStore);
  readonly bank = signal<BankForm | null>(null);
  readonly account = signal<AccountForm | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');

  readonly accountCount = computed(() => this.store.banks().reduce((n, b) => n + b.accounts.length, 0));
  readonly currencyCount = computed(() => new Set(this.store.banks().flatMap(b => b.accounts.map(a => a.currency))).size);
  bankName(id: string): string { return this.store.banks().find(b => b.id === id)?.name ?? ''; }

  async ngOnInit(): Promise<void> { await Promise.all([this.store.loadBanks(), this.store.loadCurrencies()]); }

  newBank(): void { this.error.set(''); this.bank.set({ name: '', code: '', isActive: true }); }
  editBank(b: Bank): void { this.error.set(''); this.bank.set({ id: b.id, name: b.name, code: b.code ?? '', isActive: b.isActive }); }
  newAccount(b: Bank): void {
    this.error.set('');
    this.account.set({ bankId: b.id, accountName: '', accountNumber: '', branchName: '', branchCode: '', currency: this.store.currencies()[0]?.code ?? 'USD', isDefault: b.accounts.length === 0, isActive: true });
  }
  editAccount(a: BankAccount): void {
    this.error.set('');
    this.account.set({ id: a.id, bankId: a.bankId, accountName: a.accountName, accountNumber: a.accountNumber, branchName: a.branchName ?? '', branchCode: a.branchCode ?? '', currency: a.currency, isDefault: a.isDefault, isActive: a.isActive });
  }

  private clean(e: unknown): string { return errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, ''); }

  async saveBank(): Promise<void> {
    const f = this.bank(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.put('/api/v1/banks', { id: f.id ?? null, name: f.name.trim(), code: f.code.trim() || null, isActive: f.isActive }); this.bank.set(null); this.toast.ok('Bank saved.'); await this.store.loadBanks(); }
    catch (e) { this.error.set(this.clean(e)); } finally { this.busy.set(false); }
  }

  async saveAccount(): Promise<void> {
    const f = this.account(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.put('/api/v1/banks/accounts', { id: f.id ?? null, bankId: f.bankId, accountName: f.accountName.trim(), accountNumber: f.accountNumber.trim(), branchName: f.branchName.trim() || null, branchCode: f.branchCode.trim() || null, currency: f.currency, isDefault: f.isDefault, isActive: f.isActive });
      this.account.set(null); this.toast.ok('Account saved.'); await this.store.loadBanks();
    } catch (e) { this.error.set(this.clean(e)); } finally { this.busy.set(false); }
  }

  async removeBank(b: Bank): Promise<void> {
    if (!(await this.dialog.confirm(`Delete ${b.name}${b.accounts.length ? ' and its ' + b.accounts.length + ' account(s)' : ''}?`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/banks/${b.id}`); this.toast.ok('Bank deleted.'); await this.store.loadBanks(); } catch (e) { this.toast.err(e); }
  }

  async removeAccount(a: BankAccount): Promise<void> {
    if (!(await this.dialog.confirm(`Delete account ${a.accountNumber}?`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/banks/accounts/${a.id}`); this.toast.ok('Account deleted.'); await this.store.loadBanks(); } catch (e) { this.toast.err(e); }
  }
}
