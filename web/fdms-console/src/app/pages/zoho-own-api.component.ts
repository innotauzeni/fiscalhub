import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { BridgeEndpoint, BridgeEvent, BridgeKey, BridgeModule } from '../core/integrations.models';
import { DialogService, ToastService } from '../core/ui.services';
import { IconComponent, StatusComponent } from '../shared/ui.components';

interface OwnApi { companyId: string; keys: BridgeKey[]; endpoint: BridgeEndpoint; recentEvents: BridgeEvent[]; modules: BridgeModule[]; baseUrl: string;
  ready: boolean; notReadyReason: string | null; charged: boolean; monthlyFee: number; currency: string; connectorPlan: boolean; }

/**
 * The company's own system (an ERP, a shop, a POS) talks to its Zoho Books through FiscalHub: one key, Zoho's own format, and a webhook for changes.
 * FiscalHub holds the Zoho sign-in and renews it, so the company's system never deals with Zoho's OAuth.
 */
@Component({
  selector: 'app-zoho-own-api',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DecimalPipe, IconComponent, StatusComponent],
  template: `
    @if (o(); as o) {
      <div class="card mt-4">
        <div class="mb-1 flex flex-wrap items-center justify-between gap-2">
          <div class="card-title mb-0! flex items-center gap-2"><app-icon name="key" size="h-4 w-4" />Your own system (API)</div>
          @if (o.ready) { <app-status value="Active" /> } @else { <span class="text-xs text-amber-700 dark:text-amber-300">{{ o.notReadyReason }}</span> }
        </div>
        <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Let your own system (ERP, shop, POS, payroll) push data to Zoho Books and pull it back through FiscalHub, with one key.
          FiscalHub keeps the Zoho sign-in and renews it for you; your system uses Zoho Books' normal API format.</p>
        @if (o.charged) { <p class="hint mb-3">This costs {{ o.monthlyFee | number: '1.2-2' }} {{ o.currency }} a month while a key is in use, added to your subscription invoice.</p> }

        <!-- quick start -->
        <div class="mb-5 rounded-xl bg-slate-950 p-4 text-xs text-slate-100">
          <div class="mb-2 flex items-center justify-between text-slate-400"><span>Quick start</span>
            <button type="button" class="text-slate-300 hover:text-white" (click)="copy(snippet(o))">Copy</button></div>
          <pre class="overflow-x-auto whitespace-pre-wrap font-mono leading-5">{{ snippet(o) }}</pre>
        </div>

        <div class="grid gap-4 lg:grid-cols-2">
          <div>
            <div class="mb-2 text-sm font-semibold">Keys</div>
            @if (secret(); as s) {
              <div class="mb-3 rounded-lg bg-emerald-50 p-3 text-sm dark:bg-emerald-900/30">
                <div class="mb-1 font-medium">Copy the key now. It is not shown again.</div>
                <div class="flex items-center gap-2"><code class="code min-w-0 flex-1 truncate">{{ s }}</code><button type="button" class="icon-btn" (click)="copy(s)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
              </div>
            }
            <ul class="mb-3 divide-y divide-slate-100 text-sm dark:divide-slate-800">
              @for (k of o.keys; track k.id) {
                <li class="flex items-center gap-3 py-2">
                  <div class="min-w-0 flex-1"><div class="font-medium">{{ k.name }} <span class="font-mono text-xs text-slate-500">{{ k.prefix }}…</span></div>
                    <div class="text-xs text-slate-500">{{ labels(o, k.modules ?? []) }} · {{ k.canWrite ? 'read and write' : 'read only' }} · {{ k.callsToday }} today · last used {{ (k.lastUsedAt | date: 'd MMM, HH:mm') || 'never' }}</div></div>
                  @if (k.revokedAt) { <span class="badge badge-bad">Revoked</span> } @else { <button type="button" class="btn-ghost px-2! py-1! text-xs text-red-600" (click)="revoke(k)">Revoke</button> }
                </li>
              } @empty { <li class="py-3 text-slate-500">No key yet.</li> }
            </ul>
            <form class="rounded-lg border border-slate-200 p-3 dark:border-slate-700" (ngSubmit)="createKey()">
              <label class="block"><span class="label">Key name</span><input class="input" name="kn" [(ngModel)]="keyName" placeholder="e.g. Sage ERP, online shop" /></label>
              <div class="mt-2 grid grid-cols-2 gap-1 text-sm">
                @for (m of o.modules; track m.key) { <label class="flex items-center gap-2"><input type="checkbox" [checked]="chosen.has(m.key)" (change)="toggle(chosen, m.key)" />{{ m.label }}</label> }
              </div>
              <label class="mt-2 flex items-center gap-2 text-sm"><input type="checkbox" name="kw" [(ngModel)]="canWrite" />May create and update records (push)</label>
              <div class="mt-3 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Create key</button></div>
            </form>
          </div>

          <form (ngSubmit)="saveEndpoint()">
            <div class="mb-2 text-sm font-semibold">Changes from Zoho (webhook)</div>
            <label class="block"><span class="label">Your system's webhook address (https)</span><input class="input font-mono" name="wh" [(ngModel)]="url" placeholder="https://your-system.example/zoho-changes" /></label>
            <div class="mt-2 grid grid-cols-2 gap-1 text-sm">
              @for (m of watchable(o); track m.key) { <label class="flex items-center gap-2"><input type="checkbox" [checked]="watch.has(m.key)" (change)="toggle(watch, m.key)" />{{ m.label }}</label> }
            </div>
            @if (whSecret(); as s) {
              <div class="mt-3 rounded-lg bg-emerald-50 p-3 text-sm dark:bg-emerald-900/30">
                <div class="mb-1 font-medium">Signing secret. Copy it now; it is not shown again.</div>
                <div class="flex items-center gap-2"><code class="code min-w-0 flex-1 truncate">{{ s }}</code><button type="button" class="icon-btn" (click)="copy(s)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
              </div>
            }
            <p class="hint mt-2">Each change is POSTed as JSON, signed with <code class="code">X-FiscalHub-Signature</code>. Without a webhook, read <code class="code">{{ o.baseUrl }}/events?after=0</code> with your key.</p>
            <div class="mt-3 flex flex-wrap justify-end gap-2">
              @if (o.endpoint.hasSecret) { <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="rot" [(ngModel)]="rotate" />New secret</label> }
              <button class="btn" type="submit" [disabled]="busy()">Save webhook</button>
            </div>
          </form>
        </div>

        @if (o.recentEvents.length) {
          <div class="mt-5 overflow-x-auto">
            <div class="mb-2 text-sm font-semibold">Recent changes sent to your system</div>
            <table class="table w-full text-sm">
              <thead><tr><th>#</th><th>Event</th><th>Record</th><th>When</th><th>Status</th></tr></thead>
              <tbody>
                @for (e of o.recentEvents; track e.id) {
                  <tr><td>{{ e.id }}</td><td class="font-mono text-xs">{{ e.type }}</td><td class="font-mono text-xs">{{ e.recordId }}</td><td>{{ e.createdAt | date: 'd MMM, HH:mm' }}</td>
                    <td><app-status [value]="e.status" />@if (e.lastError) { <div class="text-xs text-red-600">{{ e.lastError }}</div> }</td></tr>
                }
              </tbody>
            </table>
          </div>
        }
        <p class="hint mt-4">The full guide (every call, the errors, the webhook signature) is in Developer docs → "Zoho bridge". With your own key, use <code class="code">{{ o.baseUrl }}/zoho/&#123;path&#125;</code>.</p>
      </div>
    }
  `,
})
export class ZohoOwnApiComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly o = signal<OwnApi | null>(null);
  readonly busy = signal(false);
  readonly secret = signal<string | null>(null);
  readonly whSecret = signal<string | null>(null);
  keyName = '';
  canWrite = true;
  chosen = new Set<string>(['contacts', 'items', 'invoices']);
  url = '';
  watch = new Set<string>();
  rotate = false;

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    try {
      const o = await this.api.get<OwnApi>('/api/v1/integrations/zoho/bridge/own');
      this.o.set(o); this.url = o.endpoint.webhookUrl ?? ''; this.watch = new Set(o.endpoint.watchModules);
    } catch (e) { this.toast.err(e); }
  }

  watchable(o: OwnApi): BridgeModule[] { return o.modules.filter(m => m.watchable); }
  labels(o: OwnApi, keys: string[]): string { return keys.map(k => o.modules.find(m => m.key === k)?.label ?? k).join(', '); }
  toggle(set: Set<string>, key: string): void { if (set.has(key)) set.delete(key); else set.add(key); }

  snippet(o: OwnApi): string {
    return `# read (pull) your Zoho Books contacts\ncurl -H "X-Bridge-Key: fhb_your_key" "${o.baseUrl}/zoho/contacts?page=1"\n\n` +
      `# create (push) a customer in Zoho Books\ncurl -X POST -H "X-Bridge-Key: fhb_your_key" -H "Content-Type: application/json" \\\n  -d '{"contact_name":"Moyo Traders","contact_type":"customer"}' "${o.baseUrl}/zoho/contacts"`;
  }

  async createKey(): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.post<{ secret: string }>('/api/v1/integrations/zoho/bridge/own/keys', { name: this.keyName, modules: [...this.chosen], canWrite: this.canWrite });
      this.secret.set(r.secret); this.keyName = ''; await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async revoke(k: BridgeKey): Promise<void> {
    if (!(await this.dialog.confirm(`Revoke "${k.name}"? Your system stops working with it at once.`, 'Revoke', true))) return;
    try { await this.api.delete(`/api/v1/integrations/zoho/bridge/own/keys/${k.id}`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async saveEndpoint(): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.put<{ secret: string | null }>('/api/v1/integrations/zoho/bridge/own/endpoint', { webhookUrl: this.url || null, watchModules: [...this.watch], rotateSecret: this.rotate });
      if (r.secret) this.whSecret.set(r.secret);
      this.rotate = false; this.toast.ok('Saved.'); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async copy(t: string): Promise<void> { try { await navigator.clipboard.writeText(t); this.toast.ok('Copied.'); } catch { /* ignore */ } }
}
