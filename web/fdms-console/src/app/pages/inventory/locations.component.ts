import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { InventoryStore, StockLocation } from '../../core/inventory.store';
import { ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Branch { id: string; name: string }
interface Form { id: string | null; code: string; name: string; branchId: string | null; isDefault: boolean; isActive: boolean }

/** The places stock is kept. A location linked to a branch gives that branch's fiscal sales their stock; the default location serves the rest. */
@Component({
  selector: 'app-stock-locations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Locations" subtitle="Shops, warehouses and vans. Link a location to a branch so that branch's till sales take stock from it.">
      @if (auth.can('inventory.manage')) { <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New location</button> }
    </app-page-header>
    <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
      @for (l of store.locations(); track l.id) {
        <div class="card" [class.opacity-60]="!l.isActive">
          <div class="flex items-start justify-between gap-2">
            <div><div class="font-mono text-xs text-slate-500">{{ l.code }}</div><div class="text-lg font-semibold">{{ l.name }}</div>
              <div class="text-xs text-slate-500">{{ l.branchName ? 'Branch: ' + l.branchName : 'No branch' }}</div></div>
            <div class="flex flex-col items-end gap-1">@if (l.isDefault) { <span class="badge badge-info">Default</span> }@if (!l.isActive) { <span class="badge">Off</span> }</div>
          </div>
          <div class="mt-3 flex items-end justify-between"><div><div class="text-xs text-slate-500">{{ l.items }} item(s) in stock</div><div class="text-xl font-semibold tabular-nums">{{ money(l.value) }}</div></div>
            @if (auth.can('inventory.manage')) { <button class="btn-ghost btn-sm" (click)="edit(l)"><app-icon name="edit" size="h-4 w-4" />Edit</button> }</div>
        </div>
      } @empty { <div class="card text-center text-slate-500 sm:col-span-2 lg:col-span-3">No locations yet. Add your shop or warehouse.</div> }
    </div>
    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit location' : 'New location'" width="max-w-lg" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Code</span><input class="input font-mono uppercase" name="c" [(ngModel)]="f.code" required maxlength="20" placeholder="MAIN" /></label>
            <label class="block"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="100" placeholder="Main store" /></label>
            <label class="block sm:col-span-2"><span class="label">Branch (optional)</span><select class="input" name="b" [(ngModel)]="f.branchId"><option [ngValue]="null">—</option>@for (b of branches(); track b.id) { <option [ngValue]="b.id">{{ b.name }}</option> }</select></label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="d" [(ngModel)]="f.isDefault" />Default location</label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="a" [(ngModel)]="f.isActive" />Active</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class StockLocationsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(InventoryStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly branches = signal<Branch[]>([]);
  readonly form = signal<Form | null>(null);
  readonly error = signal('');

  async ngOnInit(): Promise<void> {
    try { await this.store.loadLocations(); } catch (e) { this.toast.err(e); }
    try { this.branches.set(await this.api.get<Branch[]>('/api/v1/branches')); } catch { /* without branches, locations still work */ }
  }
  start(): void { this.error.set(''); this.form.set({ id: null, code: '', name: '', branchId: null, isDefault: !this.store.locations().length, isActive: true }); }
  edit(l: StockLocation): void { this.error.set(''); this.form.set({ id: l.id, code: l.code, name: l.name, branchId: l.branchId ?? null, isDefault: l.isDefault, isActive: l.isActive }); }
  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    try { await this.api.put('/api/v1/inventory/locations', { ...f, code: f.code.trim().toUpperCase(), name: f.name.trim() }); this.form.set(null); this.toast.ok('Saved.'); await this.store.loadLocations(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); }
  }
}
