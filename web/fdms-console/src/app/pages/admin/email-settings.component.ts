import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { EmailSettings } from '../../core/models';
import { ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';

/** The mail server the gateway sends with (invoices, alerts, password resets) and who gets the platform's own alerts. The password is write-only. */
@Component({
  selector: 'app-email-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent],
  template: `
    <form class="card mt-6" id="email" (ngSubmit)="save()">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
        <div class="card-title mb-0! flex items-center gap-2"><app-icon name="mail" size="h-4 w-4" />Email</div>
        @if (s(); as s) {
          <span class="badge" [class.badge-ok]="s.configured" [class.badge-warn]="!s.configured">{{ s.configured ? 'Sending' : 'Not set up' }} · {{ s.source === 'database' ? 'set here' : s.source === 'config' ? 'from appsettings.json' : 'nothing set' }}</span>
        }
      </div>
      <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">The mail server used for invoices, alerts and password resets. What you save here replaces the settings in appsettings.json.
        The <strong>alert addresses</strong> get the platform's own notifications: new registrations, payments to confirm, go-live requests and failed backups (they also show in the bell).</p>

      <div class="grid gap-4 sm:grid-cols-2">
        <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" name="enabled" [(ngModel)]="f.enabled" />Send email</label>
        <label class="block"><span class="label">Mail server</span><input class="input font-mono" name="host" [(ngModel)]="f.host" placeholder="mail.yourcompany.com" autocomplete="off" /></label>
        <div class="grid grid-cols-2 gap-3">
          <label class="block"><span class="label">Port</span><input class="input" type="number" name="port" [(ngModel)]="f.port" min="1" max="65535" /></label>
          <label class="mt-6 flex items-center gap-2 text-sm"><input type="checkbox" name="ssl" [(ngModel)]="f.enableSsl" />Encrypted</label>
        </div>
        <label class="block"><span class="label">User name</span><input class="input" name="user" [(ngModel)]="f.user" autocomplete="off" /></label>
        <label class="block"><span class="label">Password</span>
          <input class="input" type="password" name="password" [(ngModel)]="f.password" autocomplete="new-password" [placeholder]="s()?.hasPassword ? '•••••••• (saved, leave empty to keep)' : ''" />
          @if (s()?.hasPassword) { <label class="mt-1 flex items-center gap-2 text-xs text-slate-500"><input type="checkbox" name="clear" [(ngModel)]="f.clearPassword" />Remove the saved password</label> }
        </label>
        <label class="block"><span class="label">Sender address</span><input class="input" type="email" name="from" [(ngModel)]="f.fromAddress" placeholder="noreply@yourcompany.com" /></label>
        <label class="block"><span class="label">Sender name</span><input class="input" name="fromName" [(ngModel)]="f.fromName" placeholder="FDMS Gateway" /></label>
        <label class="block sm:col-span-2"><span class="label">Alert addresses (your email)</span>
          <input class="input" name="notify" [(ngModel)]="f.notifyEmails" placeholder="you@yourcompany.com, finance@yourcompany.com" />
          <span class="hint">Up to 10, separated by commas. Empty: every platform administrator is emailed.</span></label>
      </div>
      <p class="hint mt-3">Port 587 uses STARTTLS, port 465 encrypts from the start. Use an app password if your provider asks for one.</p>

      <div class="mt-5 flex flex-wrap items-end justify-between gap-3 border-t border-slate-100 pt-4 dark:border-slate-800">
        <div class="flex flex-wrap items-end gap-2">
          <label class="block"><span class="label">Send a test to</span><input class="input w-64" type="email" name="testTo" [(ngModel)]="testTo" placeholder="you@yourcompany.com" /></label>
          <button type="button" class="btn-ghost" [disabled]="testing() || !testTo" (click)="test()"><app-icon name="send" size="h-4 w-4" />{{ testing() ? 'Sending…' : 'Send test' }}</button>
        </div>
        <button class="btn" type="submit" [disabled]="busy()">Save email settings</button>
      </div>
      @if (testResult(); as t) { <p class="mt-3 rounded-lg px-3 py-2 text-sm" [class]="t.ok ? 'bg-emerald-50 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-200' : 'bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-300'">{{ t.message }}</p> }
    </form>
  `,
})
export class EmailSettingsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly s = signal<EmailSettings | null>(null);
  readonly busy = signal(false);
  readonly testing = signal(false);
  readonly testResult = signal<{ ok: boolean; message: string } | null>(null);
  f = { enabled: true, host: '', port: 587, enableSsl: true, user: '', password: '', clearPassword: false, fromAddress: '', fromName: '', notifyEmails: '' };
  testTo = '';

  ngOnInit(): void { void this.load(); }

  private fill(s: EmailSettings): void {
    this.s.set(s);
    this.f = { enabled: s.enabled, host: s.host ?? '', port: s.port || 587, enableSsl: s.enableSsl, user: s.user ?? '', password: '', clearPassword: false, fromAddress: s.fromAddress ?? '', fromName: s.fromName ?? '', notifyEmails: s.notifyEmails ?? '' };
  }

  async load(): Promise<void> { try { this.fill(await this.api.get<EmailSettings>('/api/v1/settings/email')); } catch (e) { this.toast.err(e); } }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      this.fill(await this.api.put<EmailSettings>('/api/v1/settings/email', { ...this.f, port: Number(this.f.port) || 587, password: this.f.password || null }));
      this.toast.ok('Email settings saved. They apply at once.');
    } catch (e) { this.toast.err(e); }
    finally { this.busy.set(false); }
  }

  async test(): Promise<void> {
    this.testing.set(true); this.testResult.set(null);
    try { this.testResult.set(await this.api.post<{ ok: boolean; message: string }>('/api/v1/settings/email/test', { to: this.testTo })); }
    catch (e) { this.toast.err(e); }
    finally { this.testing.set(false); }
  }
}
