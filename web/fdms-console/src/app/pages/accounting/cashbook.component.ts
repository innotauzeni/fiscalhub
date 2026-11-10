import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, monthStart, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface CashLine { journalLineId: string; entryId: string; date: string; number: string; description: string; reference?: string | null; receipt: number; payment: number; balance: number; reconciled: boolean; otherAccounts?: string | null; source?: string | null }
interface Cashbook { account: { id: string; code: string; name: string }; currency: string; from: string; to: string; opening: number; lines: CashLine[]; totalReceipts: number; totalPayments: number; closing: number }
interface Entry { kind: 'receipt' | 'payment'; date: string; amount: number | null; counter: string; description: string; reference: string }

/** The cashbook of one cash, bank or wallet account: money in, money out and the running balance. Record what is missing straight from here. */
@Component({
  selector: 'app-cashbook',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Bank ledger" subtitle="Every movement of a cash, bank or wallet account, the other side of each entry, and the running balance. Fiscal sales appear by themselves.">
      <app-export-menu [build]="exportDoc" [disabled]="!book()" />
      @if (auth.can('accounting.manage') && accountId()) {
        <button class="btn-ghost" (click)="start('payment')"><app-icon name="minus" size="h-4 w-4" />Record payment</button>
        <button class="btn" (click)="start('receipt')"><app-icon name="plus" size="h-4 w-4" />Record receipt</button>
      }
    </app-page-header>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <label class="block lg:col-span-2"><span class="label">Account</span><select class="input" [ngModel]="accountId()" (ngModelChange)="accountId.set($event)">@for (a of store.meta()?.cashAccounts ?? []; track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
        <label class="block"><span class="label">Currency</span><select class="input" [ngModel]="currency()" (ngModelChange)="currency.set($event)">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
        <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event)" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event)" /></label>
        <label class="block"><span class="label">Transaction type</span><select class="input" [ngModel]="type()" (ngModelChange)="type.set($event)">
          <option value="">All transaction types</option><option value="in">Money in</option><option value="out">Money out</option>
          @for (t of typeKeys; track t) { <option [value]="t">{{ types[t] }}</option> }</select></label>
      </div>
      <div class="mt-3 flex justify-end gap-2"><button class="btn" (click)="load()" [disabled]="busy() || !accountId()">Show</button></div>
    </div>

    @if (book(); as b) {
      <div class="mb-5 grid gap-4 sm:grid-cols-4">
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Opening balance</div><div class="text-xl font-semibold tabular-nums">{{ money(b.opening) }}</div></div>
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Received</div><div class="text-xl font-semibold tabular-nums text-emerald-600">{{ money(b.totalReceipts) }}</div></div>
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Paid out</div><div class="text-xl font-semibold tabular-nums text-red-600">{{ money(b.totalPayments) }}</div></div>
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Closing balance</div><div class="text-xl font-semibold tabular-nums">{{ money(b.closing) }} <span class="text-sm font-normal text-slate-500">{{ b.currency }}</span></div></div>
      </div>
      <div class="card">
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>Date</th><th>Entry</th><th>Description</th><th>Other account(s)</th><th class="text-right">Received</th><th class="text-right">Paid out</th><th class="text-right">Balance</th><th></th></tr></thead>
            <tbody>
              <tr class="text-slate-500"><td colspan="6">Opening balance</td><td class="text-right tabular-nums">{{ money(b.opening) }}</td><td></td></tr>
              @for (l of shown(); track l.journalLineId) {
                <tr><td class="whitespace-nowrap">{{ l.date | date: 'd MMM y' }}</td><td class="font-mono text-xs">{{ l.number }}</td><td>{{ l.description }}@if (l.reference) { <span class="text-xs text-slate-400"> · {{ l.reference }}</span> }@if (l.source) { <span class="ml-1 rounded bg-slate-100 px-1 text-[10px] text-slate-500 dark:bg-slate-800">{{ types[l.source] || l.source }}</span> }</td>
                  <td class="max-w-56 truncate text-xs text-slate-500" [title]="l.otherAccounts ?? ''">{{ l.otherAccounts }}</td>
                  <td class="text-right tabular-nums text-emerald-700 dark:text-emerald-400">{{ l.receipt ? money(l.receipt) : '' }}</td><td class="text-right tabular-nums text-red-700 dark:text-red-400">{{ l.payment ? money(l.payment) : '' }}</td>
                  <td class="text-right font-medium tabular-nums">{{ money(l.balance) }}</td><td class="text-center">@if (l.reconciled) { <span class="text-emerald-600" title="Matched with the bank statement"><app-icon name="check" size="h-4 w-4" /></span> }</td></tr>
              } @empty { <tr><td colspan="8" class="py-8 text-center text-slate-500">No movements in this period.</td></tr> }
            </tbody>
            <tfoot><tr class="font-semibold"><td colspan="4">Totals</td><td class="text-right tabular-nums">{{ money(b.totalReceipts) }}</td><td class="text-right tabular-nums">{{ money(b.totalPayments) }}</td><td class="text-right tabular-nums">{{ money(b.closing) }}</td><td></td></tr></tfoot>
          </table>
        </div>
      </div>
    } @else if (!store.meta()?.cashAccounts?.length) {
      <div class="card text-center text-slate-500">Loading your cash and bank accounts…</div>
    }

    @if (entry(); as e) {
      <app-modal [title]="e.kind === 'receipt' ? 'Record a receipt' : 'Record a payment'" [subtitle]="accountLabel()" width="max-w-lg" (closed)="entry.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="e.date" required /></label>
            <label class="block"><span class="label">Amount ({{ currency() }})</span><input type="number" step="0.01" min="0.01" class="input text-right" name="a" [(ngModel)]="e.amount" required /></label>
            <label class="block sm:col-span-2"><span class="label">{{ e.kind === 'receipt' ? 'Received for (income or other account)' : 'Paid for (expense or other account)' }}</span>
              <select class="input" name="c" [(ngModel)]="e.counter" required><option value="">Choose…</option>@for (a of counterAccounts(e.kind); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Description</span><input class="input" name="m" [(ngModel)]="e.description" required maxlength="200" /></label>
            <label class="block sm:col-span-2"><span class="label">Reference (optional)</span><input class="input" name="r" [(ngModel)]="e.reference" maxlength="50" placeholder="Cheque, receipt or invoice number" /></label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="entry.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !e.amount || !e.counter">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class CashbookComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly accountId = signal('');
  readonly currency = signal('USD');
  readonly from = signal(monthStart());
  readonly to = signal(isoDate(new Date()));
  readonly book = signal<Cashbook | null>(null);
  readonly types: Record<string, string> = { sales: 'Fiscal sale', payment: 'Spend money', receipt: 'Receive money', transfer: 'Transfer', cashbook: 'Cashbook', bill: 'Bill payment', manual: 'Journal', reversal: 'Reversal', bank: 'Bank', opening: 'Opening balance', asset: 'Fixed assets', vat: 'VAT' };
  readonly typeKeys = Object.keys(this.types);
  readonly type = signal('');
  /** The lines of the chosen transaction type (the running balance stays the account's own). */
  readonly shown = computed(() => {
    const b = this.book(); const t = this.type(); if (!b) return [];
    return b.lines.filter(l => !t || (t === 'in' ? l.receipt > 0 : t === 'out' ? l.payment > 0 : l.source === t));
  });
  readonly exportDoc = (): ExportDoc | null => {
    const b = this.book(); if (!b) return null;
    return {
      title: 'Bank ledger — ' + b.account.code + ' ' + b.account.name, subtitle: b.from.slice(0, 10) + ' to ' + b.to.slice(0, 10) + ' · ' + b.currency, landscape: true,
      columns: [{ header: 'Date' }, { header: 'Entry' }, { header: 'Description' }, { header: 'Other account(s)' }, { header: 'Received', kind: 'money' }, { header: 'Paid out', kind: 'money' }, { header: 'Balance', kind: 'money' }],
      rows: [{ cells: ['', '', 'Opening balance', '', '', '', num(b.opening)], style: 'muted' },
        ...this.shown().map(l => ({ cells: [l.date.slice(0, 10), l.number, l.description + (l.reference ? ' · ' + l.reference : ''), l.otherAccounts ?? '', num(l.receipt, true), num(l.payment, true), num(l.balance)] })),
        { cells: ['', '', 'Totals and closing balance', '', num(b.totalReceipts), num(b.totalPayments), num(b.closing)], style: 'total' }],
    };
  };
  readonly busy = signal(false);
  readonly error = signal('');
  readonly entry = signal<Entry | null>(null);

  accountLabel(): string { const a = this.store.meta()?.cashAccounts.find(x => x.id === this.accountId()); return a ? a.code + ' · ' + a.name : ''; }
  counterAccounts(kind: string) { return this.store.postable().filter(a => a.isActive && a.id !== this.accountId() && (kind === 'receipt' ? a.type !== 'Expense' : a.type !== 'Income')); }

  async ngOnInit(): Promise<void> {
    try {
      await this.store.load();
      const m = this.store.meta(); if (!m) return;
      this.currency.set(m.defaultCurrency);
      this.accountId.set((m.cashAccounts.find(a => a.role === 'cash') ?? m.cashAccounts[0])?.id ?? '');
      if (this.accountId()) await this.load();
    } catch (e) { this.toast.err(e); }
  }

  async load(): Promise<void> {
    this.busy.set(true);
    try { this.book.set(await this.api.get<Cashbook>('/api/v1/accounting/reports/cashbook', { accountId: this.accountId(), currency: this.currency(), from: this.from(), to: this.to() })); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  start(kind: 'receipt' | 'payment'): void { this.error.set(''); this.entry.set({ kind, date: isoDate(new Date()), amount: null, counter: '', description: '', reference: '' }); }

  async save(): Promise<void> {
    const e = this.entry(); if (!e) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.post('/api/v1/accounting/cashbook-entry', { date: e.date, accountId: this.accountId(), kind: e.kind, counterAccountId: e.counter, amount: Number(e.amount), currency: this.currency(), description: e.description.trim(), reference: e.reference.trim() || null });
      this.entry.set(null); this.toast.ok('Saved.'); await this.load();
    } catch (x) { this.error.set(errorMessage(x).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  exportCsv(): void {
    const b = this.book(); if (!b) return;
    const esc = (v: unknown): string => { const s = v == null ? '' : String(v); return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
    const rows = [['Date', 'Entry', 'Description', 'Received', 'Paid out', 'Balance'], ...b.lines.map(l => [l.date.slice(0, 10), l.number, l.description, l.receipt || '', l.payment || '', l.balance])];
    const url = URL.createObjectURL(new Blob(['﻿' + rows.map(r => r.map(esc).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a'); a.href = url; a.download = 'cashbook-' + b.account.code + '.csv'; document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }
}
