import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { Amount, Revenue } from '../../core/models';
import { ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';

/** "1,250.00 USD + 300.00 ZWG", or a dash. */
export function money(list: Amount[] | undefined | null): string {
  if (!list?.length) return '0.00';
  return list.map(a => `${a.amount.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${a.currency}`).join(' + ');
}

/** The platform owner's money: recurring revenue, what came in, what is owed, who pays, which plans sell, and what the platform owes in rewards. */
@Component({
  selector: 'app-revenue',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DecimalPipe, RouterLink, IconComponent],
  template: `
    <div class="relative mb-6 overflow-hidden rounded-3xl bg-linear-to-br from-emerald-900 via-teal-800 to-sky-800 p-6 text-white shadow-lg sm:p-8">
      <div class="pointer-events-none absolute -right-16 -top-20 h-72 w-72 rounded-full bg-emerald-400/20 blur-3xl"></div>
      <div class="relative flex flex-wrap items-end justify-between gap-5">
        <div>
          <p class="text-sm text-emerald-200">Revenue</p>
          <h1 class="mt-1 text-3xl font-semibold tracking-tight sm:text-4xl">{{ r() ? fmt(r()!.mrr) : '…' }} <span class="text-base font-normal text-emerald-200">/ month</span></h1>
          <p class="mt-1 text-sm text-emerald-100">Recurring revenue from {{ r()?.counts?.paying ?? 0 }} paying companies · {{ r() ? fmt(r()!.arr) : '' }} a year</p>
        </div>
        <div class="flex gap-2">
          <a routerLink="/admin/payments" class="inline-flex items-center gap-1.5 rounded-xl bg-white px-4 py-2 text-sm font-medium text-emerald-800 shadow-sm hover:bg-emerald-50"><app-icon name="card" size="h-4 w-4" />Payments
            @if (r()?.counts?.awaitingConfirmation) { <span class="rounded-full bg-amber-500 px-1.5 text-xs text-white">{{ r()!.counts.awaitingConfirmation }}</span> }</a>
          <a routerLink="/admin/plans" class="inline-flex items-center gap-1.5 rounded-xl bg-white/10 px-4 py-2 text-sm font-medium ring-1 ring-white/25 hover:bg-white/20"><app-icon name="layers" size="h-4 w-4" />Plans</a>
          <button class="inline-flex items-center rounded-xl bg-white/10 px-3 py-2 ring-1 ring-white/25 hover:bg-white/20" (click)="load()" aria-label="Refresh"><app-icon name="refresh" size="h-4 w-4" /></button>
        </div>
      </div>
    </div>

    @if (r(); as r) {
      <div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <div class="card"><div class="text-xs font-medium uppercase tracking-wide text-slate-500">Collected this month</div><div class="mt-2 text-2xl font-semibold tabular-nums">{{ fmt(r.thisMonth) }}</div><div class="hint mt-1">Last month {{ fmt(r.lastMonth) }}</div></div>
        <div class="card"><div class="text-xs font-medium uppercase tracking-wide text-slate-500">Last 12 months</div><div class="mt-2 text-2xl font-semibold tabular-nums">{{ fmt(r.last12Months) }}</div><div class="hint mt-1">All time {{ fmt(r.allTime) }}</div></div>
        <div class="card"><div class="text-xs font-medium uppercase tracking-wide text-slate-500">Outstanding invoices</div><div class="mt-2 text-2xl font-semibold tabular-nums text-amber-600">{{ fmt(r.outstanding) }}</div><div class="hint mt-1">Overdue <span class="font-medium text-red-600">{{ fmt(r.overdue) }}</span></div></div>
        <div class="card"><div class="text-xs font-medium uppercase tracking-wide text-slate-500">Average per paying company</div><div class="mt-2 text-2xl font-semibold tabular-nums">{{ fmt(r.arpuMonthly) }}</div><div class="hint mt-1">Trial to paid {{ r.trialConversionPercent | number:'1.0-1' }}%</div></div>
      </div>

      <div class="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4 xl:grid-cols-8">
        @for (c of countTiles(); track c.label) {
          <a [routerLink]="c.link" class="rounded-2xl border border-slate-200 bg-white p-3 text-center transition hover:border-brand-300 hover:shadow-sm dark:border-slate-800 dark:bg-slate-900">
            <div class="text-2xl font-semibold tabular-nums" [class]="c.tone">{{ c.value }}</div><div class="text-xs text-slate-500">{{ c.label }}</div>
          </a>
        }
      </div>

      <div class="mb-6 grid gap-6 xl:grid-cols-3">
        <div class="card xl:col-span-2">
          <div class="card-title flex items-center justify-between">Collected per month <span class="text-xs font-normal text-slate-500">{{ chartCurrency() }}</span></div>
          <div class="flex h-48 items-end gap-1.5 sm:gap-2" role="img" [attr.aria-label]="'Money collected per month in ' + chartCurrency()">
            @for (m of bars(); track m.month) {
              <div class="flex h-full min-w-0 flex-1 flex-col items-center justify-end gap-1">
                <span class="hidden text-[10px] tabular-nums text-slate-500 sm:block">{{ m.value ? (m.value | number:'1.0-0') : '' }}</span>
                <div class="w-full rounded-t-md bg-linear-to-t from-emerald-600 to-teal-400 transition-all" [style.height.%]="m.pct" [title]="m.month + ': ' + (m.value | number:'1.2-2') + ' ' + chartCurrency() + ', ' + m.invoices + ' invoice(s)'"></div>
                <span class="text-[10px] text-slate-500">{{ m.label }}</span>
              </div>
            }
          </div>
        </div>
        <div class="card">
          <div class="card-title">How companies pay (12 months)</div>
          <ul class="space-y-3">
            @for (m of r.methods; track m.method) {
              <li class="flex items-center justify-between gap-3 text-sm"><span class="font-medium">{{ m.method }}</span><span class="text-right tabular-nums text-slate-600 dark:text-slate-300">{{ fmt(m.collected) }}<span class="block text-xs text-slate-400">{{ m.payments }} payment(s)</span></span></li>
            } @empty { <li class="text-sm text-slate-500">No payments yet.</li> }
          </ul>
        </div>
      </div>

      <div class="card mb-6 overflow-x-auto">
        <div class="card-title">Plans</div>
        <table class="table min-w-[520px]">
          <thead><tr><th>Plan</th><th class="text-right">Price / month</th><th class="text-right">Paying</th><th class="text-right">On trial</th><th class="text-right">Recurring / month</th><th class="w-1/3">Share</th></tr></thead>
          <tbody>
            @for (p of r.plans; track p.planId) {
              <tr><td class="font-medium">{{ p.plan }}</td><td class="text-right tabular-nums">{{ p.priceMonthly | number:'1.2-2' }} {{ p.currency }}</td><td class="text-right tabular-nums">{{ p.paying }}</td><td class="text-right tabular-nums">{{ p.trial }}</td>
                <td class="text-right tabular-nums font-medium">{{ p.mrr | number:'1.2-2' }} {{ p.currency }}</td>
                <td><div class="h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-slate-800"><div class="h-full rounded-full bg-emerald-500" [style.width.%]="share(p.mrr)"></div></div></td></tr>
            }
          </tbody>
        </table>
      </div>

      <div class="mb-6 grid gap-6 xl:grid-cols-2">
        <div class="card overflow-x-auto">
          <div class="card-title">Best paying companies</div>
          <table class="table min-w-[420px]"><thead><tr><th>Company</th><th>Plan</th><th class="text-right">Paid</th><th>Last paid</th></tr></thead>
            <tbody>@for (c of r.topClients; track c.clientId) { <tr><td class="font-medium">{{ c.company }}</td><td>{{ c.plan ?? '-' }} <span class="badge">{{ c.status }}</span></td><td class="text-right tabular-nums">{{ fmt(c.paidAllTime) }}</td><td class="whitespace-nowrap text-slate-500">{{ c.lastPaidAt ? (c.lastPaidAt | date:'d MMM y') : '-' }}</td></tr> }
              @empty { <tr><td colspan="4" class="py-6 text-center text-slate-500">No paid invoices yet.</td></tr> }</tbody></table>
        </div>
        <div class="card overflow-x-auto">
          <div class="card-title">Companies that owe</div>
          <table class="table min-w-[420px]"><thead><tr><th>Company</th><th>Status</th><th class="text-right">Owes</th><th></th></tr></thead>
            <tbody>@for (c of r.owing; track c.clientId) { <tr><td class="font-medium">{{ c.company }}</td><td><span class="badge" [class.badge-bad]="c.status === 'PastDue' || c.status === 'Suspended'">{{ c.status }}</span></td><td class="text-right tabular-nums text-amber-700 dark:text-amber-300">{{ fmt(c.outstanding) }}</td>
              <td class="text-right"><a routerLink="/admin/subscriptions" class="text-sm font-medium text-brand-600 underline">Follow up</a></td></tr> }
              @empty { <tr><td colspan="4" class="py-6 text-center text-slate-500">Nobody owes anything.</td></tr> }</tbody></table>
        </div>
      </div>

      <div class="card mb-6">
        <div class="card-title">What the platform owes</div>
        <div class="grid gap-4 sm:grid-cols-3">
          <div><div class="text-xs uppercase tracking-wide text-slate-500">Referral coins held</div><div class="mt-1 text-xl font-semibold tabular-nums">{{ r.coinsOutstanding | number }} coins</div><div class="hint">worth {{ fmt(r.coinsLiability) }} if all are spent on invoices</div></div>
          <div><div class="text-xs uppercase tracking-wide text-slate-500">Referral money not paid out</div><div class="mt-1 text-xl font-semibold tabular-nums">{{ fmt(r.referralMoneyOwed) }}</div><a routerLink="/admin/referral-program" class="hint underline">Referral program</a></div>
          <div><div class="text-xs uppercase tracking-wide text-slate-500">Partner commission not paid out</div><div class="mt-1 text-xl font-semibold tabular-nums">{{ fmt(r.partnerCommissionOwed) }}</div><a routerLink="/admin/partners" class="hint underline">Partners</a></div>
        </div>
      </div>
      <p class="hint text-right">Worked out {{ r.generatedAt | date:'d MMM y, HH:mm' }}. Money paid with referral coins is not counted as revenue.</p>
    } @else if (loading()) {
      <div class="grid animate-pulse gap-4 sm:grid-cols-2 xl:grid-cols-4">@for (i of [1,2,3,4]; track i) { <div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div> }</div>
    }
  `,
})
export class RevenueComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly r = signal<Revenue | null>(null);
  readonly loading = signal(true);
  readonly fmt = money;

  /** The chart shows the currency most of the money comes in. */
  readonly chartCurrency = computed(() => this.r()?.last12Months?.slice().sort((a, b) => b.amount - a.amount)[0]?.currency ?? 'USD');
  readonly bars = computed(() => {
    const cur = this.chartCurrency();
    const rows = (this.r()?.months ?? []).map(m => ({ month: m.month, invoices: m.invoices, value: m.collected.find(a => a.currency === cur)?.amount ?? 0 }));
    const top = Math.max(1, ...rows.map(x => x.value));
    const names = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    return rows.map(x => ({ ...x, pct: Math.max(x.value ? 3 : 1, (x.value / top) * 100), label: names[Number(x.month.slice(5, 7)) - 1] ?? x.month }));
  });
  readonly countTiles = computed(() => {
    const c = this.r()?.counts;
    if (!c) return [];
    return [
      { label: 'Companies', value: c.companies, link: '/admin/clients', tone: '' },
      { label: 'Paying', value: c.paying, link: '/admin/subscriptions', tone: 'text-emerald-600' },
      { label: 'On trial', value: c.trial, link: '/admin/subscriptions', tone: 'text-sky-600' },
      { label: 'Trials ending (7 days)', value: c.trialsEndingIn7Days, link: '/admin/subscriptions', tone: 'text-sky-600' },
      { label: 'Past due', value: c.pastDue, link: '/admin/subscriptions', tone: 'text-amber-600' },
      { label: 'Suspended', value: c.suspended, link: '/admin/subscriptions', tone: 'text-red-600' },
      { label: 'New this month', value: c.newThisMonth, link: '/admin/applications', tone: 'text-violet-600' },
      { label: 'Payments to confirm', value: c.awaitingConfirmation, link: '/admin/payments', tone: c.awaitingConfirmation ? 'text-amber-600' : '' },
    ];
  });
  private readonly topPlan = computed(() => Math.max(1, ...(this.r()?.plans ?? []).map(p => p.mrr)));

  share(mrr: number): number { return (mrr / this.topPlan()) * 100; }

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    this.loading.set(true);
    try { this.r.set(await this.api.get<Revenue>('/api/v1/admin/revenue')); }
    catch (e) { this.toast.err(e); }
    finally { this.loading.set(false); }
  }
}

