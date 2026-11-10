import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { ModuleTree, Section } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';

/** Level 1 of the menu tree: sections (the coloured headings in the sidebar). "View" drills into the section's modules. */
@Component({
  selector: 'app-sections',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Menus & modules" subtitle="Sections group the sidebar. Open a section to manage its modules, then each module's submodules and permissions.">
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />New section</button>
    </app-page-header>

    <div class="card mb-5">
      <div class="flex flex-wrap items-end gap-3">
        <label class="block min-w-56 flex-1"><span class="label">Search</span>
          <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
            <input class="input pl-9" placeholder="Search sections…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
        <label class="block"><span class="label">Status</span>
          <select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); page.set(1)"><option value="">All</option><option value="active">Active</option><option value="inactive">Inactive</option></select></label>
        <button class="btn-ghost" (click)="search.set(''); status.set(''); page.set(1)">Clear</button>
      </div>
    </div>

    <div class="card">
      <div class="mb-3 flex items-center gap-3"><div class="card-title mb-0 flex items-center gap-2"><app-icon name="layers" size="h-4 w-4" />Sections</div><span class="badge">{{ filtered().length }} total</span>
        <span class="flex-1"></span><button class="icon-btn" (click)="load()" aria-label="Refresh"><app-icon name="refresh" size="h-4 w-4" /></button></div>
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th class="w-12">#</th><th>Name</th><th>Colour</th><th>Modules</th><th>Order</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (s of rows(); track s.id; let i = $index) {
              <tr>
                <td class="text-slate-400">{{ (page() - 1) * size + i + 1 }}</td>
                <td><span class="flex items-center gap-2 font-medium"><span class="h-3 w-3 rounded-full" [style.background]="s.color"></span>{{ s.name }}</span></td>
                <td><span class="code">{{ s.color }}</span></td>
                <td>{{ moduleCount(s.id) }}</td>
                <td class="text-slate-500">{{ s.sortOrder }}</td>
                <td><app-status [value]="s.status ? 'Active' : 'Inactive'" /></td>
                <td><div class="flex justify-end gap-1.5">
                  <a class="icon-btn icon-btn-primary" [routerLink]="['/admin/modules', s.id]" title="View modules" aria-label="View modules"><app-icon name="eye" size="h-4 w-4" /></a>
                  <button class="icon-btn" (click)="edit(s)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-danger" (click)="remove(s)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="7"><app-empty icon="layers" title="No sections found" hint="Create one, or clear the filters." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit section' : 'New section'" subtitle="A coloured heading in the sidebar." width="max-w-lg" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="name" [(ngModel)]="f.name" required placeholder="e.g. Fiscalisation" /></label>
            <label class="block"><span class="label">Colour</span>
              <span class="flex gap-2"><input type="color" class="h-10 w-14 cursor-pointer rounded-lg border border-slate-300 bg-transparent p-1 dark:border-slate-700" name="colorpick" [(ngModel)]="f.color" /><input class="input font-mono" name="color" [(ngModel)]="f.color" maxlength="7" /></span></label>
            <label class="block"><span class="label">Order</span><input class="input" type="number" name="order" [(ngModel)]="f.sortOrder" /></label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="status" [(ngModel)]="f.status" /> Active (shown in the sidebar)</label>
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class SectionsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly tree = signal<ModuleTree>({ sections: [], modules: [], submodules: [] });
  readonly search = signal('');
  readonly status = signal('');
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<Section | null>(null);

  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase(), st = this.status();
    return this.tree().sections.filter(s => (!q || s.name.toLowerCase().includes(q)) && (!st || (st === 'active') === s.status));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  moduleCount(id: number): number { return this.tree().modules.filter(m => m.sectionModuleId === id).length; }

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> { try { this.tree.set(await this.api.get<ModuleTree>('/api/v1/modules')); } catch (e) { this.toast.err(e); } }

  startNew(): void { this.form.set({ id: 0, name: '', color: '#2563eb', sortOrder: this.tree().sections.length + 1, status: true }); }
  edit(s: Section): void { this.form.set({ ...s }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try { await this.api.put('/api/v1/modules/sections', { ...f, name: f.name.trim(), sortOrder: Number(f.sortOrder) }); this.toast.ok('Section saved.'); this.form.set(null); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(s: Section): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the section "${s.name}"? It must be empty.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/modules/sections/${s.id}`); this.toast.ok('Section deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
