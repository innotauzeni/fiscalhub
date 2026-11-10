import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, CostCentre } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent } from '../../shared/export-menu.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Form { id?: string; code: string; name: string; kind: string; branchId: string | null; isActive: boolean }
const KINDS: Record<string, string> = { department: 'Department', project: 'Project', branch: 'Branch' };

/** Departments, projects and branches that income and costs are tagged with, so profit can be read for each one. */
@Component({
  selector: 'app-cost-centres',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, ModalComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Cost centres" subtitle="Tag journal lines and bills with a department, project or branch, then read profit for each one.">
      <app-export-menu [build]="exportDoc" [disabled]="!store.centres().length" />
      <a class="btn-ghost" routerLink="/accounting/reports/profit-loss-centres"><app-icon name="chart" size="h-4 w-4" />Profit by cost centre</a>
      @if (canManage) { <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New cost centre</button> }
    </app-page-header>

    <div class="card p-0">
      <table class="table w-full text-sm">
        <thead><tr><th>Code</th><th>Name</th><th>Kind</th><th>Sales from</th><th>Status</th><th></th></tr></thead>
        <tbody>
          @for (c of store.centres(); track c.id) {
            <tr [class.opacity-60]="!c.isActive">
              <td class="font-mono text-xs">{{ c.code }}</td><td class="font-medium">{{ c.name }}</td><td>{{ kinds[c.kind] || c.kind }}</td>
              <td class="text-slate-500">{{ c.branchName ? 'Branch ' + c.branchName + ' (automatic)' : '—' }}</td>
              <td>@if (c.isActive) { <span class="badge badge-ok">In use</span> } @else { <span class="badge badge-warn">Switched off</span> }</td>
              <td class="whitespace-nowrap text-right">@if (canManage) {
                <button class="icon-btn" (click)="edit(c)" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                @if (!c.inUse) { <button class="icon-btn icon-btn-danger" (click)="remove(c)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button> }
              }</td>
            </tr>
          } @empty { <tr><td colspan="6" class="py-8 text-center text-slate-500">No cost centres yet. Add your shops, departments or projects.</td></tr> }
        </tbody>
      </table>
    </div>
    <p class="hint mt-3">A <strong>branch</strong> cost centre receives the sales of that branch's fiscal devices by itself. Departments and projects are chosen on journal lines and bills.</p>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit cost centre' : 'New cost centre'" width="max-w-md" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-3">
            <label class="block"><span class="label">Code</span><input class="input font-mono" name="code" [(ngModel)]="f.code" maxlength="20" placeholder="HRE" required /></label>
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="name" [(ngModel)]="f.name" maxlength="100" placeholder="Harare shop" required /></label>
            <label class="block sm:col-span-3"><span class="label">Kind</span><select class="input" name="kind" [(ngModel)]="f.kind">@for (k of kindKeys; track k) { <option [value]="k">{{ kinds[k] }}</option> }</select></label>
            @if (f.kind === 'branch') {
              <label class="block sm:col-span-3"><span class="label">Branch (its sales come here)</span>
                <select class="input" name="branch" [(ngModel)]="f.branchId"><option [ngValue]="null">Choose…</option>@for (b of branches(); track b.id) { <option [ngValue]="b.id">{{ b.code }} · {{ b.name }}</option> }</select></label>
            }
            <label class="flex items-center gap-2 text-sm sm:col-span-3"><input type="checkbox" class="h-4 w-4" name="active" [(ngModel)]="f.isActive" />In use</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class CostCentresComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly canManage = inject(AuthService).can('accounting.manage');
  readonly kinds = KINDS;
  readonly kindKeys = Object.keys(KINDS);
  readonly branches = signal<{ id: string; code: string; name: string }[]>([]);
  readonly form = signal<Form | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');

  async ngOnInit(): Promise<void> {
    try { await this.store.loadCentres(); } catch (e) { this.toast.err(e); }
    try { this.branches.set(await this.api.get('/api/v1/branches')); } catch { /* without branch access the branch kind just has no choices */ }
  }

  start(): void { this.error.set(''); this.form.set({ code: '', name: '', kind: 'department', branchId: null, isActive: true }); }
  edit(c: CostCentre): void { this.error.set(''); this.form.set({ id: c.id, code: c.code, name: c.name, kind: c.kind, branchId: c.branchId ?? null, isActive: c.isActive }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.put('/api/v1/accounting/cost-centres', { id: f.id ?? null, code: f.code.trim(), name: f.name.trim(), kind: f.kind, branchId: f.kind === 'branch' ? f.branchId : null, isActive: f.isActive }); this.form.set(null); this.toast.ok('Saved.'); await this.store.loadCentres(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async remove(c: CostCentre): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the cost centre ${c.code} ${c.name}?`, 'Delete', true))) return;
    try { await this.api.delete('/api/v1/accounting/cost-centres/' + c.id); this.toast.ok('Deleted.'); await this.store.loadCentres(); } catch (e) { this.toast.err(e); }
  }

  readonly exportDoc = (): ExportDoc => ({
    title: 'Cost centres', columns: [{ header: 'Code' }, { header: 'Name' }, { header: 'Kind' }, { header: 'Branch' }, { header: 'Status' }],
    rows: this.store.centres().map(c => ({ cells: [c.code, c.name, KINDS[c.kind] ?? c.kind, c.branchName ?? '', c.isActive ? 'In use' : 'Switched off'] })),
  });
}
