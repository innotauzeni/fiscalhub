import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Api, errorMessage } from '../core/api.service';
import { PublicInvoice } from '../core/models';

/**
 * What a customer sees when they open the link a company sent them: the invoice, its ZIMRA QR code, a download button and a button that opens ZIMRA's own check.
 * No sign-in. The link itself (signed, time-limited, for this one invoice) is the permission.
 */
@Component({
  selector: 'app-public-invoice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DecimalPipe],
  template: `
    <div class="min-h-screen bg-slate-50 px-4 py-8 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      <div class="mx-auto max-w-md">
        @if (inv(); as i) {
          <div class="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
            <div class="h-1.5" [style.background]="i.accent"></div>
            <div class="p-6">
              <div class="flex items-center gap-3">
                @if (i.hasLogo) { <img [src]="base() + '/logo'" alt="" class="max-h-12 max-w-32 object-contain" (error)="logoFailed()" /> }
                <div class="min-w-0"><div class="truncate text-lg font-semibold">{{ i.companyName }}</div><div class="text-xs text-slate-500">{{ i.type === 'FiscalInvoice' ? 'Tax invoice' : i.type === 'CreditNote' ? 'Credit note' : 'Debit note' }}</div></div>
              </div>

              <div class="mt-6 text-center">
                <div class="text-sm text-slate-500">Invoice {{ i.invoiceNo }}</div>
                <div class="mt-1 text-4xl font-bold tabular-nums">{{ i.total | number:'1.2-2' }} <span class="text-xl font-medium text-slate-500">{{ i.currency }}</span></div>
                <div class="mt-1 text-sm text-slate-500">{{ i.date | date:'d MMMM y, HH:mm' }}</div>
              </div>

              <div class="mt-5 rounded-xl px-4 py-3 text-center text-sm font-medium" [class]="tone(i.status)">{{ i.statusText }}</div>

              @if (i.qrUrl) {
                <div class="mt-5 flex flex-col items-center">
                  <div class="rounded-xl border border-slate-200 bg-white p-3"><img [src]="base() + '/qr'" alt="ZIMRA verification QR code" class="h-44 w-44" style="image-rendering: pixelated" /></div>
                  @if (i.globalNo) { <div class="mt-2 text-xs text-slate-500">ZIMRA receipt no. {{ i.globalNo }}{{ i.fiscalDayNo ? ', fiscal day ' + i.fiscalDayNo : '' }}</div> }
                </div>
              }

              <div class="mt-6 grid gap-2">
                @if (i.globalNo) { <a class="rounded-xl px-4 py-3 text-center font-semibold text-white" [style.background]="i.accent" [href]="base() + '/pdf?format=a4'" download>Download the invoice (PDF)</a> }
                @if (i.qrUrl) { <a class="rounded-xl border border-slate-300 px-4 py-3 text-center font-medium dark:border-slate-600" [href]="i.qrUrl" target="_blank" rel="noopener noreferrer">Check it with ZIMRA</a> }
              </div>
              <p class="mt-5 text-center text-xs text-slate-400">Fiscalised through the FDMS Gateway. Keep this link: it shows your invoice any time.</p>
            </div>
          </div>
        } @else if (error()) {
          <div class="rounded-2xl border border-slate-200 bg-white p-8 text-center dark:border-slate-800 dark:bg-slate-900">
            <div class="text-lg font-semibold">This invoice link cannot be opened</div>
            <p class="mt-2 text-sm text-slate-600 dark:text-slate-400">{{ error() }}</p>
          </div>
        } @else { <p class="py-20 text-center text-slate-500">Loading…</p> }
      </div>
    </div>
  `,
})
export class PublicInvoiceComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  readonly inv = signal<PublicInvoice | null>(null);
  readonly error = signal('');
  private token = '';

  base(): string { return `/api/v1/public/invoices/${this.token}`; }
  logoFailed(): void { /* the company name still shows */ }

  tone(status: string): string {
    return status === 'Submitted' ? 'bg-emerald-50 text-emerald-800 dark:bg-emerald-900/25 dark:text-emerald-200'
      : status === 'Rejected' || status === 'Cancelled' ? 'bg-red-50 text-red-800 dark:bg-red-900/25 dark:text-red-200' : 'bg-amber-50 text-amber-900 dark:bg-amber-900/25 dark:text-amber-200';
  }

  async ngOnInit(): Promise<void> {
    this.token = this.route.snapshot.paramMap.get('token') ?? '';
    try { this.inv.set(await this.api.get<PublicInvoice>(this.base())); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '') || 'The link is not valid.'); }
  }
}
