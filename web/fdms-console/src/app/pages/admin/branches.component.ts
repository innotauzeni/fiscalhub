import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { Branch, Device } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';

interface BranchForm { id?: string; code: string; name: string; address: string; phone: string; isActive: boolean; }

/** A company's shops / depots. Devices (tills) are assigned to a branch here; people are limited to branches under Users. */
@Component({
  selector: 'app-branches',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Branches" subtitle="Group your devices (tills) by shop or depot, then limit people to the branches they work in.">
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />New branch</button>
    </app-page-header>

    <div class="mb-5 grid gap-4 sm:grid-cols-3">
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Branches</div><div class="text-2xl font-semibold">{{ branches().length }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Devices in a branch</div><div class="text-2xl font-semibold text-emerald-600">{{ devices().length - unassigned() }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Devices without a branch</div><div class="text-2xl font-semibold" [class.text-amber-600]="unassigned() > 0">{{ unassigned() }}</div></div>
    </div>

    <div class="card mb-5">
      <label class="block"><span class="label">Search</span>
        <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
          <input class="input pl-9" placeholder="Search by name, code or address…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
    </div>

    <div class="card">
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Code</th><th>Branch</th><th>Devices</th><th>Limited users</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (b of rows(); track b.id) {
              <tr>
                <td><span class="code">{{ b.code }}</span></td>
                <td><div class="font-medium">{{ b.name }}</div><div class="hint mt-0">{{ b.address }}{{ b.address && b.phone ? ' · ' : '' }}{{ b.phone }}</div></td>
                <td><span class="badge badge-info">{{ b.deviceIds.length }}</span></td>
                <td>{{ b.userCount }}</td>
                <td><app-status [value]="b.isActive ? 'ACTIVE' : 'INACTIVE'" /></td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn icon-btn-primary" (click)="startDevices(b)" title="Assign devices" aria-label="Assign devices"><app-icon name="cpu" size="h-4 w-4" /></button>
                  <button class="icon-btn" (click)="edit(b)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-danger" (click)="remove(b)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="6"><app-empty icon="building" title="No branches yet" hint="Create a branch for each shop or depot, then assign its devices." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit branch' : 'New branch'" width="max-w-lg" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-3">
            <label class="block"><span class="label">Code</span><input class="input font-mono uppercase" name="code" [(ngModel)]="f.code" required maxlength="20" placeholder="HRE-01" /></label>
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="name" [(ngModel)]="f.name" required placeholder="Harare CBD" /></label>
            <label class="block sm:col-span-2"><span class="label">Address</span><input class="input" name="addr" [(ngModel)]="f.address" /></label>
            <label class="block"><span class="label">Phone</span><input class="input" name="ph" [(ngModel)]="f.phone" /></label>
            <label class="flex items-center gap-2 text-sm sm:col-span-3"><input type="checkbox" class="h-4 w-4" name="act" [(ngModel)]="f.isActive" /> Active</label>
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }

    @if (assign(); as b) {
      <app-modal [title]="'Devices of ' + b.name" subtitle="Tick the devices that belong to this branch. A device can be in one branch; ticking one moves it here." width="max-w-xl" (closed)="assign.set(null)">
        <div class="max-h-80 space-y-1 overflow-y-auto rounded-xl border border-slate-200 p-2 dark:border-slate-800">
          @for (d of devices(); track d.id) {
            <label class="flex cursor-pointer items-center gap-3 rounded-lg px-3 py-2 hover:bg-slate-50 dark:hover:bg-slate-800/50">
              <input type="checkbox" class="h-4 w-4" [checked]="picked().has(d.id)" (change)="flip(d.id)" />
              <span class="flex-1"><span class="font-medium">Device {{ d.zimraDeviceId }}</span><span class="block text-xs text-slate-500">{{ d.taxPayerName }} · {{ d.serialNo }}</span></span>
              @if (d.assignedBranch && d.branchId !== b.id) { <span class="badge badge-warn">now in {{ d.assignedBranch }}</span> }
            </label>
          } @empty { <p class="p-3 text-sm text-slate-500">No devices registered yet.</p> }
        </div>
        <div class="mt-6 flex items-center justify-between border-t border-slate-100 pt-4 dark:border-slate-800">
          <span class="text-sm text-slate-500">{{ picked().size }} selected</span>
          <span class="flex gap-2"><button class="btn-ghost" type="button" (click)="assign.set(null)">Cancel</button><button class="btn" type="button" [disabled]="busy()" (click)="saveDevices()">Save devices</button></span>
        </div>
      </app-modal>
    }
  `,
})
export class BranchesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly branches = signal<Branch[]>([]);
  readonly devices = signal<Device[]>([]);
  readonly search = signal('');
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<BranchForm | null>(null);
  readonly assign = signal<Branch | null>(null);
  readonly picked = signal<Set<string>>(new Set());

  readonly unassigned = computed(() => this.devices().filter(d => !d.branchId).length);
  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase();
    return this.branches().filter(b => !q || `${b.name} ${b.code} ${b.address ?? ''}`.toLowerCase().includes(q));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> {
    try { this.branches.set(await this.api.get<Branch[]>('/api/v1/branches')); this.devices.set(await this.api.get<Device[]>('/api/v1/devices')); }
    catch (e) { this.toast.err(e); }
  }

  startNew(): void { this.form.set({ code: '', name: '', address: '', phone: '', isActive: true }); }
  edit(b: Branch): void { this.form.set({ id: b.id, code: b.code, name: b.name, address: b.address ?? '', phone: b.phone ?? '', isActive: b.isActive }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      await this.api.put('/api/v1/branches', { id: f.id ?? null, code: f.code.trim(), name: f.name.trim(), address: f.address.trim() || null, phone: f.phone.trim() || null, isActive: f.isActive });
      this.toast.ok('Branch saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(b: Branch): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the branch "${b.name}"? People limited to it lose that access.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/branches/${b.id}`); this.toast.ok('Branch deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  startDevices(b: Branch): void { this.picked.set(new Set(b.deviceIds)); this.assign.set(b); }

  flip(id: string): void { this.picked.update(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; }); }

  async saveDevices(): Promise<void> {
    const b = this.assign(); if (!b) return;
    this.busy.set(true);
    try { await this.api.put(`/api/v1/branches/${b.id}/devices`, { ids: [...this.picked()] }); this.toast.ok('Devices assigned.'); this.assign.set(null); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
