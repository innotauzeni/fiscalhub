import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { ModuleDef, ModuleTree } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { CrumbsComponent, EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { ICON_NAMES, IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';

/** Level 2: the modules (sidebar entries) of one section. "View" drills into the module's submodules. */
@Component({
  selector: 'app-section-modules',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent, CrumbsComponent],
  template: `
    <app-crumbs [items]="[{ label: 'Menus & modules', link: ['/admin/modules'] }, { label: section()?.name ?? '…' }]" />
    <app-page-header [title]="(section()?.name ?? 'Section') + ' — modules'" subtitle="The entries of this sidebar section. Access name is the permission a user needs to see the entry.">
      <a class="btn-ghost" routerLink="/admin/modules"><app-icon name="arrow-left" size="h-4 w-4" />Back</a>
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />Add module</button>
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
      <div class="mb-3 flex items-center gap-3"><div class="card-title mb-0 flex items-center gap-2"><app-icon name="squares" size="h-4 w-4" />Modules</div><span class="badge">{{ filtered().length }} total</span></div>
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th class="w-12">#</th><th>Name</th><th>Route</th><th>Access name</th><th>Submodules</th><th>Order</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (m of rows(); track m.id; let i = $index) {
              <tr>
                <td class="text-slate-400">{{ (page() - 1) * size + i + 1 }}</td>
                <td><span class="flex items-center gap-2 font-medium"><span class="flex h-8 w-8 items-center justify-center rounded-lg bg-slate-100 text-brand-600 dark:bg-slate-800 dark:text-brand-100"><app-icon [name]="m.icon" size="h-4 w-4" /></span>{{ m.name }}</span></td>
                <td><span class="code">/{{ m.slugname }}</span></td>
                <td>@if (m.accessName) { <span class="code">{{ m.accessName }}</span> } @else { <span class="text-slate-400">—</span> }</td>
                <td>{{ subCount(m.id) }}</td>
                <td class="text-slate-500">{{ m.sortOrder }}</td>
                <td><app-status [value]="m.isActive ? 'Active' : 'Inactive'" /></td>
                <td><div class="flex justify-end gap-1.5">
                  <a class="icon-btn icon-btn-primary" [routerLink]="['/admin/modules', sectionId, m.id]" title="View submodules" aria-label="View submodules"><app-icon name="eye" size="h-4 w-4" /></a>
                  <button class="icon-btn" (click)="edit(m)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-danger" (click)="remove(m)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="8"><app-empty icon="squares" title="No modules found" hint="Add the first module to this section." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit module' : 'Add module'" [subtitle]="'In section ' + (section()?.name ?? '')" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Name</span><input class="input" name="name" [(ngModel)]="f.name" required placeholder="e.g. Receipts" /></label>
            <label class="block"><span class="label">Route (slug)</span><input class="input font-mono" name="slug" [(ngModel)]="f.slugname" required placeholder="receipts" /></label>
            <label class="block"><span class="label">Icon</span>
              <span class="flex items-center gap-2"><span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-brand-600 dark:bg-slate-800 dark:text-brand-100"><app-icon [name]="f.icon" /></span>
                <input class="input" name="icon" [(ngModel)]="f.icon" list="icon-names" required /></span>
              <datalist id="icon-names">@for (n of icons; track n) { <option [value]="n"></option> }</datalist></label>
            <label class="block"><span class="label">Access name (permission)</span><input class="input font-mono" name="acc" [(ngModel)]="f.accessName" placeholder="receipts.view" /></label>
            <label class="block sm:col-span-2"><span class="label">Description</span><input class="input" name="desc" [(ngModel)]="f.description" /></label>
            <label class="block"><span class="label">Order</span><input class="input" type="number" name="ord" [(ngModel)]="f.sortOrder" /></label>
            <label class="flex items-end gap-2 pb-2 text-sm"><input type="checkbox" class="h-4 w-4" name="act" [(ngModel)]="f.isActive" /> Active</label>
          </div>
          <p class="hint">Which role sees the entry is set on Roles & access → Menu access.</p>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class SectionModulesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly sectionId = Number(inject(ActivatedRoute).snapshot.paramMap.get('sectionId'));

  readonly size = 10;
  readonly icons = ICON_NAMES;
  readonly tree = signal<ModuleTree>({ sections: [], modules: [], submodules: [] });
  readonly search = signal('');
  readonly status = signal('');
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<ModuleDef | null>(null);

  readonly section = computed(() => this.tree().sections.find(s => s.id === this.sectionId));
  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase(), st = this.status();
    return this.tree().modules.filter(m => m.sectionModuleId === this.sectionId
      && (!q || m.name.toLowerCase().includes(q) || m.slugname.toLowerCase().includes(q) || (m.accessName ?? '').toLowerCase().includes(q))
      && (!st || (st === 'active') === m.isActive));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  subCount(id: number): number { return this.tree().submodules.filter(s => s.moduleId === id).length; }

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> { try { this.tree.set(await this.api.get<ModuleTree>('/api/v1/modules')); } catch (e) { this.toast.err(e); } }

  startNew(): void {
    this.form.set({ id: 0, sectionModuleId: this.sectionId, name: '', slugname: '', icon: 'squares', description: '', accessName: '', sortOrder: this.filtered().length + 1, isActive: true });
  }
  edit(m: ModuleDef): void { this.form.set({ ...m, description: m.description ?? '', accessName: m.accessName ?? '' }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      await this.api.put('/api/v1/modules/modules', { ...f, name: f.name.trim(), slugname: f.slugname.trim(), icon: f.icon.trim(), description: f.description?.trim() || null, accessName: f.accessName?.trim() || null, sortOrder: Number(f.sortOrder) });
      this.toast.ok('Module saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(m: ModuleDef): Promise<void> {
    if (!(await this.dialog.confirm(`Delete "${m.name}"? Its submodules are deleted too and it disappears from every role.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/modules/modules/${m.id}`); this.toast.ok('Module deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
