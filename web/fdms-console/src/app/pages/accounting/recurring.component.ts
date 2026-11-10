import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Line { accountId: string; debit: number | null; credit: number | null; description: string; costCentreId: string | null }
interface Recurring {
  id: string; name: string; currency: string; memo: string; reference?: string | null; lines: { accountId: string; debit: number; credit: number; description?: string | null; costCentreId?: string | null }[];
  frequency: string; nextDate: string; endDate?: string | null; autoReverse: boolean; isActive: boolean; lastRunAt?: string | null; lastEntryNumber?: string | null; lastError?: string | null; runCount: number; total: number;
}
interface Form { id?: string; name: string; currency: string; memo: string; reference: string; frequency: string; nextDate: string; endDate: string; autoReverse: boolean; isActive: boolean; lines: Line[] }
const FREQ: Record<string, string> = { weekly: 'Every week', monthly: 'Every month', quarterly: 'Every quarter', yearly: 'Every year' };

/** Journals that post themselves on a schedule (rent, depreciation of a lease, loan interest, salary accruals), optionally reversing at the start of the next month. */
@Component({
  selector: 'app-recurring-journals',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Recurring journals" subtitle="Entries that post themselves every week, month, quarter or year. Each run appears under Journals.">
      <app-export-menu [build]="exportDoc" [disabled]="!list().length" />
      @if (canManage) { <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New recurring journal</button> }
    </app-page-header>

    <div class="space-y-3">
      @for (r of list(); track r.id) {
        <article class="card" [class.opacity-60]="!r.isActive">
          <div class="flex flex-wrap items-center gap-3">
            <app-icon name="repeat" size="h-5 w-5" class="text-brand-600" />
            <div class="min-w-0 flex-1"><div class="font-semibold">{{ r.name }}</div>
              <div class="text-xs text-slate-500">{{ freq[r.frequency] }} · {{ r.memo }}{{ r.autoReverse ? ' · reverses next month' : '' }}</div></div>
            <div class="text-right text-sm"><div class="font-semibold tabular-nums">{{ money(r.total) }} {{ r.currency }}</div>
              <div class="text-xs text-slate-500">@if (r.isActive) { Next: {{ r.nextDate | date: 'd MMM y' }} } @else { Finished or switched off }</div></div>
            @if (canManage) {
              <button class="btn-ghost btn-sm" (click)="run(r)" [disabled]="!r.isActive || busy()">Post now</button>
              <button class="icon-btn" (click)="edit(r)" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
              <button class="icon-btn icon-btn-danger" (click)="remove(r)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
            }
          </div>
          <div class="mt-2 flex flex-wrap gap-x-4 text-xs text-slate-500">
            <span>Posted {{ r.runCount }} time(s){{ r.lastEntryNumber ? ', last ' + r.lastEntryNumber : '' }}</span>
            @if (r.endDate) { <span>Until {{ r.endDate | date: 'd MMM y' }}</span> }
            @if (r.lastError) { <span class="text-red-600">Last attempt failed: {{ r.lastError }}</span> }
          </div>
        </article>
      } @empty { <div class="card text-center text-slate-500">No recurring journals. Add rent, insurance, loan interest or accruals that repeat.</div> }
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit recurring journal' : 'New recurring journal'" width="max-w-4xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-4">
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="name" [(ngModel)]="f.name" maxlength="100" placeholder="Shop rent" required /></label>
            <label class="block sm:col-span-2"><span class="label">Description on each entry</span><input class="input" name="memo" [(ngModel)]="f.memo" maxlength="200" placeholder="Monthly rent, Samora Machel shop" required /></label>
            <label class="block"><span class="label">How often</span><select class="input" name="fr" [(ngModel)]="f.frequency">@for (k of freqKeys; track k) { <option [value]="k">{{ freq[k] }}</option> }</select></label>
            <label class="block"><span class="label">Next posting on</span><input type="date" class="input" name="nd" [(ngModel)]="f.nextDate" required /></label>
            <label class="block"><span class="label">Stop after (optional)</span><input type="date" class="input" name="ed" [(ngModel)]="f.endDate" /></label>
            <label class="block"><span class="label">Currency</span><select class="input" name="c" [(ngModel)]="f.currency">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
          </div>
          <table class="mt-4 w-full text-sm">
            <thead><tr class="text-left text-xs uppercase tracking-wide text-slate-500"><th>Account</th><th>Note</th>@if (store.activeCentres().length) { <th>Cost centre</th> }<th class="text-right">Debit</th><th class="text-right">Credit</th><th></th></tr></thead>
            <tbody>
              @for (l of f.lines; track $index; let i = $index) {
                <tr>
                  <td class="py-1 pr-2"><select class="input" [name]="'a' + i" [(ngModel)]="l.accountId"><option value="">Choose…</option>@for (a of store.postable(); track a.id) { @if (a.isActive) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> } }</select></td>
                  <td class="py-1 pr-2"><input class="input" [name]="'n' + i" [(ngModel)]="l.description" maxlength="200" /></td>
                  @if (store.activeCentres().length) { <td class="py-1 pr-2"><select class="input" [name]="'cc' + i" [(ngModel)]="l.costCentreId"><option [ngValue]="null">—</option>@for (c of store.activeCentres(); track c.id) { <option [ngValue]="c.id">{{ c.code }}</option> }</select></td> }
                  <td class="w-32 py-1 pr-2"><input type="number" step="0.01" min="0" class="input text-right" [name]="'dr' + i" [(ngModel)]="l.debit" /></td>
                  <td class="w-32 py-1 pr-2"><input type="number" step="0.01" min="0" class="input text-right" [name]="'cr' + i" [(ngModel)]="l.credit" /></td>
                  <td>@if (f.lines.length > 2) { <button type="button" class="icon-btn" (click)="f.lines.splice(i, 1)" aria-label="Remove"><app-icon name="x" size="h-4 w-4" /></button> }</td>
                </tr>
              }
            </tbody>
          </table>
          <div class="mt-2 flex flex-wrap items-center justify-between gap-2 text-sm">
            <button type="button" class="btn-ghost btn-sm" (click)="f.lines.push(blank())"><app-icon name="plus" size="h-4 w-4" />Add line</button>
            <span [class]="balanced(f) ? 'text-emerald-600' : 'text-amber-600'">Debits {{ money(total(f, 'debit')) }} · credits {{ money(total(f, 'credit')) }}</span>
          </div>
          <label class="mt-3 flex items-start gap-2 text-sm"><input type="checkbox" class="mt-0.5 h-4 w-4" name="ar" [(ngModel)]="f.autoReverse" />
            <span><strong>Reverse each posting on the first day of the next month</strong> — for accruals (for example salaries or electricity used but not yet billed).</span></label>
          <label class="mt-2 flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="ia" [(ngModel)]="f.isActive" />Switched on</label>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !balanced(f)">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class RecurringJournalsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly canManage = inject(AuthService).can('accounting.manage');
  readonly money = money;
  readonly freq = FREQ;
  readonly freqKeys = Object.keys(FREQ);
  readonly list = signal<Recurring[]>([]);
  readonly form = signal<Form | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); void this.store.loadCentres().catch(() => undefined); await this.load(); } catch (e) { this.toast.err(e); }
  }
  async load(): Promise<void> { this.list.set(await this.api.get<Recurring[]>('/api/v1/accounting/recurring')); }

  blank(): Line { return { accountId: '', debit: null, credit: null, description: '', costCentreId: null }; }
  total(f: Form, side: 'debit' | 'credit'): number { return Math.round(f.lines.reduce((n, l) => n + (Number(l[side]) || 0), 0) * 100) / 100; }
  balanced(f: Form): boolean { return this.total(f, 'debit') > 0 && this.total(f, 'debit') === this.total(f, 'credit'); }

  start(): void {
    this.error.set('');
    const d = new Date(); const monthEnd = isoDate(new Date(d.getFullYear(), d.getMonth() + 1, 0));
    this.form.set({ name: '', currency: this.store.meta()?.defaultCurrency ?? 'USD', memo: '', reference: '', frequency: 'monthly', nextDate: monthEnd, endDate: '', autoReverse: false, isActive: true, lines: [this.blank(), this.blank()] });
  }
  edit(r: Recurring): void {
    this.error.set('');
    this.form.set({ id: r.id, name: r.name, currency: r.currency, memo: r.memo, reference: r.reference ?? '', frequency: r.frequency, nextDate: r.nextDate.slice(0, 10), endDate: r.endDate?.slice(0, 10) ?? '',
      autoReverse: r.autoReverse, isActive: r.isActive, lines: r.lines.map(l => ({ accountId: l.accountId, debit: l.debit || null, credit: l.credit || null, description: l.description ?? '', costCentreId: l.costCentreId ?? null })) });
  }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.put('/api/v1/accounting/recurring', { id: f.id ?? null, name: f.name.trim(), currency: f.currency, memo: f.memo.trim(), reference: f.reference.trim() || null, frequency: f.frequency,
        nextDate: f.nextDate, endDate: f.endDate || null, autoReverse: f.autoReverse, isActive: f.isActive,
        lines: f.lines.filter(l => l.debit || l.credit).map(l => ({ accountId: l.accountId, debit: Number(l.debit) || 0, credit: Number(l.credit) || 0, description: l.description.trim() || null, costCentreId: l.costCentreId })) });
      this.form.set(null); this.toast.ok('Saved. It posts itself on each date.'); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async run(r: Recurring): Promise<void> {
    if (!(await this.dialog.confirm(`Post "${r.name}" for ${r.nextDate.slice(0, 10)} now? The schedule moves on to the next date.`, 'Post now'))) return;
    this.busy.set(true);
    try { const j = await this.api.post<{ number: string }>(`/api/v1/accounting/recurring/${r.id}/run`); this.toast.ok(`Posted as ${j.number}.`); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(r: Recurring): Promise<void> {
    if (!(await this.dialog.confirm(`Delete "${r.name}"? Entries already posted stay in the books.`, 'Delete', true))) return;
    try { await this.api.delete('/api/v1/accounting/recurring/' + r.id); this.toast.ok('Deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }

  readonly exportDoc = (): ExportDoc => ({
    title: 'Recurring journals', landscape: true,
    columns: [{ header: 'Name' }, { header: 'Description' }, { header: 'How often' }, { header: 'Next posting' }, { header: 'Until' }, { header: 'Reverses' }, { header: 'Posted', kind: 'number' }, { header: 'Amount', kind: 'money' }],
    rows: this.list().map(r => ({ cells: [r.name, r.memo, FREQ[r.frequency], r.isActive ? r.nextDate.slice(0, 10) : 'finished', r.endDate?.slice(0, 10) ?? '', r.autoReverse ? 'yes' : '', String(r.runCount), num(r.total)] })),
  });
}
