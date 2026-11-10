import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { ClientApplication, Plan } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

type Filter = 'Pending' | 'Unverified' | 'Approved' | 'Rejected' | '';

/** Companies that registered on the website. Approving creates the company, its administrator and its subscription; rejecting tells the applicant why. */
@Component({
  selector: 'app-applications',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Applications" subtitle="Companies that asked to join. Nothing exists for them until you approve.">
      <button class="btn-ghost" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    <div class="mb-4 flex flex-wrap gap-1 border-b border-slate-200 dark:border-slate-800" role="tablist">
      @for (t of tabs; track t.value) {
        <button type="button" class="tab" [class.tab-active]="filter() === t.value" (click)="setFilter(t.value)">{{ t.label }}@if (t.value === 'Pending' && pending() > 0) { <span class="badge badge-warn">{{ pending() }}</span> }</button>
      }
    </div>

    <div class="card">
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Company</th><th>Contact</th><th>Plan</th><th>Applied</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (a of rows(); track a.id) {
              <tr>
                <td><div class="font-medium">{{ a.companyName }}</div><div class="hint mt-0">{{ a.tin ? 'TIN ' + a.tin : '' }}</div></td>
                <td><div>{{ a.contactName }} {{ a.contactSurname }}</div><div class="hint mt-0">{{ a.email }}{{ a.phone ? ' · ' + a.phone : '' }}</div></td>
                <td>{{ a.planName ?? 'Not chosen' }}<div class="hint mt-0 capitalize">{{ a.billingCycle }}</div></td>
                <td class="whitespace-nowrap">{{ a.createdAt | date: 'd MMM yyyy, HH:mm' }}</td>
                <td>
                  <span class="badge" [class.badge-warn]="a.status === 'Pending'" [class.badge-ok]="a.status === 'Approved'" [class.badge-bad]="a.status === 'Rejected'">{{ a.status }}</span>
                  @if (a.decisionReason) { <div class="hint max-w-48">{{ a.decisionReason }}</div> }
                </td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn" (click)="show(a)" title="Details" aria-label="Details"><app-icon name="eye" size="h-4 w-4" /></button>
                  @if (a.status === 'Pending' || a.status === 'Unverified') {
                    <button class="btn btn-sm" (click)="startApprove(a)">Approve</button>
                    <button class="btn-ghost btn-sm" (click)="reject(a)">Reject</button>
                  }
                </div></td>
              </tr>
            } @empty { <tr><td colspan="6"><app-empty icon="user-check" title="Nothing here" hint="New registrations from the website show up in this list." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="page()" [pageSize]="size" [total]="list().length" (pageChange)="page.set($event)" />
    </div>

    @if (view(); as a) {
      <app-modal [title]="a.companyName" [subtitle]="'Applied ' + (a.createdAt | date: 'd MMM yyyy, HH:mm')" width="max-w-lg" (closed)="close()">
        @if (logoUrl(); as u) { <img [src]="u" alt="Company logo" class="mb-4 max-h-16 max-w-48 object-contain" /> }
        <dl class="grid grid-cols-[auto_1fr] gap-x-5 gap-y-2 text-sm">
          <dt class="text-slate-500">Contact</dt><dd>{{ a.contactName }} {{ a.contactSurname }}</dd>
          <dt class="text-slate-500">Email</dt><dd>{{ a.email }}</dd>
          <dt class="text-slate-500">Phone</dt><dd>{{ a.phone ?? '-' }}</dd>
          <dt class="text-slate-500">TIN</dt><dd>{{ a.tin ?? '-' }}</dd>
          <dt class="text-slate-500">Plan</dt><dd>{{ a.planName ?? 'Not chosen' }} ({{ a.billingCycle }})</dd>
          <dt class="text-slate-500">From</dt><dd class="font-mono text-xs">{{ a.ipAddress ?? '-' }}</dd>
          @if (a.message) { <dt class="text-slate-500">Message</dt><dd class="whitespace-pre-wrap">{{ a.message }}</dd> }
          @if (a.decidedAt) { <dt class="text-slate-500">Decision</dt><dd>{{ a.status }} by {{ a.decidedBy ?? 'unknown' }} on {{ a.decidedAt | date: 'd MMM yyyy' }}@if (a.decisionReason) { : {{ a.decisionReason }} }</dd> }
        </dl>
        <div class="mt-6 flex justify-end"><button class="btn-ghost" (click)="close()">Close</button></div>
      </app-modal>
    }

    @if (approving(); as a) {
      <app-modal [title]="'Approve ' + a.companyName" subtitle="This creates the company, an administrator account with the password they chose, and their subscription." width="max-w-lg" (closed)="approving.set(null)">
        <form (ngSubmit)="approve()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Plan</span>
              <select class="input" name="plan" [(ngModel)]="planId" required>@for (p of plans(); track p.id) { <option [ngValue]="p.id">{{ p.name }} ({{ p.currency }} {{ p.priceMonthly }}/mo)</option> }</select></label>
            <label class="block"><span class="label">Billing</span><select class="input" name="cycle" [(ngModel)]="cycle"><option value="monthly">Monthly</option><option value="yearly">Yearly</option></select></label>
            <label class="block"><span class="label">Free trial (days)</span><input class="input" type="number" min="0" max="90" name="trial" [(ngModel)]="trial" placeholder="Plan default" /></label>
            <label class="block sm:col-span-2"><span class="label">Note to the applicant (optional)</span><input class="input" name="note" [(ngModel)]="note" maxlength="500" /></label>
          </div>
          <p class="hint mt-3">Leave the trial empty to use the plan's own trial. A trial of 0 days bills the first period straight away.</p>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="approving.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !planId">Approve and create account</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class ApplicationsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly tabs: { value: Filter; label: string }[] = [{ value: 'Pending', label: 'Pending' }, { value: 'Unverified', label: 'Email not confirmed' }, { value: 'Approved', label: 'Approved' }, { value: 'Rejected', label: 'Rejected' }, { value: '', label: 'All' }];
  readonly filter = signal<Filter>('Pending');
  readonly all = signal<ClientApplication[]>([]);
  readonly plans = signal<Plan[]>([]);
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly view = signal<ClientApplication | null>(null);
  readonly logoUrl = signal<string | null>(null);

  async show(a: ClientApplication): Promise<void> {
    this.view.set(a); this.clearLogo();
    if (!a.hasLogo) return;
    try { const u = URL.createObjectURL(await this.api.blob(`/api/v1/applications/${a.id}/logo`)); if (this.view()?.id === a.id) this.logoUrl.set(u); else URL.revokeObjectURL(u); } catch { /* the details still show */ }
  }
  close(): void { this.view.set(null); this.clearLogo(); }
  private clearLogo(): void { const u = this.logoUrl(); if (u) URL.revokeObjectURL(u); this.logoUrl.set(null); }
  readonly approving = signal<ClientApplication | null>(null);
  planId: number | null = null; cycle: 'monthly' | 'yearly' = 'monthly'; trial: number | null = null; note = '';

  readonly pending = computed(() => this.all().filter(a => a.status === 'Pending').length);
  readonly list = computed(() => this.all().filter(a => !this.filter() || a.status === this.filter()));
  readonly rows = computed(() => slicePage(this.list(), this.page(), this.size));

  ngOnInit(): Promise<void> { return this.load(); }

  setFilter(f: Filter): void { this.filter.set(f); this.page.set(1); }

  async load(): Promise<void> {
    try { this.all.set(await this.api.get<ClientApplication[]>('/api/v1/applications')); this.plans.set((await this.api.get<Plan[]>('/api/v1/plans')).filter(p => p.isActive)); }
    catch (e) { this.toast.err(e); }
  }

  startApprove(a: ClientApplication): void {
    this.planId = a.planId ?? this.plans()[0]?.id ?? null; this.cycle = a.billingCycle; this.trial = null; this.note = ''; this.approving.set(a);
  }

  async approve(): Promise<void> {
    const a = this.approving(); if (!a) return;
    this.busy.set(true);
    try {
      await this.api.post(`/api/v1/applications/${a.id}/approve`, { planId: this.planId, billingCycle: this.cycle, trialDays: this.trial === null || this.trial === undefined || (this.trial as unknown) === '' ? null : Number(this.trial), note: this.note.trim() || null });
      this.toast.ok(`${a.companyName} is approved. They were emailed (if email is set up).`); this.approving.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async reject(a: ClientApplication): Promise<void> {
    const reason = await this.dialog.prompt(`Why is ${a.companyName} not approved? The applicant will see this.`, 'Reason', 'Reject');
    if (reason === null) return;
    try { await this.api.post(`/api/v1/applications/${a.id}/reject`, { reason }); this.toast.ok('Application rejected.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
