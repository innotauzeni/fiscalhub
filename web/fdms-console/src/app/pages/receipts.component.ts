import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DeviceStore } from '../core/device.store';
import { Paged, Receipt, doneOrHeld } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { DeviceSelectComponent } from '../shared/device-select.component';
import { PageHeaderComponent, StatusComponent } from '../shared/ui.components';
import { ReceiptCorrectDialogComponent } from './receipt-correct-dialog.component';
import { ReceiptViewDialogComponent } from './receipt-view-dialog.component';

@Component({
  selector: 'app-receipts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, DatePipe, DecimalPipe, DeviceSelectComponent, PageHeaderComponent, StatusComponent, ReceiptCorrectDialogComponent, ReceiptViewDialogComponent],
  template: `
    <app-page-header title="Receipts" subtitle="Everything issued through the gateway.">
      @if (auth.can('receipts.submit')) { <a class="btn" routerLink="/receipts/new">New invoice</a> }
    </app-page-header>

    <div class="card mb-5">
      <div class="flex flex-wrap items-end gap-3">
        <app-device-select (changed)="reload()" />
        <label class="block"><span class="label">Status</span>
          <select class="input" [(ngModel)]="status" (change)="reload()">
            <option value="">Any</option>
            @for (s of statuses; track s) { <option [value]="s">{{ s }}</option> }
          </select></label>
        <label class="block min-w-48 flex-1"><span class="label">Search invoice no. / reference</span><input class="input" [(ngModel)]="search" (keydown.enter)="reload()" /></label>
        <label class="block"><span class="label">From</span><input type="date" class="input" [(ngModel)]="from" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [(ngModel)]="to" /></label>
        <button class="btn" (click)="reload()">Search</button>
      </div>
    </div>

    <div class="card overflow-x-auto">
      <table class="table">
        <thead><tr><th>Date</th><th>Invoice</th><th>Type</th><th class="text-right">Total</th><th>Status</th><th>ZIMRA no.</th><th></th></tr></thead>
        <tbody>
          @for (r of page()?.items ?? []; track r.id) {
            <tr>
              <td class="whitespace-nowrap">{{ r.receiptDate | date:'short' }} <span class="hint mt-0">CAT</span></td>
              <td><div class="font-medium">{{ r.invoiceNo }}</div><div class="hint mt-0">{{ r.externalRef }}</div></td>
              <td>{{ r.type }}</td>
              <td class="text-right tabular-nums">{{ r.total | number:'1.2-2' }} {{ r.currency }}</td>
              <td>
                <app-status [value]="r.status" />
                @for (v of r.validationErrors; track v.code) { <div class="hint" [class.text-red-600]="v.color === 'Red'">{{ v.code }} ({{ v.color }})</div> }
                @if (r.errorMessage && r.status !== 'Submitted') { <div class="hint max-w-xs">{{ r.errorMessage }}</div> }
                @if (r.status === 'Held') { <div class="hint">sends {{ r.holdUntil | date:'shortTime' }}</div> }
              </td>
              <td class="whitespace-nowrap">@if (r.receiptGlobalNo) { #{{ r.receiptGlobalNo }} <span class="hint">day {{ r.fiscalDayNo }} / {{ r.receiptCounter }}</span> }</td>
              <td class="whitespace-nowrap text-right">
                <button class="btn-ghost btn-sm mr-1" (click)="viewing.set(r)" title="See the QR code and check this invoice with ZIMRA">View</button>
                @if (r.status === 'Held' && auth.can('receipts.submit')) {
                  <button class="btn-ghost btn-sm mr-1" (click)="release(r)">Send now</button>
                  <button class="btn-danger btn-sm" (click)="cancel(r)">Cancel</button>
                } @else if (r.status === 'Pending' && auth.can('receipts.submit')) {
                  <button class="btn-ghost btn-sm" (click)="retry(r)">Retry</button>
                } @else if (r.receiptGlobalNo) {
                  <button class="btn-ghost btn-sm mr-1" (click)="open(r, 'pdf', 'receipt')">Receipt PDF</button>
                  <button class="btn-ghost btn-sm mr-1" (click)="open(r, 'pdf', 'a4')">A4</button>
                  @if (r.status === 'Submitted' && r.type === 'FiscalInvoice' && auth.can('receipts.submit')) { <button class="btn-ghost btn-sm" (click)="correcting.set(r)" title="Reverse with a credit note and re-issue a corrected invoice">Correct…</button> }
                }
              </td>
            </tr>
          } @empty { <tr><td colspan="7" class="text-slate-500">{{ loading() ? 'Loading…' : 'No receipts match.' }}</td></tr> }
        </tbody>
      </table>

      @if (viewing(); as v) { <app-receipt-view-dialog [receipt]="v" (closed)="viewing.set(null)" (changed)="viewing.set(null); reload()" /> }

      @if (correcting(); as c) { <app-receipt-correct-dialog [receiptId]="c.id" [invoiceNo]="c.invoiceNo" (closed)="correcting.set(null)" (done)="correcting.set(null); reload()" /> }

      @if (page(); as p) {
        <div class="mt-3 flex items-center justify-between text-sm text-slate-500">
          <span>Page {{ p.page }} of {{ pages() }} · {{ p.total }} receipts</span>
          <span class="flex gap-2"><button class="btn-ghost btn-sm" [disabled]="p.page <= 1" (click)="go(p.page - 1)">‹ Previous</button><button class="btn-ghost btn-sm" [disabled]="p.page >= pages()" (click)="go(p.page + 1)">Next ›</button></span>
        </div>
      }
    </div>
  `,
})
export class ReceiptsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly store = inject(DeviceStore);
  readonly auth = inject(AuthService);

  readonly statuses = ['Submitted', 'Pending', 'Held', 'OfflineIssued', 'Rejected', 'Cancelled'];
  status = ''; search = ''; from = ''; to = '';
  readonly page = signal<Paged<Receipt> | null>(null);
  readonly loading = signal(false);
  readonly correcting = signal<Receipt | null>(null);
  readonly viewing = signal<Receipt | null>(null);

  pages(): number { const p = this.page(); return p ? Math.max(1, Math.ceil(p.total / p.pageSize)) : 1; }

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); } catch (e) { this.toast.err(e); }
    await this.go(1);
  }

  reload(): void { void this.go(1); }

  async go(page: number): Promise<void> {
    this.loading.set(true);
    try {
      this.page.set(await this.api.get<Paged<Receipt>>('/api/v1/receipts', {
        deviceId: this.store.selected()?.id, status: this.status, search: this.search, from: this.from, to: this.to, page, pageSize: 25,
      }));
    } catch (e) { this.toast.err(e); }
    finally { this.loading.set(false); }
  }

  async open(r: Receipt, kind: 'pdf' | 'qr', format?: string): Promise<void> {
    try { await this.api.open(`/api/v1/receipts/${r.id}/${kind}`, format ? { format } : undefined); } catch (e) { this.toast.err(e); }
  }

  private async act(r: Receipt, verb: string, body?: unknown, message?: string): Promise<void> {
    try { const res = await this.api.post(`/api/v1/receipts/${r.id}/${verb}`, body); if (message) this.toast.ok(doneOrHeld(res, message)); await this.go(this.page()?.page ?? 1); }
    catch (e) { this.toast.err(e); }
  }

  release(r: Receipt): Promise<void> { return this.act(r, 'release', undefined, 'Sent to ZIMRA.'); }
  retry(r: Receipt): Promise<void> { return this.act(r, 'retry', undefined, 'Delivery attempted.'); }

  async cancel(r: Receipt): Promise<void> {
    const reason = await this.dialog.prompt(`Cancel ${r.invoiceNo}? Nothing was sent to ZIMRA, so no credit note is needed.`, 'Reason (optional)', 'Cancel receipt');
    if (reason !== null) await this.act(r, 'cancel', { reason }, 'Receipt cancelled.');
  }
}
