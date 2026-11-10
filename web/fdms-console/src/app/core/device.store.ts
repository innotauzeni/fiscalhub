import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from './api.service';
import { Device } from './models';

/** The devices this user (or token) can see, and which one is currently selected across pages. */
@Injectable({ providedIn: 'root' })
export class DeviceStore {
  private readonly api = inject(Api);

  readonly devices = signal<Device[]>([]);
  readonly loaded = signal(false);
  readonly selectedId = signal<string>(this.read());
  readonly selected = computed<Device | null>(() => this.devices().find(d => d.id === this.selectedId()) ?? this.devices()[0] ?? null);

  private read(): string {
    try { return sessionStorage.getItem('fdms.device') ?? ''; } catch { return ''; }
  }

  select(id: string): void {
    this.selectedId.set(id);
    try { sessionStorage.setItem('fdms.device', id); } catch { /* ignore */ }
  }

  async load(force = false): Promise<Device[]> {
    if (this.loaded() && !force) return this.devices();
    const list = await this.api.get<Device[]>('/api/v1/devices');
    this.devices.set(list);
    this.loaded.set(true);
    if (!list.some(d => d.id === this.selectedId())) this.select(list[0]?.id ?? '');
    return list;
  }

  /** What people see in a list of machines: the company's own name when it has one ("Till 2, front counter"), always with ZIMRA's number and the branch. */
  label(d: Device): string {
    const where = [String(d.zimraDeviceId), d.assignedBranch].filter(Boolean).join(', ');
    return d.name ? `${d.name} (${where})` : `${d.zimraDeviceId} · ${d.taxPayerName ?? d.serialNo}${d.assignedBranch ? ' · ' + d.assignedBranch : ''} · ${d.environment}`;
  }
}
