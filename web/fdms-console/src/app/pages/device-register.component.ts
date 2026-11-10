import { AuthService } from '../core/auth.service';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { Device } from '../core/models';
import { ToastService } from '../core/ui.services';
import { PageHeaderComponent } from '../shared/ui.components';

interface Taxpayer { taxPayerName: string; taxPayerTin: string; vatNumber?: string; deviceBranchName: string; }

@Component({
  selector: 'app-device-register',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, PageHeaderComponent],
  template: `
    <app-page-header title="Register a device" subtitle="Link a ZIMRA fiscal device to this gateway. Keys are generated here and stored encrypted." />
    <form class="card max-w-3xl" (ngSubmit)="register()">
      <div class="grid gap-4 sm:grid-cols-2">
        <div><label class="label" for="zid">Device ID</label><input id="zid" name="zid" type="number" class="input" [(ngModel)]="zimraDeviceId" required /></div>
        <div><label class="label" for="key">Activation key</label><input id="key" name="key" class="input uppercase" maxlength="8" [(ngModel)]="activationKey" required /></div>
        <div><label class="label" for="serial">Device serial number</label><input id="serial" name="serial" class="input" [(ngModel)]="serialNo" required /></div>
        <div><label class="label" for="env">Environment</label>
          <select id="env" name="env" class="input" [(ngModel)]="environment"><option value="Test">Test</option><option value="Production" [disabled]="auth.demoOnly()">Production{{ auth.demoOnly() ? " (needs a paid plan)" : "" }}</option></select></div>
        <div><label class="label" for="mn">Model name</label><input id="mn" name="mn" class="input" [(ngModel)]="modelName" required /></div>
        <div><label class="label" for="mv">Model version</label><input id="mv" name="mv" class="input" [(ngModel)]="modelVersion" required /></div>
      </div>
      <p class="hint">Model name and version must match what ZIMRA registered for this device.</p>

      @if (taxpayer(); as t) {
        <div class="mt-4 rounded-lg border border-emerald-200 bg-emerald-50 p-3 text-sm dark:border-emerald-900/50 dark:bg-emerald-900/20">
          <div class="font-medium">{{ t.taxPayerName }}</div>
          <div class="text-slate-600 dark:text-slate-300">TIN {{ t.taxPayerTin }} @if (t.vatNumber) { · VAT {{ t.vatNumber }} } · branch {{ t.deviceBranchName }}</div>
          <div class="hint">Check this is the right taxpayer before registering.</div>
        </div>
      }

      <div class="mt-5 flex flex-wrap gap-2">
        <button type="button" class="btn-ghost" (click)="verify()" [disabled]="busy() || !ready()">Check taxpayer</button>
        <button type="submit" class="btn" [disabled]="busy() || !ready()">{{ busy() ? 'Working…' : 'Register device' }}</button>
      </div>
    </form>
  `,
})
export class DeviceRegisterComponent {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly store = inject(DeviceStore);
  readonly auth = inject(AuthService);

  /** Opened from the go-live card: ZIMRA's live device details are filled in. */
  private readonly prefill = inject(ActivatedRoute).snapshot.queryParamMap;
  zimraDeviceId: number | null = this.prefill.get('zimraDeviceId') ? Number(this.prefill.get('zimraDeviceId')) : null;
  activationKey = this.prefill.get('activationKey') ?? ''; serialNo = this.prefill.get('serialNo') ?? ''; modelName = 'Server'; modelVersion = 'v1'; environment = this.prefill.get('environment') === 'Production' ? 'Production' : 'Test';
  readonly busy = signal(false);
  readonly taxpayer = signal<Taxpayer | null>(null);

  ready(): boolean { return !!this.zimraDeviceId && this.activationKey.length === 8 && !!this.serialNo && !!this.modelName && !!this.modelVersion; }

  private body() {
    return { zimraDeviceId: this.zimraDeviceId, activationKey: this.activationKey, serialNo: this.serialNo, modelName: this.modelName, modelVersion: this.modelVersion, environment: this.environment };
  }

  async verify(): Promise<void> {
    this.busy.set(true);
    try { this.taxpayer.set(await this.api.post<Taxpayer>('/api/v1/devices/verify', this.body())); }
    catch (e) { this.toast.err(e); this.taxpayer.set(null); }
    finally { this.busy.set(false); }
  }

  async register(): Promise<void> {
    this.busy.set(true);
    try {
      const d = await this.api.post<Device>('/api/v1/devices/register', this.body());
      await this.store.load(true);
      this.store.select(d.id);
      this.toast.ok(`Device ${d.zimraDeviceId} registered.`);
      await this.router.navigateByUrl('/devices');
    } catch (e) { this.toast.err(e); }
    finally { this.busy.set(false); }
  }
}
