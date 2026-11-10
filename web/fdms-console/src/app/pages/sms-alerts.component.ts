import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { ToastService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';

interface SmsPrefs { available: boolean; phone: string | null; alerts: boolean; }

/** A person's SMS alerts: the same alerts as the bell, texted to the phone number on their profile. */
@Component({
  selector: 'app-sms-alerts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    @if (p(); as p) {
      @if (p.available) {
        <div class="card">
          <div class="card-title flex items-center gap-2"><app-icon name="send" size="h-4 w-4" />SMS alerts</div>
          <p class="text-sm text-slate-600 dark:text-slate-300">Get your alerts (a day that would not close, rejected receipts, invoices to pay…) by SMS{{ p.phone ? ' on ' + p.phone : '' }}. At most 10 a day.</p>
          @if (!p.phone) { <p class="hint mt-2">Add your phone number above first.</p> }
          <label class="mt-3 flex items-center gap-2 text-sm"><input type="checkbox" [checked]="p.alerts" [disabled]="busy() || (!p.phone && !p.alerts)" (change)="save($any($event.target).checked)" />Send my alerts by SMS</label>
        </div>
      }
    }
  `,
})
export class SmsAlertsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly p = signal<SmsPrefs | null>(null);
  readonly busy = signal(false);

  ngOnInit(): void { void this.load(); }
  async load(): Promise<void> { try { this.p.set(await this.api.get<SmsPrefs>('/api/v1/profile/sms')); } catch { /* the card stays hidden */ } }

  async save(alerts: boolean): Promise<void> {
    this.busy.set(true);
    try { this.p.set(await this.api.put<SmsPrefs>('/api/v1/profile/sms', { alerts })); this.toast.ok(alerts ? 'SMS alerts on.' : 'SMS alerts off.'); }
    catch (e) { this.toast.err(e); await this.load(); } finally { this.busy.set(false); }
  }
}
