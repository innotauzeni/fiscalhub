import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { Product } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../shared/list.components';
import { ModalComponent } from '../shared/modal.components';
import { money } from '../shared/public.components';
import { CurrencySelectComponent } from '../shared/currency-select.component';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';

interface Form { id?: string; code: string; name: string; hsCode: string; unit: string; unitPrice: number; currency: string; taxPercent: number | null; isExempt: boolean; isActive: boolean; }

/**
 * The item catalogue. ZIMRA rejects a line whose HS code is missing or the wrong length (RCPT047 / RCPT048), so each item keeps its HS code and tax rate once and
 * is picked on the invoice instead of being retyped.
 */
@Component({
  selector: 'app-products',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CurrencySelectComponent, FormsModule, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Items" subtitle="What you sell, with its HS code and tax kept once. Pick an item on a new invoice and the HS code and tax are right every time.">
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />New item</button>
    </app-page-header>

    <div class="mb-5 grid gap-4 sm:grid-cols-3">
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Items</div><div class="text-2xl font-semibold">{{ all().length }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">With an HS code</div><div class="text-2xl font-semibold text-emerald-600">{{ withHs() }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Missing an HS code</div><div class="text-2xl font-semibold" [class.text-amber-600]="all().length - withHs() > 0">{{ all().length - withHs() }}</div></div>
    </div>

    <div class="card mb-5 grid gap-3 sm:grid-cols-3">
      <label class="block sm:col-span-2"><span class="label">Search</span>
        <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
          <input class="input pl-9" placeholder="Name, code or HS code…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
      <label class="flex items-end gap-2 pb-2 text-sm"><input type="checkbox" class="h-4 w-4" [ngModel]="inactive()" (ngModelChange)="inactive.set($event); load()" /> Show inactive</label>
    </div>

    <div class="card">
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Item</th><th>HS code</th><th>Unit price</th><th>Tax</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (p of rows(); track p.id) {
              <tr>
                <td><div class="font-medium">{{ p.name }}</div><div class="hint mt-0">{{ p.code ? p.code : '' }}{{ p.code && p.unit ? ' · ' : '' }}{{ p.unit }}</div></td>
                <td>@if (p.hsCode) { <span class="code">{{ p.hsCode }}</span> } @else { <span class="badge badge-warn">missing</span> }</td>
                <td class="whitespace-nowrap tabular-nums">{{ fmt(p) }}</td>
                <td>{{ p.isExempt ? 'Exempt' : p.taxPercent !== undefined && p.taxPercent !== null ? p.taxPercent + '%' : 'Device default' }}</td>
                <td><app-status [value]="p.isActive ? 'ACTIVE' : 'INACTIVE'" /></td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn" (click)="edit(p)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-danger" (click)="remove(p)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="6"><app-empty icon="stack" title="No items yet" hint="Add what you sell, with its HS code, to invoice faster and avoid ZIMRA rejections." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit item' : 'New item'" width="max-w-2xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-3">
            <label class="block"><span class="label">Code</span><input class="input font-mono" name="c" [(ngModel)]="f.code" maxlength="40" placeholder="BR-700" /></label>
            <label class="block sm:col-span-2"><span class="label">Name *</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="200" /></label>
            <label class="block"><span class="label">HS code</span>
              <input class="input font-mono" name="hs" [(ngModel)]="f.hsCode" inputmode="numeric" maxlength="8" placeholder="19059010" [class.border-red-500]="hsBad(f)" /></label>
            <label class="block"><span class="label">Unit</span><input class="input" name="u" [(ngModel)]="f.unit" maxlength="20" placeholder="each, kg, box" /></label>
            <label class="block"><span class="label">Currency</span><app-currency-select [(value)]="f.currency" /></label>
            <label class="block"><span class="label">Unit price</span><input class="input" type="number" min="0" step="any" name="pr" [(ngModel)]="f.unitPrice" /></label>
            <label class="block"><span class="label">Tax rate % (blank = device default)</span><input class="input" type="number" min="0" max="100" step="any" name="tx" [(ngModel)]="f.taxPercent" [disabled]="f.isExempt" placeholder="15" /></label>
            <label class="flex items-end gap-2 pb-2 text-sm"><input type="checkbox" class="h-4 w-4" name="ex" [(ngModel)]="f.isExempt" /> VAT exempt</label>
          </div>
          <p class="hint mt-3" [class.text-red-600]="hsBad(f)">An HS code is 4, 6 or 8 digits (ZIMRA tariff codes are 8). A VAT-registered business must give one on every line.</p>
          <label class="mt-3 flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="ac" [(ngModel)]="f.isActive" /> Active (shown when you invoice)</label>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || hsBad(f)">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class ProductsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly all = signal<Product[]>([]);
  readonly search = signal('');
  readonly inactive = signal(false);
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<Form | null>(null);

  readonly withHs = computed(() => this.all().filter(p => !!p.hsCode).length);
  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase();
    return this.all().filter(p => !q || `${p.name} ${p.code ?? ''} ${p.hsCode ?? ''}`.toLowerCase().includes(q));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  fmt(p: Product): string { return money(p.unitPrice, p.currency); }
  hsBad(f: Form): boolean { return !!f.hsCode.trim() && !/^(\d{4}|\d{6}|\d{8})$/.test(f.hsCode.trim()); }

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> { try { this.all.set(await this.api.get<Product[]>('/api/v1/products', { includeInactive: this.inactive() })); } catch (e) { this.toast.err(e); } }

  startNew(): void { this.form.set({ code: '', name: '', hsCode: '', unit: '', unitPrice: 0, currency: 'USD', taxPercent: null, isExempt: false, isActive: true }); }
  edit(p: Product): void { this.form.set({ id: p.id, code: p.code ?? '', name: p.name, hsCode: p.hsCode ?? '', unit: p.unit ?? '', unitPrice: p.unitPrice, currency: p.currency, taxPercent: p.taxPercent ?? null, isExempt: p.isExempt, isActive: p.isActive }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      await this.api.put('/api/v1/products', { id: f.id ?? null, code: f.code.trim() || null, name: f.name.trim(), hsCode: f.hsCode.trim() || null, unit: f.unit.trim() || null, unitPrice: Number(f.unitPrice) || 0,
        currency: f.currency.trim() || 'USD', taxPercent: f.isExempt || f.taxPercent === null || (f.taxPercent as unknown) === '' ? null : Number(f.taxPercent), isExempt: f.isExempt, isActive: f.isActive });
      this.toast.ok('Item saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(p: Product): Promise<void> {
    if (!(await this.dialog.confirm(`Delete "${p.name}"? Invoices already issued are not affected.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/products/${p.id}`); this.toast.ok('Item deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
