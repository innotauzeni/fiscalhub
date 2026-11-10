import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { ItemSuggestion, PreviewResult, Product, Receipt, SubmitReceiptRequest } from '../core/models';
import { ToastService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';
import { ReceiptViewDialogComponent } from './receipt-view-dialog.component';

interface CartLine { key: string; name: string; hsCode?: string; unitPrice: number; taxPercent?: number; isExempt: boolean; quantity: number; }

/**
 * A till for phones and tablets: tap items, see the total, choose how it was paid, charge. Big buttons, no forms. The sale is fiscalised and the customer's QR code is on screen a second later.
 */
@Component({
  selector: 'app-quick-sale',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DecimalPipe, FormsModule, IconComponent, ReceiptViewDialogComponent],
  template: `
    @if (done(); as r) {
      <div class="mx-auto max-w-md text-center">
        <div class="card p-8">
          <div class="mx-auto flex h-16 w-16 items-center justify-center rounded-full" [class]="r.status === 'Submitted' ? 'bg-emerald-100 text-emerald-600' : 'bg-amber-100 text-amber-600'"><app-icon name="check" size="h-8 w-8" /></div>
          <h1 class="mt-4 text-2xl font-semibold">{{ r.status === 'Submitted' ? 'Sale fiscalised' : r.status === 'Pending' ? 'Sale recorded' : 'Sale issued' }}</h1>
          <div class="mt-1 text-4xl font-bold tabular-nums">{{ r.total | number:'1.2-2' }} <span class="text-xl font-medium text-slate-500">{{ r.currency }}</span></div>
          <div class="mt-1 text-sm text-slate-500">{{ r.invoiceNo }}@if (r.receiptGlobalNo) { · ZIMRA #{{ r.receiptGlobalNo }} }</div>
          @if (r.status === 'Pending') { <p class="mt-3 rounded-lg bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-200">ZIMRA could not be reached. The sale is numbered and will be sent automatically.</p> }
          @if (qr()) { <img [src]="qr()" alt="ZIMRA verification QR code" class="mx-auto mt-5 h-48 w-48 rounded-xl border border-slate-200 bg-white p-2" style="image-rendering: pixelated" /> }
          <div class="mt-6 grid gap-2">
            <button class="btn justify-center py-3 text-base" (click)="newSale()">New sale</button>
            <button class="btn-ghost justify-center" (click)="viewing.set(r)">Show / send the invoice</button>
          </div>
        </div>
      </div>
    } @else {
      <div class="grid gap-5 lg:grid-cols-5">
        <section class="lg:col-span-3">
          <div class="mb-3 flex flex-wrap items-center gap-2">
            <input class="input min-w-0 flex-1" type="search" placeholder="Search items…" [(ngModel)]="q" name="q" (input)="onSearch()" aria-label="Search items" />
            @if (store.devices().length > 1) {
              <select class="input w-auto" [ngModel]="store.selected()?.id" (ngModelChange)="store.select($event)" name="dev" aria-label="Device">@for (d of store.devices(); track d.id) { <option [value]="d.id">Device {{ d.zimraDeviceId }}</option> }</select>
            }
            <button class="btn-ghost" (click)="custom.set(!custom())"><app-icon name="plus" size="h-4 w-4" />Other item</button>
          </div>

          @if (custom()) {
            <div class="card mb-3 grid gap-3 sm:grid-cols-4">
              <label class="block sm:col-span-2"><span class="label">Item</span><input class="input" [(ngModel)]="cName" name="cn" /></label>
              <label class="block"><span class="label">Price</span><input class="input" type="number" min="0" step="any" [(ngModel)]="cPrice" name="cp" inputmode="decimal" /></label>
              <label class="block"><span class="label">HS code</span><input class="input font-mono" [(ngModel)]="cHs" name="ch" maxlength="8" inputmode="numeric" /></label>
              <button class="btn sm:col-span-4" (click)="addCustom()">Add to the sale</button>
            </div>
          }

          <div class="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
            @for (t of tiles(); track t.key) {
              <button type="button" class="flex min-h-24 flex-col items-start justify-between rounded-2xl border border-slate-200 bg-white p-3 text-left shadow-sm transition active:scale-95 hover:border-brand-400 dark:border-slate-700 dark:bg-slate-900" (click)="add(t)">
                <span class="line-clamp-2 text-sm font-medium">{{ t.name }}</span>
                <span class="mt-2 text-base font-semibold tabular-nums">{{ t.unitPrice | number:'1.2-2' }}</span>
              </button>
            } @empty { <p class="col-span-full rounded-xl border border-dashed border-slate-300 p-6 text-center text-sm text-slate-500 dark:border-slate-700">{{ loading() ? 'Loading items…' : 'No items yet. Add some under Items, or use “Other item”.' }}</p> }
          </div>
        </section>

        <aside class="lg:col-span-2">
          <div class="card lg:sticky lg:top-0">
            <div class="mb-2 flex items-center justify-between"><div class="card-title mb-0">This sale</div>@if (cart().length) { <button class="text-sm text-slate-500 underline" (click)="cart.set([])">Clear</button> }</div>
            <ul class="divide-y divide-slate-100 dark:divide-slate-800">
              @for (l of cart(); track l.key) {
                <li class="flex items-center gap-2 py-2.5">
                  <div class="min-w-0 flex-1"><div class="truncate text-sm font-medium">{{ l.name }}</div><div class="text-xs text-slate-500">{{ l.unitPrice | number:'1.2-2' }} each</div></div>
                  <div class="flex items-center gap-1">
                    <button class="icon-btn" (click)="step(l, -1)" aria-label="One less"><app-icon name="minus" size="h-4 w-4" /></button>
                    <span class="w-7 text-center text-sm font-semibold tabular-nums">{{ l.quantity }}</span>
                    <button class="icon-btn" (click)="step(l, 1)" aria-label="One more"><app-icon name="plus" size="h-4 w-4" /></button>
                  </div>
                  <div class="w-20 text-right text-sm font-semibold tabular-nums">{{ l.unitPrice * l.quantity | number:'1.2-2' }}</div>
                </li>
              } @empty { <li class="py-8 text-center text-sm text-slate-500">Tap an item to start.</li> }
            </ul>

            @if (cart().length) {
              <div class="mt-4 flex items-baseline justify-between border-t border-slate-100 pt-3 dark:border-slate-800"><span class="text-sm text-slate-500">Total ({{ currency }})</span><span class="text-3xl font-bold tabular-nums">{{ total() | number:'1.2-2' }}</span></div>

              <div class="mt-4 grid grid-cols-3 gap-2" role="radiogroup" aria-label="Paid by">
                @for (m of methods; track m.id) {
                  <button type="button" role="radio" [attr.aria-checked]="pay === m.id" (click)="pay = m.id" class="rounded-xl border-2 px-2 py-2.5 text-sm font-medium transition"
                    [class]="pay === m.id ? 'border-brand-600 bg-brand-50 text-brand-700 dark:bg-brand-500/10 dark:text-brand-100' : 'border-slate-200 dark:border-slate-700'">{{ m.label }}</button>
                }
              </div>

              <details class="mt-3 text-sm"><summary class="cursor-pointer text-slate-500">Customer details (optional)</summary>
                <div class="mt-2 grid gap-2"><input class="input" placeholder="Customer name" [(ngModel)]="buyerName" name="bn" /><input class="input" placeholder="Customer TIN (10 digits)" [(ngModel)]="buyerTin" name="bt" inputmode="numeric" maxlength="10" /></div></details>

              <button class="btn mt-4 w-full justify-center py-3.5 text-lg" (click)="charge()" [disabled]="busy() || !store.selected()">{{ busy() ? 'Sending to ZIMRA…' : 'Charge ' + (total() | number:'1.2-2') }}</button>
              @if (!store.selected()) { <p class="hint">Register a device first.</p> }
            }
          </div>
        </aside>
      </div>
    }

    @if (viewing(); as v) { <app-receipt-view-dialog [receipt]="v" (closed)="viewing.set(null)" (changed)="viewing.set(null)" /> }
  `,
})
export class QuickSaleComponent implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(DeviceStore);

  readonly methods = [{ id: 'Cash', label: 'Cash' }, { id: 'Card', label: 'Card' }, { id: 'MobileWallet', label: 'EcoCash / mobile' }];
  readonly products = signal<Product[]>([]);
  readonly history = signal<ItemSuggestion[]>([]);
  readonly cart = signal<CartLine[]>([]);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly custom = signal(false);
  readonly done = signal<Receipt | null>(null);
  readonly viewing = signal<Receipt | null>(null);
  readonly qr = signal<string | null>(null);
  currency = 'USD'; pay = 'Cash'; q = ''; cName = ''; cPrice: number | null = null; cHs = ''; buyerName = ''; buyerTin = '';
  private timer: ReturnType<typeof setTimeout> | undefined;
  private ref = '';

  /** What can be tapped: the catalogue, or (when it is empty) what was sold before. */
  readonly tiles = computed<CartLine[]>(() => {
    const q = this.q.trim().toLowerCase();
    const fromCatalogue = this.products().filter(p => p.isActive).map(p => ({ key: 'p' + p.id, name: p.name, hsCode: p.hsCode, unitPrice: p.unitPrice, taxPercent: p.taxPercent, isExempt: p.isExempt, quantity: 1 }));
    const list = fromCatalogue.length ? fromCatalogue : this.history().map(h => ({ key: 'h' + h.name, name: h.name, hsCode: h.hsCode, unitPrice: h.unitPrice, taxPercent: h.taxPercent ?? undefined, isExempt: h.isExempt, quantity: 1 }));
    return q ? list.filter(t => t.name.toLowerCase().includes(q) || (t.hsCode ?? '').includes(q)) : list;
  });

  readonly total = computed(() => this.cart().reduce((s, l) => s + l.unitPrice * l.quantity, 0));

  async ngOnInit(): Promise<void> {
    this.newRef();
    try { await this.store.load(); } catch (e) { this.toast.err(e); }
    try { this.products.set(await this.api.get<Product[]>('/api/v1/products')); } catch { /* a user without the catalogue sees past items instead */ }
    try { this.history.set(await this.api.get<ItemSuggestion[]>('/api/v1/suggest/items', { take: 24 })); } catch { /* none */ }
    this.loading.set(false);
  }

  ngOnDestroy(): void { clearTimeout(this.timer); const u = this.qr(); if (u) URL.revokeObjectURL(u); }

  onSearch(): void { /* the tiles are computed from q */ }
  private newRef(): void { this.ref = 'pos-' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6); }

  add(t: CartLine): void {
    this.cart.update(c => { const e = c.find(x => x.key === t.key); return e ? c.map(x => x.key === t.key ? { ...x, quantity: x.quantity + 1 } : x) : [...c, { ...t, quantity: 1 }]; });
    if (!this.cart().length || this.cart().length === 1) { const p = this.products().find(x => 'p' + x.id === t.key); if (p?.currency) this.currency = p.currency; }
  }
  step(l: CartLine, d: number): void { this.cart.update(c => c.map(x => x.key === l.key ? { ...x, quantity: x.quantity + d } : x).filter(x => x.quantity > 0)); }

  addCustom(): void {
    if (!this.cName.trim() || !(Number(this.cPrice) > 0)) { this.toast.err(new Error('Enter the item name and a price.')); return; }
    const hs = this.cHs.trim(); if (hs && !/^(\d{4}|\d{6}|\d{8})$/.test(hs)) { this.toast.err(new Error('The HS code must have 4, 6 or 8 digits.')); return; }
    this.add({ key: 'c' + Date.now(), name: this.cName.trim(), hsCode: hs || undefined, unitPrice: Number(this.cPrice), isExempt: false, quantity: 1 });
    this.cName = ''; this.cPrice = null; this.cHs = ''; this.custom.set(false);
  }

  /** The device's standard rate for items without a rate of their own. */
  private standardRate(): number {
    const rates = (this.store.selected()?.taxes ?? []).filter(t => t.taxPercent !== undefined && t.taxPercent !== null && (!t.validTill || new Date(t.validTill) > new Date())).map(t => t.taxPercent as number);
    return rates.length ? Math.max(...rates) : 15;
  }

  async charge(): Promise<void> {
    const d = this.store.selected(); if (!d || !this.cart().length) return;
    const hsMissing = this.cart().find(l => !l.hsCode);
    if (hsMissing) { this.toast.err(new Error(`“${hsMissing.name}” has no HS code. Add one under Items (ZIMRA needs it on every item).`)); return; }
    const tin = this.buyerTin.trim();
    if (tin && !/^\d{10}$/.test(tin)) { this.toast.err(new Error('A customer TIN has 10 digits.')); return; }
    const now = new Date(), pad = (n: number) => String(n).padStart(2, '0');
    const base = `/api/v1/devices/${d.id}/receipts`;
    const req: SubmitReceiptRequest = {
      externalRef: this.ref, invoiceNo: `POS-${String(now.getFullYear()).slice(2)}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`,
      receiptType: 'FiscalInvoice', currency: this.currency, linesTaxInclusive: true, autoOpenDay: true, holdMinutes: 0,
      lines: this.cart().map(l => ({ name: l.name, hsCode: l.hsCode, quantity: l.quantity, unitPrice: l.unitPrice, taxPercent: l.isExempt ? undefined : l.taxPercent ?? this.standardRate(), isExempt: l.isExempt, isDiscount: false })),
    };
    if (this.buyerName.trim() && tin) req.buyer = { registerName: this.buyerName.trim(), tin };
    this.busy.set(true);
    try {
      // the same sale is only ever sent once: the reference stays until it is done, so a double tap or a retry returns the same invoice
      const pv = await this.api.post<PreviewResult>(`${base}/preview`, req);
      req.payments = [{ moneyType: this.pay, amount: Math.abs(pv.total) }];
      const r = await this.api.post<Receipt>(base, req);
      this.done.set(r); this.cart.set([]);
      if (r.receiptGlobalNo && r.qrUrl) { try { this.qr.set(URL.createObjectURL(await this.api.blob(`/api/v1/receipts/${r.id}/qr`))); } catch { /* the invoice is still issued */ } }
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  newSale(): void { const u = this.qr(); if (u) URL.revokeObjectURL(u); this.qr.set(null); this.done.set(null); this.newRef(); this.buyerName = ''; this.buyerTin = ''; this.pay = 'Cash'; }
}
