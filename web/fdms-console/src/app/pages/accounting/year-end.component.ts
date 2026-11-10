import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, LedgerAccount, isoDate, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface YearEnd {
  yearStart: string; yearEnd: string; closed: boolean; closedAt?: string | null; closedBy?: string | null; netProfit: { currency: string; amount: number }[];
  checks: { label: string; ok: boolean; detail?: string | null }[]; lockedUntil?: string | null; history: YearEnd[];
}

/** Closing a financial year (checks, depreciation, locking) and entering the opening balances a company starts its books with. */
@Component({
  selector: 'app-year-end',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Year end" subtitle="Close a financial year once its figures are final, and enter the balances your books start from.">
      <app-export-menu [build]="exportDoc" [disabled]="!ye()" />
    </app-page-header>

    <div class="mb-5 flex gap-1 border-b border-slate-200 dark:border-slate-700">
      @for (t of tabs; track t.key) {
        <button type="button" class="-mb-px border-b-2 px-4 py-2 text-sm font-medium" [class]="tab() === t.key ? 'border-brand-600 text-brand-700 dark:text-brand-200' : 'border-transparent text-slate-500'" (click)="tab.set(t.key)">{{ t.label }}</button>
      }
    </div>

    @if (tab() === 'close') {
      <div class="card mb-5 flex flex-wrap items-end gap-3">
        <label class="block"><span class="label">Financial year ending in</span>
          <select class="input" [ngModel]="year()" (ngModelChange)="year.set(+$event); load()">@for (y of years; track y) { <option [ngValue]="y">{{ y }}</option> }</select></label>
        @if (ye(); as y) { <p class="text-sm text-slate-500">{{ y.yearStart | date: 'd MMM y' }} to {{ y.yearEnd | date: 'd MMM y' }}@if (y.lockedUntil) { · books locked up to {{ y.lockedUntil | date: 'd MMM y' }} }</p> }
      </div>

      @if (ye(); as y) {
        <div class="grid gap-5 lg:grid-cols-3">
          <section class="card lg:col-span-2">
            <h2 class="mb-3 text-base font-semibold">Before closing</h2>
            <ul class="space-y-2">
              @for (c of y.checks; track c.label) {
                <li class="flex items-start gap-2 text-sm"><app-icon [name]="c.ok ? 'check' : 'alert'" size="h-4 w-4" [class]="c.ok ? 'mt-0.5 text-emerald-600' : 'mt-0.5 text-amber-600'" />
                  <span><span class="font-medium">{{ c.label }}</span>@if (c.detail) { <span class="block text-xs text-slate-500">{{ c.detail }}</span> }</span></li>
              }
            </ul>
            @if (canManage) {
              <div class="mt-5 flex flex-wrap items-center gap-3 border-t border-slate-100 pt-4 dark:border-slate-800">
                @if (y.closed) {
                  <span class="badge badge-ok">Closed {{ y.closedAt | date: 'd MMM y' }}{{ y.closedBy ? ' by ' + y.closedBy : '' }}</span>
                  <button class="btn-ghost text-red-600" (click)="reopen(y)" [disabled]="busy()">Reopen the year</button>
                } @else {
                  <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" [ngModel]="depreciate()" (ngModelChange)="depreciate.set($event)" />Run depreciation up to the year end first</label>
                  <button class="btn" (click)="close(y)" [disabled]="busy()"><app-icon name="lock" size="h-4 w-4" />Close {{ y.yearEnd | date: 'y' }}</button>
                }
              </div>
              <p class="hint mt-2">Closing locks every date up to the year end, so the year's figures can no longer change. Its profit stays in retained earnings; income and expenses start the new year at nil.</p>
            }
          </section>
          <section class="card">
            <h2 class="mb-3 text-base font-semibold">The year's result</h2>
            @for (p of y.netProfit; track p.currency) {
              <div class="flex justify-between py-1 text-sm"><span>Net {{ p.amount >= 0 ? 'profit' : 'loss' }}</span><span class="font-semibold tabular-nums" [class.text-red-600]="p.amount < 0">{{ money(p.amount) }} {{ p.currency }}</span></div>
            } @empty { <p class="text-sm text-slate-500">Nothing was posted in this year.</p> }
          </section>
        </div>

        <section class="card mt-5 p-0">
          <h2 class="px-5 pt-4 text-base font-semibold">Closed years</h2>
          <table class="table w-full text-sm">
            <thead><tr><th>Year</th><th>Result</th><th>Closed</th><th>Status</th></tr></thead>
            <tbody>
              @for (h of y.history; track $index) {
                <tr><td>{{ h.yearStart | date: 'd MMM y' }} to {{ h.yearEnd | date: 'd MMM y' }}</td><td class="tabular-nums">@for (p of h.netProfit; track p.currency) { {{ money(p.amount) }} {{ p.currency }} }</td>
                  <td>{{ h.closedAt | date: 'd MMM y' }}{{ h.closedBy ? ' · ' + h.closedBy : '' }}</td><td>@if (h.closed) { <span class="badge badge-ok">Closed</span> } @else { <span class="badge badge-warn">Reopened</span> }</td></tr>
              } @empty { <tr><td colspan="4" class="py-6 text-center text-slate-500">No year has been closed yet.</td></tr> }
            </tbody>
          </table>
        </section>
      }
    } @else {
      <div class="card">
        <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Moving from another system or from paper? Enter what each account held on the day before you start (from your last balance sheet).
          Any difference goes to <strong>Opening balance equity</strong>. Income and expense accounts are not entered: last year's profit is part of retained earnings.</p>
        <div class="mb-4 grid gap-3 sm:grid-cols-3">
          <label class="block"><span class="label">Balances as at</span><input type="date" class="input" [ngModel]="obDate()" (ngModelChange)="obDate.set($event)" /></label>
          <label class="block"><span class="label">Currency</span><select class="input" [ngModel]="obCurrency()" (ngModelChange)="obCurrency.set($event)">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
          <label class="block"><span class="label">Find an account</span><input class="input" [ngModel]="obSearch()" (ngModelChange)="obSearch.set($event)" placeholder="Code or name" /></label>
        </div>
        <div class="max-h-[55vh] overflow-auto">
          <table class="table w-full text-sm">
            <thead class="sticky top-0 bg-white dark:bg-slate-900"><tr><th>Account</th><th class="w-40 text-right">Debit</th><th class="w-40 text-right">Credit</th></tr></thead>
            <tbody>
              @for (a of obAccounts(); track a.id) {
                <tr><td><span class="font-mono text-xs text-slate-500">{{ a.code }}</span> {{ a.name }} <span class="text-xs text-slate-400">· {{ a.type }}</span></td>
                  <td class="py-1"><input type="number" min="0" step="0.01" class="input text-right" [ngModel]="ob()[a.id]?.debit" (ngModelChange)="setOb(a.id, 'debit', $event)" /></td>
                  <td class="py-1"><input type="number" min="0" step="0.01" class="input text-right" [ngModel]="ob()[a.id]?.credit" (ngModelChange)="setOb(a.id, 'credit', $event)" /></td></tr>
              }
            </tbody>
          </table>
        </div>
        <div class="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-4 text-sm dark:border-slate-800">
          <span>Debits <strong class="tabular-nums">{{ money(obTotals().debit) }}</strong> · credits <strong class="tabular-nums">{{ money(obTotals().credit) }}</strong>
            @if (obTotals().debit !== obTotals().credit) { · <span class="text-amber-700 dark:text-amber-300">{{ money(Math.abs(obTotals().debit - obTotals().credit)) }} goes to opening balance equity</span> }</span>
          @if (canManage) { <button class="btn" (click)="postOpening()" [disabled]="busy() || !obTotals().count">Post opening balances</button> }
        </div>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
      </div>
    }
  `,
})
export class YearEndComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly canManage = inject(AuthService).can('accounting.manage');
  readonly money = money;
  readonly Math = Math;
  readonly tabs = [{ key: 'close', label: 'Close a year' }, { key: 'opening', label: 'Opening balances' }];
  readonly tab = signal('close');
  readonly years = Array.from({ length: 8 }, (_, i) => new Date().getFullYear() + 1 - i);
  readonly year = signal(new Date().getFullYear() - 1);
  readonly ye = signal<YearEnd | null>(null);
  readonly depreciate = signal(true);
  readonly busy = signal(false);
  readonly error = signal('');

  readonly obDate = signal(isoDate(new Date(new Date().getFullYear(), 0, 0)));
  readonly obCurrency = signal('USD');
  readonly obSearch = signal('');
  readonly ob = signal<Record<string, { debit: number | null; credit: number | null }>>({});
  readonly obAccounts = computed<LedgerAccount[]>(() => {
    const q = this.obSearch().trim().toLowerCase();
    return this.store.postable().filter(a => a.isActive && ['Asset', 'Liability', 'Equity'].includes(a.type) && a.role !== 'opening_balance' && (!q || (a.code + ' ' + a.name).toLowerCase().includes(q)));
  });
  readonly obTotals = computed(() => {
    const v = Object.values(this.ob());
    return { debit: Math.round(v.reduce((n, x) => n + (Number(x.debit) || 0), 0) * 100) / 100, credit: Math.round(v.reduce((n, x) => n + (Number(x.credit) || 0), 0) * 100) / 100, count: v.filter(x => x.debit || x.credit).length };
  });

  async ngOnInit(): Promise<void> {
    try {
      await this.store.load(); await this.store.loadSetup().catch(() => undefined);
      this.obCurrency.set(this.store.meta()?.defaultCurrency ?? 'USD');
      const lastEnded = new Date(this.store.yearStart(isoDate(new Date())) + 'T00:00:00'); lastEnded.setDate(0);
      this.year.set(lastEnded.getFullYear());
      await this.load();
    } catch (e) { this.toast.err(e); }
  }

  async load(): Promise<void> {
    try { this.ye.set(await this.api.get<YearEnd>('/api/v1/accounting/year-end', { year: this.year() })); } catch (e) { this.toast.err(e); }
  }

  async close(y: YearEnd): Promise<void> {
    const warnings = y.checks.filter(c => !c.ok).map(c => '• ' + c.label + (c.detail ? ': ' + c.detail : '')).join('\n');
    if (!(await this.dialog.confirm(`Close the year ending ${y.yearEnd.slice(0, 10)}? Nothing dated on or before it can be posted afterwards.${warnings ? '\n\nStill open:\n' + warnings : ''}`, 'Close the year'))) return;
    this.busy.set(true);
    try { this.ye.set(await this.api.post<YearEnd>('/api/v1/accounting/year-end/close', { yearEnd: y.yearEnd.slice(0, 10), depreciate: this.depreciate() })); this.toast.ok('The year is closed.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async reopen(y: YearEnd): Promise<void> {
    if (!(await this.dialog.confirm('Reopen this year? Its dates can be posted to again until you close it once more. This is recorded in the audit log.', 'Reopen', true))) return;
    this.busy.set(true);
    try { this.ye.set(await this.api.post<YearEnd>('/api/v1/accounting/year-end/reopen', { yearEnd: y.yearEnd.slice(0, 10) })); this.toast.ok('Reopened.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  setOb(id: string, side: 'debit' | 'credit', v: number | null): void {
    const cur = { ...(this.ob()[id] ?? { debit: null, credit: null }) };
    cur[side] = v; if (v) cur[side === 'debit' ? 'credit' : 'debit'] = null;
    this.ob.set({ ...this.ob(), [id]: cur });
  }

  async postOpening(): Promise<void> {
    if (!(await this.dialog.confirm(`Post ${this.obTotals().count} opening balance(s) in ${this.obCurrency()} as at ${this.obDate()}?`, 'Post'))) return;
    this.busy.set(true); this.error.set('');
    try {
      const lines = Object.entries(this.ob()).filter(([, v]) => v.debit || v.credit).map(([accountId, v]) => ({ accountId, debit: Number(v.debit) || 0, credit: Number(v.credit) || 0 }));
      const j = await this.api.post<{ number: string }>('/api/v1/accounting/opening-balances', { date: this.obDate(), currency: this.obCurrency(), lines });
      this.ob.set({}); this.toast.ok(`Opening balances posted as ${j.number}.`); await this.store.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  readonly exportDoc = (): ExportDoc | null => {
    const y = this.ye(); if (!y) return null;
    return {
      title: 'Year end', subtitle: `${y.yearStart.slice(0, 10)} to ${y.yearEnd.slice(0, 10)} · ${y.closed ? 'closed' : 'open'}`,
      columns: [{ header: 'Item' }, { header: 'Detail' }],
      rows: [
        { cells: ['Result'], style: 'section' }, ...y.netProfit.map(p => ({ cells: ['Net ' + (p.amount >= 0 ? 'profit' : 'loss') + ' ' + p.currency, num(p.amount)] })),
        { cells: ['Checks'], style: 'section' }, ...y.checks.map(c => ({ cells: [(c.ok ? '✓ ' : '! ') + c.label, c.detail ?? ''] })),
        { cells: ['Closed years'], style: 'section' }, ...y.history.map(h => ({ cells: [`${h.yearStart.slice(0, 10)} to ${h.yearEnd.slice(0, 10)}`, (h.closed ? 'closed ' : 'reopened ') + (h.closedAt?.slice(0, 10) ?? '')] })),
      ],
    };
  };
}
