import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';

interface ConnectorApp { product: string; label: string; auth: 'oauth' | 'apikey'; clientId: string | null; hasSecret: boolean; sandbox: boolean; redirectUri: string; configured: boolean; }
interface Pricing { charge: boolean; monthlyFee: number; currency: string; includedDocuments: number; pricePerExtraDocument: number; bridgeMonthlyFee: number; setupFee: number; }

/** Where the owner registers each product's app (the redirect address to give the provider). */
const DEV_PORTALS: Record<string, { url: string; name: string; note: string }> = {
  quickbooks: { url: 'https://developer.intuit.com', name: 'developer.intuit.com', note: 'Create an app with the Accounting scope. Use the Development keys with "Sandbox" ticked to test, then the Production keys.' },
  xero: { url: 'https://developer.xero.com/app/manage', name: 'developer.xero.com', note: 'Create a "Web app". Xero allows 25 connected organisations until the app is certified.' },
  sage: { url: 'https://developerselfservice.sageone.com', name: 'developerselfservice.sageone.com', note: 'Create an app for Sage Business Cloud Accounting.' },
};

/** The platform owner: each accounting connector's app at the provider (the secret is write-only) and its prices. */
@Component({
  selector: 'app-connector-apps',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent],
  template: `
    <div class="card mt-6">
      <div class="card-title">QuickBooks, Xero, Sage and Odoo</div>
      <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Each product is sold on its own with its own prices. QuickBooks, Xero and Sage need an app registered with the provider; Odoo needs none (each company gives its own address and API key).</p>
      <div class="mb-4 flex flex-wrap gap-1">
        @for (a of apps(); track a.product) {
          <button type="button" class="rounded-full px-3 py-1 text-sm" [class]="product() === a.product ? 'bg-brand-600 text-white' : 'bg-slate-100 dark:bg-slate-800'" (click)="pick(a.product)">
            {{ a.label }} <span class="ml-1 text-xs opacity-75">{{ a.configured ? '✓' : '·' }}</span></button>
        }
      </div>

      @if (current(); as c) {
        <div class="grid gap-4 lg:grid-cols-2">
          <form (ngSubmit)="saveApp()">
            <div class="mb-2 flex items-center justify-between"><div class="font-medium">{{ c.label }} app</div>
              <span class="badge" [class.badge-ok]="c.configured" [class.badge-warn]="!c.configured">{{ c.configured ? 'Ready' : 'Not set up' }}</span></div>
            @if (c.auth === 'oauth') {
              @if (portal(); as p) {
                <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Register an app at <a class="text-brand-600 hover:underline" [href]="p.url" target="_blank" rel="noopener">{{ p.name }}</a> with this redirect address, then paste its client ID and secret. {{ p.note }}</p>
              }
              <div class="mb-3 flex items-center gap-2"><code class="code min-w-0 flex-1 truncate text-xs">{{ c.redirectUri }}</code>
                <button type="button" class="icon-btn" (click)="copy(c.redirectUri)" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
              <label class="block"><span class="label">Client ID</span><input class="input font-mono" name="cid" [(ngModel)]="a.clientId" autocomplete="off" /></label>
              <label class="mt-3 block"><span class="label">Client secret</span>
                <input class="input" type="password" name="sec" [(ngModel)]="a.clientSecret" autocomplete="new-password" [placeholder]="c.hasSecret ? '•••••••• (saved, leave empty to keep)' : ''" /></label>
              @if (c.hasSecret) { <label class="mt-1 flex items-center gap-2 text-xs text-slate-500"><input type="checkbox" name="clr" [(ngModel)]="a.clearSecret" />Remove the saved secret</label> }
              @if (c.product === 'quickbooks') { <label class="mt-3 flex items-center gap-2 text-sm"><input type="checkbox" name="sbx" [(ngModel)]="a.sandbox" />Sandbox (QuickBooks test companies)</label> }
              <div class="mt-4 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Save {{ c.label }} app</button></div>
            } @else {
              <p class="text-sm text-slate-600 dark:text-slate-300">Nothing to set up: each company connects with its Odoo address, database, user and an API key. Only public https addresses are accepted.</p>
            }
          </form>

          <form (ngSubmit)="savePricing()">
            <div class="mb-2 font-medium">What {{ c.label }} costs</div>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" name="charge" [(ngModel)]="p.charge" />Charge for {{ c.label }} connections</label>
            <div class="mt-3 grid gap-3 sm:grid-cols-2">
              <label class="block"><span class="label">Set-up fee (one-off, invoiced on approval)</span><input class="input" type="number" min="0" step="0.01" name="sfee" [(ngModel)]="p.setupFee" /></label>
              <label class="block"><span class="label">Monthly fee</span><input class="input" type="number" min="0" step="0.01" name="fee" [(ngModel)]="p.monthlyFee" /></label>
              <label class="block"><span class="label">Currency</span><input class="input uppercase" maxlength="3" name="cur" [(ngModel)]="p.currency" /></label>
              <label class="block"><span class="label">Documents included a month</span><input class="input" type="number" min="0" name="inc" [(ngModel)]="p.includedDocuments" /></label>
              <label class="block"><span class="label">Price per extra document</span><input class="input" type="number" min="0" step="0.0001" name="per" [(ngModel)]="p.pricePerExtraDocument" /></label>
            </div>
            <p class="hint mt-2">Added to the company's subscription invoice. Only companies whose plan is billed in the same currency are charged.</p>
            <div class="mt-4 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Save prices</button></div>
          </form>
        </div>
      }
    </div>
  `,
})
export class ConnectorAppsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly apps = signal<ConnectorApp[]>([]);
  readonly product = signal('quickbooks');
  readonly current = computed(() => this.apps().find(a => a.product === this.product()) ?? null);
  readonly portal = computed(() => DEV_PORTALS[this.product()] ?? null);
  readonly busy = signal(false);
  a = { clientId: '', clientSecret: '', clearSecret: false, sandbox: false };
  p: Pricing = { charge: false, monthlyFee: 10, currency: 'USD', includedDocuments: 200, pricePerExtraDocument: 0.05, bridgeMonthlyFee: 0, setupFee: 0 };

  async ngOnInit(): Promise<void> {
    try { this.apps.set(await this.api.get<ConnectorApp[]>('/api/v1/admin/integrations/connectors/apps')); await this.select(); } catch (e) { this.toast.err(e); }
  }

  async pick(product: string): Promise<void> { this.product.set(product); await this.select(); }

  private fill(c: ConnectorApp): void { this.a = { clientId: c.clientId ?? '', clientSecret: '', clearSecret: false, sandbox: c.sandbox }; }

  private async select(): Promise<void> {
    const c = this.current(); if (!c) return;
    this.fill(c);
    try { this.p = await this.api.get<Pricing>(`/api/v1/admin/integrations/connectors/${c.product}/pricing`); } catch (e) { this.toast.err(e); }
  }

  async saveApp(): Promise<void> {
    this.busy.set(true);
    try {
      const saved = await this.api.put<ConnectorApp>(`/api/v1/admin/integrations/connectors/${this.product()}/app`, { ...this.a, clientId: this.a.clientId || null, clientSecret: this.a.clientSecret || null });
      this.apps.update(list => list.map(x => (x.product === saved.product ? saved : x)));
      this.fill(saved); this.toast.ok(`${saved.label} app saved.`);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async savePricing(): Promise<void> {
    this.busy.set(true);
    try {
      this.p = await this.api.put<Pricing>(`/api/v1/admin/integrations/connectors/${this.product()}/pricing`,
        { ...this.p, monthlyFee: Number(this.p.monthlyFee) || 0, includedDocuments: Number(this.p.includedDocuments) || 0, pricePerExtraDocument: Number(this.p.pricePerExtraDocument) || 0, setupFee: Number(this.p.setupFee) || 0 });
      this.toast.ok('Prices saved.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async copy(text: string): Promise<void> { try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { /* ignore */ } }
}
