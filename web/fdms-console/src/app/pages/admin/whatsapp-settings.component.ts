import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';

interface WhatsAppSettings { enabled: boolean; phoneNumberId: string | null; businessAccountId: string | null; displayNumber: string | null; hasAccessToken: boolean; hasAppSecret: boolean;
  verifyToken: string; webhookUrl: string; alertTemplate: string | null; alertTemplateLanguage: string; apiVersion: string; configured: boolean; }

/** The FiscalHub WhatsApp number (Meta Cloud API): staff chat with it to fiscalise, and alerts go out through it. Token and app secret are write-only. */
@Component({
  selector: 'app-whatsapp-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent],
  template: `
    <form class="card mt-6" (ngSubmit)="save()">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div class="card-title mb-0! flex items-center gap-2"><app-icon name="message" size="h-4 w-4" />WhatsApp assistant</div>
        @if (s(); as s) { <span class="badge" [class.badge-ok]="s.configured && s.enabled" [class.badge-warn]="!(s.configured && s.enabled)">{{ s.enabled && s.configured ? 'On' : 'Off' }}</span> }
      </div>
      <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Company staff link their own WhatsApp number in their profile, then issue fiscal invoices, check the day and get PDFs by chatting with this number. Their alerts can go out here too.
        Set it up in Meta's WhatsApp Cloud API (developers.facebook.com → your app → WhatsApp → API Setup).</p>
      @if (s(); as s) {
        <div class="mb-4 space-y-2 rounded-xl border border-slate-200 p-3 text-sm dark:border-slate-700">
          <div class="font-medium">Webhook (Meta → WhatsApp → Configuration)</div>
          <div class="flex items-center gap-2"><span class="w-28 shrink-0 text-xs text-slate-500">Callback URL</span><code class="code min-w-0 flex-1 truncate text-xs">{{ s.webhookUrl }}</code>
            <button type="button" class="icon-btn" (click)="copy(s.webhookUrl)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
          <div class="flex items-center gap-2"><span class="w-28 shrink-0 text-xs text-slate-500">Verify token</span><code class="code min-w-0 flex-1 truncate text-xs">{{ s.verifyToken }}</code>
            <button type="button" class="icon-btn" (click)="copy(s.verifyToken)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
          <p class="hint">Subscribe the webhook to the <strong>messages</strong> field.</p>
        </div>
      }
      <div class="grid gap-4 sm:grid-cols-2">
        <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" name="en" [(ngModel)]="f.enabled" />Switch the WhatsApp assistant on</label>
        <label class="block"><span class="label">WhatsApp number (shown to staff)</span><input class="input" name="num" [(ngModel)]="f.displayNumber" placeholder="+263 77 123 4567" /></label>
        <label class="block"><span class="label">Phone number ID</span><input class="input font-mono" name="pid" [(ngModel)]="f.phoneNumberId" placeholder="1234567890123" autocomplete="off" /></label>
        <label class="block"><span class="label">WhatsApp Business Account ID</span><input class="input font-mono" name="waba" [(ngModel)]="f.businessAccountId" autocomplete="off" /></label>
        <label class="block"><span class="label">Graph API version</span><input class="input font-mono" name="ver" [(ngModel)]="f.apiVersion" placeholder="v21.0" /></label>
        <label class="block"><span class="label">Permanent access token</span>
          <input class="input" type="password" name="tok" [(ngModel)]="f.accessToken" autocomplete="new-password" [placeholder]="s()?.hasAccessToken ? 'saved, leave empty to keep' : 'system user token with whatsapp_business_messaging'" /></label>
        <label class="block"><span class="label">App secret (signs the webhook)</span>
          <input class="input" type="password" name="sec" [(ngModel)]="f.appSecret" autocomplete="new-password" [placeholder]="s()?.hasAppSecret ? 'saved, leave empty to keep' : 'App settings → Basic → App secret'" /></label>
        <label class="block"><span class="label">Alert template name</span><input class="input font-mono" name="tpl" [(ngModel)]="f.alertTemplate" placeholder="fiscalhub_alert" /></label>
        <label class="block"><span class="label">Template language</span><input class="input font-mono" name="lang" [(ngModel)]="f.alertTemplateLanguage" placeholder="en" /></label>
      </div>
      <p class="hint mt-3">WhatsApp only lets a business write first, or more than 24 hours after the person's last message, with an approved template. Create a <em>Utility</em> template called fiscalhub_alert with body
        <code class="code">FiscalHub alert: {{ '{{' }}1{{ '}}' }}. {{ '{{' }}2{{ '}}' }}</code>. Within 24 hours of a chat, alerts are sent as normal messages.</p>
      <div class="mt-4 flex flex-wrap items-end justify-between gap-3 border-t border-slate-100 pt-4 dark:border-slate-800">
        <div class="flex flex-wrap items-end gap-2">
          <label class="block"><span class="label">Send a test to</span><input class="input w-52" name="to" [(ngModel)]="testTo" placeholder="+263 77 ..." /></label>
          <button type="button" class="btn-ghost" [disabled]="!testTo" (click)="test()"><app-icon name="send" size="h-4 w-4" />Send test</button>
        </div>
        <div class="flex items-center gap-2">
          <label class="flex items-center gap-2 text-xs text-slate-500"><input type="checkbox" name="rot" [(ngModel)]="f.rotateVerifyToken" />New verify token</label>
          <button class="btn" type="submit" [disabled]="busy()">Save WhatsApp settings</button>
        </div>
      </div>
    </form>
  `,
})
export class WhatsAppSettingsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly s = signal<WhatsAppSettings | null>(null);
  readonly busy = signal(false);
  testTo = '';
  f = { enabled: false, phoneNumberId: '', businessAccountId: '', displayNumber: '', accessToken: '', appSecret: '', alertTemplate: '', alertTemplateLanguage: 'en', apiVersion: 'v21.0', rotateVerifyToken: false };

  ngOnInit(): void { void this.load(); }

  private fill(s: WhatsAppSettings): void {
    this.s.set(s);
    this.f = { enabled: s.enabled, phoneNumberId: s.phoneNumberId ?? '', businessAccountId: s.businessAccountId ?? '', displayNumber: s.displayNumber ? '+' + s.displayNumber : '', accessToken: '', appSecret: '',
      alertTemplate: s.alertTemplate ?? '', alertTemplateLanguage: s.alertTemplateLanguage || 'en', apiVersion: s.apiVersion || 'v21.0', rotateVerifyToken: false };
  }

  async load(): Promise<void> { try { this.fill(await this.api.get<WhatsAppSettings>('/api/v1/admin/whatsapp')); } catch (e) { this.toast.err(e); } }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const f = this.f;
      this.fill(await this.api.put<WhatsAppSettings>('/api/v1/admin/whatsapp', { ...f, phoneNumberId: f.phoneNumberId || null, businessAccountId: f.businessAccountId || null, displayNumber: f.displayNumber || null,
        accessToken: f.accessToken || null, appSecret: f.appSecret || null, alertTemplate: f.alertTemplate || null }));
      this.toast.ok('WhatsApp settings saved.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async test(): Promise<void> {
    try { const r = await this.api.post<{ message: string }>('/api/v1/admin/whatsapp/test', { to: this.testTo }); this.toast.ok(r.message); } catch (e) { this.toast.err(e); }
  }

  async copy(t: string): Promise<void> { try { await navigator.clipboard.writeText(t); this.toast.ok('Copied.'); } catch { /* ignore */ } }
}
