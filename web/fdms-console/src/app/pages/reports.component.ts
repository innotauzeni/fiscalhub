import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { FiscalDayReportRow, SalesReport } from '../core/models';
import { ToastService } from '../core/ui.services';
import { EmptyComponent } from '../shared/list.components';
import { money } from '../shared/public.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';

type Preset = 'today' | '7' | '30' | 'month' | 'last';

/** Sales and VAT for a period, from what was actually sent to ZIMRA (credit notes count against sales), plus each fiscal day's totals. Exports to CSV or prints. */
@Component({
  selector: 'app-reports',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DecimalPipe, IconComponent, PageHeaderComponent, StatusComponent, EmptyComponent],
  template: `
    <app-page-header title="Reports" subtitle="Sales and VAT for a period, from what was sent to ZIMRA. Credit notes are taken off.">
      <button class="btn print:hidden" (click)="vat('xlsx')" title="Sales and VAT by rate for this period, laid out for your VAT return"><app-icon name="download" size="h-4 w-4" />VAT return (Excel)</button>
      <button class="btn-ghost print:hidden" (click)="vat('pdf')"><app-icon name="download" size="h-4 w-4" />VAT return (PDF)</button>
      <button class="btn-ghost print:hidden" (click)="download('sales')"><app-icon name="download" size="h-4 w-4" />Sales CSV</button>
      <button class="btn-ghost print:hidden" (click)="download('fiscal-days')"><app-icon name="download" size="h-4 w-4" />Fiscal days CSV</button>
      <button class="btn-ghost print:hidden" (click)="print()">Print</button>
    </app-page-header>

    <div class="card mb-5 print:hidden">
      <div class="flex flex-wrap gap-2">
        @for (p of presets; track p.id) { <button type="button" class="rounded-full border px-3.5 py-1.5 text-sm font-medium transition" [class]="preset() === p.id ? 'border-brand-600 bg-brand-600 text-white' : 'border-slate-200 text-slate-600 hover:border-brand-500 dark:border-slate-700 dark:text-slate-300'" (click)="choose(p.id)">{{ p.label }}</button> }
      </div>
      <div class="mt-4 grid gap-3 sm:grid-cols-3">
        <label class="block"><span class="label">From</span><input class="input" type="date" [ngModel]="from()" (ngModelChange)="custom($event, to())" /></label>
        <label class="block"><span class="label">To</span><input class="input" type="date" [ngModel]="to()" (ngModelChange)="custom(from(), $event)" /></label>
        <label class="block"><span class="label">Device</span>
          <select class="input" [ngModel]="deviceId()" (ngModelChange)="deviceId.set($event); load()"><option value="">All devices</option>@for (d of devices(); track d.id) { <option [value]="d.id">Device {{ d.zimraDeviceId }}{{ d.branchName ? ' - ' + d.branchName : '' }}</option> }</select></label>
      </div>
    </div>

    @if (report(); as r) {
      <div class="mb-2 text-sm text-slate-500">{{ r.from | date: 'd MMM yyyy' }} – {{ r.to | date: 'd MMM yyyy' }} · {{ r.counts.counted }} document(s) counted@if (r.counts.rejected) { · <span class="text-red-600">{{ r.counts.rejected }} rejected (not counted)</span> }@if (r.counts.pending) { · {{ r.counts.pending }} still waiting for ZIMRA }</div>
      @if (r.truncated) { <p class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-900/25 dark:text-amber-200">This period has more than 20,000 documents, so only the first 20,000 are included. Choose a shorter period.</p> }

      @for (c of r.currencies; track c.currency) {
        <div class="mb-6">
          <div class="mb-3 flex items-center gap-2"><span class="badge badge-info">{{ c.currency }}</span><h2 class="text-base font-semibold">{{ c.currency }} sales</h2></div>
          <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <div class="rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs font-medium uppercase tracking-wide text-slate-500">Net sales (incl. VAT)</div><div class="mt-2 text-3xl font-semibold tabular-nums">{{ fmt(c.netSales, c.currency) }}</div></div>
            <div class="rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs font-medium uppercase tracking-wide text-slate-500">VAT to account for</div><div class="mt-2 text-3xl font-semibold tabular-nums text-brand-600">{{ fmt(c.netTax, c.currency) }}</div></div>
            <div class="rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs font-medium uppercase tracking-wide text-slate-500">Invoices</div><div class="mt-2 text-3xl font-semibold tabular-nums">{{ c.invoices }}</div><div class="hint">{{ fmt(c.invoicesTotal, c.currency) }}</div></div>
            <div class="rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs font-medium uppercase tracking-wide text-slate-500">Credit notes</div><div class="mt-2 text-3xl font-semibold tabular-nums" [class.text-amber-600]="c.creditNotes > 0">{{ c.creditNotes }}</div><div class="hint">-{{ fmt(c.creditNotesTotal, c.currency) }}@if (c.debitNotes) { · {{ c.debitNotes }} debit note(s) }</div></div>
          </div>
          <div class="mt-4 grid gap-4 lg:grid-cols-2">
            <div class="card">
              <div class="card-title">VAT by rate</div>
              <table class="table"><thead><tr><th>Rate</th><th class="text-right">Sales (incl. VAT)</th><th class="text-right">VAT</th></tr></thead>
                <tbody>@for (t of c.taxes; track t.label) { <tr><td class="font-medium">{{ t.label }}</td><td class="text-right tabular-nums">{{ t.sales | number:'1.2-2' }}</td><td class="text-right tabular-nums">{{ t.tax | number:'1.2-2' }}</td></tr> }
                  <tr class="font-semibold"><td>Total</td><td class="text-right tabular-nums">{{ sum(c.taxes, 'sales') | number:'1.2-2' }}</td><td class="text-right tabular-nums">{{ c.netTax | number:'1.2-2' }}</td></tr></tbody></table>
            </div>
            <div class="card">
              <div class="card-title">Paid by</div>
              <table class="table"><thead><tr><th>Method</th><th class="text-right">Amount</th></tr></thead>
                <tbody>@for (p of c.payments; track p.moneyType) { <tr><td class="font-medium">{{ p.moneyType }}</td><td class="text-right tabular-nums">{{ p.amount | number:'1.2-2' }}</td></tr> } @empty { <tr><td colspan="2" class="text-slate-500">No payments recorded.</td></tr> }</tbody></table>
            </div>
          </div>
        </div>
      } @empty { <div class="card"><app-empty icon="chart" title="No sales in this period" hint="Documents sent to ZIMRA in the chosen dates appear here." /></div> }

      @if (r.days.length) {
        <div class="card mb-6">
          <div class="card-title">Net sales by day</div>
          <div class="flex h-40 items-end gap-1.5 overflow-x-auto pb-1">
            @for (d of bars(); track d.date) {
              <div class="group flex min-w-6 flex-1 flex-col items-center justify-end" [title]="d.date + ': ' + (d.net | number:'1.2-2') + ' ' + d.currency">
                <div class="w-full rounded-t-md bg-linear-to-t from-blue-600 to-violet-500 transition group-hover:opacity-80" [style.height.%]="d.pct"></div>
              </div>
            }
          </div>
          <div class="mt-1 flex justify-between text-[11px] text-slate-500"><span>{{ r.days[0].date }}</span><span>{{ r.days[r.days.length - 1].date }}</span></div>
        </div>
      }
    }

    <div class="card">
      <div class="card-title">Fiscal days (Z report totals)</div>
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Device</th><th>Day</th><th>Opened</th><th>Closed</th><th>Receipts</th><th>Status</th><th>Counters</th></tr></thead>
          <tbody>
            @for (d of days(); track d.deviceZimraId + '-' + d.fiscalDayNo) {
              <tr>
                <td>{{ d.deviceZimraId }}</td><td class="font-medium">#{{ d.fiscalDayNo }}</td>
                <td class="whitespace-nowrap">{{ d.openedAt | date: 'd MMM, HH:mm' }}</td><td class="whitespace-nowrap">{{ d.closedAt ? (d.closedAt | date: 'd MMM, HH:mm') : '-' }}</td>
                <td>{{ d.receipts }}{{ d.offline ? ' (offline)' : '' }}</td><td><app-status [value]="d.status" /></td>
                <td class="text-xs"><div class="flex flex-wrap gap-x-4 gap-y-0.5">@for (t of main(d); track t.currency + t.type) { <span><span class="text-slate-500">{{ t.type }}</span> <strong class="tabular-nums">{{ t.value | number:'1.2-2' }}</strong> {{ t.currency }}</span> }</div></td>
              </tr>
            } @empty { <tr><td colspan="7"><app-empty icon="calendar" title="No fiscal days in this period" /></td></tr> }
          </tbody>
        </table>
      </div>
    </div>
  `,
})
export class ReportsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly store = inject(DeviceStore);

  readonly presets: { id: Preset; label: string }[] = [{ id: 'today', label: 'Today' }, { id: '7', label: 'Last 7 days' }, { id: '30', label: 'Last 30 days' }, { id: 'month', label: 'This month' }, { id: 'last', label: 'Last month' }];
  readonly devices = computed(() => this.store.devices());
  readonly preset = signal<Preset | ''>('30');
  readonly from = signal('');
  readonly to = signal('');
  readonly deviceId = signal('');
  readonly report = signal<SalesReport | null>(null);
  readonly days = signal<FiscalDayReportRow[]>([]);
  readonly bars = computed(() => {
    const d = this.report()?.days ?? []; const max = Math.max(1, ...d.map(x => Math.abs(x.net)));
    return d.map(x => ({ ...x, pct: Math.max(3, (Math.abs(x.net) / max) * 100) }));
  });

  fmt(v: number, c: string): string { return money(v, c); }
  sum(rows: { sales: number }[], key: 'sales'): number { return rows.reduce((s, r) => s + r[key], 0); }
  /** the counters worth showing at a glance: what was sold, its tax, and what was credited */
  main(d: FiscalDayReportRow) { return d.totals.filter(t => ['SaleByTax', 'SaleTaxByTax', 'CreditNoteByTax', 'CreditNoteTaxByTax'].includes(t.type)); }

  private static iso(d: Date): string { return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; }

  async ngOnInit(): Promise<void> { try { await this.store.load(); } catch { /* the device filter is optional */ } this.choose('30'); }

  choose(p: Preset): void {
    const now = new Date(); let a = new Date(now); let b = new Date(now);
    if (p === '7') a.setDate(a.getDate() - 6);
    else if (p === '30') a.setDate(a.getDate() - 29);
    else if (p === 'month') a = new Date(now.getFullYear(), now.getMonth(), 1);
    else if (p === 'last') { a = new Date(now.getFullYear(), now.getMonth() - 1, 1); b = new Date(now.getFullYear(), now.getMonth(), 0); }
    this.preset.set(p); this.from.set(ReportsComponent.iso(a)); this.to.set(ReportsComponent.iso(b)); void this.load();
  }

  custom(from: string, to: string): void { this.preset.set(''); this.from.set(from); this.to.set(to); if (from && to) void this.load(); }

  private query() { return { from: this.from(), to: this.to(), deviceId: this.deviceId() || undefined }; }

  async load(): Promise<void> {
    try {
      this.report.set(await this.api.get<SalesReport>('/api/v1/reports/sales', this.query()));
      this.days.set(await this.api.get<FiscalDayReportRow[]>('/api/v1/reports/fiscal-days', this.query()));
    } catch (e) { this.toast.err(e); }
  }

  download(kind: 'sales' | 'fiscal-days'): Promise<void> {
    const q = this.query();
    const url = `/api/v1/reports/${kind}.csv?from=${q.from}&to=${q.to}${q.deviceId ? '&deviceId=' + q.deviceId : ''}`;
    return this.api.download(url, `${kind}-${q.from}-${q.to}.csv`).catch(e => this.toast.err(e));
  }

  /** The VAT summary for the chosen period and device, as Excel or PDF, ready to complete a VAT return from. */
  vat(kind: 'xlsx' | 'pdf'): Promise<void> {
    const q = this.query();
    return this.api.download(`/api/v1/reports/vat-return/${kind}?from=${q.from}&to=${q.to}${q.deviceId ? '&deviceId=' + q.deviceId : ''}`, `vat-summary-${q.from}-${q.to}.${kind}`).catch(e => this.toast.err(e));
  }

  print(): void { window.print(); }
}
