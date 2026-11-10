import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { ModuleTree, SubmoduleDef } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { CrumbsComponent, EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { ICON_NAMES, IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';
import { PermissionsDialogComponent } from './permissions-dialog.component';

/** Level 3: the submodules of one module. The shield button opens a popup with that submodule's permissions (with batch add). */
@Component({
  selector: 'app-module-submodules',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent, CrumbsComponent, PermissionsDialogComponent],
  template: `
    <app-crumbs [items]="[{ label: 'Menus & modules', link: ['/admin/modules'] }, { label: section()?.name ?? '…', link: ['/admin/modules', sectionId] }, { label: module()?.name ?? '…' }]" />
    <app-page-header [title]="(module()?.name ?? 'Module') + ' — submodules'" [subtitle]="module()?.description || 'Routes inside this module and the permission each one needs.'">
      <a class="btn-ghost" [routerLink]="['/admin/modules', sectionId]"><app-icon name="arrow-left" size="h-4 w-4" />Back</a>
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />Add submodule</button>
    </app-page-header>

    <div class="card mb-5">
      <div class="flex flex-wrap items-end gap-3">
        <label class="block min-w-56 flex-1"><span class="label">Search</span>
          <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
            <input class="input pl-9" placeholder="Search by name, route or access name…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
        <label class="block"><span class="label">Status</span>
          <select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); page.set(1)"><option value="">All</option><option value="active">Active</option><option value="inactive">Inactive</option></select></label>
        <button class="btn-ghost" (click)="search.set(''); status.set(''); page.set(1)">Clear</button>
      </div>
    </div>

    <div class="card">
      <div class="mb-3 flex items-center gap-3"><div class="card-title mb-0 flex items-center gap-2"><app-icon name="layers" size="h-4 w-4" />Submodules</div><span class="badge">{{ filtered().length }} total</span></div>
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th class="w-12">#</th><th>Name</th><th>Route</th><th>Access name</th><th>Order</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (s of rows(); track s.id; let i = $index) {
              <tr>
                <td class="text-slate-400">{{ (page() - 1) * size + i + 1 }}</td>
                <td><span class="flex items-center gap-2 font-medium"><span class="flex h-8 w-8 items-center justify-center rounded-lg bg-slate-100 text-brand-600 dark:bg-slate-800 dark:text-brand-100"><app-icon [name]="s.icon" size="h-4 w-4" /></span>{{ s.name }}</span></td>
                <td><span class="code">/{{ s.route }}</span></td>
                <td>@if (s.accessName) { <span class="code">{{ s.accessName }}</span> } @else { <span class="text-slate-400">—</span> }</td>
                <td class="text-slate-500">{{ s.order }}</td>
                <td><app-status [value]="s.isActive ? 'Active' : 'Inactive'" /></td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn icon-btn-primary" (click)="perms.set(s)" title="View permissions" aria-label="View permissions"><app-icon name="shield" size="h-4 w-4" /></button>
                  <button class="icon-btn" (click)="edit(s)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-danger" (click)="remove(s)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="7"><app-empty icon="layers" title="No submodules found" hint="Add submodules to define the routes and access points of this module." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit submodule' : 'Add submodule'" [subtitle]="'In ' + (module()?.name ?? '')" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="name" [(ngModel)]="f.name" required placeholder="e.g. New invoice" /></label>
            <label class="block"><span class="label">Route</span><input class="input font-mono" name="route" [(ngModel)]="f.route" required placeholder="receipts/new" /></label>
            <label class="block"><span class="label">Icon</span>
              <span class="flex items-center gap-2"><span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-brand-600 dark:bg-slate-800 dark:text-brand-100"><app-icon [name]="f.icon" /></span>
                <input class="input" name="icon" [(ngModel)]="f.icon" list="sub-icon-names" required /></span>
              <datalist id="sub-icon-names">@for (n of icons; track n) { <option [value]="n"></option> }</datalist></label>
            <label class="block"><span class="label">Access name (permission)</span><input class="input font-mono" name="acc" [(ngModel)]="f.accessName" placeholder="receipts.submit" /></label>
            <label class="block"><span class="label">Order</span><input class="input" type="number" name="ord" [(ngModel)]="f.order" /></label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="act" [(ngModel)]="f.isActive" /> Active</label>
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }

    @if (perms(); as s) {
      <app-permissions-dialog [submoduleName]="s.name" [moduleName]="module()?.name ?? ''" [accessName]="s.accessName" (closed)="perms.set(null)" />
    }
  `,
})
export class ModuleSubmodulesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly route = inject(ActivatedRoute).snapshot.paramMap;
  readonly sectionId = Number(this.route.get('sectionId'));
  readonly moduleId = Number(this.route.get('moduleId'));

  readonly size = 10;
  readonly icons = ICON_NAMES;
  readonly tree = signal<ModuleTree>({ sections: [], modules: [], submodules: [] });
  readonly search = signal('');
  readonly status = signal('');
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<SubmoduleDef | null>(null);
  readonly perms = signal<SubmoduleDef | null>(null);

  readonly section = computed(() => this.tree().sections.find(s => s.id === this.sectionId));
  readonly module = computed(() => this.tree().modules.find(m => m.id === this.moduleId));
  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase(), st = this.status();
    return this.tree().submodules.filter(s => s.moduleId === this.moduleId
      && (!q || s.name.toLowerCase().includes(q) || s.route.toLowerCase().includes(q) || (s.accessName ?? '').toLowerCase().includes(q))
      && (!st || (st === 'active') === s.isActive));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> { try { this.tree.set(await this.api.get<ModuleTree>('/api/v1/modules')); } catch (e) { this.toast.err(e); } }

  startNew(): void { this.form.set({ id: 0, moduleId: this.moduleId, name: '', icon: 'circle', route: '', accessName: '', order: this.filtered().length + 1, isActive: true }); }
  edit(s: SubmoduleDef): void { this.form.set({ ...s, accessName: s.accessName ?? '' }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      await this.api.put('/api/v1/modules/submodules', { ...f, name: f.name.trim(), route: f.route.trim(), icon: f.icon.trim(), accessName: f.accessName?.trim() || null, order: Number(f.order) });
      this.toast.ok('Submodule saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(s: SubmoduleDef): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the submodule "${s.name}"?`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/modules/submodules/${s.id}`); this.toast.ok('Submodule deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
