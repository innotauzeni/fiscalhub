import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ModuleDef, ModuleTree, Permission, RoleAccess } from '../../core/models';
import { ToastService } from '../../core/ui.services';
import { CrumbsComponent, EmptyComponent } from '../../shared/list.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

/** Dara's "Role configuration": one page, two tabs - which permissions the role has and which menu entries it sees. */
@Component({
  selector: 'app-role-assign',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, PageHeaderComponent, CrumbsComponent, EmptyComponent],
  template: `
    <app-crumbs [items]="[{ label: 'Roles & access', link: ['/admin/roles'] }, { label: access()?.roleName ?? '…' }]" />
    <app-page-header [title]="'Role configuration — ' + (access()?.roleName ?? '')" subtitle="Tick what this role may do and which menus it sees, then save.">
      <a class="btn-ghost" routerLink="/admin/roles"><app-icon name="arrow-left" size="h-4 w-4" />Back</a>
      <button class="btn" (click)="save()" [disabled]="busy() || locked() || !dirty()"><app-icon name="check" size="h-4 w-4" />Save all changes</button>
    </app-page-header>

    @if (locked()) { <div class="mb-4 flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-200"><app-icon name="lock" size="h-4 w-4" />SuperAdmin is the platform operator: it keeps the access it needs to administer the gateway, so it cannot be changed here.</div> }

    <div class="card p-0">
      <div class="flex border-b border-slate-200 px-2 dark:border-slate-800">
        <button type="button" class="tab" [class.tab-active]="tab() === 'permissions'" (click)="tab.set('permissions')"><app-icon name="shield" size="h-4 w-4" />Permissions <span class="badge badge-ok">{{ perms().size }}</span></button>
        <button type="button" class="tab" [class.tab-active]="tab() === 'menus'" (click)="tab.set('menus')"><app-icon name="squares" size="h-4 w-4" />Menu access <span class="badge badge-info">{{ menus().size }}</span></button>
      </div>

      <div class="p-5">
        <div class="mb-4 flex flex-wrap items-end gap-3">
          <label class="block min-w-56 flex-1"><span class="label">Search</span>
            <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
              <input class="input pl-9" [placeholder]="tab() === 'permissions' ? 'Search permissions…' : 'Search menu entries…'" [ngModel]="search()" (ngModelChange)="search.set($event)" /></span></label>
          <button class="btn-ghost" [disabled]="locked()" (click)="all(true)"><app-icon name="check" size="h-4 w-4" />Select all</button>
          <button class="btn-ghost" [disabled]="locked()" (click)="all(false)"><app-icon name="x" size="h-4 w-4" />Deselect all</button>
        </div>

        @if (tab() === 'permissions') {
          <div class="space-y-4">
            @for (g of permissionGroups(); track g.module) {
              <div class="overflow-hidden rounded-xl border border-slate-200 dark:border-slate-800">
                <div class="flex items-center justify-between gap-3 bg-slate-50 px-4 py-3 dark:bg-slate-800/50">
                  <button type="button" class="flex items-center gap-3 text-left" (click)="toggleOpen(g.module)">
                    <span class="flex h-9 w-9 items-center justify-center rounded-lg bg-brand-50 text-brand-600 dark:bg-brand-500/10 dark:text-brand-100"><app-icon name="folder" /></span>
                    <span><span class="block font-semibold">{{ g.module }}</span><span class="text-xs text-slate-500">{{ selectedIn(g.items) }} / {{ g.items.length }} selected</span></span>
                  </button>
                  <span class="flex items-center gap-3">
                    <label class="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300"><input type="checkbox" class="h-4 w-4" [disabled]="locked()" [checked]="selectedIn(g.items) === g.items.length" [indeterminate]="selectedIn(g.items) > 0 && selectedIn(g.items) < g.items.length" (change)="group(g.items, $any($event.target).checked)" />Select all</label>
                    <button type="button" class="icon-btn" (click)="toggleOpen(g.module)" [attr.aria-label]="'Toggle ' + g.module"><app-icon name="chevron" size="h-4 w-4" /></button>
                  </span>
                </div>
                @if (!collapsed().has(g.module)) {
                  <div class="grid gap-2 p-3 sm:grid-cols-2 xl:grid-cols-3">
                    @for (p of g.items; track p.id) {
                      <label class="flex cursor-pointer items-start gap-3 rounded-lg border p-3 transition"
                             [class]="perms().has(p.id) ? 'border-brand-500/50 bg-brand-50 dark:border-brand-500/40 dark:bg-brand-500/10' : 'border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/40'">
                        <input type="checkbox" class="mt-1 h-4 w-4" [disabled]="locked()" [checked]="perms().has(p.id)" (change)="flip(perms, p.id)" />
                        <span class="min-w-0"><span class="block text-sm font-medium">{{ p.displayName }}</span><span class="block truncate font-mono text-xs text-slate-500">{{ p.name }}</span></span>
                      </label>
                    }
                  </div>
                }
              </div>
            } @empty { <app-empty icon="shield" title="No permissions match" /> }
          </div>
        } @else {
          <div class="space-y-5">
            @for (s of menuGroups(); track s.id) {
              <div>
                <div class="mb-2 flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-slate-400"><span class="h-2.5 w-2.5 rounded-full" [style.background]="s.color"></span>{{ s.name }}
                  <span class="flex-1"></span>
                  <button type="button" class="text-xs font-medium normal-case text-brand-600 hover:underline disabled:opacity-40" [disabled]="locked()" (click)="group(s.items, !allMenus(s.items))">{{ allMenus(s.items) ? 'Deselect section' : 'Select section' }}</button></div>
                <div class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                  @for (m of s.items; track m.id) {
                    <label class="relative flex cursor-pointer items-start gap-3 rounded-xl border p-4 transition"
                           [class]="menus().has(m.id) ? 'border-brand-500/50 bg-brand-50 dark:border-brand-500/40 dark:bg-brand-500/10' : 'border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/40'">
                      <input type="checkbox" class="sr-only" [disabled]="locked()" [checked]="menus().has(m.id)" (change)="flip(menus, m.id)" />
                      <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-white text-brand-600 shadow-sm dark:bg-slate-800 dark:text-brand-100"><app-icon [name]="m.icon" /></span>
                      <span class="min-w-0 flex-1">
                        <span class="block font-medium">{{ m.name }}</span>
                        <span class="block truncate text-xs text-slate-500">{{ m.description || '/' + m.slugname }}</span>
                        <span class="mt-1 block text-xs text-slate-400">order {{ m.sortOrder }}@if (m.accessName) { · needs <span class="font-mono">{{ m.accessName }}</span> }</span>
                      </span>
                      @if (menus().has(m.id)) { <span class="text-brand-600"><app-icon name="check" size="h-5 w-5" /></span> }
                    </label>
                  }
                </div>
              </div>
            } @empty { <app-empty icon="squares" title="No menu entries match" /> }
          </div>
          <p class="hint mt-4">A menu entry also needs its permission: the menu shows the page, the permission lets it work.</p>
        }
      </div>
    </div>
  `,
})
export class RoleAssignComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);
  readonly roleId = inject(ActivatedRoute).snapshot.paramMap.get('id')!;

  readonly access = signal<RoleAccess | null>(null);
  readonly permissions = signal<Permission[]>([]);
  readonly tree = signal<ModuleTree>({ sections: [], modules: [], submodules: [] });
  readonly perms = signal<Set<number>>(new Set());
  readonly menus = signal<Set<number>>(new Set());
  readonly tab = signal<'permissions' | 'menus'>('permissions');
  readonly search = signal('');
  readonly collapsed = signal<Set<string>>(new Set());
  readonly busy = signal(false);
  private readonly saved = signal({ p: '', m: '' });

  readonly locked = computed(() => this.access()?.roleName === 'SuperAdmin');
  readonly dirty = computed(() => this.key(this.perms()) !== this.saved().p || this.key(this.menus()) !== this.saved().m);

  readonly permissionGroups = computed(() => {
    const q = this.search().trim().toLowerCase(), by = new Map<string, Permission[]>();
    for (const p of this.permissions()) if (!q || p.name.toLowerCase().includes(q) || p.displayName.toLowerCase().includes(q) || p.module.toLowerCase().includes(q)) by.set(p.module, [...(by.get(p.module) ?? []), p]);
    return [...by].map(([module, items]) => ({ module, items }));
  });

  readonly menuGroups = computed(() => {
    const q = this.search().trim().toLowerCase();
    return this.tree().sections.map(s => ({ ...s, items: this.tree().modules.filter(m => m.sectionModuleId === s.id && m.isActive && (!q || m.name.toLowerCase().includes(q))) })).filter(g => g.items.length);
  });

  private key(s: Set<number>): string { return [...s].sort((a, b) => a - b).join(','); }

  async ngOnInit(): Promise<void> {
    try {
      const [a, perms, tree] = await Promise.all([this.api.get<RoleAccess>(`/api/v1/roles/${this.roleId}/access`), this.api.get<Permission[]>('/api/v1/roles/permissions'), this.api.get<ModuleTree>('/api/v1/modules')]);
      this.access.set(a); this.permissions.set(perms); this.tree.set(tree);
      this.perms.set(new Set(a.permissionIds)); this.menus.set(new Set(a.moduleIds));
      this.saved.set({ p: this.key(this.perms()), m: this.key(this.menus()) });
    } catch (e) { this.toast.err(e); }
  }

  selectedIn(items: Permission[]): number { return items.filter(p => this.perms().has(p.id)).length; }
  allMenus(items: ModuleDef[]): boolean { return items.every(m => this.menus().has(m.id)); }
  toggleOpen(module: string): void { this.collapsed.update(s => { const n = new Set(s); if (n.has(module)) n.delete(module); else n.add(module); return n; }); }

  flip(sig: { update: (f: (s: Set<number>) => Set<number>) => void }, id: number): void {
    sig.update(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  }

  /** Select or clear a whole group (permission module or menu section). */
  group(items: { id: number }[], on: boolean): void {
    const target = this.tab() === 'permissions' ? this.perms : this.menus;
    target.update(s => { const n = new Set(s); for (const i of items) { if (on) n.add(i.id); else n.delete(i.id); } return n; });
  }

  /** Select/deselect everything visible on the current tab (respects the search box). */
  all(on: boolean): void {
    this.group(this.tab() === 'permissions' ? this.permissionGroups().flatMap(g => g.items) : this.menuGroups().flatMap(g => g.items), on);
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.put(`/api/v1/roles/${this.roleId}/permissions`, { ids: [...this.perms()] });
      await this.api.put(`/api/v1/roles/${this.roleId}/menus`, { ids: [...this.menus()] });
      this.saved.set({ p: this.key(this.perms()), m: this.key(this.menus()) });
      this.toast.ok('Access saved. People with this role see it the next time they sign in or refresh.');
      void this.auth.refresh();   // picks up a change to the signed-in user's own role
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
