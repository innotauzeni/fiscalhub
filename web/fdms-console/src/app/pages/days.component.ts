import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DeviceStore } from '../core/device.store';
import { FiscalDay, Paged, Receipt } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { DeviceSelectComponent } from '../shared/device-select.component';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';
import { ReceiptCorrectDialogComponent, VALIDATION_HELP } from './receipt-correct-dialog.component';

/** What a failed close means, and what to do about it. */
const CLOSE_HELP: Record<string, string> = {
  ReceiptsWithValidationErrors: 'ZIMRA will not close a day that holds a receipt it flagged with a red validation error. Correct the receipts listed below, then close again.',
  MissingReceipts: 'ZIMRA has not received every receipt of this day yet. Wait a minute (queued receipts are delivered automatically) and close again.',
  CountersMismatch: 'The totals sent with the close do not match what ZIMRA calculated from its receipts. Close again; if it keeps failing, check the receipts of this day.',
  BadCertificateSignature: 'ZIMRA rejected the signature on the close request. The device certificate may have expired: renew it on the Devices page, then close again.',
};

@Component({
  selector: 'app-days',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DecimalPipe, IconComponent, DeviceSelectComponent, PageHeaderComponent, StatusComponent, ReceiptCorrectDialogComponent],
  template: `
    <app-page-header title="Fiscal days" subtitle="Open and close the fiscal day (Z report). Status is read from ZIMRA.">
      <app-device-select (changed)="load()" />
    </app-page-header>

    <div class="card">
      @if (day(); as d) {
        <div class="flex flex-wrap items-center gap-3">
          <app-status [value]="d.status" />
          @if (d.fiscalDayNo) { <span class="text-sm">Day <strong>{{ d.fiscalDayNo }}</strong> · opened {{ d.openedAt | date:'medium' }} CAT · {{ d.lastReceiptCounter }} receipts</span> }
          @else { <span class="text-sm text-slate-500">No fiscal day has been opened on this device yet.</span> }
          <span class="flex-1"></span>
          <button class="btn-ghost" (click)="load()" [disabled]="busy()">Refresh</button>
          @if (auth.can('day.manage')) {
            @if (d.status === 'FiscalDayClosed') { <button class="btn" (click)="open()" [disabled]="busy()">Open fiscal day</button> }
            @else { <button class="btn-danger" (click)="close()" [disabled]="busy()">Close fiscal day</button> }
          }
        </div>

        @if (d.closingErrorCode) {
          <div class="mt-3 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800 dark:border-red-900/50 dark:bg-red-900/25 dark:text-red-200">
            <div class="font-medium">ZIMRA could not close the day: {{ d.closingErrorCode }}</div>
            <div class="mt-0.5">{{ closeHelp(d.closingErrorCode) }}</div>
          </div>
        }

        @if (problems().length) {
          <div class="mt-5 rounded-xl border border-amber-200 bg-amber-50/60 p-4 dark:border-amber-900/50 dark:bg-amber-900/10">
            <div class="mb-1 flex items-center gap-2 font-medium text-amber-900 dark:text-amber-200"><app-icon name="alert" size="h-4 w-4" />Receipts ZIMRA flagged in this day ({{ problems().length }})</div>
            <p class="mb-3 text-sm text-amber-900/80 dark:text-amber-200/80">A fiscalised receipt cannot be edited. Correct each one with a credit note and a corrected invoice.</p>
            <div class="overflow-x-auto">
              <table class="table">
                <thead><tr><th>Invoice</th><th class="text-right">Total</th><th>What ZIMRA said</th><th></th></tr></thead>
                <tbody>
                  @for (r of problems(); track r.id) {
                    <tr>
                      <td class="font-medium">{{ r.invoiceNo }}<div class="hint mt-0">#{{ r.receiptGlobalNo }}</div></td>
                      <td class="text-right tabular-nums">{{ r.total | number:'1.2-2' }} {{ r.currency }}</td>
                      <td>@for (v of r.validationErrors; track v.code) { <div class="text-sm"><span class="badge" [class.badge-bad]="v.color === 'Red'" [class.badge-warn]="v.color !== 'Red'">{{ v.code }} · {{ v.color }}</span> <span class="text-slate-600 dark:text-slate-300">{{ help(v.code) }}</span></div> }</td>
                      <td class="whitespace-nowrap text-right">@if (auth.can('receipts.submit') && r.type === 'FiscalInvoice') { <button class="btn btn-sm" (click)="correcting.set(r)">Correct…</button> }</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </div>
        }

        @if (d.counters.length) {
          <div class="card-title mt-6">Fiscal counters (what the Z report will contain)</div>
          <div class="overflow-x-auto">
            <table class="table">
              <thead><tr><th>Counter</th><th>Currency</th><th>Tax</th><th class="text-right">Value</th></tr></thead>
              <tbody>@for (c of d.counters; track $index) { <tr><td>{{ c.type }}</td><td>{{ c.currency }}</td><td>{{ c.moneyType ?? (c.taxPercent === undefined ? '' : c.taxPercent + '%') }}</td><td class="text-right tabular-nums">{{ c.value | number:'1.2-2' }}</td></tr> }</tbody>
            </table>
          </div>
        }
      } @else { <p class="text-sm text-slate-500">{{ busy() ? 'Asking ZIMRA…' : 'Choose a device.' }}</p> }
    </div>

    @if (correcting(); as r) {
      <app-receipt-correct-dialog [receiptId]="r.id" [invoiceNo]="r.invoiceNo" (closed)="correcting.set(null)" (done)="corrected()" />
    }
  `,
})
export class DaysComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly store = inject(DeviceStore);
  readonly auth = inject(AuthService);
  readonly day = signal<FiscalDay | null>(null);
  readonly busy = signal(false);
  readonly flagged = signal<Receipt[]>([]);
  readonly correcting = signal<Receipt | null>(null);

  /** Invoices of the day that ZIMRA flagged. */
  readonly problems = computed(() => this.flagged().filter(r => r.type === 'FiscalInvoice'));

  closeHelp(code: string): string { return CLOSE_HELP[code] ?? 'Fix the cause and close again.'; }
  help(code: string): string { return VALIDATION_HELP[code] ?? 'ZIMRA flagged this receipt. Look the code up in the FDMS error list.'; }

  async ngOnInit(): Promise<void> { try { await this.store.load(); } catch (e) { this.toast.err(e); } await this.load(); }

  private async call(path: string, method: 'get' | 'post', done?: string): Promise<void> {
    const d = this.store.selected(); if (!d) return;
    this.busy.set(true);
    try {
      const r = method === 'get' ? await this.api.get<FiscalDay>(`/api/v1/devices/${d.id}/${path}`) : await this.api.post<FiscalDay>(`/api/v1/devices/${d.id}/${path}`);
      this.day.set(r); if (done) this.toast.ok(done);
      await this.loadFlagged(d.id, r.fiscalDayNo);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  private async loadFlagged(deviceId: string, dayNo: number): Promise<void> {
    if (!dayNo || !this.auth.can('receipts.view')) { this.flagged.set([]); return; }
    try { this.flagged.set((await this.api.get<Paged<Receipt>>('/api/v1/receipts', { deviceId, fiscalDayNo: dayNo, hasErrors: true, pageSize: 100 })).items); } catch { this.flagged.set([]); }
  }

  load(): Promise<void> { return this.call('day', 'get'); }
  open(): Promise<void> { return this.call('day/open', 'post', 'Fiscal day opened.'); }

  async corrected(): Promise<void> { this.correcting.set(null); await this.load(); }

  async close(): Promise<void> {
    if (this.problems().length && !(await this.dialog.confirm(`${this.problems().length} receipt(s) in this day are flagged by ZIMRA and will probably stop the close. Try anyway?`, 'Try to close', true))) return;
    if (!(await this.dialog.confirm('Close the fiscal day? This sends the Z report to ZIMRA and cannot be undone.', 'Close day', true))) return;
    await this.call('day/close', 'post', 'Close requested - see the status above.');
  }
}
