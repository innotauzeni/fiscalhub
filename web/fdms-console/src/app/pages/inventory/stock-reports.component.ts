import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';
import { Api } from '../../core/api.service';
import { isoDate, money } from '../../core/accounting.store';
import { InventoryStore, StockItem, qty } from '../../core/inventory.store';
import { ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { PageHeaderComponent } from '../../shared/ui.components';

type Kind = 'on-hand' | 'card' | 'reorder';
interface OnHandRow { productId: string; code?: string | null; name: string; category?: string | null; unit?: string | null; quantity: number; averageCost: number; value: number; reorderLevel: number }
interface OnHand { asAt: string; currency: string; rows: OnHandRow[]; totalQuantity: number; totalValue: number; ledgerValue?: number | null; difference?: number | null }
interface CardLine { date: string; documentId: string; kind: string; number?: string | null; location: string; reference?: string | null; in: number; out: number; balance: number; unitCost: number; value: number }
interface Card { item: StockItem; from: string; to: string; openingQuantity: number; openingValue: number; lines: CardLine[]; closingQuantity: number; closingValue: number }
interface ReorderRow { productId: string; code?: string | null; name: string; category?: string | null; unit?: string | null; onHand: number; reorderLevel: number; suggested: number; lastCost: number; lastSupplier?: string | null }

const TITLES: Record<Kind, { title: string; sub: string }> = {
  'on-hand': { title: 'Stock on hand and valuation', sub: 'What was held on any date, at average cost, and whether it agrees with the inventory accounts in the books.' },
  'card': { title: 'Stock card', sub: 'Every movement of one item: what came in, what went out, and the balance after each.' },
  'reorder': { title: 'Reorder list', sub: 'Items at or below their reorder level, with a suggested order and who supplied them last.' },
};

@Component({
  selector: 'app-stock-reports',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header [title]="head().title" [subtitle]="head().sub"><app-export-menu [build]="exportDoc" [disabled]="!ready()" /></app-page-header>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        @if (kind() === 'card') {
          <label class="block lg:col-span-2"><span class="label">Item</span><select class="input" [ngModel]="item()" (ngModelChange)="item.set($event); load()"><option value="">Choose…</option>@for (i of store.stockItems(); track i.id) { <option [value]="i.id">{{ store.label(i) }}</option> }</select></label>
          <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event)" /></label>
        }
        @if (kind() !== 'reorder') {
          <label class="block"><span class="label">{{ kind() === 'card' ? 'To' : 'As at' }}</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event)" /></label>
          <label class="block"><span class="label">Location</span><select class="input" [ngModel]="location()" (ngModelChange)="location.set($event)"><option value="">All</option>@for (l of store.locations(); track l.id) { <option [value]="l.id">{{ l.code }} · {{ l.name }}</option> }</select></label>
        }
        @if (kind() === 'on-hand') { <label class="block"><span class="label">Category</span><select class="input" [ngModel]="category()" (ngModelChange)="category.set($event)"><option value="">All</option>@for (c of store.categories(); track c.id) { <option [value]="c.id">{{ c.level ? '— ' : '' }}{{ c.name }}</option> }</select></label> }
        <div class="flex items-end"><button class="btn w-full" (click)="load()">Show</button></div>
      </div>
    </div>

    @switch (kind()) {
      @case ('on-hand') { @if (onHand(); as d) {
        <div class="mb-5 grid gap-4 sm:grid-cols-3">
          <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Stock value</div><div class="text-2xl font-semibold tabular-nums">{{ money(d.totalValue) }}</div><div class="text-xs text-slate-500">{{ d.currency }} as at {{ d.asAt | date: 'd MMM y' }}</div></div>
          @if (d.ledgerValue != null) {
            <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Inventory in the books</div><div class="text-2xl font-semibold tabular-nums">{{ money(d.ledgerValue) }}</div><div class="text-xs text-slate-500">the inventory accounts' balance</div></div>
            <div class="rounded-2xl border p-4" [class]="d.difference ? 'border-amber-300 bg-amber-50 dark:bg-amber-900/20' : 'border-emerald-300 bg-emerald-50 dark:bg-emerald-900/20'"><div class="text-xs uppercase tracking-wide text-slate-500">Difference</div><div class="text-2xl font-semibold tabular-nums">{{ money(d.difference) }}</div>
              <div class="text-xs">{{ d.difference ? 'Inventory was posted in the books without stock (e.g. an opening trial balance), or stock without the books.' : 'The stock and the books agree.' }}</div></div>
          }
        </div>
        <div class="card p-0"><div class="overflow-x-auto"><table class="table w-full text-sm">
          <thead><tr><th>Item</th><th>Category</th><th class="text-right">Quantity</th><th class="text-right">Average cost</th><th class="text-right">Value</th></tr></thead>
          <tbody>@for (r of d.rows; track r.productId) {
            <tr><td>{{ r.name }} <span class="font-mono text-xs text-slate-500">{{ r.code }}</span></td><td class="text-xs">{{ r.category }}</td><td class="text-right tabular-nums" [class.text-red-600]="r.quantity < 0">{{ qty(r.quantity) }} <span class="text-xs text-slate-500">{{ r.unit }}</span></td>
              <td class="text-right tabular-nums">{{ money(r.averageCost) }}</td><td class="text-right tabular-nums">{{ money(r.value) }}</td></tr>
          } @empty { <tr><td colspan="5" class="py-6 text-center text-slate-500">No stock on that date.</td></tr> }</tbody>
          <tfoot><tr class="font-semibold"><td colspan="2">Total</td><td class="text-right tabular-nums">{{ qty(d.totalQuantity) }}</td><td></td><td class="text-right tabular-nums">{{ money(d.totalValue) }}</td></tr></tfoot>
        </table></div></div>
      } }
      @case ('card') { @if (card(); as d) {
        <div class="card p-0"><div class="overflow-x-auto"><table class="table w-full text-sm">
          <thead><tr><th>Date</th><th>Document</th><th>Location</th><th>Reference</th><th class="text-right">In</th><th class="text-right">Out</th><th class="text-right">Balance</th><th class="text-right">Unit cost</th><th class="text-right">Value</th></tr></thead>
          <tbody>
            <tr class="text-slate-500"><td colspan="6">Opening</td><td class="text-right tabular-nums">{{ qty(d.openingQuantity) }}</td><td></td><td class="text-right tabular-nums">{{ money(d.openingValue) }}</td></tr>
            @for (l of d.lines; track $index) {
              <tr><td class="whitespace-nowrap">{{ l.date | date: 'd MMM y' }}</td><td>{{ l.kind }} <span class="font-mono text-xs text-slate-500">{{ l.number }}</span></td><td>{{ l.location }}</td><td class="text-xs">{{ l.reference }}</td>
                <td class="text-right tabular-nums text-emerald-700">{{ l.in ? qty(l.in) : '' }}</td><td class="text-right tabular-nums text-red-600">{{ l.out ? qty(l.out) : '' }}</td><td class="text-right font-medium tabular-nums">{{ qty(l.balance) }}</td>
                <td class="text-right tabular-nums">{{ money(l.unitCost) }}</td><td class="text-right tabular-nums">{{ money(l.value) }}</td></tr>
            } @empty { <tr><td colspan="9" class="py-6 text-center text-slate-500">No movements in this period.</td></tr> }
          </tbody>
          <tfoot><tr class="font-semibold"><td colspan="6">Closing</td><td class="text-right tabular-nums">{{ qty(d.closingQuantity) }}</td><td></td><td class="text-right tabular-nums">{{ money(d.closingValue) }}</td></tr></tfoot>
        </table></div></div>
      } @else { <div class="card text-center text-slate-500">Choose an item.</div> } }
      @case ('reorder') {
        <div class="card p-0"><div class="overflow-x-auto"><table class="table w-full text-sm">
          <thead><tr><th>Item</th><th>Category</th><th class="text-right">On hand</th><th class="text-right">Reorder at</th><th class="text-right">Suggested order</th><th class="text-right">Last cost</th><th>Last supplier</th></tr></thead>
          <tbody>@for (r of reorder(); track r.productId) {
            <tr><td>{{ r.name }} <span class="font-mono text-xs text-slate-500">{{ r.code }}</span></td><td class="text-xs">{{ r.category }}</td><td class="text-right tabular-nums" [class.text-red-600]="r.onHand <= 0">{{ qty(r.onHand) }} <span class="text-xs text-slate-500">{{ r.unit }}</span></td>
              <td class="text-right tabular-nums">{{ qty(r.reorderLevel) }}</td><td class="text-right font-semibold tabular-nums">{{ qty(r.suggested) }}</td><td class="text-right tabular-nums">{{ money(r.lastCost) }}</td><td>{{ r.lastSupplier }}</td></tr>
          } @empty { <tr><td colspan="7" class="py-6 text-center text-slate-500">Nothing needs reordering. Set a reorder level on an item to see it here when it runs low.</td></tr> }</tbody>
        </table></div></div>
      }
    }
  `,
})
export class StockReportsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  readonly store = inject(InventoryStore);
  readonly money = (v: number | null | undefined): string => money(v ?? 0);
  readonly qty = qty;
  readonly kind = toSignal(this.route.paramMap.pipe(map(p => (p.get('kind') ?? 'on-hand') as Kind)), { initialValue: 'on-hand' as Kind });
  readonly head = computed(() => TITLES[this.kind()] ?? TITLES['on-hand']);
  readonly item = signal(''); readonly location = signal(''); readonly category = signal('');
  readonly from = signal(isoDate(new Date(new Date().getFullYear(), 0, 1))); readonly to = signal(isoDate(new Date()));
  readonly onHand = signal<OnHand | null>(null); readonly card = signal<Card | null>(null); readonly reorder = signal<ReorderRow[]>([]);
  readonly ready = computed(() => (this.kind() === 'on-hand' ? !!this.onHand() : this.kind() === 'card' ? !!this.card() : true));

  constructor() { effect(() => { this.kind(); untracked(() => void this.load()); }); }

  async ngOnInit(): Promise<void> {
    this.item.set(this.route.snapshot.queryParamMap.get('item') ?? '');
    try { await this.store.loadAll(); } catch (e) { this.toast.err(e); }
    await this.load();
  }

  async load(): Promise<void> {
    try {
      switch (this.kind()) {
        case 'on-hand': this.onHand.set(await this.api.get<OnHand>('/api/v1/inventory/reports/on-hand', { asAt: this.to(), locationId: this.location(), categoryId: this.category() })); break;
        case 'card': this.card.set(this.item() ? await this.api.get<Card>('/api/v1/inventory/reports/card', { productId: this.item(), locationId: this.location(), from: this.from(), to: this.to() }) : null); break;
        case 'reorder': this.reorder.set(await this.api.get<ReorderRow[]>('/api/v1/inventory/reports/reorder')); break;
      }
    } catch (e) { this.toast.err(e); }
  }

  readonly exportDoc = (): ExportDoc | null => {
    const where = this.location() ? ' · ' + (this.store.locations().find(l => l.id === this.location())?.name ?? '') : '';
    switch (this.kind()) {
      case 'on-hand': {
        const d = this.onHand(); if (!d) return null;
        return { title: 'Stock on hand and valuation', subtitle: `As at ${d.asAt.slice(0, 10)} · ${d.currency}${where}`,
          columns: [{ header: 'Code' }, { header: 'Item' }, { header: 'Category' }, { header: 'Quantity', kind: 'number' }, { header: 'Average cost', kind: 'money' }, { header: 'Value', kind: 'money' }],
          rows: [...d.rows.map(r => ({ cells: [r.code ?? '', r.name, r.category ?? '', String(r.quantity), num(r.averageCost), num(r.value)] })), { cells: ['', 'Total', '', String(d.totalQuantity), '', num(d.totalValue)], style: 'total' as const }] };
      }
      case 'card': {
        const d = this.card(); if (!d) return null;
        return { title: 'Stock card — ' + this.store.label(d.item), subtitle: `${d.from.slice(0, 10)} to ${d.to.slice(0, 10)}${where}`, landscape: true,
          columns: [{ header: 'Date' }, { header: 'Document' }, { header: 'Location' }, { header: 'Reference' }, { header: 'In', kind: 'number' }, { header: 'Out', kind: 'number' }, { header: 'Balance', kind: 'number' }, { header: 'Unit cost', kind: 'money' }, { header: 'Value', kind: 'money' }],
          rows: [{ cells: ['', 'Opening', '', '', '', '', String(d.openingQuantity), '', num(d.openingValue)], style: 'muted' as const },
            ...d.lines.map(l => ({ cells: [l.date.slice(0, 10), l.kind + ' ' + (l.number ?? ''), l.location, l.reference ?? '', l.in ? String(l.in) : '', l.out ? String(l.out) : '', String(l.balance), num(l.unitCost), num(l.value)] })),
            { cells: ['', 'Closing', '', '', '', '', String(d.closingQuantity), '', num(d.closingValue)], style: 'total' as const }] };
      }
      default:
        return { title: 'Reorder list', subtitle: 'Items at or below their reorder level',
          columns: [{ header: 'Code' }, { header: 'Item' }, { header: 'On hand', kind: 'number' }, { header: 'Reorder at', kind: 'number' }, { header: 'Suggested', kind: 'number' }, { header: 'Last cost', kind: 'money' }, { header: 'Last supplier' }],
          rows: this.reorder().map(r => ({ cells: [r.code ?? '', r.name, String(r.onHand), String(r.reorderLevel), String(r.suggested), num(r.lastCost), r.lastSupplier ?? ''] })) };
    }
  };
}
