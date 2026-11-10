import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, monthStart, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface VatReturn {
  currency: string; from: string; to: string; taxableSales: number; salesReturns: number; netSales: number; outputVat: number; creditNoteVat: number; netOutputVat: number; purchases: number; inputVat: number; vatPayable: number;
  outputBalance: number; inputBalance: number; settlementDue: number; lines: { label: string; amount: number; note?: string | null }[]; bills: { supplier: string; number: string; date: string; net: number; vat: number }[];
}

/** The VAT return worked out from the books: VAT charged on sales less credit notes, VAT paid on purchases (from bills), and what is payable. Settling it clears both VAT accounts. */
@Component({
  selector: 'app-vat-return',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="VAT return" subtitle="Worked out from your books for the period you choose: VAT charged on sales, VAT paid on purchases, and what is payable to ZIMRA.">
      <button class="btn-ghost" (click)="print()" [disabled]="!ret()"><app-icon name="file-text" size="h-4 w-4" />Print</button>
      <button class="btn-ghost" (click)="exportCsv()" [disabled]="!ret()"><app-icon name="download" size="h-4 w-4" />CSV</button>
    </app-page-header>

    <div class="card mb-5 print:hidden">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label class="block"><span class="label">Currency</span><select class="input" [ngModel]="currency()" (ngModelChange)="currency.set($event)">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
        <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event)" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event)" /></label>
        <div class="flex items-end"><button class="btn w-full" (click)="load()" [disabled]="busy()">{{ busy() ? 'Working…' : 'Show return' }}</button></div>
      </div>
    </div>

    @if (ret(); as r) {
      <div class="grid gap-5 xl:grid-cols-3">
        <section class="card xl:col-span-2">
          <div class="mb-4 border-b border-slate-100 pb-3 dark:border-slate-800"><h2 class="text-lg font-semibold">VAT return</h2><p class="text-sm text-slate-500">{{ r.from | date: 'd MMM y' }} to {{ r.to | date: 'd MMM y' }} · {{ r.currency }}</p></div>
          <table class="w-full text-sm"><tbody>
            @for (l of r.lines; track $index; let last = $last) {
              <tr [class]="last ? 'border-t-2 border-slate-300 dark:border-slate-600' : 'border-b border-slate-100 dark:border-slate-800'" [class.font-semibold]="isTotal(l.label) || last">
                <td class="py-2">{{ l.label }}@if (l.note) { <span class="ml-2 text-xs font-normal text-slate-400">{{ l.note }}</span> }</td>
                <td class="py-2 text-right tabular-nums" [class.text-lg]="last" [class.text-red-600]="last && l.amount > 0" [class.text-emerald-600]="last && l.amount < 0">{{ money(l.amount) }}</td>
              </tr>
            }
          </tbody></table>
          <p class="mt-3 text-xs text-slate-500">{{ r.vatPayable >= 0 ? 'This is what you owe ZIMRA for the period.' : 'This is what ZIMRA owes you for the period.' }} Sales come from your fiscal receipts, purchases from the bills you entered.</p>
        </section>

        <div class="space-y-5">
          <section class="card">
            <h2 class="text-base font-semibold">Held in your books</h2>
            <dl class="mt-3 space-y-2 text-sm">
              <div class="flex justify-between"><dt class="text-slate-500">VAT collected, not yet paid</dt><dd class="tabular-nums">{{ money(r.outputBalance) }}</dd></div>
              <div class="flex justify-between"><dt class="text-slate-500">VAT recoverable</dt><dd class="tabular-nums">{{ money(r.inputBalance) }}</dd></div>
              <div class="flex justify-between border-t border-slate-100 pt-2 font-semibold dark:border-slate-800"><dt>{{ r.settlementDue >= 0 ? 'To pay ZIMRA' : 'To get back' }}</dt><dd class="tabular-nums">{{ money(abs(r.settlementDue)) }}</dd></div>
            </dl>
            @if (auth.can('accounting.manage')) {
              <button class="btn mt-4 w-full" (click)="open.set(true)" [disabled]="r.outputBalance === 0 && r.inputBalance === 0">{{ r.settlementDue >= 0 ? 'Record VAT payment' : 'Record VAT refund' }}</button>
              <p class="hint">Posts one entry that clears both VAT accounts against the account you paid from (or received into).</p>
            }
          </section>
          <section class="card">
            <h2 class="text-base font-semibold">Bills in this return</h2>
            <ul class="mt-2 divide-y divide-slate-100 text-sm dark:divide-slate-800">
              @for (b of r.bills; track $index) { <li class="flex items-center justify-between gap-3 py-2"><span class="min-w-0 truncate">{{ b.supplier }} <span class="font-mono text-xs text-slate-500">{{ b.number }}</span></span><span class="tabular-nums">{{ money(b.vat) }}</span></li> }
              @empty { <li class="py-2 text-slate-500">No bills in this period.</li> }
            </ul>
          </section>
        </div>
      </div>
    }

    @if (open()) {
      <app-modal [title]="(ret()?.settlementDue ?? 0) >= 0 ? 'Record VAT payment' : 'Record VAT refund'" [subtitle]="'Clears all VAT held up to the date, in ' + currency()" width="max-w-md" (closed)="open.set(false)">
        <form (ngSubmit)="settle()">
          <div class="grid gap-4">
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="date" required [max]="today" /></label>
            <label class="block"><span class="label">{{ (ret()?.settlementDue ?? 0) >= 0 ? 'Paid from' : 'Received into' }}</span><select class="input" name="a" [(ngModel)]="account"><option value="">Choose…</option>@for (a of store.meta()?.cashAccounts ?? []; track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="open.set(false)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !account">Post</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class VatReturnComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly currency = signal('USD');
  readonly from = signal(monthStart());
  readonly to = signal(isoDate(new Date()));
  readonly ret = signal<VatReturn | null>(null);
  readonly busy = signal(false);
  readonly open = signal(false);
  readonly error = signal('');
  readonly today = isoDate(new Date());
  date = isoDate(new Date()); account = '';

  abs(v: number): number { return Math.abs(v); }
  isTotal(label: string): boolean { return /^(Net |VAT payable|VAT refundable)/.test(label); }

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); this.currency.set(this.store.meta()?.defaultCurrency ?? 'USD'); const d = new Date(); this.from.set(isoDate(new Date(d.getFullYear(), d.getMonth() - 1, 1))); this.to.set(isoDate(new Date(d.getFullYear(), d.getMonth(), 0))); } catch (e) { this.toast.err(e); }
    await this.load();
  }

  async load(): Promise<void> {
    this.busy.set(true);
    try { this.ret.set(await this.api.get<VatReturn>('/api/v1/accounting/vat/return', { currency: this.currency(), from: this.from(), to: this.to() })); } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async settle(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try { await this.api.post('/api/v1/accounting/vat/settle', { date: this.date, currency: this.currency(), accountId: this.account }); this.open.set(false); this.toast.ok('VAT settled.'); await this.load(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  print(): void { window.print(); }

  exportCsv(): void {
    const r = this.ret(); if (!r) return;
    const esc = (v: unknown): string => { const s = v == null ? '' : String(v); return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
    const rows: unknown[][] = [['VAT return', r.currency, r.from.slice(0, 10), r.to.slice(0, 10)], ...r.lines.map(l => [l.label, l.amount]), [], ['Supplier', 'Bill', 'Date', 'Before VAT', 'VAT'], ...r.bills.map(b => [b.supplier, b.number, b.date.slice(0, 10), b.net, b.vat])];
    const url = URL.createObjectURL(new Blob(['﻿' + rows.map(x => x.map(esc).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a'); a.href = url; a.download = 'vat-return-' + r.from.slice(0, 7) + '.csv'; document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }
}
