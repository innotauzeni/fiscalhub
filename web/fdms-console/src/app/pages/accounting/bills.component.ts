import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { doneOrHeld } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { DocumentsPanelComponent } from '../../shared/documents-panel.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';
import { Supplier } from './suppliers.component';

interface Payment { id: string; date: string; amount: number; accountName: string; reference?: string | null; voided: boolean }
interface Bill { id: string; supplierId: string; supplierName: string; number: string; date: string; dueDate: string; currency: string; memo?: string | null; subtotal: number; vat: number; total: number; paid: number; outstanding: number; status: string; daysOverdue: number; expenseAccountName: string; journalNumber?: string | null; payments: Payment[] }
interface BillForm { supplierId: string; number: string; date: string; dueDate: string; currency: string; memo: string; subtotal: number | null; vat: number | null; expense: string; costCentreId: string | null }
interface PayForm { bill: Bill; date: string; amount: number | null; account: string; reference: string }

/** Supplier invoices the company owes. Entering one posts it to the books (expense, VAT input, accounts payable); paying one posts the payment. */
@Component({
  selector: 'app-bills',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DocumentsPanelComponent, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Bills" subtitle="Supplier invoices you owe. Entering one posts it to your books; paying one posts the payment.">
      @if (auth.can('accounting.manage')) { <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New bill</button> }
    </app-page-header>

    <div class="mb-5 grid gap-4 sm:grid-cols-3">
      @for (t of totals(); track t.currency) {
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Owing in {{ t.currency }}</div>
          <div class="text-2xl font-semibold tabular-nums">{{ money(t.owing) }}</div><div class="text-xs" [class]="t.overdue ? 'text-red-600' : 'text-slate-500'">{{ t.overdue ? money(t.overdue) + ' overdue' : 'Nothing overdue' }}</div></div>
      }
    </div>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label class="block"><span class="label">Show</span><select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); load()"><option value="">All bills</option><option value="open">Still owing</option><option value="overdue">Overdue</option><option value="paid">Paid</option><option value="void">Void</option></select></label>
        <label class="block"><span class="label">Supplier</span><select class="input" [ngModel]="supplier()" (ngModelChange)="supplier.set($event); load()"><option value="">All suppliers</option>@for (s of suppliers(); track s.id) { <option [value]="s.id">{{ s.name }}</option> }</select></label>
        <label class="block sm:col-span-2"><span class="label">Search</span><input class="input" placeholder="Invoice number or note" [ngModel]="q()" (ngModelChange)="q.set($event)" (keyup.enter)="load()" /></label>
      </div>
    </div>

    <div class="space-y-3">
      @for (b of bills(); track b.id) {
        <article class="card p-0" [class.opacity-60]="b.status === 'Void'">
          <button type="button" class="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left" (click)="toggle(b.id)" [attr.aria-expanded]="open() === b.id">
            <div class="min-w-0 flex-1"><div class="truncate text-sm font-semibold">{{ b.supplierName }} <span class="font-mono text-xs font-normal text-slate-500">· {{ b.number }}</span></div>
              <div class="text-xs text-slate-500">Billed {{ b.date | date: 'd MMM y' }} · due {{ b.dueDate | date: 'd MMM y' }}@if (b.daysOverdue) { <span class="font-medium text-red-600"> · {{ b.daysOverdue }} days overdue</span> }</div></div>
            <span class="badge" [class.badge-ok]="b.status === 'Paid'" [class.badge-warn]="b.status === 'Part paid' || b.status === 'Void'" [class.badge-bad]="b.status === 'Unpaid' && b.daysOverdue > 0" [class.badge-info]="b.status === 'Unpaid' && !b.daysOverdue">{{ b.status }}</span>
            <div class="w-36 text-right"><div class="text-sm font-semibold tabular-nums">{{ money(b.total) }} {{ b.currency }}</div>@if (b.status !== 'Void' && b.outstanding > 0 && b.paid > 0) { <div class="text-xs text-amber-700 dark:text-amber-300">{{ money(b.outstanding) }} left</div> }</div>
            <app-icon name="chevron" size="h-4 w-4" class="text-slate-400 transition" [class.rotate-180]="open() === b.id" />
          </button>
          @if (open() === b.id) {
            <div class="space-y-3 border-t border-slate-100 px-5 py-3 text-sm dark:border-slate-800">
              <div class="grid gap-2 sm:grid-cols-4">
                <div><div class="text-xs text-slate-500">Before VAT</div><div class="tabular-nums">{{ money(b.subtotal) }}</div></div><div><div class="text-xs text-slate-500">VAT</div><div class="tabular-nums">{{ money(b.vat) }}</div></div>
                <div><div class="text-xs text-slate-500">Paid</div><div class="tabular-nums">{{ money(b.paid) }}</div></div><div><div class="text-xs text-slate-500">For</div><div>{{ b.expenseAccountName }}</div></div>
              </div>
              @if (b.memo) { <div class="text-slate-600 dark:text-slate-300">{{ b.memo }}</div> }
              @if (b.payments.length) {
                <ul class="divide-y divide-slate-100 rounded-xl border border-slate-200 dark:divide-slate-800 dark:border-slate-700">
                  @for (p of b.payments; track p.id) {
                    <li class="flex items-center gap-3 px-3 py-2" [class.opacity-50]="p.voided"><span class="w-24 text-xs text-slate-500">{{ p.date | date: 'd MMM y' }}</span><span class="flex-1">{{ p.accountName }}{{ p.reference ? ' · ' + p.reference : '' }}@if (p.voided) { <span class="badge badge-warn ml-2">Voided</span> }</span>
                      <span class="tabular-nums">{{ money(p.amount) }}</span>@if (!p.voided && auth.can('accounting.manage')) { <button class="btn-ghost btn-sm" (click)="voidPayment(p)">Void</button> }</li>
                  }
                </ul>
              }
              <div class="flex flex-wrap items-center justify-between gap-2 text-xs text-slate-500"><span>Posted as {{ b.journalNumber || '—' }} · <button type="button" class="font-medium text-brand-600 hover:underline" (click)="docs.set({ id: b.id, label: b.supplierName + ' ' + b.number })">Documents</button></span>
                @if (auth.can('accounting.manage') && b.status !== 'Void') { <span class="flex gap-2">
                  @if (b.outstanding > 0) { <button class="btn btn-sm" (click)="startPay(b)"><app-icon name="card" size="h-4 w-4" />Pay</button> }
                  @if (!b.paid) { <button class="btn-ghost btn-sm" (click)="voidBill(b)">Void bill</button> }</span> }</div>
            </div>
          }
        </article>
      } @empty { <div class="card text-center text-slate-500">{{ loaded() ? 'No bills match.' : 'Loading…' }}</div> }
    </div>

    @if (form(); as f) {
      <app-modal title="New bill" subtitle="A supplier's invoice you have received" width="max-w-2xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Supplier</span><select class="input" name="s" [(ngModel)]="f.supplierId" required><option value="">Choose…</option>@for (s of suppliers(); track s.id) { @if (s.isActive) { <option [value]="s.id">{{ s.name }}</option> } }</select></label>
            <label class="block"><span class="label">Their invoice number</span><input class="input" name="n" [(ngModel)]="f.number" required maxlength="50" /></label>
            <label class="block"><span class="label">Bill date</span><input type="date" class="input" name="d" [(ngModel)]="f.date" required /></label>
            <label class="block"><span class="label">Due date (optional)</span><input type="date" class="input" name="dd" [(ngModel)]="f.dueDate" /><span class="hint">Empty = the supplier's payment terms.</span></label>
            <label class="block sm:col-span-2"><span class="label">What it is for</span><select class="input" name="x" [(ngModel)]="f.expense" required><option value="">Choose an account…</option>@for (a of purchaseAccounts(); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            @if (store.activeCentres().length) {
              <label class="block sm:col-span-2"><span class="label">Cost centre (optional)</span><select class="input" name="cc" [(ngModel)]="f.costCentreId"><option [ngValue]="null">—</option>@for (c of store.activeCentres(); track c.id) { <option [ngValue]="c.id">{{ c.code }} · {{ c.name }}</option> }</select></label>
            }
            <label class="block"><span class="label">Currency</span><select class="input" name="c" [(ngModel)]="f.currency">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
            <div class="grid grid-cols-2 gap-2">
              <label class="block"><span class="label">Amount before VAT</span><input type="number" step="0.01" min="0.01" class="input text-right" name="a" [(ngModel)]="f.subtotal" required /></label>
              <label class="block"><span class="label">VAT <button type="button" class="text-xs text-brand-600 underline" (click)="vat15(f)">15%</button></span><input type="number" step="0.01" min="0" class="input text-right" name="v" [(ngModel)]="f.vat" /></label>
            </div>
            <label class="block sm:col-span-2"><span class="label">Note (optional)</span><input class="input" name="m" [(ngModel)]="f.memo" maxlength="200" /></label>
          </div>
          <p class="mt-3 text-sm text-slate-600 dark:text-slate-300">Total owed: <strong class="tabular-nums">{{ money((f.subtotal || 0) + (f.vat || 0)) }} {{ f.currency }}</strong></p>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !f.supplierId || !f.expense || !f.subtotal">Post bill</button></div>
        </form>
      </app-modal>
    }

    @if (pay(); as p) {
      <app-modal title="Pay this bill" [subtitle]="p.bill.supplierName + ' · ' + p.bill.number + ' · ' + money(p.bill.outstanding) + ' ' + p.bill.currency + ' owing'" width="max-w-md" (closed)="pay.set(null)">
        <form (ngSubmit)="doPay()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="p.date" required /></label>
            <label class="block"><span class="label">Amount</span><input type="number" step="0.01" min="0.01" [max]="p.bill.outstanding" class="input text-right" name="a" [(ngModel)]="p.amount" required /></label>
            <label class="block sm:col-span-2"><span class="label">Paid from</span><select class="input" name="f" [(ngModel)]="p.account" required><option value="">Choose…</option>@for (a of store.meta()?.cashAccounts ?? []; track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Reference (optional)</span><input class="input" name="r" [(ngModel)]="p.reference" maxlength="50" placeholder="Cheque or transfer number" /></label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="pay.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !p.amount || !p.account">Record payment</button></div>
        </form>
      </app-modal>
    }
    @if (docs(); as d) {
      <app-modal [title]="'Documents: ' + d.label" subtitle="Scans and papers kept with this record." width="max-w-2xl" (closed)="docs.set(null)">
        <app-documents-panel linkType="bill" [linkId]="d.id" />
      </app-modal>
    }
  `,
})
export class BillsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly route = inject(ActivatedRoute);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly bills = signal<Bill[]>([]);
  readonly suppliers = signal<Supplier[]>([]);
  readonly loaded = signal(false);
  readonly open = signal('');
  readonly status = signal('');
  readonly supplier = signal('');
  readonly q = signal('');
  readonly form = signal<BillForm | null>(null);
  readonly pay = signal<PayForm | null>(null);
  readonly docs = signal<{ id: string; label: string } | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly purchaseAccounts = computed(() => this.store.postable().filter(a => a.isActive && (a.type === 'Expense' || (a.type === 'Asset' && a.subType !== 'cash' && a.role !== 'receivable' && a.role !== 'vat_input'))));
  readonly totals = computed(() => {
    const by = new Map<string, { currency: string; owing: number; overdue: number }>();
    for (const b of this.bills()) if (b.status !== 'Void' && b.outstanding > 0) { const t = by.get(b.currency) ?? { currency: b.currency, owing: 0, overdue: 0 }; t.owing += b.outstanding; if (b.daysOverdue) t.overdue += b.outstanding; by.set(b.currency, t); }
    return [...by.values()];
  });

  async ngOnInit(): Promise<void> {
    void this.store.loadCentres().catch(() => undefined);
    this.supplier.set(this.route.snapshot.queryParamMap.get('supplier') ?? '');
    try { await this.store.load(); this.suppliers.set(await this.api.get<Supplier[]>('/api/v1/accounting/suppliers')); } catch (e) { this.toast.err(e); }
    await this.load();
  }

  async load(): Promise<void> {
    try { this.bills.set(await this.api.get<Bill[]>('/api/v1/accounting/bills', { status: this.status(), supplierId: this.supplier(), q: this.q() })); this.loaded.set(true); } catch (e) { this.toast.err(e); }
  }

  toggle(id: string): void { this.open.set(this.open() === id ? '' : id); }
  vat15(f: BillForm): void { f.vat = Math.round((f.subtotal ?? 0) * 15) / 100; this.form.update(x => (x ? { ...x } : x)); }

  start(): void { this.error.set(''); this.form.set({ supplierId: this.supplier(), number: '', date: isoDate(new Date()), dueDate: '', currency: this.store.meta()?.defaultCurrency ?? 'USD', memo: '', subtotal: null, vat: null, expense: '', costCentreId: null }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      const res = await this.api.post('/api/v1/accounting/bills', { supplierId: f.supplierId, number: f.number.trim(), date: f.date, dueDate: f.dueDate || null, currency: f.currency, memo: f.memo.trim() || null, subtotal: Number(f.subtotal), vat: Number(f.vat) || 0, expenseAccountId: f.expense, costCentreId: f.costCentreId });
      this.form.set(null); this.toast.ok(doneOrHeld(res, 'Bill posted to the books.')); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  startPay(b: Bill): void {
    this.error.set('');
    const cash = this.store.meta()?.cashAccounts ?? [];
    this.pay.set({ bill: b, date: isoDate(new Date()), amount: b.outstanding, account: (cash.find(a => a.role === 'cash') ?? cash[0])?.id ?? '', reference: '' });
  }

  async doPay(): Promise<void> {
    const p = this.pay(); if (!p) return;
    this.busy.set(true); this.error.set('');
    try { const res = await this.api.post('/api/v1/accounting/bills/' + p.bill.id + '/pay', { date: p.date, amount: Number(p.amount), accountId: p.account, reference: p.reference.trim() || null }); this.pay.set(null); this.toast.ok(doneOrHeld(res, 'Payment recorded.')); await this.load(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async voidBill(b: Bill): Promise<void> {
    if (!(await this.dialog.confirm('Void bill ' + b.number + '? It is reversed in the books.', 'Void', true))) return;
    try { await this.api.post('/api/v1/accounting/bills/' + b.id + '/void'); this.toast.ok('Bill voided.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async voidPayment(p: Payment): Promise<void> {
    if (!(await this.dialog.confirm('Void this payment of ' + money(p.amount) + '? It is reversed in the books.', 'Void', true))) return;
    try { await this.api.post('/api/v1/accounting/bills/payments/' + p.id + '/void'); this.toast.ok('Payment voided.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
