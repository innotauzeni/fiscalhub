import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { IntegrationDocument, IntegrationDocumentPage, MONEY_TYPES, ZohoConnection, ZohoSyncResult } from '../core/integrations.models';
import { DialogService, ToastService } from '../core/ui.services';
import { PagerComponent } from '../shared/list.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';
import { ZohoBridgeGrantsComponent } from './zoho-bridge-grants.component';
import { ZohoOwnApiComponent } from './zoho-own-api.component';
import { ZohoAccessComponent } from './zoho-access.component';

const RETURN_MESSAGES: Record<string, string> = {
  connected: 'Zoho Books is connected.',
  choose: 'Signed in to Zoho. Choose the organisation to use below.',
  denied: 'The Zoho sign-in was cancelled.',
};

/**
 * Zoho Books for one company: sign in to Zoho once, choose the organisation and the fiscal device, and the gateway pulls the invoices and credit notes,
 * fiscalises them (automatically or on a click) and writes the fiscal number, verification code and PDF back to Zoho.
 */
@Component({
  selector: 'app-integrations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DecimalPipe, IconComponent, PageHeaderComponent, StatusComponent, PagerComponent, ZohoBridgeGrantsComponent, ZohoOwnApiComponent, ZohoAccessComponent],
  template: `
    <app-page-header title="Zoho Books" subtitle="Fiscalise the invoices and credit notes you raise in Zoho Books, and see the ZIMRA result back in Zoho.">
      @if (c()?.status === 'Connected' && c()?.accessStatus === 'Active') {
        <button class="btn" type="button" [disabled]="syncing()" (click)="sync()"><app-icon name="refresh" size="h-4 w-4" />{{ syncing() ? 'Syncing…' : 'Sync now' }}</button>
      }
    </app-page-header>

    @if (c(); as c) {
      @if (c.accessStatus !== 'Active') { <app-zoho-access (changed)="refresh()" /> }
      @if (c.accessStatus === 'Active' || c.accessStatus === 'AwaitingPayment') {
      @if (c.status === 'NotConnected' || c.status === 'NeedsReconnect') {
        <div class="card">
          @if (c.status === 'NeedsReconnect') {
            <p class="mb-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">Zoho stopped accepting this connection ({{ c.lastError }}). Connect again to carry on; nothing is fiscalised until then.</p>
          }
          <div class="card-title">{{ c.status === 'NeedsReconnect' ? 'Reconnect Zoho Books' : 'Connect Zoho Books' }}</div>
          <label class="block max-w-md"><span class="label">Zoho data centre (where you sign in to Zoho)</span>
            <select class="input" [(ngModel)]="region" name="region">@for (r of c.regions; track r.key) { <option [value]="r.key">{{ r.label }}</option> }</select></label>

          <div class="mt-4 flex flex-wrap gap-2 border-b border-slate-100 pb-2 text-sm dark:border-slate-800">
            <button type="button" class="rounded-full px-3 py-1" [class]="mode === 'platform' ? 'bg-brand-600 text-white' : 'bg-slate-100 dark:bg-slate-800'" [disabled]="!c.platformApp" (click)="mode = 'platform'">Sign in with Zoho</button>
            <button type="button" class="rounded-full px-3 py-1" [class]="mode === 'own' ? 'bg-brand-600 text-white' : 'bg-slate-100 dark:bg-slate-800'" (click)="mode = 'own'">Use my own Zoho app</button>
          </div>

          @if (mode === 'platform' && c.platformApp) {
            <ol class="my-3 list-decimal space-y-1 pl-5 text-sm text-slate-600 dark:text-slate-300">
              <li>Click Connect and sign in to Zoho.</li>
              <li>Allow access to Zoho Books.</li>
              <li>Choose the fiscal device here. Invoices are then fiscalised when you click, or automatically.</li>
            </ol>
            <button class="btn" type="button" [disabled]="busy()" (click)="connect()"><app-icon name="link" size="h-4 w-4" />{{ c.status === 'NeedsReconnect' ? 'Reconnect' : 'Connect' }}</button>
          } @else {
            <form class="mt-3" (ngSubmit)="connectOwn()">
              <ol class="mb-3 list-decimal space-y-1 pl-5 text-sm text-slate-600 dark:text-slate-300">
                <li>Open <a class="text-brand-600 hover:underline" href="https://api-console.zoho.com" target="_blank" rel="noopener">api-console.zoho.com</a> (signed in as your Zoho Books admin), choose <strong>Add Client → Self Client</strong> and create it.</li>
                <li>Copy its <strong>Client ID</strong> and <strong>Client Secret</strong> below.</li>
                <li>In the Self Client, open <strong>Generate Code</strong>: scope <code class="code">ZohoBooks.fullaccess.all</code>, time 10 minutes, any description, then Create. Paste the code below and click Connect straight away (it works once).</li>
              </ol>
              <div class="grid gap-3 sm:grid-cols-3">
                <label class="block"><span class="label">Client ID</span><input class="input font-mono" name="oid" [(ngModel)]="own.clientId" placeholder="1000.XXXXXXXX" autocomplete="off" /></label>
                <label class="block"><span class="label">Client secret</span>
                  <input class="input" type="password" name="osec" [(ngModel)]="own.clientSecret" autocomplete="new-password" [placeholder]="c.ownClientId ? 'saved, leave empty to keep' : ''" /></label>
                <label class="block"><span class="label">Generated code</span><input class="input font-mono" name="ocode" [(ngModel)]="own.grantCode" placeholder="1000.xxxx.xxxx" autocomplete="off" /></label>
              </div>
              <p class="hint mt-2">Using a server-based app instead? Add <code class="code">{{ c.redirectUri }}</code> as its redirect address, leave the code empty and click Connect to sign in.</p>
              <div class="mt-3 flex flex-wrap justify-end gap-2">
                @if (c.ownClientId) { <button type="button" class="btn-ghost text-red-600" (click)="forgetOwn()">Remove my app</button> }
                <button class="btn" type="submit" [disabled]="busy()"><app-icon name="link" size="h-4 w-4" />Connect</button>
              </div>
            </form>
          }
          @if (c.pricing.charge) {
            <p class="hint mt-4">The connection costs {{ c.pricing.monthlyFee | number: '1.2-2' }} {{ c.pricing.currency }} a month with {{ c.pricing.includedDocuments }} documents included, then
              {{ c.pricing.pricePerExtraDocument | number: '1.2-4' }} {{ c.pricing.currency }} per document. It is added to your subscription invoice.</p>
          }
        </div>
      } @else if (c.status === 'ChooseOrganization') {
        <div class="card">
          <div class="card-title">Which Zoho Books organisation?</div>
          <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Your Zoho account reaches several organisations. Choose the one whose invoices are fiscalised here.</p>
          <div class="grid gap-2 sm:grid-cols-2">
            @for (o of c.organizations; track o.id) {
              <button type="button" class="rounded-lg border border-slate-200 px-4 py-3 text-left hover:border-brand-500 dark:border-slate-700" (click)="choose(o.id)" [disabled]="busy()">
                <div class="font-medium">{{ o.name }}</div><div class="text-xs text-slate-500">ID {{ o.id }}{{ o.currencyCode ? ' · ' + o.currencyCode : '' }}</div>
              </button>
            }
          </div>
        </div>
      } @else {
        <div class="grid gap-4 lg:grid-cols-3">
          <div class="card lg:col-span-1">
            <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0!">Connection</div><app-status [value]="health()" /></div>
            <dl class="space-y-1.5 text-sm">
              <div class="flex justify-between gap-2"><dt class="text-slate-500">Organisation</dt><dd class="text-right font-medium">{{ c.organizationName }}</dd></div>
              <div class="flex justify-between gap-2"><dt class="text-slate-500">Connected by</dt><dd class="text-right">{{ c.connectedBy || '—' }}, {{ c.connectedAt | date: 'd MMM y' }}</dd></div>
              <div class="flex justify-between gap-2"><dt class="text-slate-500">Token renewed</dt><dd class="text-right">{{ (c.lastTokenRefreshAt | date: 'd MMM, HH:mm') || '—' }}</dd></div>
              <div class="flex justify-between gap-2"><dt class="text-slate-500">Access valid until</dt><dd class="text-right">{{ (c.accessTokenExpiresAt | date: 'HH:mm') || 'renewed when needed' }}</dd></div>
              <div class="flex justify-between gap-2"><dt class="text-slate-500">Last sync</dt><dd class="text-right">{{ (c.lastSyncAt | date: 'd MMM, HH:mm') || 'not yet' }}</dd></div>
              <div class="flex justify-between gap-2"><dt class="text-slate-500">Zoho calls today</dt><dd class="text-right">{{ c.apiCallsToday }} <span class="text-slate-400">({{ c.apiCallsTotal }} in all)</span></dd></div>
            </dl>
            @if (c.lastError) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-xs text-red-700 dark:bg-red-900/30 dark:text-red-300">{{ c.lastError }}</p> }
            <div class="mt-4 grid grid-cols-3 gap-2 text-center">
              <button type="button" class="rounded-lg bg-amber-50 p-2 dark:bg-amber-900/20" (click)="filter('Pending')"><div class="text-lg font-semibold">{{ c.counts.pending }}</div><div class="text-xs text-slate-500">Waiting</div></button>
              <button type="button" class="rounded-lg bg-emerald-50 p-2 dark:bg-emerald-900/20" (click)="filter('Fiscalised')"><div class="text-lg font-semibold">{{ c.counts.fiscalisedThisMonth }}</div><div class="text-xs text-slate-500">This month</div></button>
              <button type="button" class="rounded-lg bg-red-50 p-2 dark:bg-red-900/20" (click)="filter('Failed')"><div class="text-lg font-semibold">{{ c.counts.failed }}</div><div class="text-xs text-slate-500">Failed</div></button>
            </div>
            <button type="button" class="btn-ghost mt-4 w-full text-red-600" (click)="disconnect()"><app-icon name="x" size="h-4 w-4" />Disconnect</button>
          </div>

          <form class="card lg:col-span-2" (ngSubmit)="saveSettings()">
            <div class="card-title">Settings</div>
            <div class="grid gap-4 sm:grid-cols-2">
              <label class="block"><span class="label">Fiscal device</span>
                <select class="input" name="device" [(ngModel)]="s.deviceId">
                  <option [ngValue]="null">Choose…</option>
                  @for (d of devices.devices(); track d.id) { <option [ngValue]="d.id">{{ devices.label(d) }}</option> }
                </select></label>
              <label class="block"><span class="label">How the sales are paid</span>
                <select class="input" name="money" [(ngModel)]="s.defaultMoneyType">@for (m of moneyTypes; track m) { <option [value]="m">{{ m === 'Credit' ? 'Credit (on account)' : m }}</option> }</select></label>
              <label class="block"><span class="label">HS code for lines without one</span>
                <input class="input font-mono" name="hs" [(ngModel)]="s.defaultHsCode" placeholder="e.g. 99001000" inputmode="numeric" />
                <span class="hint">Put the HS code on items in Zoho (HSN/SAC or a field called "HS code") to override it.</span></label>
              <label class="block"><span class="label">Take documents dated from</span>
                <input class="input" type="date" name="start" [(ngModel)]="s.startDate" />
                <span class="hint">Older documents are left alone, so history is not fiscalised by mistake.</span></label>
              <label class="flex items-start gap-2 text-sm"><input type="checkbox" name="auto" [(ngModel)]="s.autoFiscalise" class="mt-0.5" />
                <span><strong>Fiscalise automatically</strong><br /><span class="text-slate-500">New invoices and credit notes are fiscalised within minutes. Off: they wait for you to click Fiscalise.</span></span></label>
              <label class="flex items-start gap-2 text-sm"><input type="checkbox" name="post" [(ngModel)]="s.postBack" class="mt-0.5" />
                <span><strong>Write the result back to Zoho</strong><br /><span class="text-slate-500">A comment with the fiscal number, verification code and link, the fiscal PDF, and your custom fields "Fiscal Number", "Verification Code" and "ZIMRA QR".</span></span></label>
            </div>
            @if (c.webhookUrl) {
              <div class="mt-4 rounded-lg border border-slate-200 p-3 text-sm dark:border-slate-700">
                <div class="mb-1 font-medium">Instant sync (optional)</div>
                <p class="mb-2 text-xs text-slate-500">In Zoho Books, add a workflow rule on invoices and credit notes (on create and edit) with a webhook to this address. Without it the gateway checks every few minutes.</p>
                <div class="flex items-center gap-2"><code class="code min-w-0 flex-1 truncate text-xs">{{ c.webhookUrl }}</code>
                  <button type="button" class="icon-btn" (click)="copy(c.webhookUrl)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
              </div>
            }
            <div class="mt-4 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Save settings</button></div>
          </form>
        </div>

        <div class="card mt-4">
          <div class="mb-3 flex flex-wrap items-end justify-between gap-3">
            <div class="flex flex-wrap gap-1">
              @for (f of filters; track f.key) {
                <button type="button" class="rounded-full px-3 py-1 text-sm" [class]="status() === f.key ? 'bg-brand-600 text-white' : 'bg-slate-100 dark:bg-slate-800'" (click)="filter(f.key)">{{ f.label }}</button>
              }
            </div>
            <input class="input w-56" placeholder="Number or customer" [(ngModel)]="search" (keyup.enter)="load(1)" name="search" />
          </div>
          <div class="overflow-x-auto">
            <table class="table w-full text-sm">
              <thead><tr><th>Zoho document</th><th>Customer</th><th>Date</th><th class="text-right">Total</th><th>Status</th><th>Fiscal no.</th><th></th></tr></thead>
              <tbody>
                @for (d of docs()?.items ?? []; track d.id) {
                  <tr>
                    <td><div class="font-medium">{{ d.externalNumber }}</div><div class="text-xs text-slate-500">{{ d.kind === 'creditnote' ? 'Credit note' : 'Invoice' }} · {{ d.externalStatus }}</div></td>
                    <td class="max-w-48 truncate">{{ d.customerName }}</td>
                    <td class="whitespace-nowrap">{{ d.documentDate | date: 'd MMM y' }}</td>
                    <td class="whitespace-nowrap text-right">{{ d.total | number: '1.2-2' }} {{ d.currency }}</td>
                    <td>
                      <app-status [value]="d.status" />
                      @if (d.changedAfterFiscalised) { <div class="mt-1 text-xs text-amber-700 dark:text-amber-300" title="The fiscal record cannot change. Raise a credit note in Zoho Books.">Changed in Zoho after fiscalising</div> }
                      @if (d.error) { <div class="mt-1 max-w-72 text-xs text-red-600 dark:text-red-400">{{ d.error }}</div> }
                      @if (d.postBackError) { <div class="mt-1 max-w-72 text-xs text-amber-700 dark:text-amber-300">Not all written back: {{ d.postBackError }}</div> }
                    </td>
                    <td class="whitespace-nowrap font-mono text-xs">{{ d.fiscalInvoiceNo || '—' }}</td>
                    <td class="whitespace-nowrap text-right">
                      @if (d.status === 'Pending' || d.status === 'Failed') {
                        <button type="button" class="btn-ghost px-2! py-1! text-xs" [disabled]="working() === d.id" (click)="fiscalise(d)">{{ working() === d.id ? 'Working…' : d.status === 'Failed' ? 'Retry' : 'Fiscalise' }}</button>
                      }
                      @if (d.status !== 'Fiscalised') { <button type="button" class="btn-ghost px-2! py-1! text-xs" (click)="skip(d)">{{ d.status === 'Skipped' ? 'Bring back' : 'Skip' }}</button> }
                      @if (d.status === 'Fiscalised') {
                        <a class="btn-ghost px-2! py-1! text-xs" [href]="'/api/v1/receipts/' + d.receiptId + '/pdf?format=a4'" (click)="pdf($event, d)">PDF</a>
                        @if (!d.postedBackAt || d.postBackError) { <button type="button" class="btn-ghost px-2! py-1! text-xs" (click)="postBack(d)">Write back</button> }
                      }
                      @if (d.externalUrl) { <a class="icon-btn inline-flex" [href]="d.externalUrl" target="_blank" rel="noopener" title="Open in Zoho Books"><app-icon name="link" size="h-4 w-4" /></a> }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="7" class="py-10 text-center text-slate-500">Nothing here yet. Raise an invoice in Zoho Books (not a draft) and click Sync now.</td></tr>
                }
              </tbody>
            </table>
          </div>
          @if (docs(); as p) { <app-pager [page]="p.page" [pageSize]="p.pageSize" [total]="p.total" (pageChange)="load($event)" /> }
        </div>
        <app-zoho-own-api />
        <app-zoho-bridge-grants />
      }
      }
    }
  `,
})
export class IntegrationsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly devices = inject(DeviceStore);
  readonly moneyTypes = MONEY_TYPES;
  readonly filters = [
    { key: '', label: 'All' }, { key: 'Pending', label: 'Waiting' }, { key: 'Failed', label: 'Failed' }, { key: 'Fiscalised', label: 'Fiscalised' },
    { key: 'Changed', label: 'Changed after' }, { key: 'NotPostedBack', label: 'Not written back' }, { key: 'Skipped', label: 'Skipped' },
  ];

  readonly c = signal<ZohoConnection | null>(null);
  readonly docs = signal<IntegrationDocumentPage | null>(null);
  readonly status = signal('');
  readonly busy = signal(false);
  readonly syncing = signal(false);
  readonly working = signal<string | null>(null);
  readonly health = computed(() => { const c = this.c(); return !c ? '' : c.lastError ? 'Failed' : c.counts.failed > 0 ? 'Pending' : 'Active'; });
  region = 'com';
  mode: 'platform' | 'own' = 'platform';
  own = { clientId: '', clientSecret: '', grantCode: '' };
  search = '';
  s: { deviceId: string | null; defaultHsCode: string; defaultMoneyType: string; startDate: string; autoFiscalise: boolean; postBack: boolean } =
    { deviceId: null, defaultHsCode: '', defaultMoneyType: 'Credit', startDate: '', autoFiscalise: false, postBack: true };

  async ngOnInit(): Promise<void> {
    const back = this.route.snapshot.queryParamMap.get('zoho');
    if (back) {
      const reason = this.route.snapshot.queryParamMap.get('reason');
      if (RETURN_MESSAGES[back]) this.toast.ok(RETURN_MESSAGES[back]);
      else this.toast.err(new Error('Zoho Books could not be connected' + (reason ? ` (${reason})` : '') + '. Try again; if it keeps failing, ask the platform administrator to check the Zoho app settings.'));
      await this.router.navigate([], { queryParams: {}, replaceUrl: true });
    }
    void this.devices.load().catch(() => undefined);
    await this.refresh();
  }

  private fill(c: ZohoConnection): void {
    this.c.set(c);
    if (!c.platformApp || c.ownClientId) this.mode = 'own';
    if (c.ownClientId && !this.own.clientId) this.own.clientId = c.ownClientId;
    this.s = { deviceId: c.deviceId, defaultHsCode: c.defaultHsCode ?? '', defaultMoneyType: c.defaultMoneyType || 'Credit', startDate: c.startDate ? c.startDate.slice(0, 10) : '', autoFiscalise: c.autoFiscalise, postBack: c.postBack };
  }

  async refresh(): Promise<void> {
    try {
      this.fill(await this.api.get<ZohoConnection>('/api/v1/integrations/zoho'));
      if (this.c()?.status === 'Connected') await this.load(1);
    } catch (e) { this.toast.err(e); }
  }

  async load(page: number): Promise<void> {
    try { this.docs.set(await this.api.get<IntegrationDocumentPage>('/api/v1/integrations/zoho/documents', { status: this.status(), search: this.search, page, pageSize: 25 })); }
    catch (e) { this.toast.err(e); }
  }

  filter(key: string): void { this.status.set(key); void this.load(1); }

  async connect(): Promise<void> {
    this.busy.set(true);
    try { const r = await this.api.post<{ authorizeUrl: string }>('/api/v1/integrations/zoho/connect', { region: this.region }); window.location.href = r.authorizeUrl; }
    catch (e) { this.toast.err(e); this.busy.set(false); }
  }

  /** The company's own Zoho app: a Self Client code connects at once; without a code, its server-based app signs in. */
  async connectOwn(): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.post<{ authorizeUrl: string | null; connection: ZohoConnection | null }>('/api/v1/integrations/zoho/own-app',
        { region: this.region, clientId: this.own.clientId.trim(), clientSecret: this.own.clientSecret || null, grantCode: this.own.grantCode.trim() || null });
      if (r.authorizeUrl) { window.location.href = r.authorizeUrl; return; }
      this.own = { ...this.own, clientSecret: '', grantCode: '' };
      if (r.connection) this.fill(r.connection);
      this.toast.ok(r.connection?.status === 'ChooseOrganization' ? 'Connected. Choose the organisation below.' : 'Zoho Books is connected.');
      if (this.c()?.status === 'Connected') await this.load(1);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async forgetOwn(): Promise<void> {
    if (!(await this.dialog.confirm('Remove your own Zoho app? You will need to connect again.', 'Remove', true))) return;
    try { this.fill(await this.api.delete<ZohoConnection>('/api/v1/integrations/zoho/own-app')); this.own = { clientId: '', clientSecret: '', grantCode: '' }; } catch (e) { this.toast.err(e); }
  }

  async choose(id: string): Promise<void> {
    this.busy.set(true);
    try { this.fill(await this.api.post<ZohoConnection>('/api/v1/integrations/zoho/organization', { organizationId: id })); this.toast.ok('Connected. Now choose the fiscal device.'); await this.load(1); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async saveSettings(): Promise<void> {
    this.busy.set(true);
    try {
      this.fill(await this.api.put<ZohoConnection>('/api/v1/integrations/zoho/settings', { ...this.s, defaultHsCode: this.s.defaultHsCode || null, startDate: this.s.startDate || null }));
      this.toast.ok('Settings saved.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async sync(): Promise<void> {
    this.syncing.set(true);
    try {
      const r = await this.api.post<ZohoSyncResult>('/api/v1/integrations/zoho/sync');
      if (r.error) this.toast.err(new Error(r.error));
      else this.toast.ok(`Synced: ${r.new} new, ${r.updated} updated` + (r.fiscalised || r.failed ? `, ${r.fiscalised} fiscalised, ${r.failed} failed` : '') + '.');
      await this.refresh();
    } catch (e) { this.toast.err(e); } finally { this.syncing.set(false); }
  }

  private replace(d: IntegrationDocument): void {
    const p = this.docs(); if (!p) return;
    this.docs.set({ ...p, items: p.items.map(x => (x.id === d.id ? d : x)) });
  }

  async fiscalise(d: IntegrationDocument): Promise<void> {
    this.working.set(d.id);
    try {
      const r = await this.api.post<IntegrationDocument>(`/api/v1/integrations/zoho/documents/${d.id}/fiscalise`);
      this.replace(r);
      if (r.status === 'Fiscalised') this.toast.ok(`${r.externalNumber} fiscalised.`); else this.toast.err(new Error(r.error ?? 'Not fiscalised.'));
    } catch (e) { this.toast.err(e); } finally { this.working.set(null); }
  }

  async skip(d: IntegrationDocument): Promise<void> {
    try { this.replace(await this.api.post<IntegrationDocument>(`/api/v1/integrations/zoho/documents/${d.id}/skip`)); } catch (e) { this.toast.err(e); }
  }

  async postBack(d: IntegrationDocument): Promise<void> {
    try { const r = await this.api.post<IntegrationDocument>(`/api/v1/integrations/zoho/documents/${d.id}/post-back`); this.replace(r); if (!r.postBackError) this.toast.ok('Written back to Zoho.'); }
    catch (e) { this.toast.err(e); }
  }

  /** The PDF needs the sign-in header, so it is fetched and opened as a file. */
  async pdf(ev: Event, d: IntegrationDocument): Promise<void> {
    ev.preventDefault();
    try {
      await this.api.open(`/api/v1/receipts/${d.receiptId}/pdf`, { format: 'a4' });
    } catch (e) { this.toast.err(e); }
  }

  async disconnect(): Promise<void> {
    if (!(await this.dialog.confirm('Disconnect Zoho Books? Nothing more is pulled or fiscalised. Documents already fiscalised stay; you can connect again later.', 'Disconnect', true))) return;
    try { await this.api.delete('/api/v1/integrations/zoho'); this.toast.ok('Disconnected.'); this.docs.set(null); await this.refresh(); } catch (e) { this.toast.err(e); }
  }

  async copy(text: string): Promise<void> { try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { /* ignore */ } }
}
