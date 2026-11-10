import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { DeviceStore } from '../core/device.store';

@Component({
  selector: 'app-device-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <label class="block min-w-56">
      <span class="label">{{ label() }}</span>
      <select class="input" [value]="store.selected()?.id ?? ''" (change)="pick($any($event.target).value)" [disabled]="!store.devices().length">
        @for (d of filtered(); track d.id) { <option [value]="d.id">{{ store.label(d) }} ({{ d.operatingMode }})</option> }
        @empty { <option value="">No devices</option> }
      </select>
    </label>`,
})
export class DeviceSelectComponent {
  readonly store = inject(DeviceStore);
  readonly label = input('Device');
  /** Only list devices in this operating mode (e.g. 'Offline'). */
  readonly mode = input<string | null>(null);
  readonly changed = output<string>();

  filtered() { return this.mode() ? this.store.devices().filter(d => d.operatingMode === this.mode()) : this.store.devices(); }

  pick(id: string): void { this.store.select(id); this.changed.emit(id); }
}
