import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { BridgeMonitor, IntegrationMonitor, IntegrationPricing, ZohoAppSettings } from '../../core/integrations.models';
import { DialogService, ToastService } from '../../core/ui.services';
import { IntegrationAccess, USES } from '../zoho-access.component';
import { ConnectorAppsComponent } from './connector-apps.component';

interface OwnerKey { id: string; name: string; prefix: string; owner: string; ownerKind: 'partner' | 'company'; clientId: string | null; createdAt: string; lastUsedAt: string | null; }
import { IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';

/** The platform owner decides who may use Zoho Books (applications, set-up fee, suspend, revoke keys) and sees every company's Zoho Books connection (token health, syncs, failures, usage), the Zoho app keys and what a connection costs. */
@Component({
  selector: 'app-integrations-monitor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DecimalPipe, IconComponent, PageHeaderComponent, StatusComponent, ConnectorAppsComponent],
  template: `
    <app-page-header title="Integrations monitor" subtitle="Who may use which accounting product, every company's Zoho Books connection, and the apps and prices of QuickBooks, Xero, Sage and Odoo.">
      <button class="btn-ghost" type="button" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    <div class="card mb-4 overflow-x-auto">
      <div class="mb-3 flex flex-wrap items-center justify-between gap-2"><div class="card-title mb-0!">Applications and access</div>
        <div class="flex flex-wrap gap-1">
          @for (f of accessFilters; track f.key) { <button type="button" [class]="accessFilter() === f.key ? 'btn' : 'btn-ghost'" (click)="setAccessFilter(f.key)">{{ f.label }}</button> }
        </div></div>
      <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Companies apply for each accounting product (Zoho Books, QuickBooks, Xero, Sage, Odoo) on its own. Approve with a set-up fee and it switches on when that invoice is paid (no fee: at once).
        Suspend stops that product at once (for Zoho also its API keys and partner access).</p>
      <table class="table w-full text-sm">
        <thead><tr><th>Company</th><th>Product</th><th>Wants</th><th>Status</th><th>Set-up invoice</th><th>Decided</th><th class="text-right"></th></tr></thead>
        <tbody>
          @for (r of access(); track r.clientId + (r.provider ?? 'zoho')) {
            <tr>
              <td class="font-medium">{{ r.company }}<div class="text-xs text-slate-500">{{ r.requestedBy || '' }} {{ r.requestedAt | date: 'd MMM y' }}</div>
                @if (r.note) { <div class="mt-1 max-w-72 text-xs text-slate-500">“{{ r.note }}”</div> }</td>
              <td class="whitespace-nowrap font-medium">{{ r.providerLabel || 'Zoho Books' }}</td>
              <td class="text-xs">{{ (r.provider ?? 'zoho') === 'zoho' ? uses(r.uses) : 'Fiscalise its invoices' }}</td>
              <td><app-status [value]="statusBadge(r.status)" /><div class="text-xs text-slate-500">{{ statusLabel(r.status) }}</div>
                @if (r.decisionNote && (r.status === 'Rejected' || r.status === 'Suspended')) { <div class="mt-1 max-w-56 text-xs text-red-600 dark:text-red-400">{{ r.decisionNote }}</div> }</td>
              <td class="whitespace-nowrap">@if (r.invoiceNumber) { {{ r.invoiceNumber }} · {{ r.setupFee | number: '1.2-2' }} {{ r.currency }}<div class="text-xs text-slate-500">{{ r.invoiceStatus }}</div> } @else { — }</td>
              <td class="whitespace-nowrap">{{ (r.decidedAt | date: 'd MMM y') || '—' }}<div class="text-xs text-slate-500">{{ r.decidedBy || '' }}</div></td>
              <td class="whitespace-nowrap text-right">
                @switch (r.status) {
                  @case ('Requested') {
                    <button class="btn" type="button" [disabled]="busy()" (click)="approve(r)">Approve</button>
                    <button class="btn-ghost text-red-600" type="button" [disabled]="busy()" (click)="reject(r)">Reject</button>
                  }
                  @case ('AwaitingPayment') { <button class="btn-ghost text-red-600" type="button" [disabled]="busy()" (click)="reject(r)">Cancel</button> }
                  @case ('Active') { <button class="btn-ghost text-red-600" type="button" [disabled]="busy()" (click)="suspend(r)">Suspend</button> }
                  @case ('Suspended') { <button class="btn" type="button" [disabled]="busy()" (click)="reactivate(r)">Reactivate</button> }
                }
              </td>
            </tr>
          } @empty { <tr><td colspan="7" class="py-8 text-center text-slate-500">Nothing here.</td></tr> }
        </tbody>
      </table>
    </div>

    @if (m(); as m) {
      <div class="mb-4 grid gap-3 sm:grid-cols-4">
        <div class="card"><div class="text-xs text-slate-500">Connected</div><div class="text-2xl font-semibold">{{ m.connected }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Need attention</div><div class="text-2xl font-semibold" [class.text-red-600]="m.needsAttention > 0">{{ m.needsAttention }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Fiscalised this month</div><div class="text-2xl font-semibold">{{ m.fiscalisedThisMonth }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Earning this month</div>
          <div class="text-2xl font-semibold">@for (r of m.monthlyRevenue; track r.currency) { {{ r.amount | number: '1.2-2' }} {{ r.currency }} } @empty { <span class="text-base text-slate-400">charging off</span> }</div></div>
      </div>
      <div class="card overflow-x-auto">
        <table class="table w-full text-sm">
          <thead><tr><th>Company</th><th>Status</th><th>Organisation</th><th>Token renewed</th><th>Last sync</th><th class="text-right">Calls today</th><th class="text-right">This month</th><th class="text-right">Waiting</th><th class="text-right">Failed</th></tr></thead>
          <tbody>
            @for (r of m.connections; track r.connectionId) {
              <tr>
                <td class="font-medium">{{ r.company }}<div class="text-xs text-slate-500">{{ r.autoFiscalise ? 'automatic' : 'by hand' }}</div></td>
                <td><app-status [value]="r.status === 'Connected' ? (r.lastError ? 'Failed' : 'Active') : r.status === 'NeedsReconnect' ? 'Expired' : 'Pending'" />
                  @if (r.lastError) { <div class="mt-1 max-w-64 text-xs text-red-600 dark:text-red-400">{{ r.lastError }}</div> }</td>
                <td>{{ r.organizationName || '—' }}</td>
                <td class="whitespace-nowrap">{{ (r.lastTokenRefreshAt | date: 'd MMM, HH:mm') || '—' }}</td>
                <td class="whitespace-nowrap">{{ (r.lastSyncAt | date: 'd MMM, HH:mm') || 'never' }}</td>
                <td class="text-right">{{ r.apiCallsToday }}</td>
                <td class="text-right">{{ r.fiscalisedThisMonth }}</td>
                <td class="text-right">{{ r.pending }}</td>
                <td class="text-right" [class.text-red-600]="r.failed > 0">{{ r.failed }}</td>
              </tr>
            } @empty { <tr><td colspan="9" class="py-10 text-center text-slate-500">No company has connected Zoho Books yet.</td></tr> }
          </tbody>
        </table>
      </div>
    }

    @if (bridge(); as b) {
      <div class="card mt-4 overflow-x-auto">
        <div class="mb-3 flex flex-wrap items-center justify-between gap-2"><div class="card-title mb-0!">Zoho bridge (partners)</div>
          <span class="text-sm text-slate-500">{{ b.companiesShared }} companies sharing · {{ b.callsToday }} calls today</span></div>
        <table class="table w-full text-sm">
          <thead><tr><th>Partner</th><th class="text-right">Keys</th><th class="text-right">Companies</th><th class="text-right">Calls today</th><th class="text-right">In all</th><th>Webhook</th><th class="text-right">Waiting</th><th class="text-right">Failed</th><th>Last used</th></tr></thead>
          <tbody>
            @for (r of b.partners; track r.partnerId) {
              <tr><td class="font-medium">{{ r.partner }}</td><td class="text-right">{{ r.activeKeys }}</td><td class="text-right">{{ r.companies }}</td><td class="text-right">{{ r.callsToday }}</td><td class="text-right">{{ r.callsTotal }}</td>
                <td>{{ r.webhookHost || 'feed only' }}</td><td class="text-right">{{ r.eventsPending }}</td><td class="text-right" [class.text-red-600]="r.eventsFailed > 0">{{ r.eventsFailed }}</td><td>{{ (r.lastUsedAt | date: 'd MMM, HH:mm') || 'never' }}</td></tr>
            } @empty { <tr><td colspan="9" class="py-8 text-center text-slate-500">No partner uses the bridge yet. Partners find it under Partner → Zoho bridge.</td></tr> }
          </tbody>
        </table>
      </div>
    }

    <div class="card mt-4 overflow-x-auto">
      <div class="card-title">Live API keys</div>
      <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Every key that can reach Zoho Books through FiscalHub: partners' keys and companies' own-system keys. Revoking one stops it at once; its owner is told.</p>
      <table class="table w-full text-sm">
        <thead><tr><th>Key</th><th>Belongs to</th><th>Created</th><th>Last used</th><th class="text-right"></th></tr></thead>
        <tbody>
          @for (k of keys(); track k.id) {
            <tr><td class="font-medium">{{ k.name }}<div class="font-mono text-xs text-slate-500">{{ k.prefix }}…</div></td>
              <td>{{ k.owner }}<div class="text-xs text-slate-500">{{ k.ownerKind === 'partner' ? 'partner' : 'company (own system)' }}</div></td>
              <td class="whitespace-nowrap">{{ k.createdAt | date: 'd MMM y' }}</td><td class="whitespace-nowrap">{{ (k.lastUsedAt | date: 'd MMM, HH:mm') || 'never' }}</td>
              <td class="text-right"><button class="btn-ghost text-red-600" type="button" [disabled]="busy()" (click)="revoke(k)">Revoke</button></td></tr>
          } @empty { <tr><td colspan="5" class="py-8 text-center text-slate-500">No live keys.</td></tr> }
        </tbody>
      </table>
    </div>

    @if (canSettings()) {
      <div class="mt-6 grid gap-4 lg:grid-cols-2">
        <form class="card" (ngSubmit)="saveApp()">
          <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0!">Zoho app</div>
            @if (app(); as a) { <span class="badge" [class.badge-ok]="a.configured" [class.badge-warn]="!a.configured">{{ a.configured ? 'Ready' : 'Not set up' }}</span> }</div>
          <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Register a <strong>Server-based application</strong> at <a class="text-brand-600 hover:underline" href="https://api-console.zoho.com" target="_blank" rel="noopener">api-console.zoho.com</a>
            with this redirect address, switch on multi-DC so accounts from every region can connect, then paste the client ID and secret here.</p>
          @if (app(); as a) {
            <div class="mb-3 flex items-center gap-2"><code class="code min-w-0 flex-1 truncate text-xs">{{ a.redirectUri }}</code>
              <button type="button" class="icon-btn" (click)="copy(a.redirectUri)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
          }
          <label class="block"><span class="label">Client ID</span><input class="input font-mono" name="cid" [(ngModel)]="a.clientId" autocomplete="off" placeholder="1000.XXXXXXXX" /></label>
          <label class="mt-3 block"><span class="label">Client secret</span>
            <input class="input" type="password" name="sec" [(ngModel)]="a.clientSecret" autocomplete="new-password" [placeholder]="app()?.hasSecret ? '•••••••• (saved, leave empty to keep)' : ''" /></label>
          @if (app()?.hasSecret) { <label class="mt-1 flex items-center gap-2 text-xs text-slate-500"><input type="checkbox" name="clr" [(ngModel)]="a.clearSecret" />Remove the saved secret</label> }
          <div class="mt-4 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Save Zoho app</button></div>
        </form>

        <form class="card" (ngSubmit)="savePricing()">
          <div class="card-title">What a connection costs</div>
          <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Added to each connected company's subscription invoice: the monthly fee, and every document above the included number fiscalised in the period before.</p>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="charge" [(ngModel)]="p.charge" />Charge for connections</label>
          <div class="mt-3 grid gap-3 sm:grid-cols-2">
            <label class="block"><span class="label">Monthly fee</span><input class="input" type="number" min="0" step="0.01" name="fee" [(ngModel)]="p.monthlyFee" /></label>
            <label class="block"><span class="label">Currency</span><input class="input uppercase" maxlength="3" name="cur" [(ngModel)]="p.currency" /></label>
            <label class="block"><span class="label">Documents included a month</span><input class="input" type="number" min="0" name="inc" [(ngModel)]="p.includedDocuments" /></label>
            <label class="block"><span class="label">Price per extra document</span><input class="input" type="number" min="0" step="0.0001" name="per" [(ngModel)]="p.pricePerExtraDocument" /></label>
            <label class="block"><span class="label">Set-up fee (one-off, invoiced on approval)</span><input class="input" type="number" min="0" step="0.01" name="sfee" [(ngModel)]="p.setupFee" /></label>
            <label class="block"><span class="label">Zoho bridge: monthly fee per company using API keys or a partner</span><input class="input" type="number" min="0" step="0.01" name="bfee" [(ngModel)]="p.bridgeMonthlyFee" /></label>
          </div>
          <p class="hint mt-2">Only companies whose plan is billed in the same currency are charged.</p>
          <div class="mt-4 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Save prices</button></div>
        </form>
      </div>
      <app-connector-apps />
    }
  `,
})
export class IntegrationsMonitorComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);
  readonly m = signal<IntegrationMonitor | null>(null);
  readonly app = signal<ZohoAppSettings | null>(null);
  readonly busy = signal(false);
  readonly canSettings = signal(false);
  a = { clientId: '', clientSecret: '', clearSecret: false };
  p: IntegrationPricing = { charge: false, monthlyFee: 10, currency: 'USD', includedDocuments: 200, pricePerExtraDocument: 0.05, bridgeMonthlyFee: 0, setupFee: 0 };
  private readonly dialog = inject(DialogService);
  readonly access = signal<IntegrationAccess[]>([]);
  readonly keys = signal<OwnerKey[]>([]);
  readonly accessFilter = signal('');
  readonly accessFilters = [{ key: '', label: 'All' }, { key: 'Requested', label: 'To decide' }, { key: 'AwaitingPayment', label: 'Awaiting payment' }, { key: 'Active', label: 'Active' }, { key: 'Suspended', label: 'Suspended' }, { key: 'Rejected', label: 'Rejected' }];
  readonly bridge = signal<BridgeMonitor | null>(null);

  ngOnInit(): void {
    this.canSettings.set(this.auth.can('settings.manage'));
    void this.load();
    if (this.canSettings()) void this.loadSettings();
  }

  async load(): Promise<void> {
    try {
      this.m.set(await this.api.get<IntegrationMonitor>('/api/v1/admin/integrations'));
      this.bridge.set(await this.api.get<BridgeMonitor>('/api/v1/admin/integrations/bridge'));
      await this.loadAccess();
    } catch (e) { this.toast.err(e); }
  }

  async loadAccess(): Promise<void> {
    this.access.set(await this.api.get<IntegrationAccess[]>('/api/v1/admin/integrations/access', { status: this.accessFilter() }));
    this.keys.set(await this.api.get<OwnerKey[]>('/api/v1/admin/integrations/access/keys'));
  }
  setAccessFilter(k: string): void { this.accessFilter.set(k); void this.loadAccess().catch(e => this.toast.err(e)); }
  uses(keys: string[]): string { return keys.map(k => USES.find(u => u.key === k)?.label ?? k).join('; ') || '—'; }
  statusLabel(s: string): string { return ({ Requested: 'to decide', AwaitingPayment: 'awaiting payment', Active: 'on', Suspended: 'suspended', Rejected: 'rejected' } as Record<string, string>)[s] ?? s; }
  statusBadge(s: string): string { return s === 'Active' ? 'Active' : s === 'Suspended' || s === 'Rejected' ? 'Failed' : 'Pending'; }

  private async act(run: () => Promise<unknown>, done: string): Promise<void> {
    this.busy.set(true);
    try { await run(); this.toast.ok(done); await this.load(); } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
  async approve(r: IntegrationAccess): Promise<void> {
    const fee = await this.dialog.prompt(`Approve ${r.company}. Set-up fee in ${r.currency} (0 = switch on now, no invoice). Default ${r.setupFee}:`, String(r.setupFee ?? 0), 'Approve');
    if (fee === null) return;
    const setupFee = Number(fee.trim() === '' ? r.setupFee : fee);
    if (!Number.isFinite(setupFee) || setupFee < 0) { this.toast.err(new Error('Enter a fee of 0 or more.')); return; }
    await this.act(() => this.api.post(`/api/v1/admin/integrations/access/${r.id}/approve`, { setupFee, note: null }), setupFee > 0 ? 'Approved. The set-up invoice was raised; it switches on when paid.' : 'Approved and switched on.');
  }
  async reject(r: IntegrationAccess): Promise<void> {
    const reason = await this.dialog.prompt(`Why is ${r.company} not approved? They will see this.`, '', 'Reject');
    if (!reason?.trim()) return;
    await this.act(() => this.api.post(`/api/v1/admin/integrations/access/${r.id}/reject`, { reason }), 'Rejected.');
  }
  async suspend(r: IntegrationAccess): Promise<void> {
    const label = r.providerLabel || 'Zoho Books';
    const reason = await this.dialog.prompt(`Suspend ${label} for ${r.company}? It stops at once. Reason (they will see it):`, '', 'Suspend');
    if (!reason?.trim()) return;
    await this.act(() => this.api.post(`/api/v1/admin/integrations/access/companies/${r.clientId}/suspend?provider=${r.provider ?? 'zoho'}`, { reason }), 'Suspended.');
  }
  async reactivate(r: IntegrationAccess): Promise<void> {
    if (!await this.dialog.confirm(`Switch ${r.providerLabel || 'Zoho Books'} back on for ${r.company}?`, 'Reactivate')) return;
    await this.act(() => this.api.post(`/api/v1/admin/integrations/access/companies/${r.clientId}/reactivate?provider=${r.provider ?? 'zoho'}`, {}), 'Reactivated.');
  }
  async revoke(k: OwnerKey): Promise<void> {
    const reason = await this.dialog.prompt(`Revoke "${k.name}" (${k.owner})? It stops working at once and cannot be undone. Reason (optional):`, '', 'Revoke');
    if (reason === null) return;
    await this.act(() => this.api.post(`/api/v1/admin/integrations/access/keys/${k.id}/revoke`, { reason: reason || null }), 'Key revoked.');
  }

  private fillApp(s: ZohoAppSettings): void { this.app.set(s); this.a = { clientId: s.clientId ?? '', clientSecret: '', clearSecret: false }; }

  async loadSettings(): Promise<void> {
    try {
      this.fillApp(await this.api.get<ZohoAppSettings>('/api/v1/admin/integrations/zoho-app'));
      this.p = await this.api.get<IntegrationPricing>('/api/v1/admin/integrations/pricing');
    } catch (e) { this.toast.err(e); }
  }

  async saveApp(): Promise<void> {
    this.busy.set(true);
    try { this.fillApp(await this.api.put<ZohoAppSettings>('/api/v1/admin/integrations/zoho-app', { ...this.a, clientId: this.a.clientId || null, clientSecret: this.a.clientSecret || null })); this.toast.ok('Zoho app saved.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async savePricing(): Promise<void> {
    this.busy.set(true);
    try {
      this.p = await this.api.put<IntegrationPricing>('/api/v1/admin/integrations/pricing', { ...this.p, monthlyFee: Number(this.p.monthlyFee) || 0, includedDocuments: Number(this.p.includedDocuments) || 0, pricePerExtraDocument: Number(this.p.pricePerExtraDocument) || 0, bridgeMonthlyFee: Number(this.p.bridgeMonthlyFee) || 0, setupFee: Number(this.p.setupFee) || 0 });
      this.toast.ok('Prices saved.'); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async copy(text: string): Promise<void> { try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { /* ignore */ } }
}
