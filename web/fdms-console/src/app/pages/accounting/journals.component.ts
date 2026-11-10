import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, Journal, isoDate, monthStart, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { doneOrHeld } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Line { accountId: string; description: string; debit: number | null; credit: number | null; costCentreId: string | null }
interface Form { date: string; currency: string; memo: string; reference: string; lines: Line[]; reverse: boolean; reverseOn: string }

const SOURCES: Record<string, string | undefined> = { sales: 'Fiscal sale', manual: 'Manual', cashbook: 'Cashbook', bank: 'Bank', reversal: 'Reversal', bill: 'Bill', opening: 'Opening balances', depn: 'Depreciation', asset: 'Fixed assets', vat: 'VAT' };
/** What only the platform's own books hold (billing to companies). */
const PLATFORM_SOURCES: Record<string, string> = { billing: 'Billing invoice', billing_paid: 'Billing payment', commission: 'Partner commission', payout: 'Partner payout' };

/** Every posting in the books. Fiscal receipts appear by themselves; manual journals are entered here, and a wrong one is reversed, never edited. */
@Component({
  selector: 'app-journals',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Journals" [subtitle]="platform() ? 'Your platform books. Billing invoices, their payments and partner commissions post themselves.' : 'Every posting in your books. Confirmed fiscal invoices and credit notes post themselves.'">
      <app-export-menu [build]="exportDoc" [disabled]="!journals().length" />
      @if (auth.can('accounting.manage')) {
        <button class="btn-ghost" (click)="sync()" [disabled]="busy()"><app-icon name="refresh" size="h-4 w-4" />{{ platform() ? 'Post new billing' : 'Post new sales' }}</button>
        <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New journal</button>
      }
    </app-page-header>

    <div class="mb-5 flex flex-wrap items-center justify-between gap-3 rounded-xl border px-4 py-3 text-sm"
         [class]="lockedUntil() ? 'border-amber-200 bg-amber-50 text-amber-900 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-100' : 'border-slate-200 bg-white text-slate-600 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-300'">
      <span class="flex items-center gap-2"><app-icon name="lock" size="h-4 w-4" />@if (lockedUntil(); as l) { <span>The books are <strong>closed up to {{ l | date: 'd MMM y' }}</strong>. Nothing can be posted on or before that day.</span> } @else { <span>No period is closed. When a month is final, close it so its figures can never change.</span> }</span>
      @if (auth.can('accounting.manage')) { <button class="btn-ghost btn-sm" (click)="openLock()">{{ lockedUntil() ? 'Change' : 'Close a period' }}</button> }
    </div>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event)" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event)" /></label>
        <label class="block"><span class="label">Kind</span><select class="input" [ngModel]="source()" (ngModelChange)="source.set($event)"><option value="">All</option>@for (s of sourceKeys; track s) { <option [value]="s">{{ sources[s] ?? s }}</option> }</select></label>
        <label class="block"><span class="label">Currency</span><select class="input" [ngModel]="currency()" (ngModelChange)="currency.set($event)"><option value="">All</option>@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
        <label class="block"><span class="label">Search</span><input class="input" placeholder="Memo, number, reference" [ngModel]="q()" (ngModelChange)="q.set($event)" (keyup.enter)="load()" /></label>
      </div>
      <div class="mt-3 flex justify-end"><button class="btn" (click)="load()" [disabled]="busy()">Show</button></div>
    </div>

    <div class="space-y-3">
      @for (j of journals(); track j.id) {
        <article class="card p-0" [class.opacity-70]="j.reversed">
          <button type="button" class="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left" (click)="toggle(j.id)" [attr.aria-expanded]="open() === j.id">
            <span class="font-mono text-sm font-semibold text-brand-700 dark:text-brand-100">{{ j.number }}</span>
            <span class="text-sm text-slate-500">{{ j.date | date: 'd MMM y' }}</span>
            <span class="min-w-0 flex-1 truncate text-sm font-medium">{{ j.memo }}</span>
            <span class="badge" [class.badge-ok]="j.source === 'sales'" [class.badge-info]="j.source !== 'sales'">{{ sources[j.source] ?? j.source }}</span>
            @if (j.reversed) { <span class="badge badge-warn">Reversed</span> }
            <span class="w-32 text-right text-sm font-semibold tabular-nums">{{ money(j.total) }} {{ j.currency }}</span>
            <app-icon name="chevron" size="h-4 w-4" class="text-slate-400 transition" [class.rotate-180]="open() === j.id" />
          </button>
          @if (open() === j.id) {
            <div class="border-t border-slate-100 px-5 py-3 dark:border-slate-800">
              <table class="w-full text-sm">
                <thead><tr class="text-left text-xs uppercase tracking-wide text-slate-500"><th class="py-1.5">Account</th><th class="py-1.5">Note</th><th class="py-1.5">Cost centre</th><th class="py-1.5 text-right">Debit</th><th class="py-1.5 text-right">Credit</th></tr></thead>
                <tbody>@for (l of j.lines; track l.id) { <tr class="border-t border-slate-100 dark:border-slate-800"><td class="py-1.5"><span class="font-mono text-xs text-slate-500">{{ l.accountCode }}</span> {{ l.accountName }}</td><td class="py-1.5 text-slate-500">{{ l.description }}</td><td class="py-1.5 text-slate-500">{{ l.costCentre }}</td>
                  <td class="py-1.5 text-right tabular-nums">{{ l.debit ? money(l.debit) : '' }}</td><td class="py-1.5 text-right tabular-nums">{{ l.credit ? money(l.credit) : '' }}</td></tr> }</tbody>
              </table>
              <div class="mt-3 flex flex-wrap items-center justify-between gap-2 text-xs text-slate-500">
                <span>{{ j.reference ? 'Reference ' + j.reference + ' · ' : '' }}Entered by {{ j.createdBy || 'the system' }}</span>
                @if (auth.can('accounting.manage') && !j.reversed && !j.isReversal) { <button class="btn-ghost btn-sm" (click)="reverse(j)">Reverse this entry</button> }
              </div>
            </div>
          }
        </article>
      } @empty {
        <div class="card text-center text-slate-500">{{ loaded() ? (platform() ? 'No entries match. Billing invoices to companies appear here by themselves.' : 'No entries match. Issue a fiscal invoice and it appears here by itself.') : 'Loading…' }}</div>
      }
    </div>

    @if (lockOpen()) {
      <app-modal title="Close a period" subtitle="Everything dated on or before this day becomes final: no new entries, and sales confirmed late are posted on the next open day." width="max-w-md" (closed)="lockOpen.set(false)">
        <label class="block"><span class="label">Books are closed up to and including</span><input type="date" class="input" [ngModel]="lockDate()" (ngModelChange)="lockDate.set($event)" [max]="today" /></label>
        <p class="hint">Pick the last day of the month you have finished, for example 30 September. You can reopen later; the change is recorded in the audit log.</p>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
        <div class="mt-5 flex flex-wrap justify-between gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
          <span>@if (lockedUntil()) { <button class="btn-ghost text-red-600" type="button" (click)="saveLock(null)" [disabled]="busy()">Reopen everything</button> }</span>
          <span class="flex gap-2"><button class="btn-ghost" type="button" (click)="lockOpen.set(false)">Cancel</button><button class="btn" type="button" (click)="saveLock(lockDate())" [disabled]="busy() || !lockDate()">Close period</button></span>
        </div>
      </app-modal>
    }

    @if (form(); as f) {
      <app-modal title="New journal" subtitle="Debits and credits must be equal. Posted entries cannot be edited, only reversed." width="max-w-4xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-4">
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="f.date" required /></label>
            <label class="block"><span class="label">Currency</span><select class="input" name="c" [(ngModel)]="f.currency">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Description</span><input class="input" name="m" [(ngModel)]="f.memo" required maxlength="200" placeholder="October rent" /></label>
          </div>
          <div class="mt-4 overflow-x-auto">
            <table class="w-full text-sm">
              <thead><tr class="text-left text-xs uppercase tracking-wide text-slate-500"><th class="pb-1">Account</th><th class="pb-1">Note</th>@if (store.activeCentres().length) { <th class="pb-1">Cost centre</th> }<th class="pb-1 text-right">Debit</th><th class="pb-1 text-right">Credit</th><th></th></tr></thead>
              <tbody>
                @for (l of f.lines; track $index; let i = $index) {
                  <tr>
                    <td class="py-1 pr-2"><select class="input" [name]="'a' + i" [(ngModel)]="l.accountId"><option value="">Choose…</option>
                      @for (a of store.postable(); track a.id) { @if (a.isActive) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> } }</select></td>
                    <td class="py-1 pr-2"><input class="input" [name]="'n' + i" [(ngModel)]="l.description" maxlength="200" /></td>
                    @if (store.activeCentres().length) {
                      <td class="py-1 pr-2"><select class="input" [name]="'cc' + i" [(ngModel)]="l.costCentreId"><option [ngValue]="null">—</option>@for (c of store.activeCentres(); track c.id) { <option [ngValue]="c.id">{{ c.code }} · {{ c.name }}</option> }</select></td>
                    }
                    <td class="w-32 py-1 pr-2"><input type="number" step="0.01" min="0" class="input text-right" [name]="'dr' + i" [(ngModel)]="l.debit" (ngModelChange)="l.credit = $event ? null : l.credit" /></td>
                    <td class="w-32 py-1 pr-2"><input type="number" step="0.01" min="0" class="input text-right" [name]="'cr' + i" [(ngModel)]="l.credit" (ngModelChange)="l.debit = $event ? null : l.debit" /></td>
                    <td class="py-1">@if (f.lines.length > 2) { <button type="button" class="icon-btn" (click)="removeLine(i)" aria-label="Remove line"><app-icon name="x" size="h-4 w-4" /></button> }</td>
                  </tr>
                }
              </tbody>
              <tfoot><tr class="border-t border-slate-200 font-semibold dark:border-slate-700"><td class="py-2" [attr.colspan]="store.activeCentres().length ? 3 : 2"><button type="button" class="btn-ghost btn-sm" (click)="addLine()"><app-icon name="plus" size="h-4 w-4" />Add line</button></td>
                <td class="py-2 pr-2 text-right tabular-nums">{{ money(debit(f)) }}</td><td class="py-2 pr-2 text-right tabular-nums">{{ money(credit(f)) }}</td><td></td></tr></tfoot>
            </table>
          </div>
          <div class="mt-3 flex flex-wrap items-center gap-3 rounded-lg bg-slate-50 px-3 py-2 text-sm dark:bg-slate-800/50">
            <label class="flex items-center gap-2"><input type="checkbox" class="h-4 w-4" name="rev" [(ngModel)]="f.reverse" (ngModelChange)="onReverse(f, $event)" />
              <span><strong>Reverse automatically</strong> (an accrual or prepayment)</span></label>
            @if (f.reverse) { <label class="flex items-center gap-2">on <input type="date" class="input w-44" name="revon" [(ngModel)]="f.reverseOn" /></label> }
          </div>
          <p class="mt-2 text-sm" [class]="balanced(f) ? 'text-emerald-600' : 'text-amber-600'">{{ balanced(f) ? 'Balanced.' : 'Out of balance by ' + money(abs(debit(f) - credit(f))) + '.' }}</p>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !balanced(f)">Post journal</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class JournalsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  /** The platform operator keeps the platform's own books (billing), not a company's. */
  readonly platform = computed(() => !this.auth.user()?.clientId);
  readonly money = money;
  readonly sources: Record<string, string | undefined> = { ...SOURCES, ...PLATFORM_SOURCES };
  get sourceKeys(): string[] { return Object.keys(this.platform() ? { manual: 1, ...PLATFORM_SOURCES, cashbook: 1, bank: 1, reversal: 1, bill: 1, opening: 1 } : SOURCES); }

  readonly journals = signal<Journal[]>([]);
  readonly loaded = signal(false);
  readonly busy = signal(false);
  readonly open = signal('');
  readonly error = signal('');
  readonly form = signal<Form | null>(null);
  readonly from = signal(monthStart());
  readonly to = signal(isoDate(new Date()));
  readonly source = signal('');
  readonly currency = signal('');
  readonly q = signal('');
  readonly count = computed(() => this.journals().length);

  readonly lockedUntil = signal<string | null>(null);
  readonly lockOpen = signal(false);
  readonly lockDate = signal('');
  readonly today = isoDate(new Date());

  private async loadPeriod(): Promise<void> {
    try { const p = await this.api.get<{ lockedUntil?: string | null }>('/api/v1/accounting/period'); this.lockedUntil.set(p.lockedUntil ?? null); } catch { /* the banner just stays neutral */ }
  }
  openLock(): void { this.error.set(''); this.lockDate.set(this.lockedUntil()?.slice(0, 10) ?? ''); this.lockOpen.set(true); }
  async saveLock(date: string | null): Promise<void> {
    this.busy.set(true); this.error.set('');
    try { const p = await this.api.put<{ lockedUntil?: string | null }>('/api/v1/accounting/period', { lockedUntil: date || null }); this.lockedUntil.set(p.lockedUntil ?? null); this.lockOpen.set(false); this.toast.ok(date ? 'Period closed.' : 'Books reopened.'); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  abs(v: number): number { return Math.abs(v); }
  debit(f: Form): number { return Math.round(f.lines.reduce((n, l) => n + (Number(l.debit) || 0), 0) * 100) / 100; }
  credit(f: Form): number { return Math.round(f.lines.reduce((n, l) => n + (Number(l.credit) || 0), 0) * 100) / 100; }
  balanced(f: Form): boolean { return this.debit(f) > 0 && this.debit(f) === this.credit(f) && f.lines.every(l => !!l.accountId || (!l.debit && !l.credit)); }

  async ngOnInit(): Promise<void> { try { await this.store.load(); } catch (e) { this.toast.err(e); } void this.store.loadCentres().catch(() => undefined); void this.loadPeriod(); await this.load(); }

  /** The first day of the month after a date: when an accrual is usually reversed. */
  onReverse(f: Form, on: boolean): void { if (on && !f.reverseOn) f.reverseOn = this.nextMonth(f.date); }
  nextMonth(date: string): string { const d = new Date(date + 'T00:00:00'); return isoDate(new Date(d.getFullYear(), d.getMonth() + 1, 1)); }

  readonly exportDoc = (): ExportDoc | null => {
    const list = this.journals(); if (!list.length) return null;
    return {
      title: 'Journals', subtitle: this.from() + ' to ' + this.to() + (this.currency() ? ' · ' + this.currency() : ''), landscape: true,
      columns: [{ header: 'Entry' }, { header: 'Date' }, { header: 'Account' }, { header: 'Description' }, { header: 'Cost centre' }, { header: 'Currency' }, { header: 'Debit', kind: 'money' }, { header: 'Credit', kind: 'money' }],
      rows: list.flatMap(j => [
        { cells: [j.number, j.date.slice(0, 10), '', j.memo + (j.reversed ? ' (reversed)' : ''), '', j.currency, num(j.total), num(j.total)], style: 'bold' as const },
        ...j.lines.map(l => ({ cells: ['', '', l.accountCode + ' ' + l.accountName, l.description ?? '', l.costCentre ?? '', '', num(l.debit, true), num(l.credit, true)], indent: 1 })),
      ]),
    };
  };

  async load(): Promise<void> {
    this.busy.set(true);
    try { this.journals.set(await this.api.get<Journal[]>('/api/v1/accounting/journals', { from: this.from(), to: this.to(), source: this.source(), currency: this.currency(), q: this.q(), take: 200 })); this.loaded.set(true); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  toggle(id: string): void { this.open.set(this.open() === id ? '' : id); }

  async sync(): Promise<void> {
    this.busy.set(true);
    try { const r = await this.api.post<{ posted: number }>('/api/v1/accounting/sync'); this.toast.ok(r.posted ? r.posted + ' receipt(s) posted to the books.' : 'The books are up to date.'); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  start(): void {
    this.error.set('');
    this.form.set({ date: isoDate(new Date()), currency: this.store.meta()?.defaultCurrency ?? 'USD', memo: '', reference: '', reverse: false, reverseOn: '',
      lines: [{ accountId: '', description: '', debit: null, credit: null, costCentreId: null }, { accountId: '', description: '', debit: null, credit: null, costCentreId: null }] });
  }
  addLine(): void { const f = this.form(); if (f) f.lines.push({ accountId: '', description: '', debit: null, credit: null, costCentreId: null }); this.form.update(x => (x ? { ...x } : x)); }
  removeLine(i: number): void { const f = this.form(); if (f) f.lines.splice(i, 1); this.form.update(x => (x ? { ...x } : x)); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      const res = await this.api.post('/api/v1/accounting/journals', {
        date: f.date, currency: f.currency, memo: f.memo.trim(), reference: f.reference.trim() || null, reverseOn: f.reverse && f.reverseOn ? f.reverseOn : null,
        lines: f.lines.filter(l => l.debit || l.credit).map(l => ({ accountId: l.accountId, debit: Number(l.debit) || 0, credit: Number(l.credit) || 0, description: l.description.trim() || null, costCentreId: l.costCentreId })),
      });
      this.form.set(null); this.toast.ok(doneOrHeld(res, 'Journal posted.')); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async reverse(j: Journal): Promise<void> {
    if (!(await this.dialog.confirm('Reverse ' + j.number + '? A new entry with the debits and credits swapped is posted, and both stay in the books.', 'Reverse', true))) return;
    try { await this.api.post('/api/v1/accounting/journals/' + j.id + '/reverse'); this.toast.ok('Reversed.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
