import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { Plan, SubscriptionRow } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { money } from '../../shared/public.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';
import { DemoAccountsComponent } from './demo-accounts.component';

/** Every company's subscription. Move a company to another plan, extend or end its period, or issue an invoice by hand. */
@Component({
  selector: 'app-subscriptions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent, DemoAccountsComponent],
  template: `
    <app-page-header title="Subscriptions" subtitle="Which company is on which plan, and who owes money.">
      <button class="btn-ghost" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    <div class="mb-5 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
      @for (c of counts(); track c.label) {
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">{{ c.label }}</div><div class="text-2xl font-semibold" [class]="c.cls">{{ c.value }}</div></div>
      }
    </div>

    <div class="card mb-5 grid gap-3 sm:grid-cols-3">
      <label class="block sm:col-span-2"><span class="label">Search</span>
        <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
          <input class="input pl-9" placeholder="Company or plan…" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" /></span></label>
      <label class="block"><span class="label">Status</span>
        <select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); page.set(1)"><option value="">All</option>@for (s of statuses; track s) { <option [value]="s">{{ s }}</option> }</select></label>
    </div>

    <div class="card">
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Company</th><th>Plan</th><th>Status</th><th>Period ends</th><th>Outstanding</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (s of rows(); track s.id) {
              <tr>
                <td class="font-medium">{{ s.clientName }}</td>
                <td>{{ s.planName }}<div class="hint mt-0 capitalize">{{ s.billingCycle }}</div></td>
                <td><app-status [value]="s.status" />@if (s.status === 'Trial' && s.trialEndsAt) { <div class="hint">until {{ s.trialEndsAt | date: 'd MMM' }}</div> }</td>
                <td class="whitespace-nowrap">{{ s.currentPeriodEnd | date: 'd MMM yyyy' }}</td>
                <td class="whitespace-nowrap" [class.font-semibold]="s.outstanding > 0" [class.text-amber-600]="s.outstanding > 0">{{ s.outstanding > 0 ? fmt(s.outstanding) : '-' }}</td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn" (click)="invoice(s)" title="Issue an invoice now" aria-label="Issue an invoice"><app-icon name="receipt" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-primary" (click)="edit(s)" title="Edit subscription" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="6"><app-empty icon="chart" title="No subscriptions" hint="Companies get a subscription when you approve their application." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="filtered().length" (pageChange)="page.set($event)" />
    </div>

    @if (form(); as f) {
      <app-modal [title]="'Subscription: ' + f.name" width="max-w-lg" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Plan</span><select class="input" name="plan" [(ngModel)]="f.planId">@for (p of plans(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }</select></label>
            <label class="block"><span class="label">Status</span><select class="input" name="st" [(ngModel)]="f.status">@for (s of statuses; track s) { <option [value]="s">{{ s }}</option> }</select></label>
            <label class="block"><span class="label">Billing</span><select class="input" name="cy" [(ngModel)]="f.cycle"><option value="monthly">Monthly</option><option value="yearly">Yearly</option></select></label>
            <label class="block"><span class="label">Period ends</span><input class="input" type="date" name="pe" [(ngModel)]="f.periodEnd" /></label>
          </div>
          <p class="hint mt-3">Suspended or Cancelled signs the company out and stops its tokens. Setting it back to Active restores access.</p>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
    <app-demo-accounts />
  `,
})
export class SubscriptionsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly statuses = ['Trial', 'Active', 'PastDue', 'Suspended', 'Cancelled'];
  readonly all = signal<SubscriptionRow[]>([]);
  readonly plans = signal<Plan[]>([]);
  readonly search = signal('');
  readonly status = signal('');
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly form = signal<{ clientId: string; name: string; planId: number; status: string; cycle: string; periodEnd: string } | null>(null);

  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase();
    return this.all().filter(s => (!this.status() || s.status === this.status()) && (!q || `${s.clientName} ${s.planName}`.toLowerCase().includes(q)));
  });
  readonly rows = computed(() => slicePage(this.filtered(), this.page(), this.size));
  readonly counts = computed(() => {
    const a = this.all();
    return [
      { label: 'Active', value: a.filter(s => s.status === 'Active').length, cls: 'text-emerald-600' },
      { label: 'On trial', value: a.filter(s => s.status === 'Trial').length, cls: '' },
      { label: 'Past due', value: a.filter(s => s.status === 'PastDue').length, cls: a.some(s => s.status === 'PastDue') ? 'text-amber-600' : '' },
      { label: 'Suspended / cancelled', value: a.filter(s => s.status === 'Suspended' || s.status === 'Cancelled').length, cls: '' },
    ];
  });

  fmt(v: number): string { return money(v, 'USD'); }

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> {
    try { this.all.set(await this.api.get<SubscriptionRow[]>('/api/v1/admin/billing/subscriptions')); this.plans.set(await this.api.get<Plan[]>('/api/v1/plans')); } catch (e) { this.toast.err(e); }
  }

  edit(s: SubscriptionRow): void {
    this.form.set({ clientId: s.clientId, name: s.clientName ?? '', planId: s.planId, status: s.status, cycle: s.billingCycle, periodEnd: s.currentPeriodEnd.slice(0, 10) });
  }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      await this.api.put(`/api/v1/admin/billing/subscriptions/${f.clientId}`, { planId: f.planId, status: f.status, billingCycle: f.cycle, periodEnd: f.periodEnd ? f.periodEnd + 'T00:00:00Z' : null });
      this.toast.ok('Subscription updated.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async invoice(s: SubscriptionRow): Promise<void> {
    if (!(await this.dialog.confirm(`Issue an invoice to ${s.clientName} for their current plan now?`, 'Issue invoice'))) return;
    try { await this.api.post(`/api/v1/admin/billing/subscriptions/${s.clientId}/invoice`); this.toast.ok('Invoice issued.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
