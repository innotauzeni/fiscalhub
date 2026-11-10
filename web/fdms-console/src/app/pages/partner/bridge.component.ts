import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { BridgePortal } from '../../core/integrations.models';
import { DialogService, ToastService } from '../../core/ui.services';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';

/**
 * The partner's page for the Zoho bridge: keys for their system, the webhook that receives changes, the companies that shared their Zoho Books,
 * recent events, and how to call it.
 */
@Component({
  selector: 'app-partner-bridge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, PageHeaderComponent, StatusComponent],
  template: `
    <app-page-header title="Zoho bridge" subtitle="Your system reads and writes the Zoho Books of the companies that share it with you, and hears when something changes.">
      <button class="btn-ghost" type="button" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    @if (p(); as p) {
      <div class="card mb-4 text-sm">
        <div class="card-title">How it works</div>
        <ol class="list-decimal space-y-1 pl-5 text-slate-600 dark:text-slate-300">
          <li>Give your partner code <code class="code">{{ p.partnerCode }}</code> to the company. In FiscalHub (Integration → Zoho Books) it connects Zoho and shares the modules you need.</li>
          <li>Create a key below and put it in your system. Send it as <code class="code">X-Bridge-Key</code>.</li>
          <li>Call <code class="code">{{ p.baseUrl }}/companies</code> for the companies, then <code class="code">{{ p.baseUrl }}/companies/&#123;companyId&#125;/zoho/contacts</code> and so on, exactly as in Zoho Books' API (v3). The organisation is filled in for you.</li>
          <li>Set a webhook to receive changes, or read <code class="code">{{ p.baseUrl }}/events?after=0</code>.</li>
        </ol>
      </div>

      <div class="grid gap-4 lg:grid-cols-2">
        <div class="card">
          <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0!">Keys</div></div>
          @if (newKey(); as k) {
            <div class="mb-3 rounded-lg bg-emerald-50 p-3 text-sm dark:bg-emerald-900/30">
              <div class="mb-1 font-medium">Copy the key now. It is not shown again.</div>
              <div class="flex items-center gap-2"><code class="code min-w-0 flex-1 truncate">{{ k }}</code>
                <button type="button" class="icon-btn" (click)="copy(k)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
            </div>
          }
          <form class="mb-3 flex gap-2" (ngSubmit)="createKey()">
            <input class="input flex-1" name="kname" [(ngModel)]="keyName" placeholder="Name, e.g. Production server" />
            <button class="btn" type="submit" [disabled]="busy()">Create key</button>
          </form>
          <ul class="divide-y divide-slate-100 text-sm dark:divide-slate-800">
            @for (k of p.keys; track k.id) {
              <li class="flex items-center gap-3 py-2">
                <div class="min-w-0 flex-1"><div class="font-medium">{{ k.name }} <span class="font-mono text-xs text-slate-500">{{ k.prefix }}…</span></div>
                  <div class="text-xs text-slate-500">{{ k.callsToday }} calls today · {{ k.callsTotal }} in all · last used {{ (k.lastUsedAt | date: 'd MMM, HH:mm') || 'never' }}</div></div>
                @if (k.revokedAt) { <span class="badge badge-bad">Revoked</span> } @else { <button type="button" class="btn-ghost px-2! py-1! text-xs text-red-600" (click)="revoke(k.id)">Revoke</button> }
              </li>
            } @empty { <li class="py-4 text-center text-slate-500">No keys yet.</li> }
          </ul>
        </div>

        <form class="card" (ngSubmit)="saveEndpoint()">
          <div class="card-title">Changes (webhook)</div>
          <label class="block"><span class="label">Your webhook address (https)</span><input class="input font-mono" name="url" [(ngModel)]="url" placeholder="https://your-system.example/fiscalhub" /></label>
          <div class="mt-3 text-sm"><span class="label">Watch these modules</span>
            <div class="grid grid-cols-2 gap-1">
              @for (m of watchable(); track m.key) { <label class="flex items-center gap-2"><input type="checkbox" [checked]="watch.has(m.key)" (change)="toggleWatch(m.key)" />{{ m.label }}</label> }
            </div></div>
          @if (secret(); as s) {
            <div class="mt-3 rounded-lg bg-emerald-50 p-3 text-sm dark:bg-emerald-900/30">
              <div class="mb-1 font-medium">Signing secret. Copy it now; it is not shown again.</div>
              <div class="flex items-center gap-2"><code class="code min-w-0 flex-1 truncate">{{ s }}</code><button type="button" class="icon-btn" (click)="copy(s)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
            </div>
          }
          <p class="hint mt-3">Each change is POSTed as JSON with <code class="code">X-FiscalHub-Signature: sha256=HMAC(secret, timestamp + "." + body)</code> and <code class="code">X-FiscalHub-Timestamp</code>. Failed deliveries are retried for a day. Changes are checked about every 15 minutes, or at once when the company's Zoho workflow webhook calls.</p>
          <div class="mt-3 flex flex-wrap justify-end gap-2">
            @if (p.endpoint.hasSecret) { <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="rot" [(ngModel)]="rotate" />New secret</label> }
            <button class="btn" type="submit" [disabled]="busy()">Save</button>
          </div>
        </form>
      </div>

      <div class="card mt-4 overflow-x-auto">
        <div class="card-title">Companies that shared with you</div>
        <table class="table w-full text-sm">
          <thead><tr><th>Company</th><th>Company ID</th><th>Zoho organisation</th><th>Shared</th><th>Status</th></tr></thead>
          <tbody>
            @for (c of p.companies; track c.companyId) {
              <tr>
                <td class="font-medium">{{ c.name }}</td>
                <td><code class="code text-xs">{{ c.companyId }}</code></td>
                <td>{{ c.organizationName || '—' }}</td>
                <td class="text-xs">{{ c.modules.join(', ') }} · {{ c.canWrite ? 'read and write' : 'read only' }}</td>
                <td>@if (c.ready) { <app-status value="Active" /> } @else { <span class="text-xs text-amber-700 dark:text-amber-300">{{ c.notReadyReason }}</span> }</td>
              </tr>
            } @empty { <tr><td colspan="5" class="py-8 text-center text-slate-500">No company has shared its Zoho Books with you yet. Give them your code {{ p.partnerCode }}.</td></tr> }
          </tbody>
        </table>
      </div>

      <div class="card mt-4 overflow-x-auto">
        <div class="card-title">Recent changes</div>
        <table class="table w-full text-sm">
          <thead><tr><th>#</th><th>Event</th><th>Record</th><th>When</th><th>Status</th></tr></thead>
          <tbody>
            @for (e of p.recentEvents; track e.id) {
              <tr><td>{{ e.id }}</td><td class="font-mono text-xs">{{ e.type }}</td><td class="font-mono text-xs">{{ e.recordId }}</td><td>{{ e.createdAt | date: 'd MMM, HH:mm' }}</td>
                <td><app-status [value]="e.status === 'Delivered' ? 'Delivered' : e.status" />@if (e.lastError) { <div class="text-xs text-red-600">{{ e.lastError }}</div> }</td></tr>
            } @empty { <tr><td colspan="5" class="py-8 text-center text-slate-500">No changes yet.</td></tr> }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class PartnerBridgeComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly p = signal<BridgePortal | null>(null);
  readonly busy = signal(false);
  readonly newKey = signal<string | null>(null);
  readonly secret = signal<string | null>(null);
  keyName = '';
  url = '';
  rotate = false;
  watch = new Set<string>();

  ngOnInit(): void { void this.load(); }

  watchable() { return (this.p()?.modules ?? []).filter(m => m.watchable); }

  async load(): Promise<void> {
    try {
      const p = await this.api.get<BridgePortal>('/api/v1/partner/bridge');
      this.p.set(p); this.url = p.endpoint.webhookUrl ?? ''; this.watch = new Set(p.endpoint.watchModules);
    } catch (e) { this.toast.err(e); }
  }

  toggleWatch(key: string): void { if (this.watch.has(key)) this.watch.delete(key); else this.watch.add(key); }

  async createKey(): Promise<void> {
    this.busy.set(true);
    try { const r = await this.api.post<{ secret: string }>('/api/v1/partner/bridge/keys', { name: this.keyName }); this.newKey.set(r.secret); this.keyName = ''; await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async revoke(id: string): Promise<void> {
    if (!(await this.dialog.confirm('Revoke this key? Your system stops working with it at once.', 'Revoke', true))) return;
    try { await this.api.delete(`/api/v1/partner/bridge/keys/${id}`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async saveEndpoint(): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.put<{ secret: string | null }>('/api/v1/partner/bridge/endpoint', { webhookUrl: this.url || null, watchModules: [...this.watch], rotateSecret: this.rotate });
      if (r.secret) this.secret.set(r.secret);
      this.rotate = false; this.toast.ok('Saved.'); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async copy(text: string): Promise<void> { try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { /* ignore */ } }
}
