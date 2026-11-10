import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { PlatformDashboard, Revenue } from '../../core/models';
import { ToastService } from '../../core/ui.services';
import { TrendChartComponent } from '../../shared/charts.components';
import { NotificationsPanelComponent } from '../../shared/notifications-panel.component';
import { money } from './revenue.component';
import { StatCardComponent } from '../../shared/modal.components';
import { IconComponent } from '../../shared/ui.components';
import { greeting } from '../dashboard.component';

/** The platform operator's home: every company at a glance. */
@Component({
  selector: 'app-overview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, IconComponent, StatCardComponent, TrendChartComponent, NotificationsPanelComponent],
  template: `
    <div class="relative mb-6 overflow-hidden rounded-3xl bg-linear-to-br from-slate-900 via-indigo-900 to-blue-800 p-6 text-white shadow-lg sm:p-8">
      <div class="pointer-events-none absolute -right-10 -top-20 h-72 w-72 rounded-full bg-blue-400/20 blur-3xl"></div>
      <div class="pointer-events-none absolute -bottom-24 left-1/4 h-60 w-60 rounded-full bg-violet-500/20 blur-3xl"></div>
      <div class="relative flex flex-wrap items-end justify-between gap-5">
        <div>
          <p class="text-sm text-blue-200">{{ today | date:'EEEE, d MMMM y' }}</p>
          <h1 class="mt-1 text-2xl font-semibold tracking-tight sm:text-3xl">{{ hello }}, {{ auth.user()?.name }}</h1>
          <p class="mt-1 text-sm text-blue-200">Platform overview across every client.</p>
        </div>
        <div class="flex gap-2">
          <a routerLink="/admin/clients" class="inline-flex items-center gap-1.5 rounded-xl bg-white px-4 py-2 text-sm font-medium text-indigo-700 shadow-sm transition hover:bg-blue-50"><app-icon name="building" size="h-4 w-4" />Clients</a>
          <a routerLink="/admin/users" class="inline-flex items-center gap-1.5 rounded-xl bg-white/10 px-4 py-2 text-sm font-medium ring-1 ring-white/25 transition hover:bg-white/20"><app-icon name="users" size="h-4 w-4" />Users</a>
          <button class="inline-flex items-center rounded-xl bg-white/10 px-3 py-2 ring-1 ring-white/25 transition hover:bg-white/20" (click)="load()" aria-label="Refresh"><app-icon name="refresh" size="h-4 w-4" /></button>
        </div>
      </div>
    </div>

    @if (rev() || pendingApps() !== null) {
      <div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        @if (pendingApps() !== null) {
          <a routerLink="/admin/applications" class="group rounded-2xl border bg-white p-5 transition hover:shadow-md dark:bg-slate-900" [class]="pendingApps() ? 'border-violet-300 ring-2 ring-violet-100 dark:border-violet-700 dark:ring-violet-900/40' : 'border-slate-200 dark:border-slate-800'">
            <div class="flex items-center justify-between"><span class="text-xs font-medium uppercase tracking-wide text-slate-500">New applications</span><app-icon name="building" size="h-5 w-5 text-violet-500" /></div>
            <div class="mt-2 text-3xl font-semibold tabular-nums">{{ pendingApps() }}</div>
            <div class="mt-1 text-sm font-medium text-violet-600">{{ pendingApps() ? 'Review and approve' : 'None waiting' }} →</div>
          </a>
        }
        @if (rev(); as r) {
          <a routerLink="/admin/payments" class="rounded-2xl border bg-white p-5 transition hover:shadow-md dark:bg-slate-900" [class]="r.counts.awaitingConfirmation ? 'border-amber-300 ring-2 ring-amber-100 dark:border-amber-700 dark:ring-amber-900/40' : 'border-slate-200 dark:border-slate-800'">
            <div class="flex items-center justify-between"><span class="text-xs font-medium uppercase tracking-wide text-slate-500">Payments to confirm</span><app-icon name="card" size="h-5 w-5 text-amber-500" /></div>
            <div class="mt-2 text-3xl font-semibold tabular-nums">{{ r.counts.awaitingConfirmation }}</div>
            <div class="mt-1 text-sm font-medium text-amber-600">Outstanding {{ fmt(r.outstanding) }}</div>
          </a>
          <a routerLink="/admin/revenue" class="rounded-2xl border border-slate-200 bg-linear-to-br from-emerald-50 to-white p-5 transition hover:shadow-md dark:border-slate-800 dark:from-emerald-900/20 dark:to-slate-900">
            <div class="flex items-center justify-between"><span class="text-xs font-medium uppercase tracking-wide text-slate-500">Recurring / month</span><app-icon name="chart" size="h-5 w-5 text-emerald-500" /></div>
            <div class="mt-2 truncate text-2xl font-semibold tabular-nums">{{ fmt(r.mrr) }}</div>
            <div class="mt-1 text-sm text-slate-500">{{ r.counts.paying }} paying · {{ r.counts.trial }} on trial</div>
          </a>
          <a routerLink="/admin/revenue" class="rounded-2xl border border-slate-200 bg-white p-5 transition hover:shadow-md dark:border-slate-800 dark:bg-slate-900">
            <div class="flex items-center justify-between"><span class="text-xs font-medium uppercase tracking-wide text-slate-500">Collected this month</span><app-icon name="receipt" size="h-5 w-5 text-sky-500" /></div>
            <div class="mt-2 truncate text-2xl font-semibold tabular-nums">{{ fmt(r.thisMonth) }}</div>
            <div class="mt-1 text-sm text-slate-500">Last month {{ fmt(r.lastMonth) }}</div>
          </a>
        }
      </div>
    }

    <app-notifications-panel class="mb-6 block" title="Platform notifications" [take]="8" />

    @if (d(); as d) {
      @if (d.attention?.length) {
        <section class="mb-6 rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900" aria-label="Needs attention">
          <h2 class="mb-3 text-sm font-semibold uppercase tracking-wide text-slate-500">Needs attention ({{ d.attention!.length }})</h2>
          <ul class="divide-y divide-slate-100 dark:divide-slate-800">
            @for (a of d.attention!; track $index) {
              <li class="flex flex-wrap items-center gap-3 py-2.5">
                <span class="h-2.5 w-2.5 shrink-0 rounded-full" [class]="a.severity === 'bad' ? 'bg-red-500' : a.severity === 'warn' ? 'bg-amber-500' : 'bg-sky-500'"></span>
                <div class="min-w-0 flex-1"><span class="font-medium">{{ a.clientName }}</span> <span class="text-slate-600 dark:text-slate-300">· {{ a.title }}</span><div class="text-xs text-slate-500">{{ a.detail }}</div></div>
                @if (a.route) { <a [routerLink]="'/' + a.route" class="text-sm font-medium text-brand-600 underline">Open</a> }
              </li>
            }
          </ul>
        </section>
      }
      @if (d.alerts.length) {
        <div class="mb-6 space-y-2">
          @for (a of d.alerts; track a) {
            <div class="flex items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-200"><app-icon name="alert" size="h-4 w-4 mt-0.5" /><span>{{ a }}</span></div>
          }
        </div>
      }

      <div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <app-stat-card label="Clients" [value]="d.clients" icon="building" tone="violet" [hint]="d.activeClients + ' active'" />
        <app-stat-card label="Users" [value]="d.users" icon="users" tone="blue" hint="across all clients" />
        <app-stat-card label="Devices" [value]="d.devices" icon="cpu" tone="emerald" [hint]="d.openDays + ' with an open fiscal day'" />
        <app-stat-card label="Receipts today" [value]="d.receiptsToday" icon="receipt" tone="amber" [hint]="d.pending + ' queued · ' + d.rejected + ' rejected (all time)'" />
      </div>

      <div class="mb-6 grid gap-6 xl:grid-cols-3">
        <div class="card xl:col-span-2">
          <div class="card-title">Receipts — last 7 days, all clients</div>
          <app-trend-chart [points]="d.trend" />
        </div>
        <div class="card">
          <div class="card-title">Busiest clients this week</div>
          <div class="space-y-4">
            @for (c of d.topClients; track c.id) {
              <div>
                <div class="mb-1 flex items-center justify-between text-sm"><span class="truncate font-medium">{{ c.name }}</span><span class="tabular-nums text-slate-500">{{ c.receiptsWeek }}</span></div>
                <div class="h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-slate-800"><div class="h-full rounded-full bg-linear-to-r from-blue-500 to-violet-500" [style.width.%]="pct(c.receiptsWeek)"></div></div>
                <div class="hint mt-1">{{ c.devices }} device(s)</div>
              </div>
            } @empty { <p class="text-sm text-slate-500">No clients yet. <a routerLink="/admin/clients" class="font-medium text-brand-600 underline">Create the first one.</a></p> }
          </div>
        </div>
      </div>

      <div class="card">
        <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0">Recent activity</div><a routerLink="/admin/audit" class="text-xs font-medium text-brand-600 hover:underline">Audit log</a></div>
        <ul class="divide-y divide-slate-100 dark:divide-slate-800">
          @for (a of d.recentActivity; track a.id) {
            <li class="flex items-start gap-3 py-2.5 text-sm">
              <span class="mt-1.5 h-2 w-2 shrink-0 rounded-full bg-blue-500"></span>
              <div class="min-w-0 flex-1"><span class="font-medium">{{ a.action }}</span> <span class="text-slate-500">{{ a.userName ?? 'system' }}{{ a.clientName ? ' · ' + a.clientName : '' }}</span>
                @if (a.detail) { <div class="truncate text-xs text-slate-500">{{ a.detail }}</div> }</div>
              <span class="whitespace-nowrap text-xs text-slate-500">{{ a.at | date:'short' }}</span>
            </li>
          } @empty { <li class="py-6 text-center text-sm text-slate-500">Nothing recorded yet.</li> }
        </ul>
      </div>
    } @else if (loading()) {
      <div class="grid animate-pulse gap-4 sm:grid-cols-2 xl:grid-cols-4"><div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div><div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div><div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div><div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div></div>
    }
  `,
})
export class OverviewComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly auth = inject(AuthService);
  readonly d = signal<PlatformDashboard | null>(null);
  readonly loading = signal(true);
  readonly rev = signal<Revenue | null>(null);
  readonly pendingApps = signal<number | null>(null);
  readonly fmt = money;
  readonly today = new Date();
  readonly hello = greeting();
  private readonly top = computed(() => Math.max(1, ...(this.d()?.topClients ?? []).map(c => c.receiptsWeek)));

  pct(n: number): number { return Math.max(4, (n / this.top()) * 100); }

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    this.loading.set(true);
    if (this.auth.can('revenue.view')) this.api.get<Revenue>('/api/v1/admin/revenue').then(r => this.rev.set(r), () => { /* the page works without it */ });
    if (this.auth.can('applications.manage')) this.api.get<unknown[]>('/api/v1/applications', { status: 'Pending' }).then(a => this.pendingApps.set(a.length), () => { /* ignore */ });
    try { this.d.set(await this.api.get<PlatformDashboard>('/api/v1/dashboard/platform')); }
    catch (e) { this.toast.err(e); }
    finally { this.loading.set(false); }
  }
}
