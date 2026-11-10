import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Month { period: string; amount: number; posted: boolean; journalNumber?: string | null }
interface Schedule {
  id: string; name: string; kind: string; currency: string; amount: number; startMonth: string; months: number; balanceAccountId: string; balanceAccount: string; pnlAccountId: string; pnlAccount: string;
  costCentreId?: string | null; status: string; notes?: string | null; released: number; remaining: number; monthsReleased: number; plan: Month[];
}
interface Form { id: string | null; name: string; kind: string; currency: string; amount: number | null; startMonth: string; months: number | null; balanceAccountId: string; pnlAccountId: string; costCentreId: string | null; notes: string; locked: boolean }

/** Amounts paid or received in advance, released to profit or loss month by month. */
@Component({
  selector: 'app-schedules',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Prepayments and deferred income" subtitle="Paid a year's insurance up front, or received a year's fees in advance? Spread it over the months it covers.">
      @if (auth.can('accounting.manage')) {
        <button class="btn-ghost" (click)="run()" [disabled]="busy()"><app-icon name="refresh" size="h-4 w-4" />Release due months now</button>
        <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New schedule</button>
      }
    </app-page-header>
    <p class="mb-5 text-sm text-slate-600 dark:text-slate-300">Record the payment itself as usual (for example <em>Spend money</em> into the prepayments account). Each month that has ended is then released by itself, dated the month's last day.</p>

    <div class="space-y-3">
      @for (s of list(); track s.id) {
        <article class="card p-0" [class.opacity-60]="s.status === 'Cancelled'">
          <button type="button" class="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left" (click)="open.set(open() === s.id ? '' : s.id)">
            <div class="min-w-0 flex-1"><div class="truncate text-sm font-semibold">{{ s.name }}</div>
              <div class="text-xs text-slate-500">{{ s.kind === 'prepayment' ? 'Prepayment' : 'Deferred income' }} · {{ s.startMonth | date: 'MMM y' }}, {{ s.months }} months · {{ s.pnlAccount }}</div></div>
            <span class="badge" [class.badge-ok]="s.status === 'Finished'" [class.badge-info]="s.status === 'Active'" [class.badge-warn]="s.status === 'Cancelled'">{{ s.status }}</span>
            <div class="w-44">
              <div class="flex justify-between text-xs text-slate-500"><span>{{ s.monthsReleased }}/{{ s.months }}</span><span class="tabular-nums">{{ money(s.released) }} / {{ money(s.amount) }} {{ s.currency }}</span></div>
              <div class="mt-1 h-1.5 rounded-full bg-slate-100 dark:bg-slate-800"><div class="h-1.5 rounded-full bg-brand-600" [style.width.%]="(s.released / s.amount) * 100"></div></div>
            </div>
            <app-icon name="chevron" size="h-4 w-4" class="text-slate-400 transition" [class.rotate-180]="open() === s.id" />
          </button>
          @if (open() === s.id) {
            <div class="border-t border-slate-100 px-5 py-3 text-sm dark:border-slate-800">
              <p class="mb-2 text-xs text-slate-500">Held in {{ s.balanceAccount }}, released to {{ s.pnlAccount }}.{{ s.notes ? ' ' + s.notes : '' }}</p>
              <div class="grid gap-1 sm:grid-cols-3 lg:grid-cols-4">
                @for (m of s.plan; track m.period) {
                  <div class="flex items-center justify-between rounded-lg px-2 py-1" [class]="m.posted ? 'bg-emerald-50 dark:bg-emerald-900/20' : 'bg-slate-50 dark:bg-slate-800/50'">
                    <span>{{ m.period | date: 'MMM y' }}</span><span class="tabular-nums">{{ money(m.amount) }}</span><span class="text-xs text-slate-500">{{ m.posted ? m.journalNumber : '' }}</span>
                  </div>
                }
              </div>
              @if (auth.can('accounting.manage')) {
                <div class="mt-3 flex justify-end gap-2">
                  <button class="btn-ghost btn-sm" (click)="edit(s)"><app-icon name="edit" size="h-4 w-4" />{{ s.monthsReleased ? 'Rename' : 'Edit' }}</button>
                  @if (s.status === 'Active') { <button class="btn-ghost btn-sm" (click)="cancel(s)">Stop releasing</button> }
                </div>
              }
            </div>
          }
        </article>
      } @empty { <div class="card text-center text-slate-500">{{ loaded() ? 'No schedules yet.' : 'Loading…' }}</div> }
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Schedule' : 'New schedule'" [subtitle]="f.locked ? 'Months were already released: only the name and notes can change' : 'Released over the months it covers'" width="max-w-2xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="120" placeholder="Vehicle insurance 2026" /></label>
            <label class="block"><span class="label">Kind</span><select class="input" name="k" [(ngModel)]="f.kind" (ngModelChange)="kindChanged(f)" [disabled]="f.locked"><option value="prepayment">Prepayment (paid in advance)</option><option value="deferred_income">Deferred income (received in advance)</option></select></label>
            <label class="block"><span class="label">Currency</span><select class="input" name="c" [(ngModel)]="f.currency" [disabled]="f.locked">@for (c of store.meta()?.currencies ?? ['USD']; track c) { <option [value]="c">{{ c }}</option> }</select></label>
            <label class="block"><span class="label">Amount</span><input type="number" step="0.01" min="0.01" class="input text-right" name="a" [(ngModel)]="f.amount" [disabled]="f.locked" /></label>
            <div class="grid grid-cols-2 gap-2">
              <label class="block"><span class="label">First month</span><input type="month" class="input" name="s" [(ngModel)]="f.startMonth" [disabled]="f.locked" /></label>
              <label class="block"><span class="label">Months</span><input type="number" min="1" max="120" class="input text-right" name="m" [(ngModel)]="f.months" [disabled]="f.locked" /></label>
            </div>
            <label class="block"><span class="label">{{ f.kind === 'prepayment' ? 'Prepayments account (asset)' : 'Deferred income account (liability)' }}</span>
              <select class="input" name="b" [(ngModel)]="f.balanceAccountId" [disabled]="f.locked"><option value="">Choose…</option>@for (a of balanceAccounts(f.kind); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            <label class="block"><span class="label">{{ f.kind === 'prepayment' ? 'Expense it becomes' : 'Income it becomes' }}</span>
              <select class="input" name="p" [(ngModel)]="f.pnlAccountId" [disabled]="f.locked"><option value="">Choose…</option>@for (a of pnlAccounts(f.kind); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
            @if (store.activeCentres().length) {
              <label class="block"><span class="label">Cost centre (optional)</span><select class="input" name="cc" [(ngModel)]="f.costCentreId" [disabled]="f.locked"><option [ngValue]="null">—</option>@for (c of store.activeCentres(); track c.id) { <option [ngValue]="c.id">{{ c.code }} · {{ c.name }}</option> }</select></label>
            }
            <label class="block sm:col-span-2"><span class="label">Notes (optional)</span><input class="input" name="no" [(ngModel)]="f.notes" maxlength="300" /></label>
          </div>
          @if (f.amount && f.months) { <p class="mt-3 text-sm text-slate-600 dark:text-slate-300">About <strong class="tabular-nums">{{ money(f.amount / f.months) }} {{ f.currency }}</strong> a month.</p> }
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class SchedulesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly list = signal<Schedule[]>([]);
  readonly loaded = signal(false);
  readonly open = signal('');
  readonly form = signal<Form | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  private readonly active = computed(() => this.store.postable().filter(a => a.isActive));

  balanceAccounts(kind: string) { return this.active().filter(a => (kind === 'prepayment' ? a.type === 'Asset' && a.subType !== 'cash' : a.type === 'Liability')); }
  pnlAccounts(kind: string) { return this.active().filter(a => a.type === (kind === 'prepayment' ? 'Expense' : 'Income')); }

  async ngOnInit(): Promise<void> {
    void this.store.loadCentres().catch(() => undefined);
    try { await this.store.load(); } catch (e) { this.toast.err(e); }
    await this.load();
  }
  async load(): Promise<void> { try { this.list.set(await this.api.get<Schedule[]>('/api/v1/accounting/schedules')); this.loaded.set(true); } catch (e) { this.toast.err(e); } }

  private thisMonth(): string { const d = new Date(); return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0'); }
  start(): void {
    this.error.set('');
    const prepay = this.store.postable().find(a => a.code === '1300') ?? this.balanceAccounts('prepayment')[0];
    this.form.set({ id: null, name: '', kind: 'prepayment', currency: this.store.meta()?.defaultCurrency ?? 'USD', amount: null, startMonth: this.thisMonth(), months: 12, balanceAccountId: prepay?.id ?? '', pnlAccountId: '', costCentreId: null, notes: '', locked: false });
  }
  edit(s: Schedule): void {
    this.error.set('');
    this.form.set({ id: s.id, name: s.name, kind: s.kind, currency: s.currency, amount: s.amount, startMonth: s.startMonth.slice(0, 7), months: s.months, balanceAccountId: s.balanceAccountId, pnlAccountId: s.pnlAccountId, costCentreId: s.costCentreId ?? null, notes: s.notes ?? '', locked: s.monthsReleased > 0 });
  }
  kindChanged(f: Form): void { f.balanceAccountId = ''; f.pnlAccountId = ''; this.form.update(x => (x ? { ...x } : x)); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.put('/api/v1/accounting/schedules', { id: f.id, name: f.name.trim(), kind: f.kind, currency: f.currency, amount: Number(f.amount) || 0, startMonth: f.startMonth + '-01', months: Number(f.months) || 0, balanceAccountId: f.balanceAccountId || null, pnlAccountId: f.pnlAccountId || null, costCentreId: f.costCentreId, notes: f.notes.trim() || null });
      this.form.set(null); this.toast.ok('Schedule saved.'); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }
  async run(): Promise<void> {
    this.busy.set(true);
    try { const r = await this.api.post<{ posted: number }>('/api/v1/accounting/schedules/run'); this.toast.ok(r.posted ? `${r.posted} month(s) released.` : 'Nothing is due yet.'); await this.load(); } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
  async cancel(s: Schedule): Promise<void> {
    if (!(await this.dialog.confirm(`Stop releasing "${s.name}"? The ${money(s.remaining)} ${s.currency} not yet released stays in ${s.balanceAccount}.`, 'Stop', true))) return;
    try { await this.api.post('/api/v1/accounting/schedules/' + s.id + '/cancel'); this.toast.ok('Stopped.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
