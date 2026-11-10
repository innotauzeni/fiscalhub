import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { ToastService } from '../../core/ui.services';
import { PageHeaderComponent } from '../../shared/ui.components';

interface Totals { companies: number; activeCompanies: number; onTrial: number; locked: number; atRisk: number; devices: number; receipts: number; rejected: number; pending: number; rejectedPercent: number; receiptsPrevious: number }
interface Day { date: string; receipts: number; rejected: number }
interface Company { id: string; name: string; status: string; plan?: string | null; partner?: string | null; devices: number; receipts: number; receiptsPrevious: number; rejected: number; pending: number; lastReceiptAt?: string | null; lastLoginAt?: string | null; risk: string; reasons: string[]; createdAt: string }
interface PlanRow { plan: string; companies: number; receipts: number }
interface Usage { days: number; from: string; to: string; totals: Totals; daily: Day[]; companies: Company[]; plans: PlanRow[] }

/** For the platform operator: how the gateway is used, and which companies look like they are slipping away, with the reasons. */
@Component({
  selector: 'app-analytics',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DecimalPipe, FormsModule, PageHeaderComponent],
  template: `
    <app-page-header title="Usage analytics" subtitle="Receipts, rejections and the companies that need a phone call.">
      <div class="flex gap-1 rounded-xl border border-slate-200 p-1 dark:border-slate-700">
        @for (d of [7, 30, 90]; track d) { <button type="button" class="rounded-lg px-3 py-1.5 text-sm font-medium" [class]="days() === d ? 'bg-brand-600 text-white' : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800'" (click)="setDays(d)">{{ d }} days</button> }
      </div>
    </app-page-header>

    @if (u(); as r) {
      <div class="mb-5 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <div class="card"><div class="text-xs text-slate-500">Companies</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ r.totals.activeCompanies }}<span class="text-sm font-normal text-slate-500"> / {{ r.totals.companies }}</span></div><div class="text-xs text-slate-500">{{ r.totals.onTrial }} on trial · {{ r.totals.locked }} locked for payment</div></div>
        <div class="card" [class.ring-2]="r.totals.atRisk > 0" [class.ring-amber-400]="r.totals.atRisk > 0"><div class="text-xs text-slate-500">At risk</div><div class="mt-1 text-2xl font-semibold tabular-nums" [class.text-amber-600]="r.totals.atRisk > 0">{{ r.totals.atRisk }}</div><div class="text-xs text-slate-500">companies to look at</div></div>
        <div class="card"><div class="text-xs text-slate-500">Receipts in {{ r.days }} days</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ r.totals.receipts | number }}</div>
          <div class="text-xs" [class]="trend(r) >= 0 ? 'text-emerald-600' : 'text-red-600'">{{ r.totals.receiptsPrevious ? (trend(r) >= 0 ? '+' : '') + (trend(r) | number: '1.0-0') + '% vs the period before' : 'no earlier period to compare' }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Rejected by ZIMRA</div><div class="mt-1 text-2xl font-semibold tabular-nums" [class.text-red-600]="r.totals.rejectedPercent >= 5">{{ r.totals.rejectedPercent }}%</div><div class="text-xs text-slate-500">{{ r.totals.rejected }} receipts · {{ r.totals.pending }} pending · {{ r.totals.devices }} devices</div></div>
      </div>

      <div class="mb-5 grid gap-5 lg:grid-cols-3">
        <section class="card lg:col-span-2">
          <h2 class="text-sm font-semibold">Receipts per day</h2>
          <div class="mt-4 flex h-40 items-end gap-px" role="img" [attr.aria-label]="'Receipts per day over the last ' + r.days + ' days'">
            @for (d of r.daily; track d.date) {
              <div class="group relative flex h-full flex-1 flex-col justify-end" [title]="(d.date | date: 'd MMM') + ': ' + d.receipts + ' receipts' + (d.rejected ? ', ' + d.rejected + ' rejected' : '')">
                <div class="w-full rounded-t bg-red-400" [style.height.%]="pct(d.rejected, max())"></div>
                <div class="w-full bg-brand-500 group-hover:bg-brand-600" [class.rounded-t]="!d.rejected" [style.height.%]="pct(d.receipts - d.rejected, max())"></div>
              </div>
            }
          </div>
          <div class="mt-1 flex justify-between text-xs text-slate-500"><span>{{ r.from | date: 'd MMM' }}</span><span>{{ r.to | date: 'd MMM' }}</span></div>
          <div class="mt-2 flex gap-4 text-xs text-slate-500"><span><span class="mr-1 inline-block h-2 w-2 rounded-sm bg-brand-500"></span>Accepted or queued</span><span><span class="mr-1 inline-block h-2 w-2 rounded-sm bg-red-400"></span>Rejected</span></div>
        </section>
        <section class="card">
          <h2 class="text-sm font-semibold">By plan</h2>
          <table class="table mt-2"><thead><tr><th>Plan</th><th class="text-right">Companies</th><th class="text-right">Receipts</th></tr></thead>
            <tbody>@for (p of r.plans; track p.plan) { <tr><td>{{ p.plan }}</td><td class="text-right tabular-nums">{{ p.companies }}</td><td class="text-right tabular-nums">{{ p.receipts | number }}</td></tr> }</tbody></table>
        </section>
      </div>

      <section class="card">
        <div class="mb-3 flex flex-wrap items-end gap-3">
          <h2 class="flex-1 text-sm font-semibold">Companies</h2>
          <label class="block"><span class="label">Show</span><select class="input" [ngModel]="filter()" (ngModelChange)="filter.set($event)"><option value="all">All</option><option value="risk">Needs attention</option><option value="High">High risk</option></select></label>
          <label class="block"><span class="label">Search</span><input class="input" placeholder="Company…" [ngModel]="q()" (ngModelChange)="q.set($event)" /></label>
        </div>
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>Company</th><th>Status</th><th class="text-right">Devices</th><th class="text-right">Receipts</th><th class="text-right">Before</th><th class="text-right">Rejected</th><th>Last receipt</th><th>Last sign-in</th><th>Risk</th></tr></thead>
            <tbody>
              @for (c of rows(); track c.id) {
                <tr>
                  <td class="font-medium">{{ c.name }}<div class="text-xs font-normal text-slate-500">{{ c.plan ?? 'No plan' }}@if (c.partner) { · via {{ c.partner }} }</div></td>
                  <td><span class="badge whitespace-nowrap" [class]="statusClass(c.status)">{{ c.status }}</span></td>
                  <td class="text-right tabular-nums">{{ c.devices }}</td>
                  <td class="text-right tabular-nums">{{ c.receipts | number }}</td>
                  <td class="text-right tabular-nums text-slate-500">{{ c.receiptsPrevious | number }}</td>
                  <td class="text-right tabular-nums" [class.text-red-600]="c.rejected > 0">{{ c.rejected }}</td>
                  <td class="whitespace-nowrap text-xs text-slate-500">{{ c.lastReceiptAt ? (c.lastReceiptAt | date: 'd MMM y') : 'never' }}</td>
                  <td class="whitespace-nowrap text-xs text-slate-500">{{ c.lastLoginAt ? (c.lastLoginAt | date: 'd MMM y') : 'never' }}</td>
                  <td class="min-w-48"><span class="badge" [class]="c.risk === 'High' ? 'badge-bad' : c.risk === 'Medium' ? 'badge-warn' : 'badge-ok'">{{ c.risk }}</span>
                    @for (why of c.reasons; track why) { <div class="mt-0.5 text-xs text-slate-600 dark:text-slate-400">{{ why }}</div> }</td>
                </tr>
              } @empty { <tr><td colspan="9" class="py-6 text-center text-slate-500">{{ loaded() ? 'Nothing to show.' : 'Loading…' }}</td></tr> }
            </tbody>
          </table>
        </div>
      </section>
    } @else { <div class="card text-center text-slate-500">Loading…</div> }
  `,
})
export class AnalyticsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly u = signal<Usage | null>(null);
  readonly loaded = signal(false);
  readonly days = signal(30);
  readonly filter = signal('all');
  readonly q = signal('');
  readonly max = computed(() => Math.max(1, ...(this.u()?.daily ?? []).map(d => d.receipts)));
  readonly rows = computed(() => {
    const q = this.q().trim().toLowerCase(), f = this.filter();
    return (this.u()?.companies ?? []).filter(c => (!q || c.name.toLowerCase().includes(q)) && (f === 'all' || (f === 'risk' ? c.risk !== 'Low' : c.risk === f)));
  });

  ngOnInit(): Promise<void> { return this.load(); }

  async setDays(d: number): Promise<void> { this.days.set(d); await this.load(); }

  private async load(): Promise<void> {
    try { this.u.set(await this.api.get<Usage>('/api/v1/analytics/usage', { days: this.days() })); this.loaded.set(true); } catch (e) { this.toast.err(e); }
  }

  pct(v: number, max: number): number { return max <= 0 ? 0 : Math.max(v > 0 ? 3 : 0, (v / max) * 100); }
  trend(r: Usage): number { return r.totals.receiptsPrevious ? ((r.totals.receipts - r.totals.receiptsPrevious) / r.totals.receiptsPrevious) * 100 : 0; }
  statusClass(s: string): string { return s === 'Active' ? 'badge-ok' : s === 'Trial' ? 'badge-info' : s === 'Locked' || s === 'Suspended' || s === 'PastDue' ? 'badge-bad' : 'badge-warn'; }
}
