import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { DialogService, ToastService } from '../core/ui.services';
import { SmsAccountComponent } from '../shared/sms-account.component';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

interface CompanyWhatsApp { enabled: boolean; phoneNumberId: string | null; businessAccountId: string | null; displayNumber: string | null; hasAccessToken: boolean; hasAppSecret: boolean;
  verifyToken: string; webhookUrl: string; alertTemplate: string | null; alertTemplateLanguage: string; apiVersion: string; customersEnabled: boolean; customerGreeting: string | null;
  configured: boolean; platformNumberAvailable: boolean; platformNumber: string | null; }

/**
 * A company's own messaging: its WhatsApp number (Meta Cloud API) for the FiscalHub assistant, its alerts and its customers; and its SMS provider account.
 */
@Component({
  selector: 'app-messaging',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, PageHeaderComponent, SmsAccountComponent],
  template: `
    <app-page-header title="WhatsApp & SMS" subtitle="Your own WhatsApp number for the assistant and your customers, and your own SMS account." />

    <form class="card mb-6" (ngSubmit)="save()">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <div class="card-title mb-0! flex items-center gap-2"><app-icon name="message" size="h-4 w-4" />Your WhatsApp number</div>
        @if (w(); as w) { <span class="badge" [class.badge-ok]="w.configured && w.enabled" [class.badge-warn]="!(w.configured && w.enabled)">{{ w.enabled && w.configured ? 'On' : 'Off' }}</span> }
      </div>
      <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Connect your business's WhatsApp number and FiscalHub answers on it:</p>
      <ul class="mb-4 list-disc space-y-1 pl-5 text-sm text-slate-600 dark:text-slate-300">
        <li><strong>Your staff</strong> link their WhatsApp (Profile → WhatsApp) and issue fiscal invoices, check the day and approve requests by chatting.</li>
        <li><strong>Your alerts</strong> go out from your number.</li>
        <li><strong>Your customers</strong> (if you allow it) message the number to get copies of their own fiscal invoices. They are matched by the phone number on the invoice, so nobody sees anyone else's.</li>
      </ul>
      @if (w()?.platformNumberAvailable && !w()?.configured) { <p class="mb-4 rounded-lg bg-sky-50 px-3 py-2 text-sm text-sky-800 dark:bg-sky-900/25 dark:text-sky-200">Until your number is connected, your staff use the FiscalHub number +{{ w()?.platformNumber }}.</p> }

      <details class="mb-4 rounded-xl border border-slate-200 p-3 text-sm dark:border-slate-700">
        <summary class="cursor-pointer font-medium">How to connect your number (about 20 minutes)</summary>
        <ol class="mt-2 list-decimal space-y-1 pl-5 text-slate-600 dark:text-slate-300">
          <li>At <a class="text-brand-600 underline" href="https://developers.facebook.com/apps" target="_blank" rel="noopener">developers.facebook.com</a> create an app (type <em>Business</em>) and add <strong>WhatsApp</strong>.</li>
          <li>Under <strong>WhatsApp → API Setup</strong>, add your business phone number and verify it. Copy the <strong>Phone number ID</strong> and the <strong>WhatsApp Business Account ID</strong>.</li>
          <li>In Meta Business settings, create a <strong>system user</strong>, give it the app and the WhatsApp account, and generate a <strong>permanent token</strong> with <code class="code">whatsapp_business_messaging</code>.</li>
          <li>Under <strong>App settings → Basic</strong>, copy the <strong>App secret</strong>.</li>
          <li>Under <strong>WhatsApp → Configuration</strong>, paste the callback URL and verify token below, then subscribe to <strong>messages</strong>.</li>
          <li>Fill in the form, tick <em>Switch my number on</em>, save, and send a test.</li>
        </ol>
      </details>

      @if (w(); as w) {
        <div class="mb-4 space-y-2 rounded-xl border border-slate-200 p-3 text-sm dark:border-slate-700">
          <div class="font-medium">Webhook (Meta → WhatsApp → Configuration)</div>
          <div class="flex items-center gap-2"><span class="w-28 shrink-0 text-xs text-slate-500">Callback URL</span><code class="code min-w-0 flex-1 truncate text-xs">{{ w.webhookUrl }}</code>
            <button type="button" class="icon-btn" (click)="copy(w.webhookUrl)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
          <div class="flex items-center gap-2"><span class="w-28 shrink-0 text-xs text-slate-500">Verify token</span><code class="code min-w-0 flex-1 truncate text-xs">{{ w.verifyToken }}</code>
            <button type="button" class="icon-btn" (click)="copy(w.verifyToken)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
        </div>
      }

      <div class="grid gap-4 sm:grid-cols-2">
        <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" name="en" [(ngModel)]="f.enabled" />Switch my number on</label>
        <label class="block"><span class="label">WhatsApp number</span><input class="input" name="num" [(ngModel)]="f.displayNumber" placeholder="+263 77 123 4567" /></label>
        <label class="block"><span class="label">Phone number ID</span><input class="input font-mono" name="pid" [(ngModel)]="f.phoneNumberId" placeholder="1234567890123" autocomplete="off" /></label>
        <label class="block"><span class="label">WhatsApp Business Account ID</span><input class="input font-mono" name="waba" [(ngModel)]="f.businessAccountId" autocomplete="off" /></label>
        <label class="block"><span class="label">Graph API version</span><input class="input font-mono" name="ver" [(ngModel)]="f.apiVersion" placeholder="v21.0" /></label>
        <label class="block"><span class="label">Permanent access token</span>
          <input class="input" type="password" name="tok" [(ngModel)]="f.accessToken" autocomplete="new-password" [placeholder]="w()?.hasAccessToken ? 'saved, leave empty to keep' : ''" /></label>
        <label class="block"><span class="label">App secret</span>
          <input class="input" type="password" name="sec" [(ngModel)]="f.appSecret" autocomplete="new-password" [placeholder]="w()?.hasAppSecret ? 'saved, leave empty to keep' : ''" /></label>
        <label class="block"><span class="label">Alert template name (optional)</span><input class="input font-mono" name="tpl" [(ngModel)]="f.alertTemplate" placeholder="fiscalhub_alert" /></label>
        <label class="block"><span class="label">Template language</span><input class="input font-mono" name="lang" [(ngModel)]="f.alertTemplateLanguage" placeholder="en" /></label>
        <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" name="cust" [(ngModel)]="f.customersEnabled" />Let my customers get copies of their invoices on this number</label>
        @if (f.customersEnabled) {
          <label class="block sm:col-span-2"><span class="label">Greeting for customers (optional)</span>
            <input class="input" name="greet" [(ngModel)]="f.customerGreeting" maxlength="300" placeholder="Welcome to Moyo Hardware on WhatsApp." /></label>
        }
      </div>
      <p class="hint mt-3">Alerts more than 24 hours after a person's last message need an approved template (category Utility, body <code class="code">FiscalHub alert: {{ '{{' }}1{{ '}}' }}. {{ '{{' }}2{{ '}}' }}</code>). Meta charges your account for conversations.</p>
      <div class="mt-4 flex flex-wrap items-end justify-between gap-3 border-t border-slate-100 pt-4 dark:border-slate-800">
        <div class="flex flex-wrap items-end gap-2">
          <label class="block"><span class="label">Send a test to</span><input class="input w-52" name="to" [(ngModel)]="testTo" placeholder="+263 77 ..." /></label>
          <button type="button" class="btn-ghost" [disabled]="!testTo" (click)="test()"><app-icon name="send" size="h-4 w-4" />Send test</button>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <label class="flex items-center gap-2 text-xs text-slate-500"><input type="checkbox" name="rot" [(ngModel)]="f.rotateVerifyToken" />New verify token</label>
          @if (w()?.hasAccessToken) { <button type="button" class="btn-ghost text-red-600" (click)="remove()">Disconnect</button> }
          <button class="btn" type="submit" [disabled]="busy()">Save</button>
        </div>
      </div>
    </form>

    <app-sms-account />
  `,
})
export class MessagingComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly w = signal<CompanyWhatsApp | null>(null);
  readonly busy = signal(false);
  testTo = '';
  f = { enabled: false, phoneNumberId: '', businessAccountId: '', displayNumber: '', accessToken: '', appSecret: '', alertTemplate: '', alertTemplateLanguage: 'en', apiVersion: 'v21.0',
    rotateVerifyToken: false, customersEnabled: true, customerGreeting: '' };

  ngOnInit(): void { void this.load(); }

  private fill(w: CompanyWhatsApp): void {
    this.w.set(w);
    this.f = { enabled: w.enabled, phoneNumberId: w.phoneNumberId ?? '', businessAccountId: w.businessAccountId ?? '', displayNumber: w.displayNumber ? '+' + w.displayNumber : '', accessToken: '', appSecret: '',
      alertTemplate: w.alertTemplate ?? '', alertTemplateLanguage: w.alertTemplateLanguage || 'en', apiVersion: w.apiVersion || 'v21.0', rotateVerifyToken: false,
      customersEnabled: w.customersEnabled, customerGreeting: w.customerGreeting ?? '' };
  }

  async load(): Promise<void> { try { this.fill(await this.api.get<CompanyWhatsApp>('/api/v1/messaging/whatsapp')); } catch (e) { this.toast.err(e); } }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const f = this.f;
      this.fill(await this.api.put<CompanyWhatsApp>('/api/v1/messaging/whatsapp', { ...f, phoneNumberId: f.phoneNumberId.trim() || null, businessAccountId: f.businessAccountId.trim() || null,
        displayNumber: f.displayNumber.trim() || null, accessToken: f.accessToken || null, appSecret: f.appSecret || null, alertTemplate: f.alertTemplate.trim() || null, customerGreeting: f.customerGreeting.trim() || null }));
      this.toast.ok('WhatsApp settings saved.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async test(): Promise<void> {
    try { const r = await this.api.post<{ message: string }>('/api/v1/messaging/whatsapp/test', { to: this.testTo }); this.toast.ok(r.message); } catch (e) { this.toast.err(e); }
  }

  async remove(): Promise<void> {
    if (!await this.dialog.confirm('Disconnect your WhatsApp number? The assistant stops answering on it and your staff go back to the FiscalHub number. Your Meta account is not changed.', 'Disconnect', true)) return;
    try { this.fill(await this.api.delete<CompanyWhatsApp>('/api/v1/messaging/whatsapp')); this.toast.ok('Disconnected.'); } catch (e) { this.toast.err(e); }
  }

  async copy(t: string): Promise<void> { try { await navigator.clipboard.writeText(t); this.toast.ok('Copied.'); } catch { /* ignore */ } }
}
