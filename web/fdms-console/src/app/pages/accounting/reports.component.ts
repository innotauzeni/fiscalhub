import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';
import { Api } from '../../core/api.service';
import { AccountingStore, Journal, isoDate, monthStart, money } from '../../core/accounting.store';
import { ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

type Kind = 'ledger' | 'trial-balance' | 'profit-loss' | 'balance-sheet' | 'payables' | 'receivables' | 'cash-flow' | 'profit-loss-centres' | 'changes-in-equity' | 'financial-statements';
interface EquityRow { component: string; accountId?: string | null; opening: number; profit: number; other: number; closing: number }
interface CashFlow { currency: string; from: string; to: string; netProfit: number; sections: { key: string; title: string; lines: { label: string; accountId?: string | null; amount: number }[]; total: number }[]; netChange: number; openingCash: number; closingCash: number; reconciles: boolean }
interface ByCentre { currency: string; from: string; to: string; columns: { id?: string | null; code: string; name: string }[]; rows: { section: string; accountId: string; code: string; name: string; amounts: number[]; total: number }[]; summaries: { label: string; amounts: number[]; total: number }[] }
interface Row { accountId: string; code: string; name: string; amount: number; compare?: number | null; level?: number; parentId?: string | null; hasChildren?: boolean; subtotal?: number | null; compareSubtotal?: number | null }
interface Section { title: string; key?: string; rows: Row[]; total: number; compareTotal?: number | null }
interface TbRow {
  accountId: string; code: string; name: string; type: string; debit: number; credit: number; subType?: string; parentId?: string | null; level?: number; hasChildren?: boolean; isGroup?: boolean;
  opening: number; periodDebit: number; periodCredit: number; closing: number; subtotalOpening: number; subtotalPeriodDebit: number; subtotalPeriodCredit: number; subtotalClosing: number;
}
interface LedgerLine { entryId: string; date: string; number: string; memo: string; reference?: string | null; debit: number; credit: number; balance: number; accountCode?: string | null; accountName?: string | null; source?: string | null }
interface Ledger { account: { id: string; code: string; name: string; hasChildren?: boolean }; currency: string; from: string; to: string; opening: number; lines: LedgerLine[]; totalDebit: number; totalCredit: number; closing: number; includesSubAccounts?: boolean }

const TITLES: Record<Kind, { title: string; sub: string }> = {
  'ledger': { title: 'General ledger', sub: 'Every posting of an account (and its sub-accounts), with the running balance. Click an entry to open it.' },
  'trial-balance': { title: 'Trial balance', sub: 'Opening balances, the period\'s debits and credits, and closing balances. Click an account to see its postings.' },
  'profit-loss': { title: 'Profit and loss', sub: 'Revenue, cost of sales, expenses and profit over a period, with a comparative.' },
  'balance-sheet': { title: 'Balance sheet', sub: 'What the company owns, owes and is worth at a date, classified, with a comparative.' },
  'payables': { title: 'Payables ageing', sub: 'What you owe each supplier, by how long it has been due.' },
  'receivables': { title: 'Receivables ageing', sub: 'What each customer owes you, by how long it has been due.' },
  'cash-flow': { title: 'Cash flow statement', sub: 'Where cash came from and went: operating, investing and financing (indirect method).' },
  'profit-loss-centres': { title: 'Profit by cost centre', sub: 'Income, costs and profit side by side for each department, project or branch.' },
  'changes-in-equity': { title: 'Statement of changes in equity', sub: 'How each part of equity moved: opening, profit for the period, capital in and drawings, closing.' },
  'financial-statements': { title: 'Financial statements', sub: 'The annual financial statements as one PDF: financial position, profit or loss, changes in equity, cash flows and notes, with last year alongside.' },
};
const CENTRE_SECTIONS: Record<string, string> = { revenue: 'Revenue', cost_of_sales: 'Cost of sales', operating_expense: 'Operating expenses', other_income: 'Other income', finance_cost: 'Finance costs', income_tax: 'Income tax' };

/** After these sections a statement shows a running result line. */
const RESULTS: Record<string, { label: string; field: string; compare: string }> = {
  cost_of_sales: { label: 'Gross profit', field: 'grossProfit', compare: 'compareGrossProfit' },
  other_income: { label: 'Operating profit', field: 'operatingProfit', compare: 'compareOperatingProfit' },
  finance_cost: { label: 'Profit before tax', field: 'profitBeforeTax', compare: 'compareProfitBeforeTax' },
  current_assets: { label: 'Total assets', field: 'totalAssets', compare: 'compareTotalAssets' },
  non_current_liabilities: { label: 'Total liabilities', field: 'totalLiabilities', compare: 'compareTotalLiabilities' },
};

/** The financial reports in one page: pick the report from the menu, the currency and the dates; drill from any account to its ledger and from any entry to the journal. */
@Component({
  selector: 'app-accounting-reports',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header [title]="head().title" [subtitle]="head().sub">
      @if (kind() !== 'financial-statements') {
        <button class="btn-ghost" (click)="print()" [disabled]="!data()"><app-icon name="file-text" size="h-4 w-4" />Print</button>
        <app-export-menu [build]="exportDoc" [disabled]="!data()" />
      }
    </app-page-header>

    <div class="card mb-5 print:hidden">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-6">
        @if (kind() === 'ledger') {
          <label class="block lg:col-span-2"><span class="label">Account</span><select class="input" [ngModel]="accountId()" (ngModelChange)="accountId.set($event)">
            @for (a of store.accounts(); track a.id) { <option [value]="a.id">{{ '  '.repeat(a.level ?? 0) }}{{ a.code }} · {{ a.name }}{{ a.isGroup ? ' (heading)' : '' }}</option> }</select></label>
        }
        <label class="block"><span class="label">Currency</span><select class="input" [ngModel]="currency()" (ngModelChange)="currency.set($event)">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }@if (combinable()) { <option value="ALL">All, in base currency</option> }</select></label>
        @if (ranged() || kind() === 'trial-balance') {
          <label class="block"><span class="label">From{{ kind() === 'trial-balance' ? ' (movements)' : '' }}</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event)" /></label>
        }
        <label class="block"><span class="label">{{ ranged() ? 'To' : 'As at' }}</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event)" /></label>
        @if (kind() === 'profit-loss') {
          <label class="block"><span class="label">Compare with</span><select class="input" [ngModel]="compare()" (ngModelChange)="compare.set($event)">
            <option value="">Nothing</option><option value="previous">The period before</option><option value="year">The same period last year</option></select></label>
        }
        @if ((kind() === 'profit-loss' || kind() === 'ledger') && store.centres().length) {
          <label class="block"><span class="label">Cost centre</span><select class="input" [ngModel]="centre()" (ngModelChange)="centre.set($event)">
            <option value="">All</option>@for (c of store.centres(); track c.id) { <option [value]="c.id">{{ c.code }} · {{ c.name }}</option> }<option [value]="empty">Not assigned</option></select></label>
        }
        @if (kind() === 'balance-sheet') {
          <label class="block"><span class="label">Compare with</span><select class="input" [ngModel]="compare()" (ngModelChange)="compare.set($event)">
            <option value="">Nothing</option><option value="year">A year earlier</option></select></label>
        }
      </div>
      @if (kind() !== 'payables' && kind() !== 'receivables') {
        <div class="mt-3 flex flex-wrap items-center gap-2">
          <span class="text-xs text-slate-500">Period:</span>
          @for (p of presets; track p.key) { <button type="button" class="rounded-full bg-slate-100 px-3 py-1 text-xs dark:bg-slate-800" (click)="preset(p.key)">{{ p.label }}</button> }
          <span class="flex-1"></span>
          @if (kind() === 'trial-balance') {
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" [ngModel]="extended()" (ngModelChange)="extended.set($event)" />Opening and movements</label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" [ngModel]="zero()" (ngModelChange)="zero.set($event)" />Accounts with nothing</label>
          }
          @if (kind() === 'ledger') { <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" [ngModel]="subAccounts()" (ngModelChange)="subAccounts.set($event)" />With sub-accounts</label> }
          @if (kind() === 'trial-balance' || kind() === 'profit-loss' || kind() === 'balance-sheet') {
            <button type="button" class="btn-ghost text-sm" (click)="expandAll()">Expand all</button>
            <button type="button" class="btn-ghost text-sm" (click)="collapseAll()">Collapse all</button>
          }
          <button class="btn" (click)="load()" [disabled]="busy()">{{ busy() ? 'Working…' : 'Show report' }}</button>
        </div>
      } @else { <div class="mt-3 flex justify-end"><button class="btn" (click)="load()" [disabled]="busy()">{{ busy() ? 'Working…' : 'Show report' }}</button></div> }
    </div>

    @if (kind() === 'financial-statements') {
      <div class="grid gap-5 lg:grid-cols-3">
        <div class="card">
          <h2 class="card-title mb-2">Annual financial statements</h2>
          <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">For {{ from() }} to {{ to() }} in {{ currency() }}, with the same dates a year earlier alongside. Check the trial balance and close the year first.</p>
          <ul class="mb-4 list-inside list-disc text-sm text-slate-600 dark:text-slate-300"><li>Statement of financial position</li><li>Statement of profit or loss</li><li>Statement of changes in equity</li><li>Statement of cash flows</li><li>Notes, starting with your accounting policies</li></ul>
          <button class="btn w-full" (click)="pack()" [disabled]="busy()"><app-icon name="download" size="h-4 w-4" />{{ busy() ? 'Preparing…' : 'Download PDF' }}</button>
        </div>
        <div class="card lg:col-span-2">
          <div class="mb-2 flex items-center justify-between"><h2 class="card-title">Note 1: accounting policies</h2>@if (policies()?.isStandard) { <span class="badge badge-info">Standard wording</span> }</div>
          <p class="mb-2 text-xs text-slate-500">Each paragraph starts with its heading on its own line; leave an empty line between paragraphs.</p>
          <textarea class="input font-mono text-xs" rows="16" [ngModel]="policyText()" (ngModelChange)="policyText.set($event)"></textarea>
          <div class="mt-3 flex justify-end gap-2"><button class="btn-ghost" (click)="resetPolicies()">Use the standard wording</button><button class="btn" (click)="savePolicies()">Save</button></div>
        </div>
      </div>
    } @else if (data(); as d) {
      <div class="card">
        <div class="mb-4 border-b border-slate-100 pb-3 dark:border-slate-800">
          <h2 class="text-lg font-semibold">{{ head().title }}</h2>
          <p class="text-sm text-slate-500">
            @if (kind() === 'trial-balance') { Movements {{ d.from | date: 'd MMM y' }} to {{ d.asAt | date: 'd MMM y' }} }
            @else if (ranged()) { {{ d.from | date: 'd MMM y' }} to {{ d.to | date: 'd MMM y' }}@if (d.compareFrom) { · compared with {{ d.compareFrom | date: 'd MMM y' }} to {{ d.compareTo | date: 'd MMM y' }} } }
            @else { As at {{ d.asAt | date: 'd MMM y' }}@if (d.compareAsAt) { · compared with {{ d.compareAsAt | date: 'd MMM y' }} } }
            · {{ d.currency }}{{ currency() === 'ALL' ? ' (all currencies combined at your exchange rates)' : '' }}@if (kind() === 'ledger') { · {{ d.account.code }} {{ d.account.name }}{{ d.includesSubAccounts ? ' and sub-accounts' : '' }} }</p>
        </div>

        @switch (group()) {
          @case ('ledger') {
            <div class="overflow-x-auto"><table class="table">
              <thead><tr><th>Date</th><th>Entry</th>@if (d.includesSubAccounts) { <th>Account</th> }<th>Description</th><th class="text-right">Debit</th><th class="text-right">Credit</th><th class="text-right">Balance</th></tr></thead>
              <tbody>
                <tr class="text-slate-500"><td [attr.colspan]="d.includesSubAccounts ? 6 : 5">Opening balance</td><td class="text-right tabular-nums">{{ money(d.opening) }}</td></tr>
                @for (l of d.lines; track $index) {
                  <tr class="cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50" (click)="openJournal(l.entryId)">
                    <td class="whitespace-nowrap">{{ l.date | date: 'd MMM y' }}</td><td class="font-mono text-xs text-brand-600">{{ l.number }}</td>
                    @if (d.includesSubAccounts) { <td class="whitespace-nowrap font-mono text-xs">{{ l.accountCode }}</td> }
                    <td>{{ l.memo }}@if (l.reference) { <span class="text-xs text-slate-400"> · {{ l.reference }}</span> }</td>
                    <td class="text-right tabular-nums">{{ l.debit ? money(l.debit) : '' }}</td><td class="text-right tabular-nums">{{ l.credit ? money(l.credit) : '' }}</td><td class="text-right font-medium tabular-nums">{{ money(l.balance) }}</td></tr>
                }
                @if (!d.lines.length) { <tr><td colspan="7" class="py-6 text-center text-slate-500">No postings in this period.</td></tr> }
              </tbody>
              <tfoot><tr class="font-semibold"><td [attr.colspan]="d.includesSubAccounts ? 4 : 3">Totals and closing balance</td><td class="text-right tabular-nums">{{ money(d.totalDebit) }}</td><td class="text-right tabular-nums">{{ money(d.totalCredit) }}</td><td class="text-right tabular-nums">{{ money(d.closing) }}</td></tr></tfoot>
            </table></div>
          }
          @case ('trial-balance') {
            <div class="overflow-x-auto"><table class="table">
              <thead>
                @if (extended()) {
                  <tr><th>Code</th><th>Account</th><th class="text-right">Opening</th><th class="text-right">Debits</th><th class="text-right">Credits</th><th class="text-right">Closing debit</th><th class="text-right">Closing credit</th></tr>
                } @else { <tr><th>Code</th><th>Account</th><th class="text-right">Debit</th><th class="text-right">Credit</th></tr> }
              </thead>
              <tbody>
                @for (r of tbRows(); track r.accountId) {
                  @let open = r.hasChildren && collapsed().has(r.accountId);
                  @let closing = open ? r.subtotalClosing : r.closing;
                  <tr [class.font-semibold]="r.isGroup || open">
                    <td class="whitespace-nowrap font-mono text-xs"><span class="inline-flex items-center" [style.padding-left.rem]="(r.level ?? 0) * 1.1">
                      @if (r.hasChildren) { <button type="button" class="icon-btn -ml-1 mr-0.5 h-6! w-6! print:hidden" (click)="toggle(r.accountId)"><app-icon [name]="open ? 'chevron-right' : 'chevron'" size="h-3.5 w-3.5" /></button> } @else { <span class="inline-block w-6"></span> }
                      {{ r.code }}</span></td>
                    <td>@if (r.accountId !== empty) { <button type="button" class="text-left hover:text-brand-600 hover:underline" (click)="drill(r.accountId, r.hasChildren ?? false)">{{ r.name }}</button> } @else { {{ r.name }} }
                      @if (open) { <span class="text-xs font-normal text-slate-400"> (with sub-accounts)</span> }</td>
                    @if (extended()) {
                      <td class="text-right tabular-nums">{{ signed(open ? r.subtotalOpening : r.opening) }}</td>
                      <td class="text-right tabular-nums">{{ nz(open ? r.subtotalPeriodDebit : r.periodDebit) }}</td>
                      <td class="text-right tabular-nums">{{ nz(open ? r.subtotalPeriodCredit : r.periodCredit) }}</td>
                    }
                    <td class="text-right tabular-nums">{{ closing > 0 ? money(closing) : '' }}</td>
                    <td class="text-right tabular-nums">{{ closing < 0 ? money(-closing) : '' }}</td>
                  </tr>
                }
                @if (!d.rows.length) { <tr><td colspan="7" class="py-6 text-center text-slate-500">Nothing is posted yet in this currency.</td></tr> }
              </tbody>
              <tfoot><tr class="font-semibold"><td colspan="2">Totals</td>
                @if (extended()) { <td class="text-right tabular-nums">{{ money(d.totalOpeningDebit - d.totalOpeningCredit) }}</td><td class="text-right tabular-nums">{{ money(d.totalPeriodDebit) }}</td><td class="text-right tabular-nums">{{ money(d.totalPeriodCredit) }}</td> }
                <td class="text-right tabular-nums">{{ money(d.totalDebit) }}</td><td class="text-right tabular-nums">{{ money(d.totalCredit) }}</td></tr></tfoot>
            </table></div>
            <p class="mt-3 text-sm" [class]="d.balanced ? 'text-emerald-600' : 'text-red-600'">{{ d.balanced ? 'In balance: total debits equal total credits.' : 'Out of balance. Contact support.' }}</p>
            <p class="hint">Income and expenses start each financial year at nil; earlier years' profit is in retained earnings. Opening: debit positive, credit negative.</p>
          }
          @case ('cash-flow') {
            <table class="w-full text-sm">
              @for (s of d.sections; track s.key) {
                <tbody>
                  <tr><td colspan="2" class="pb-1 pt-4 text-sm font-semibold uppercase tracking-wide text-slate-500">{{ s.title }}</td></tr>
                  @for (l of s.lines; track $index) {
                    <tr class="border-b border-slate-100 dark:border-slate-800"><td class="py-1.5">
                      @if (l.accountId) { <button type="button" class="text-left hover:text-brand-600 hover:underline" (click)="drill(l.accountId, false)">{{ l.label }}</button> } @else { {{ l.label }} }</td>
                      <td class="py-1.5 text-right tabular-nums">{{ signed(l.amount) || '0.00' }}</td></tr>
                  } @empty { <tr><td colspan="2" class="py-1.5 text-slate-400">None</td></tr> }
                  <tr class="font-semibold"><td class="py-2">Net cash from {{ s.key }} activities</td><td class="py-2 text-right tabular-nums">{{ signed(s.total) || '0.00' }}</td></tr>
                </tbody>
              }
              <tbody>
                <tr class="border-t-2 border-slate-300 font-semibold dark:border-slate-600"><td class="py-2">Net change in cash</td><td class="py-2 text-right tabular-nums">{{ signed(d.netChange) || '0.00' }}</td></tr>
                <tr><td class="py-1.5">Cash at the start</td><td class="py-1.5 text-right tabular-nums">{{ money(d.openingCash) }}</td></tr>
                <tr class="font-semibold"><td class="py-1.5">Cash at the end</td><td class="py-1.5 text-right tabular-nums">{{ money(d.closingCash) }}</td></tr>
              </tbody>
            </table>
            <p class="mt-3 text-sm" [class]="d.reconciles ? 'text-emerald-600' : 'text-red-600'">{{ d.reconciles ? 'The cash flow agrees with the cash and bank accounts.' : 'The cash flow does not agree with the cash accounts. Check the classification of your accounts.' }}</p>
          }
          @case ('profit-loss-centres') {
            <div class="overflow-x-auto"><table class="table text-sm">
              <thead><tr><th>Account</th>@for (c of d.columns; track $index) { <th class="text-right">{{ c.code || c.name }}<div class="text-[10px] font-normal normal-case text-slate-400">{{ c.code ? c.name : '' }}</div></th> }<th class="text-right">Total</th></tr></thead>
              <tbody>
                @for (r of d.rows; track r.accountId; let i = $index) {
                  @if (i === 0 || d.rows[i - 1].section !== r.section) { <tr><td [attr.colspan]="d.columns.length + 2" class="pt-3 text-xs font-semibold uppercase tracking-wide text-slate-500">{{ centreSection(r.section) }}</td></tr> }
                  <tr><td><button type="button" class="text-left hover:text-brand-600 hover:underline" (click)="drill(r.accountId, false)"><span class="font-mono text-xs text-slate-500">{{ r.code }}</span> {{ r.name }}</button></td>
                    @for (a of r.amounts; track $index) { <td class="text-right tabular-nums">{{ nz(a) }}</td> }<td class="text-right font-medium tabular-nums">{{ money(r.total) }}</td></tr>
                }
                @if (!d.rows.length) { <tr><td [attr.colspan]="d.columns.length + 2" class="py-6 text-center text-slate-500">Nothing posted in this period.</td></tr> }
              </tbody>
              <tfoot>@for (x of d.summaries; track x.label) {
                <tr [class.font-semibold]="isResult(x.label)" [class.bg-slate-50]="isResult(x.label)" [class.dark:bg-slate-800/50]="isResult(x.label)"><td>{{ x.label }}</td>@for (a of x.amounts; track $index) { <td class="text-right tabular-nums">{{ money(a) }}</td> }<td class="text-right tabular-nums">{{ money(x.total) }}</td></tr>
              }</tfoot>
            </table></div>
            <p class="hint mt-2">Tag journal lines and bills with a cost centre; a branch cost centre gets its branch's sales by itself.</p>
          }
          @case ('changes-in-equity') {
            <div class="overflow-x-auto"><table class="table">
              <thead><tr><th>Component</th><th class="text-right">Opening</th><th class="text-right">Profit for the period</th><th class="text-right">Other movements</th><th class="text-right">Closing</th></tr></thead>
              <tbody>
                @for (r of d.rows; track $index) {
                  <tr><td>@if (r.accountId) { <button type="button" class="text-left hover:text-brand-600 hover:underline" (click)="drill(r.accountId, false)">{{ r.component }}</button> } @else { {{ r.component }} }</td>
                    <td class="text-right tabular-nums">{{ signed(r.opening) || '0.00' }}</td><td class="text-right tabular-nums">{{ signed(r.profit) }}</td><td class="text-right tabular-nums">{{ signed(r.other) }}</td><td class="text-right font-medium tabular-nums">{{ signed(r.closing) || '0.00' }}</td></tr>
                } @empty { <tr><td colspan="5" class="py-6 text-center text-slate-500">No equity yet in this currency.</td></tr> }
              </tbody>
              <tfoot><tr class="font-semibold"><td>{{ d.total.component }}</td><td class="text-right tabular-nums">{{ signed(d.total.opening) || '0.00' }}</td><td class="text-right tabular-nums">{{ signed(d.total.profit) || '0.00' }}</td><td class="text-right tabular-nums">{{ signed(d.total.other) || '0.00' }}</td><td class="text-right tabular-nums">{{ signed(d.total.closing) || '0.00' }}</td></tr></tfoot>
            </table></div>
            <p class="hint mt-2">Other movements are capital put in, drawings, dividends and anything else posted straight to equity. The closing total equals equity on the balance sheet.</p>
          }
          @case ('ageing') {
            <div class="overflow-x-auto"><table class="table">
              <thead><tr><th>{{ kind() === 'receivables' ? 'Customer' : 'Supplier' }}</th><th class="text-right">Not due</th><th class="text-right">1-30 days</th><th class="text-right">31-60 days</th><th class="text-right">61-90 days</th><th class="text-right">Over 90</th><th class="text-right">Total owing</th></tr></thead>
              <tbody>
                @for (r of d.rows; track $index) { <tr><td class="font-medium">{{ r.supplier ?? r.customer }}</td><td class="text-right tabular-nums">{{ r.notDue ? money(r.notDue) : '' }}</td><td class="text-right tabular-nums">{{ r.days1To30 ? money(r.days1To30) : '' }}</td><td class="text-right tabular-nums">{{ r.days31To60 ? money(r.days31To60) : '' }}</td>
                  <td class="text-right tabular-nums">{{ r.days61To90 ? money(r.days61To90) : '' }}</td><td class="text-right tabular-nums text-red-600">{{ r.over90 ? money(r.over90) : '' }}</td><td class="text-right font-semibold tabular-nums">{{ money(r.total) }}</td></tr> }
                @if (!d.rows.length) { <tr><td colspan="7" class="py-6 text-center text-slate-500">{{ kind() === 'receivables' ? 'Nobody owes you anything in this currency.' : 'You owe nothing in this currency.' }}</td></tr> }
              </tbody>
              <tfoot><tr class="font-semibold"><td>Total</td><td class="text-right tabular-nums">{{ money(d.totals.notDue) }}</td><td class="text-right tabular-nums">{{ money(d.totals.days1To30) }}</td><td class="text-right tabular-nums">{{ money(d.totals.days31To60) }}</td><td class="text-right tabular-nums">{{ money(d.totals.days61To90) }}</td><td class="text-right tabular-nums">{{ money(d.totals.over90) }}</td><td class="text-right tabular-nums">{{ money(d.totals.total) }}</td></tr></tfoot>
            </table></div>
          }
          @default {
            @let cmp = hasCompare();
            <div class="overflow-x-auto"><table class="w-full text-sm">
              <thead><tr class="border-b border-slate-200 text-xs uppercase tracking-wide text-slate-500 dark:border-slate-700"><th class="py-2 text-left" colspan="2"></th>
                <th class="py-2 text-right">{{ kind() === 'profit-loss' ? (d.to | date: 'd MMM y') : (d.asAt | date: 'd MMM y') }}</th>
                @if (cmp) { <th class="py-2 text-right">{{ kind() === 'profit-loss' ? (d.compareTo | date: 'd MMM y') : (d.compareAsAt | date: 'd MMM y') }}</th> }</tr></thead>
              @for (s of sections(); track s.title) {
                <tbody>
                  <tr><td colspan="4" class="pb-1 pt-4 text-sm font-semibold uppercase tracking-wide text-slate-500">{{ s.title }}</td></tr>
                  @for (r of visible(s); track $index) {
                    @let rolled = r.hasChildren && collapsed().has(r.accountId);
                    <tr class="border-b border-slate-100 dark:border-slate-800">
                      <td class="w-28 whitespace-nowrap py-1.5 font-mono text-xs text-slate-500"><span class="inline-flex items-center" [style.padding-left.rem]="(r.level ?? 0) * 1.1">
                        @if (r.hasChildren) { <button type="button" class="icon-btn -ml-1 mr-0.5 h-6! w-6! print:hidden" (click)="toggle(r.accountId)"><app-icon [name]="rolled ? 'chevron-right' : 'chevron'" size="h-3.5 w-3.5" /></button> }
                        {{ r.code }}</span></td>
                      <td class="py-1.5" [class.font-medium]="r.hasChildren">
                        @if (r.accountId !== empty) { <button type="button" class="text-left hover:text-brand-600 hover:underline" (click)="drill(r.accountId, r.hasChildren ?? false)">{{ r.name }}</button> } @else { {{ r.name }} }</td>
                      <td class="py-1.5 text-right tabular-nums" [class.font-medium]="rolled">{{ rolled ? money(r.subtotal) : (r.hasChildren && !r.amount ? '' : money(r.amount)) }}</td>
                      @if (cmp) { <td class="py-1.5 text-right tabular-nums text-slate-500">{{ rolled ? money(r.compareSubtotal) : (r.hasChildren && !r.compare ? '' : money(r.compare)) }}</td> }
                    </tr>
                  }
                  @if (!s.rows.length) { <tr><td colspan="4" class="py-1.5 text-slate-400">None</td></tr> }
                  <tr class="font-semibold"><td></td><td class="py-2">Total {{ s.title.toLowerCase() }}</td><td class="py-2 text-right tabular-nums">{{ money(s.total) }}</td>@if (cmp) { <td class="py-2 text-right tabular-nums">{{ money(s.compareTotal) }}</td> }</tr>
                  @if (result(s.key); as res) {
                    <tr class="bg-slate-50 font-semibold dark:bg-slate-800/50"><td></td><td class="py-2">{{ res.label }}</td><td class="py-2 text-right tabular-nums">{{ money(d[res.field]) }}</td>@if (cmp) { <td class="py-2 text-right tabular-nums">{{ money(d[res.compare]) }}</td> }</tr>
                  }
                </tbody>
              }
            </table></div>
            @if (kind() === 'profit-loss') {
              <div class="mt-5 rounded-xl bg-slate-50 p-4 dark:bg-slate-800/50">
                <div class="flex justify-between text-lg font-semibold"><span>Net profit</span>
                  <span class="tabular-nums" [class.text-emerald-600]="d.netProfit >= 0" [class.text-red-600]="d.netProfit < 0">{{ money(d.netProfit) }} {{ d.currency }}@if (cmp) { <span class="ml-3 text-sm font-normal text-slate-500">({{ money(d.compareNetProfit) }})</span> }</span></div>
              </div>
            } @else {
              <div class="mt-5 rounded-xl p-4" [class]="d.balanced ? 'bg-emerald-50 dark:bg-emerald-900/20' : 'bg-red-50 dark:bg-red-900/20'">
                <div class="flex justify-between text-sm"><span>Total assets</span><span class="font-semibold tabular-nums">{{ money(d.totalAssets) }}</span></div>
                <div class="flex justify-between text-sm"><span>Total liabilities and equity</span><span class="font-semibold tabular-nums">{{ money(d.totalLiabilitiesAndEquity) }}</span></div>
                <p class="mt-1 text-xs">{{ d.balanced ? 'The balance sheet balances.' : 'The balance sheet does not balance. Contact support.' }}</p>
              </div>
            }
          }
        }
      </div>
    } @else if (loaded()) { <div class="card text-center text-slate-500">Nothing to show.</div> }

    <!-- drill down: an account's postings -->
    @if (drilled(); as l) {
      <app-modal [title]="l.account.code + ' · ' + l.account.name" width="max-w-4xl" (closed)="drilled.set(null)">
        <p class="mb-3 text-sm text-slate-500">{{ l.from | date: 'd MMM y' }} to {{ l.to | date: 'd MMM y' }} · {{ l.currency }}{{ l.includesSubAccounts ? ' · with sub-accounts' : '' }}. Click an entry to open it.</p>
        <div class="max-h-[60vh] overflow-auto"><table class="table w-full text-sm">
          <thead><tr><th>Date</th><th>Entry</th>@if (l.includesSubAccounts) { <th>Account</th> }<th>Description</th><th class="text-right">Debit</th><th class="text-right">Credit</th><th class="text-right">Balance</th></tr></thead>
          <tbody>
            <tr class="text-slate-500"><td [attr.colspan]="l.includesSubAccounts ? 6 : 5">Opening balance</td><td class="text-right tabular-nums">{{ money(l.opening) }}</td></tr>
            @for (x of l.lines; track $index) {
              <tr class="cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50" (click)="openJournal(x.entryId)">
                <td class="whitespace-nowrap">{{ x.date | date: 'd MMM y' }}</td><td class="font-mono text-xs text-brand-600">{{ x.number }}</td>
                @if (l.includesSubAccounts) { <td class="whitespace-nowrap font-mono text-xs">{{ x.accountCode }}</td> }
                <td>{{ x.memo }}</td><td class="text-right tabular-nums">{{ x.debit ? money(x.debit) : '' }}</td><td class="text-right tabular-nums">{{ x.credit ? money(x.credit) : '' }}</td><td class="text-right tabular-nums">{{ money(x.balance) }}</td></tr>
            } @empty { <tr><td colspan="7" class="py-6 text-center text-slate-500">No postings in this period.</td></tr> }
          </tbody>
          <tfoot><tr class="font-semibold"><td [attr.colspan]="l.includesSubAccounts ? 4 : 3">Closing balance</td><td class="text-right tabular-nums">{{ money(l.totalDebit) }}</td><td class="text-right tabular-nums">{{ money(l.totalCredit) }}</td><td class="text-right tabular-nums">{{ money(l.closing) }}</td></tr></tfoot>
        </table></div>
      </app-modal>
    }

    <!-- drill down: one journal entry -->
    @if (journal(); as j) {
      <app-modal [title]="'Entry ' + j.number" width="max-w-2xl" (closed)="journal.set(null)">
        <dl class="mb-4 grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
          <dt class="text-slate-500">Date</dt><dd>{{ j.date | date: 'd MMM y' }}</dd>
          <dt class="text-slate-500">Description</dt><dd>{{ j.memo }}</dd>
          @if (j.reference) { <dt class="text-slate-500">Reference</dt><dd>{{ j.reference }}</dd> }
          <dt class="text-slate-500">From</dt><dd>{{ sourceLabel(j.source) }}{{ j.reversed ? ' · reversed' : '' }}{{ j.isReversal ? ' · a reversal' : '' }}</dd>
          @if (j.createdBy) { <dt class="text-slate-500">Posted by</dt><dd>{{ j.createdBy }}</dd> }
        </dl>
        <table class="table w-full text-sm">
          <thead><tr><th>Account</th><th>Description</th><th class="text-right">Debit</th><th class="text-right">Credit</th></tr></thead>
          <tbody>@for (x of j.lines; track x.id) { <tr><td><button type="button" class="text-left hover:text-brand-600 hover:underline" (click)="journal.set(null); drill(x.accountId, false)">{{ x.accountCode }} · {{ x.accountName }}</button></td><td class="text-slate-500">{{ x.description }}</td>
            <td class="text-right tabular-nums">{{ x.debit ? money(x.debit) : '' }}</td><td class="text-right tabular-nums">{{ x.credit ? money(x.credit) : '' }}</td></tr> }</tbody>
          <tfoot><tr class="font-semibold"><td colspan="2">Total</td><td class="text-right tabular-nums">{{ money(j.total) }}</td><td class="text-right tabular-nums">{{ money(j.total) }}</td></tr></tfoot>
        </table>
      </app-modal>
    }
  `,
})
export class AccountingReportsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(AccountingStore);
  readonly money = (v: number | null | undefined): string => money(v ?? 0);
  readonly empty = '00000000-0000-0000-0000-000000000000';
  readonly kind = toSignal(inject(ActivatedRoute).paramMap.pipe(map(p => (p.get('kind') ?? 'trial-balance') as Kind)), { initialValue: 'trial-balance' as Kind });
  readonly group = computed(() => (this.kind() === 'payables' || this.kind() === 'receivables' ? 'ageing' : this.kind()));
  readonly head = computed(() => TITLES[this.kind()] ?? TITLES['trial-balance']);
  readonly ranged = computed(() => ['ledger', 'profit-loss', 'cash-flow', 'profit-loss-centres', 'changes-in-equity', 'financial-statements'].includes(this.kind()));
  /** The reports that can show every currency together, at the exchange rates. */
  readonly combinable = computed(() => ['trial-balance', 'profit-loss', 'balance-sheet'].includes(this.kind()));
  private readonly route = inject(ActivatedRoute);
  readonly policies = signal<{ text: string; isStandard: boolean } | null>(null);
  readonly policyText = signal('');
  readonly centre = signal('');
  readonly presets = [
    { key: 'month', label: 'This month' }, { key: 'last-month', label: 'Last month' }, { key: 'quarter', label: 'This quarter' },
    { key: 'ytd', label: 'Year to date' }, { key: 'last-year', label: 'Last financial year' },
  ];
  readonly accountId = signal('');
  readonly currency = signal('USD');
  readonly from = signal(monthStart());
  readonly to = signal(isoDate(new Date()));
  readonly compare = signal('');
  readonly extended = signal(true);
  readonly zero = signal(false);
  readonly subAccounts = signal(true);
  readonly collapsed = signal(new Set<string>());
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly data = signal<any>(null);
  readonly drilled = signal<Ledger | null>(null);
  readonly journal = signal<Journal | null>(null);
  readonly busy = signal(false);
  readonly loaded = signal(false);
  readonly sections = computed<Section[]>(() => (this.data()?.sections ?? []) as Section[]);
  readonly hasCompare = computed(() => !!(this.data()?.compareFrom || this.data()?.compareAsAt));

  /** Trial balance rows with the sub-accounts of a collapsed account hidden. */
  readonly tbRows = computed<TbRow[]>(() => this.hideCollapsed((this.data()?.rows ?? []) as TbRow[]));

  constructor() {
    // moving between the reports in the menu starts that report fresh
    effect(() => {
      this.kind();
      untracked(() => {
        this.data.set(null); this.loaded.set(false); this.compare.set(''); this.collapsed.set(new Set());
        if (this.currency() === 'ALL' && !this.combinable()) this.currency.set(this.store.meta()?.defaultCurrency ?? 'USD');
      });
      if (this.store.meta()) { this.defaults(); void this.load(); }
    });
  }

  async ngOnInit(): Promise<void> {
    try {
      await this.store.load();
      await this.store.loadSetup().catch(() => undefined);
      await this.store.loadCentres().catch(() => undefined);
      this.currency.set(this.route.snapshot.queryParamMap.get('currency') ?? this.store.meta()?.defaultCurrency ?? 'USD');
      this.accountId.set(this.store.accounts().find(a => a.role === 'sales')?.id ?? this.store.accounts()[0]?.id ?? '');
      this.defaults();
      await this.load();
    } catch (e) { this.toast.err(e); }
  }

  /** The trial balance's movements start with the financial year; the other reports with the month. */
  private defaults(): void {
    if (this.kind() === 'trial-balance') this.from.set(this.store.yearStart(this.to()));
    if (this.kind() === 'financial-statements') {   // the last financial year that has ended
      const ys = new Date(this.store.yearStart(isoDate(new Date())) + 'T00:00:00');
      this.from.set(isoDate(new Date(ys.getFullYear() - 1, ys.getMonth(), 1))); this.to.set(isoDate(new Date(ys.getFullYear(), ys.getMonth(), 0)));
    }
  }

  async pack(): Promise<void> {
    this.busy.set(true);
    try { await this.api.download('/api/v1/accounting/reports/financial-statements?currency=' + this.currency() + '&from=' + this.from() + '&to=' + this.to(), 'financial-statements-' + this.currency() + '-' + this.to() + '.pdf'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
  private setPolicies(p: { text: string; isStandard: boolean }): void { this.policies.set(p); this.policyText.set(p.text); }
  async savePolicies(): Promise<void> {
    try { this.setPolicies(await this.api.put('/api/v1/accounting/policies', { text: this.policyText() })); this.toast.ok('Accounting policies saved.'); } catch (e) { this.toast.err(e); }
  }
  async resetPolicies(): Promise<void> {
    try { this.setPolicies(await this.api.put('/api/v1/accounting/policies', { text: null })); this.toast.ok('Back to the standard wording.'); } catch (e) { this.toast.err(e); }
  }

  private hideCollapsed<T extends { accountId: string; parentId?: string | null }>(rows: T[]): T[] {
    const closed = this.collapsed(); if (!closed.size) return rows;
    const byId = new Map(rows.map(r => [r.accountId, r]));
    return rows.filter(r => { for (let p = r.parentId ? byId.get(r.parentId) : undefined, n = 0; p && n < 12; p = p.parentId ? byId.get(p.parentId) : undefined, n++) if (closed.has(p.accountId)) return false; return true; });
  }
  visible(s: Section): Row[] { return this.hideCollapsed(s.rows); }
  toggle(id: string): void { const s = new Set(this.collapsed()); if (s.has(id)) s.delete(id); else s.add(id); this.collapsed.set(s); }
  expandAll(): void { this.collapsed.set(new Set()); }
  collapseAll(): void {
    const d = this.data(); const ids = new Set<string>();
    for (const r of (d?.rows ?? []) as TbRow[]) if (r.hasChildren) ids.add(r.accountId);
    for (const s of this.sections()) for (const r of s.rows) if (r.hasChildren) ids.add(r.accountId);
    this.collapsed.set(ids);
  }
  result(key?: string) { return key ? RESULTS[key] ?? null : null; }
  centreSection(key: string): string { return CENTRE_SECTIONS[key] ?? key; }
  isResult(label: string): boolean { return ['Gross profit', 'Operating profit', 'Net profit'].includes(label); }
  nz(v: number): string { return v ? money(v) : ''; }
  signed(v: number): string { return v ? (v < 0 ? '(' + money(-v) + ')' : money(v)) : ''; }
  sourceLabel(s: string): string { return ({ manual: 'Manual journal', sales: 'Fiscal receipt', cashbook: 'Cashbook', bank: 'Bank reconciliation', reversal: 'Reversal', bill: 'Supplier bill', payment: 'Payment', asset: 'Fixed assets', depn: 'Depreciation', opening: 'Opening balances', vat: 'VAT settlement' } as Record<string, string>)[s] ?? s; }

  preset(key: string): void {
    const today = new Date(); const y = today.getFullYear(); const m = today.getMonth();
    const ys = this.store.yearStart(isoDate(today)); const ysDate = new Date(ys + 'T00:00:00');
    let f: Date, t: Date;
    switch (key) {
      case 'last-month': f = new Date(y, m - 1, 1); t = new Date(y, m, 0); break;
      case 'quarter': f = new Date(y, Math.floor(m / 3) * 3, 1); t = today; break;
      case 'ytd': f = ysDate; t = today; break;
      case 'last-year': f = new Date(ysDate.getFullYear() - 1, ysDate.getMonth(), 1); t = new Date(ysDate.getFullYear(), ysDate.getMonth(), 0); break;
      default: f = new Date(y, m, 1); t = today;
    }
    this.from.set(isoDate(f)); this.to.set(isoDate(t));
    void this.load();
  }

  async load(): Promise<void> {
    if (!this.store.meta()) return;
    if (this.kind() === 'financial-statements') {
      if (!this.policies()) try { this.setPolicies(await this.api.get('/api/v1/accounting/policies')); } catch (e) { this.toast.err(e); }
      return;
    }
    this.busy.set(true);
    try {
      const k = this.kind(); const cur = this.currency();
      const q: Record<string, string | boolean> =
        k === 'ledger' ? { currency: cur, from: this.from(), to: this.to(), accountId: this.accountId(), subAccounts: this.subAccounts(), costCentreId: this.centre() }
        : k === 'profit-loss' ? { currency: cur, from: this.from(), to: this.to(), compare: this.compare(), costCentreId: this.centre() }
        : k === 'cash-flow' || k === 'profit-loss-centres' || k === 'changes-in-equity' ? { currency: cur, from: this.from(), to: this.to() }
        : k === 'trial-balance' ? { currency: cur, from: this.from(), asAt: this.to(), zero: this.zero() }
        : k === 'balance-sheet' ? { currency: cur, asAt: this.to(), compare: this.compare() }
        : { currency: cur, asAt: this.to() };
      this.data.set(await this.api.get('/api/v1/accounting/reports/' + k, q));
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); this.loaded.set(true); }
  }

  /** An account's postings for the report's period (a balance sheet account from the start of the financial year). */
  async drill(accountId: string, subAccounts: boolean): Promise<void> {
    const d = this.data(); if (!d) return;
    const to = (d.asAt ?? d.to ?? this.to()).slice(0, 10);
    const from = (this.kind() === 'balance-sheet' ? this.store.yearStart(to) : (d.from ?? this.from())).slice(0, 10);
    try { this.drilled.set(await this.api.get<Ledger>('/api/v1/accounting/reports/ledger', { accountId, currency: d.currency, from, to, subAccounts })); }
    catch (e) { this.toast.err(e); }
  }

  async openJournal(id: string): Promise<void> {
    try { this.journal.set(await this.api.get<Journal>('/api/v1/accounting/journals/' + id)); } catch (e) { this.toast.err(e); }
  }

  print(): void { window.print(); }

  /** The report on screen as a table, for PDF, Excel or CSV. */
  readonly exportDoc = (): ExportDoc | null => {
    const d = this.data(); if (!d) return null;
    const title = this.head().title;
    const when = this.kind() === 'trial-balance' ? `Movements ${d.from?.slice(0, 10)} to ${d.asAt?.slice(0, 10)}` : d.asAt ? `As at ${d.asAt.slice(0, 10)}` : `${d.from?.slice(0, 10)} to ${d.to?.slice(0, 10)}`;
    const centre = this.centre() ? ' · ' + (this.centre() === this.empty ? 'not assigned to a cost centre' : this.store.centres().find(c => c.id === this.centre())?.name ?? '') : '';
    const subtitle = when + ' · ' + d.currency + centre;
    switch (this.kind()) {
      case 'ledger': return {
        title: title + ' — ' + d.account.code + ' ' + d.account.name, subtitle, landscape: true,
        columns: [{ header: 'Date' }, { header: 'Entry' }, { header: 'Account' }, { header: 'Description' }, { header: 'Debit', kind: 'money' }, { header: 'Credit', kind: 'money' }, { header: 'Balance', kind: 'money' }],
        rows: [{ cells: ['', '', '', 'Opening balance', '', '', num(d.opening)], style: 'muted' },
          ...(d.lines as LedgerLine[]).map(l => ({ cells: [l.date.slice(0, 10), l.number, l.accountCode ?? '', l.memo, num(l.debit, true), num(l.credit, true), num(l.balance)] })),
          { cells: ['', '', '', 'Totals and closing balance', num(d.totalDebit), num(d.totalCredit), num(d.closing)], style: 'total' }],
      };
      case 'receivables': case 'payables': {
        const who = this.kind() === 'receivables' ? 'Customer' : 'Supplier';
        type R = { supplier?: string; customer?: string; notDue: number; days1To30: number; days31To60: number; days61To90: number; over90: number; total: number };
        const row = (r: R) => [num(r.notDue, true), num(r.days1To30, true), num(r.days31To60, true), num(r.days61To90, true), num(r.over90, true), num(r.total)];
        return { title, subtitle, columns: [{ header: who }, ...['Not due', '1-30 days', '31-60 days', '61-90 days', 'Over 90', 'Total'].map(h => ({ header: h, kind: 'money' as const }))],
          rows: [...(d.rows as R[]).map(r => ({ cells: [r.supplier ?? r.customer ?? '', ...row(r)] })), { cells: ['Total', ...row(d.totals)], style: 'total' }] };
      }
      case 'trial-balance': return {
        title, subtitle, landscape: true,
        columns: [{ header: 'Code' }, { header: 'Account' }, { header: 'Opening', kind: 'money' }, { header: 'Debits', kind: 'money' }, { header: 'Credits', kind: 'money' }, { header: 'Closing debit', kind: 'money' }, { header: 'Closing credit', kind: 'money' }],
        rows: [...(d.rows as TbRow[]).map(r => ({ cells: [r.code, r.name, num(r.opening, true), num(r.periodDebit, true), num(r.periodCredit, true), num(r.debit, true), num(r.credit, true)], indent: r.level ?? 0, style: r.isGroup ? 'bold' as const : 'normal' as const })),
          { cells: ['', 'Totals', num(d.totalOpeningDebit - d.totalOpeningCredit), num(d.totalPeriodDebit), num(d.totalPeriodCredit), num(d.totalDebit), num(d.totalCredit)], style: 'total' }],
      };
      case 'changes-in-equity': {
        const row = (r: EquityRow, style?: 'total') => ({ cells: [r.component, num(r.opening), num(r.profit, true), num(r.other, true), num(r.closing)], style });
        return { title, subtitle, columns: [{ header: 'Component' }, { header: 'Opening', kind: 'money' }, { header: 'Profit', kind: 'money' }, { header: 'Other movements', kind: 'money' }, { header: 'Closing', kind: 'money' }],
          rows: [...(d.rows as EquityRow[]).map(r => row(r)), row(d.total as EquityRow, 'total')] };
      }
      case 'cash-flow': {
        const cf = d as CashFlow;
        return { title, subtitle, columns: [{ header: 'Item' }, { header: 'Amount', kind: 'money' }],
          rows: [...cf.sections.flatMap(s2 => [{ cells: [s2.title, null], style: 'section' as const }, ...s2.lines.map(l => ({ cells: [l.label, num(l.amount)], indent: 1 })), { cells: ['Net cash from ' + s2.key + ' activities', num(s2.total)], style: 'total' as const }]),
            { cells: ['Net change in cash', num(cf.netChange)], style: 'bold' }, { cells: ['Cash at the start', num(cf.openingCash)] }, { cells: ['Cash at the end', num(cf.closingCash)], style: 'total' }] };
      }
      case 'profit-loss-centres': {
        const b = d as ByCentre;
        const rows: ExportDoc['rows'] = [];
        b.rows.forEach((r, i) => {
          if (i === 0 || b.rows[i - 1].section !== r.section) rows.push({ cells: [this.centreSection(r.section)], style: 'section' });
          rows.push({ cells: [r.code + ' ' + r.name, ...r.amounts.map(a => num(a, true)), num(r.total)], indent: 1 });
        });
        for (const x of b.summaries) rows.push({ cells: [x.label, ...x.amounts.map(a => num(a)), num(x.total)], style: this.isResult(x.label) ? 'total' : 'bold' });
        return { title, subtitle, landscape: true, columns: [{ header: 'Account' }, ...b.columns.map(c => ({ header: c.code ? c.code + ' ' + c.name : c.name, kind: 'money' as const })), { header: 'Total', kind: 'money' }], rows };
      }
      default: {
        const cmp = this.hasCompare();
        const cols: ExportDoc['columns'] = [{ header: 'Code' }, { header: 'Account' }, { header: (d.to ?? d.asAt).slice(0, 10), kind: 'money' }];
        if (cmp) cols.push({ header: (d.compareTo ?? d.compareAsAt).slice(0, 10), kind: 'money' });
        const rows: ExportDoc['rows'] = [];
        for (const sec of this.sections()) {
          rows.push({ cells: ['', sec.title], style: 'section' });
          for (const r of sec.rows) rows.push({ cells: [r.code, r.name, num(r.amount, r.hasChildren), ...(cmp ? [num(r.compare ?? 0, r.hasChildren)] : [])], indent: r.level ?? 0 });
          rows.push({ cells: ['', 'Total ' + sec.title.toLowerCase(), num(sec.total), ...(cmp ? [num(sec.compareTotal ?? 0)] : [])], style: 'total' });
          const res = this.result(sec.key); if (res) rows.push({ cells: ['', res.label, num(d[res.field]), ...(cmp ? [num(d[res.compare])] : [])], style: 'bold' });
        }
        if (this.kind() === 'profit-loss') rows.push({ cells: ['', 'Net profit', num(d.netProfit), ...(cmp ? [num(d.compareNetProfit)] : [])], style: 'total' });
        else rows.push({ cells: ['', 'Total liabilities and equity', num(d.totalLiabilitiesAndEquity), ...(cmp ? [num(d.compareTotalLiabilitiesAndEquity)] : [])], style: 'total' });
        return { title, subtitle, columns: cols, rows };
      }
    }
  };
}
