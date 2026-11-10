import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Payment { id: string; date: string; amount: number; accountName: string; reference?: string | null; voided: boolean }
interface Invoice { source?: string; receiptId: string; invoiceNo: string; date: string; dueDate: string; customerKey: string; customer: string; tin?: string | null; currency: string; total: number; onAccount: number; credited: number; received: number; outstanding: number; daysOverdue: number; payments: Payment[] }
interface Statement { customer: string; tin?: string | null; currency: string; from: string; to: string; opening: number; lines: { date: string; kind: string; reference: string; description: string; debit: number; credit: number; balance: number }[]; closing: number }
interface ReceiveForm { inv: Invoice; date: string; amount: number | null; account: string; reference: string }

/** Customers who have not paid yet: every fiscal invoice left on account, what has been received or credited against it, and how late it is. */
@Component({
  selector: 'app-receivables',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Customers owing" subtitle="Invoices left on account, what has been paid or credited, and who is late.">
      @if (auth.can('accounting.manage')) { <button class="btn-ghost" (click)="termsOpen.set(true)"><app-icon name="calendar" size="h-4 w-4" />Payment terms: {{ terms() }} days</button> }
    </app-page-header>

    <div class="mb-5 grid gap-4 sm:grid-cols-3">
      @for (t of totals(); track t.currency) {
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Owed to you in {{ t.currency }}</div>
          <div class="text-2xl font-semibold tabular-nums">{{ money(t.owing) }}</div><div class="text-xs" [class]="t.overdue ? 'text-red-600' : 'text-slate-500'">{{ t.overdue ? money(t.overdue) + ' overdue' : 'Nothing overdue' }}</div></div>
      }
      @if (!totals().length && loaded()) { <div class="card sm:col-span-3 text-center text-slate-500">No customer owes you anything. Invoices paid in full at the till never appear here.</div> }
    </div>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-3">
        <label class="block"><span class="label">Show</span><select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); load()"><option value="open">Still owing</option><option value="overdue">Overdue</option><option value="paid">Settled</option><option value="all">All</option></select></label>
        <label class="block sm:col-span-2"><span class="label">Search</span><input class="input" placeholder="Customer or invoice number" [ngModel]="q()" (ngModelChange)="q.set($event)" (keyup.enter)="load()" /></label>
      </div>
    </div>

    <div class="space-y-3">
      @for (i of invoices(); track i.receiptId) {
        <article class="card p-0">
          <button type="button" class="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left" (click)="toggle(i.receiptId)" [attr.aria-expanded]="open() === i.receiptId">
            <div class="min-w-0 flex-1"><div class="truncate text-sm font-semibold">{{ i.customer }} <span class="font-mono text-xs font-normal text-slate-500">· {{ i.invoiceNo }}</span>@if (i.source === 'opening') { <span class="badge ml-1 text-[10px]">Brought forward</span> }@if (i.source === 'invoice') { <span class="badge ml-1 text-[10px]">From the books</span> }</div>
              <div class="text-xs text-slate-500">Issued {{ i.date | date: 'd MMM y' }} · due {{ i.dueDate | date: 'd MMM y' }}@if (i.daysOverdue) { <span class="font-medium text-red-600"> · {{ i.daysOverdue }} days late</span> }</div></div>
            <span class="badge" [class.badge-ok]="i.outstanding <= 0" [class.badge-bad]="i.outstanding > 0 && i.daysOverdue > 0" [class.badge-info]="i.outstanding > 0 && !i.daysOverdue">{{ i.outstanding <= 0 ? 'Settled' : i.received || i.credited ? 'Part settled' : 'Unpaid' }}</span>
            <div class="w-36 text-right"><div class="text-sm font-semibold tabular-nums">{{ money(i.outstanding) }} {{ i.currency }}</div><div class="text-xs text-slate-500">of {{ money(i.onAccount) }}</div></div>
            <app-icon name="chevron" size="h-4 w-4" class="text-slate-400 transition" [class.rotate-180]="open() === i.receiptId" />
          </button>
          @if (open() === i.receiptId) {
            <div class="space-y-3 border-t border-slate-100 px-5 py-3 text-sm dark:border-slate-800">
              <div class="grid gap-2 sm:grid-cols-4"><div><div class="text-xs text-slate-500">Invoice total</div><div class="tabular-nums">{{ money(i.total) }}</div></div><div><div class="text-xs text-slate-500">Left on account</div><div class="tabular-nums">{{ money(i.onAccount) }}</div></div>
                <div><div class="text-xs text-slate-500">Credited</div><div class="tabular-nums">{{ money(i.credited) }}</div></div><div><div class="text-xs text-slate-500">Received</div><div class="tabular-nums">{{ money(i.received) }}</div></div></div>
              @if (i.payments.length) {
                <ul class="divide-y divide-slate-100 rounded-xl border border-slate-200 dark:divide-slate-800 dark:border-slate-700">
                  @for (p of i.payments; track p.id) { <li class="flex items-center gap-3 px-3 py-2" [class.opacity-50]="p.voided"><span class="w-24 text-xs text-slate-500">{{ p.date | date: 'd MMM y' }}</span><span class="flex-1">{{ p.accountName }}{{ p.reference ? ' · ' + p.reference : '' }}@if (p.voided) { <span class="badge badge-warn ml-2">Voided</span> }</span>
                    <span class="tabular-nums">{{ money(p.amount) }}</span>@if (!p.voided && auth.can('accounting.manage')) { <button class="btn-ghost btn-sm" (click)="voidPayment(p)">Void</button> }</li> }
                </ul>
              }
              <div class="flex flex-wrap justify-end gap-2">
                <button class="btn-ghost btn-sm" (click)="openStatement(i)"><app-icon name="file-text" size="h-4 w-4" />Statement</button>
                @if (auth.can('accounting.manage') && i.outstanding > 0) { <button class="btn btn-sm" (click)="startReceive(i)"><app-icon name="card" size="h-4 w-4" />Receive payment</button> }
              </div>
            </div>
          }
        </article>
      } @empty { <div class="card text-center text-slate-500">{{ loaded() ? 'Nothing here.' : 'Loading…' }}</div> }
    </div>

    @if (receive(); as r) {
      <app-modal title="Receive a payment" [subtitle]="r.inv.customer + ' · ' + r.inv.invoiceNo + ' · ' + money(r.inv.outstanding) + ' ' + r.inv.currency + ' owing'" width="max-w-md" (closed)="receive.set(null)">
        <form (ngSubmit)="doReceive()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="r.date" required /></label>
            <label class="block"><span class="label">Amount</span><input type="number" step="0.01" min="0.01" [max]="r.inv.outstanding" class="input text-right" name="a" [(ngModel)]="r.amount" required /></label>
            <label class="block sm:col-span-2"><span class="label">Paid into</span><select class="input" name="f" [(ngModel)]="r.account" required><option value="">Choose…</option>@for (a of store.meta()?.cashAccounts ?? []; track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Reference (optional)</span><input class="input" name="r" [(ngModel)]="r.reference" maxlength="50" placeholder="Receipt, deposit or transfer number" /></label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="receive.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !r.amount || !r.account">Record payment</button></div>
        </form>
      </app-modal>
    }

    @if (termsOpen()) {
      <app-modal title="Payment terms" subtitle="How many days after the invoice date a customer's balance falls due." width="max-w-sm" (closed)="termsOpen.set(false)">
        <label class="block"><span class="label">Days</span><input type="number" min="0" max="365" class="input" [ngModel]="terms()" (ngModelChange)="terms.set($event)" /></label>
        <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="termsOpen.set(false)">Cancel</button><button class="btn" type="button" (click)="saveTerms()" [disabled]="busy()">Save</button></div>
      </app-modal>
    }

    @if (statement(); as s) {
      <app-modal title="Customer statement" [subtitle]="s.customer + (s.tin ? ' · TIN ' + s.tin : '') + ' · ' + s.currency" width="max-w-3xl" (closed)="statement.set(null)">
        <div class="mb-3 flex flex-wrap items-end gap-3 print:hidden">
          <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="stFrom()" (ngModelChange)="stFrom.set($event)" /></label>
          <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="stTo()" (ngModelChange)="stTo.set($event)" /></label>
          <button class="btn" type="button" (click)="reloadStatement()">Show</button><span class="flex-1"></span><button class="btn-ghost" type="button" (click)="print()"><app-icon name="file-text" size="h-4 w-4" />Print</button>
          <button class="btn-ghost" type="button" (click)="statementPdf()" [disabled]="stBusy()"><app-icon name="download" size="h-4 w-4" />PDF</button>
          <button class="btn-ghost" type="button" (click)="emailStatement()" [disabled]="stBusy()"><app-icon name="mail" size="h-4 w-4" />Email</button>
        </div>
        <table class="table"><thead><tr><th>Date</th><th>Reference</th><th>Description</th><th class="text-right">Charged</th><th class="text-right">Paid / credited</th><th class="text-right">Balance</th></tr></thead>
          <tbody><tr class="text-slate-500"><td colspan="5">Balance brought forward</td><td class="text-right tabular-nums">{{ money(s.opening) }}</td></tr>
            @for (l of s.lines; track $index) { <tr><td class="whitespace-nowrap">{{ l.date | date: 'd MMM y' }}</td><td class="font-mono text-xs">{{ l.reference }}</td><td>{{ l.description }}</td><td class="text-right tabular-nums">{{ l.debit ? money(l.debit) : '' }}</td><td class="text-right tabular-nums">{{ l.credit ? money(l.credit) : '' }}</td><td class="text-right font-medium tabular-nums">{{ money(l.balance) }}</td></tr> }
            @if (!s.lines.length) { <tr><td colspan="6" class="py-5 text-center text-slate-500">No movements in this period.</td></tr> }</tbody>
          <tfoot><tr class="font-semibold"><td colspan="5">Balance owing</td><td class="text-right tabular-nums">{{ money(s.closing) }}</td></tr></tfoot></table>
      </app-modal>
    }
  `,
})
export class ReceivablesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly invoices = signal<Invoice[]>([]);
  readonly all = signal<Invoice[]>([]);
  readonly loaded = signal(false);
  readonly open = signal('');
  readonly status = signal('open');
  readonly q = signal('');
  readonly terms = signal(30);
  readonly termsOpen = signal(false);
  readonly receive = signal<ReceiveForm | null>(null);
  readonly statement = signal<Statement | null>(null);
  readonly stKey = signal<{ key: string; currency: string } | null>(null);
  readonly stFrom = signal(isoDate(new Date(new Date().getFullYear(), 0, 1)));
  readonly stTo = signal(isoDate(new Date()));
  readonly busy = signal(false);
  readonly error = signal('');
  readonly totals = computed(() => {
    const by = new Map<string, { currency: string; owing: number; overdue: number }>();
    for (const i of this.all()) if (i.outstanding > 0) { const t = by.get(i.currency) ?? { currency: i.currency, owing: 0, overdue: 0 }; t.owing += i.outstanding; if (i.daysOverdue) t.overdue += i.outstanding; by.set(i.currency, t); }
    return [...by.values()];
  });

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); this.terms.set((await this.api.get<{ termsDays: number }>('/api/v1/accounting/receivables/settings')).termsDays); } catch (e) { this.toast.err(e); }
    await this.load();
  }

  async load(): Promise<void> {
    try {
      this.invoices.set(await this.api.get<Invoice[]>('/api/v1/accounting/receivables', { status: this.status(), q: this.q() }));
      this.all.set(this.status() === 'open' && !this.q() ? this.invoices() : await this.api.get<Invoice[]>('/api/v1/accounting/receivables', { status: 'open' }));
      this.loaded.set(true);
    } catch (e) { this.toast.err(e); }
  }

  toggle(id: string): void { this.open.set(this.open() === id ? '' : id); }

  startReceive(i: Invoice): void {
    this.error.set('');
    const cash = this.store.meta()?.cashAccounts ?? [];
    this.receive.set({ inv: i, date: isoDate(new Date()), amount: i.outstanding, account: (cash.find(a => a.role === 'cash') ?? cash[0])?.id ?? '', reference: '' });
  }

  async doReceive(): Promise<void> {
    const r = this.receive(); if (!r) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.post('/api/v1/accounting/receivables/' + r.inv.receiptId + '/receive', { date: r.date, amount: Number(r.amount), accountId: r.account, reference: r.reference.trim() || null }); this.receive.set(null); this.toast.ok('Payment recorded.'); await this.load(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async voidPayment(p: Payment): Promise<void> {
    if (!(await this.dialog.confirm('Void this payment of ' + money(p.amount) + '? It is reversed in the books.', 'Void', true))) return;
    try { await this.api.post('/api/v1/accounting/receivables/payments/' + p.id + '/void'); this.toast.ok('Payment voided.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async saveTerms(): Promise<void> {
    this.busy.set(true);
    try { const r = await this.api.put<{ termsDays: number }>('/api/v1/accounting/receivables/settings', { termsDays: Number(this.terms()) }); this.terms.set(r.termsDays); this.termsOpen.set(false); this.toast.ok('Terms saved.'); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  readonly stBusy = signal(false);
  async statementPdf(): Promise<void> {
    const k = this.stKey(); if (!k) return;
    this.stBusy.set(true);
    try { await this.api.open('/api/v1/accounting/customers/statement/pdf', { customer: k.key, currency: k.currency, from: this.stFrom(), to: this.stTo() }); } catch (e) { this.toast.err(e); } finally { this.stBusy.set(false); }
  }
  async emailStatement(): Promise<void> {
    const k = this.stKey(); const st = this.statement(); if (!k) return;
    const to = await this.dialog.prompt(`Email the statement to ${st?.customer ?? 'the customer'}. Address (leave empty to use the one on the customer record):`, '', 'Send');
    if (to === null) return;
    this.stBusy.set(true);
    try { const r = await this.api.post<{ sentTo: string }>('/api/v1/accounting/statements/email', { customer: k.key, currency: k.currency, from: this.stFrom(), to: this.stTo(), email: to.trim() || null }); this.toast.ok('Statement sent to ' + r.sentTo + '.'); }
    catch (e) { this.toast.err(e); } finally { this.stBusy.set(false); }
  }
  async openStatement(i: Invoice): Promise<void> { this.stKey.set({ key: i.customerKey, currency: i.currency }); await this.reloadStatement(); }

  async reloadStatement(): Promise<void> {
    const k = this.stKey(); if (!k) return;
    try { this.statement.set(await this.api.get<Statement>('/api/v1/accounting/customers/statement', { customer: k.key, currency: k.currency, from: this.stFrom(), to: this.stTo() })); } catch (e) { this.toast.err(e); }
  }

  print(): void { window.print(); }
}
