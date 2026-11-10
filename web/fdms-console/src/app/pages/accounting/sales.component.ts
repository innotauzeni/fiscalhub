import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { Customer, Product } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface SalesLine { id: string; productId?: string | null; description: string; quantity: number; unitPrice: number; discountPercent: number; amount: number; accountId: string; account: string; costCentreId?: string | null }
export interface SalesDoc {
  id: string; kind: string; number?: string | null; status: string; customerId: string; customer: string; customerTin?: string | null; customerEmail?: string | null; date: string; dueDate: string; currency: string;
  reference?: string | null; notes?: string | null; subtotal: number; discount: number; total: number; credited: number; paid: number; outstanding: number; daysOverdue: number;
  originalId?: string | null; originalNumber?: string | null; journalNumber?: string | null; createdBy?: string | null; createdAt: string; lines: SalesLine[];
}
interface Setup { allowed: boolean; reason?: string | null; defaultIncomeAccountId?: string | null; termsDays: number; footer?: string | null }
interface LineForm { productId: string | null; description: string; quantity: number | null; unitPrice: number | null; discountPercent: number | null; accountId: string; costCentreId: string | null }
interface DocForm { id: string | null; kind: string; originalId: string | null; originalNumber: string; customerId: string; date: string; dueDate: string; currency: string; reference: string; notes: string; lines: LineForm[] }
interface ReceiveForm { doc: SalesDoc; date: string; amount: number | null; account: string; reference: string }

const lineAmount = (l: LineForm): number => Math.round((l.quantity || 0) * (l.unitPrice || 0) * (1 - (l.discountPercent || 0) / 100) * 100) / 100;

/** Invoices and credit notes issued from the books, for businesses that keep their books here without fiscalising. No VAT is charged on them. */
@Component({
  selector: 'app-sales',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, RouterLink, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Sales invoices" subtitle="Invoice your customers from the books. Issuing posts the invoice; payments are received against it.">
      @if (setup()?.allowed && auth.can('accounting.manage')) {
        <button class="btn-ghost" (click)="footerForm.set(setup()?.footer ?? '')"><app-icon name="edit" size="h-4 w-4" />Invoice footer</button>
        <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New invoice</button>
      }
    </app-page-header>

    @if (setup(); as s) {
      @if (!s.allowed) {
        <div class="card mb-5 text-sm"><p class="font-medium">Invoices are not issued here for your company.</p><p class="mt-1 text-slate-600 dark:text-slate-300">{{ s.reason }}</p>
          <p class="mt-2 text-slate-600 dark:text-slate-300">Unpaid invoices from before your books started here are listed below and under <a routerLink="/accounting/receivables" class="text-brand-600 hover:underline">Customers owing</a>.</p></div>
      } @else {
        <p class="mb-5 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-100">These invoices carry no VAT and say they are not fiscal tax invoices. A business registered for VAT must fiscalise its invoices.</p>
      }
    }

    <div class="mb-5 grid gap-4 sm:grid-cols-3">
      @for (t of totals(); track t.currency) {
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Owed to you in {{ t.currency }}</div>
          <div class="text-2xl font-semibold tabular-nums">{{ money(t.owing) }}</div><div class="text-xs" [class]="t.overdue ? 'text-red-600' : 'text-slate-500'">{{ t.overdue ? money(t.overdue) + ' overdue' : 'Nothing overdue' }}</div></div>
      }
    </div>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label class="block"><span class="label">Show</span><select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); load()"><option value="">Everything</option><option value="draft">Drafts</option><option value="open">Still owing</option><option value="overdue">Overdue</option><option value="paid">Paid</option><option value="void">Void</option></select></label>
        <label class="block"><span class="label">Customer</span><select class="input" [ngModel]="customer()" (ngModelChange)="customer.set($event); load()"><option value="">All customers</option>@for (c of customers(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }</select></label>
        <label class="block sm:col-span-2"><span class="label">Search</span><input class="input" placeholder="Number, customer or reference" [ngModel]="q()" (ngModelChange)="q.set($event)" (keyup.enter)="load()" /></label>
      </div>
    </div>

    <div class="space-y-3">
      @for (d of docs(); track d.id) {
        <article class="card p-0" [class.opacity-60]="d.status === 'Void'">
          <button type="button" class="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left" (click)="open.set(open() === d.id ? '' : d.id)" [attr.aria-expanded]="open() === d.id">
            <div class="min-w-0 flex-1"><div class="truncate text-sm font-semibold">{{ d.customer }} <span class="font-mono text-xs font-normal text-slate-500">· {{ d.number || 'Draft' }}</span></div>
              <div class="text-xs text-slate-500">{{ kindLabel(d) }} · {{ d.date | date: 'd MMM y' }}@if (d.kind !== 'credit_note' && d.status === 'Issued') { · due {{ d.dueDate | date: 'd MMM y' }} }@if (d.daysOverdue) { <span class="font-medium text-red-600"> · {{ d.daysOverdue }} days overdue</span> }</div></div>
            <span class="badge" [class]="badge(d)">{{ statusLabel(d) }}</span>
            <div class="w-36 text-right"><div class="text-sm font-semibold tabular-nums">{{ d.kind === 'credit_note' ? '-' : '' }}{{ money(d.total) }} {{ d.currency }}</div>@if (d.status === 'Issued' && d.outstanding > 0 && d.outstanding < d.total) { <div class="text-xs text-amber-700 dark:text-amber-300">{{ money(d.outstanding) }} owing</div> }</div>
            <app-icon name="chevron" size="h-4 w-4" class="text-slate-400 transition" [class.rotate-180]="open() === d.id" />
          </button>
          @if (open() === d.id) {
            <div class="space-y-3 border-t border-slate-100 px-5 py-3 text-sm dark:border-slate-800">
              <div class="overflow-x-auto"><table class="table w-full text-sm">
                <thead><tr><th>Description</th><th class="text-right">Qty</th><th class="text-right">Price</th><th class="text-right">Disc.</th><th class="text-right">Amount</th><th>Account</th></tr></thead>
                <tbody>@for (l of d.lines; track l.id) { <tr><td>{{ l.description }}</td><td class="text-right tabular-nums">{{ l.quantity }}</td><td class="text-right tabular-nums">{{ money(l.unitPrice) }}</td><td class="text-right">{{ l.discountPercent ? l.discountPercent + '%' : '' }}</td><td class="text-right tabular-nums">{{ money(l.amount) }}</td><td class="text-xs text-slate-500">{{ l.account }}</td></tr> }</tbody>
              </table></div>
              <div class="grid gap-2 sm:grid-cols-4">
                @if (d.discount) { <div><div class="text-xs text-slate-500">Discount</div><div class="tabular-nums">{{ money(d.discount) }}</div></div> }
                @if (d.kind !== 'credit_note') {
                  <div><div class="text-xs text-slate-500">Credited</div><div class="tabular-nums">{{ money(d.credited) }}</div></div>
                  <div><div class="text-xs text-slate-500">Received</div><div class="tabular-nums">{{ money(d.paid) }}</div></div>
                  <div><div class="text-xs text-slate-500">Still owing</div><div class="font-semibold tabular-nums">{{ money(d.outstanding) }}</div></div>
                } @else { <div><div class="text-xs text-slate-500">Against invoice</div><div>{{ d.originalNumber }}</div></div> }
              </div>
              @if (d.notes) { <div class="text-slate-600 dark:text-slate-300">{{ d.notes }}</div> }
              <div class="flex flex-wrap items-center justify-between gap-2 text-xs text-slate-500">
                <span>{{ d.journalNumber ? 'Posted as ' + d.journalNumber : 'Not posted yet' }}{{ d.reference ? ' · ref ' + d.reference : '' }}{{ d.createdBy ? ' · by ' + d.createdBy : '' }}</span>
                <span class="flex flex-wrap gap-2">
                  <button class="btn-ghost btn-sm" (click)="pdf(d)"><app-icon name="download" size="h-4 w-4" />PDF</button>
                  @if (auth.can('accounting.manage')) {
                    @if (d.status === 'Draft') {
                      <button class="btn-ghost btn-sm" (click)="edit(d)"><app-icon name="edit" size="h-4 w-4" />Edit</button>
                      <button class="btn-ghost btn-sm" (click)="remove(d)"><app-icon name="trash" size="h-4 w-4" />Delete</button>
                      <button class="btn btn-sm" (click)="issue(d)"><app-icon name="check" size="h-4 w-4" />Issue</button>
                    }
                    @if (d.status === 'Issued') {
                      <button class="btn-ghost btn-sm" (click)="email(d)"><app-icon name="mail" size="h-4 w-4" />Email</button>
                      @if (d.kind !== 'credit_note' && d.outstanding > 0) {
                        @if (d.kind === 'invoice') { <button class="btn-ghost btn-sm" (click)="credit(d)">Credit note</button> }
                        <button class="btn btn-sm" (click)="startReceive(d)"><app-icon name="card" size="h-4 w-4" />Receive payment</button>
                      }
                      @if (!d.paid && !d.credited) { <button class="btn-ghost btn-sm" (click)="voidDoc(d)">Void</button> }
                    }
                  }
                </span>
              </div>
            </div>
          }
        </article>
      } @empty { <div class="card text-center text-slate-500">{{ loaded() ? (setup()?.allowed ? 'No invoices yet. Click New invoice to raise the first.' : 'Nothing here.') : 'Loading…' }}</div> }
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.kind === 'credit_note' ? 'Credit note' : f.id ? 'Edit draft invoice' : 'New invoice'" [subtitle]="f.kind === 'credit_note' ? 'Reduces invoice ' + f.originalNumber : 'Saved as a draft until you issue it'" width="max-w-4xl" (closed)="form.set(null)">
        <form (ngSubmit)="save(false)">
          <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <label class="block sm:col-span-2"><span class="label">Customer</span>
              <select class="input" name="c" [(ngModel)]="f.customerId" required [disabled]="f.kind === 'credit_note'"><option value="">Choose…</option>@for (c of customers(); track c.id) { @if (c.isActive || c.id === f.customerId) { <option [value]="c.id">{{ c.name }}</option> } }</select>
              @if (f.kind !== 'credit_note') { <span class="hint">Not in the list? Add them under <a routerLink="/customers" class="text-brand-600 hover:underline">Customers</a>.</span> }</label>
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="f.date" required /></label>
            @if (f.kind !== 'credit_note') { <label class="block"><span class="label">Due date</span><input type="date" class="input" name="dd" [(ngModel)]="f.dueDate" /><span class="hint">Empty = {{ setup()?.termsDays }} days.</span></label> }
            <label class="block"><span class="label">Currency</span><select class="input" name="cur" [(ngModel)]="f.currency" [disabled]="f.kind === 'credit_note'">@for (c of currencies(); track c) { <option [value]="c">{{ c }}</option> }</select></label>
            <label class="block"><span class="label">Reference (optional)</span><input class="input" name="r" [(ngModel)]="f.reference" maxlength="60" placeholder="Order number" /></label>
          </div>

          <div class="mt-5 overflow-x-auto">
            <table class="w-full text-sm">
              <thead><tr class="text-left text-xs text-slate-500"><th class="pb-1">Description</th><th class="w-24 pb-1 text-right">Qty</th><th class="w-28 pb-1 text-right">Price</th><th class="w-20 pb-1 pr-2 text-right">Disc. %</th><th class="w-48 pb-1 pl-1">Income account</th>@if (store.activeCentres().length) { <th class="w-36 pb-1">Cost centre</th> }<th class="w-28 pb-1 text-right">Amount</th><th class="w-8"></th></tr></thead>
              <tbody>
                @for (l of f.lines; track $index; let i = $index) {
                  <tr class="align-top">
                    <td class="py-1 pr-2"><input class="input" [name]="'ds' + i" [(ngModel)]="l.description" maxlength="300" [attr.list]="'products'" (change)="pickProduct(l)" placeholder="What was sold" /></td>
                    <td class="py-1 pr-2"><input type="number" step="any" min="0" class="input text-right" [name]="'q' + i" [(ngModel)]="l.quantity" /></td>
                    <td class="py-1 pr-2"><input type="number" step="0.01" min="0" class="input text-right" [name]="'p' + i" [(ngModel)]="l.unitPrice" /></td>
                    <td class="py-1 pr-2"><input type="number" step="0.01" min="0" max="100" class="input text-right" [name]="'dc' + i" [(ngModel)]="l.discountPercent" /></td>
                    <td class="py-1 pr-2"><select class="input" [name]="'a' + i" [(ngModel)]="l.accountId">@for (a of incomeAccounts(); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></td>
                    @if (store.activeCentres().length) { <td class="py-1 pr-2"><select class="input" [name]="'cc' + i" [(ngModel)]="l.costCentreId"><option [ngValue]="null">—</option>@for (c of store.activeCentres(); track c.id) { <option [ngValue]="c.id">{{ c.code }}</option> }</select></td> }
                    <td class="py-1 pt-3 text-right tabular-nums">{{ money(amount(l)) }}</td>
                    <td class="py-1"><button type="button" class="btn-ghost btn-sm" (click)="removeLine(f, i)" [disabled]="f.lines.length === 1" aria-label="Remove line"><app-icon name="x" size="h-4 w-4" /></button></td>
                  </tr>
                }
              </tbody>
            </table>
            <datalist id="products">@for (p of products(); track p.id) { <option [value]="p.name"></option> }</datalist>
          </div>
          <button type="button" class="btn-ghost btn-sm mt-2" (click)="addLine(f)"><app-icon name="plus" size="h-4 w-4" />Add a line</button>
          <label class="mt-4 block"><span class="label">Notes on the invoice (optional)</span><textarea class="input" rows="2" name="n" [(ngModel)]="f.notes" maxlength="1000"></textarea></label>
          <p class="mt-3 text-right text-base">Total <strong class="tabular-nums">{{ money(formTotal(f)) }} {{ f.currency }}</strong> <span class="text-xs text-slate-500">· no VAT</span></p>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
            <button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button>
            <button class="btn-ghost" type="submit" [disabled]="busy() || !f.customerId">Save draft</button>
            <button class="btn" type="button" (click)="save(true)" [disabled]="busy() || !f.customerId || formTotal(f) <= 0">Save and issue</button>
          </div>
        </form>
      </app-modal>
    }

    @if (receive(); as r) {
      <app-modal title="Receive payment" [subtitle]="r.doc.customer + ' · ' + r.doc.number + ' · ' + money(r.doc.outstanding) + ' ' + r.doc.currency + ' owing'" width="max-w-md" (closed)="receive.set(null)">
        <form (ngSubmit)="doReceive()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="r.date" required /></label>
            <label class="block"><span class="label">Amount</span><input type="number" step="0.01" min="0.01" [max]="r.doc.outstanding" class="input text-right" name="a" [(ngModel)]="r.amount" required /></label>
            <label class="block sm:col-span-2"><span class="label">Paid into</span><select class="input" name="f" [(ngModel)]="r.account" required><option value="">Choose…</option>@for (a of store.meta()?.cashAccounts ?? []; track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Reference (optional)</span><input class="input" name="r" [(ngModel)]="r.reference" maxlength="50" placeholder="Transfer or EcoCash reference" /></label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="receive.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !r.amount || !r.account">Record payment</button></div>
        </form>
      </app-modal>
    }

    @if (footerForm() !== null) {
      <app-modal title="Invoice footer" subtitle="Printed at the foot of every invoice: bank details, payment terms" width="max-w-lg" (closed)="footerForm.set(null)">
        <textarea class="input" rows="5" maxlength="500" [ngModel]="footerForm()" (ngModelChange)="footerForm.set($event)" placeholder="Pay to: CBZ Bank, account 0123456789, branch Harare"></textarea>
        <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" (click)="footerForm.set(null)">Cancel</button><button class="btn" (click)="saveFooter()">Save</button></div>
      </app-modal>
    }
  `,
})
export class SalesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly setup = signal<Setup | null>(null);
  readonly docs = signal<SalesDoc[]>([]);
  readonly customers = signal<Customer[]>([]);
  readonly products = signal<Product[]>([]);
  readonly loaded = signal(false);
  readonly open = signal('');
  readonly status = signal('');
  readonly customer = signal('');
  readonly q = signal('');
  readonly form = signal<DocForm | null>(null);
  readonly receive = signal<ReceiveForm | null>(null);
  readonly footerForm = signal<string | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly incomeAccounts = computed(() => this.store.postable().filter(a => a.isActive && a.type === 'Income'));
  readonly currencies = computed(() => { const c = this.store.meta()?.currencies ?? ['USD']; return c.includes('USD') ? c : ['USD', ...c]; });
  readonly totals = computed(() => {
    const by = new Map<string, { currency: string; owing: number; overdue: number }>();
    for (const d of this.docs()) if (d.status === 'Issued' && d.outstanding > 0) { const t = by.get(d.currency) ?? { currency: d.currency, owing: 0, overdue: 0 }; t.owing += d.outstanding; if (d.daysOverdue) t.overdue += d.outstanding; by.set(d.currency, t); }
    return [...by.values()];
  });

  async ngOnInit(): Promise<void> {
    void this.store.loadCentres().catch(() => undefined);
    try {
      const [setup, customers] = await Promise.all([this.api.get<Setup>('/api/v1/accounting/sales/setup'), this.api.get<Customer[]>('/api/v1/customers', { includeInactive: true }), this.store.load()]);
      this.setup.set(setup); this.customers.set(customers);
      void this.api.get<Product[]>('/api/v1/products').then(p => this.products.set(p)).catch(() => undefined);
    } catch (e) { this.toast.err(e); }
    const quote = this.route.snapshot.queryParamMap.get('quote');
    if (quote && this.setup()?.allowed) {
      try { const d = await this.api.post<SalesDoc>('/api/v1/accounting/sales/from-quote/' + quote); this.toast.ok('Draft invoice made from the quote.'); this.open.set(d.id); void this.router.navigate([], { queryParams: {} }); }
      catch (e) { this.toast.err(e); }
    }
    await this.load();
  }

  async load(): Promise<void> {
    try { this.docs.set(await this.api.get<SalesDoc[]>('/api/v1/accounting/sales', { status: this.status(), customerId: this.customer(), q: this.q() })); this.loaded.set(true); } catch (e) { this.toast.err(e); }
  }

  kindLabel(d: SalesDoc): string { return d.kind === 'credit_note' ? 'Credit note' : d.kind === 'opening' ? 'Brought forward' : 'Invoice'; }
  statusLabel(d: SalesDoc): string {
    if (d.status !== 'Issued') return d.status;
    if (d.kind === 'credit_note') return 'Issued';
    return d.outstanding <= 0 ? 'Paid' : d.daysOverdue ? 'Overdue' : d.paid + d.credited > 0 ? 'Part paid' : 'Owing';
  }
  badge(d: SalesDoc): string {
    const s = this.statusLabel(d);
    return s === 'Paid' || s === 'Issued' ? 'badge-ok' : s === 'Overdue' ? 'badge-bad' : s === 'Draft' || s === 'Void' || s === 'Part paid' ? 'badge-warn' : 'badge-info';
  }

  amount(l: LineForm): number { return lineAmount(l); }
  formTotal(f: DocForm): number { return Math.round(f.lines.reduce((s, l) => s + lineAmount(l), 0) * 100) / 100; }
  private blankLine(kind = 'invoice'): LineForm {
    const sales = this.setup()?.defaultIncomeAccountId ?? this.incomeAccounts()[0]?.id ?? '';
    const returns = kind === 'credit_note' ? this.store.postable().find(a => a.role === 'sales_returns')?.id : undefined;
    return { productId: null, description: '', quantity: 1, unitPrice: null, discountPercent: null, accountId: returns ?? sales, costCentreId: null };
  }
  addLine(f: DocForm): void { f.lines.push(this.blankLine(f.kind)); this.form.update(x => (x ? { ...x } : x)); }
  removeLine(f: DocForm, i: number): void { f.lines.splice(i, 1); this.form.update(x => (x ? { ...x } : x)); }
  pickProduct(l: LineForm): void {
    const p = this.products().find(x => x.name === l.description);
    if (p) { l.productId = p.id; if (!l.unitPrice) l.unitPrice = p.unitPrice; this.form.update(x => (x ? { ...x } : x)); }
  }

  start(): void {
    this.error.set('');
    this.form.set({ id: null, kind: 'invoice', originalId: null, originalNumber: '', customerId: this.customer(), date: isoDate(new Date()), dueDate: '', currency: this.store.meta()?.defaultCurrency ?? 'USD', reference: '', notes: '', lines: [this.blankLine()] });
  }

  edit(d: SalesDoc): void {
    this.error.set('');
    this.form.set({ id: d.id, kind: d.kind, originalId: d.originalId ?? null, originalNumber: d.originalNumber ?? '', customerId: d.customerId, date: d.date.slice(0, 10), dueDate: d.kind === 'credit_note' ? '' : d.dueDate.slice(0, 10),
      currency: d.currency, reference: d.reference ?? '', notes: d.notes ?? '',
      lines: d.lines.map(l => ({ productId: l.productId ?? null, description: l.description, quantity: l.quantity, unitPrice: l.unitPrice, discountPercent: l.discountPercent || null, accountId: l.accountId, costCentreId: l.costCentreId ?? null })) });
  }

  credit(d: SalesDoc): void {
    this.error.set('');
    this.form.set({ id: null, kind: 'credit_note', originalId: d.id, originalNumber: d.number ?? '', customerId: d.customerId, date: isoDate(new Date()), dueDate: '', currency: d.currency, reference: d.number ?? '', notes: '',
      lines: [{ ...this.blankLine('credit_note'), description: 'Credit against ' + d.number, unitPrice: d.outstanding }] });
  }

  async save(issue: boolean): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      const doc = await this.api.put<SalesDoc>('/api/v1/accounting/sales', {
        id: f.id, kind: f.kind, originalId: f.originalId, customerId: f.customerId, date: f.date, dueDate: f.dueDate || null, currency: f.currency, reference: f.reference.trim() || null, notes: f.notes.trim() || null,
        lines: f.lines.filter(l => l.description.trim() || l.unitPrice).map(l => ({ productId: l.productId, description: l.description.trim(), quantity: Number(l.quantity) || 0, unitPrice: Number(l.unitPrice) || 0, discountPercent: Number(l.discountPercent) || 0, accountId: l.accountId || null, costCentreId: l.costCentreId })),
      });
      if (issue) { const done = await this.api.post<SalesDoc>('/api/v1/accounting/sales/' + doc.id + '/issue'); this.toast.ok(`${done.number} issued and posted to the books.`); }
      else this.toast.ok('Draft saved.');
      this.form.set(null); this.open.set(doc.id); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async issue(d: SalesDoc): Promise<void> {
    if (!(await this.dialog.confirm(`Issue this ${d.kind === 'credit_note' ? 'credit note' : 'invoice'} to ${d.customer} for ${money(d.total)} ${d.currency}? It is numbered and posted to the books; after that it can only be credited or voided.`, 'Issue'))) return;
    try { const done = await this.api.post<SalesDoc>('/api/v1/accounting/sales/' + d.id + '/issue'); this.toast.ok(`${done.number} issued.`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async remove(d: SalesDoc): Promise<void> {
    if (!(await this.dialog.confirm('Delete this draft?', 'Delete', true))) return;
    try { await this.api.delete('/api/v1/accounting/sales/' + d.id); this.toast.ok('Draft deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async voidDoc(d: SalesDoc): Promise<void> {
    if (!(await this.dialog.confirm(`Void ${d.number}? It is reversed in the books and stays listed as void.`, 'Void', true))) return;
    try { await this.api.post('/api/v1/accounting/sales/' + d.id + '/void'); this.toast.ok(`${d.number} voided.`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async pdf(d: SalesDoc): Promise<void> { try { await this.api.open('/api/v1/accounting/sales/' + d.id + '/pdf'); } catch (e) { this.toast.err(e); } }

  async email(d: SalesDoc): Promise<void> {
    const to = await this.dialog.prompt(`Email ${d.number} as a PDF. Leave the address empty to use the customer's (${d.customerEmail || 'none on file'}).`, d.customerEmail ?? 'name@example.com', 'Send');
    if (to == null) return;
    try { const r = await this.api.post<{ sentTo: string }>('/api/v1/accounting/sales/' + d.id + '/email', { email: to.trim() || null }); this.toast.ok('Sent to ' + r.sentTo + '.'); } catch (e) { this.toast.err(e); }
  }

  startReceive(d: SalesDoc): void {
    this.error.set('');
    const cash = this.store.meta()?.cashAccounts ?? [];
    this.receive.set({ doc: d, date: isoDate(new Date()), amount: d.outstanding, account: (cash.find(a => a.role === 'bank') ?? cash[0])?.id ?? '', reference: '' });
  }

  async doReceive(): Promise<void> {
    const r = this.receive(); if (!r) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.post('/api/v1/accounting/receivables/' + r.doc.id + '/receive', { date: r.date, amount: Number(r.amount), accountId: r.account, reference: r.reference.trim() || null }); this.receive.set(null); this.toast.ok('Payment recorded.'); await this.load(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async saveFooter(): Promise<void> {
    try { this.setup.set(await this.api.put<Setup>('/api/v1/accounting/sales/footer', { footer: this.footerForm() })); this.footerForm.set(null); this.toast.ok('Footer saved.'); } catch (e) { this.toast.err(e); }
  }
}
