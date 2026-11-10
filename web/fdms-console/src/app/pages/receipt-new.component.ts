import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { Customer, DeviceTax, ItemSuggestion, PreviewResult, Product, Receipt, SubmitReceiptRequest } from '../core/models';
import { ToastService } from '../core/ui.services';
import { DeviceSelectComponent } from '../shared/device-select.component';
import { CurrencySelectComponent } from '../shared/currency-select.component';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';

interface Line { name: string; hsCode: string; quantity: number; unitPrice: number; taxId: number; }

@Component({
  selector: 'app-receipt-new',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CurrencySelectComponent, FormsModule, DecimalPipe, DeviceSelectComponent, IconComponent, PageHeaderComponent, StatusComponent],
  template: `
    <app-page-header title="New invoice" subtitle="Issue an invoice, credit note or debit note through ZIMRA." />

    <div class="grid gap-6 2xl:grid-cols-3">
      <form class="card 2xl:col-span-2" (ngSubmit)="submit()">
        <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <app-device-select (changed)="refreshTaxes(true)" />
          <label class="block"><span class="label">Type</span>
            <select class="input" name="type" [(ngModel)]="type"><option value="FiscalInvoice">Invoice</option><option value="CreditNote">Credit note</option><option value="DebitNote">Debit note</option></select></label>
          <label class="block"><span class="label">Invoice number</span><input class="input" name="no" [(ngModel)]="invoiceNo" required /></label>
          <label class="block"><span class="label">Currency</span><app-currency-select [(value)]="currency" /></label>
          <label class="block"><span class="label">Payment method</span>
            <select class="input" name="pay" [(ngModel)]="payment">@for (p of methods; track p) { <option [value]="p">{{ p }}</option> }</select></label>
          <label class="block"><span class="label">Hold for (minutes)</span><input class="input" type="number" min="0" max="60" name="hold" [(ngModel)]="hold" /></label>
        </div>
        <p class="hint">A hold keeps the invoice un-sent for a few minutes so a mistake can be cancelled with no trace at ZIMRA.</p>

        @if (type !== 'FiscalInvoice') {
          <div class="mt-4 grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Original receipt id (from the Receipts list)</span><input class="input" name="orig" [(ngModel)]="original" /></label>
            <label class="block"><span class="label">Reason</span><input class="input" name="why" [(ngModel)]="notes" /></label>
          </div>
        }

        @if (customers().length) {
          <label class="mt-4 block"><span class="label">Customer from your register</span>
            <select class="input" [ngModel]="''" name="cust" (ngModelChange)="pickCustomer($event)"><option value="">Choose a customer to fill in the buyer…</option>@for (c of customers(); track c.id) { <option [value]="c.id">{{ c.name }}{{ c.tin ? ' (' + c.tin + ')' : '' }}</option> }</select></label>
        }
        <div class="mt-4 grid gap-4 sm:grid-cols-3">
          <label class="block"><span class="label">Buyer name (optional)</span><input class="input" name="bn" [(ngModel)]="buyerName" /></label>
          <label class="block"><span class="label">Buyer TIN</span><input class="input" name="bt" [(ngModel)]="buyerTin" list="tin-list" inputmode="numeric" maxlength="10" (change)="tinPicked()" [class.border-red-500]="!!buyerTin && !tinOk()" /></label>
          <label class="flex items-end gap-2 pb-2 text-sm"><input type="checkbox" name="incl" [(ngModel)]="inclusive" class="h-4 w-4" /> Prices include tax</label>
        </div>

        <div class="mt-5 overflow-x-auto">
          <table class="table">
            <thead><tr><th>Item</th><th>HS code</th><th>Qty</th><th>Unit price</th><th>Tax</th><th></th></tr></thead>
            <tbody>
              @for (l of lines(); track $index; let i = $index) {
                <tr>
                  <td><input class="input min-w-40" [name]="'n' + i" [(ngModel)]="l.name" list="item-suggest" autocomplete="off" (input)="suggest(l.name)" (change)="pickSuggestion(l)" placeholder="Start typing an item…" /></td>
                  <td><input class="input w-32 font-mono" [name]="'h' + i" [(ngModel)]="l.hsCode" inputmode="numeric" maxlength="8" placeholder="8 digits" [class.border-red-500]="badHs(l)" [title]="badHs(l) ? 'HS code must be 4, 6 or 8 digits' : ''" /></td>
                  <td><input class="input w-20" type="number" step="any" [name]="'q' + i" [(ngModel)]="l.quantity" /></td>
                  <td><input class="input w-28" type="number" step="any" [name]="'p' + i" [(ngModel)]="l.unitPrice" /></td>
                  <td><select class="input w-44" [name]="'t' + i" [(ngModel)]="l.taxId">@for (x of taxes(); track x.taxId) { <option [ngValue]="x.taxId">{{ taxLabel(x) }}</option> } @empty { <option [ngValue]="0">Choose a device</option> }</select></td>
                  <td><button type="button" class="btn-ghost btn-sm" (click)="remove(i)" aria-label="Remove line"><app-icon name="trash" size="h-4 w-4" /></button></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <div class="mt-3 flex flex-wrap items-center gap-2">
          <button type="button" class="btn-ghost btn-sm" (click)="add()"><app-icon name="plus" size="h-4 w-4" />Add line</button>
          @if (products().length) {
            <select class="input w-auto max-w-56 py-1 text-xs" [ngModel]="''" name="prod" (ngModelChange)="addProduct($event)" aria-label="Add an item from your catalogue"><option value="">+ Add item from catalogue…</option>@for (p of products(); track p.id) { <option [value]="p.id">{{ p.name }}{{ p.hsCode ? ' · ' + p.hsCode : '' }}</option> }</select>
          }
          <button type="button" class="btn-ghost btn-sm" (click)="refreshTaxes(false)" [disabled]="refreshing()"><app-icon name="refresh" size="h-4 w-4" />{{ refreshing() ? 'Asking ZIMRA…' : 'Refresh taxes from ZIMRA' }}</button>
          <span class="hint mt-0">{{ taxes().length }} tax(es) configured for this device by ZIMRA.</span>
        </div>

        <datalist id="item-suggest">@for (s of suggestions(); track s.name) { <option [value]="s.name">{{ s.hsCode }} · {{ s.unitPrice }} {{ s.currency }} ({{ s.source === 'catalogue' ? 'your catalogue' : 'sold before' }})</option> }</datalist>
        <datalist id="tin-list">@for (c of customers(); track c.id) { @if (c.tin) { <option [value]="c.tin">{{ c.name }}</option> } }</datalist>

        @if (problems().length) {
          <div class="mt-5 rounded-xl border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-100" role="status">
            <div class="mb-1 flex items-center gap-2 font-semibold"><app-icon name="alert" size="h-4 w-4" />Check before you send ({{ problems().length }})</div>
            <ul class="list-disc space-y-0.5 pl-5">@for (p of problems(); track p) { <li>{{ p }}</li> }</ul>
          </div>
        }

        <div class="mt-6 flex flex-wrap gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
          <button type="button" class="btn-ghost" (click)="preview()" [disabled]="busy()">Preview</button>
          <button type="submit" class="btn" [disabled]="busy()">{{ hold > 0 ? 'Hold & send later' : 'Issue to ZIMRA' }}</button>
        </div>
      </form>

      <div class="space-y-5">
        @if (prev(); as p) {
          <div class="card">
            <div class="card-title">Preview — nothing was sent</div>
            <div class="text-2xl font-semibold tabular-nums">{{ p.total | number:'1.2-2' }} {{ p.currency }}</div>
            <table class="table mt-3">
              <thead><tr><th>Tax</th><th class="text-right">Tax amount</th><th class="text-right">Sales incl. tax</th></tr></thead>
              <tbody>@for (t of p.taxes; track t.taxId) { <tr><td>{{ t.taxPercent === undefined ? 'Exempt' : t.taxPercent + '%' }}</td><td class="text-right tabular-nums">{{ t.taxAmount | number:'1.2-2' }}</td><td class="text-right tabular-nums">{{ t.salesAmountWithTax | number:'1.2-2' }}</td></tr> }</tbody>
            </table>
          </div>
        }
        @if (done(); as r) {
          <div class="card">
            <div class="card-title">Result</div>
            <div class="flex items-center gap-2"><app-status [value]="r.status" /><span class="font-medium">{{ r.invoiceNo }}</span></div>
            <div class="mt-1 text-sm text-slate-500">{{ r.total | number:'1.2-2' }} {{ r.currency }}@if (r.receiptGlobalNo) { · receipt #{{ r.receiptGlobalNo }} }</div>
            @if (r.status === 'Held') { <p class="hint">Held — it will be sent automatically, or send/cancel it from Receipts.</p> }
            @if (r.status === 'Pending') { <p class="hint">ZIMRA could not be reached. The receipt is numbered and will be delivered automatically.</p> }
            <button class="btn-ghost btn-sm mt-3" (click)="router.navigateByUrl('/receipts')">Go to receipts</button>
          </div>
        }
      </div>
    </div>
  `,
})
export class ReceiptNewComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly store = inject(DeviceStore);
  readonly router = inject(Router);

  readonly methods = ['Cash', 'Card', 'MobileWallet', 'BankTransfer', 'Coupon', 'Credit', 'Other'];
  type = 'FiscalInvoice'; invoiceNo = ''; currency = 'USD'; payment = 'Cash'; hold = 0; inclusive = true;
  original = ''; notes = ''; buyerName = ''; buyerTin = '';
  /** Taxes configured on the chosen device. A line can only use one of these (ZIMRA rejects any other, RCPT025). */
  readonly taxes = computed<DeviceTax[]>(() => (this.store.selected()?.taxes ?? []).filter(x => !x.validTill || new Date(x.validTill) > new Date()));
  readonly lines = signal<Line[]>([{ name: '', hsCode: '', quantity: 1, unitPrice: 0, taxId: 0 }]);
  readonly busy = signal(false);
  readonly refreshing = signal(false);
  /** the customer register and item catalogue (empty when this user may not use them) */
  readonly customers = signal<Customer[]>([]);
  readonly products = signal<Product[]>([]);
  readonly suggestions = signal<ItemSuggestion[]>([]);
  private suggestTimer: ReturnType<typeof setTimeout> | undefined;
  readonly prev = signal<PreviewResult | null>(null);
  readonly done = signal<Receipt | null>(null);
  private reference = this.newReference();

  constructor() {
    // when the device (and so its tax list) changes, move any line whose tax the device does not have onto its default tax
    effect(() => {
      const ts = this.taxes(), def = this.defaultTax(ts);
      this.lines.update(ls => ls.map(l => (ts.some(x => x.taxId === l.taxId) ? l : { ...l, taxId: def })));
    });
  }

  async ngOnInit(): Promise<void> {
    // the customer register and item catalogue are optional: a user who may not use them simply sees no pickers
    this.api.get<Customer[]>('/api/v1/customers').then(c => this.customers.set(c)).catch(() => undefined);
    this.api.get<Product[]>('/api/v1/products').then(p => this.products.set(p)).catch(() => undefined);
    this.suggest('');
    try { await this.store.load(); } catch (e) { this.toast.err(e); }
    await this.refreshTaxes(true);
    // "Re-issue" from a receipt: start from a copy of it, so correcting an invoice is a few clicks instead of retyping it
    const from = (history.state as { reissue?: string } | null)?.reissue;
    if (from) await this.prefill(from);
  }

  /** Item names from your catalogue first, then from invoices you issued before. Typing narrows the list; picking one fills the HS code, price and tax. */
  suggest(q: string): void {
    clearTimeout(this.suggestTimer);
    this.suggestTimer = setTimeout(async () => {
      try { this.suggestions.set(await this.api.get<ItemSuggestion[]>('/api/v1/suggest/items', { q: q.trim() || undefined, take: 12 })); } catch { /* typing still works without suggestions */ }
    }, 200);
  }

  pickSuggestion(l: Line): void {
    const s = this.suggestions().find(x => x.name.toLowerCase() === l.name.trim().toLowerCase());
    if (!s) return;
    if (!l.hsCode.trim() && s.hsCode) l.hsCode = s.hsCode;
    if (!Number(l.unitPrice)) l.unitPrice = s.unitPrice;
    const ts = this.taxes();
    const tax = s.isExempt ? ts.find(x => x.taxPercent === undefined) : s.taxPercent !== undefined && s.taxPercent !== null ? ts.find(x => x.taxPercent === s.taxPercent) : undefined;
    if (tax) l.taxId = tax.taxId;
    if (this.lines().length === 1 && s.currency && !this.currency) this.currency = s.currency;
    this.lines.update(ls => [...ls]);
  }

  tinOk(): boolean { return /^\d{10}$/.test(this.buyerTin.trim()); }
  tinPicked(): void { const c = this.customers().find(x => x.tin === this.buyerTin.trim()); if (c) this.buyerName = c.name; }

  /** What would make ZIMRA refuse this invoice, spotted while typing instead of after sending. */
  problems(): string[] { return this.checkProblems(); }

  checkProblems(): string[] {
    const p: string[] = [];
    if (!this.invoiceNo.trim()) p.push('Give the invoice a number.');
    if (this.currency.trim().length !== 3) p.push('The currency must be a 3-letter code such as USD.');
    if (!this.taxes().length) p.push('This device has no taxes from ZIMRA. Press “Refresh taxes from ZIMRA”.');
    if (this.buyerTin.trim() && !this.tinOk()) p.push('A buyer TIN has 10 digits.');
    if (this.type !== 'FiscalInvoice' && !this.original.trim()) p.push('A credit or debit note needs the id of the original invoice.');
    this.lines().forEach((l, i) => {
      const n = 'Line ' + (i + 1) + ': ';
      if (!l.name.trim()) p.push(n + 'enter the item name.');
      if (!(Number(l.quantity) > 0)) p.push(n + 'the quantity must be above zero.');
      if (!(Number(l.unitPrice) > 0)) p.push(n + 'enter a price above zero.');
      if (!l.hsCode.trim()) p.push(n + 'add an HS code (VAT-registered businesses must give one on every item).');
      else if (this.badHs(l)) p.push(n + 'the HS code must have 4, 6 or 8 digits.');
    });
    return p;
  }

  private async prefill(receiptId: string): Promise<void> {
    try {
      const d = await this.api.get<{ receipt: Receipt; lines: { receiptLineName: string; receiptLineHSCode?: string; receiptLinePrice: number; receiptLineQuantity: number; taxPercent?: number }[];
        linesTaxInclusive: boolean; buyer?: { buyerRegisterName: string; buyerTin: string } }>(`/api/v1/receipts/${receiptId}/detail`);
      const ts = this.taxes(), def = this.defaultTax(ts);
      this.lines.set(d.lines.map(l => ({ name: l.receiptLineName, hsCode: l.receiptLineHSCode ?? '', quantity: Math.abs(l.receiptLineQuantity), unitPrice: Math.abs(l.receiptLinePrice),
        taxId: (l.taxPercent === undefined || l.taxPercent === null ? ts.find(x => x.taxPercent === undefined) : ts.find(x => x.taxPercent === l.taxPercent))?.taxId ?? def })));
      this.currency = d.receipt.currency; this.inclusive = d.linesTaxInclusive; this.invoiceNo = d.receipt.invoiceNo + '-R';
      this.buyerName = d.buyer?.buyerRegisterName ?? ''; this.buyerTin = d.buyer?.buyerTin ?? '';
      this.toast.ok('Copied from ' + d.receipt.invoiceNo + '. Fix what was wrong, then issue it.');
    } catch (e) { this.toast.err(e); }
  }

  /** Re-reads the device's taxes from ZIMRA (getConfig) so the dropdown always shows what ZIMRA has today. Quiet on failure when automatic. */
  async refreshTaxes(quiet: boolean): Promise<void> {
    const d = this.store.selected();
    if (!d) return;
    this.refreshing.set(true);
    try {
      await this.api.post(`/api/v1/devices/${d.id}/refresh-config`);
      await this.store.load(true);
      if (!quiet) this.toast.ok('Taxes refreshed from ZIMRA.');
    } catch (e) { if (!quiet) this.toast.err(e); }
    finally { this.refreshing.set(false); }
  }

  badHs(l: Line): boolean { const h = l.hsCode.trim(); return !!h && !/^(\d{4}|\d{6}|\d{8})$/.test(h); }

  taxLabel(x: DeviceTax): string { return x.taxPercent === undefined ? x.taxName : `${x.taxName} (${x.taxPercent}%)`; }
  /** The standard-rate tax if the device has one, otherwise its first tax. */
  private defaultTax(ts: DeviceTax[]): number { return (ts.filter(x => x.taxPercent !== undefined).sort((a, b) => (b.taxPercent ?? 0) - (a.taxPercent ?? 0))[0] ?? ts[0])?.taxId ?? 0; }

  private newReference(): string { return 'web-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6); }
  add(): void { this.lines.update(l => [...l, { name: '', hsCode: '', quantity: 1, unitPrice: 0, taxId: this.defaultTax(this.taxes()) }]); }
  pickCustomer(id: string): void { const c = this.customers().find(x => x.id === id); if (c) { this.buyerName = c.name; this.buyerTin = c.tin ?? ''; } }

  /** adds an item from the catalogue with its HS code and the device tax that matches its rate */
  addProduct(id: string): void {
    const p = this.products().find(x => x.id === id); if (!p) return;
    const ts = this.taxes();
    const tax = p.isExempt ? ts.find(x => x.taxPercent === undefined) : p.taxPercent !== undefined ? ts.find(x => x.taxPercent === p.taxPercent) : undefined;
    const line: Line = { name: p.name, hsCode: p.hsCode ?? '', quantity: 1, unitPrice: p.unitPrice, taxId: tax?.taxId ?? this.defaultTax(ts) };
    const empty = this.lines().length === 1 && !this.lines()[0].name.trim() && !Number(this.lines()[0].unitPrice);
    this.lines.update(l => (empty ? [line] : [...l, line]));
    if (empty && p.currency) this.currency = p.currency;
  }

  remove(i: number): void { this.lines.update(l => l.length > 1 ? l.filter((_, k) => k !== i) : l); }

  private build(withPayment: boolean): SubmitReceiptRequest {
    const bad = this.lines().findIndex(l => this.badHs(l));
    if (bad >= 0) throw new Error(`Line ${bad + 1}: the HS code must be 4, 6 or 8 digits (ZIMRA tariff codes are 8 digits).`);
    const req: SubmitReceiptRequest = {
      externalRef: this.reference, invoiceNo: this.invoiceNo.trim(), receiptType: this.type, currency: this.currency.trim().toUpperCase(),
      linesTaxInclusive: this.inclusive, autoOpenDay: true, holdMinutes: Number(this.hold) || 0,
      lines: this.lines().map(l => {
        const tax = this.taxes().find(x => x.taxId === l.taxId);
        return { name: l.name.trim(), hsCode: l.hsCode.trim() || undefined, quantity: Number(l.quantity), unitPrice: Number(l.unitPrice), taxId: l.taxId || undefined, isExempt: tax ? tax.taxPercent === undefined : false, isDiscount: false };
      }),
    };
    if (this.type !== 'FiscalInvoice') { req.originalReceiptId = this.original.trim() || undefined; req.notes = this.notes.trim() || undefined; }
    if (this.buyerName.trim() && this.buyerTin.trim()) req.buyer = { registerName: this.buyerName.trim(), tin: this.buyerTin.trim() };
    if (withPayment) req.payments = undefined;
    return req;
  }

  private url(suffix: string): string | null {
    const d = this.store.selected();
    if (!d) { this.toast.err(new Error('Choose a device first.')); return null; }
    return `/api/v1/devices/${d.id}/receipts${suffix}`;
  }

  async preview(): Promise<void> {
    const u = this.url('/preview'); if (!u) return;
    this.busy.set(true);
    try { this.prev.set(await this.api.post<PreviewResult>(u, this.build(false))); } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async submit(): Promise<void> {
    const u = this.url(''); const d = this.store.selected(); if (!u || !d) return;
    this.busy.set(true);
    try {
      // the whole amount is paid with the chosen method: ask for the total first, then send it as the payment
      const pv = await this.api.post<PreviewResult>(`${u}/preview`, this.build(false));
      const req = this.build(false);
      req.payments = [{ moneyType: this.payment, amount: Math.abs(pv.total) }];
      const r = await this.api.post<Receipt>(u, req);
      this.done.set(r); this.prev.set(null);
      this.reference = this.newReference();
      this.toast.ok(`Receipt ${r.status}.`);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
