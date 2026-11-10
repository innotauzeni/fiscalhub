import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { isoDate } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { CurrencySelectComponent } from '../../shared/currency-select.component';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Rate { id: string; currency: string; date: string; rate: number }
interface Rates { baseCurrency: string; currencies: string[]; rates: Rate[] }

/** The base currency and what each other currency was worth in it over time, so the reports can show all currencies together. */
@Component({
  selector: 'app-exchange-rates',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, RouterLink, IconComponent, PageHeaderComponent, CurrencySelectComponent],
  template: `
    <app-page-header title="Exchange rates" subtitle="Your books keep each currency in its own ledger. With rates, the trial balance, profit and loss and balance sheet can show them all together." />

    @if (data(); as d) {
      <div class="grid gap-5 lg:grid-cols-3">
        <div class="card">
          <h2 class="card-title mb-3">Base currency</h2>
          <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">The currency the combined reports are shown in. Rates below say what one unit of another currency is worth in it.</p>
          <div class="flex gap-2"><app-currency-select class="flex-1" [value]="base()" (valueChange)="base.set($event)" /><button class="btn-ghost" (click)="setBase()" [disabled]="base() === d.baseCurrency || !auth.can('accounting.manage')">Change</button></div>
          <p class="mt-4 text-sm"><a routerLink="/accounting/reports/balance-sheet" [queryParams]="{ currency: 'ALL' }" class="text-brand-600 hover:underline">Balance sheet, all currencies in {{ d.baseCurrency }}</a></p>
        </div>

        <div class="card lg:col-span-2">
          <h2 class="card-title mb-3">Rates</h2>
          @if (auth.can('accounting.manage')) {
            <form class="mb-4 grid gap-3 sm:grid-cols-4" (ngSubmit)="add()">
              <label class="block"><span class="label">Currency</span><app-currency-select [value]="cur()" (valueChange)="cur.set($event)" /></label>
              <label class="block"><span class="label">From</span><input type="date" class="input" name="d" [(ngModel)]="date" required /></label>
              <label class="block"><span class="label">1 {{ cur() }} = ? {{ d.baseCurrency }}</span><input type="number" step="any" min="0" class="input text-right" name="r" [(ngModel)]="rate" required /></label>
              <div class="flex items-end"><button class="btn w-full" type="submit" [disabled]="!cur() || !rate || cur() === d.baseCurrency"><app-icon name="plus" size="h-4 w-4" />Add rate</button></div>
            </form>
            @if (rate) { <p class="-mt-2 mb-4 text-xs text-slate-500">That is {{ inverse() }} {{ cur() }} per 1 {{ d.baseCurrency }}.</p> }
          }
          @if (missing().length) { <p class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-100">Your books also use {{ missing().join(', ') }}, which have no rate yet. Add one before combining.</p> }
          <div class="overflow-x-auto"><table class="table w-full text-sm">
            <thead><tr><th>Currency</th><th>From</th><th class="text-right">1 unit in {{ d.baseCurrency }}</th><th class="text-right">Per 1 {{ d.baseCurrency }}</th><th></th></tr></thead>
            <tbody>
              @for (r of d.rates; track r.id) {
                <tr><td class="font-medium">{{ r.currency }}</td><td>{{ r.date | date: 'd MMM y' }}</td><td class="text-right tabular-nums">{{ r.rate }}</td><td class="text-right tabular-nums text-slate-500">{{ per(r.rate) }}</td>
                  <td class="text-right">@if (auth.can('accounting.manage')) { <button class="btn-ghost btn-sm" (click)="remove(r)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button> }</td></tr>
              } @empty { <tr><td colspan="5" class="text-center text-slate-500">No rates yet.</td></tr> }
            </tbody>
          </table></div>
          <p class="mt-3 text-xs text-slate-500">Combined reports translate assets and liabilities at the rate in force on the report date, and income, expenses and equity at the rate on each transaction's date. The difference appears in equity as the foreign currency translation reserve.</p>
        </div>
      </div>
    }
  `,
})
export class ExchangeRatesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly auth = inject(AuthService);
  readonly data = signal<Rates | null>(null);
  readonly base = signal('USD');
  readonly cur = signal('ZWG');
  date = isoDate(new Date()); rate: number | null = null;
  readonly missing = computed(() => { const d = this.data(); return d ? d.currencies.filter(c => !d.rates.some(r => r.currency === c)) : []; });

  async ngOnInit(): Promise<void> { await this.load(); }
  private set(d: Rates): void { this.data.set(d); this.base.set(d.baseCurrency); }
  async load(): Promise<void> { try { this.set(await this.api.get<Rates>('/api/v1/accounting/rates')); } catch (e) { this.toast.err(e); } }
  per(rate: number): string { return rate ? (Math.round((1 / rate) * 10000) / 10000).toString() : ''; }
  inverse(): string { return this.per(Number(this.rate)); }

  async add(): Promise<void> {
    try { this.set(await this.api.put<Rates>('/api/v1/accounting/rates', { currency: this.cur(), date: this.date, rate: Number(this.rate) })); this.rate = null; this.toast.ok('Rate saved.'); } catch (e) { this.toast.err(e); }
  }
  async remove(r: Rate): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the ${r.currency} rate from ${r.date.slice(0, 10)}?`, 'Delete', true))) return;
    try { this.set(await this.api.delete<Rates>('/api/v1/accounting/rates/' + r.id)); } catch (e) { this.toast.err(e); }
  }
  async setBase(): Promise<void> {
    try { this.set(await this.api.put<Rates>('/api/v1/accounting/rates/base', { baseCurrency: this.base() })); this.toast.ok('Base currency changed.'); } catch (e) { this.toast.err(e); }
  }
}
