import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DeviceStore } from '../core/device.store';
import { Quote, QuoteLine, Receipt, SendResult } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { LineEditorComponent } from '../shared/line-editor.component';
import { EmptyComponent } from '../shared/list.components';
import { ModalComponent } from '../shared/modal.components';
import { CurrencySelectComponent } from '../shared/currency-select.component';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

interface Form {
  id?: string; number: string; customerName: string; customerTin: string; customerVat: string; customerEmail: string; customerPhone: string; currency: string; taxInclusive: boolean;
  validUntil: string; notes: string; lines: QuoteLine[];
}

const blank = (): Form => ({ number: '', customerName: '', customerTin: '', customerVat: '', customerEmail: '', customerPhone: '', currency: 'USD', taxInclusive: true, validUntil: '', notes: '', lines: [LineEditorComponent.blank()] });

/** Price quotations: write one, email it as a PDF, mark it accepted, and turn it into a fiscal invoice in one click. */
@Component({
  selector: 'app-quotes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CurrencySelectComponent, DatePipe, DecimalPipe, FormsModule, IconComponent, ModalComponent, PageHeaderComponent, EmptyComponent, LineEditorComponent],
  template: `
    <app-page-header title="Quotes" subtitle="Send a customer a price. When they accept, turn it into a fiscal invoice with one click.">
      <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New quote</button>
    </app-page-header>

    <div class="card mb-5">
      <div class="flex flex-wrap items-end gap-3">
        <label class="block"><span class="label">Status</span>
          <select class="input" [(ngModel)]="status" (change)="load()"><option value="">All</option>@for (s of statuses; track s) { <option [value]="s">{{ s }}</option> }</select></label>
        <label class="block min-w-56 flex-1"><span class="label">Search number or customer</span><input class="input" [(ngModel)]="search" (keydown.enter)="load()" /></label>
        <button class="btn-ghost" (click)="load()">Search</button>
      </div>
    </div>

    <div class="card overflow-x-auto">
      <table class="table">
        <thead><tr><th>Quote</th><th>Customer</th><th>Date</th><th>Valid until</th><th class="text-right">Total</th><th>Status</th><th></th></tr></thead>
        <tbody>
          @for (q of quotes(); track q.id) {
            <tr>
              <td class="font-medium">{{ q.number }}</td>
              <td>{{ q.customerName }}<div class="hint mt-0">{{ q.customerEmail }}</div></td>
              <td class="whitespace-nowrap">{{ q.createdAt | date:'d MMM y' }}</td>
              <td class="whitespace-nowrap" [class.text-red-600]="expired(q)">{{ q.validUntil ? (q.validUntil | date:'d MMM y') : '—' }}</td>
              <td class="text-right tabular-nums">{{ q.total | number:'1.2-2' }} {{ q.currency }}</td>
              <td><span class="badge" [class]="badge(q.status)">{{ q.status }}</span></td>
              <td class="whitespace-nowrap text-right">
                <button class="icon-btn" (click)="pdf(q)" title="Download PDF" aria-label="Download PDF"><app-icon name="file-text" size="h-4 w-4" /></button>
                @if (q.status !== 'Invoiced') {
                  <button class="icon-btn" (click)="mail.set(q); mailTo = q.customerEmail ?? ''; mailNote = ''" title="Email the customer" aria-label="Email"><app-icon name="mail" size="h-4 w-4" /></button>
                  <button class="icon-btn" (click)="edit(q)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  @if (q.status !== 'Declined' && books() && auth.can('accounting.manage')) { <button class="btn btn-sm ml-1" (click)="toBooks(q)">Make invoice</button> }
                  @else if (q.status !== 'Declined' && auth.can('receipts.submit')) { <button class="btn btn-sm ml-1" (click)="startConvert(q)">Make invoice</button> }
                  <button class="icon-btn text-red-600" (click)="remove(q)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                } @else { <span class="text-xs text-slate-500">Invoiced</span> }
              </td>
            </tr>
          } @empty { <tr><td colspan="7"><app-empty icon="clipboard" title="No quotes" hint="Press New quote to write your first one." /></td></tr> }
        </tbody>
      </table>
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit quote ' + f.number : 'New quote'" subtitle="Quotes are not tax invoices: the fiscal invoice is made when the customer accepts." width="max-w-4xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-3">
            <label class="block sm:col-span-2"><span class="label">Customer name *</span><input class="input" name="cn" [(ngModel)]="f.customerName" maxlength="200" /></label>
            <label class="block"><span class="label">Quote number</span><input class="input" name="no" [(ngModel)]="f.number" placeholder="Automatic" maxlength="50" /></label>
            <label class="block"><span class="label">TIN</span><input class="input" name="tin" [(ngModel)]="f.customerTin" maxlength="10" inputmode="numeric" /></label>
            <label class="block"><span class="label">VAT number</span><input class="input" name="vat" [(ngModel)]="f.customerVat" maxlength="9" /></label>
            <label class="block"><span class="label">Email</span><input class="input" type="email" name="em" [(ngModel)]="f.customerEmail" /></label>
            <label class="block"><span class="label">Phone</span><input class="input" type="tel" name="ph" [(ngModel)]="f.customerPhone" /></label>
            <label class="block"><span class="label">Valid until</span><input class="input" type="date" name="vu" [(ngModel)]="f.validUntil" /></label>
            <div class="grid grid-cols-2 gap-3">
              <label class="block"><span class="label">Currency</span><app-currency-select [(value)]="f.currency" /></label>
              <label class="flex items-end gap-2 pb-2 text-sm"><input type="checkbox" class="h-4 w-4" name="incl" [(ngModel)]="f.taxInclusive" />Prices include VAT</label>
            </div>
          </div>
          <div class="mt-5"><app-line-editor [(lines)]="f.lines" [inclusive]="f.taxInclusive" [currency]="f.currency" /></div>
          <label class="mt-4 block"><span class="label">Notes (shown on the quote)</span><input class="input" name="nt" [(ngModel)]="f.notes" maxlength="1000" placeholder="Delivery, payment terms…" /></label>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
            <button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Save quote' }}</button>
          </div>
        </form>
      </app-modal>
    }

    @if (mail(); as q) {
      <app-modal [title]="'Email ' + q.number" subtitle="The quote is attached as a PDF." width="max-w-md" (closed)="mail.set(null)">
        <label class="block"><span class="label">Send to</span><input class="input" type="email" [(ngModel)]="mailTo" name="mt" /></label>
        <label class="mt-3 block"><span class="label">Message (optional)</span><input class="input" [(ngModel)]="mailNote" name="mm" maxlength="300" /></label>
        <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" (click)="mail.set(null)">Cancel</button><button class="btn" (click)="send(q)" [disabled]="busy()">{{ busy() ? 'Sending…' : 'Send' }}</button></div>
      </app-modal>
    }

    @if (conv(); as q) {
      <app-modal [title]="'Make invoice from ' + q.number" subtitle="This is sent to ZIMRA and cannot be undone. Doing it twice never makes a second invoice." width="max-w-md" (closed)="conv.set(null)">
        <label class="block"><span class="label">Device</span>
          <select class="input" [(ngModel)]="deviceId" name="dv">@for (d of store.devices(); track d.id) { <option [value]="d.id">{{ store.label(d) }}</option> }</select></label>
        <label class="mt-3 block"><span class="label">Invoice number</span><input class="input" [(ngModel)]="invoiceNo" name="inv" maxlength="50" /></label>
        <label class="mt-3 block"><span class="label">Paid by</span>
          <select class="input" [(ngModel)]="pay" name="py">@for (m of methods; track m) { <option [value]="m">{{ m }}</option> }</select></label>
        <p class="mt-3 text-sm text-slate-600 dark:text-slate-300">Total <strong>{{ q.total | number:'1.2-2' }} {{ q.currency }}</strong> for {{ q.customerName }}.</p>
        <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" (click)="conv.set(null)">Cancel</button><button class="btn" (click)="convert(q)" [disabled]="busy() || !deviceId">{{ busy() ? 'Sending to ZIMRA…' : 'Issue the invoice' }}</button></div>
      </app-modal>
    }
  `,
})
export class QuotesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(DeviceStore);
  readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  /** A company on an accounting-only plan invoices from its books, not through a fiscal device. */
  readonly books = computed(() => this.auth.menus().some(m => m.slugname === 'accounting/sales'));
  toBooks(q: Quote): void { void this.router.navigate(['/accounting/sales'], { queryParams: { quote: q.id } }); }

  readonly statuses = ['Draft', 'Sent', 'Accepted', 'Declined', 'Invoiced'];
  readonly methods = ['Cash', 'Card', 'MobileWallet', 'BankTransfer', 'Credit', 'Other'];
  readonly quotes = signal<Quote[]>([]);
  readonly form = signal<Form | null>(null);
  readonly mail = signal<Quote | null>(null);
  readonly conv = signal<Quote | null>(null);
  readonly busy = signal(false);
  status = ''; search = ''; mailTo = ''; mailNote = ''; deviceId = ''; invoiceNo = ''; pay = 'Cash';

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); } catch { /* the convert dialog just has no devices */ }
    await this.load();
  }

  async load(): Promise<void> { try { this.quotes.set(await this.api.get<Quote[]>('/api/v1/quotes', { status: this.status || undefined, search: this.search || undefined })); } catch (e) { this.toast.err(e); } }

  badge(s: string): string { return s === 'Invoiced' || s === 'Accepted' ? 'badge-ok' : s === 'Declined' ? 'badge-bad' : s === 'Sent' ? 'badge-info' : ''; }
  expired(q: Quote): boolean { return !!q.validUntil && q.status !== 'Invoiced' && new Date(q.validUntil) < new Date(new Date().toDateString()); }

  start(): void { this.form.set(blank()); }
  edit(q: Quote): void {
    this.form.set({ id: q.id, number: q.number, customerName: q.customerName, customerTin: q.customerTin ?? '', customerVat: q.customerVat ?? '', customerEmail: q.customerEmail ?? '', customerPhone: q.customerPhone ?? '',
      currency: q.currency, taxInclusive: q.taxInclusive, validUntil: q.validUntil ? q.validUntil.slice(0, 10) : '', notes: q.notes ?? '', lines: q.lines.map(l => ({ ...l })) });
  }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      await this.api.put('/api/v1/quotes', {
        id: f.id ?? null, number: f.number.trim() || null, customerName: f.customerName, customerTin: f.customerTin.trim() || null, customerVat: f.customerVat.trim() || null, customerEmail: f.customerEmail.trim() || null,
        customerPhone: f.customerPhone.trim() || null, currency: f.currency, taxInclusive: f.taxInclusive, validUntil: f.validUntil || null, notes: f.notes.trim() || null,
        lines: f.lines.map(l => ({ name: l.name, hsCode: l.hsCode?.trim() || null, quantity: Number(l.quantity), unitPrice: Number(l.unitPrice), taxPercent: l.isExempt ? null : Number(l.taxPercent), isExempt: l.isExempt })),
      });
      this.toast.ok('Quote saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  pdf(q: Quote): Promise<void> { return this.api.download(`/api/v1/quotes/${q.id}/pdf`, `${q.number}.pdf`).catch(e => this.toast.err(e)); }

  async send(q: Quote): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.post<SendResult>(`/api/v1/quotes/${q.id}/email`, { to: this.mailTo.trim() || null, message: this.mailNote.trim() || null });
      if (r.sent || r.queued) { this.toast.ok(r.sent ? `Sent to ${r.to}.` : r.message); this.mail.set(null); await this.load(); } else this.toast.err(new Error(r.message));
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(q: Quote): Promise<void> {
    if (!(await this.dialog.confirm(`Delete quote ${q.number}?`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/quotes/${q.id}`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  startConvert(q: Quote): void {
    this.deviceId = this.store.selected()?.id ?? ''; this.pay = 'Cash';
    this.invoiceNo = 'INV-' + q.number.replace(/^[A-Za-z]+[-_/ ]*/, '');
    this.conv.set(q);
  }

  async convert(q: Quote): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.post<Receipt>(`/api/v1/quotes/${q.id}/convert`, { deviceId: this.deviceId, invoiceNo: this.invoiceNo.trim() || null, paymentMethod: this.pay });
      this.toast.ok(`Invoice ${r.invoiceNo} is ${r.status === 'Submitted' ? 'fiscalised' : r.status.toLowerCase()}.`); this.conv.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
