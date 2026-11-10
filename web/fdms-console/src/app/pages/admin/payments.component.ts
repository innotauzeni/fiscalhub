import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { BillingInvoice, IssuedCode, PaymentRow } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { money } from '../../shared/public.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

type Tab = 'payments' | 'invoices';

/** Money coming in: confirm or reject bank transfers (with their proof), watch Paynow payments, and record cash or other payments by hand. */
@Component({
  selector: 'app-payments',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Payments" subtitle="Confirm bank transfers, follow online payments and record manual ones.">
      <button class="btn-ghost" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    <div class="mb-4 flex flex-wrap gap-1 border-b border-slate-200 dark:border-slate-800" role="tablist">
      <button type="button" class="tab" [class.tab-active]="tab() === 'payments'" (click)="setTab('payments')">Payments @if (waiting() > 0) { <span class="badge badge-warn">{{ waiting() }} to confirm</span> }</button>
      <button type="button" class="tab" [class.tab-active]="tab() === 'invoices'" (click)="setTab('invoices')">Invoices @if (openCount() > 0) { <span class="badge">{{ openCount() }} open</span> }</button>
    </div>

    @if (tab() === 'payments') {
      <div class="card">
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>Company</th><th>Invoice</th><th>Method</th><th>Amount</th><th>Status</th><th>Date</th><th class="text-right">Actions</th></tr></thead>
            <tbody>
              @for (p of payRows(); track p.id) {
                <tr>
                  <td class="font-medium">{{ p.clientName }}</td>
                  <td>{{ p.invoiceNumber }}<div class="hint mt-0">{{ p.reference }}</div></td>
                  <td>{{ methodLabel(p.method) }}</td>
                  <td class="whitespace-nowrap font-medium">{{ fmt(p.amount, p.currency) }}</td>
                  <td><span class="badge" [class.badge-ok]="p.status === 'Paid'" [class.badge-warn]="p.status === 'AwaitingConfirmation' || p.status === 'Pending' || p.status === 'CodeSent'" [class.badge-bad]="p.status === 'Failed' || p.status === 'Rejected' || p.status === 'Cancelled'">{{ statusLabel(p.status) }}</span>
                    @if (p.notes) { <div class="hint max-w-48 whitespace-pre-wrap">{{ p.notes }}</div> }</td>
                  <td class="whitespace-nowrap">{{ p.createdAt | date: 'd MMM yyyy, HH:mm' }}</td>
                  <td><div class="flex justify-end gap-1.5">
                    @if (p.hasProof) { <button class="icon-btn" (click)="proof(p)" title="View proof of payment" aria-label="View proof"><app-icon name="eye" size="h-4 w-4" /></button> }
                    @if (p.status === 'AwaitingConfirmation') {
                      <button class="btn btn-sm" (click)="startConfirm(p)">Confirm</button>
                      <button class="btn-ghost btn-sm" (click)="reject(p)">Reject</button>
                    }
                    @if (p.status === 'CodeSent') {
                      <button class="btn-ghost btn-sm" (click)="reissue(p)">New code</button>
                      <button class="btn-ghost btn-sm" (click)="reject(p)">Reject</button>
                    }
                    @if (p.status === 'Pending' && p.method === 'Paynow') { <button class="icon-btn" (click)="refresh(p)" title="Ask Paynow now" aria-label="Refresh"><app-icon name="refresh" size="h-4 w-4" /></button> }
                  </div></td>
                </tr>
              } @empty { <tr><td colspan="7"><app-empty icon="card" title="No payments yet" hint="Payments appear here as companies pay their invoices." /></td></tr> }
            </tbody>
          </table>
        </div>
        <app-pager [page]="page()" [pageSize]="size" [total]="payments().length" (pageChange)="page.set($event)" />
      </div>
    } @else {
      <div class="card">
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>Invoice</th><th>Company</th><th>Amount</th><th>Due</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
            <tbody>
              @for (i of invRows(); track i.id) {
                <tr>
                  <td><div class="font-medium">{{ i.number }}</div><div class="hint mt-0 max-w-64 truncate">{{ i.description }}</div></td>
                  <td>{{ i.clientName }}</td>
                  <td class="whitespace-nowrap font-medium">{{ fmt(i.total, i.currency) }}</td>
                  <td class="whitespace-nowrap">{{ i.dueAt | date: 'd MMM yyyy' }}</td>
                  <td><span class="badge" [class.badge-ok]="i.status === 'Paid'" [class.badge-warn]="i.status === 'Open'">{{ i.status }}</span>@if (i.paymentMethod) { <div class="hint">{{ i.paymentMethod }}</div> }</td>
                  <td><div class="flex justify-end gap-1.5">
                    <button class="icon-btn" (click)="pdf(i)" title="Download PDF" aria-label="PDF"><app-icon name="download" size="h-4 w-4" /></button>
                    @if (i.status === 'Open') { <button class="btn btn-sm" (click)="startMark(i)">Mark paid</button> }
                  </div></td>
                </tr>
              } @empty { <tr><td colspan="6"><app-empty icon="receipt" title="No invoices yet" /></td></tr> }
            </tbody>
          </table>
        </div>
        <app-pager [page]="page()" [pageSize]="size" [total]="invoices().length" (pageChange)="page.set($event)" />
      </div>
    }

    @if (confirming(); as p) {
      <app-modal title="Confirm this payment" [subtitle]="p.clientName + ' · ' + fmt(p.amount, p.currency) + ' · ' + methodLabel(p.method)" width="max-w-md" (closed)="confirming.set(null)">
        @if (p.reference) { <p class="mb-3 text-sm">Reference: <span class="font-mono">{{ p.reference }}</span></p> }
        <div class="space-y-2 text-sm">
          <label class="flex cursor-pointer items-start gap-2 rounded-lg border border-slate-200 p-3 dark:border-slate-700"><input type="radio" class="mt-0.5" name="how" [value]="true" [(ngModel)]="sendCode" /><span><strong>Send an activation code</strong><br /><span class="text-slate-500">The company types it under Billing and the account switches on. Use this when you have checked the payment yourself.</span></span></label>
          <label class="flex cursor-pointer items-start gap-2 rounded-lg border border-slate-200 p-3 dark:border-slate-700"><input type="radio" class="mt-0.5" name="how" [value]="false" [(ngModel)]="sendCode" /><span><strong>Activate now</strong><br /><span class="text-slate-500">Marks the invoice paid straight away.</span></span></label>
        </div>
        <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" (click)="confirming.set(null)">Cancel</button><button class="btn" [disabled]="busy()" (click)="confirm(p)">Confirm payment</button></div>
      </app-modal>
    }

    @if (issued(); as c) {
      <app-modal title="Activation code" subtitle="Shown once. It is also emailed to the company when email is set up." width="max-w-md" (closed)="issued.set(null)">
        <div class="rounded-xl border border-brand-100 bg-brand-50/60 p-5 text-center dark:border-brand-500/30 dark:bg-brand-500/10">
          <div class="select-all font-mono text-3xl font-bold tracking-widest">{{ c.code }}</div>
          <button type="button" class="btn-ghost btn-sm mt-3" (click)="copy(c.code)"><app-icon name="copy" size="h-4 w-4" />Copy code</button>
        </div>
        <ul class="mt-4 list-disc space-y-1 pl-5 text-sm text-slate-600 dark:text-slate-400">
          <li>Valid once, until <strong>{{ c.expiresAt | date: 'd MMM yyyy' }}</strong>, for this company's invoice only.</li>
          <li>{{ c.emailed ? 'It was emailed to the company.' : 'Email is not set up for this company, so give them the code yourself.' }}</li>
          <li>We store only a scrambled copy: if it is lost, use <em>New code</em> on the payment.</li>
        </ul>
        <div class="mt-5 flex justify-end"><button class="btn" (click)="issued.set(null)">Done</button></div>
      </app-modal>
    }

    @if (marking(); as i) {
      <app-modal [title]="'Record a payment for ' + i.number" [subtitle]="i.clientName + ' · ' + fmt(i.total, i.currency)" width="max-w-md" (closed)="marking.set(null)">
        <form (ngSubmit)="mark()">
          <div class="grid gap-4">
            <label class="block"><span class="label">How was it paid?</span><select class="input" name="m" [(ngModel)]="method"><option value="Cash">Cash</option><option value="BankTransfer">Bank transfer</option><option value="Other">Other</option></select></label>
            <label class="block"><span class="label">Reference (optional)</span><input class="input" name="r" [(ngModel)]="reference" maxlength="150" /></label>
            <label class="block"><span class="label">Note (optional)</span><input class="input" name="n" [(ngModel)]="notes" maxlength="500" /></label>
          </div>
          <label class="mt-4 flex cursor-pointer items-start gap-2 text-sm"><input type="checkbox" class="mt-0.5 h-4 w-4" name="sc" [(ngModel)]="sendCode" /><span><strong>Send an activation code</strong> instead of activating now. The company types the code under Billing and the account switches on. Recommended for cash.</span></label>
          <p class="hint mt-3">Without a code, the invoice is marked paid and applied straight away (extends the period, switches the plan, restores a suspended company).</p>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="marking.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">{{ sendCode ? 'Record and send code' : 'Mark as paid' }}</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class PaymentsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly tab = signal<Tab>('payments');
  readonly payments = signal<PaymentRow[]>([]);
  readonly invoices = signal<BillingInvoice[]>([]);
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly marking = signal<BillingInvoice | null>(null);
  readonly confirming = signal<PaymentRow | null>(null);
  readonly issued = signal<IssuedCode | null>(null);
  method = 'Cash'; reference = ''; notes = ''; sendCode = true;

  readonly waiting = computed(() => this.payments().filter(p => p.status === 'AwaitingConfirmation').length);
  readonly openCount = computed(() => this.invoices().filter(i => i.status === 'Open').length);
  readonly payRows = computed(() => slicePage(this.payments(), this.page(), this.size));
  readonly invRows = computed(() => slicePage(this.invoices(), this.page(), this.size));

  fmt(v: number, c: string): string { return money(v, c); }
  methodLabel(m: string): string { return ({ BankTransfer: 'Bank transfer', EcoCashManual: 'EcoCash (Send Money)', Stripe: 'Card (Stripe)' } as Record<string, string>)[m] ?? m; }
  statusLabel(s: string): string { return s === 'AwaitingConfirmation' ? 'To confirm' : s === 'CodeSent' ? 'Code sent' : s; }
  setTab(t: Tab): void { this.tab.set(t); this.page.set(1); }

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> {
    try {
      this.payments.set(await this.api.get<PaymentRow[]>('/api/v1/admin/billing/payments'));
      this.invoices.set(await this.api.get<BillingInvoice[]>('/api/v1/admin/billing/invoices'));
    } catch (e) { this.toast.err(e); }
  }

  async proof(p: PaymentRow): Promise<void> { try { await this.api.open(`/api/v1/admin/billing/payments/${p.id}/proof`); } catch (e) { this.toast.err(e); } }
  async pdf(i: BillingInvoice): Promise<void> { try { await this.api.open(`/api/v1/admin/billing/invoices/${i.id}/pdf`); } catch (e) { this.toast.err(e); } }

  startConfirm(p: PaymentRow): void { this.sendCode = true; this.confirming.set(p); }

  async confirm(p: PaymentRow): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.post<{ payment: PaymentRow; code?: IssuedCode }>(`/api/v1/admin/billing/payments/${p.id}/confirm`, { reference: null, sendCode: this.sendCode });
      this.confirming.set(null);
      if (r.code) this.issued.set(r.code); else this.toast.ok('Payment confirmed and applied.');
      await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  /** a lost code: issue a new one (the old one stops working) */
  async reissue(p: PaymentRow): Promise<void> {
    if (!(await this.dialog.confirm('Issue a new activation code? The previous code stops working.', 'New code'))) return;
    this.sendCode = true; await this.confirm(p);
  }

  async copy(text: string): Promise<void> { try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { this.toast.err(new Error('Copy failed - select the code and copy it manually.')); } }

  async reject(p: PaymentRow): Promise<void> {
    const reason = await this.dialog.prompt(`Why can't you confirm this payment from ${p.clientName}? They will see this.`, 'Reason', 'Reject');
    if (reason === null) return;
    try { await this.api.post(`/api/v1/admin/billing/payments/${p.id}/reject`, { reason }); this.toast.ok('Payment rejected.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async refresh(p: PaymentRow): Promise<void> {
    try { await this.api.post(`/api/v1/admin/billing/payments/${p.id}/refresh`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  startMark(i: BillingInvoice): void { this.method = 'Cash'; this.reference = ''; this.notes = ''; this.sendCode = true; this.marking.set(i); }

  async mark(): Promise<void> {
    const i = this.marking(); if (!i) return;
    this.busy.set(true);
    try {
      const r = await this.api.post<{ code?: IssuedCode }>(`/api/v1/admin/billing/invoices/${i.id}/mark-paid`, { method: this.method, reference: this.reference.trim() || null, notes: this.notes.trim() || null, sendCode: this.sendCode });
      this.marking.set(null);
      if (r.code) this.issued.set(r.code); else this.toast.ok('Invoice marked as paid.');
      await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
