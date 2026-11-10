import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { ToastService } from '../core/ui.services';
import { IconComponent } from './ui.components';

interface SmsProvider { key: string; label: string; fields: string[]; defaultUrl: string | null; help: string; }
interface SmsSettings { enabled: boolean; provider: string; apiUrl: string | null; username: string | null; hasApiKey: boolean; hasApiSecret: boolean; senderId: string | null;
  allowCompanies: boolean; configured: boolean; providers: SmsProvider[]; platformAvailable: boolean; }
interface SmsLogRow { id: string; to: string; kind: string; status: string; body: string; error: string | null; attempts: number; createdAt: string; sentAt: string | null; }

const LABELS: Record<string, string> = { apiKey: 'API key', apiSecret: 'Secret / password / auth token', username: 'User name / account ID', senderId: 'Sender ID', apiUrl: 'API address' };

/**
 * An SMS provider account (Nhume, Twilio, eSolutions, BulkSMS, Africa's Talking or any HTTPS address). Used for the platform's own account (Settings) and for a
 * company's own (Integration → WhatsApp & SMS). Secrets are write-only.
 */
@Component({
  selector: 'app-sms-account',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent],
  template: `
    <form class="card" (ngSubmit)="save()">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div class="card-title mb-0! flex items-center gap-2"><app-icon name="send" size="h-4 w-4" />{{ platform() ? 'SMS (platform account)' : 'SMS' }}</div>
        @if (s(); as s) { <span class="badge" [class.badge-ok]="s.configured" [class.badge-warn]="!s.configured">{{ s.configured ? 'On' : 'Off' }}</span> }
      </div>
      @if (platform()) {
        <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Sends sign-up and password-reset codes, platform alerts, and (if you allow it) invoices and alerts for companies that have no SMS account of their own. The provider bills this account.</p>
      } @else {
        <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Text fiscal invoices to your customers, and alerts and password codes to your staff, from your own SMS account and sender ID.
          @if (s()?.platformAvailable) { Until you set one up, SMS go out through the platform's account. } @else { Without one, SMS cannot be sent. }</p>
      }
      <div class="grid gap-4 sm:grid-cols-2">
        <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" name="en" [(ngModel)]="f.enabled" />Send SMS through this account</label>
        <label class="block sm:col-span-2"><span class="label">Provider</span>
          <select class="input" name="prov" [ngModel]="f.provider" (ngModelChange)="f.provider = $event; provider.set($event)">
            @for (p of s()?.providers ?? []; track p.key) { <option [value]="p.key">{{ p.label }}</option> }
          </select>
          @if (current(); as p) { <span class="hint">{{ p.help }}</span> }</label>
        @for (field of fields(); track field.key) {
          <label class="block">
            <span class="label">{{ field.label }}@if (field.optional) { <span class="text-slate-400"> (optional)</span> }</span>
            @switch (field.key) {
              @case ('apiKey') { <input class="input" type="password" name="key" [(ngModel)]="f.apiKey" autocomplete="new-password" [placeholder]="s()?.hasApiKey ? 'saved, leave empty to keep' : ''" /> }
              @case ('apiSecret') { <input class="input" type="password" name="secret" [(ngModel)]="f.apiSecret" autocomplete="new-password" [placeholder]="s()?.hasApiSecret ? 'saved, leave empty to keep' : ''" /> }
              @case ('username') { <input class="input" name="user" [(ngModel)]="f.username" autocomplete="off" /> }
              @case ('senderId') { <input class="input" name="sender" [(ngModel)]="f.senderId" maxlength="20" placeholder="FiscalHub" /> }
              @case ('apiUrl') { <input class="input font-mono text-xs" name="url" [(ngModel)]="f.apiUrl" [placeholder]="current()?.defaultUrl ?? 'https://'" /> }
            }
          </label>
        }
        @if (platform()) { <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" name="share" [(ngModel)]="f.allowCompanies" />Companies without their own SMS account may send through this one</label> }
      </div>
      <div class="mt-4 flex flex-wrap items-end justify-between gap-3 border-t border-slate-100 pt-4 dark:border-slate-800">
        <div class="flex flex-wrap items-end gap-2">
          <label class="block"><span class="label">Send a test to</span><input class="input w-52" name="to" [(ngModel)]="testTo" placeholder="+263 77 ..." /></label>
          <button type="button" class="btn-ghost" [disabled]="!testTo || busy()" (click)="test()"><app-icon name="send" size="h-4 w-4" />Send test</button>
        </div>
        <div class="flex items-center gap-2">
          <button type="button" class="btn-ghost" (click)="toggleLog()">{{ log() ? 'Hide' : 'Show' }} recent SMS</button>
          <button class="btn" type="submit" [disabled]="busy()">Save SMS settings</button>
        </div>
      </div>
      @if (log(); as rows) {
        <div class="mt-4 overflow-x-auto">
          <table class="table w-full text-sm">
            <thead><tr><th>When</th><th>To</th><th>What</th><th>Status</th><th>Text</th></tr></thead>
            <tbody>
              @for (r of rows; track r.id) {
                <tr><td class="whitespace-nowrap">{{ r.createdAt | date: 'd MMM, HH:mm' }}</td><td class="whitespace-nowrap">{{ r.to }}</td><td>{{ r.kind }}</td>
                  <td><span class="badge" [class.badge-ok]="r.status === 'Sent'" [class.badge-warn]="r.status !== 'Sent'">{{ r.status }}</span>
                    @if (r.error) { <div class="max-w-56 text-xs text-red-600 dark:text-red-400">{{ r.error }}</div> }</td>
                  <td class="max-w-80 truncate text-xs text-slate-500" [title]="r.body">{{ r.body }}</td></tr>
              } @empty { <tr><td colspan="5" class="py-6 text-center text-slate-500">No SMS yet.</td></tr> }
            </tbody>
          </table>
        </div>
      }
    </form>
  `,
})
export class SmsAccountComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  /** true: the platform's account (/api/v1/admin/sms); false: the signed-in company's (/api/v1/messaging/sms). */
  readonly platform = input(false);
  readonly s = signal<SmsSettings | null>(null);
  readonly busy = signal(false);
  readonly provider = signal('NHUME');
  readonly log = signal<SmsLogRow[] | null>(null);
  readonly current = computed(() => this.s()?.providers.find(p => p.key === this.provider()) ?? null);
  readonly fields = computed(() => (this.current()?.fields ?? []).map(f => ({ key: f.replace('?', ''), optional: f.endsWith('?'), label: LABELS[f.replace('?', '')] ?? f })));
  testTo = '';
  f = { enabled: false, provider: 'NHUME', apiUrl: '', username: '', apiKey: '', apiSecret: '', senderId: '', allowCompanies: true };

  private get base(): string { return this.platform() ? '/api/v1/admin/sms' : '/api/v1/messaging/sms'; }

  ngOnInit(): void { void this.load(); }

  private fill(s: SmsSettings): void {
    this.s.set(s); this.provider.set(s.provider);
    this.f = { enabled: s.enabled, provider: s.provider, apiUrl: s.apiUrl ?? '', username: s.username ?? '', apiKey: '', apiSecret: '', senderId: s.senderId ?? '', allowCompanies: s.allowCompanies };
  }

  async load(): Promise<void> { try { this.fill(await this.api.get<SmsSettings>(this.base)); } catch (e) { this.toast.err(e); } }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const f = this.f;
      this.fill(await this.api.put<SmsSettings>(this.base, { ...f, apiUrl: f.apiUrl.trim() || null, username: f.username.trim() || null, apiKey: f.apiKey || null, apiSecret: f.apiSecret || null, senderId: f.senderId.trim() || null }));
      this.toast.ok('SMS settings saved.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async test(): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.post<{ sent: boolean; message: string }>(this.base + '/test', { to: this.testTo });
      if (r.sent) this.toast.ok(r.message); else this.toast.err(new Error(r.message));
      if (this.log()) await this.loadLog();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async toggleLog(): Promise<void> { if (this.log()) this.log.set(null); else await this.loadLog(); }
  private async loadLog(): Promise<void> { try { this.log.set(await this.api.get<SmsLogRow[]>(this.base + '/log', { take: 50 })); } catch (e) { this.toast.err(e); } }
}
