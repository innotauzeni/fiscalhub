import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { appUrl } from '../../core/env';
import { ToastService } from '../../core/ui.services';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';
import { BillingProfileComponent } from './billing-profile.component';
import { GatewaysComponent } from './gateways.component';
import { EmailSettingsComponent } from './email-settings.component';
import { AppLinksSettingsComponent } from './app-links-settings.component';
import { WhatsAppSettingsComponent } from './whatsapp-settings.component';
import { SmsAccountComponent } from '../../shared/sms-account.component';
import { LegalSettingsComponent } from './legal-settings.component';

interface PlatformSettings { publicApiUrl?: string; publicAppUrl?: string; effectiveAppUrl: string; effectiveApiUrl: string; source: 'database' | 'config' | 'request'; swaggerUrl: string; openApiUrl: string; guideUrl: string; }

/** Platform settings kept in the database. For now: the public address of the API that the developer documentation shows. */
@Component({
  selector: 'app-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, PageHeaderComponent, BillingProfileComponent, GatewaysComponent, LegalSettingsComponent, EmailSettingsComponent, AppLinksSettingsComponent, WhatsAppSettingsComponent, SmsAccountComponent],
  template: `
    <app-page-header title="Settings" subtitle="Platform-wide settings, kept in the database." />

    <div class="grid gap-6 xl:grid-cols-5">
      <form class="card xl:col-span-3" (ngSubmit)="save()">
        <div class="card-title">Public API address</div>
        <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">
          The address of the <strong>API</strong> (not of this web page). The developer documentation uses it in every example and link, so a developer can copy a request and it works.
        </p>
        <label class="block"><span class="label">API address</span>
          <input class="input font-mono" name="url" [(ngModel)]="url" placeholder="https://localhost:7167" autocomplete="off" /></label>
        <label class="mt-5 block"><span class="label">Web app address</span>
          <input class="input font-mono" name="appurl" [(ngModel)]="appUrl" placeholder="http://localhost:4200/app" autocomplete="off" /></label>
        <p class="hint">Where the <strong>web console</strong> lives. Customers are sent back here after paying (Stripe, Paynow) and links in emails point here.
          In development with <code class="code">ng serve</code> use <code class="code">http://localhost:4200/app</code>; in production use your site, for example <code class="code">https://app.yourcompany.com/app</code>.
          Leave it empty to use the API address followed by <code class="code">/app</code> (currently <span class="font-mono">{{ settings()?.effectiveAppUrl }}</span>).</p>
        <p class="hint">In development this is the address the API listens on (for example <code class="code">https://localhost:7167</code>). In production, use your API domain (for example <code class="code">https://api.yourcompany.com</code>).
          Leave it empty to fall back to the address a request arrives on.</p>
        <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
          <button type="button" class="btn-ghost" (click)="url = settings()?.publicApiUrl ?? ''; appUrl = settings()?.publicAppUrl ?? ''">Reset</button>
          <button class="btn" type="submit" [disabled]="busy()">Save</button>
        </div>
      </form>

      <div class="card xl:col-span-2">
        <div class="card-title">What developers will see</div>
        @if (settings(); as s) {
          <div class="mb-3 flex items-center gap-2 text-sm"><span class="badge" [class.badge-ok]="s.source === 'database'" [class.badge-warn]="s.source !== 'database'">{{ label(s.source) }}</span>
            <span class="min-w-0 truncate font-mono text-xs">{{ s.effectiveApiUrl }}</span></div>
          @if (s.source === 'request') { <p class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-900/25 dark:text-amber-200">Not set yet: the guide shows whichever address the reader's request arrived on, which can be the web page's address. Set the API address on the left.</p> }
          <ul class="space-y-2 text-sm">
            @for (l of links(); track l.label) {
              <li class="flex items-center gap-2 rounded-lg border border-slate-200 px-3 py-2 dark:border-slate-800">
                <span class="w-28 shrink-0 text-xs text-slate-500">{{ l.label }}</span>
                <a class="min-w-0 flex-1 truncate font-mono text-xs text-brand-600 hover:underline" [href]="l.url" target="_blank" rel="noopener">{{ l.url }}</a>
                <button type="button" class="icon-btn" (click)="copy(l.url)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button>
              </li>
            }
          </ul>
        }
      </div>
    </div>

    <app-email-settings />
    <app-app-links-settings />
    <app-whatsapp-settings />
    <div class="mt-6"><app-sms-account [platform]="true" /></div>
    <app-gateways />
    <app-billing-profile />
    <app-legal-settings />
  `,
})
export class SettingsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly settings = signal<PlatformSettings | null>(null);
  readonly busy = signal(false);
  url = '';
  appUrl = '';

  links(): { label: string; url: string }[] {
    const s = this.settings();
    return s ? [
      { label: 'API reference', url: s.swaggerUrl }, { label: 'OpenAPI JSON', url: s.openApiUrl },
      { label: 'Guide (page)', url: appUrl('/docs') }, { label: 'Guide (Markdown)', url: s.guideUrl },
    ] : [];
  }
  label(source: string): string { return source === 'database' ? 'Saved' : source === 'config' ? 'From configuration' : 'Detected from the request'; }

  async ngOnInit(): Promise<void> { await this.load(); }

  async load(): Promise<void> {
    try { const s = await this.api.get<PlatformSettings>('/api/v1/settings'); this.settings.set(s); this.url = s.publicApiUrl ?? ''; this.appUrl = s.publicAppUrl ?? ''; } catch (e) { this.toast.err(e); }
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try { const s = await this.api.put<PlatformSettings>('/api/v1/settings', { publicApiUrl: this.url.trim(), publicAppUrl: this.appUrl.trim() }); this.settings.set(s); this.url = s.publicApiUrl ?? ''; this.appUrl = s.publicAppUrl ?? ''; this.toast.ok('Saved. The documentation uses it straight away.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async copy(text: string): Promise<void> { try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { this.toast.err(new Error('Copy failed - select the text and copy it manually.')); } }
}
