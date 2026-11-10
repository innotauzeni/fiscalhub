import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';

interface UsagePricing { charge: boolean; currency: string; smsPrice: number; whatsAppNumberMonthly: number; extraDeviceMonthly: number; extraUserMonthly: number; extraBranchMonthly: number; goLiveFee: number; liveDeviceMonthly: number; }

/** The owner's prices for usage and add-ons. They can change at any time; a change applies from each company's next invoice. */
@Component({
  selector: 'app-usage-pricing',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent],
  template: `
    <form class="card mt-6" (ngSubmit)="save()">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div class="card-title mb-0! flex items-center gap-2"><app-icon name="card" size="h-4 w-4" />Usage and add-on prices</div>
        <span class="badge" [class.badge-ok]="f.charge" [class.badge-warn]="!f.charge">{{ f.charge ? 'Charging' : 'Off' }}</span>
      </div>
      <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Added to each company's subscription invoice on top of its plan. Usage (SMS) is billed once, for what was used since the last invoice;
        monthly prices are charged for the period ahead. Each plan's price per extra receipt is set on the plan itself (above). Changes apply from the next invoice.</p>
      <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="charge" [(ngModel)]="f.charge" />Charge for usage and offer add-ons</label>
      <div class="mt-4 grid gap-4 sm:grid-cols-3">
        <label class="block"><span class="label">Currency</span><input class="input uppercase" maxlength="3" name="cur" [(ngModel)]="f.currency" /></label>
        <label class="block"><span class="label">Each SMS (sent through your SMS account)</span><input class="input" type="number" min="0" step="0.0001" name="sms" [(ngModel)]="f.smsPrice" /></label>
        <label class="block"><span class="label">Own WhatsApp number, per month</span><input class="input" type="number" min="0" step="0.01" name="wa" [(ngModel)]="f.whatsAppNumberMonthly" /></label>
        <label class="block"><span class="label">Extra device, per month</span><input class="input" type="number" min="0" step="0.01" name="dev" [(ngModel)]="f.extraDeviceMonthly" /></label>
        <label class="block"><span class="label">Extra user, per month</span><input class="input" type="number" min="0" step="0.01" name="usr" [(ngModel)]="f.extraUserMonthly" /></label>
        <label class="block"><span class="label">Extra branch, per month</span><input class="input" type="number" min="0" step="0.01" name="br" [(ngModel)]="f.extraBranchMonthly" /></label>
        <label class="block"><span class="label">Go-live fee (one-off, on approval)</span><input class="input" type="number" min="0" step="0.01" name="gl" [(ngModel)]="f.goLiveFee" /></label>
        <label class="block"><span class="label">Each live (Production) device, per month</span><input class="input" type="number" min="0" step="0.01" name="ld" [(ngModel)]="f.liveDeviceMonthly" /></label>
      </div>
      <p class="hint mt-3">Only companies whose plan is billed in this currency are charged. Set a price to 0 to stop charging for that item (an add-on priced 0 is not offered). Companies using their own SMS account are not charged per SMS.</p>
      <div class="mt-4 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Save prices</button></div>
    </form>
  `,
})
export class UsagePricingComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly busy = signal(false);
  f: UsagePricing = { charge: false, currency: 'USD', smsPrice: 0.05, whatsAppNumberMonthly: 10, extraDeviceMonthly: 5, extraUserMonthly: 2, extraBranchMonthly: 5, goLiveFee: 50, liveDeviceMonthly: 0 };

  ngOnInit(): void { void this.load(); }
  async load(): Promise<void> { try { this.f = await this.api.get<UsagePricing>('/api/v1/plans/usage-pricing'); } catch (e) { this.toast.err(e); } }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const n = (v: unknown) => Number(v) || 0;
      this.f = await this.api.put<UsagePricing>('/api/v1/plans/usage-pricing', { ...this.f, currency: this.f.currency.trim().toUpperCase(), smsPrice: n(this.f.smsPrice),
        whatsAppNumberMonthly: n(this.f.whatsAppNumberMonthly), extraDeviceMonthly: n(this.f.extraDeviceMonthly), extraUserMonthly: n(this.f.extraUserMonthly), extraBranchMonthly: n(this.f.extraBranchMonthly), goLiveFee: n(this.f.goLiveFee), liveDeviceMonthly: n(this.f.liveDeviceMonthly) });
      this.toast.ok('Prices saved. They apply from the next invoice.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
