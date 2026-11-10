import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';

interface GatewayField { key: string; label: string; secret: boolean; required: boolean; value?: string; hasValue: boolean; hint?: string; }
interface Gateway {
  code: string; name: string; description: string; automatic: boolean; enabled: boolean; configured: boolean; source: 'database' | 'config' | 'none'; fields: GatewayField[];
  callbackUrl?: string; updatedAt?: string; updatedBy?: string;
}
interface Draft { enabled: boolean; values: Record<string, string>; replace: Set<string>; }

/**
 * Payment gateway credentials, kept in the database (secrets encrypted) so they are set here instead of in server files. A saved secret is never shown
 * again: leave its box empty to keep it, type a new one to replace it.
 */
@Component({
  selector: 'app-gateways',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, NgTemplateOutlet, IconComponent],
  template: `
    <div class="mb-3 mt-8">
      <h2 class="text-lg font-semibold tracking-tight">Payment gateways</h2>
      <p class="text-sm text-slate-500 dark:text-slate-400">Connect the services customers pay through. Credentials are saved in the database; secrets are encrypted and never shown again.</p>
    </div>

    <div class="grid gap-6 xl:grid-cols-2">
      @for (g of gateways(); track g.code) {
        <form class="card" (ngSubmit)="save(g)">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0">
              <div class="flex items-center gap-2"><span class="flex h-9 w-9 items-center justify-center rounded-xl bg-linear-to-br from-emerald-500 to-teal-600 text-white"><app-icon name="card" /></span>
                <h3 class="text-base font-semibold">{{ g.name }}</h3>
                @if (!g.automatic) { <span class="badge">Manual</span> }
                <span class="badge" [class.badge-ok]="g.configured" [class.badge-warn]="!g.configured && g.enabled" [class.badge-bad]="!g.enabled && g.source !== 'none'">{{ g.configured ? 'Connected' : g.enabled ? 'Incomplete' : g.source === 'none' ? 'Not connected' : 'Disabled' }}</span></div>
              <p class="mt-2 text-sm text-slate-600 dark:text-slate-400">{{ g.description }}</p>
            </div>
            <label class="flex shrink-0 cursor-pointer items-center gap-2 text-sm font-medium">
              <input type="checkbox" class="h-4 w-4" [name]="g.code + '-on'" [ngModel]="draft(g).enabled" (ngModelChange)="draft(g).enabled = $event" /> Enabled
            </label>
          </div>

          @if (g.source === 'config') {
            <p class="mt-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-800 dark:bg-amber-900/25 dark:text-amber-200">Using the credentials in the server's configuration file. Save here to move them into the database (the key is encrypted).</p>
          }

          <ng-template #field let-f let-g="g">

              <div>
                <label class="label" [for]="g.code + f.key">{{ f.label }}@if (f.required) { <span> *</span> }</label>
                @if (f.secret) {
                  <div class="relative">
                    <input class="input pr-24 font-mono [-webkit-text-security:disc]" [id]="g.code + f.key" [name]="g.code + f.key" type="text" autocomplete="off" autocapitalize="off" spellcheck="false" data-lpignore="true" data-1p-ignore data-bwignore
                           [placeholder]="f.hasValue ? '•••••••••••• saved (type to replace)' : 'Paste the key'" [ngModel]="draft(g).values[f.key]" (ngModelChange)="draft(g).values[f.key] = $event" />
                    @if (f.hasValue) { <span class="absolute right-3 top-1/2 -translate-y-1/2 text-xs font-medium text-emerald-600">● saved</span> }
                  </div>
                } @else {
                  <input class="input font-mono" [id]="g.code + f.key" [name]="g.code + f.key" [ngModel]="draft(g).values[f.key]" (ngModelChange)="draft(g).values[f.key] = $event" autocomplete="off" />
                }
                @if (f.hint) { <p class="hint">{{ f.hint }}</p> }
              </div>

          </ng-template>
          <div class="mt-4 grid gap-4">
            @for (f of g.fields; track f.key) { @if (f.required || g.fields.length <= 4) { <ng-container *ngTemplateOutlet="field; context: { $implicit: f, g: g }" /> } }
          </div>
          @if (g.fields.length > 4) {
            <details class="mt-4 rounded-lg border border-slate-200 p-3 dark:border-slate-800">
              <summary class="cursor-pointer text-sm font-medium text-slate-600 dark:text-slate-300">Advanced settings (optional)</summary>
              <div class="mt-3 grid gap-4">
                @for (f of g.fields; track f.key) { @if (!f.required) { <ng-container *ngTemplateOutlet="field; context: { $implicit: f, g: g }" /> } }
              </div>
            </details>
          }

          @if (g.callbackUrl) {
            <div class="mt-4 rounded-lg border border-slate-200 p-3 dark:border-slate-800">
              <div class="text-xs font-medium text-slate-500">Result URL (set this in the gateway's dashboard if it asks for one)</div>
              <div class="mt-1 flex items-center gap-2"><code class="code min-w-0 flex-1 truncate">{{ g.callbackUrl }}</code>
                <button type="button" class="icon-btn" (click)="copy(g.callbackUrl)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
            </div>
          }

          <div class="mt-5 flex flex-wrap items-center justify-between gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
            <span class="hint mt-0">@if (g.updatedAt) { Saved {{ g.updatedAt | date: 'd MMM yyyy, HH:mm' }}@if (g.updatedBy) { by {{ g.updatedBy }} } }</span>
            <span class="flex gap-2">
              @if (g.fields.some(x => x.secret && x.hasValue) && g.source === 'database') { <button type="button" class="btn-ghost" (click)="clear(g)">Remove saved key</button> }
              <button class="btn" type="submit" [disabled]="busy()">Save {{ g.name }}</button>
            </span>
          </div>
        </form>
      }
    </div>
    <p class="hint mt-3">Manual methods (EcoCash Send Money, PayPal, bank transfer) need no integration: customers pay, tell us the reference, and you confirm it under <em>Payments</em>. Bank details are set under <em>Billing and invoice template</em> below.</p>
  `,
})
export class GatewaysComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly gateways = signal<Gateway[]>([]);
  readonly busy = signal(false);
  private drafts = new Map<string, Draft>();

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> {
    try { this.setAll(await this.api.get<Gateway[]>('/api/v1/settings/gateways')); } catch (e) { this.toast.err(e); }
  }

  private setAll(list: Gateway[]): void {
    this.drafts.clear();
    for (const g of list) this.drafts.set(g.code, { enabled: g.enabled, values: Object.fromEntries(g.fields.filter(f => !f.secret).map(f => [f.key, f.value ?? ''])), replace: new Set() });
    this.gateways.set(list);
  }

  draft(g: Gateway): Draft { return this.drafts.get(g.code) ?? { enabled: false, values: {}, replace: new Set() }; }

  async save(g: Gateway, clearSecrets = false): Promise<void> {
    const d = this.draft(g);
    this.busy.set(true);
    try {
      const updated = await this.api.put<Gateway>(`/api/v1/settings/gateways/${g.code}`, { enabled: d.enabled, values: d.values, clearSecrets });
      this.setAll(this.gateways().map(x => (x.code === g.code ? updated : x)));
      this.toast.ok(clearSecrets ? `${g.name} key removed.` : `${g.name} saved. It applies straight away.`);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async clear(g: Gateway): Promise<void> {
    if (!(await this.dialog.confirm(`Remove the saved ${g.name} key? Online payments through ${g.name} stop until you enter a new one.`, 'Remove', true))) return;
    this.draft(g).enabled = false;
    await this.save(g, true);
  }

  async copy(text: string): Promise<void> { try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { this.toast.err(new Error('Copy failed - select the text and copy it manually.')); } }
}
