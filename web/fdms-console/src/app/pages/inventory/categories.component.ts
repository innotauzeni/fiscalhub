import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { InventoryStore, StockCategory } from '../../core/inventory.store';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Form { id: string | null; parentId: string | null; name: string; code: string; inventoryAccountId: string | null; cogsAccountId: string | null; isActive: boolean }

/** Categories and their subcategories. A category can keep its items' value and cost of sales on accounts of its own. */
@Component({
  selector: 'app-stock-categories',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Categories" subtitle="Group your items into categories and subcategories. Reports and the stock list can be filtered by them.">
      @if (auth.can('inventory.manage')) { <button class="btn" (click)="start(null)"><app-icon name="plus" size="h-4 w-4" />New category</button> }
    </app-page-header>
    <div class="card p-0"><div class="overflow-x-auto"><table class="table w-full text-sm">
      <thead><tr><th>Category</th><th>Code</th><th class="text-right">Items</th><th>Inventory account</th><th>Cost of sales account</th><th></th></tr></thead>
      <tbody>
        @for (c of store.categories(); track c.id) {
          <tr [class.opacity-60]="!c.isActive">
            <td [style.padding-left.rem]="c.level ? 2.25 : 1"><span [class.font-semibold]="!c.level">{{ c.name }}</span>@if (!c.isActive) { <span class="badge ml-2">Off</span> }</td>
            <td class="font-mono text-xs">{{ c.code }}</td><td class="text-right tabular-nums">{{ c.items }}</td>
            <td class="text-xs">{{ account(c.inventoryAccountId) || (c.level ? 'as its category' : 'default') }}</td><td class="text-xs">{{ account(c.cogsAccountId) || (c.level ? 'as its category' : 'default') }}</td>
            <td class="whitespace-nowrap text-right">@if (auth.can('inventory.manage')) {
              @if (!c.level) { <button class="btn-ghost btn-sm" (click)="start(c.id)" title="Add a subcategory"><app-icon name="plus" size="h-4 w-4" />Sub</button> }
              <button class="btn-ghost btn-sm" (click)="edit(c)" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
              @if (!c.items) { <button class="btn-ghost btn-sm" (click)="remove(c)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button> } }</td>
          </tr>
        } @empty { <tr><td colspan="6" class="py-6 text-center text-slate-500">No categories yet. Items can also be imported with their categories.</td></tr> }
      </tbody>
    </table></div></div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit category' : f.parentId ? 'New subcategory' : 'New category'" width="max-w-lg" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4">
            <label class="block"><span class="label">Under</span><select class="input" name="p" [(ngModel)]="f.parentId"><option [ngValue]="null">— (a main category)</option>@for (c of mains(); track c.id) { @if (c.id !== f.id) { <option [ngValue]="c.id">{{ c.name }}</option> } }</select></label>
            <label class="block"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="100" /></label>
            <label class="block"><span class="label">Code (optional)</span><input class="input font-mono" name="c" [(ngModel)]="f.code" maxlength="20" /></label>
            <label class="block"><span class="label">Inventory account (optional)</span><select class="input" name="ia" [(ngModel)]="f.inventoryAccountId"><option [ngValue]="null">{{ f.parentId ? 'As its category' : 'The default inventory account' }}</option>@for (a of assets(); track a.id) { <option [ngValue]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block"><span class="label">Cost of sales account (optional)</span><select class="input" name="ca" [(ngModel)]="f.cogsAccountId"><option [ngValue]="null">{{ f.parentId ? 'As its category' : 'The default cost of sales account' }}</option>@for (a of expenses(); track a.id) { <option [ngValue]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="a" [(ngModel)]="f.isActive" />Active</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="!f.name.trim()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class StockCategoriesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(InventoryStore);
  readonly books = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly form = signal<Form | null>(null);
  readonly error = signal('');
  readonly mains = computed(() => this.store.categories().filter(c => !c.level));
  readonly assets = computed(() => this.books.postable().filter(a => a.isActive && a.type === 'Asset' && a.subType !== 'cash'));
  readonly expenses = computed(() => this.books.postable().filter(a => a.isActive && a.type === 'Expense'));

  async ngOnInit(): Promise<void> { try { await Promise.all([this.store.loadCategories(), this.books.load().catch(() => undefined)]); } catch (e) { this.toast.err(e); } }
  account(id?: string | null): string { const a = id ? this.books.accounts().find(x => x.id === id) : null; return a ? a.code + ' ' + a.name : ''; }
  start(parentId: string | null): void { this.error.set(''); this.form.set({ id: null, parentId, name: '', code: '', inventoryAccountId: null, cogsAccountId: null, isActive: true }); }
  edit(c: StockCategory): void { this.error.set(''); this.form.set({ id: c.id, parentId: c.parentId ?? null, name: c.name, code: c.code ?? '', inventoryAccountId: c.inventoryAccountId ?? null, cogsAccountId: c.cogsAccountId ?? null, isActive: c.isActive }); }
  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    try { await this.api.put('/api/v1/inventory/categories', { ...f, name: f.name.trim(), code: f.code.trim() || null }); this.form.set(null); this.toast.ok('Saved.'); await this.store.loadCategories(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); }
  }
  async remove(c: StockCategory): Promise<void> {
    if (!(await this.dialog.confirm(`Delete ${c.path}?`, 'Delete', true))) return;
    try { await this.api.delete('/api/v1/inventory/categories/' + c.id); await this.store.loadCategories(); } catch (e) { this.toast.err(e); }
  }
}
