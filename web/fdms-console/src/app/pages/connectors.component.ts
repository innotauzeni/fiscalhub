import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { IntegrationDocument, IntegrationDocumentPage, MONEY_TYPES, ZohoSyncResult } from '../core/integrations.models';
import { DialogService, ToastService } from '../core/ui.services';
import { PagerComponent } from '../shared/list.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';
import { IntegrationAccess } from './zoho-access.component';

interface Pricing { charge: boolean; monthlyFee: number; currency: string; includedDocuments: number; pricePerExtraDocument: number; setupFee: number; }
interface Counts { pending: number; fiscalised: number; failed: number; skipped: number; fiscalisedThisMonth: number; notPostedBack: number; }
export interface ConnectorStatus {
  product: string; label: string; auth: 'oauth' | 'apikey'; accessStatus: string; available: boolean; unavailableReason: string | null;
  connectionId: string | null; status: 'NotConnected' | 'ChooseOrganization' | 'Connected' | 'NeedsReconnect'; organizationId: string | null; organizationName: string | null;
  organizations: { id: string; name: string; currency: string | null }[]; connectedBy: string | null; connectedAt: string | null; lastSyncAt: string | null;
  lastError: string | null; lastErrorAt: string | null; baseUrl: string | null; database: string | null; username: string | null;
  settings: { autoFiscalise: boolean; deviceId: string | null; defaultHsCode: string | null; defaultMoneyType: string; postBack: boolean; startDate: string | null };
  counts: Counts; pricing: Pricing;
}

/** Where each product keeps what the gateway needs, shown under the settings. */
const TIPS: Record<string, { hs: string; creditNote: string; writeBack: string; connect: string[] }> = {
  quickbooks: {
    hs: 'Put the HS code in each product\'s SKU in QuickBooks to override it.',
    creditNote: 'Add a custom field called "Invoice" to credit memos and fill in the invoice number it credits.',
    writeBack: 'The fiscal number, verification code and link go into the private note, and the fiscal PDF is attached.',
    connect: ['Click Connect and sign in to QuickBooks Online.', 'Choose the company and allow access.', 'Choose the fiscal device here.'],
  },
  xero: {
    hs: 'Use the HS code as the item code in Xero to override it.',
    creditNote: 'Allocate each credit note to the invoice it credits.',
    writeBack: 'A history note with the fiscal details is added and the fiscal PDF is attached (shown online).',
    connect: ['Click Connect and sign in to Xero.', 'Allow access to the organisation(s).', 'Choose the organisation and the fiscal device here.'],
  },
  sage: {
    hs: 'Use the HS code as the product\'s item code in Sage to override it.',
    creditNote: 'Fill in the original invoice number on each credit note.',
    writeBack: 'The fiscal details are added to the notes and the fiscal PDF is attached.',
    connect: ['Click Connect and sign in to Sage Accounting.', 'Allow access.', 'Choose the business and the fiscal device here.'],
  },
  odoo: {
    hs: 'Use the HS code as the product\'s internal reference in Odoo to override it.',
    creditNote: 'Raise credit notes with "Add credit note" on the invoice, so they point at it.',
    writeBack: 'A note with the fiscal details is posted in the document\'s chatter and the fiscal PDF is attached.',
    connect: [],
  },
};

const RETURN_MESSAGES: Record<string, string> = { connected: 'Connected.', choose: 'Signed in. Choose the company to use below.', denied: 'The sign-in was cancelled.' };

/**
 * QuickBooks Online, Xero, Sage Accounting and Odoo: each applied for and paid on its own, then connected once. Invoices and credit notes are pulled,
 * fiscalised (automatically or on a click) and the fiscal number, verification code and PDF are written back.
 */
@Component({
  selector: 'app-connectors',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DecimalPipe, RouterLink, IconComponent, PageHeaderComponent, StatusComponent, PagerComponent],
  template: `
    <app-page-header title="Accounting connectors" subtitle="Fiscalise the invoices and credit notes you raise in QuickBooks, Xero, Sage or Odoo, and see the ZIMRA result back there.">
      @if (c()?.status === 'Connected' && c()?.accessStatus === 'Active') {
        <button class="btn" type="button" [disabled]="syncing()" (click)="sync()"><app-icon name="refresh" size="h-4 w-4" />{{ syncing() ? 'Syncing…' : 'Sync now' }}</button>
      }
    </app-page-header>

    <div class="mb-4 grid grid-cols-2 gap-2 sm:grid-cols-4">
      @for (p of products(); track p.product) {
        <button type="button" class="rounded-xl border px-3 py-3 text-left transition" (click)="pick(p.product)"
          [class]="p.product === product() ? 'border-brand-500 bg-brand-50 dark:bg-brand-900/20' : 'border-slate-200 hover:border-brand-400 dark:border-slate-700'">
          <div class="font-medium">{{ p.label }}</div>
          <div class="mt-1 text-xs text-slate-500">{{ badge(p) }}</div>
        </button>
      } @empty { <p class="col-span-full text-sm text-slate-500">Loading…</p> }
    </div>

    @if (c(); as c) {
      <!-- 1. the application -->
      @if (c.accessStatus !== 'Active') {
        <div class="card mb-4">
          <div class="card-title flex items-center gap-2"><app-icon name="link" size="h-4 w-4" />{{ c.label }} integration</div>
          @switch (c.accessStatus) {
            @case ('Requested') { <p class="text-sm">⏳ Your application was sent{{ access()?.requestedAt ? ' on ' + (access()!.requestedAt | date: 'd MMM y, HH:mm') : '' }} and is being reviewed. You will get a notification and an email when it is decided.</p> }
            @case ('AwaitingPayment') {
              <p class="text-sm">✅ Approved. You can connect {{ c.label }} and choose your settings below now. It starts working as soon as invoice <strong>{{ access()?.invoiceNumber }}</strong>
                (set-up {{ access()?.setupFee | number: '1.2-2' }} {{ access()?.currency }} plus VAT) is paid.</p>
              @if (access()?.decisionNote) { <p class="hint mt-1">{{ access()?.decisionNote }}</p> }
              <a class="btn mt-3" routerLink="/billing"><app-icon name="card" size="h-4 w-4" />Pay under Billing</a>
            }
            @case ('Suspended') { <p class="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{{ c.label }} is suspended for your company: {{ access()?.decisionNote }}. Contact the platform operator.</p> }
            @default {
              @if (c.accessStatus === 'Rejected') { <p class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-900/25 dark:text-amber-200">Your last application was not approved: {{ access()?.decisionNote }}. You can apply again.</p> }
              <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Apply to connect {{ c.label }}. We review every application and switch it on
                @if (c.pricing.setupFee > 0) { after a one-time set-up fee of {{ c.pricing.setupFee | number: '1.2-2' }} {{ c.pricing.currency }} } @else { once approved }.
                @if (c.pricing.charge) { Then {{ c.pricing.monthlyFee | number: '1.2-2' }} {{ c.pricing.currency }} a month with {{ c.pricing.includedDocuments }} documents included and
                  {{ c.pricing.pricePerExtraDocument | number: '1.2-4' }} {{ c.pricing.currency }} per extra document, on your subscription invoice. }</p>
              @if (!c.available) { <p class="hint mb-3">{{ c.unavailableReason }}</p> }
              <form (ngSubmit)="apply()">
                <label class="block"><span class="label">Tell us about your business (optional)</span>
                  <textarea class="input" rows="3" name="note" [(ngModel)]="note" maxlength="1000" placeholder="e.g. About 300 invoices a month, two branches."></textarea></label>
                <div class="mt-3 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Apply for {{ c.label }}</button></div>
              </form>
            }
          }
        </div>
      }

      @if (c.accessStatus === 'Active' || c.accessStatus === 'AwaitingPayment') {
        <!-- 2. connecting -->
        @if (c.status === 'NotConnected' || c.status === 'NeedsReconnect') {
          <div class="card">
            @if (c.status === 'NeedsReconnect') {
              <p class="mb-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{{ c.label }} stopped accepting this connection ({{ c.lastError }}). Connect again to carry on; nothing is fiscalised until then.</p>
            }
            <div class="card-title">{{ c.status === 'NeedsReconnect' ? 'Reconnect' : 'Connect' }} {{ c.label }}</div>
            @if (c.auth === 'oauth') {
              @if (!c.available) { <p class="text-sm text-slate-600 dark:text-slate-300">{{ c.unavailableReason }}</p> }
              @else {
                <ol class="my-3 list-decimal space-y-1 pl-5 text-sm text-slate-600 dark:text-slate-300">@for (s of tip().connect; track s) { <li>{{ s }}</li> }</ol>
                <button class="btn" type="button" [disabled]="busy()" (click)="connect()"><app-icon name="link" size="h-4 w-4" />{{ c.status === 'NeedsReconnect' ? 'Reconnect' : 'Connect' }}</button>
              }
            } @else {
              <form (ngSubmit)="connectOdoo()">
                <ol class="mb-3 list-decimal space-y-1 pl-5 text-sm text-slate-600 dark:text-slate-300">
                  <li>In Odoo, open your profile (top right) → <strong>My Profile → Account Security → New API Key</strong>, name it "FiscalHub" and copy the key.</li>
                  <li>Enter your Odoo address, database name and the email you sign in with, paste the key and click Connect.</li>
                </ol>
                <div class="grid gap-3 sm:grid-cols-2">
                  <label class="block"><span class="label">Odoo address</span><input class="input" name="ourl" [(ngModel)]="odoo.url" placeholder="https://mycompany.odoo.com" autocomplete="off" /></label>
                  <label class="block"><span class="label">Database</span><input class="input font-mono" name="odb" [(ngModel)]="odoo.database" placeholder="mycompany" autocomplete="off" />
                    <span class="hint">On Odoo Online it is usually the first part of the address.</span></label>
                  <label class="block"><span class="label">Sign-in email</span><input class="input" name="ouser" [(ngModel)]="odoo.username" autocomplete="off" /></label>
                  <label class="block"><span class="label">API key</span><input class="input font-mono" type="password" name="okey" [(ngModel)]="odoo.apiKey" autocomplete="new-password" [placeholder]="c.baseUrl ? 'saved, leave empty to keep' : ''" /></label>
                </div>
                <div class="mt-3 flex justify-end"><button class="btn" type="submit" [disabled]="busy()"><app-icon name="link" size="h-4 w-4" />Connect</button></div>
              </form>
            }
          </div>
        } @else if (c.status === 'ChooseOrganization') {
          <div class="card">
            <div class="card-title">Which {{ c.label }} company?</div>
            <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">The sign-in reaches several companies. Choose the one whose invoices are fiscalised here.</p>
            <div class="grid gap-2 sm:grid-cols-2">
              @for (o of c.organizations; track o.id) {
                <button type="button" class="rounded-lg border border-slate-200 px-4 py-3 text-left hover:border-brand-500 dark:border-slate-700" (click)="choose(o.id)" [disabled]="busy()">
                  <div class="font-medium">{{ o.name }}</div><div class="text-xs text-slate-500">ID {{ o.id }}{{ o.currency ? ' · ' + o.currency : '' }}</div>
                </button>
              }
            </div>
          </div>
        } @else {
          <!-- 3. connected -->
          <div class="grid gap-4 lg:grid-cols-3">
            <div class="card lg:col-span-1">
              <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0!">Connection</div><app-status [value]="health()" /></div>
              <dl class="space-y-1.5 text-sm">
                <div class="flex justify-between gap-2"><dt class="text-slate-500">Company</dt><dd class="text-right font-medium">{{ c.organizationName }}</dd></div>
                @if (c.baseUrl) { <div class="flex justify-between gap-2"><dt class="text-slate-500">Address</dt><dd class="truncate text-right">{{ c.baseUrl }}</dd></div> }
                <div class="flex justify-between gap-2"><dt class="text-slate-500">Connected by</dt><dd class="text-right">{{ c.connectedBy || '—' }}, {{ c.connectedAt | date: 'd MMM y' }}</dd></div>
                <div class="flex justify-between gap-2"><dt class="text-slate-500">Last sync</dt><dd class="text-right">{{ (c.lastSyncAt | date: 'd MMM, HH:mm') || 'not yet' }}</dd></div>
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
                  <span class="hint">{{ tip().hs }}</span></label>
                <label class="block"><span class="label">Take documents dated from</span>
                  <input class="input" type="date" name="start" [(ngModel)]="s.startDate" />
                  <span class="hint">Older documents are left alone, so history is not fiscalised by mistake.</span></label>
                <label class="flex items-start gap-2 text-sm"><input type="checkbox" name="auto" [(ngModel)]="s.autoFiscalise" class="mt-0.5" />
                  <span><strong>Fiscalise automatically</strong><br /><span class="text-slate-500">New invoices and credit notes are fiscalised within minutes. Off: they wait for you to click Fiscalise.</span></span></label>
                <label class="flex items-start gap-2 text-sm"><input type="checkbox" name="post" [(ngModel)]="s.postBack" class="mt-0.5" />
                  <span><strong>Write the result back to {{ c.label }}</strong><br /><span class="text-slate-500">{{ tip().writeBack }}</span></span></label>
              </div>
              <p class="hint mt-3">Credit notes: {{ tip().creditNote }} A buyer is named on the fiscal invoice only with a 10-digit TIN in their tax number.</p>
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
              <input class="input w-full sm:w-56" placeholder="Number or customer" [(ngModel)]="search" (keyup.enter)="load(1)" name="search" />
            </div>
            <div class="overflow-x-auto">
              <table class="table w-full text-sm">
                <thead><tr><th>{{ c.label }} document</th><th>Customer</th><th>Date</th><th class="text-right">Total</th><th>Status</th><th>Fiscal no.</th><th></th></tr></thead>
                <tbody>
                  @for (d of docs()?.items ?? []; track d.id) {
                    <tr>
                      <td><div class="font-medium">{{ d.externalNumber }}</div><div class="text-xs text-slate-500">{{ d.kind === 'creditnote' ? 'Credit note' : 'Invoice' }} · {{ d.externalStatus }}</div></td>
                      <td class="max-w-48 truncate">{{ d.customerName }}</td>
                      <td class="whitespace-nowrap">{{ d.documentDate | date: 'd MMM y' }}</td>
                      <td class="whitespace-nowrap text-right">{{ d.total | number: '1.2-2' }} {{ d.currency }}</td>
                      <td>
                        <app-status [value]="d.status" />
                        @if (d.changedAfterFiscalised) { <div class="mt-1 text-xs text-amber-700 dark:text-amber-300">Changed after fiscalising: raise a credit note</div> }
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
                      </td>
                    </tr>
                  } @empty {
                    <tr><td colspan="7" class="py-10 text-center text-slate-500">Nothing here yet. Raise an invoice in {{ c.label }} (not a draft) and click Sync now.</td></tr>
                  }
                </tbody>
              </table>
            </div>
            @if (docs(); as p) { <app-pager [page]="p.page" [pageSize]="p.pageSize" [total]="p.total" (pageChange)="load($event)" /> }
          </div>
        }
      }
    }
  `,
})
export class ConnectorsComponent implements OnInit {
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

  readonly products = signal<ConnectorStatus[]>([]);
  readonly product = signal('quickbooks');
  readonly c = computed(() => this.products().find(p => p.product === this.product()) ?? null);
  readonly tip = computed(() => TIPS[this.product()] ?? TIPS['quickbooks']);
  readonly access = signal<IntegrationAccess | null>(null);
  readonly docs = signal<IntegrationDocumentPage | null>(null);
  readonly status = signal('');
  readonly busy = signal(false);
  readonly syncing = signal(false);
  readonly working = signal<string | null>(null);
  readonly health = computed(() => { const c = this.c(); return !c ? '' : c.lastError ? 'Failed' : c.counts.failed > 0 ? 'Pending' : 'Active'; });
  note = '';
  search = '';
  odoo = { url: '', database: '', username: '', apiKey: '' };
  s: { deviceId: string | null; defaultHsCode: string; defaultMoneyType: string; startDate: string; autoFiscalise: boolean; postBack: boolean } =
    { deviceId: null, defaultHsCode: '', defaultMoneyType: 'Credit', startDate: '', autoFiscalise: false, postBack: true };

  private get base(): string { return `/api/v1/integrations/connectors/${this.product()}`; }

  async ngOnInit(): Promise<void> {
    const q = this.route.snapshot.queryParamMap;
    if (q.get('product')) this.product.set(q.get('product')!);
    const back = q.get('connector');
    if (back) {
      const reason = q.get('reason');
      if (RETURN_MESSAGES[back]) this.toast.ok(RETURN_MESSAGES[back]);
      else this.toast.err(new Error('The connection did not work' + (reason ? ` (${reason})` : '') + '. Try again; if it keeps failing, ask the platform operator to check the app settings.'));
      await this.router.navigate([], { queryParams: { product: this.product() }, replaceUrl: true });
    }
    void this.devices.load().catch(() => undefined);
    await this.refresh();
  }

  badge(p: ConnectorStatus): string {
    if (p.accessStatus === 'Active') return p.status === 'Connected' ? `Connected · ${p.counts.pending} waiting` : p.status === 'NeedsReconnect' ? 'Needs reconnecting' : 'Not connected yet';
    return ({ Requested: 'Application sent', AwaitingPayment: 'Approved, awaiting payment', Suspended: 'Suspended', Rejected: 'Not approved' } as Record<string, string>)[p.accessStatus] ?? 'Apply to use';
  }

  async pick(product: string): Promise<void> {
    if (product === this.product()) return;
    this.product.set(product); this.docs.set(null); this.status.set(''); this.search = '';
    await this.router.navigate([], { queryParams: { product }, replaceUrl: true });
    await this.select();
  }

  private fill(c: ConnectorStatus): void {
    this.products.update(list => list.map(p => (p.product === c.product ? c : p)));
    const st = c.settings;
    this.s = { deviceId: st.deviceId, defaultHsCode: st.defaultHsCode ?? '', defaultMoneyType: st.defaultMoneyType || 'Credit', startDate: st.startDate ? st.startDate.slice(0, 10) : '', autoFiscalise: st.autoFiscalise, postBack: st.postBack };
    if (c.auth === 'apikey') this.odoo = { url: c.baseUrl ?? '', database: c.database ?? '', username: c.username ?? '', apiKey: '' };
  }

  async refresh(): Promise<void> {
    try {
      this.products.set(await this.api.get<ConnectorStatus[]>('/api/v1/integrations/connectors'));
      if (!this.c() && this.products().length) this.product.set(this.products()[0].product);
      await this.select();
    } catch (e) { this.toast.err(e); }
  }

  private async select(): Promise<void> {
    const c = this.c(); if (!c) return;
    this.fill(c);
    try { this.access.set(c.accessStatus === 'None' ? null : await this.api.get<IntegrationAccess>('/api/v1/integrations/access', { provider: c.product })); } catch { this.access.set(null); }
    if (c.status === 'Connected' && (c.accessStatus === 'Active' || c.accessStatus === 'AwaitingPayment')) await this.load(1);
  }

  async load(page: number): Promise<void> {
    try { this.docs.set(await this.api.get<IntegrationDocumentPage>(`${this.base}/documents`, { status: this.status(), search: this.search, page, pageSize: 25 })); }
    catch (e) { this.toast.err(e); }
  }

  filter(key: string): void { this.status.set(key); void this.load(1); }

  async apply(): Promise<void> {
    this.busy.set(true);
    try {
      this.access.set(await this.api.post<IntegrationAccess>('/api/v1/integrations/access', { provider: this.product(), note: this.note || null }));
      this.toast.ok('Application sent. We will let you know.'); this.note = '';
      await this.refresh();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async connect(): Promise<void> {
    this.busy.set(true);
    try { const r = await this.api.post<{ authorizeUrl: string }>(`${this.base}/connect`); window.location.href = r.authorizeUrl; }
    catch (e) { this.toast.err(e); this.busy.set(false); }
  }

  async connectOdoo(): Promise<void> {
    this.busy.set(true);
    try {
      const c = await this.api.post<ConnectorStatus>('/api/v1/integrations/connectors/odoo/key', { ...this.odoo, url: this.odoo.url.trim() });
      this.fill(c);
      this.toast.ok(c.status === 'ChooseOrganization' ? 'Connected. Choose the company below.' : 'Odoo is connected.');
      if (c.status === 'Connected') await this.load(1);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async choose(id: string): Promise<void> {
    this.busy.set(true);
    try { this.fill(await this.api.post<ConnectorStatus>(`${this.base}/organization`, { organizationId: id })); this.toast.ok('Connected. Now choose the fiscal device.'); await this.load(1); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async saveSettings(): Promise<void> {
    this.busy.set(true);
    try {
      this.fill(await this.api.put<ConnectorStatus>(`${this.base}/settings`, { ...this.s, defaultHsCode: this.s.defaultHsCode || null, startDate: this.s.startDate || null }));
      this.toast.ok('Settings saved.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async sync(): Promise<void> {
    this.syncing.set(true);
    try {
      const r = await this.api.post<ZohoSyncResult>(`${this.base}/sync`);
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
      const r = await this.api.post<IntegrationDocument>(`${this.base}/documents/${d.id}/fiscalise`);
      this.replace(r);
      if (r.status === 'Fiscalised') this.toast.ok(`${r.externalNumber} fiscalised.`); else this.toast.err(new Error(r.error ?? 'Not fiscalised.'));
    } catch (e) { this.toast.err(e); } finally { this.working.set(null); }
  }

  async skip(d: IntegrationDocument): Promise<void> {
    try { this.replace(await this.api.post<IntegrationDocument>(`${this.base}/documents/${d.id}/skip`)); } catch (e) { this.toast.err(e); }
  }

  async postBack(d: IntegrationDocument): Promise<void> {
    try { const r = await this.api.post<IntegrationDocument>(`${this.base}/documents/${d.id}/post-back`); this.replace(r); if (!r.postBackError) this.toast.ok('Written back.'); }
    catch (e) { this.toast.err(e); }
  }

  async pdf(ev: Event, d: IntegrationDocument): Promise<void> {
    ev.preventDefault();
    try { await this.api.open(`/api/v1/receipts/${d.receiptId}/pdf`, { format: 'a4' }); } catch (e) { this.toast.err(e); }
  }

  async disconnect(): Promise<void> {
    const label = this.c()?.label ?? 'it';
    if (!(await this.dialog.confirm(`Disconnect ${label}? Nothing more is pulled or fiscalised. Documents already fiscalised stay; you can connect again later.`, 'Disconnect', true))) return;
    try { await this.api.delete(this.base); this.toast.ok('Disconnected.'); this.docs.set(null); await this.refresh(); } catch (e) { this.toast.err(e); }
  }
}
