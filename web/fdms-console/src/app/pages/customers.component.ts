import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { Customer } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../shared/list.components';
import { ModalComponent } from '../shared/modal.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';

interface Form { id?: string; name: string; tradeName: string; tin: string; vatNumber: string; email: string; phone: string; address: string; notes: string; isActive: boolean; }

/** The company's buyers. Pick one on a new invoice and the name, TIN and VAT number are filled in exactly as ZIMRA needs them. */
@Component({
  selector: 'app-customers',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Customers" subtitle="Your buyers, kept once. Business invoices need the buyer's name and TIN: pick them here instead of retyping.">
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />New customer</button>
    </app-page-header>

    <div class="mb-5 grid gap-4 sm:grid-cols-3">
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Customers</div><div class="text-2xl font-semibold">{{ all().length }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">With a TIN</div><div class="text-2xl font-semibold text-emerald-600">{{ withTin() }}</div></div>
      <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">VAT registered</div><div class="text-2xl font-semibold">{{ withVat() }}</div></div>
    </div>

    <div class="card mb-5 grid gap-3 sm:grid-cols-3">
      <label class="block sm:col-span-2"><span class="label">Search</span>
        <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
          <input class="input pl-9" placeholder="Name, TIN or email…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
      <label class="flex items-end gap-2 pb-2 text-sm"><input type="checkbox" class="h-4 w-4" [ngModel]="inactive()" (ngModelChange)="inactive.set($event); load()" /> Show inactive</label>
    </div>

    <div class="card">
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Customer</th><th>TIN</th><th>VAT no.</th><th>Contact</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (c of rows(); track c.id) {
              <tr>
                <td><div class="font-medium">{{ c.name }}</div><div class="hint mt-0">{{ c.tradeName }}{{ c.tradeName && c.address ? ' · ' : '' }}{{ c.address }}</div></td>
                <td class="font-mono text-xs">{{ c.tin ?? '-' }}</td>
                <td class="font-mono text-xs">{{ c.vatNumber ?? '-' }}</td>
                <td><div>{{ c.email ?? '' }}</div><div class="hint mt-0">{{ c.phone }}</div></td>
                <td><app-status [value]="c.isActive ? 'ACTIVE' : 'INACTIVE'" /></td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn" (click)="edit(c)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-danger" (click)="remove(c)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="6"><app-empty icon="users" title="No customers yet" hint="Add the businesses you invoice most often." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit customer' : 'New customer'" width="max-w-2xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Registered name *</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="150" /></label>
            <label class="block sm:col-span-2"><span class="label">Trade name</span><input class="input" name="tn" [(ngModel)]="f.tradeName" maxlength="150" /></label>
            <label class="block"><span class="label">TIN (10 digits)</span><input class="input font-mono" name="tin" [(ngModel)]="f.tin" inputmode="numeric" maxlength="10" placeholder="2000123456" /></label>
            <label class="block"><span class="label">VAT number (9 digits)</span><input class="input font-mono" name="vat" [(ngModel)]="f.vatNumber" inputmode="numeric" maxlength="9" placeholder="220054321" /></label>
            <label class="block"><span class="label">Email</span><input class="input" name="em" type="email" [(ngModel)]="f.email" maxlength="200" /></label>
            <label class="block"><span class="label">Phone</span><input class="input" name="ph" type="tel" [(ngModel)]="f.phone" maxlength="40" /></label>
            <label class="block sm:col-span-2"><span class="label">Address</span><input class="input" name="ad" [(ngModel)]="f.address" maxlength="300" /></label>
            <label class="block sm:col-span-2"><span class="label">Notes</span><input class="input" name="no" [(ngModel)]="f.notes" maxlength="500" /></label>
            <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" class="h-4 w-4" name="ac" [(ngModel)]="f.isActive" /> Active (shown when you invoice)</label>
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class CustomersComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly all = signal<Customer[]>([]);
  readonly search = signal('');
  readonly inactive = signal(false);
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<Form | null>(null);

  readonly withTin = computed(() => this.all().filter(c => !!c.tin).length);
  readonly withVat = computed(() => this.all().filter(c => !!c.vatNumber).length);
  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase();
    return this.all().filter(c => !q || `${c.name} ${c.tradeName ?? ''} ${c.tin ?? ''} ${c.email ?? ''}`.toLowerCase().includes(q));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> { try { this.all.set(await this.api.get<Customer[]>('/api/v1/customers', { includeInactive: this.inactive() })); } catch (e) { this.toast.err(e); } }

  startNew(): void { this.form.set({ name: '', tradeName: '', tin: '', vatNumber: '', email: '', phone: '', address: '', notes: '', isActive: true }); }
  edit(c: Customer): void { this.form.set({ id: c.id, name: c.name, tradeName: c.tradeName ?? '', tin: c.tin ?? '', vatNumber: c.vatNumber ?? '', email: c.email ?? '', phone: c.phone ?? '', address: c.address ?? '', notes: c.notes ?? '', isActive: c.isActive }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    const n = (v: string) => v.trim() || null;
    this.busy.set(true);
    try {
      await this.api.put('/api/v1/customers', { id: f.id ?? null, name: f.name.trim(), tradeName: n(f.tradeName), tin: n(f.tin), vatNumber: n(f.vatNumber), email: n(f.email), phone: n(f.phone), address: n(f.address), notes: n(f.notes), isActive: f.isActive });
      this.toast.ok('Customer saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(c: Customer): Promise<void> {
    if (!(await this.dialog.confirm(`Delete "${c.name}"? Invoices already issued keep their own copy of the buyer's details.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/customers/${c.id}`); this.toast.ok('Customer deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
