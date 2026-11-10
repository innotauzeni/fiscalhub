import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DeviceHealth, HealthCheck } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { EmptyComponent } from '../shared/list.components';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

/** "Is my device healthy?" in one page: a traffic light per check and, where there is one, the single button that fixes it. */
@Component({
  selector: 'app-health',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, PageHeaderComponent, EmptyComponent],
  template: `
    <app-page-header title="Device health" subtitle="Everything that can stop you invoicing, checked for you. Green is fine; amber and red come with a button to fix them.">
      <button class="btn" (click)="load(true)" [disabled]="busy() === 'all'"><app-icon name="refresh" size="h-4 w-4" />{{ busy() === 'all' ? 'Asking ZIMRA…' : 'Test connection and check' }}</button>
    </app-page-header>

    @for (d of devices(); track d.deviceId) {
      <section class="card mb-5">
        <div class="mb-4 flex flex-wrap items-center justify-between gap-3">
          <div class="flex items-center gap-3">
            <span class="flex h-11 w-11 items-center justify-center rounded-2xl text-white" [class]="d.status === 'ok' ? 'bg-emerald-500' : d.status === 'warn' ? 'bg-amber-500' : 'bg-red-500'">
              <app-icon [name]="d.status === 'ok' ? 'check' : 'alert'" size="h-6 w-6" /></span>
            <div><div class="text-lg font-semibold">{{ d.name || 'Device ' + d.zimraDeviceId }}</div>
              <div class="text-sm text-slate-500">ZIMRA {{ d.zimraDeviceId }} · {{ d.status === 'ok' ? 'Everything looks good' : d.status === 'warn' ? 'Needs a look' : 'Needs fixing now' }}</div></div>
          </div>
        </div>
        <ul class="divide-y divide-slate-100 dark:divide-slate-800">
          @for (c of d.checks; track c.id) {
            <li class="flex flex-wrap items-center gap-3 py-3">
              <span class="h-3 w-3 shrink-0 rounded-full" [class]="dot(c.status)" aria-hidden="true"></span>
              <div class="min-w-0 flex-1"><div class="text-sm font-medium">{{ c.title }}</div><div class="text-sm text-slate-600 dark:text-slate-400">{{ c.detail }}</div></div>
              @if (c.fixAction && canFix(c)) {
                <button class="btn-ghost btn-sm" (click)="fix(d, c)" [disabled]="busy() === d.deviceId + c.id">{{ busy() === d.deviceId + c.id ? 'Working…' : c.fixLabel }}</button>
              }
            </li>
          }
        </ul>
      </section>
    } @empty {
      <div class="card"><app-empty icon="cpu" title="No devices yet" hint="Register a device and its health appears here." /></div>
    }
  `,
})
export class HealthComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly router = inject(Router);
  readonly auth = inject(AuthService);

  readonly devices = signal<DeviceHealth[]>([]);
  readonly busy = signal<string | null>(null);
  private live = false;

  ngOnInit(): Promise<void> { return this.load(false); }

  dot(s: string): string { return s === 'ok' ? 'bg-emerald-500' : s === 'warn' ? 'bg-amber-500' : s === 'bad' ? 'bg-red-500' : 'bg-slate-300 dark:bg-slate-600'; }

  /** Buttons that change something need the right to manage; links to other pages are for everyone who can see them. */
  canFix(c: HealthCheck): boolean {
    switch (c.fixAction) {
      case 'refresh-config': case 'renew-certificate': return this.auth.can('devices.manage');
      case 'retry-pending': return this.auth.can('receipts.submit');
      case 'close-day': return this.auth.can('day.manage');
      default: return true;
    }
  }

  async load(live: boolean): Promise<void> {
    this.live = live || this.live;
    this.busy.set(live ? 'all' : null);
    try { this.devices.set(await this.api.get<DeviceHealth[]>('/api/v1/health', { live: this.live })); } catch (e) { this.toast.err(e); } finally { this.busy.set(null); }
  }

  async fix(d: DeviceHealth, c: HealthCheck): Promise<void> {
    const id = d.deviceId;
    switch (c.fixAction) {
      case 'open-fiscal-days': await this.router.navigateByUrl('/days'); return;
      case 'offline-files': await this.router.navigateByUrl('/offline'); return;
      case 'view-rejected': await this.router.navigateByUrl('/receipts'); return;
      case 'ping': await this.load(true); return;
    }
    if (c.fixAction === 'close-day' && !(await this.dialog.confirm('Close the fiscal day? This sends the Z report to ZIMRA and cannot be undone.', 'Close day', true))) return;
    this.busy.set(id + c.id);
    try {
      switch (c.fixAction) {
        case 'refresh-config': await this.api.post(`/api/v1/devices/${id}/refresh-config`); this.toast.ok('Settings refreshed from ZIMRA.'); break;
        case 'renew-certificate': await this.api.post(`/api/v1/devices/${id}/renew-certificate`); this.toast.ok('Certificate renewed.'); break;
        case 'retry-pending': {
          const r = await this.api.post<{ delivered: number; stillWaiting: number }>(`/api/v1/health/devices/${id}/retry-pending`);
          this.toast.ok(r.stillWaiting ? `${r.delivered} sent, ${r.stillWaiting} still waiting. Check the connection.` : `${r.delivered} receipt(s) sent to ZIMRA.`); break;
        }
        case 'close-day': await this.api.post(`/api/v1/devices/${id}/day/close`); this.toast.ok('Close requested.'); break;
      }
    } catch (e) { this.toast.err(e); }
    finally { this.busy.set(null); await this.load(false); }
  }
}
