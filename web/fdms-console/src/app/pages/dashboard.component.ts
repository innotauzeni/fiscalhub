import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { Dashboard } from '../core/models';
import { ToastService } from '../core/ui.services';
import { DonutComponent, DonutSegment, TrendChartComponent } from '../shared/charts.components';
import { FiscalDayPanelComponent } from '../shared/fiscal-day-panel.component';
import { GettingStartedComponent } from '../shared/getting-started.component';
import { NotificationsPanelComponent } from '../shared/notifications-panel.component';
import { StatCardComponent } from '../shared/modal.components';
import { IconComponent, StatusComponent } from '../shared/ui.components';

export function greeting(): string {
  const h = new Date().getHours();
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
}

@Component({
  selector: 'app-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, DecimalPipe, IconComponent, StatusComponent, StatCardComponent, TrendChartComponent, DonutComponent, FiscalDayPanelComponent, GettingStartedComponent, NotificationsPanelComponent],
  template: `
    <!-- hero -->
    <div class="relative mb-6 overflow-hidden rounded-3xl bg-linear-to-br from-blue-600 via-indigo-600 to-violet-600 p-6 text-white shadow-lg sm:p-8">
      <div class="pointer-events-none absolute -right-16 -top-16 h-64 w-64 rounded-full bg-white/10 blur-2xl"></div>
      <div class="pointer-events-none absolute -bottom-20 left-1/3 h-56 w-56 rounded-full bg-fuchsia-400/20 blur-3xl"></div>
      <div class="relative flex flex-wrap items-end justify-between gap-5">
        <div>
          <p class="text-sm text-blue-100">{{ today | date:'EEEE, d MMMM y' }}</p>
          <h1 class="mt-1 text-2xl font-semibold tracking-tight sm:text-3xl">{{ hello }}, {{ auth.user()?.name }}</h1>
          <p class="mt-1 text-sm text-blue-100">{{ auth.user()?.clientName }} · here is how your fiscalisation is doing.</p>
        </div>
        <div class="flex flex-wrap gap-2">
          @if (auth.can('receipts.submit')) { <a routerLink="/receipts/new" class="inline-flex items-center gap-1.5 rounded-xl bg-white px-4 py-2 text-sm font-medium text-indigo-700 shadow-sm transition hover:bg-blue-50"><app-icon name="plus" size="h-4 w-4" />New invoice</a> }
          @if (auth.can('receipts.import')) { <a routerLink="/receipts/import" class="inline-flex items-center gap-1.5 rounded-xl bg-white/15 px-4 py-2 text-sm font-medium text-white ring-1 ring-white/30 transition hover:bg-white/25"><app-icon name="upload" size="h-4 w-4" />Import</a> }
          @if (auth.can('day.view')) { <a routerLink="/days" class="inline-flex items-center gap-1.5 rounded-xl bg-white/15 px-4 py-2 text-sm font-medium text-white ring-1 ring-white/30 transition hover:bg-white/25"><app-icon name="calendar" size="h-4 w-4" />Fiscal day</a> }
          <button class="inline-flex items-center gap-1.5 rounded-xl bg-white/15 px-3 py-2 text-sm text-white ring-1 ring-white/30 transition hover:bg-white/25" (click)="load()" aria-label="Refresh"><app-icon name="refresh" size="h-4 w-4" /></button>
        </div>
      </div>
    </div>

    @if (d(); as d) {
      @if (d.alerts.length) {
        <div class="mb-6 space-y-2">
          @for (a of d.alerts; track a) {
            <div class="flex items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-200">
              <app-icon name="alert" size="h-4 w-4 mt-0.5" /><span>{{ a }}</span>
            </div>
          }
        </div>
      }

      <app-notifications-panel class="mb-6 block" title="Needs your attention" />

      <app-getting-started [checklist]="d.checklist" [usage]="d.usage" />

      <app-fiscal-day-panel [devices]="d.deviceList" [autoClose]="d.autoCloseTime" (changed)="load()" />

      <div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <app-stat-card label="Devices" [value]="d.devices" icon="cpu" tone="blue" [hint]="d.openDays + ' with an open fiscal day'" />
        <app-stat-card label="Receipts today" [value]="d.receiptsToday" icon="receipt" tone="emerald" [hint]="totals()" />
        <app-stat-card label="Waiting for ZIMRA" [value]="d.pending + d.offlineIssued" icon="cloud-upload" tone="amber" [hint]="d.pending + ' queued · ' + d.offlineIssued + ' issued offline'" />
        <app-stat-card label="Needs attention" [value]="d.rejected" icon="alert" tone="rose" [hint]="d.held + ' on hold · rejected by ZIMRA'" />
      </div>

      <div class="mb-6 grid gap-6 xl:grid-cols-3">
        <div class="card xl:col-span-2">
          <div class="mb-1 flex items-center justify-between"><div class="card-title mb-0">Receipts — last 7 days</div><span class="flex items-center gap-3 text-xs text-slate-500"><span class="flex items-center gap-1"><span class="h-2 w-2 rounded-full bg-blue-500"></span>issued</span><span class="flex items-center gap-1"><span class="h-2 w-2 rounded-full bg-rose-500"></span>rejected</span></span></div>
          <app-trend-chart [points]="d.trend" />
        </div>
        <div class="card">
          <div class="card-title">Receipts by status</div>
          <app-donut [segments]="segments()" />
        </div>
      </div>

      <div class="grid gap-6 xl:grid-cols-5">
        <div class="card xl:col-span-2">
          <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0">Devices</div>
            @if (auth.can('devices.view')) { <a routerLink="/devices" class="text-xs font-medium text-brand-600 hover:underline">View all</a> }</div>
          <div class="space-y-3">
            @for (x of d.deviceList; track x.id) {
              <div class="flex items-center gap-3 rounded-xl border border-slate-100 p-3 dark:border-slate-800">
                <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300"><app-icon name="cpu" /></div>
                <div class="min-w-0 flex-1">
                  <div class="truncate font-medium">Device {{ x.zimraDeviceId }}</div>
                  <div class="truncate text-xs text-slate-500">{{ x.taxPayerName }} {{ x.branchName }}</div>
                </div>
                <div class="text-right">
                  <app-status [value]="x.dayStatus" />
                  @if (x.dayNo) { <div class="mt-1 text-xs text-slate-500">day {{ x.dayNo }} · {{ x.lastReceiptCounter }} receipts</div> }
                </div>
              </div>
            } @empty {
              <div class="rounded-xl border border-dashed border-slate-300 p-6 text-center text-sm text-slate-500 dark:border-slate-700">No devices yet.
                @if (auth.can('devices.manage')) { <a class="ml-1 font-medium text-brand-600 underline" routerLink="/devices/register">Register one</a> }</div>
            }
          </div>
        </div>

        <div class="card xl:col-span-3 overflow-x-auto">
          <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0">Latest receipts</div>
            @if (auth.can('receipts.view')) { <a routerLink="/receipts" class="text-xs font-medium text-brand-600 hover:underline">View all</a> }</div>
          <table class="table">
            <thead><tr><th>Invoice</th><th class="text-right">Total</th><th>Status</th><th>When</th></tr></thead>
            <tbody>
              @for (r of d.recent; track r.id) {
                <tr><td><div class="font-medium">{{ r.invoiceNo }}</div><div class="hint mt-0">{{ r.type }}</div></td>
                  <td class="text-right tabular-nums">{{ r.total | number:'1.2-2' }} <span class="text-xs text-slate-500">{{ r.currency }}</span></td>
                  <td><app-status [value]="r.status" /></td><td class="whitespace-nowrap text-slate-500">{{ r.receiptDate | date:'short' }}</td></tr>
              } @empty { <tr><td colspan="4" class="py-8 text-center text-slate-500">No receipts yet.</td></tr> }
            </tbody>
          </table>
        </div>
      </div>
    } @else if (loading()) {
      <div class="grid animate-pulse gap-4 sm:grid-cols-2 xl:grid-cols-4"><div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div><div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div><div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div><div class="h-28 rounded-2xl bg-slate-200 dark:bg-slate-800"></div></div>
    }
  `,
})
export class DashboardComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly auth = inject(AuthService);
  readonly d = signal<Dashboard | null>(null);
  readonly loading = signal(true);
  readonly today = new Date();
  readonly hello = greeting();

  readonly totals = computed(() => {
    const t = this.d()?.todayTotals ?? [];
    return t.length ? t.map(x => `${x.total.toFixed(2)} ${x.currency}`).join(' · ') : 'nothing yet today';
  });

  readonly segments = computed<DonutSegment[]>(() => {
    const d = this.d();
    return d ? [
      { label: 'Fiscalised', value: d.submitted, stroke: 'stroke-emerald-500', dot: 'bg-emerald-500' },
      { label: 'Queued', value: d.pending, stroke: 'stroke-amber-500', dot: 'bg-amber-500' },
      { label: 'Issued offline', value: d.offlineIssued, stroke: 'stroke-sky-500', dot: 'bg-sky-500' },
      { label: 'On hold', value: d.held, stroke: 'stroke-violet-500', dot: 'bg-violet-500' },
      { label: 'Rejected', value: d.rejected, stroke: 'stroke-rose-500', dot: 'bg-rose-500' },
    ] : [];
  });

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    this.loading.set(true);
    try { this.d.set(await this.api.get<Dashboard>('/api/v1/dashboard')); }
    catch (e) { this.toast.err(e); }
    finally { this.loading.set(false); }
  }
}
