import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { Permission } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent } from '../../shared/ui.components';

interface BatchRow { action: string; display: string; }

/** Hand-typed batch actions most modules need (same quick chips as dara). */
const COMMON: [string, string][] = [
  ['access', 'View'], ['view', 'View details'], ['create', 'Create'], ['edit', 'Edit'], ['delete', 'Delete'], ['manage', 'Manage'], ['export', 'Export'], ['approve', 'Approve'],
];

const slug = (s: string) => (s || '').trim().toLowerCase().replace(/[^a-z0-9_]+/g, '');

/**
 * The permissions of one submodule, as a popup. Permissions belong to a submodule by name: everything starting with the
 * prefix of its access name ("receipts.view" -> "receipts."), exactly like dara. Single add, edit, delete and bulk add.
 */
@Component({
  selector: 'app-permissions-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PagerComponent, EmptyComponent],
  template: `
    <app-modal [title]="'Permissions — ' + submoduleName()" [subtitle]="prefix() ? 'Everything named ' + prefix() + '.*' : 'This submodule has no access name yet'" width="max-w-4xl" (closed)="closed.emit()">
      @if (!prefix()) {
        <app-empty icon="lock" title="No access name set" hint="Give the submodule an access name such as reports.view. Its prefix (reports) groups its permissions." />
      } @else {
        <div class="mb-4 flex flex-wrap items-end gap-3">
          <label class="block min-w-52 flex-1"><span class="label">Search</span>
            <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
              <input class="input pl-9" placeholder="Search permissions…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
          <button class="btn-ghost" (click)="openBatch()"><app-icon name="stack" size="h-4 w-4" />Batch add</button>
          <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />Add permission</button>
        </div>

        <div class="mb-2 flex items-center gap-2 text-sm text-slate-500"><app-icon name="shield" size="h-4 w-4" /> Permissions <span class="badge">{{ filtered().length }} total</span></div>
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th class="w-10">#</th><th>Permission name</th><th>Display name</th><th>Module</th><th class="text-right">Actions</th></tr></thead>
            <tbody>
              @for (p of rows(); track p.id; let i = $index) {
                <tr>
                  <td class="text-slate-400">{{ (page() - 1) * size + i + 1 }}</td>
                  <td><span class="code">{{ p.name }}</span></td>
                  <td class="font-medium">{{ p.displayName }}</td>
                  <td class="text-slate-500">{{ p.module }}</td>
                  <td><div class="flex justify-end gap-1.5">
                    <button class="icon-btn" (click)="edit(p)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                    <button class="icon-btn icon-btn-danger" (click)="remove(p)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                  </div></td>
                </tr>
              } @empty { <tr><td colspan="5"><app-empty icon="shield" title="No permissions yet" hint="Add one, or use Batch add to create view / create / edit / delete in one go." /></td></tr> }
            </tbody>
          </table>
        </div>
        <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
      }
    </app-modal>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit permission' : 'Add permission'" width="max-w-lg" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="space-y-4">
            <label class="block"><span class="label">Permission name</span><input class="input font-mono" name="name" [(ngModel)]="f.name" required placeholder="{{ prefix() }}.create" />
              <span class="hint">Lowercase words joined by dots (module.action).</span></label>
            <label class="block"><span class="label">Display name</span><input class="input" name="dn" [(ngModel)]="f.displayName" required placeholder="Create records" /></label>
            <label class="block"><span class="label">Module (group name)</span><input class="input" name="mod" [(ngModel)]="f.module" required /></label>
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">{{ f.id ? 'Update' : 'Add' }}</button></div>
        </form>
      </app-modal>
    }

    @if (batch(); as b) {
      <app-modal title="Batch add permissions" subtitle="Set the module and prefix once, then add one row per action." width="max-w-3xl" (closed)="batch.set(null)">
        <div class="grid gap-4 sm:grid-cols-2">
          <label class="block"><span class="label">Module</span><input class="input" [(ngModel)]="b.module" name="bm" /></label>
          <label class="block"><span class="label">Key prefix</span><input class="input font-mono" [(ngModel)]="b.prefix" name="bp" /><span class="hint">Keys become <code class="code">{{ slug(b.prefix) || 'prefix' }}</code>.action</span></label>
        </div>

        <div class="mb-3 mt-4 flex flex-wrap items-center gap-2">
          <span class="text-xs text-slate-500">Quick add:</span>
          @for (c of common; track c[0]) {
            <button type="button" class="rounded-full border border-slate-200 bg-slate-50 px-3 py-1 text-xs text-slate-600 transition hover:border-indigo-200 hover:bg-indigo-50 hover:text-indigo-700 disabled:opacity-40 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300"
                    [disabled]="used(c[0])" (click)="addRow(c[0], c[1])">+ {{ c[0] }}</button>
          }
        </div>

        <div class="space-y-2">
          <div class="flex gap-2 px-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400"><span class="w-40">Action</span><span class="flex-1">Display name</span><span class="hidden w-44 sm:block">Resulting key</span><span class="w-9"></span></div>
          @for (r of b.rows; track $index; let i = $index) {
            <div class="flex items-center gap-2">
              <input class="input w-40" [name]="'a' + i" [(ngModel)]="r.action" placeholder="action" />
              <input class="input flex-1" [name]="'d' + i" [(ngModel)]="r.display" placeholder="e.g. Create records" />
              <span class="hidden w-44 truncate font-mono text-xs sm:block" [class.text-indigo-600]="key(b, r)" [class.text-slate-300]="!key(b, r)">{{ key(b, r) || '—' }}</span>
              <button type="button" class="icon-btn icon-btn-danger" (click)="removeRow(i)" aria-label="Remove row"><app-icon name="x" size="h-4 w-4" /></button>
            </div>
          }
        </div>
        <button type="button" class="btn-ghost btn-sm mt-3" (click)="addRow()"><app-icon name="plus" size="h-4 w-4" />Add another row</button>

        <div class="mt-6 flex items-center justify-between border-t border-slate-100 pt-4 dark:border-slate-800">
          <span class="text-sm text-slate-500">{{ ready(b) ? ready(b) + ' permission(s) ready' : '' }}</span>
          <span class="flex gap-2"><button class="btn-ghost" type="button" (click)="batch.set(null)">Cancel</button><button class="btn" type="button" [disabled]="busy() || !ready(b)" (click)="saveBatch()">Create all</button></span>
        </div>
      </app-modal>
    }
  `,
})
export class PermissionsDialogComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly submoduleName = input.required<string>();
  readonly moduleName = input.required<string>();
  readonly accessName = input<string | null | undefined>(null);
  readonly closed = output<void>();

  readonly size = 8;
  readonly common = COMMON;
  readonly slug = slug;
  readonly all = signal<Permission[]>([]);
  readonly search = signal('');
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<Permission | null>(null);
  readonly batch = signal<{ module: string; prefix: string; rows: BatchRow[] } | null>(null);

  readonly prefix = computed(() => slug((this.accessName() ?? '').split('.')[0]));
  readonly filtered = computed(() => {
    const p = this.prefix() + '.', q = this.search().trim().toLowerCase();
    return this.all().filter(x => x.name.toLowerCase().startsWith(p) && (!q || x.name.toLowerCase().includes(q) || x.displayName.toLowerCase().includes(q)));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> { try { this.all.set(await this.api.get<Permission[]>('/api/v1/modules/permissions')); } catch (e) { this.toast.err(e); } }

  startNew(): void { this.form.set({ id: 0, name: this.prefix() + '.', displayName: '', module: this.moduleName() }); }
  edit(p: Permission): void { this.form.set({ ...p }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try { await this.api.put('/api/v1/modules/permissions', { id: f.id, name: f.name.trim(), displayName: f.displayName.trim(), module: f.module.trim() }); this.toast.ok('Permission saved.'); this.form.set(null); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(p: Permission): Promise<void> {
    if (!(await this.dialog.confirm(`Delete ${p.name}? Every role loses it.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/modules/permissions/${p.id}`); this.toast.ok('Permission deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  // ---- batch add ----
  openBatch(): void { this.batch.set({ module: this.moduleName(), prefix: this.prefix(), rows: [{ action: '', display: '' }, { action: '', display: '' }] }); }
  addRow(action = '', display = ''): void {
    this.batch.update(b => {
      if (!b) return b;
      const blank = b.rows.findIndex(r => !r.action.trim());
      const rows = [...b.rows];
      if (action && blank >= 0) rows[blank] = { action, display }; else rows.push({ action, display });
      return { ...b, rows };
    });
  }
  removeRow(i: number): void { this.batch.update(b => (b ? { ...b, rows: b.rows.filter((_, k) => k !== i) } : b)); }
  used(action: string): boolean { return !!this.batch()?.rows.some(r => r.action.trim().toLowerCase() === action); }

  key(b: { prefix: string }, r: BatchRow): string {
    const a = r.action.trim().toLowerCase().replace(/[^a-z0-9._]/g, '');
    return !a ? '' : a.includes('.') ? a : slug(b.prefix) ? `${slug(b.prefix)}.${a}` : a;
  }
  ready(b: { prefix: string; rows: BatchRow[] }): number { return b.rows.filter(r => this.key(b, r)).length; }

  async saveBatch(): Promise<void> {
    const b = this.batch(); if (!b) return;
    this.busy.set(true);
    try {
      const r = await this.api.post<{ created: number; skipped: string[] }>('/api/v1/modules/permissions/batch', {
        module: b.module.trim(), prefix: slug(b.prefix), items: b.rows.filter(x => x.action.trim()).map(x => ({ action: x.action.trim(), displayName: x.display.trim() || null })),
      });
      this.toast.ok(`${r.created} permission(s) created${r.skipped.length ? `, ${r.skipped.length} already existed` : ''}.`);
      this.batch.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
