import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import { money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { InventoryStore, StockItem, qty } from '../../core/inventory.store';
import { ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface ItemForm { id: string | null; code: string; name: string; barcode: string; unit: string; hsCode: string; categoryId: string | null; unitPrice: number | null; currency: string; taxPercent: number | null; isExempt: boolean; isActive: boolean; trackStock: boolean; reorderLevel: number | null; reorderQuantity: number | null }

/** Stock items: what is on hand at each location, at what average cost, and what needs reordering. */
@Component({
  selector: 'app-stock-items',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, ModalComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Stock items" subtitle="What you hold, where, and what it is worth at average cost. GRVs bring stock in; sales take it out by themselves.">
      <app-export-menu [build]="exportDoc" [disabled]="!list().length" />
      @if (auth.can('inventory.manage')) {
        <a routerLink="/inventory/import" class="btn-ghost"><app-icon name="upload" size="h-4 w-4" />Import</a>
        <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New item</button>
      }
    </app-page-header>

    @if (store.settings(); as s) {
      @if (!s.startDate) { <p class="mb-5 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-100">Stock counting starts with your first GRV or opening stock. From that date on, every sale of a stock item takes it out of stock and posts its cost.</p> }
    }

    <div class="mb-5 grid gap-4 sm:grid-cols-4">
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Stock value</div><div class="text-2xl font-semibold tabular-nums">{{ money(totalValue()) }}</div><div class="text-xs text-slate-500">{{ store.settings()?.currency }} at average cost</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Stock items</div><div class="text-2xl font-semibold tabular-nums">{{ tracked() }}</div><div class="text-xs text-slate-500">counted in stock</div></div>
      <button type="button" class="rounded-2xl border border-slate-200 bg-white p-4 text-left dark:border-slate-800 dark:bg-slate-900" (click)="status.set('low')"><div class="text-xs uppercase tracking-wide text-slate-500">To reorder</div><div class="text-2xl font-semibold tabular-nums" [class.text-amber-600]="low()">{{ low() }}</div><div class="text-xs text-slate-500">at or below reorder level</div></button>
      <button type="button" class="rounded-2xl border border-slate-200 bg-white p-4 text-left dark:border-slate-800 dark:bg-slate-900" (click)="status.set('out')"><div class="text-xs uppercase tracking-wide text-slate-500">Out of stock</div><div class="text-2xl font-semibold tabular-nums" [class.text-red-600]="out()">{{ out() }}</div><div class="text-xs text-slate-500">nothing on hand</div></button>
    </div>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label class="block"><span class="label">Search</span><input class="input" placeholder="Name, code or barcode" [ngModel]="q()" (ngModelChange)="q.set($event)" /></label>
        <label class="block"><span class="label">Category</span><select class="input" [ngModel]="category()" (ngModelChange)="category.set($event)"><option value="">All</option>@for (c of store.categories(); track c.id) { <option [value]="c.id">{{ c.level ? '— ' : '' }}{{ c.name }}</option> }</select></label>
        <label class="block"><span class="label">Location</span><select class="input" [ngModel]="location()" (ngModelChange)="location.set($event)"><option value="">All</option>@for (l of store.locations(); track l.id) { <option [value]="l.id">{{ l.code }} · {{ l.name }}</option> }</select></label>
        <label class="block"><span class="label">Show</span><select class="input" [ngModel]="status()" (ngModelChange)="status.set($event)"><option value="stock">Stock items</option><option value="">Everything (incl. services)</option><option value="low">To reorder</option><option value="out">Out of stock</option><option value="inactive">Switched off</option></select></label>
      </div>
    </div>

    <div class="card p-0"><div class="overflow-x-auto"><table class="table w-full text-sm">
      <thead><tr><th>Item</th><th>Category</th><th>Where</th><th class="text-right">On hand</th><th class="text-right">Avg cost</th><th class="text-right">Value</th><th class="text-right">Price</th><th></th></tr></thead>
      <tbody>
        @for (i of list(); track i.id) {
          <tr [class.opacity-60]="!i.isActive">
            <td><div class="font-medium">{{ i.name }}</div><div class="font-mono text-xs text-slate-500">{{ i.code }}{{ i.barcode ? ' · ' + i.barcode : '' }}@if (!i.trackStock) { <span class="badge ml-1">Not counted</span> }</div></td>
            <td class="text-xs">{{ i.category }}</td>
            <td class="text-xs">@for (l of shown(i); track l.locationId) { <span class="mr-2 whitespace-nowrap"><span class="text-slate-500">{{ l.location }}</span> {{ qty(l.quantity) }}</span> }</td>
            <td class="text-right font-medium tabular-nums" [class.text-red-600]="i.trackStock && i.onHand <= 0" [class.text-amber-600]="i.belowReorder && i.onHand > 0">{{ i.trackStock ? qty(locQty(i)) : '' }} <span class="text-xs font-normal text-slate-500">{{ i.unit }}</span></td>
            <td class="text-right tabular-nums">{{ i.trackStock ? money(i.averageCost) : '' }}</td>
            <td class="text-right tabular-nums">{{ i.trackStock ? money(locValue(i)) : '' }}</td>
            <td class="text-right tabular-nums">{{ money(i.unitPrice) }} <span class="text-xs text-slate-500">{{ i.currency }}</span></td>
            <td class="whitespace-nowrap text-right">
              @if (i.trackStock) { <a class="btn-ghost btn-sm" routerLink="/inventory/reports/card" [queryParams]="{ item: i.id }" title="Stock card"><app-icon name="clipboard" size="h-4 w-4" /></a> }
              @if (auth.can('inventory.manage')) { <button class="btn-ghost btn-sm" (click)="edit(i)" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button> }
            </td>
          </tr>
        } @empty { <tr><td colspan="8" class="py-6 text-center text-slate-500">{{ loaded() ? 'No items match.' : 'Loading…' }}</td></tr> }
      </tbody>
    </table></div></div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit item' : 'New item'" subtitle="Stock items are counted, valued at average cost and taken out by sales" width="max-w-2xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="200" /><span class="hint">Use the same name on the till: fiscal sales find the item by its name or code.</span></label>
            <label class="block"><span class="label">Code (optional)</span><input class="input font-mono" name="c" [(ngModel)]="f.code" maxlength="40" /></label>
            <label class="block"><span class="label">Barcode (optional)</span><input class="input font-mono" name="b" [(ngModel)]="f.barcode" maxlength="60" /></label>
            <label class="block"><span class="label">Category</span><select class="input" name="cat" [(ngModel)]="f.categoryId"><option [ngValue]="null">—</option>@for (c of store.categories(); track c.id) { @if (c.isActive || c.id === f.categoryId) { <option [ngValue]="c.id">{{ c.path }}</option> } }</select></label>
            <label class="block"><span class="label">Unit</span><input class="input" name="u" [(ngModel)]="f.unit" maxlength="20" placeholder="each, kg, bag, litre" /></label>
            <label class="block"><span class="label">Selling price</span><input type="number" step="0.01" min="0" class="input text-right" name="p" [(ngModel)]="f.unitPrice" /></label>
            <label class="block"><span class="label">Currency</span><select class="input" name="cur" [(ngModel)]="f.currency">@for (c of currencies; track c) { <option [value]="c">{{ c }}</option> }</select></label>
            <label class="block"><span class="label">VAT %</span><input type="number" step="0.01" min="0" max="100" class="input text-right" name="t" [(ngModel)]="f.taxPercent" [disabled]="f.isExempt" /></label>
            <label class="block"><span class="label">HS code (for fiscal invoices)</span><input class="input font-mono" name="hs" [(ngModel)]="f.hsCode" maxlength="8" /></label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="ex" [(ngModel)]="f.isExempt" />Exempt from VAT</label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="ts" [(ngModel)]="f.trackStock" />Track stock (counted and valued)</label>
            @if (f.trackStock) {
              <label class="block"><span class="label">Reorder at (all locations)</span><input type="number" step="any" min="0" class="input text-right" name="rl" [(ngModel)]="f.reorderLevel" /></label>
              <label class="block"><span class="label">Reorder quantity</span><input type="number" step="any" min="0" class="input text-right" name="rq" [(ngModel)]="f.reorderQuantity" /></label>
            }
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="a" [(ngModel)]="f.isActive" />Active</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !f.name.trim()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class StockItemsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(InventoryStore);
  readonly auth = inject(AuthService);
  readonly money = money; readonly qty = qty;
  readonly currencies = ['USD', 'ZWG', 'ZAR'];
  readonly loaded = signal(false);
  readonly q = signal(''); readonly category = signal(''); readonly location = signal(''); readonly status = signal('stock');
  readonly form = signal<ItemForm | null>(null);
  readonly busy = signal(false); readonly error = signal('');

  readonly list = computed(() => {
    const q = this.q().trim().toLowerCase(); const cat = this.category(); const loc = this.location(); const st = this.status();
    const cats = new Set(cat ? this.store.categories().filter(c => c.id === cat || c.parentId === cat).map(c => c.id) : []);
    return this.store.items().filter(i =>
      (st === 'inactive' ? !i.isActive : i.isActive) && (st !== 'stock' && st !== 'low' && st !== 'out' || i.trackStock) && (st !== 'low' || i.belowReorder) && (st !== 'out' || i.onHand <= 0)
      && (!q || i.name.toLowerCase().includes(q) || i.code?.toLowerCase().includes(q) || i.barcode === this.q().trim())
      && (!cat || (i.categoryId != null && cats.has(i.categoryId))) && (!loc || i.levels.some(l => l.locationId === loc)));
  });
  readonly tracked = computed(() => this.store.items().filter(i => i.trackStock && i.isActive).length);
  readonly low = computed(() => this.store.items().filter(i => i.isActive && i.belowReorder).length);
  readonly out = computed(() => this.store.items().filter(i => i.isActive && i.trackStock && i.onHand <= 0).length);
  readonly totalValue = computed(() => this.list().reduce((s, i) => s + (i.trackStock ? this.locValue(i) : 0), 0));

  async ngOnInit(): Promise<void> { try { await this.store.loadAll(); this.loaded.set(true); } catch (e) { this.toast.err(e); } }

  shown(i: StockItem) { const loc = this.location(); return loc ? i.levels.filter(l => l.locationId === loc) : i.levels; }
  locQty(i: StockItem): number { const loc = this.location(); return loc ? i.levels.filter(l => l.locationId === loc).reduce((s, l) => s + l.quantity, 0) : i.onHand; }
  locValue(i: StockItem): number { return this.location() ? Math.round(this.locQty(i) * i.averageCost * 100) / 100 : i.value; }

  start(): void { this.error.set(''); this.form.set({ id: null, code: '', name: '', barcode: '', unit: '', hsCode: '', categoryId: this.category() || null, unitPrice: null, currency: this.store.settings()?.currency ?? 'USD', taxPercent: 15, isExempt: false, isActive: true, trackStock: true, reorderLevel: null, reorderQuantity: null }); }
  edit(i: StockItem): void {
    this.error.set('');
    this.form.set({ id: i.id, code: i.code ?? '', name: i.name, barcode: i.barcode ?? '', unit: i.unit ?? '', hsCode: i.hsCode ?? '', categoryId: i.categoryId ?? null, unitPrice: i.unitPrice, currency: i.currency, taxPercent: i.taxPercent ?? null,
      isExempt: i.isExempt, isActive: i.isActive, trackStock: i.trackStock, reorderLevel: i.reorderLevel || null, reorderQuantity: i.reorderQuantity || null });
  }
  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.put('/api/v1/inventory/items', { id: f.id, code: f.code.trim() || null, name: f.name.trim(), barcode: f.barcode.trim() || null, unit: f.unit.trim() || null, hsCode: f.hsCode.trim() || null, categoryId: f.categoryId,
        unitPrice: Number(f.unitPrice) || 0, currency: f.currency, taxPercent: f.isExempt ? null : f.taxPercent, isExempt: f.isExempt, isActive: f.isActive, trackStock: f.trackStock, reorderLevel: Number(f.reorderLevel) || 0, reorderQuantity: Number(f.reorderQuantity) || 0 });
      this.form.set(null); this.toast.ok('Item saved.'); await this.store.loadItems();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  readonly exportDoc = (): ExportDoc => ({
    title: 'Stock items', subtitle: (this.location() ? this.store.locations().find(l => l.id === this.location())?.name + ' · ' : '') + 'values at average cost in ' + (this.store.settings()?.currency ?? ''), landscape: true,
    columns: [{ header: 'Code' }, { header: 'Item' }, { header: 'Category' }, { header: 'Unit' }, { header: 'On hand', kind: 'number' }, { header: 'Average cost', kind: 'money' }, { header: 'Value', kind: 'money' }, { header: 'Reorder at', kind: 'number' }, { header: 'Price', kind: 'money' }],
    rows: [...this.list().map(i => ({ cells: [i.code ?? '', i.name, i.category ?? '', i.unit ?? '', i.trackStock ? String(this.locQty(i)) : '', i.trackStock ? num(i.averageCost) : '', i.trackStock ? num(this.locValue(i)) : '', i.reorderLevel ? String(i.reorderLevel) : '', num(i.unitPrice)] })),
      { cells: ['', 'Total', '', '', '', '', num(this.totalValue()), '', ''], style: 'total' as const }],
  });
}
