import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { QuoteLine, Recurring } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { LineEditorComponent } from '../shared/line-editor.component';
import { EmptyComponent } from '../shared/list.components';
import { ModalComponent } from '../shared/modal.components';
import { CurrencySelectComponent } from '../shared/currency-select.component';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

interface Form {
  id?: string; deviceId: string; name: string; customerName: string; customerTin: string; customerVat: string; customerEmail: string; customerPhone: string; currency: string; taxInclusive: boolean;
  paymentMethod: string; notes: string; lines: QuoteLine[]; frequency: string; nextRunDate: string; endDate: string; prefix: string; emailCustomer: boolean;
}

const today = (): string => new Date().toLocaleDateString('en-CA');

/** Invoices that repeat: rent, retainers, subscriptions. The gateway issues a normal fiscal invoice on each due date, optionally emails it, and warns you if one cannot be issued. */
@Component({
  selector: 'app-recurring',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CurrencySelectComponent, DatePipe, DecimalPipe, FormsModule, IconComponent, ModalComponent, PageHeaderComponent, EmptyComponent, LineEditorComponent],
  template: `
    <app-page-header title="Recurring invoices" subtitle="Set it once. A fiscal invoice is issued on every due date, without anyone remembering.">
      <button class="btn" (click)="start()" [disabled]="!store.devices().length"><app-icon name="plus" size="h-4 w-4" />New schedule</button>
    </app-page-header>

    <div class="space-y-3">
      @for (r of items(); track r.id) {
        <article class="card">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2"><h3 class="text-base font-semibold">{{ r.name }}</h3>
                <span class="badge" [class]="state(r).cls">{{ state(r).text }}</span></div>
              <div class="text-sm text-slate-600 dark:text-slate-300">{{ r.customerName }} · {{ r.frequency }} · {{ r.total | number:'1.2-2' }} {{ r.currency }} · device {{ r.zimraDeviceId }}</div>
              <div class="mt-1 text-sm">@if (r.isActive) { Next invoice <strong>{{ r.nextRunDate | date:'EEE d MMM y' }}</strong> as <span class="font-mono">{{ r.prefix }}{{ pad(r.nextNumber) }}</span> } @else { <span class="text-slate-500">Not issuing.</span> }
                @if (r.endDate) { <span class="text-slate-500"> Ends {{ r.endDate | date:'d MMM y' }}.</span> }</div>
            </div>
            <div class="flex flex-wrap gap-2">
              @if (r.isActive) { <button class="btn-ghost btn-sm" (click)="run(r)" [disabled]="busy() === r.id">{{ busy() === r.id ? 'Issuing…' : 'Issue now' }}</button> }
              <button class="btn-ghost btn-sm" (click)="toggle(r)">{{ r.isActive ? 'Pause' : 'Resume' }}</button>
              <button class="btn-ghost btn-sm" (click)="edit(r)">Edit</button>
              <button class="icon-btn text-red-600" (click)="remove(r)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
            </div>
          </div>
          @if (r.recent.length) {
            <div class="mt-3 border-t border-slate-100 pt-3 text-sm dark:border-slate-800">
              <div class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-500">Recent</div>
              <ul class="space-y-1">
                @for (x of r.recent; track x.id) {
                  <li class="flex flex-wrap items-center gap-2"><span class="badge" [class]="x.status === 'Ok' ? 'badge-ok' : 'badge-bad'">{{ x.status === 'Ok' ? 'Issued' : 'Failed' }}</span>
                    <span>{{ x.runDate | date:'d MMM y' }}</span>@if (x.invoiceNo) { <span class="font-mono text-xs">{{ x.invoiceNo }}</span> }@if (x.error) { <span class="text-red-600">{{ x.error }}</span> }</li>
                }
              </ul>
            </div>
          }
        </article>
      } @empty { <div class="card"><app-empty icon="repeat" title="No recurring invoices" hint="Add a schedule for anything you bill on a regular basis." /></div> }
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit schedule' : 'New schedule'" subtitle="Each due date issues a fiscal invoice on the chosen device." width="max-w-4xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-3">
            <label class="block sm:col-span-2"><span class="label">Name *</span><input class="input" name="nm" [(ngModel)]="f.name" maxlength="150" placeholder="Office rent, Moyo Retailers" /></label>
            <label class="block"><span class="label">Device *</span>
              <select class="input" name="dv" [(ngModel)]="f.deviceId">@for (d of store.devices(); track d.id) { <option [value]="d.id">{{ store.label(d) }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Customer name *</span><input class="input" name="cn" [(ngModel)]="f.customerName" maxlength="200" /></label>
            <label class="block"><span class="label">TIN</span><input class="input" name="tin" [(ngModel)]="f.customerTin" maxlength="10" inputmode="numeric" /></label>
            <label class="block"><span class="label">VAT number</span><input class="input" name="vat" [(ngModel)]="f.customerVat" maxlength="9" /></label>
            <label class="block"><span class="label">Email</span><input class="input" type="email" name="em" [(ngModel)]="f.customerEmail" /></label>
            <label class="block"><span class="label">Phone</span><input class="input" type="tel" name="ph" [(ngModel)]="f.customerPhone" /></label>
            <label class="block"><span class="label">How often</span>
              <select class="input" name="fr" [(ngModel)]="f.frequency">@for (x of frequencies; track x) { <option [value]="x">{{ x }}</option> }</select></label>
            <label class="block"><span class="label">First / next invoice date</span><input class="input" type="date" name="nx" [(ngModel)]="f.nextRunDate" /></label>
            <label class="block"><span class="label">Stop after (optional)</span><input class="input" type="date" name="en" [(ngModel)]="f.endDate" /></label>
            <label class="block"><span class="label">Invoice number prefix</span><input class="input" name="pf" [(ngModel)]="f.prefix" maxlength="20" /></label>
            <label class="block"><span class="label">Paid by</span>
              <select class="input" name="py" [(ngModel)]="f.paymentMethod">@for (m of methods; track m) { <option [value]="m">{{ m }}</option> }</select></label>
            <div class="grid grid-cols-2 gap-3">
              <label class="block"><span class="label">Currency</span><app-currency-select [(value)]="f.currency" /></label>
              <label class="flex items-end gap-2 pb-2 text-sm"><input type="checkbox" class="h-4 w-4" name="incl" [(ngModel)]="f.taxInclusive" />Prices include VAT</label>
            </div>
          </div>
          <div class="mt-5"><app-line-editor [(lines)]="f.lines" [inclusive]="f.taxInclusive" [currency]="f.currency" /></div>
          <label class="mt-4 block"><span class="label">Notes (printed on each invoice)</span><input class="input" name="nt" [(ngModel)]="f.notes" maxlength="1000" /></label>
          <label class="mt-3 flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="mail" [(ngModel)]="f.emailCustomer" />Email each invoice to the customer (needs their email address)</label>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
            <button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() === 'save'">{{ busy() === 'save' ? 'Saving…' : 'Save schedule' }}</button>
          </div>
        </form>
      </app-modal>
    }
  `,
})
export class RecurringComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(DeviceStore);

  readonly frequencies = ['Weekly', 'Monthly', 'Quarterly', 'Yearly'];
  readonly methods = ['Cash', 'Card', 'MobileWallet', 'BankTransfer', 'Credit', 'Other'];
  readonly items = signal<Recurring[]>([]);
  readonly form = signal<Form | null>(null);
  readonly busy = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); } catch (e) { this.toast.err(e); }
    await this.load();
  }

  async load(): Promise<void> { try { this.items.set(await this.api.get<Recurring[]>('/api/v1/recurring')); } catch (e) { this.toast.err(e); } }

  pad(n: number): string { return String(n).padStart(4, '0'); }
  state(r: Recurring): { text: string; cls: string } {
    if (r.recent[0]?.status === 'Failed' && r.isActive) return { text: 'Last run failed', cls: 'badge-bad' };
    return r.isActive ? { text: 'Active', cls: 'badge-ok' } : { text: r.endDate && new Date(r.nextRunDate) > new Date(r.endDate) ? 'Finished' : 'Paused', cls: '' };
  }

  start(): void {
    this.form.set({ deviceId: this.store.selected()?.id ?? '', name: '', customerName: '', customerTin: '', customerVat: '', customerEmail: '', customerPhone: '', currency: 'USD', taxInclusive: true, paymentMethod: 'Cash', notes: '',
      lines: [LineEditorComponent.blank()], frequency: 'Monthly', nextRunDate: today(), endDate: '', prefix: 'REC-', emailCustomer: false });
  }

  edit(r: Recurring): void {
    this.form.set({ id: r.id, deviceId: r.deviceId, name: r.name, customerName: r.customerName, customerTin: r.customerTin ?? '', customerVat: r.customerVat ?? '', customerEmail: r.customerEmail ?? '', customerPhone: r.customerPhone ?? '',
      currency: r.currency, taxInclusive: r.taxInclusive, paymentMethod: r.paymentMethod, notes: r.notes ?? '', lines: r.lines.map(l => ({ ...l })), frequency: r.frequency, nextRunDate: r.nextRunDate.slice(0, 10),
      endDate: r.endDate ? r.endDate.slice(0, 10) : '', prefix: r.prefix, emailCustomer: r.emailCustomer });
  }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set('save');
    try {
      await this.api.put('/api/v1/recurring', {
        id: f.id ?? null, deviceId: f.deviceId, name: f.name, customerName: f.customerName, customerTin: f.customerTin.trim() || null, customerVat: f.customerVat.trim() || null, customerEmail: f.customerEmail.trim() || null,
        customerPhone: f.customerPhone.trim() || null, currency: f.currency, taxInclusive: f.taxInclusive, paymentMethod: f.paymentMethod, notes: f.notes.trim() || null, frequency: f.frequency, nextRunDate: f.nextRunDate,
        endDate: f.endDate || null, prefix: f.prefix, emailCustomer: f.emailCustomer,
        lines: f.lines.map(l => ({ name: l.name, hsCode: l.hsCode?.trim() || null, quantity: Number(l.quantity), unitPrice: Number(l.unitPrice), taxPercent: l.isExempt ? null : Number(l.taxPercent), isExempt: l.isExempt })),
      });
      this.toast.ok('Schedule saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(null); }
  }

  async run(r: Recurring): Promise<void> {
    if (!(await this.dialog.confirm(`Issue the invoice for ${r.name} now? It is sent to ZIMRA and the schedule moves on to its next date.`, 'Issue now'))) return;
    this.busy.set(r.id);
    try { await this.api.post(`/api/v1/recurring/${r.id}/run`); this.toast.ok('Invoice issued.'); } catch (e) { this.toast.err(e); } finally { this.busy.set(null); await this.load(); }
  }

  async toggle(r: Recurring): Promise<void> {
    try { await this.api.post(`/api/v1/recurring/${r.id}/active`, { active: !r.isActive }); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async remove(r: Recurring): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the schedule “${r.name}”? Invoices already issued stay as they are.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/recurring/${r.id}`); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
