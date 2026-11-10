import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ActivityPage, Client, DeviceActivity } from '../core/models';
import { ToastService } from '../core/ui.services';
import { EmptyComponent, PagerComponent } from '../shared/list.components';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

/**
 * What people and integrating systems are doing on the company's devices: a card per device (last seen, calls and errors today, receipts, live tokens)
 * and the full list of API calls behind it, so a misbehaving integration or an idle till is easy to spot.
 */
@Component({
  selector: 'app-activity',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, PageHeaderComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Activity" subtitle="Which devices are busy, which systems are calling them, and what went wrong. Times are shown in your own timezone.">
      <button class="btn-ghost" (click)="refresh()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    @if (!auth.user()?.clientId) {
      <div class="card mb-5"><label class="block max-w-sm"><span class="label">Company</span>
        <select class="input" [(ngModel)]="clientId" (change)="refresh()"><option value="">All companies</option>@for (c of clients(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }</select></label></div>
    }

    <h2 class="mb-2 text-sm font-semibold uppercase tracking-wide text-slate-500">Devices</h2>
    @if (devices().length) {
      <div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
        @for (d of devices(); track d.deviceId) {
          <button type="button" class="card text-left transition hover:border-blue-400" [class.ring-2]="deviceId === d.deviceId" [class.ring-blue-500]="deviceId === d.deviceId" (click)="pick(d.deviceId)">
            <div class="flex items-start justify-between gap-3">
              <div class="min-w-0"><div class="truncate font-semibold">{{ d.name || ('Device ' + d.zimraDeviceId) }}</div><div class="truncate text-xs text-slate-500">ZIMRA {{ d.zimraDeviceId }}@if (d.branch) { · {{ d.branch }} }</div></div>
              <span class="badge" [class]="seen(d).cls">{{ seen(d).text }}</span>
            </div>
            <div class="mt-4 grid grid-cols-3 gap-2 text-center">
              <div><div class="text-2xl font-bold tabular-nums">{{ d.calls24h }}</div><div class="text-[11px] uppercase tracking-wide text-slate-500">Calls today</div></div>
              <div><div class="text-2xl font-bold tabular-nums" [class.text-red-600]="d.errors24h > 0">{{ d.errors24h }}</div><div class="text-[11px] uppercase tracking-wide text-slate-500">Errors</div></div>
              <div><div class="text-2xl font-bold tabular-nums">{{ d.receipts24h }}</div><div class="text-[11px] uppercase tracking-wide text-slate-500">Receipts</div></div>
            </div>
            <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-slate-500">
              <span class="inline-flex items-center gap-1"><app-icon name="key" size="h-3.5 w-3.5" />{{ d.tokenCount }} live {{ d.tokenCount === 1 ? 'token' : 'tokens' }}</span>
              @if (d.lastTokenCallAt) { <span>System last called {{ d.lastTokenCallAt | date:'short' }}</span> } @else { <span>No system has called it yet</span> }
            </div>
          </button>
        }
      </div>
    } @else { <div class="card mb-6"><app-empty icon="cpu" title="No devices yet" hint="Register a device and give it an API token to see its activity here." /></div> }

    <div class="card">
      <div class="mb-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label class="block lg:col-span-2"><span class="label">Search</span><input class="input" placeholder="Path, token or person, address…" [(ngModel)]="search" (keydown.enter)="reload()" /></label>
        <label class="block"><span class="label">Device</span>
          <select class="input" [(ngModel)]="deviceId" (change)="reload()"><option value="">All devices</option>@for (d of devices(); track d.deviceId) { <option [value]="d.deviceId">Device {{ d.zimraDeviceId }}</option> }</select></label>
        <label class="block"><span class="label">Done by</span>
          <select class="input" [(ngModel)]="actorType" (change)="reload()"><option value="">Anyone</option><option value="user">People</option><option value="token">Integrations (API tokens)</option></select></label>
        <label class="block"><span class="label">Outcome</span>
          <select class="input" [(ngModel)]="outcome" (change)="reload()"><option value="">All</option><option value="ok">Succeeded</option><option value="errors">Failed or refused</option></select></label>
        <label class="block"><span class="label">From</span><input type="date" class="input" [(ngModel)]="from" (change)="reload()" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [(ngModel)]="to" (change)="reload()" /></label>
        <div class="flex items-end gap-2"><button class="btn" (click)="reload()">Search</button><button class="btn-ghost" (click)="clear()">Clear</button></div>
      </div>
      <div class="mb-3 flex items-center gap-3"><div class="card-title mb-0 flex items-center gap-2"><app-icon name="clipboard" size="h-4 w-4" />Calls</div><span class="badge">{{ page()?.total ?? 0 }}</span></div>
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>When</th><th>Who</th><th>Device</th><th>Call</th><th>Result</th><th>Time</th>@if (!auth.user()?.clientId) { <th>Company</th> }<th>From</th></tr></thead>
          <tbody>
            @for (a of page()?.items ?? []; track a.id) {
              <tr>
                <td class="whitespace-nowrap">{{ a.at | date:'medium' }}</td>
                <td><div class="flex items-center gap-2">
                  <span class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full" [class]="a.actorType === 'token' ? 'bg-violet-100 text-violet-700 dark:bg-violet-500/20 dark:text-violet-200' : 'bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-200'"><app-icon [name]="a.actorType === 'token' ? 'key' : 'users'" size="h-4 w-4" /></span>
                  <span class="min-w-0"><span class="block max-w-48 truncate font-medium">{{ a.actorName ?? 'Unknown' }}</span><span class="block text-xs text-slate-500">{{ a.actorType === 'token' ? 'Integration' : 'Person' }}</span></span></div></td>
                <td>@if (a.zimraDeviceId) { <span class="badge">{{ a.zimraDeviceId }}</span> } @else { <span class="text-slate-400">—</span> }</td>
                <td class="max-w-sm"><span class="code">{{ a.method }}</span> <span class="break-all font-mono text-xs text-slate-600 dark:text-slate-300">{{ a.path }}</span></td>
                <td><span class="badge" [class]="a.statusCode < 400 ? 'badge-ok' : a.statusCode < 500 ? 'badge-warn' : 'badge-bad'">{{ a.statusCode }}</span>@if (a.errorCode) { <span class="ml-1 font-mono text-[11px] text-slate-500">{{ a.errorCode }}</span> }</td>
                <td class="whitespace-nowrap text-xs tabular-nums text-slate-500">{{ a.durationMs }} ms</td>
                @if (!auth.user()?.clientId) { <td>{{ a.clientName }}</td> }
                <td class="whitespace-nowrap font-mono text-xs text-slate-500">{{ a.ipAddress }}</td>
              </tr>
            } @empty { <tr><td colspan="8"><app-empty icon="clipboard" title="No activity matches" hint="Change the filters, or make a call from your system." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="pageNo()" [pageSize]="size" [total]="page()?.total ?? 0" (pageChange)="go($event)" />
    </div>
  `,
})
export class ActivityComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly auth = inject(AuthService);

  readonly size = 25;
  readonly page = signal<ActivityPage | null>(null);
  readonly devices = signal<DeviceActivity[]>([]);
  readonly clients = signal<Client[]>([]);
  readonly pageNo = signal(1);
  search = ''; deviceId = ''; actorType = ''; outcome = ''; clientId = ''; from = ''; to = '';

  async ngOnInit(): Promise<void> {
    if (!this.auth.user()?.clientId) { try { this.clients.set(await this.api.get<Client[]>('/api/v1/clients')); } catch { /* list stays empty */ } }
    await this.refresh();
  }

  async refresh(): Promise<void> { await Promise.all([this.loadDevices(), this.load()]); }
  reload(): void { this.pageNo.set(1); void this.load(); }
  go(p: number): void { this.pageNo.set(p); void this.load(); }
  pick(id: string): void { this.deviceId = this.deviceId === id ? '' : id; this.reload(); }
  clear(): void { this.search = this.deviceId = this.actorType = this.outcome = this.from = this.to = ''; this.reload(); }

  private async loadDevices(): Promise<void> {
    try { this.devices.set(await this.api.get<DeviceActivity[]>('/api/v1/activity/devices', { clientId: this.clientId })); } catch (e) { this.toast.err(e); }
  }

  async load(): Promise<void> {
    // the date boxes are the viewer's local days; the API compares UTC moments (and counts the whole "to" day)
    const start = this.from ? new Date(this.from + 'T00:00:00') : null;
    const end = this.to ? new Date(this.to + 'T00:00:00') : null;
    try {
      this.page.set(await this.api.get<ActivityPage>('/api/v1/activity', {
        search: this.search, deviceId: this.deviceId, actorType: this.actorType, outcome: this.outcome, clientId: this.clientId,
        from: start?.toISOString(), to: end?.toISOString(), page: this.pageNo(), pageSize: this.size,
      }));
    } catch (e) { this.toast.err(e); }
  }

  /** "Seen" is about the last call of any kind: green within the hour, amber within a day, grey otherwise. */
  seen(d: DeviceActivity): { text: string; cls: string } {
    if (!d.lastSeenAt) return { text: 'Never used', cls: '' };
    const mins = (Date.now() - new Date(d.lastSeenAt).getTime()) / 60000;
    if (mins < 60) return { text: 'Active now', cls: 'badge-ok' };
    if (mins < 1440) return { text: `Seen ${Math.max(1, Math.round(mins / 60))} h ago`, cls: 'badge-warn' };
    return { text: `Seen ${Math.round(mins / 1440)} d ago`, cls: '' };
  }
}
