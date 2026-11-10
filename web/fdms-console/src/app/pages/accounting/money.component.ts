import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

export type MoneyKind = 'payment' | 'receipt' | 'transfer';
export interface MoneyLine { accountId: string; accountCode: string; accountName: string; description?: string | null; amount: number; vatRate: number; vat: number; costCentreId?: string | null; costCentre?: string | null }
export interface MoneyDoc {
  id: string; kind: MoneyKind; number: string; date: string; currency: string; accountId: string; accountName: string; toAccountId?: string | null; toAccountName?: string | null;
  contact?: string | null; reference?: string | null; memo?: string | null; lines: MoneyLine[]; subtotal: number; vat: number; total: number; fee: number; status: 'Draft' | 'Posted' | 'Void';
  journalEntryId?: string | null; journalNumber?: string | null; createdBy?: string | null; createdAt: string; postedBy?: string | null; postedAt?: string | null;
}
export interface MoneyPage { items: MoneyDoc[]; totals: { currency: string; amount: number }[]; count: number }
interface FormLine { accountId: string; description: string; amount: number | null; vatRate: number; costCentreId: string | null }
interface Form { id?: string; date: string; currency: string; accountId: string; toAccountId: string; contact: string; reference: string; memo: string; lines: FormLine[]; amount: number | null; fee: number | null }

const TEXT: Record<MoneyKind, { title: string; sub: string; add: string; total: string; unit: string; contact: string; account: string; lines: string; empty: string }> = {
  payment: { title: 'Spend money', sub: 'Record payments from your cash or bank accounts: expenses, purchases, drawings, loan repayments.', add: 'Record payment', total: 'Total spent', unit: 'payment', contact: 'Paid to', account: 'Paid from', lines: 'What it was for', empty: 'No payments in this period.' },
  receipt: { title: 'Receive money', sub: 'Record money received into your cash or bank accounts that is not a fiscal sale: refunds, interest, capital, loans.', add: 'Record receipt', total: 'Total received', unit: 'receipt', contact: 'Received from', account: 'Paid into', lines: 'What it was for', empty: 'No receipts in this period.' },
  transfer: { title: 'Bank transfers', sub: 'Move money between your cash, bank and wallet accounts (for example banking the cash takings).', add: 'New transfer', total: 'Total moved', unit: 'transfer', contact: '', account: 'From', lines: '', empty: 'No transfers in this period.' },
};
export const RANGES = [{ key: 'all', label: 'All dates' }, { key: 'today', label: 'Today' }, { key: 'yesterday', label: 'Yesterday' }, { key: 'week', label: 'This week' }, { key: 'month', label: 'This month' }];

/** The from/to dates of a quick range ('' = no limit). */
export function rangeDates(key: string): [string, string] {
  const t = new Date(); const d = (x: Date) => isoDate(x);
  switch (key) {
    case 'today': return [d(t), d(t)];
    case 'yesterday': { const y = new Date(t); y.setDate(t.getDate() - 1); return [d(y), d(y)]; }
    case 'week': { const m = new Date(t); m.setDate(t.getDate() - ((t.getDay() + 6) % 7)); return [d(m), d(t)]; }
    case 'month': return [d(new Date(t.getFullYear(), t.getMonth(), 1)), d(t)];
    default: return ['', ''];
  }
}

/** Spend money, receive money or bank transfers (the kind comes from the route): kept as drafts or posted straight away, voided rather than edited once posted. */
@Component({
  selector: 'app-money',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, RouterLink, IconComponent, ModalComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header [title]="t().title" [subtitle]="t().sub">
      <app-export-menu [build]="exportDoc" [disabled]="!page()?.count" />
      @if (canManage) { <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />{{ t().add }}</button> }
    </app-page-header>

    <div class="card mb-4 space-y-3">
      <div class="flex flex-wrap items-center gap-2">
        <div class="inline-flex rounded-xl bg-slate-100 p-1 dark:bg-slate-800">
          @for (b of dateBys; track b.key) { <button type="button" class="rounded-lg px-3 py-1.5 text-sm" [class]="dateBy() === b.key ? 'bg-brand-600 text-white' : 'text-slate-600 dark:text-slate-300'" (click)="dateBy.set(b.key); load()">{{ b.label }}</button> }
        </div>
        <div class="inline-flex flex-wrap rounded-xl bg-slate-100 p-1 dark:bg-slate-800">
          @for (r of ranges; track r.key) { <button type="button" class="rounded-lg px-3 py-1.5 text-sm" [class]="range() === r.key ? 'bg-brand-600 text-white' : 'text-slate-600 dark:text-slate-300'" (click)="pick(r.key)">{{ r.label }}</button> }
        </div>
      </div>
      <div class="flex flex-wrap items-end gap-3">
        <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event); range.set('custom')" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event); range.set('custom')" /></label>
        <label class="block"><span class="label">Status</span><select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); load()"><option value="">All statuses</option><option value="Draft">Draft</option><option value="Posted">Posted</option><option value="Void">Void</option></select></label>
        <label class="block"><span class="label">Account</span><select class="input" [ngModel]="account()" (ngModelChange)="account.set($event); load()"><option value="">All accounts</option>@for (a of cashAccounts(); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
        <label class="block min-w-48 flex-1"><span class="label">Search</span><input class="input" [ngModel]="q()" (ngModelChange)="q.set($event)" (keyup.enter)="load()" placeholder="Number, reference, name or note" /></label>
        <button class="btn" (click)="load()" [disabled]="busy()">Apply</button>
      </div>
    </div>

    <div class="card mb-4 flex flex-wrap items-center gap-4 py-3">
      <span class="text-xs font-semibold uppercase tracking-wide text-slate-500">{{ t().total }} ({{ dateBy() === 'created' ? 'created' : 'dated' }} · {{ rangeLabel() }})</span>
      @for (x of page()?.totals ?? []; track x.currency) { <span class="text-xl font-semibold tabular-nums"><span class="text-xs text-slate-500">{{ x.currency }}</span> {{ money(x.amount) }}</span> }
      @if (!(page()?.totals ?? []).length) { <span class="text-xl font-semibold tabular-nums">0.00</span> }
      <span class="flex-1"></span><span class="text-sm text-slate-500">{{ page()?.count ?? 0 }} {{ t().unit }}{{ page()?.count === 1 ? '' : 's' }}</span>
    </div>

    <div class="card p-0">
      <div class="overflow-x-auto">
        <table class="table w-full text-sm">
          <thead><tr><th>Number</th><th>Date</th>@if (kind() !== 'transfer') { <th>{{ t().contact }}</th> }<th>{{ kind() === 'transfer' ? 'From → to' : t().account }}</th><th>Reference / note</th><th class="text-right">Amount</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (d of page()?.items ?? []; track d.id) {
              <tr [class.opacity-60]="d.status === 'Void'">
                <td><button type="button" class="font-mono text-xs text-brand-600 hover:underline" (click)="view.set(d)">{{ d.number }}</button></td>
                <td class="whitespace-nowrap">{{ d.date | date: 'd MMM y' }}</td>
                @if (kind() !== 'transfer') { <td class="max-w-40 truncate">{{ d.contact }}</td> }
                <td class="max-w-56 truncate text-xs">{{ d.accountName }}@if (d.toAccountName) { → {{ d.toAccountName }} }</td>
                <td class="max-w-56 truncate text-xs text-slate-500">{{ d.reference }}{{ d.reference && d.memo ? ' · ' : '' }}{{ d.memo }}</td>
                <td class="whitespace-nowrap text-right font-medium tabular-nums">{{ money(d.total) }} <span class="text-xs text-slate-500">{{ d.currency }}</span></td>
                <td><span class="badge" [class.badge-warn]="d.status === 'Draft'" [class.badge-ok]="d.status === 'Posted'">{{ d.status }}</span>@if (d.journalNumber) { <div class="font-mono text-[10px] text-slate-400">{{ d.journalNumber }}</div> }</td>
                <td class="whitespace-nowrap text-right">@if (canManage) {
                  @if (d.status === 'Draft') {
                    <button class="btn-ghost btn-sm" (click)="post(d)" [disabled]="busy()">Post</button>
                    <button class="icon-btn" (click)="edit(d)" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                    <button class="icon-btn icon-btn-danger" (click)="remove(d)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                  } @else if (d.status === 'Posted') { <button class="btn-ghost btn-sm text-red-600" (click)="voidDoc(d)" [disabled]="busy()">Void</button> }
                }</td>
              </tr>
            } @empty { <tr><td colspan="8" class="py-10 text-center text-slate-500">{{ loaded() ? t().empty : 'Loading…' }}</td></tr> }
          </tbody>
        </table>
      </div>
    </div>
    @if (drafts()) { <p class="hint mt-3">{{ drafts() }} draft(s) are waiting. Post them here or all at once under <a routerLink="/accounting/post-to-ledger" class="text-brand-600 hover:underline">Post to ledger</a>.</p> }

    <!-- one document -->
    @if (view(); as d) {
      <app-modal [title]="d.number" [subtitle]="d.status + (d.journalNumber ? ' · journal ' + d.journalNumber : '')" width="max-w-2xl" (closed)="view.set(null)">
        <dl class="mb-4 grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
          <dt class="text-slate-500">Date</dt><dd>{{ d.date | date: 'd MMM y' }}</dd>
          @if (d.contact) { <dt class="text-slate-500">{{ t().contact }}</dt><dd>{{ d.contact }}</dd> }
          <dt class="text-slate-500">{{ kind() === 'transfer' ? 'From' : t().account }}</dt><dd>{{ d.accountName }}</dd>
          @if (d.toAccountName) { <dt class="text-slate-500">To</dt><dd>{{ d.toAccountName }}</dd> }
          @if (d.reference) { <dt class="text-slate-500">Reference</dt><dd>{{ d.reference }}</dd> }
          @if (d.memo) { <dt class="text-slate-500">Note</dt><dd>{{ d.memo }}</dd> }
          <dt class="text-slate-500">Entered by</dt><dd>{{ d.createdBy || '—' }} · {{ d.createdAt | date: 'd MMM y, HH:mm' }}</dd>
          @if (d.postedAt) { <dt class="text-slate-500">Posted by</dt><dd>{{ d.postedBy || '—' }} · {{ d.postedAt | date: 'd MMM y, HH:mm' }}</dd> }
        </dl>
        @if (d.lines.length) {
          <table class="table w-full text-sm"><thead><tr><th>Account</th><th>Description</th><th>Cost centre</th><th class="text-right">VAT</th><th class="text-right">Amount</th></tr></thead>
            <tbody>@for (l of d.lines; track $index) { <tr><td>{{ l.accountCode }} · {{ l.accountName }}</td><td class="text-slate-500">{{ l.description }}</td><td class="text-slate-500">{{ l.costCentre }}</td>
              <td class="text-right tabular-nums">{{ l.vat ? money(l.vat) + ' (' + l.vatRate + '%)' : '' }}</td><td class="text-right tabular-nums">{{ money(l.amount) }}</td></tr> }</tbody>
            <tfoot><tr class="font-semibold"><td colspan="3">Total (VAT {{ money(d.vat) }})</td><td></td><td class="text-right tabular-nums">{{ money(d.total) }} {{ d.currency }}</td></tr></tfoot></table>
        } @else { <p class="text-sm">Amount <strong>{{ money(d.total) }} {{ d.currency }}</strong>@if (d.fee) { · bank charge {{ money(d.fee) }} }</p> }
      </app-modal>
    }

    <!-- entering one -->
    @if (form(); as f) {
      <app-modal [title]="(f.id ? 'Edit ' : '') + t().add" width="max-w-4xl" (closed)="form.set(null)">
        <form (ngSubmit)="save(true)">
          <div class="grid gap-4 sm:grid-cols-4">
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="f.date" required /></label>
            <label class="block"><span class="label">Currency</span><select class="input" name="c" [(ngModel)]="f.currency">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">{{ t().account }}</span><select class="input" name="acc" [(ngModel)]="f.accountId" required><option value="">Choose…</option>@for (a of cashAccounts(); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            @if (kind() === 'transfer') {
              <label class="block sm:col-span-2"><span class="label">To</span><select class="input" name="to" [(ngModel)]="f.toAccountId" required><option value="">Choose…</option>@for (a of cashAccounts(); track a.id) { @if (a.id !== f.accountId) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> } }</select></label>
              <label class="block"><span class="label">Amount</span><input type="number" step="0.01" min="0.01" class="input text-right" name="amt" [(ngModel)]="f.amount" required /></label>
              <label class="block"><span class="label">Bank charge (optional)</span><input type="number" step="0.01" min="0" class="input text-right" name="fee" [(ngModel)]="f.fee" /></label>
            } @else {
              <label class="block sm:col-span-2"><span class="label">{{ t().contact }}</span><input class="input" name="ct" [(ngModel)]="f.contact" maxlength="120" [placeholder]="kind() === 'payment' ? 'e.g. ZESA, landlord, supplier' : 'e.g. customer, bank, owner'" /></label>
            }
            <label class="block"><span class="label">Reference</span><input class="input" name="ref" [(ngModel)]="f.reference" maxlength="50" placeholder="Cheque or receipt no." /></label>
            <label class="block sm:col-span-3"><span class="label">Note</span><input class="input" name="memo" [(ngModel)]="f.memo" maxlength="200" /></label>
          </div>
          @if (kind() !== 'transfer') {
            <div class="mt-4 overflow-x-auto"><table class="w-full text-sm">
              <thead><tr class="text-left text-xs uppercase tracking-wide text-slate-500"><th class="pb-1">{{ t().lines }}</th><th class="pb-1">Description</th>@if (store.activeCentres().length) { <th class="pb-1">Cost centre</th> }<th class="pb-1">VAT</th><th class="pb-1 text-right">Amount (VAT incl.)</th><th></th></tr></thead>
              <tbody>
                @for (l of f.lines; track $index; let i = $index) {
                  <tr>
                    <td class="py-1 pr-2"><select class="input" [name]="'a' + i" [(ngModel)]="l.accountId"><option value="">Choose…</option>@for (a of lineAccounts(); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></td>
                    <td class="py-1 pr-2"><input class="input" [name]="'n' + i" [(ngModel)]="l.description" maxlength="200" /></td>
                    @if (store.activeCentres().length) { <td class="py-1 pr-2"><select class="input" [name]="'cc' + i" [(ngModel)]="l.costCentreId"><option [ngValue]="null">—</option>@for (c of store.activeCentres(); track c.id) { <option [ngValue]="c.id">{{ c.code }}</option> }</select></td> }
                    <td class="w-28 py-1 pr-2"><select class="input" [name]="'v' + i" [(ngModel)]="l.vatRate">@for (v of vatRates; track v) { <option [ngValue]="v">{{ v ? v + '%' : 'None' }}</option> }</select></td>
                    <td class="w-36 py-1 pr-2"><input type="number" step="0.01" min="0" class="input text-right" [name]="'amt' + i" [(ngModel)]="l.amount" /></td>
                    <td>@if (f.lines.length > 1) { <button type="button" class="icon-btn" (click)="f.lines.splice(i, 1)" aria-label="Remove line"><app-icon name="x" size="h-4 w-4" /></button> }</td>
                  </tr>
                }
              </tbody>
              <tfoot><tr class="border-t border-slate-200 font-semibold dark:border-slate-700"><td class="py-2"><button type="button" class="btn-ghost btn-sm" (click)="f.lines.push(blank())"><app-icon name="plus" size="h-4 w-4" />Add line</button></td>
                <td class="py-2 text-right text-xs font-normal text-slate-500" [attr.colspan]="store.activeCentres().length ? 3 : 2">VAT {{ money(vatOf(f)) }}</td><td class="py-2 pr-2 text-right tabular-nums">{{ money(totalOf(f)) }}</td><td></td></tr></tfoot>
            </table></div>
          }
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex flex-wrap justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
            <button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button>
            <button class="btn-ghost" type="button" (click)="save(false)" [disabled]="busy()">Save as draft</button>
            <button class="btn" type="submit" [disabled]="busy()">Save and post</button>
          </div>
        </form>
      </app-modal>
    }
  `,
})
export class MoneyComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly canManage = inject(AuthService).can('accounting.manage');
  readonly money = money;
  readonly kind = toSignal(inject(ActivatedRoute).data.pipe(map(d => (d['kind'] ?? 'payment') as MoneyKind)), { initialValue: 'payment' as MoneyKind });
  readonly t = computed(() => TEXT[this.kind()]);
  readonly ranges = RANGES;
  readonly dateBys = [{ key: 'transaction', label: 'Transaction date' }, { key: 'created', label: 'Created date' }];
  readonly vatRates = [0, 15, 15.5, 14.5];
  readonly dateBy = signal('transaction');
  readonly range = signal('month');
  readonly from = signal(rangeDates('month')[0]);
  readonly to = signal(rangeDates('month')[1]);
  readonly status = signal('');
  readonly account = signal('');
  readonly q = signal('');
  readonly page = signal<MoneyPage | null>(null);
  readonly loaded = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly form = signal<Form | null>(null);
  readonly view = signal<MoneyDoc | null>(null);
  readonly cashAccounts = computed(() => this.store.postable().filter(a => a.isActive && a.type === 'Asset' && a.subType === 'cash'));
  readonly lineAccounts = computed(() => this.store.postable().filter(a => a.isActive && a.subType !== 'cash'));
  readonly drafts = computed(() => (this.page()?.items ?? []).filter(d => d.status === 'Draft').length);
  readonly rangeLabel = computed(() => this.range() === 'custom' ? `${this.from()} to ${this.to()}` : RANGES.find(r => r.key === this.range())?.label.toLowerCase() ?? '');

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); void this.store.loadCentres().catch(() => undefined); await this.load(); } catch (e) { this.toast.err(e); }
  }

  pick(key: string): void { const [f, t] = rangeDates(key); this.range.set(key); this.from.set(f); this.to.set(t); void this.load(); }

  async load(): Promise<void> {
    this.busy.set(true);
    try {
      this.page.set(await this.api.get<MoneyPage>('/api/v1/accounting/money', { kind: this.kind(), status: this.status(), from: this.from(), to: this.to(), dateBy: this.dateBy(), q: this.q(), accountId: this.account() }));
      this.loaded.set(true);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  blank(): FormLine { return { accountId: '', description: '', amount: null, vatRate: 0, costCentreId: null }; }
  vatOf(f: Form): number { return Math.round(f.lines.reduce((n, l) => n + (l.vatRate ? (Number(l.amount) || 0) * l.vatRate / (100 + l.vatRate) : 0), 0) * 100) / 100; }
  totalOf(f: Form): number { return Math.round(f.lines.reduce((n, l) => n + (Number(l.amount) || 0), 0) * 100) / 100; }

  start(): void {
    this.error.set('');
    const cash = this.cashAccounts();
    this.form.set({ date: isoDate(new Date()), currency: this.store.meta()?.defaultCurrency ?? 'USD', accountId: this.account() || (this.kind() === 'transfer' ? cash.find(a => a.role === 'cash')?.id : cash.find(a => a.role === 'bank')?.id) || cash[0]?.id || '',
      toAccountId: '', contact: '', reference: '', memo: '', lines: [this.blank()], amount: null, fee: null });
  }

  edit(d: MoneyDoc): void {
    this.error.set('');
    this.form.set({ id: d.id, date: d.date.slice(0, 10), currency: d.currency, accountId: d.accountId, toAccountId: d.toAccountId ?? '', contact: d.contact ?? '', reference: d.reference ?? '', memo: d.memo ?? '',
      lines: d.lines.length ? d.lines.map(l => ({ accountId: l.accountId, description: l.description ?? '', amount: l.amount, vatRate: l.vatRate, costCentreId: l.costCentreId ?? null })) : [this.blank()],
      amount: d.kind === 'transfer' ? d.total : null, fee: d.fee || null });
  }

  async save(post: boolean): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      const doc = await this.api.put<MoneyDoc>('/api/v1/accounting/money', {
        id: f.id ?? null, kind: this.kind(), date: f.date, currency: f.currency, accountId: f.accountId, toAccountId: this.kind() === 'transfer' ? f.toAccountId || null : null,
        contact: f.contact.trim() || null, reference: f.reference.trim() || null, memo: f.memo.trim() || null,
        lines: this.kind() === 'transfer' ? [] : f.lines.filter(l => l.amount).map(l => ({ accountId: l.accountId, description: l.description.trim() || null, amount: Number(l.amount), vatRate: l.vatRate, costCentreId: l.costCentreId })),
        amount: Number(f.amount) || 0, fee: Number(f.fee) || 0, post,
      });
      this.form.set(null); this.toast.ok(post ? `${doc.number} posted (${doc.journalNumber}).` : `${doc.number} saved as a draft.`); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  private async act(run: () => Promise<unknown>, done: string): Promise<void> {
    this.busy.set(true);
    try { await run(); this.toast.ok(done); await this.load(); } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
  post(d: MoneyDoc): Promise<void> { return this.act(() => this.api.post(`/api/v1/accounting/money/${d.id}/post`), `${d.number} posted.`); }
  async voidDoc(d: MoneyDoc): Promise<void> {
    if (!(await this.dialog.confirm(`Void ${d.number}? Its journal is reversed today; both stay in the books.`, 'Void', true))) return;
    await this.act(() => this.api.post(`/api/v1/accounting/money/${d.id}/void`), `${d.number} voided.`);
  }
  async remove(d: MoneyDoc): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the draft ${d.number}?`, 'Delete', true))) return;
    await this.act(() => this.api.delete(`/api/v1/accounting/money/${d.id}`), 'Draft deleted.');
  }

  readonly exportDoc = (): ExportDoc | null => {
    const p = this.page(); if (!p) return null;
    const transfer = this.kind() === 'transfer';
    return {
      title: this.t().title, subtitle: `${this.dateBy() === 'created' ? 'Created' : 'Dated'} ${this.rangeLabel()}${this.status() ? ' · ' + this.status() : ''}`, landscape: true,
      columns: [{ header: 'Number' }, { header: 'Date' }, { header: transfer ? 'From' : this.t().contact }, { header: transfer ? 'To' : this.t().account }, { header: 'Reference / note' }, { header: 'Status' }, { header: 'Currency' }, { header: 'VAT', kind: 'money' }, { header: 'Amount', kind: 'money' }],
      rows: [...p.items.map(d => ({ cells: [d.number, d.date.slice(0, 10), transfer ? d.accountName : d.contact ?? '', transfer ? d.toAccountName ?? '' : d.accountName, [d.reference, d.memo].filter(Boolean).join(' · '), d.status, d.currency, num(d.vat, true), num(d.total)],
          style: d.status === 'Void' ? 'muted' as const : 'normal' as const })),
        ...p.totals.map(x => ({ cells: ['Total ' + x.currency, '', '', '', '', '', x.currency, '', num(x.amount)], style: 'total' as const }))],
    };
  };
}
