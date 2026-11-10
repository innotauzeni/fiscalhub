import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DeviceSummary } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';

type Tone = 'ok' | 'warn' | 'bad' | 'idle';
interface DayView { tone: Tone; badge: string; title: string; lines: string[]; action: string; canOpen: boolean; canClose: boolean; }

const CLOSE_HELP: Record<string, string> = {
  ReceiptsWithValidationErrors: 'ZIMRA flagged a receipt of this day in red. Correct it under Fiscal days, then close again.',
  MissingReceipts: 'ZIMRA has not received every receipt of this day yet. Wait a minute (queued receipts are sent automatically) and close again.',
  CountersMismatch: 'ZIMRA calculated different totals from its receipts. Close again; if it keeps failing, check the receipts of the day.',
  BadCertificateSignature: 'ZIMRA rejected the signature. The device certificate may have expired: renew it on the Devices page, then close again.',
};

/**
 * Fiscal day at a glance, per device: is it open or closed, for how long, what is waiting for ZIMRA, and the one thing to do about it, with the button to do it.
 * The day also closes by itself at the auto-close time (23:59 Zimbabwe time by default), so a forgotten day is never a disaster.
 */
@Component({
  selector: 'app-fiscal-day-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe],
  template: `
    @if (views().length) {
      <section class="mb-6" aria-label="Fiscal day status">
        <div class="mb-2 flex flex-wrap items-center justify-between gap-2">
          <h2 class="text-sm font-semibold uppercase tracking-wide text-slate-500">Fiscal day</h2>
          @if (autoClose()) { <span class="text-xs text-slate-500">A day left open closes by itself at {{ autoClose() }} (Zimbabwe time)</span> }
        </div>
        <div class="grid gap-4 lg:grid-cols-2">
          @for (v of views(); track v.d.id) {
            <article class="rounded-2xl border p-5" [class]="box(v.v.tone)">
              <div class="flex items-start justify-between gap-3">
                <div class="min-w-0">
                  <div class="flex items-center gap-2"><span class="h-3 w-3 shrink-0 rounded-full" [class]="dot(v.v.tone)" aria-hidden="true"></span><span class="text-lg font-semibold">{{ v.v.badge }}</span></div>
                  <div class="mt-0.5 truncate text-sm text-slate-600 dark:text-slate-300">{{ v.d.name || 'Device ' + v.d.zimraDeviceId }}@if (v.d.name) { <span class="text-slate-400"> (ZIMRA {{ v.d.zimraDeviceId }})</span> }@if (v.d.branchName) { · {{ v.d.branchName }} }@if (v.d.operatingMode === 'Offline') { · <span class="font-medium text-sky-700 dark:text-sky-300">Offline mode</span> }</div>
                </div>
                @if (v.d.dayNo) { <span class="shrink-0 rounded-lg bg-white/70 px-2 py-1 text-xs font-medium tabular-nums dark:bg-slate-900/60">Day {{ v.d.dayNo }}</span> }
              </div>

              <p class="mt-3 text-sm font-medium">{{ v.v.title }}</p>
              <ul class="mt-1.5 space-y-1 text-sm text-slate-600 dark:text-slate-300">
                @for (l of v.v.lines; track l) { <li class="flex gap-2"><span class="mt-1.5 h-1 w-1 shrink-0 rounded-full bg-slate-400"></span><span>{{ l }}</span></li> }
              </ul>

              <div class="mt-4 rounded-xl bg-white/70 px-3 py-2 text-sm dark:bg-slate-900/60"><span class="font-semibold">What to do: </span>{{ v.v.action }}</div>

              <div class="mt-4 flex flex-wrap items-center gap-2">
                @if (v.v.canClose && auth.can('day.manage')) { <button class="btn" (click)="close(v.d)" [disabled]="busy() === v.d.id">{{ busy() === v.d.id ? 'Working…' : 'Close the day' }}</button> }
                @if (v.v.canOpen && auth.can('day.manage')) { <button class="btn" (click)="open(v.d)" [disabled]="busy() === v.d.id">{{ busy() === v.d.id ? 'Working…' : 'Open a day' }}</button> }
                @if (auth.can('day.view')) { <a routerLink="/days" class="btn-ghost">Fiscal days</a> }
                @if (v.d.operatingMode === 'Offline' && auth.can('day.view')) { <a routerLink="/offline" class="btn-ghost">Offline files &amp; help</a> }
                @if (v.d.dayOpenedAt && v.d.dayStatus === 'FiscalDayOpened') { <span class="ml-auto text-xs text-slate-500">opened {{ openedLocal(v.d) | date:'EEE HH:mm' }}</span> }
              </div>
            </article>
          }
        </div>
      </section>
    }
  `,
})
export class FiscalDayPanelComponent {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly auth = inject(AuthService);

  readonly devices = input.required<DeviceSummary[]>();
  readonly autoClose = input<string | null | undefined>(null);
  readonly changed = output<void>();
  readonly busy = signal<string | null>(null);

  readonly views = computed(() => this.devices().map(d => ({ d, v: this.view(d) })));

  /** "Now" in Zimbabwe time, as a UTC-flavoured clock reading, comparable with the gateway's wall-clock timestamps. */
  private catNow(): number { return Date.now() + 2 * 3600_000; }
  private openedMs(d: DeviceSummary): number { return d.dayOpenedAt ? Date.parse(d.dayOpenedAt.endsWith('Z') ? d.dayOpenedAt : d.dayOpenedAt + 'Z') : 0; }
  /** The opening time as a Date whose local fields read the Zimbabwe wall clock (what the date pipe prints). */
  openedLocal(d: DeviceSummary): Date { const t = new Date(this.openedMs(d)); return new Date(t.getUTCFullYear(), t.getUTCMonth(), t.getUTCDate(), t.getUTCHours(), t.getUTCMinutes()); }

  private waiting(d: DeviceSummary): string[] {
    const l: string[] = [];
    if (d.pendingReceipts) l.push(`${d.pendingReceipts} receipt${d.pendingReceipts === 1 ? ' is' : 's are'} not with ZIMRA yet. They are sent automatically; the day can only close once they arrive.`);
    if (d.heldReceipts) l.push(`${d.heldReceipts} scheduled receipt${d.heldReceipts === 1 ? ' is' : 's are'} still waiting to be sent (Receipts, status Held).`);
    if (d.offlineWaiting) l.push(`${d.offlineWaiting} receipt${d.offlineWaiting === 1 ? ' was' : 's were'} issued offline and ${d.offlineWaiting === 1 ? 'is' : 'are'} waiting for the day's file to be uploaded.`);
    return l;
  }

  private view(d: DeviceSummary): DayView {
    const waiting = this.waiting(d);
    const offline = d.operatingMode === 'Offline';
    switch (d.dayStatus) {
      case 'FiscalDayOpened': {
        const mins = Math.max(0, Math.floor((this.catNow() - this.openedMs(d)) / 60000));
        const h = Math.floor(mins / 60), m = mins % 60;
        const near = h >= (d.dayMaxHrs || 24) - 3;
        return {
          tone: near ? 'warn' : 'ok', badge: 'Day open', canClose: true, canOpen: false,
          title: `Open for ${h} h ${m} min. Receipts are being issued normally.`,
          lines: [...(near ? [`ZIMRA refuses receipts after ${d.dayMaxHrs} hours. Close this day soon.`] : []), ...waiting],
          action: near ? 'Close the day now.' : offline ? 'Close the day when trading ends, then upload the day’s file (Offline files).' : 'Close the day when trading ends. If you forget, it is closed for you automatically.',
        };
      }
      case 'FiscalDayCloseInitiated':
        return { tone: 'warn', badge: 'Closing…', canClose: false, canOpen: false, title: 'ZIMRA is checking the close request.', lines: waiting, action: 'Wait a minute, then refresh. Nothing else is needed.' };
      case 'FiscalDayCloseFailed':
        return {
          tone: 'bad', badge: 'Close failed', canClose: true, canOpen: false,
          title: `ZIMRA refused to close the day${d.closingErrorCode ? ' (' + d.closingErrorCode + ')' : ''}.`,
          lines: [CLOSE_HELP[d.closingErrorCode ?? ''] ?? 'Fix the cause and close again.', ...waiting],
          action: 'Open Fiscal days to see which receipts are flagged, correct them, then close again.',
        };
      default:
        return {
          tone: 'idle', badge: 'Day closed', canClose: false, canOpen: true,
          title: 'No fiscal day is open, so nothing is waiting to be closed.',
          lines: waiting,
          action: waiting.length ? (offline ? 'Upload the day’s file under Offline files.' : 'Nothing: waiting receipts are delivered automatically.') : 'Nothing. The next receipt opens a new day by itself, or open one now.',
        };
    }
  }

  box(t: Tone): string {
    return t === 'ok' ? 'border-emerald-200 bg-emerald-50/70 dark:border-emerald-900/50 dark:bg-emerald-900/15'
      : t === 'warn' ? 'border-amber-200 bg-amber-50/80 dark:border-amber-900/50 dark:bg-amber-900/15'
      : t === 'bad' ? 'border-red-200 bg-red-50/80 dark:border-red-900/50 dark:bg-red-900/15' : 'border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900';
  }
  dot(t: Tone): string { return t === 'ok' ? 'bg-emerald-500' : t === 'warn' ? 'bg-amber-500' : t === 'bad' ? 'bg-red-500' : 'bg-slate-400'; }

  async open(d: DeviceSummary): Promise<void> {
    this.busy.set(d.id);
    try { await this.api.post(`/api/v1/devices/${d.id}/day/open`); this.toast.ok('Fiscal day opened.'); this.changed.emit(); } catch (e) { this.toast.err(e); } finally { this.busy.set(null); }
  }

  async close(d: DeviceSummary): Promise<void> {
    const extra = d.pendingReceipts ? ` ${d.pendingReceipts} receipt(s) are not with ZIMRA yet, so it may be refused until they arrive.` : '';
    if (!(await this.dialog.confirm(`Close the fiscal day of device ${d.zimraDeviceId}? This sends the Z report to ZIMRA and cannot be undone.${extra}`, 'Close day', true))) return;
    this.busy.set(d.id);
    try { await this.api.post(`/api/v1/devices/${d.id}/day/close`); this.toast.ok('Close requested. See the status above.'); this.changed.emit(); } catch (e) { this.toast.err(e); } finally { this.busy.set(null); }
  }
}
