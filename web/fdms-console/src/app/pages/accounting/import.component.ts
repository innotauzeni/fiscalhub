import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { DialogService, ToastService } from '../../core/ui.services';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface BalanceRow { code: string; name: string | null; type: string | null; debit: number | null; credit: number | null; subType?: string | null }
interface PartyRow { name: string; number: string; date: string; dueDate: string | null; amount: number | null; email?: string | null; tin?: string | null }
interface FileResult { kind: string; balances: { code: string; name?: string | null; type?: string | null; debit: number; credit: number }[]; parties: { name: string; number: string; date: string; dueDate?: string | null; amount: number; email?: string | null; tin?: string | null }[]; problems: string[] }
interface CheckLine { line: number; code: string; name: string; type: string; isNew: boolean; debit: number; credit: number; note?: string | null; subType?: string | null }
interface Check {
  lines: CheckLine[]; newAccounts: number; totalDebit: number; totalCredit: number; difference: number; receivablesInBalances: number; customerInvoices: number; payablesInBalances: number; supplierBills: number;
  newCustomers: number; newSuppliers: number; problems: string[]; warnings: string[]; canPost: boolean;
}
interface Done { currency: string; date: string; number: string; entryId: string; customerInvoices: number; supplierBills: number }
type Kind = 'balances' | 'customers' | 'suppliers';

/**
 * Starting the books here: the trial balance at the conversion date, the customers' unpaid invoices and the suppliers' unpaid bills, from Excel or CSV (or typed in),
 * checked together and posted in one go.
 */
@Component({
  selector: 'app-import-balances',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, RouterLink, IconComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Import balances" subtitle="Moving from another system? Bring in your trial balance, what customers owe you and what you owe suppliers, as at one date." />

    @if (done().length) {
      <div class="card mb-5 text-sm">
        <div class="mb-2 font-medium">Already brought in</div>
        <ul class="space-y-1">@for (d of done(); track d.entryId) {
          <li><span class="badge badge-ok mr-2">{{ d.currency }}</span>as at {{ d.date | date: 'd MMM y' }} · journal {{ d.number }} · {{ d.customerInvoices }} customer invoice(s) · {{ d.supplierBills }} supplier bill(s)</li>
        }</ul>
        <p class="mt-2 text-xs text-slate-500">To bring a currency in again, reverse its opening journal under <a routerLink="/accounting/journals" class="text-brand-600 hover:underline">Journals</a> first.</p>
      </div>
    }

    <ol class="space-y-5">
      <li class="card">
        <h2 class="card-title mb-3"><span class="mr-2 inline-flex h-6 w-6 items-center justify-center rounded-full bg-brand-600 text-xs text-white">1</span>Conversion date and currency</h2>
        <div class="grid gap-4 sm:grid-cols-3">
          <label class="block"><span class="label">Balances as at the end of</span><input type="date" class="input" [ngModel]="date()" (ngModelChange)="date.set($event); stale()" /><span class="hint">Usually the last day before you start working here, e.g. the end of a month.</span></label>
          <label class="block"><span class="label">Currency</span><select class="input" [ngModel]="currency()" (ngModelChange)="currency.set($event); stale()">@for (c of currencies(); track c) { <option [value]="c">{{ c }}</option> }</select><span class="hint">One currency at a time. Do each currency you keep books in.</span></label>
        </div>
      </li>

      @for (k of kinds; track k.key) {
        <li class="card">
          <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
            <h2 class="card-title"><span class="mr-2 inline-flex h-6 w-6 items-center justify-center rounded-full bg-brand-600 text-xs text-white">{{ k.step }}</span>{{ k.title }}</h2>
            <span class="flex flex-wrap gap-2">
              <button class="btn-ghost btn-sm" (click)="template(k.key)"><app-icon name="download" size="h-4 w-4" />Excel template</button>
              <label class="btn-ghost btn-sm cursor-pointer"><app-icon name="upload" size="h-4 w-4" />{{ busy() === k.key ? 'Reading…' : 'Upload Excel or CSV' }}<input type="file" class="hidden" accept=".xlsx,.csv,.txt" (change)="upload(k.key, $event)" /></label>
              @if (k.key === 'balances') { <button class="btn-ghost btn-sm" (click)="fromChart()"><app-icon name="stack" size="h-4 w-4" />Type them in</button> }
              @if (count(k.key)) { <button class="btn-ghost btn-sm" (click)="clear(k.key)"><app-icon name="trash" size="h-4 w-4" />Clear</button> }
            </span>
          </div>
          <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">{{ k.help }}</p>
          @for (p of fileProblems()[k.key] ?? []; track $index) { <p class="mb-1 text-sm text-red-700 dark:text-red-300">{{ p }}</p> }

          @if (k.key === 'balances' && balances().length) {
            <div class="max-h-[28rem] overflow-auto rounded-xl border border-slate-200 dark:border-slate-700">
              <table class="table w-full text-sm">
                <thead class="sticky top-0 bg-white dark:bg-slate-900"><tr><th class="w-32">Code</th><th>Account name (for new accounts)</th><th class="w-32">Type</th><th class="w-44">Shows under</th><th class="w-36 text-right">Debit</th><th class="w-36 text-right">Credit</th><th class="w-8"></th></tr></thead>
                <tbody>
                  @for (r of balances(); track $index; let i = $index) {
                    <tr>
                      <td><input class="input py-1 font-mono" [(ngModel)]="r.code" (ngModelChange)="stale()" /></td>
                      <td><input class="input py-1" [(ngModel)]="r.name" (ngModelChange)="stale()" [placeholder]="accountName(r.code)" /></td>
                      <td><select class="input py-1" [(ngModel)]="r.type" (ngModelChange)="stale()"><option [ngValue]="null">{{ accountType(r.code) || '—' }}</option>@for (t of types; track t) { <option [ngValue]="t">{{ t }}</option> }</select></td>
                      <td>@if (isNew(r.code)) { <select class="input py-1" [(ngModel)]="r.subType" (ngModelChange)="stale()"><option [ngValue]="null">Like its neighbours</option>@for (st of subTypes(r.type); track st.key) { <option [ngValue]="st.key">{{ st.label }}</option> }</select> }
                        @else { <span class="text-xs text-slate-500">{{ store.subTypeLabel(accountSub(r.code)) }}</span> }</td>
                      <td><input type="number" step="0.01" min="0" class="input py-1 text-right" [(ngModel)]="r.debit" (ngModelChange)="stale()" /></td>
                      <td><input type="number" step="0.01" min="0" class="input py-1 text-right" [(ngModel)]="r.credit" (ngModelChange)="stale()" /></td>
                      <td><button class="btn-ghost btn-sm" (click)="removeRow('balances', i)" aria-label="Remove"><app-icon name="x" size="h-4 w-4" /></button></td>
                    </tr>
                  }
                </tbody>
                <tfoot><tr class="font-semibold"><td colspan="4">Totals</td><td class="text-right tabular-nums">{{ money(sum('debit')) }}</td><td class="text-right tabular-nums">{{ money(sum('credit')) }}</td><td></td></tr></tfoot>
              </table>
            </div>
            <button class="btn-ghost btn-sm mt-2" (click)="addRow('balances')"><app-icon name="plus" size="h-4 w-4" />Add an account</button>
          }
          @if (k.key !== 'balances' && parties(k.key).length) {
            <div class="max-h-[24rem] overflow-auto rounded-xl border border-slate-200 dark:border-slate-700">
              <table class="table w-full text-sm">
                <thead class="sticky top-0 bg-white dark:bg-slate-900"><tr><th>{{ k.key === 'customers' ? 'Customer' : 'Supplier' }}</th><th class="w-36">{{ k.key === 'customers' ? 'Invoice' : 'Bill' }} number</th><th class="w-40">Date</th><th class="w-40">Due</th><th class="w-36 text-right">Amount owing</th><th class="w-8"></th></tr></thead>
                <tbody>
                  @for (r of parties(k.key); track $index; let i = $index) {
                    <tr>
                      <td><input class="input py-1" [(ngModel)]="r.name" (ngModelChange)="stale()" /></td>
                      <td><input class="input py-1" [(ngModel)]="r.number" (ngModelChange)="stale()" /></td>
                      <td><input type="date" class="input py-1" [(ngModel)]="r.date" (ngModelChange)="stale()" /></td>
                      <td><input type="date" class="input py-1" [(ngModel)]="r.dueDate" (ngModelChange)="stale()" /></td>
                      <td><input type="number" step="0.01" min="0" class="input py-1 text-right" [(ngModel)]="r.amount" (ngModelChange)="stale()" /></td>
                      <td><button class="btn-ghost btn-sm" (click)="removeRow(k.key, i)" aria-label="Remove"><app-icon name="x" size="h-4 w-4" /></button></td>
                    </tr>
                  }
                </tbody>
                <tfoot><tr class="font-semibold"><td colspan="4">Total</td><td class="text-right tabular-nums">{{ money(partyTotal(k.key)) }}</td><td></td></tr></tfoot>
              </table>
            </div>
          }
          @if (k.key !== 'balances') { <button class="btn-ghost btn-sm mt-2" (click)="addRow(k.key)"><app-icon name="plus" size="h-4 w-4" />Add {{ k.key === 'customers' ? 'an invoice' : 'a bill' }}</button> }
        </li>
      }

      <li class="card">
        <h2 class="card-title mb-3"><span class="mr-2 inline-flex h-6 w-6 items-center justify-center rounded-full bg-brand-600 text-xs text-white">5</span>Check and post</h2>
        <div class="flex flex-wrap items-center gap-3">
          <button class="btn" (click)="check()" [disabled]="busy() !== ''"><app-icon name="check" size="h-4 w-4" />{{ busy() === 'check' ? 'Checking…' : 'Check everything' }}</button>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" [ngModel]="allowDiff()" (ngModelChange)="allowDiff.set($event); stale()" />Post a difference to opening balance equity (if the trial balance does not balance)</label>
        </div>
        @if (result(); as r) {
          <div class="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <div class="rounded-xl border p-3" [class]="r.difference === 0 ? 'border-emerald-300' : 'border-red-300'"><div class="text-xs text-slate-500">Trial balance</div><div class="tabular-nums">{{ money(r.totalDebit) }} Dr · {{ money(r.totalCredit) }} Cr</div><div class="text-xs" [class]="r.difference === 0 ? 'text-emerald-700' : 'text-red-600'">{{ r.difference === 0 ? 'Balances' : 'Out by ' + money(r.difference) }}</div></div>
            <div class="rounded-xl border p-3" [class]="!r.customerInvoices || r.customerInvoices === r.receivablesInBalances ? 'border-emerald-300' : 'border-red-300'"><div class="text-xs text-slate-500">Receivables</div><div class="tabular-nums">{{ money(r.receivablesInBalances) }} in the TB</div><div class="text-xs text-slate-500">{{ money(r.customerInvoices) }} in customer invoices · {{ r.newCustomers }} new customer(s)</div></div>
            <div class="rounded-xl border p-3" [class]="!r.supplierBills || r.supplierBills === r.payablesInBalances ? 'border-emerald-300' : 'border-red-300'"><div class="text-xs text-slate-500">Payables</div><div class="tabular-nums">{{ money(r.payablesInBalances) }} in the TB</div><div class="text-xs text-slate-500">{{ money(r.supplierBills) }} in supplier bills · {{ r.newSuppliers }} new supplier(s)</div></div>
            <div class="rounded-xl border border-slate-200 p-3 dark:border-slate-700"><div class="text-xs text-slate-500">Chart of accounts</div><div>{{ r.newAccounts }} new account(s)</div><div class="text-xs text-slate-500">{{ r.lines.length }} balance(s) in all</div></div>
          </div>
          @if (r.problems.length) { <ul class="mt-4 space-y-1 rounded-lg bg-red-50 px-4 py-3 text-sm text-red-800 dark:bg-red-900/30 dark:text-red-200" role="alert">@for (p of r.problems; track $index) { <li>• {{ p }}</li> }</ul> }
          @if (r.warnings.length) { <ul class="mt-3 space-y-1 rounded-lg bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-100">@for (w of r.warnings; track $index) { <li>• {{ w }}</li> }</ul> }
          @if (r.lines.length) {
            <details class="mt-3"><summary class="cursor-pointer text-sm text-brand-600">What will be posted</summary>
              <div class="mt-2 max-h-80 overflow-auto"><table class="table w-full text-sm"><thead><tr><th>Code</th><th>Account</th><th>Type</th><th>Shows under</th><th class="text-right">Debit</th><th class="text-right">Credit</th><th></th></tr></thead>
                <tbody>@for (l of r.lines; track l.line) { <tr><td class="font-mono">{{ l.code }}</td><td>{{ l.name }}</td><td>{{ l.type }}</td><td class="text-xs">{{ store.subTypeLabel(l.subType) }}</td><td class="text-right tabular-nums">{{ l.debit ? money(l.debit) : '' }}</td><td class="text-right tabular-nums">{{ l.credit ? money(l.credit) : '' }}</td><td class="text-xs" [class.text-brand-600]="l.isNew">{{ l.note }}</td></tr> }</tbody></table></div>
            </details>
          }
          <div class="mt-4 flex justify-end"><button class="btn" (click)="post()" [disabled]="!r.canPost || busy() !== '' || dirty()"><app-icon name="upload" size="h-4 w-4" />{{ busy() === 'post' ? 'Posting…' : 'Post to the books' }}</button></div>
          @if (dirty()) { <p class="mt-2 text-right text-xs text-slate-500">You changed something since the check: check again before posting.</p> }
        }
      </li>
    </ol>
  `,
})
export class ImportBalancesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly money = money;
  readonly types = ['Asset', 'Liability', 'Equity', 'Income', 'Expense'];
  readonly kinds: { key: Kind; step: number; title: string; help: string }[] = [
    { key: 'balances', step: 2, title: 'Trial balance', help: 'Every account with its balance at the conversion date, as a debit or a credit. Accounts not in your chart yet are created: give their name and type. Income and expenses are this financial year\'s figures so far.' },
    { key: 'customers', step: 3, title: 'Customers who owe you', help: 'Each unpaid invoice with what is still owing on it. Their total must equal accounts receivable in the trial balance, so each can be paid and aged here. Optional: without it, receivables come in as one figure.' },
    { key: 'suppliers', step: 4, title: 'Suppliers you owe', help: 'Each unpaid supplier bill with what is still owing on it. Their total must equal accounts payable in the trial balance. Optional, like the customers.' },
  ];
  readonly date = signal(this.lastMonthEnd());
  readonly currency = signal('USD');
  readonly balances = signal<BalanceRow[]>([]);
  readonly customers = signal<PartyRow[]>([]);
  readonly suppliers = signal<PartyRow[]>([]);
  readonly fileProblems = signal<Partial<Record<Kind, string[]>>>({});
  readonly allowDiff = signal(false);
  readonly result = signal<Check | null>(null);
  readonly dirty = signal(false);
  readonly busy = signal<string>('');
  readonly done = signal<Done[]>([]);
  readonly currencies = computed(() => { const c = this.store.meta()?.currencies ?? ['USD']; return c.includes('USD') ? c : ['USD', ...c]; });
  private readonly byCode = computed(() => new Map(this.store.accounts().map(a => [a.code.toLowerCase(), a])));

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); await this.store.loadSetup().catch(() => undefined); this.currency.set(this.store.meta()?.defaultCurrency ?? 'USD'); await this.loadDone(); } catch (e) { this.toast.err(e); }
  }

  private lastMonthEnd(): string { const d = new Date(); return isoDate(new Date(d.getFullYear(), d.getMonth(), 0)); }
  private async loadDone(): Promise<void> { this.done.set(await this.api.get<Done[]>('/api/v1/accounting/conversion')); }

  stale(): void { if (this.result()) this.dirty.set(true); }
  accountName(code: string): string { return this.byCode().get((code ?? '').trim().toLowerCase())?.name ?? 'New account: give it a name'; }
  accountType(code: string): string { return this.byCode().get((code ?? '').trim().toLowerCase())?.type ?? ''; }
  accountSub(code: string): string | null { return this.byCode().get((code ?? '').trim().toLowerCase())?.subType ?? null; }
  isNew(code: string): boolean { return !!(code ?? '').trim() && !this.byCode().has(code.trim().toLowerCase()); }
  subTypes(type: string | null) { return (this.store.setup()?.subTypes ?? []).filter(s => !type || s.type === type).filter(s => s.key !== 'cash'); }
  parties(k: Kind): PartyRow[] { return k === 'customers' ? this.customers() : this.suppliers(); }
  count(k: Kind): number { return k === 'balances' ? this.balances().length : this.parties(k).length; }
  sum(side: 'debit' | 'credit'): number { return this.balances().reduce((s, r) => s + (Number(r[side]) || 0), 0); }
  partyTotal(k: Kind): number { return this.parties(k).reduce((s, r) => s + (Number(r.amount) || 0), 0); }

  private setRows(k: Kind, rows: BalanceRow[] | PartyRow[]): void {
    if (k === 'balances') this.balances.set(rows as BalanceRow[]); else if (k === 'customers') this.customers.set(rows as PartyRow[]); else this.suppliers.set(rows as PartyRow[]);
    this.stale();
  }
  addRow(k: Kind): void {
    if (k === 'balances') this.setRows(k, [...this.balances(), { code: '', name: null, type: null, debit: null, credit: null, subType: null }]);
    else this.setRows(k, [...this.parties(k), { name: '', number: '', date: this.date(), dueDate: null, amount: null }]);
  }
  removeRow(k: Kind, i: number): void {
    if (k === 'balances') this.setRows(k, this.balances().filter((_, x) => x !== i)); else this.setRows(k, this.parties(k).filter((_, x) => x !== i));
  }
  async clear(k: Kind): Promise<void> { if (await this.dialog.confirm('Clear this list?', 'Clear', true)) this.setRows(k, []); }

  /** Every account of the chart, to type the balances in by hand. */
  fromChart(): void {
    const have = new Set(this.balances().map(r => r.code.toLowerCase()));
    const rows = this.store.postable().filter(a => a.isActive && !have.has(a.code.toLowerCase())).map(a => ({ code: a.code, name: null, type: null, debit: null, credit: null, subType: null }));
    this.setRows('balances', [...this.balances(), ...rows]);
  }

  async template(k: Kind): Promise<void> { try { await this.api.download('/api/v1/accounting/conversion/template/' + k, `fiscalhub-${k}-template.xlsx`); } catch (e) { this.toast.err(e); } }

  async upload(k: Kind, ev: Event): Promise<void> {
    const input = ev.target as HTMLInputElement; const file = input.files?.[0]; input.value = '';
    if (!file) return;
    this.busy.set(k);
    try {
      const fd = new FormData(); fd.append('file', file, file.name);
      const r = await this.api.post<FileResult>('/api/v1/accounting/conversion/read/' + k, fd);
      this.fileProblems.update(p => ({ ...p, [k]: r.problems }));
      if (k === 'balances') this.setRows(k, r.balances.map(b => ({ code: b.code, name: b.name ?? null, type: this.types.find(t => t.toLowerCase() === (b.type ?? '').toLowerCase()) ?? null, debit: b.debit || null, credit: b.credit || null, subType: null })));
      else this.setRows(k, r.parties.map(p => ({ name: p.name, number: p.number, date: p.date.slice(0, 10), dueDate: p.dueDate ? p.dueDate.slice(0, 10) : null, amount: p.amount, email: p.email, tin: p.tin })));
      this.toast.ok(`${k === 'balances' ? r.balances.length + ' balance(s)' : r.parties.length + ' row(s)'} read from ${file.name}.`);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(''); }
  }

  private body(): unknown {
    return {
      date: this.date(), currency: this.currency(), postDifferenceToEquity: this.allowDiff(),
      balances: this.balances().filter(r => r.code.trim() && (Number(r.debit) || Number(r.credit))).map(r => ({ code: r.code.trim(), name: r.name?.trim() || null, type: r.type, debit: Number(r.debit) || 0, credit: Number(r.credit) || 0, subType: this.isNew(r.code) ? r.subType ?? null : null })),
      customers: this.customers().filter(r => r.name.trim()).map(r => ({ ...r, amount: Number(r.amount) || 0, dueDate: r.dueDate || null })),
      suppliers: this.suppliers().filter(r => r.name.trim()).map(r => ({ ...r, amount: Number(r.amount) || 0, dueDate: r.dueDate || null })),
    };
  }

  async check(): Promise<void> {
    this.busy.set('check');
    try { this.result.set(await this.api.post<Check>('/api/v1/accounting/conversion/check', this.body())); this.dirty.set(false); } catch (e) { this.toast.err(e); } finally { this.busy.set(''); }
  }

  async post(): Promise<void> {
    if (!(await this.dialog.confirm(`Post these opening balances in ${this.currency()} as at ${this.date()}? They go into your books in one go.`, 'Post'))) return;
    this.busy.set('post');
    try {
      const r = await this.api.post<{ journalNumber?: string | null; accountsCreated: number; customerInvoices: number; supplierBills: number }>('/api/v1/accounting/conversion/post', this.body());
      this.toast.ok(`Posted${r.journalNumber ? ' as ' + r.journalNumber : ''}: ${r.accountsCreated} new account(s), ${r.customerInvoices} customer invoice(s), ${r.supplierBills} supplier bill(s).`);
      this.balances.set([]); this.customers.set([]); this.suppliers.set([]); this.result.set(null);
      await Promise.all([this.loadDone(), this.store.load()]);
    } catch (e) { this.toast.err(errorMessage(e)); } finally { this.busy.set(''); }
  }
}
