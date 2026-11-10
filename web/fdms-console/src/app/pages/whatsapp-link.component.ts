import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DeviceStore } from '../core/device.store';
import { DialogService, ToastService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';

interface WhatsAppLink { available: boolean; botNumber: string | null; linkedNumber: string | null; linkedAt: string | null; alerts: boolean; deviceId: string | null; code: string | null; codeExpiresAt: string | null; waLink: string | null; }

/** Link your own WhatsApp number to FiscalHub: then issue invoices, check the day and get PDFs by chatting with the FiscalHub number, and receive your alerts there. */
@Component({
  selector: 'app-whatsapp-link',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent],
  template: `
    @if (w(); as w) {
      @if (w.available || w.linkedNumber) {
        <div class="card">
          <div class="card-title flex items-center gap-2"><app-icon name="message" size="h-4 w-4" />WhatsApp</div>
          @if (w.linkedNumber) {
            <p class="text-sm">Linked to <strong>+{{ w.linkedNumber }}</strong> since {{ w.linkedAt | date: 'd MMM y' }}. Message <strong>+{{ w.botNumber }}</strong> and say <em>hi</em> to issue invoices, check today's sales or the fiscal day, and get PDFs.</p>
            <div class="mt-4 grid gap-3 sm:grid-cols-2">
              <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="alerts" [(ngModel)]="alerts" (change)="save()" />Send my alerts to WhatsApp too</label>
              @if (devices.devices().length > 1) {
                <label class="block"><span class="label">Device for my WhatsApp sales</span>
                  <select class="input" name="dev" [(ngModel)]="deviceId" (change)="save()">
                    <option [ngValue]="null">Ask me each time</option>
                    @for (d of devices.devices(); track d.id) { <option [ngValue]="d.id">{{ devices.label(d) }}</option> }
                  </select></label>
              }
            </div>
            <div class="mt-4 flex flex-wrap gap-2">
              @if (chat(w); as url) { <a class="btn" [href]="url" target="_blank" rel="noopener"><app-icon name="message" size="h-4 w-4" />Open the chat</a> }
              <button type="button" class="btn-ghost text-red-600" (click)="unlink()">Unlink this number</button>
            </div>
          } @else {
            <p class="text-sm text-slate-600 dark:text-slate-300">Issue fiscal invoices, check the fiscal day and get PDFs by chatting with FiscalHub on WhatsApp, and receive your alerts there.</p>
            @if (w.code) {
              <div class="mt-4 rounded-xl bg-emerald-50 p-4 text-sm dark:bg-emerald-900/25">
                <p>On the phone with your WhatsApp, tap the button (or send <strong class="font-mono">LINK {{ w.code }}</strong> to <strong>+{{ w.botNumber }}</strong>). The code works for 10 minutes.</p>
                <div class="mt-3 flex flex-wrap items-center gap-2">
                  <a class="btn" [href]="w.waLink" target="_blank" rel="noopener"><app-icon name="send" size="h-4 w-4" />Send the code on WhatsApp</a>
                  <button type="button" class="btn-ghost" (click)="refresh()">I've sent it</button>
                </div>
              </div>
            } @else {
              <button type="button" class="btn mt-4" [disabled]="busy()" (click)="start()"><app-icon name="link" size="h-4 w-4" />Link WhatsApp</button>
            }
          }
        </div>
      }
    }
  `,
})
export class WhatsAppLinkComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly auth = inject(AuthService);
  readonly devices = inject(DeviceStore);
  readonly w = signal<WhatsAppLink | null>(null);
  readonly busy = signal(false);
  alerts = true;
  deviceId: string | null = null;

  async ngOnInit(): Promise<void> {
    await this.refresh();
    if (this.auth.can('devices.view')) void this.devices.load().catch(() => undefined);
  }

  private fill(w: WhatsAppLink): void { this.w.set(w); this.alerts = w.alerts; this.deviceId = w.deviceId; }
  chat(w: WhatsAppLink): string | null { return w.botNumber ? `https://wa.me/${w.botNumber}?text=hi` : null; }

  async refresh(): Promise<void> {
    try {
      const before = this.w()?.code ?? null;
      const w = await this.api.get<WhatsAppLink>('/api/v1/profile/whatsapp');
      // keep the code on screen until the number is linked
      this.fill(!w.linkedNumber && before ? { ...w, code: before, waLink: this.w()?.waLink ?? null } : w);
      if (w.linkedNumber && before) this.toast.ok('WhatsApp is linked. Say hi to FiscalHub on WhatsApp.');
    } catch { this.w.set(null); }
  }

  async start(): Promise<void> {
    this.busy.set(true);
    try { this.fill(await this.api.post<WhatsAppLink>('/api/v1/profile/whatsapp/link')); } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async save(): Promise<void> {
    try { this.fill(await this.api.put<WhatsAppLink>('/api/v1/profile/whatsapp', { alerts: this.alerts, deviceId: this.deviceId })); this.toast.ok('Saved.'); } catch (e) { this.toast.err(e); }
  }

  async unlink(): Promise<void> {
    if (!(await this.dialog.confirm('Unlink this WhatsApp number? The assistant will stop answering it.', 'Unlink', true))) return;
    try { this.fill(await this.api.delete<WhatsAppLink>('/api/v1/profile/whatsapp')); } catch (e) { this.toast.err(e); }
  }
}
