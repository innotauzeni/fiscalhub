import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DeviceStore } from '../core/device.store';
import { Device } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';

@Component({
  selector: 'app-devices',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, RouterLink, DatePipe, PageHeaderComponent, StatusComponent],
  template: `
    <app-page-header title="Devices" subtitle="Fiscal devices registered with ZIMRA for your company.">
      @if (auth.can('devices.manage')) { <a class="btn" routerLink="/devices/register">Register device</a> }
    </app-page-header>

    <div class="grid gap-5 md:grid-cols-2 2xl:grid-cols-3">
      @for (d of store.devices(); track d.id) {
        <article class="card flex flex-col p-0 transition hover:shadow-md">
          <header class="flex items-start gap-3 border-b border-slate-100 p-5 dark:border-slate-800">
            <span class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-500/15"><app-icon name="cpu" size="h-6 w-6" /></span>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <h2 class="truncate text-base font-semibold">{{ d.name || ('Device ' + d.zimraDeviceId) }}</h2>
                @if (auth.can('devices.manage')) { <button class="icon-btn h-6 w-6 shrink-0" (click)="rename(d)" title="Give this machine a name" aria-label="Rename"><app-icon name="edit" size="h-3.5 w-3.5" /></button> }
              </div>
              <p class="truncate text-xs text-slate-500">ZIMRA {{ d.zimraDeviceId }} · {{ d.serialNo }}</p>
            </div>
            <app-status [value]="d.operatingMode" />
          </header>

          <div class="flex-1 space-y-4 p-5">
            <div>
              <div class="text-xs font-medium uppercase tracking-wide text-slate-400">Taxpayer</div>
              <div class="mt-0.5 text-sm font-medium">{{ d.taxPayerName || '—' }}</div>
            </div>
            <dl class="grid grid-cols-2 gap-x-4 gap-y-3 text-sm">
              <div><dt class="text-xs text-slate-500">TIN</dt><dd class="font-medium tabular-nums">{{ d.taxPayerTin || '—' }}</dd></div>
              <div><dt class="text-xs text-slate-500">VAT number</dt><dd class="font-medium tabular-nums">{{ d.vatNumber || '—' }}</dd></div>
              <div><dt class="text-xs text-slate-500">Branch</dt><dd>@if (d.assignedBranch) { <span class="badge badge-info">{{ d.assignedBranch }}</span> } @else { <span class="text-slate-400">Not assigned</span> }</dd></div>
              <div><dt class="text-xs text-slate-500">Environment</dt><dd><span class="badge" [class.badge-warn]="d.environment === 'Test'" [class.badge-ok]="d.environment !== 'Test'">{{ d.environment }}</span></dd></div>
            </dl>
            <div class="flex items-center justify-between rounded-xl bg-slate-50 px-3 py-2.5 text-sm dark:bg-slate-800/50">
              <span class="flex items-center gap-2 text-slate-600 dark:text-slate-300"><app-icon name="shield" size="h-4 w-4" />Certificate valid till</span>
              <span class="flex items-center gap-2 font-medium"><span class="whitespace-nowrap">{{ d.certificateValidTill | date:'mediumDate' }}</span>
                @if (daysLeft(d); as n) { @if (n.left < 30) { <span class="badge" [class.badge-bad]="n.left < 7" [class.badge-warn]="n.left >= 7">{{ n.left < 0 ? 'expired' : n.left + ' days' }}</span> } }</span>
            </div>
          </div>

          <footer class="space-y-2 border-t border-slate-100 p-4 dark:border-slate-800">
            <div class="flex flex-wrap gap-2">
              @if (auth.can('day.view')) { <button class="btn btn-sm" (click)="go('/days', d)"><app-icon name="calendar" size="h-4 w-4" />Fiscal day</button> }
              @if (auth.can('tokens.manage')) { <button class="btn-ghost btn-sm" (click)="go('/tokens', d)"><app-icon name="key" size="h-4 w-4" />API tokens</button> }
              <button class="btn-ghost btn-sm" (click)="taxes(d)">Taxes</button>
              <button class="btn-ghost btn-sm" (click)="ping(d)"><app-icon name="pulse" size="h-4 w-4" />Ping</button>
            </div>
            @if (auth.can('devices.manage')) {
              <div class="flex flex-wrap gap-2 border-t border-dashed border-slate-100 pt-2 dark:border-slate-800">
                <button class="btn-ghost btn-sm" (click)="refresh(d)"><app-icon name="refresh" size="h-4 w-4" />Refresh config</button>
                <button class="btn-ghost btn-sm" (click)="renew(d)">Renew certificate</button>
              </div>
            }
          </footer>
        </article>
      } @empty {
        <div class="card md:col-span-2 2xl:col-span-3 text-center text-slate-500">{{ store.loaded() ? 'No devices yet.' : 'Loading…' }}</div>
      }
    </div>

    @if (info(); as i) {
      <div class="card mt-5">
        <div class="card-title flex items-center justify-between">{{ i.title }}<button class="btn-ghost btn-sm" (click)="info.set(null)">Close</button></div>
        <pre class="max-h-72 overflow-auto rounded-lg bg-slate-50 p-3 text-xs dark:bg-slate-950">{{ i.text }}</pre>
      </div>
    }
  `,
})
export class DevicesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(DeviceStore);
  readonly auth = inject(AuthService);
  readonly info = signal<{ title: string; text: string } | null>(null);

  async ngOnInit(): Promise<void> { try { await this.store.load(true); } catch (e) { this.toast.err(e); } }

  /** Whole days until the certificate expires (negative once it has). */
  daysLeft(d: Device): { left: number } | null {
    const t = d.certificateValidTill ? new Date(d.certificateValidTill).getTime() : NaN;
    return Number.isNaN(t) ? null : { left: Math.floor((t - Date.now()) / 86_400_000) };
  }

  go(url: string, d: Device): void { this.store.select(d.id); void this.router.navigateByUrl(url); }

  taxes(d: Device): void {
    this.info.set({ title: `Taxes configured for device ${d.zimraDeviceId}`,
      text: d.taxes.map(t => `#${t.taxId}  ${t.taxName}  ${t.taxPercent === undefined ? 'exempt' : t.taxPercent + '%'}`).join('\n') || 'No taxes loaded - use Refresh config.' });
  }

  async ping(d: Device): Promise<void> {
    try { const r = await this.api.post<{ operationId: string; reportingFrequency?: number }>(`/api/v1/devices/${d.id}/ping`); this.toast.ok(`ZIMRA answered (operation ${r.operationId}).`); }
    catch (e) { this.toast.err(e); }
  }

  async refresh(d: Device): Promise<void> {
    try { await this.api.post(`/api/v1/devices/${d.id}/refresh-config`); await this.store.load(true); this.toast.ok('Configuration refreshed from ZIMRA.'); }
    catch (e) { this.toast.err(e); }
  }

  async rename(d: Device): Promise<void> {
    const name = await this.dialog.prompt(`A name for device ${d.zimraDeviceId}, so you can tell the machines of a branch apart (for example “Till 2, front counter”). Leave empty to remove the name.`, d.name ?? 'Till 1, front counter', 'Save name');
    if (name === null) return;
    try { await this.api.put(`/api/v1/devices/${d.id}/name`, { name: name.trim() || null }); await this.store.load(true); this.toast.ok(name.trim() ? 'Name saved.' : 'Name removed.'); } catch (e) { this.toast.err(e); }
  }

  async renew(d: Device): Promise<void> {
    if (!(await this.dialog.confirm(`Request a new certificate for device ${d.zimraDeviceId}? The gateway does this automatically 30 days before expiry.`, 'Renew'))) return;
    try { await this.api.post(`/api/v1/devices/${d.id}/renew-certificate`); await this.store.load(true); this.toast.ok('Certificate renewed.'); }
    catch (e) { this.toast.err(e); }
  }
}
