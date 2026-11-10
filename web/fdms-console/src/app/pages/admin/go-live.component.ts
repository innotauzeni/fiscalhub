import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { GoLiveRequestRow } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent, slicePage } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

type Filter = 'Pending' | 'SentToZimra' | 'AwaitingPayment' | 'Approved' | 'Rejected' | '';
interface LiveCompany { clientId: string; name: string; approvedAt?: string | null; viaZimra: boolean; zimraLiveDeviceId?: number | null; liveDevices: number; testDevices: number }
interface ZimraMail { to: string; cc?: string | null; subject: string; body: string; attachments: string[] }

/**
 * Companies moving from ZIMRA's demo environment to live. Each request carries the six test documents ZIMRA asks for. The operator emails them to ZIMRA (PDFs attached),
 * and when ZIMRA approves and issues the live device, records its ID and activation key here: the company is switched to live and sees the device to register.
 */
@Component({
  selector: 'app-go-live',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Go-live requests" subtitle="Companies ready for live ZIMRA devices: send their six test documents to ZIMRA, then record the live device ZIMRA issues.">
      <button class="btn-ghost" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    <ol class="mb-4 grid gap-2 text-xs text-slate-600 sm:grid-cols-4 dark:text-slate-300">
      <li class="rounded-lg bg-slate-50 px-3 py-2 dark:bg-slate-800/50"><strong>1.</strong> The company issues 6 test documents (USD and ZWG invoice, credit note, debit note) and asks.</li>
      <li class="rounded-lg bg-slate-50 px-3 py-2 dark:bg-slate-800/50"><strong>2.</strong> <em>Email ZIMRA</em>: the letter and the 6 fiscal PDFs go to ZIMRA.</li>
      <li class="rounded-lg bg-slate-50 px-3 py-2 dark:bg-slate-800/50"><strong>3.</strong> ZIMRA approves and sends you the live device ID and activation key.</li>
      <li class="rounded-lg bg-slate-50 px-3 py-2 dark:bg-slate-800/50"><strong>4.</strong> <em>ZIMRA approved</em>: enter them; the company goes live and registers the device.</li>
    </ol>

    <div class="mb-4 flex flex-wrap gap-1 border-b border-slate-200 dark:border-slate-800" role="tablist">
      @for (t of tabs; track t.value) {
        <button type="button" class="tab" [class.tab-active]="filter() === t.value" (click)="setFilter(t.value)">{{ t.label }}@if (count(t.value); as n) { @if (t.value === 'Pending' || t.value === 'SentToZimra' || t.value === 'Approved') { <span class="badge badge-warn">{{ n }}</span> } }</button>
      }
    </div>

    @if (filter() === 'Approved') {
      @if (unvetted()) {
        <p class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-100">
          {{ unvetted() }} compan{{ unvetted() === 1 ? 'y was' : 'ies were' }} approved for live devices without the ZIMRA process (created by you before it existed, or approved without test documents).
          Switch them off so they go through ZIMRA, unless ZIMRA has already given them a live device.</p>
      }
      <div class="card p-0"><div class="overflow-x-auto"><table class="table w-full text-sm">
        <thead><tr><th>Company</th><th>Live since</th><th>How</th><th class="text-right">Live devices</th><th class="text-right">Demo devices</th><th></th></tr></thead>
        <tbody>
          @for (c of live(); track c.clientId) {
            <tr><td class="font-medium">{{ c.name }}</td><td class="whitespace-nowrap">{{ (c.approvedAt | date: 'd MMM y') ?? '—' }}</td>
              <td>@if (c.viaZimra) { <span class="badge badge-ok">Through ZIMRA</span>@if (c.zimraLiveDeviceId) { <span class="ml-1 font-mono text-xs">device {{ c.zimraLiveDeviceId }}</span> } }
                @else { <span class="badge badge-warn">Without the ZIMRA process</span> }</td>
              <td class="text-right tabular-nums">{{ c.liveDevices }}</td><td class="text-right tabular-nums">{{ c.testDevices }}</td>
              <td class="text-right"><button class="btn-ghost btn-sm" (click)="revokeCompany(c)">Switch live off</button></td></tr>
          } @empty { <tr><td colspan="6"><app-empty icon="bolt" title="No live companies" hint="Companies appear here once ZIMRA has approved them." /></td></tr> }
        </tbody>
      </table></div></div>
    } @else {
    <div class="space-y-3">
      @for (r of rows(); track r.id) {
        <article class="card">
          <div class="flex flex-wrap items-start gap-3">
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2"><span class="font-semibold">{{ r.clientName }}</span>
                <span class="badge" [class.badge-warn]="r.status === 'Pending' || r.status === 'AwaitingPayment'" [class.badge-info]="r.status === 'SentToZimra'" [class.badge-ok]="r.status === 'Approved'" [class.badge-bad]="r.status === 'Rejected'">{{ label(r.status) }}</span></div>
              <div class="text-xs text-slate-500">{{ r.contactName }} · {{ r.contactPhone }}{{ r.zimraTin ? ' · TIN ' + r.zimraTin : '' }} · asked {{ r.createdAt | date: 'd MMM y, HH:mm' }}</div>
              @if (r.notes) { <div class="mt-1 whitespace-pre-wrap text-xs text-slate-500">{{ r.notes }}</div> }
              @if (r.sentToZimraAt) { <div class="mt-1 text-xs text-slate-500">Sent to ZIMRA ({{ r.zimraEmail }}) on {{ r.sentToZimraAt | date: 'd MMM y, HH:mm' }} by {{ r.sentToZimraBy }}</div> }
              @if (r.liveDevice) { <div class="mt-1 text-xs">Live device <strong class="font-mono">{{ r.liveDevice.deviceId }}</strong>{{ r.liveDevice.serialNo ? ' · serial ' + r.liveDevice.serialNo : '' }}</div> }
              @if (r.status === 'AwaitingPayment') { <div class="hint">Go-live fee {{ r.fee }} · invoice {{ r.invoiceNumber }} unpaid</div> }
              @if (r.decisionReason) { <div class="hint">{{ r.decisionReason }}</div> }
            </div>
            <div class="flex flex-wrap justify-end gap-1.5">
              @if (r.testDocuments?.length) { <button class="btn-ghost btn-sm" (click)="pack(r)"><app-icon name="download" size="h-4 w-4" />Pack (zip)</button> }
              @if (r.status === 'Pending' || r.status === 'SentToZimra') {
                <button class="btn-ghost btn-sm" (click)="openMail(r)"><app-icon name="mail" size="h-4 w-4" />{{ r.status === 'SentToZimra' ? 'Email ZIMRA again' : 'Email ZIMRA' }}</button>
                <button class="btn btn-sm" (click)="startApprove(r)">ZIMRA approved…</button>
                <button class="btn-ghost btn-sm" (click)="reject(r)">Reject</button>
              }
              @if (r.status === 'AwaitingPayment') { <button class="btn-ghost btn-sm" (click)="reject(r)">Cancel</button> }
              @if (r.status === 'Approved') { <button class="btn-ghost btn-sm" (click)="revoke(r)">Switch live off</button> }
            </div>
          </div>
          @if (r.testDocuments?.length) {
            <div class="mt-3 overflow-x-auto">
              <table class="table w-full text-xs">
                <thead><tr><th>Test document</th><th>Number</th><th>Against</th><th class="text-right">Total</th><th>Global no.</th><th>Day</th><th>Device</th><th>Date</th><th></th></tr></thead>
                <tbody>@for (d of r.testDocuments; track d.slot) {
                  <tr><td class="font-medium">@if (d.receiptId) { <span class="text-emerald-600">✓</span> } @else { <span class="text-red-600">✗</span> } {{ d.slot }}</td>
                    <td class="font-mono">{{ d.invoiceNo ?? '—' }}</td><td class="font-mono">{{ d.original ?? '' }}</td><td class="text-right tabular-nums">{{ d.total }} {{ d.receiptId ? d.currency : '' }}</td>
                    <td>{{ d.receiptGlobalNo }}</td><td>{{ d.fiscalDayNo }}</td><td>{{ d.zimraDeviceId }}</td><td class="whitespace-nowrap">{{ d.date | date: 'd MMM y HH:mm' }}</td>
                    <td>@if (d.receiptId) { <button class="text-brand-600 hover:underline" (click)="pdf(r, d.receiptId)">PDF</button> }</td></tr>
                }</tbody>
              </table>
            </div>
          } @else if (r.status !== 'Approved' && r.status !== 'Rejected') { <p class="hint mt-2">This request was made before test documents were needed.</p> }
        </article>
      } @empty { <div class="card"><app-empty icon="bolt" title="No requests" hint="Companies that are ready to go live show up here with their six test documents." /></div> }
    </div>
    <app-pager [page]="page()" [pageSize]="size" [total]="list().length" (pageChange)="page.set($event)" />
    }

    @if (mail(); as m) {
      <app-modal [title]="'Email ZIMRA: ' + m.row.clientName" subtitle="Check the letter, then send. The six fiscal PDFs are attached." width="max-w-3xl" (closed)="mail.set(null)">
        <div class="grid gap-3 sm:grid-cols-2">
          <label class="block"><span class="label">To (ZIMRA)</span><input class="input" [(ngModel)]="m.to" placeholder="ZIMRA's FDMS email address" /></label>
          <label class="block"><span class="label">Copy to (optional)</span><input class="input" [(ngModel)]="m.cc" placeholder="you@yourcompany.co.zw" /></label>
          <label class="block sm:col-span-2"><span class="label">Subject</span><input class="input" [(ngModel)]="m.subject" maxlength="200" /></label>
          <label class="block sm:col-span-2"><span class="label">Letter</span><textarea class="input font-mono text-xs" rows="16" [(ngModel)]="m.body"></textarea></label>
        </div>
        <div class="mt-2 flex flex-wrap gap-1">@for (a of m.attachments; track a) { <span class="rounded bg-slate-100 px-2 py-0.5 text-[11px] dark:bg-slate-800">📎 {{ a }}</span> }</div>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
        <div class="mt-5 flex flex-wrap justify-end gap-2"><button class="btn-ghost" (click)="mail.set(null)">Cancel</button><button class="btn-ghost" (click)="pack(m.row)">Download pack instead</button>
          <button class="btn" [disabled]="busy() || !m.to.trim()" (click)="send(m)"><app-icon name="send" size="h-4 w-4" />Send to ZIMRA</button></div>
      </app-modal>
    }

    @if (approving(); as r) {
      <app-modal [title]="'ZIMRA approved ' + r.clientName" subtitle="Enter the live device ZIMRA issued. The company is switched to live and sees the device and its key to register it." width="max-w-lg" (closed)="approving.set(null)">
        <div class="grid gap-3 sm:grid-cols-2">
          <label class="block"><span class="label">Live device ID</span><input class="input font-mono" type="number" min="1" [(ngModel)]="deviceId" /></label>
          <label class="block"><span class="label">Activation key</span><input class="input font-mono uppercase" maxlength="8" [(ngModel)]="activationKey" placeholder="8 characters" /></label>
          <label class="block sm:col-span-2"><span class="label">Device serial number (optional)</span><input class="input font-mono" maxlength="60" [(ngModel)]="serialNo" /></label>
          <label class="block"><span class="label">Go-live fee</span><input class="input" type="number" min="0" step="0.01" [(ngModel)]="fee" /></label>
          <label class="block"><span class="label">Note to the company (optional)</span><input class="input" [(ngModel)]="note" maxlength="500" /></label>
        </div>
        <p class="hint">With a fee, the company gets an invoice and goes live when it is paid. Leave the device empty only if ZIMRA has not issued it yet.</p>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
        <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" (click)="approving.set(null)">Cancel</button><button class="btn" [disabled]="busy()" (click)="approve(r)">Approve and go live</button></div>
      </app-modal>
    }
  `,
})
export class GoLiveComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly size = 10;
  readonly tabs: { value: Filter; label: string }[] = [{ value: 'Pending', label: 'To send to ZIMRA' }, { value: 'SentToZimra', label: 'With ZIMRA' }, { value: 'AwaitingPayment', label: 'Awaiting payment' },
    { value: 'Approved', label: 'Live' }, { value: 'Rejected', label: 'Rejected' }, { value: '', label: 'All' }];
  readonly filter = signal<Filter>('Pending');
  readonly all = signal<GoLiveRequestRow[]>([]);
  readonly live = signal<LiveCompany[]>([]);
  readonly unvetted = computed(() => this.live().filter(c => !c.viaZimra).length);
  readonly page = signal(1);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly approving = signal<GoLiveRequestRow | null>(null);
  readonly mail = signal<(ZimraMail & { row: GoLiveRequestRow }) | null>(null);
  note = ''; fee = 0; deviceId: number | null = null; activationKey = ''; serialNo = '';
  private standardFee = 0;

  readonly list = computed(() => this.all().filter(a => !this.filter() || a.status === this.filter()));
  readonly rows = computed(() => slicePage(this.list(), this.page(), this.size));

  count(f: Filter): number { return f === 'Approved' ? this.unvetted() : this.all().filter(a => a.status === f).length; }
  label(s: string): string { return ({ Pending: 'To send to ZIMRA', SentToZimra: 'With ZIMRA', AwaitingPayment: 'Awaiting payment', Approved: 'Live', Rejected: 'Rejected' } as Record<string, string>)[s] ?? s; }

  ngOnInit(): Promise<void> { return this.load(); }
  setFilter(f: Filter): void { this.filter.set(f); this.page.set(1); }

  async load(): Promise<void> {
    try { this.all.set(await this.api.get<GoLiveRequestRow[]>('/api/v1/admin/go-live')); this.live.set(await this.api.get<LiveCompany[]>('/api/v1/admin/go-live/live-companies')); } catch (e) { this.toast.err(e); }
    try { const p = await this.api.get<{ charge: boolean; goLiveFee: number }>('/api/v1/plans/usage-pricing'); this.standardFee = p.charge ? p.goLiveFee : 0; } catch { /* no plans permission: no default */ }
  }

  async pdf(r: GoLiveRequestRow, receiptId: string): Promise<void> { try { await this.api.open(`/api/v1/admin/go-live/${r.id}/documents/${receiptId}/pdf`); } catch (e) { this.toast.err(e); } }
  async pack(r: GoLiveRequestRow): Promise<void> {
    try { await this.api.download(`/api/v1/admin/go-live/${r.id}/pack`, `zimra-test-documents-${(r.clientName ?? 'company').replace(/[^A-Za-z0-9]+/g, '-')}.zip`); } catch (e) { this.toast.err(e); }
  }

  async openMail(r: GoLiveRequestRow): Promise<void> {
    this.error.set('');
    try { const m = await this.api.get<ZimraMail>(`/api/v1/admin/go-live/${r.id}/zimra-email`); this.mail.set({ ...m, cc: m.cc ?? '', row: r }); } catch (e) { this.toast.err(e); }
  }

  async send(m: ZimraMail & { row: GoLiveRequestRow }): Promise<void> {
    this.busy.set(true); this.error.set('');
    try { await this.api.post(`/api/v1/admin/go-live/${m.row.id}/send-to-zimra`, { to: m.to.trim(), cc: (m.cc ?? '').trim() || null, subject: m.subject, body: m.body }); this.mail.set(null); this.toast.ok('Sent to ZIMRA. The company was told.'); await this.load(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  startApprove(r: GoLiveRequestRow): void { this.error.set(''); this.fee = this.standardFee; this.deviceId = null; this.activationKey = ''; this.serialNo = ''; this.note = ''; this.approving.set(r); }

  async approve(r: GoLiveRequestRow): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      const fee = Number(this.fee) || 0;
      await this.api.post(`/api/v1/admin/go-live/${r.id}/approve`, { note: this.note.trim() || null, fee, liveDeviceId: this.deviceId || null, activationKey: this.activationKey.trim() || null, serialNo: this.serialNo.trim() || null });
      this.approving.set(null);
      this.toast.ok(fee > 0 ? 'Approved. The go-live invoice was sent; the company goes live when it is paid.' : 'The company is live and can register its device.'); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async reject(r: GoLiveRequestRow): Promise<void> {
    const reason = await this.dialog.prompt(`Why can't ${r.clientName} go live yet (for example ZIMRA's answer)? They will see this.`, 'Reason', 'Reject');
    if (reason === null) return;
    try { await this.api.post(`/api/v1/admin/go-live/${r.id}/reject`, { reason }); this.toast.ok('Request rejected.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async revokeCompany(c: LiveCompany): Promise<void> {
    if (!(await this.dialog.confirm(`Switch live devices off for ${c.name}? They go back to demo devices and must go through ZIMRA (six test documents) to go live again. Live devices already registered are not deleted.`, 'Switch off', true))) return;
    try { await this.api.post(`/api/v1/admin/go-live/clients/${c.clientId}/revoke`); this.toast.ok('Live access switched off.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async revoke(r: GoLiveRequestRow): Promise<void> {
    if (!(await this.dialog.confirm(`Switch live devices off for ${r.clientName}? They can still use demo devices; existing live devices are not deleted.`, 'Switch off', true))) return;
    try { await this.api.post(`/api/v1/admin/go-live/clients/${r.clientId}/revoke`); this.toast.ok('Live access switched off.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
