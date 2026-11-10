import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/ui.services';
import { DocumentsPanelComponent } from '../../shared/documents-panel.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Asset { id: string; name: string; code?: string | null; purchaseDate: string; currency: string; cost: number; residualValue: number; usefulLifeMonths: number; method: string; ratePercent: number; status: string; disposedOn?: string | null; disposalProceeds?: number | null; accumulatedDepreciation: number; netBookValue: number; fullyDepreciatedBy?: string | null }
interface Register { asAt: string; assets: Asset[]; cost: { currency: string; amount: number }[]; accumulated: { currency: string; amount: number }[]; netBook: { currency: string; amount: number }[] }
interface Form { name: string; code: string; purchaseDate: string; currency: string; cost: number | null; residual: number | null; life: number | null; method: string; rate: number | null; asset: string; accumulated: string; expense: string; paidFrom: string; notes: string }

const lastMonthEnd = (): string => { const d = new Date(); return isoDate(new Date(d.getFullYear(), d.getMonth(), 0)); };

/** Things the company owns for years. Depreciation is charged one month at a time and posted to the books; disposing of an asset books the gain or loss. */
@Component({
  selector: 'app-assets',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DocumentsPanelComponent, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Fixed assets" subtitle="Vehicles, computers and equipment: what they cost, what they are worth now, and their depreciation.">
      @if (auth.can('accounting.manage')) {
        <button class="btn-ghost" (click)="depOpen.set(true)"><app-icon name="refresh" size="h-4 w-4" />Run depreciation</button>
        <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New asset</button>
      }
    </app-page-header>

    @if (reg(); as r) {
      <div class="mb-5 grid gap-4 sm:grid-cols-3">
        @for (c of r.cost; track c.currency) {
          <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Worth now ({{ c.currency }})</div>
            <div class="text-2xl font-semibold tabular-nums">{{ money(amount(r.netBook, c.currency)) }}</div><div class="text-xs text-slate-500">cost {{ money(c.amount) }} less {{ money(amount(r.accumulated, c.currency)) }} depreciation</div></div>
        }
        @if (!r.cost.length) { <div class="card sm:col-span-3 text-center text-slate-500">No assets yet. Add a vehicle, computer or piece of equipment to start depreciating it.</div> }
      </div>

      <div class="grid gap-5 md:grid-cols-2 xl:grid-cols-3">
        @for (a of r.assets; track a.id) {
          <article class="card flex flex-col p-0" [class.opacity-60]="a.status === 'Disposed'">
            <header class="flex items-start gap-3 border-b border-slate-100 p-5 dark:border-slate-800">
              <span class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-500/15"><app-icon name="cpu" size="h-6 w-6" /></span>
              <div class="min-w-0 flex-1"><h2 class="truncate text-base font-semibold">{{ a.name }}</h2><p class="truncate text-xs text-slate-500">{{ a.code ? a.code + ' · ' : '' }}bought {{ a.purchaseDate | date: 'd MMM y' }}</p></div>
              @if (a.status === 'Disposed') { <span class="badge badge-warn">Disposed</span> }
            </header>
            <div class="flex-1 space-y-3 p-5 text-sm">
              <div class="grid grid-cols-2 gap-3"><div><div class="text-xs text-slate-500">Cost</div><div class="font-medium tabular-nums">{{ money(a.cost) }} {{ a.currency }}</div></div><div><div class="text-xs text-slate-500">Worth now</div><div class="font-semibold tabular-nums">{{ money(a.netBookValue) }}</div></div></div>
              <div><div class="mb-1 flex justify-between text-xs text-slate-500"><span>Depreciated {{ money(a.accumulatedDepreciation) }}</span><span>{{ pct(a) }}%</span></div>
                <div class="h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-slate-800"><div class="h-full rounded-full bg-brand-500" [style.width.%]="pct(a)"></div></div></div>
              <div class="text-xs text-slate-500">{{ a.method === 'straight_line' ? 'Straight line over ' + a.usefulLifeMonths + ' months' + (a.fullyDepreciatedBy ? ', until ' + (a.fullyDepreciatedBy | date: 'MMM y') : '') : a.ratePercent + '% a year on the reducing balance' }}{{ a.residualValue ? ' · keeps ' + money(a.residualValue) : '' }}</div>
              @if (a.status === 'Disposed') { <div class="rounded-lg bg-slate-50 px-3 py-2 text-xs dark:bg-slate-800/50">Disposed {{ a.disposedOn | date: 'd MMM y' }} for {{ money(a.disposalProceeds ?? 0) }} {{ a.currency }}</div> }
            </div>
            <footer class="flex gap-2 border-t border-slate-100 p-3 dark:border-slate-800"><button class="btn-ghost btn-sm flex-1 justify-center" (click)="docs.set({ id: a.id, label: a.name })"><app-icon name="file-text" size="h-4 w-4" />Documents</button>@if (a.status !== 'Disposed' && auth.can('accounting.manage')) { <button class="btn-ghost btn-sm flex-1 justify-center" (click)="startDispose(a)">Sell or scrap</button> }</footer>
          </article>
        }
      </div>
    } @else { <div class="card text-center text-slate-500">Loading…</div> }

    @if (form(); as f) {
      <app-modal title="New asset" width="max-w-2xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Kind of asset (fills in a typical life or rate)</span><select class="input" name="cat" [ngModel]="''" (ngModelChange)="applyCategory(f, $event)"><option value="">Choose…</option>@for (c of categories; track c.key) { <option [value]="c.key">{{ c.label }}</option> }</select></label>
            <label class="block"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="120" placeholder="Delivery van" /></label>
            <label class="block"><span class="label">Asset code (optional)</span><input class="input font-mono" name="c" [(ngModel)]="f.code" maxlength="30" /></label>
            <label class="block"><span class="label">Bought on</span><input type="date" class="input" name="d" [(ngModel)]="f.purchaseDate" required /></label>
            <label class="block"><span class="label">Currency</span><select class="input" name="cur" [(ngModel)]="f.currency">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
            <label class="block"><span class="label">Cost</span><input type="number" step="0.01" min="0.01" class="input text-right" name="co" [(ngModel)]="f.cost" required /></label>
            <label class="block"><span class="label">Worth at the end of its life</span><input type="number" step="0.01" min="0" class="input text-right" name="r" [(ngModel)]="f.residual" /></label>
            <label class="block"><span class="label">Method</span><select class="input" name="m" [(ngModel)]="f.method"><option value="straight_line">Straight line (equal every month)</option><option value="reducing">Reducing balance (a yearly percentage)</option></select></label>
            @if (f.method === 'reducing') { <label class="block"><span class="label">Yearly rate (%)</span><input type="number" step="0.01" min="1" max="100" class="input text-right" name="rt" [(ngModel)]="f.rate" /></label> }
            @else { <label class="block"><span class="label">Useful life (months)</span><input type="number" min="1" max="600" class="input text-right" name="l" [(ngModel)]="f.life" required /></label> }
            <label class="block sm:col-span-2"><span class="label">Paid from (posts the purchase; leave empty if it is already in your books)</span>
              <select class="input" name="pf" [(ngModel)]="f.paidFrom"><option value="">Already recorded</option>@for (a of store.meta()?.cashAccounts ?? []; track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block"><span class="label">Asset account</span><select class="input" name="aa" [(ngModel)]="f.asset">@for (a of store.byType('Asset'); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block"><span class="label">Accumulated depreciation account</span><select class="input" name="ac" [(ngModel)]="f.accumulated">@for (a of store.byType('Asset'); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Depreciation expense account</span><select class="input" name="ex" [(ngModel)]="f.expense">@for (a of store.byType('Expense'); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Notes (optional)</span><input class="input" name="no" [(ngModel)]="f.notes" maxlength="300" /></label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !f.name || !f.cost">Add asset</button></div>
        </form>
      </app-modal>
    }

    @if (depOpen()) {
      <app-modal title="Run depreciation" subtitle="Charges every month that has ended, for every asset, once. Months already charged are skipped." width="max-w-md" (closed)="depOpen.set(false)">
        <label class="block"><span class="label">Depreciate up to and including</span><input type="date" class="input" [ngModel]="depTo()" (ngModelChange)="depTo.set($event)" /></label>
        <p class="hint">Only whole months that have ended are charged, so use a month end such as the last day of last month.</p>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
        <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="depOpen.set(false)">Cancel</button><button class="btn" type="button" (click)="depreciate()" [disabled]="busy() || !depTo()">Run</button></div>
      </app-modal>
    }

    @if (disposing(); as d) {
      <app-modal title="Sell or scrap" [subtitle]="d.asset.name + ' · worth ' + money(d.asset.netBookValue) + ' ' + d.asset.currency + ' now'" width="max-w-md" (closed)="disposing.set(null)">
        <div class="grid gap-4 sm:grid-cols-2">
          <label class="block"><span class="label">Date</span><input type="date" class="input" [(ngModel)]="d.date" /></label>
          <label class="block"><span class="label">Sold for (0 if scrapped)</span><input type="number" step="0.01" min="0" class="input text-right" [(ngModel)]="d.proceeds" /></label>
          @if ((d.proceeds || 0) > 0) { <label class="block sm:col-span-2"><span class="label">Money received into</span><select class="input" [(ngModel)]="d.account"><option value="">Choose…</option>@for (a of store.meta()?.cashAccounts ?? []; track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label> }
        </div>
        <p class="hint">Depreciation is brought up to the end of that month first. The difference between what it sold for and what it was worth is booked as a gain or loss.</p>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
        <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="disposing.set(null)">Cancel</button><button class="btn" type="button" (click)="dispose()" [disabled]="busy() || ((d.proceeds || 0) > 0 && !d.account)">Dispose</button></div>
      </app-modal>
    }
    @if (docs(); as d) {
      <app-modal [title]="'Documents: ' + d.label" subtitle="Scans and papers kept with this record." width="max-w-2xl" (closed)="docs.set(null)">
        <app-documents-panel linkType="asset" [linkId]="d.id" />
      </app-modal>
    }
  `,
})
export class AssetsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly reg = signal<Register | null>(null);
  readonly form = signal<Form | null>(null);
  readonly docs = signal<{ id: string; label: string } | null>(null);
  readonly depOpen = signal(false);
  readonly depTo = signal(lastMonthEnd());
  readonly disposing = signal<{ asset: Asset; date: string; proceeds: number | null; account: string } | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly count = computed(() => this.reg()?.assets.length ?? 0);

  amount(list: { currency: string; amount: number }[], currency: string): number { return list.find(x => x.currency === currency)?.amount ?? 0; }
  pct(a: Asset): number { const base = a.cost - a.residualValue; return base <= 0 ? 0 : Math.min(100, Math.round((a.accumulatedDepreciation / base) * 100)); }

  async ngOnInit(): Promise<void> { try { await this.store.load(); } catch (e) { this.toast.err(e); } await this.load(); }

  async load(): Promise<void> {
    try { this.reg.set(await this.api.get<Register>('/api/v1/accounting/assets', { asAt: isoDate(new Date()) })); } catch (e) { this.toast.err(e); }
  }

  /** Typical wear-and-tear lives and rates; a starting point the accountant can still change. */
  readonly categories = [
    { key: 'buildings', label: 'Buildings (50 years)', method: 'straight_line', life: 600, rate: 0 },
    { key: 'plant', label: 'Plant and machinery (10 years)', method: 'straight_line', life: 120, rate: 0 },
    { key: 'vehicles', label: 'Motor vehicles (25% a year, reducing)', method: 'reducing', life: 60, rate: 25 },
    { key: 'computers', label: 'Computers and phones (3 years)', method: 'straight_line', life: 36, rate: 0 },
    { key: 'furniture', label: 'Furniture and fittings (10 years)', method: 'straight_line', life: 120, rate: 0 },
    { key: 'equipment', label: 'Office equipment (5 years)', method: 'straight_line', life: 60, rate: 0 },
  ];

  applyCategory(f: Form, key: string): void {
    const c = this.categories.find(x => x.key === key); if (!c) return;
    f.method = c.method; f.life = c.life; if (c.rate) f.rate = c.rate;
    this.form.set({ ...f });
  }

  start(): void {
    this.error.set('');
    // the standard accounts, found by code (or, in a renumbered chart, by their place)
    const id = (code: string): string => this.store.accounts().find(a => a.code === code)?.id
      ?? (code === '5900' ? this.store.postable().find(a => /depreciation/i.test(a.name) && a.type === 'Expense')?.id
        : this.store.postable().find(a => a.subType === 'fixed_asset' && (code === '1590') === /depreciation/i.test(a.name))?.id) ?? '';
    this.form.set({ name: '', code: '', purchaseDate: isoDate(new Date()), currency: this.store.meta()?.defaultCurrency ?? 'USD', cost: null, residual: 0, life: 60, method: 'straight_line', rate: 20, asset: id('1500'), accumulated: id('1590'), expense: id('5900'), paidFrom: '', notes: '' });
  }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.post('/api/v1/accounting/assets', { name: f.name.trim(), code: f.code.trim() || null, purchaseDate: f.purchaseDate, currency: f.currency, cost: Number(f.cost), residualValue: Number(f.residual) || 0, usefulLifeMonths: f.method === 'reducing' ? 60 : Number(f.life) || 0, method: f.method, ratePercent: f.method === 'reducing' ? Number(f.rate) || 0 : 0, assetAccountId: f.asset, accumulatedAccountId: f.accumulated, expenseAccountId: f.expense, notes: f.notes.trim() || null, paidFromAccountId: f.paidFrom || null });
      this.form.set(null); this.toast.ok('Asset added.'); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async depreciate(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      const r = await this.api.post<{ charges: number; amount: number; months: string[] }>('/api/v1/accounting/assets/depreciate', { upTo: this.depTo() });
      this.depOpen.set(false); this.toast.ok(r.charges ? r.charges + ' charge(s) of ' + money(r.amount) + ' posted for ' + r.months.join(', ') + '.' : 'Everything is already depreciated up to that date.'); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  startDispose(a: Asset): void { this.error.set(''); this.disposing.set({ asset: a, date: isoDate(new Date()), proceeds: 0, account: '' }); }

  async dispose(): Promise<void> {
    const d = this.disposing(); if (!d) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.post('/api/v1/accounting/assets/' + d.asset.id + '/dispose', { date: d.date, proceeds: Number(d.proceeds) || 0, receivedIntoAccountId: d.account || null }); this.disposing.set(null); this.toast.ok('Asset disposed of.'); await this.load(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }
}
