import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { Role } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface RoleForm { id?: string; name: string; level: string; description: string; system: boolean; }

@Component({
  selector: 'app-roles',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, ModalComponent, PageHeaderComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Roles & access" subtitle="A role decides which menus people see and which actions they may perform. Use Assign to set both.">
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />New role</button>
    </app-page-header>

    <div class="card mb-5">
      <div class="flex flex-wrap items-end gap-3">
        <label class="block min-w-56 flex-1"><span class="label">Search</span>
          <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
            <input class="input pl-9" placeholder="Search roles…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
        <label class="block"><span class="label">Type</span>
          <select class="input" [ngModel]="type()" (ngModelChange)="type.set($event); page.set(1)"><option value="">All</option><option value="system">Built-in</option><option value="custom">Custom</option></select></label>
        <label class="block"><span class="label">Level</span>
          <select class="input" [ngModel]="level()" (ngModelChange)="level.set($event); page.set(1)"><option value="">All</option><option value="platform">Platform</option><option value="client">Client</option></select></label>
        <button class="btn-ghost" (click)="search.set(''); type.set(''); level.set(''); page.set(1)">Clear</button>
      </div>
    </div>

    <div class="card">
      <div class="mb-3 flex items-center gap-3"><div class="card-title mb-0 flex items-center gap-2"><app-icon name="shield" size="h-4 w-4" />Roles</div><span class="badge">{{ filtered().length }} total</span></div>
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th class="w-12">#</th><th>Role</th><th>Type</th><th>Level</th><th>Users</th><th>Permissions</th><th>Menus</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (r of rows(); track r.id; let i = $index) {
              <tr>
                <td class="text-slate-400">{{ (page() - 1) * size + i + 1 }}</td>
                <td><div class="font-medium">{{ r.name }}</div>@if (r.description) { <div class="hint mt-0 max-w-xs truncate">{{ r.description }}</div> }</td>
                <td><span class="badge" [class.badge-info]="r.type === 'system'">{{ r.type === 'system' ? 'Built-in' : 'Custom' }}</span></td>
                <td class="capitalize">{{ r.level }}</td>
                <td>{{ r.userCount }}</td>
                <td><span class="badge badge-ok">{{ r.permissionCount }}</span></td>
                <td><span class="badge badge-info">{{ r.menuCount }}</span></td>
                <td><div class="flex justify-end gap-1.5">
                  <a class="icon-btn icon-btn-primary" [routerLink]="['/admin/roles', r.id]" title="Assign permissions & menus" aria-label="Assign permissions and menus"><app-icon name="shield" size="h-4 w-4" /></a>
                  <button class="icon-btn" (click)="edit(r)" title="Edit role" aria-label="Edit role"><app-icon name="edit" size="h-4 w-4" /></button>
                  @if (r.type !== 'system') { <button class="icon-btn icon-btn-danger" (click)="remove(r)" title="Delete role" aria-label="Delete role"><app-icon name="trash" size="h-4 w-4" /></button> }
                </div></td>
              </tr>
            } @empty { <tr><td colspan="8"><app-empty icon="shield" title="No roles found" hint="Create a custom role or clear the filters." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit role' : 'New role'" [subtitle]="f.id ? '' : 'Create it, then use Assign to tick its permissions and menus.'" width="max-w-xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Role name</span><input class="input" name="n" [(ngModel)]="f.name" [disabled]="f.system" required /></label>
            <label class="block"><span class="label">Level</span>
              <select class="input" name="l" [(ngModel)]="f.level" [disabled]="f.system"><option value="client">Client (a company's staff)</option><option value="platform">Platform (operator)</option></select></label>
            <label class="block sm:col-span-2"><span class="label">Description</span><input class="input" name="d" [(ngModel)]="f.description" /></label>
          </div>
          @if (f.system) { <p class="hint">Built-in roles keep their name and level; only the description can change.</p> }
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">{{ f.id ? 'Save changes' : 'Create role' }}</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class RolesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly roles = signal<Role[]>([]);
  readonly search = signal('');
  readonly type = signal('');
  readonly level = signal('');
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<RoleForm | null>(null);

  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase();
    return this.roles().filter(r => (!q || r.name.toLowerCase().includes(q) || (r.description ?? '').toLowerCase().includes(q))
      && (!this.type() || r.type === this.type()) && (!this.level() || r.level === this.level()));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> { try { this.roles.set(await this.api.get<Role[]>('/api/v1/roles')); } catch (e) { this.toast.err(e); } }

  startNew(): void { this.form.set({ name: '', level: 'client', description: '', system: false }); }
  edit(r: Role): void { this.form.set({ id: r.id, name: r.name, level: r.level ?? 'client', description: r.description ?? '', system: r.type === 'system' }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      const body = { name: f.name.trim(), level: f.level, description: f.description.trim() || null };
      if (f.id) await this.api.put(`/api/v1/roles/${f.id}`, body); else await this.api.post('/api/v1/roles', body);
      this.toast.ok(f.id ? 'Role updated.' : 'Role created. Use Assign to give it access.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(r: Role): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the role "${r.name}"?`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/roles/${r.id}`); this.toast.ok('Role deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
