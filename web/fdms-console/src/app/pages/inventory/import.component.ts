import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import { isoDate, money } from '../../core/accounting.store';
import { InventoryStore, qty } from '../../core/inventory.store';
import { DialogService, ToastService } from '../../core/ui.services';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Row { code?: string | null; name: string; category?: string | null; subcategory?: string | null; unit?: string | null; barcode?: string | null; hsCode?: string | null; sellingPrice?: number | null;
  currency?: string | null; taxPercent?: number | null; cost?: number | null; reorderLevel?: number | null; reorderQuantity?: number | null; location?: string | null; openingQuantity?: number | null }
interface Check { newItems: number; updatedItems: number; unchanged: number; newCategories: number; newSubcategories: number; newLocations: number; openingLines: number; openingValue: number; problems: string[]; warnings: string[]; canPost: boolean }

/** Bringing in the item list from Excel or CSV: items, their categories and subcategories, and the stock on the shelves today. */
@Component({
  selector: 'app-stock-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Import items" subtitle="Your item list from Excel or CSV: items, categories, subcategories, prices, reorder levels and today's stock per location.">
      <button class="btn-ghost" (click)="template()"><app-icon name="download" size="h-4 w-4" />Excel template</button>
    </app-page-header>

    <ol class="space-y-5">
      <li class="card">
        <h2 class="card-title mb-2">1. Your file</h2>
        <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Columns: code, name, category, subcategory, unit, barcode, HS code, selling price, currency, tax %, cost, reorder level, reorder quantity, location, opening quantity. Other column names you use (SKU, description, UOM, qty, store…) are understood too.</p>
        <label class="btn cursor-pointer"><app-icon name="upload" size="h-4 w-4" />{{ busy() === 'read' ? 'Reading…' : 'Upload Excel or CSV' }}<input type="file" class="hidden" accept=".xlsx,.csv,.txt" (change)="upload($event)" /></label>
        @for (p of fileProblems(); track $index) { <p class="mt-1 text-sm text-red-700 dark:text-red-300">{{ p }}</p> }
        @if (rows().length) {
          <div class="mt-4 max-h-96 overflow-auto rounded-xl border border-slate-200 dark:border-slate-700"><table class="table w-full text-xs">
            <thead class="sticky top-0 bg-white dark:bg-slate-900"><tr><th>Code</th><th>Name</th><th>Category</th><th>Unit</th><th class="text-right">Price</th><th class="text-right">Cost</th><th class="text-right">Reorder</th><th>Location</th><th class="text-right">Opening qty</th></tr></thead>
            <tbody>@for (r of rows(); track $index) {
              <tr><td class="font-mono">{{ r.code }}</td><td>{{ r.name }}</td><td>{{ r.category }}{{ r.subcategory ? ' / ' + r.subcategory : '' }}</td><td>{{ r.unit }}</td><td class="text-right">{{ r.sellingPrice }}</td><td class="text-right">{{ r.cost }}</td>
                <td class="text-right">{{ r.reorderLevel }}</td><td>{{ r.location }}</td><td class="text-right">{{ qty(r.openingQuantity) }}</td></tr>
            }</tbody>
          </table></div>
          <p class="mt-2 text-xs text-slate-500">{{ rows().length }} item(s) · opening stock {{ money(openingValue()) }}</p>
        }
      </li>
      <li class="card">
        <h2 class="card-title mb-3">2. Check and import</h2>
        <div class="grid gap-4 sm:grid-cols-3">
          <label class="block"><span class="label">Opening stock date</span><input type="date" class="input" [ngModel]="date()" (ngModelChange)="date.set($event); result.set(null)" /><span class="hint">Usually the day you start counting stock here.</span></label>
          <label class="flex items-center gap-2 pt-6 text-sm"><input type="checkbox" [ngModel]="update()" (ngModelChange)="update.set($event); result.set(null)" />Update items already in my list (same code or name)</label>
        </div>
        <p class="mt-2 text-xs text-slate-500">Opening stock is posted against opening balance equity. If your opening trial balance already includes inventory, bring the stock in with the same value so the two agree (see Stock reports → On hand).</p>
        <div class="mt-4 flex gap-2"><button class="btn-ghost" (click)="check()" [disabled]="!rows().length || busy() !== ''"><app-icon name="check" size="h-4 w-4" />Check</button>
          <button class="btn" (click)="run()" [disabled]="!result()?.canPost || busy() !== ''"><app-icon name="upload" size="h-4 w-4" />{{ busy() === 'post' ? 'Importing…' : 'Import' }}</button></div>
        @if (result(); as r) {
          <div class="mt-4 grid gap-3 sm:grid-cols-4 text-sm">
            <div class="rounded-xl border border-slate-200 p-3 dark:border-slate-700"><div class="text-xs text-slate-500">Items</div>{{ r.newItems }} new · {{ r.updatedItems }} updated@if (r.unchanged) { · {{ r.unchanged }} left alone }</div>
            <div class="rounded-xl border border-slate-200 p-3 dark:border-slate-700"><div class="text-xs text-slate-500">Categories</div>{{ r.newCategories }} new · {{ r.newSubcategories }} new subcategories</div>
            <div class="rounded-xl border border-slate-200 p-3 dark:border-slate-700"><div class="text-xs text-slate-500">Locations</div>{{ r.newLocations }} new</div>
            <div class="rounded-xl border border-slate-200 p-3 dark:border-slate-700"><div class="text-xs text-slate-500">Opening stock</div>{{ r.openingLines }} line(s) · {{ money(r.openingValue) }}</div>
          </div>
          @if (r.problems.length) { <ul class="mt-3 space-y-1 rounded-lg bg-red-50 px-4 py-3 text-sm text-red-800 dark:bg-red-900/30 dark:text-red-200" role="alert">@for (p of r.problems; track $index) { <li>• {{ p }}</li> }</ul> }
          @if (r.warnings.length) { <ul class="mt-3 space-y-1 rounded-lg bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-100">@for (w of r.warnings; track $index) { <li>• {{ w }}</li> }</ul> }
        }
        @if (done()) { <p class="mt-3 rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-200">{{ done() }} <a routerLink="/inventory/items" class="underline">See the items</a></p> }
      </li>
    </ol>
  `,
})
export class StockImportComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(InventoryStore);
  readonly money = money; readonly qty = qty;
  readonly rows = signal<Row[]>([]);
  readonly fileProblems = signal<string[]>([]);
  readonly date = signal(isoDate(new Date()));
  readonly update = signal(true);
  readonly result = signal<Check | null>(null);
  readonly done = signal('');
  readonly busy = signal('');
  readonly openingValue = computed(() => this.rows().reduce((s, r) => s + (r.openingQuantity ?? 0) * (r.cost ?? 0), 0));

  async ngOnInit(): Promise<void> { void this.store.loadLocations().catch(() => undefined); }
  async template(): Promise<void> { try { await this.api.download('/api/v1/inventory/templates/items', 'fiscalhub-items-template.xlsx'); } catch (e) { this.toast.err(e); } }

  async upload(ev: Event): Promise<void> {
    const input = ev.target as HTMLInputElement; const file = input.files?.[0]; input.value = '';
    if (!file) return;
    this.busy.set('read'); this.result.set(null); this.done.set('');
    try {
      const fd = new FormData(); fd.append('file', file, file.name);
      const r = await this.api.post<{ rows: Row[]; problems: string[] }>('/api/v1/inventory/import/items/read', fd);
      this.rows.set(r.rows); this.fileProblems.set(r.problems);
      if (r.rows.length) await this.check();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(''); }
  }
  private body() { return { rows: this.rows(), date: this.date(), updateExisting: this.update() }; }
  async check(): Promise<void> {
    this.busy.set('check');
    try { this.result.set(await this.api.post<Check>('/api/v1/inventory/import/items/check', this.body())); } catch (e) { this.toast.err(e); } finally { this.busy.set(''); }
  }
  async run(): Promise<void> {
    if (!(await this.dialog.confirm(`Import ${this.rows().length} item(s)${this.result()?.openingLines ? ' and post their opening stock' : ''}?`, 'Import'))) return;
    this.busy.set('post');
    try {
      const r = await this.api.post<{ created: number; updated: number; categories: number; locations: number; openingDocuments: string[] }>('/api/v1/inventory/import/items', this.body());
      this.done.set(`${r.created} item(s) added, ${r.updated} updated, ${r.categories} categories and ${r.locations} location(s) created${r.openingDocuments.length ? '; opening stock posted as ' + r.openingDocuments.join(', ') : ''}.`);
      this.rows.set([]); this.result.set(null); await this.store.loadAll();
    } catch (e) { this.toast.err(errorMessage(e)); } finally { this.busy.set(''); }
  }
}
