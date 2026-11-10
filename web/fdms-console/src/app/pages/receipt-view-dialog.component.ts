import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { Receipt, SendResult, ShareLink } from '../core/models';
import { explain } from '../core/error-help';
import { ToastService } from '../core/ui.services';
import { ModalComponent } from '../shared/modal.components';
import { IconComponent, StatusComponent } from '../shared/ui.components';

interface Explain { tone: 'ok' | 'warn' | 'bad' | 'info'; title: string; text: string; }

/**
 * Look at an invoice the way the customer will: its QR code and numbers, whether ZIMRA already has it, and a button that opens ZIMRA's own verification page.
 * If that page says it cannot find the invoice, the status here says why (scheduled, queued, issued offline) and what to do.
 */
@Component({
  selector: 'app-receipt-view-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DecimalPipe, FormsModule, RouterLink, ModalComponent, IconComponent, StatusComponent],
  template: `
    <app-modal [title]="'Invoice ' + receipt().invoiceNo" subtitle="See the QR code and check it at ZIMRA" width="max-w-3xl" (closed)="closed.emit()">
      <div class="grid gap-6 md:grid-cols-5">
        <div class="md:col-span-2">
          <div class="flex aspect-square items-center justify-center rounded-2xl border border-slate-200 bg-white p-3 dark:border-slate-700">
            @if (qr(); as u) { <img [src]="u" alt="ZIMRA verification QR code" class="h-full w-full object-contain" style="image-rendering: pixelated" /> }
            @else { <span class="px-4 text-center text-sm text-slate-500">{{ receipt().receiptGlobalNo ? (qrFailed() ? 'The QR code is not available for this receipt.' : 'Loading the QR code…') : 'No QR code yet: this invoice has not been numbered.' }}</span> }
          </div>
          @if (receipt().qrUrl) {
            <a class="btn mt-3 w-full justify-center" [href]="receipt().qrUrl" target="_blank" rel="noopener noreferrer"><app-icon name="shield" size="h-4 w-4" />Verify on ZIMRA</a>
            <button type="button" class="btn-ghost mt-2 w-full justify-center" (click)="copy()"><app-icon name="copy" size="h-4 w-4" />Copy verification link</button>
          }
        </div>

        <div class="md:col-span-3">
          <dl class="grid grid-cols-[auto_1fr] gap-x-5 gap-y-2 text-sm">
            <dt class="text-slate-500">Status</dt><dd><app-status [value]="receipt().status" /></dd>
            <dt class="text-slate-500">Type</dt><dd>{{ receipt().type }}</dd>
            <dt class="text-slate-500">Total</dt><dd class="font-medium tabular-nums">{{ receipt().total | number:'1.2-2' }} {{ receipt().currency }}</dd>
            <dt class="text-slate-500">Date</dt><dd>{{ receipt().receiptDate | date:'medium' }} <span class="text-xs text-slate-500">Zimbabwe time</span></dd>
            @if (receipt().receiptGlobalNo) { <dt class="text-slate-500">ZIMRA number</dt><dd>#{{ receipt().receiptGlobalNo }} <span class="text-xs text-slate-500">fiscal day {{ receipt().fiscalDayNo }}, receipt {{ receipt().receiptCounter }}</span></dd> }
            @if (receipt().fdmsReceiptId) { <dt class="text-slate-500">ZIMRA receipt id</dt><dd class="font-mono text-xs">{{ receipt().fdmsReceiptId }}</dd> }
            @if (receipt().serverDate) { <dt class="text-slate-500">Accepted by ZIMRA</dt><dd>{{ receipt().serverDate | date:'medium' }}</dd> }
            @if (receipt().holdUntil && receipt().status === 'Held') { <dt class="text-slate-500">Scheduled for</dt><dd>{{ receipt().holdUntil | date:'medium' }}</dd> }
          </dl>

          <div class="mt-4 rounded-xl border p-3 text-sm" [class]="box()">
            <div class="font-semibold">{{ explain().title }}</div>
            <p class="mt-1">{{ explain().text }}</p>
            @if (receipt().errorMessage && receipt().status !== 'Submitted') { <p class="mt-1 text-xs opacity-80">{{ receipt().errorMessage }}</p> }
            @for (h of helps(); track h.code) {
              <div class="mt-2 border-t border-current/20 pt-2"><div class="text-xs font-semibold">{{ h.code }}: {{ h.what }}</div><div class="text-xs">{{ h.todo }}</div></div>
            }
          </div>
          @if (receipt().status === 'Rejected' && auth.can('receipts.submit')) {
            <button class="btn mt-3" (click)="reissue()"><app-icon name="edit" size="h-4 w-4" />Fix it: open a corrected copy</button>
          }

          <div class="mt-4 flex flex-wrap gap-2">
            @if (receipt().status === 'Pending' && auth.can('receipts.submit')) { <button class="btn" (click)="act('retry', 'Delivery attempted.')" [disabled]="busy()">Send to ZIMRA now</button> }
            @if (receipt().status === 'Held' && auth.can('receipts.submit')) { <button class="btn" (click)="act('release', 'Sent to ZIMRA.')" [disabled]="busy()">Send now</button> }
            @if (receipt().status === 'OfflineIssued' && auth.can('day.view')) { <a class="btn" routerLink="/offline" (click)="closed.emit()">Offline files</a> }
            @if (receipt().receiptGlobalNo) {
              <button class="btn-ghost" (click)="pdf('a4')">Invoice PDF (A4)</button>
              <button class="btn-ghost" (click)="pdf('receipt')">Receipt PDF</button>
            }
          </div>
        </div>
      </div>

      @if (canSend()) {
        <section class="mt-6 rounded-xl border border-slate-200 p-4 dark:border-slate-700" aria-label="Send to the customer">
          <div class="mb-3 flex items-center gap-2 text-sm font-semibold"><app-icon name="send" size="h-4 w-4" />Send it to the customer</div>
          <div class="grid gap-3 sm:grid-cols-2">
            <label class="block"><span class="label">Email</span><input class="input" type="email" [(ngModel)]="email" name="sendEmail" placeholder="customer@company.co.zw" /></label>
            <label class="block"><span class="label">Phone (SMS / WhatsApp)</span><input class="input" type="tel" [(ngModel)]="phone" name="sendPhone" placeholder="0771234567" /></label>
          </div>
          <div class="mt-3 flex flex-wrap gap-2">
            <button type="button" class="btn" (click)="sendEmail()" [disabled]="sending()"><app-icon name="mail" size="h-4 w-4" />{{ sending() === 'email' ? 'Sending… (can take a minute)' : 'Email the invoice' }}</button>
            <button type="button" class="btn-ghost" (click)="sendSms()" [disabled]="sending()"><app-icon name="send" size="h-4 w-4" />{{ sending() === 'sms' ? 'Sending…' : 'SMS' }}</button>
            <button type="button" class="btn-ghost" (click)="whatsApp()" [disabled]="sending()"><app-icon name="message" size="h-4 w-4" />WhatsApp</button>
            <button type="button" class="btn-ghost" (click)="copyLink()" [disabled]="sending()"><app-icon name="link" size="h-4 w-4" />Copy customer link</button>
          </div>
          @if (sendNote()) { <p class="mt-2 text-sm" [class]="sendOk() ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-700 dark:text-amber-300'">{{ sendNote() }}</p> }
          <p class="hint">The link opens a page with the invoice, its QR code and a PDF download. It needs no sign-in and works for 90 days.</p>
        </section>
      }

      <div class="mt-6 rounded-xl bg-slate-50 p-4 text-sm dark:bg-slate-950">
        <div class="mb-1 font-medium">How to check it with ZIMRA</div>
        <ol class="list-decimal space-y-0.5 pl-5 text-slate-600 dark:text-slate-300">
          <li>Press <strong>Verify on ZIMRA</strong> (or scan the QR code with a phone). ZIMRA's own page opens.</li>
          <li>If it shows this invoice with the same total, ZIMRA has it: it is submitted.</li>
          <li>If it says it cannot find the invoice, ZIMRA does not have it yet. The status above says why and what to do.</li>
        </ol>
      </div>
      <div class="mt-5 flex justify-end border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" (click)="closed.emit()">Close</button></div>
    </app-modal>
  `,
})
export class ReceiptViewDialogComponent implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  readonly auth = inject(AuthService);

  readonly receipt = input.required<Receipt>();
  readonly closed = output<void>();
  readonly changed = output<void>();

  readonly qr = signal<string | null>(null);
  readonly qrFailed = signal(false);
  readonly busy = signal(false);
  readonly sending = signal<'email' | 'sms' | 'link' | null>(null);
  readonly sendNote = signal('');
  readonly sendOk = signal(true);
  email = ''; phone = '';

  /** Only an invoice that has a ZIMRA number can be sent: a scheduled or cancelled one has nothing to show yet. */
  canSend(): boolean { const r = this.receipt(); return !!r.receiptGlobalNo && r.status !== 'Rejected' && r.status !== 'Cancelled' && this.auth.can('receipts.view'); }

  /** Plain-words help for every ZIMRA code attached to this receipt. */
  helps(): { code: string; what: string; todo: string }[] {
    const r = this.receipt(); const codes = [...(r.errorCode ? [r.errorCode] : []), ...r.validationErrors.map(v => v.code)];
    return [...new Set(codes)].map(c => ({ code: c, h: explain(c) })).filter(x => x.h).map(x => ({ code: x.code, what: x.h!.what, todo: x.h!.todo }));
  }

  reissue(): void { this.closed.emit(); void this.router.navigateByUrl('/receipts/new', { state: { reissue: this.receipt().id } }); }

  private async link(): Promise<ShareLink> { return this.api.post<ShareLink>(`/api/v1/receipts/${this.receipt().id}/share`); }

  async sendEmail(): Promise<void> {
    this.sending.set('email'); this.sendNote.set('');
    try {
      const r = await this.api.post<SendResult>(`/api/v1/receipts/${this.receipt().id}/email`, { email: this.email.trim() || null });
      this.sendOk.set(r.sent || !!r.queued); this.sendNote.set(r.sent ? `Sent to ${r.to}.` : r.message);
    } catch (e) { this.sendOk.set(false); this.sendNote.set((e as Error).message ?? 'Could not send.'); this.toast.err(e); }
    finally { this.sending.set(null); }
  }

  /** Texts the invoice number, total and link: to the phone typed here, else the buyer's phone on the invoice. Uses the company's SMS account, else the platform's. */
  async sendSms(): Promise<void> {
    this.sending.set('sms'); this.sendNote.set('');
    try {
      const r = await this.api.post<{ sent: boolean; configured: boolean; to: string; message: string }>(`/api/v1/receipts/${this.receipt().id}/sms`, { phone: this.phone.trim() || null });
      this.sendOk.set(r.sent); this.sendNote.set(r.message);
    } catch (e) { this.sendOk.set(false); this.toast.err(e); }
    finally { this.sending.set(null); }
  }

  /** Opens WhatsApp with the message ready to send. Zimbabwe numbers starting with 0 become +263. With no number, WhatsApp lets you pick the contact. */
  async whatsApp(): Promise<void> {
    this.sending.set('link');
    try {
      const l = await this.link();
      let digits = this.phone.replace(/[^\d+]/g, '').replace(/^\+/, '');
      if (digits.startsWith('0')) digits = '263' + digits.slice(1);
      window.open(`https://wa.me/${digits}?text=${encodeURIComponent(l.whatsAppText)}`, '_blank', 'noopener');
      this.sendOk.set(true); this.sendNote.set('WhatsApp opened with the message ready: press send there.');
    } catch (e) { this.toast.err(e); } finally { this.sending.set(null); }
  }

  async copyLink(): Promise<void> {
    this.sending.set('link');
    try {
      const l = await this.link();
      try { await navigator.clipboard.writeText(l.url); this.sendOk.set(true); this.sendNote.set('Link copied. Paste it into any message.'); }
      catch { this.sendOk.set(false); this.sendNote.set('Could not copy automatically. Copy this link: ' + l.url); }
    } catch (e) { this.toast.err(e); } finally { this.sending.set(null); }
  }

  readonly explain = computed<Explain>(() => {
    const r = this.receipt();
    switch (r.status) {
      case 'Submitted': return { tone: 'ok', title: 'Submitted: ZIMRA has this invoice', text: 'ZIMRA accepted it. The verification page should show it right away.' };
      case 'Pending': return { tone: 'warn', title: 'Queued: numbered and signed, but not with ZIMRA yet', text: 'ZIMRA could not be reached when it was issued. The gateway keeps trying every 30 seconds. Until it arrives, ZIMRA’s page will not find the invoice. Press “Send to ZIMRA now” to try immediately.' };
      case 'OfflineIssued': return { tone: 'info', title: 'Issued offline: waiting for the day’s file', text: 'This device works offline, so the invoice is numbered and signed locally. ZIMRA receives it when the fiscal day is closed and its file is uploaded; until then the verification page will not find it.' };
      case 'Held': return { tone: 'info', title: 'Scheduled: not sent yet, so there is no ZIMRA number or QR code', text: `It is checked and ready, and will be sent${r.holdUntil ? ' at ' + new Date(r.holdUntil).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ' later'}. Until then nothing exists at ZIMRA and it can still be cancelled with no trace. “Send now” numbers it and sends it immediately.` };
      case 'Rejected': return { tone: 'bad', title: 'Rejected by ZIMRA', text: 'ZIMRA refused this receipt, so it is not on their system. Read the message below, then correct the invoice.' };
      case 'Cancelled': return { tone: 'info', title: 'Cancelled before it was sent', text: 'Nothing was sent to ZIMRA and no number was used.' };
      default: return { tone: 'info', title: r.status, text: '' };
    }
  });
  readonly box = computed(() => {
    const t = this.explain().tone;
    return t === 'ok' ? 'border-emerald-200 bg-emerald-50 text-emerald-900 dark:border-emerald-900/50 dark:bg-emerald-900/20 dark:text-emerald-100'
      : t === 'warn' ? 'border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-100'
      : t === 'bad' ? 'border-red-200 bg-red-50 text-red-900 dark:border-red-900/50 dark:bg-red-900/20 dark:text-red-100'
      : 'border-sky-200 bg-sky-50 text-sky-900 dark:border-sky-900/50 dark:bg-sky-900/20 dark:text-sky-100';
  });

  async ngOnInit(): Promise<void> {
    if (!this.receipt().receiptGlobalNo || !this.receipt().qrUrl) return;
    try { this.qr.set(URL.createObjectURL(await this.api.blob(`/api/v1/receipts/${this.receipt().id}/qr`))); } catch { this.qrFailed.set(true); }
  }
  ngOnDestroy(): void { const u = this.qr(); if (u) URL.revokeObjectURL(u); }

  async copy(): Promise<void> {
    try { await navigator.clipboard.writeText(this.receipt().qrUrl ?? ''); this.toast.ok('Verification link copied.'); } catch { this.toast.err(new Error('Could not copy. Open the link and copy it from the address bar.')); }
  }

  async pdf(format: 'a4' | 'receipt'): Promise<void> {
    try { await this.api.open(`/api/v1/receipts/${this.receipt().id}/pdf`, { format }); } catch (e) { this.toast.err(e); }
  }

  async act(verb: 'retry' | 'release', message: string): Promise<void> {
    this.busy.set(true);
    try { await this.api.post(`/api/v1/receipts/${this.receipt().id}/${verb}`); this.toast.ok(message); this.changed.emit(); } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
