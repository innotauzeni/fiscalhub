import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DeviceStore } from '../core/device.store';
import { OfflineFile } from '../core/models';
import { ToastService } from '../core/ui.services';
import { DeviceSelectComponent } from '../shared/device-select.component';
import { OfflineGuideComponent } from '../shared/offline-guide.component';
import { PageHeaderComponent, StatusComponent } from '../shared/ui.components';

@Component({
  selector: 'app-offline',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DeviceSelectComponent, OfflineGuideComponent, PageHeaderComponent, StatusComponent],
  template: `
    <app-page-header title="Offline files" subtitle="Devices ZIMRA has set to Offline mode issue receipts locally and hand them over in fiscal-day files.">
      <app-device-select mode="Offline" (changed)="load()" />
    </app-page-header>

    <app-offline-guide [open]="hasOffline() && !files().length" />

    @if (!store.devices().length || !hasOffline()) {
      <div class="card text-sm text-slate-500">None of your devices is in Offline mode. Online devices send each receipt to ZIMRA straight away, so there are no files.</div>
    } @else {
      <div class="card overflow-x-auto">
        <div class="mb-3 flex items-center justify-between">
          <div class="card-title mb-0">Files for the selected device</div>
          @if (auth.can('offline.manage')) { <button class="btn" (click)="sync()" [disabled]="busy()">Upload / check now</button> }
        </div>
        <table class="table">
          <thead><tr><th>Day</th><th>File</th><th>Receipts</th><th>Z report</th><th>Status</th><th>Uploaded</th><th>Problems</th></tr></thead>
          <tbody>
            @for (f of files(); track f.id) {
              <tr><td>{{ f.fiscalDayNo }}</td><td>{{ f.sequence }}</td><td>{{ f.receiptCount }}</td><td>{{ f.hasFooter ? 'yes' : '' }}</td><td><app-status [value]="f.status" /></td>
                <td class="whitespace-nowrap">{{ f.uploadedAt | date:'short' }}</td><td class="text-sm text-red-600">{{ f.errors.join(', ') || f.lastError }}</td></tr>
            } @empty { <tr><td colspan="7" class="text-slate-500">No files yet — they are created when a day is closed or synced.</td></tr> }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class OfflineComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(DeviceStore);
  readonly auth = inject(AuthService);
  readonly files = signal<OfflineFile[]>([]);
  readonly busy = signal(false);

  hasOffline(): boolean { return this.store.devices().some(d => d.operatingMode === 'Offline'); }

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); } catch (e) { this.toast.err(e); }
    const off = this.store.devices().find(d => d.operatingMode === 'Offline');
    if (off && this.store.selected()?.operatingMode !== 'Offline') this.store.select(off.id);
    await this.load();
  }

  async load(): Promise<void> {
    const d = this.store.selected();
    if (!d || d.operatingMode !== 'Offline') { this.files.set([]); return; }
    try { this.files.set(await this.api.get<OfflineFile[]>(`/api/v1/devices/${d.id}/offline-files`)); } catch (e) { this.toast.err(e); }
  }

  async sync(): Promise<void> {
    const d = this.store.selected(); if (!d) return;
    this.busy.set(true);
    try { this.files.set(await this.api.post<OfflineFile[]>(`/api/v1/devices/${d.id}/offline-files/sync`)); this.toast.ok('Files uploaded and checked.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
