import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { PageHeaderComponent } from '../../shared/ui.components';

interface Ratio { key: string; label: string; value: number | null; unit: 'x' | '%' | 'days'; hint: string }
interface Point { month: string; revenue: number; expenses: number; netProfit: number; cash: number }
interface Dash {
  currency: string; from: string; to: string; revenue: number; grossProfit: number; netProfit: number; cash: number; receivables: number; payables: number;
  ratios: Ratio[]; trend: Point[]; topExpenses: { label: string; accountId?: string | null; amount: number }[];
}

/** The books at a glance: what came in, what it cost, what is left, the key ratios and the last twelve months. */
@Component({
  selector: 'app-accounting-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DecimalPipe, RouterLink, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Financial dashboard" subtitle="Profit, cash and the ratios an accountant or a bank looks at, for any period.">
      <app-export-menu [build]="exportDoc" [disabled]="!d()" />
    </app-page-header>

    <div class="card mb-5 flex flex-wrap items-end gap-3">
      <label class="block"><span class="label">Currency</span><select class="input" [ngModel]="currency()" (ngModelChange)="currency.set($event); load()">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
      <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event)" /></label>
      <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event)" /></label>
      <div class="flex flex-wrap gap-1">
        @for (p of presets; track p.key) { <button type="button" class="rounded-full bg-slate-100 px-3 py-1 text-xs dark:bg-slate-800" (click)="preset(p.key)">{{ p.label }}</button> }
      </div>
      <span class="flex-1"></span>
      <button class="btn" (click)="load()" [disabled]="busy()">{{ busy() ? 'Working…' : 'Show' }}</button>
    </div>

    @if (d(); as d) {
      <div class="mb-5 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        @for (k of kpis(); track k.label) {
          <a class="card block p-4 transition hover:border-brand-400" [routerLink]="k.link">
            <div class="text-xs text-slate-500">{{ k.label }}</div>
            <div class="mt-1 text-xl font-semibold tabular-nums" [class.text-red-600]="k.value < 0">{{ money(k.value) }}</div>
            <div class="text-xs text-slate-400">{{ d.currency }}</div>
          </a>
        }
      </div>

      <div class="grid gap-5 xl:grid-cols-3">
        <section class="card xl:col-span-2">
          <h2 class="mb-1 text-base font-semibold">The last twelve months</h2>
          <p class="mb-4 text-xs text-slate-500"><span class="mr-3 inline-flex items-center gap-1"><span class="inline-block h-2.5 w-2.5 rounded-sm bg-emerald-500"></span>Income</span>
            <span class="mr-3 inline-flex items-center gap-1"><span class="inline-block h-2.5 w-2.5 rounded-sm bg-rose-400"></span>Expenses</span>
            <span class="inline-flex items-center gap-1"><span class="inline-block h-2.5 w-2.5 rounded-full bg-brand-600"></span>Cash at month end</span></p>
          <div class="flex h-56 items-end gap-1.5 overflow-x-auto pb-1">
            @for (p of d.trend; track p.month) {
              <div class="flex min-w-10 flex-1 flex-col items-center gap-1" [title]="p.month + ': income ' + money(p.revenue) + ', expenses ' + money(p.expenses) + ', profit ' + money(p.netProfit) + ', cash ' + money(p.cash)">
                <div class="relative flex h-44 w-full items-end justify-center gap-0.5">
                  <div class="w-1/3 rounded-t bg-emerald-500" [style.height.%]="bar(p.revenue)"></div>
                  <div class="w-1/3 rounded-t bg-rose-400" [style.height.%]="bar(p.expenses)"></div>
                  <div class="absolute h-2 w-2 -translate-y-1 rounded-full bg-brand-600" [style.bottom.%]="bar(p.cash)"></div>
                </div>
                <div class="text-[10px] text-slate-500">{{ (p.month + '-01') | date: 'MMM yy' }}</div>
                <div class="text-[10px] tabular-nums" [class.text-red-600]="p.netProfit < 0" [class.text-emerald-600]="p.netProfit > 0">{{ p.netProfit | number: '1.0-0' }}</div>
              </div>
            }
          </div>
        </section>

        <section class="card">
          <h2 class="mb-3 text-base font-semibold">Biggest expenses</h2>
          @for (e of d.topExpenses; track e.label) {
            <div class="mb-2">
              <div class="flex justify-between gap-2 text-sm"><span class="truncate">{{ e.label }}</span><span class="tabular-nums">{{ money(e.amount) }}</span></div>
              <div class="mt-1 h-1.5 rounded bg-slate-100 dark:bg-slate-800"><div class="h-1.5 rounded bg-rose-400" [style.width.%]="share(e.amount)"></div></div>
            </div>
          } @empty { <p class="text-sm text-slate-500">No expenses in this period.</p> }
        </section>
      </div>

      <section class="card mt-5">
        <h2 class="mb-3 text-base font-semibold">Key ratios</h2>
        <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          @for (r of d.ratios; track r.key) {
            <div class="rounded-xl border border-slate-200 p-4 dark:border-slate-700">
              <div class="flex items-baseline justify-between gap-2"><span class="text-sm font-medium">{{ r.label }}</span>
                <span class="text-xl font-semibold tabular-nums" [class]="tone(r)">{{ show(r) }}</span></div>
              <p class="mt-1 text-xs text-slate-500">{{ r.hint }}</p>
            </div>
          }
        </div>
      </section>
    } @else if (!busy()) { <div class="card text-center text-slate-500">Choose a period and click Show.</div> }
  `,
})
export class AccountingDashboardComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(AccountingStore);
  readonly money = money;
  readonly d = signal<Dash | null>(null);
  readonly busy = signal(false);
  readonly currency = signal('USD');
  readonly from = signal(isoDate(new Date(new Date().getFullYear(), 0, 1)));
  readonly to = signal(isoDate(new Date()));
  readonly presets = [{ key: 'month', label: 'This month' }, { key: 'quarter', label: 'This quarter' }, { key: 'ytd', label: 'Year to date' }, { key: 'last-year', label: 'Last financial year' }];

  readonly kpis = computed(() => {
    const d = this.d(); if (!d) return [];
    return [
      { label: 'Revenue', value: d.revenue, link: '/accounting/reports/profit-loss' }, { label: 'Gross profit', value: d.grossProfit, link: '/accounting/reports/profit-loss' },
      { label: 'Net profit', value: d.netProfit, link: '/accounting/reports/profit-loss' }, { label: 'Cash and bank', value: d.cash, link: '/accounting/reports/cash-flow' },
      { label: 'Customers owe you', value: d.receivables, link: '/accounting/receivables' }, { label: 'You owe suppliers', value: d.payables, link: '/accounting/bills' },
    ];
  });
  private readonly peak = computed(() => Math.max(1, ...(this.d()?.trend ?? []).flatMap(p => [p.revenue, p.expenses, p.cash])));

  bar(v: number): number { return Math.max(0, Math.min(100, (v / this.peak()) * 100)); }
  share(v: number): number { const top = this.d()?.topExpenses[0]?.amount ?? 1; return top > 0 ? Math.max(3, (v / top) * 100) : 0; }
  show(r: Ratio): string { return r.value == null ? '—' : r.unit === '%' ? r.value.toFixed(1) + '%' : r.unit === 'days' ? Math.round(r.value) + ' days' : r.value.toFixed(2) + '×'; }
  tone(r: Ratio): string {
    if (r.value == null) return 'text-slate-400';
    const good = { current: r.value >= 1.5, cash: r.value >= 0.5, gross_margin: r.value >= 25, operating_margin: r.value >= 10, net_margin: r.value >= 5, debtor_days: r.value <= 45, creditor_days: true, debt_equity: r.value <= 2, roe: r.value >= 10 }[r.key];
    const bad = { current: r.value < 1, cash: r.value < 0.2, gross_margin: r.value < 10, operating_margin: r.value < 0, net_margin: r.value < 0, debtor_days: r.value > 90, creditor_days: false, debt_equity: r.value > 4, roe: r.value < 0 }[r.key];
    return good ? 'text-emerald-600' : bad ? 'text-red-600' : 'text-amber-600';
  }

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); await this.store.loadSetup().catch(() => undefined); this.currency.set(this.store.meta()?.defaultCurrency ?? 'USD'); this.from.set(this.store.yearStart(this.to())); await this.load(); }
    catch (e) { this.toast.err(e); }
  }

  preset(key: string): void {
    const t = new Date(); const ys = new Date(this.store.yearStart(isoDate(t)) + 'T00:00:00');
    let f: Date, to: Date = t;
    switch (key) {
      case 'month': f = new Date(t.getFullYear(), t.getMonth(), 1); break;
      case 'quarter': f = new Date(t.getFullYear(), Math.floor(t.getMonth() / 3) * 3, 1); break;
      case 'last-year': f = new Date(ys.getFullYear() - 1, ys.getMonth(), 1); to = new Date(ys.getFullYear(), ys.getMonth(), 0); break;
      default: f = ys;
    }
    this.from.set(isoDate(f)); this.to.set(isoDate(to)); void this.load();
  }

  async load(): Promise<void> {
    this.busy.set(true);
    try { this.d.set(await this.api.get<Dash>('/api/v1/accounting/reports/dashboard', { currency: this.currency(), from: this.from(), to: this.to() })); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  readonly exportDoc = (): ExportDoc | null => {
    const d = this.d(); if (!d) return null;
    return {
      title: 'Financial dashboard', subtitle: `${d.from.slice(0, 10)} to ${d.to.slice(0, 10)} · ${d.currency}`, landscape: true,
      columns: [{ header: 'Item' }, { header: 'Revenue', kind: 'money' }, { header: 'Expenses', kind: 'money' }, { header: 'Net profit', kind: 'money' }, { header: 'Cash', kind: 'money' }],
      rows: [
        { cells: ['Key figures'], style: 'section' },
        ...this.kpis().map(k => ({ cells: [k.label, num(k.value)] })),
        { cells: ['Ratios'], style: 'section' },
        ...d.ratios.map(r => ({ cells: [r.label + ': ' + this.show(r)] })),
        { cells: ['Month', 'Revenue', 'Expenses', 'Net profit', 'Cash'], style: 'section' },
        ...d.trend.map(p => ({ cells: [p.month, num(p.revenue), num(p.expenses), num(p.netProfit), num(p.cash)] })),
      ],
    };
  };
}
