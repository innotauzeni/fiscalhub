import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface BudgetAccount { accountId: string; code: string; name: string; type: string; months: number[] }
interface Budget { id: string; name: string; year: number; currency: string; totalIncome: number; totalExpense: number; accounts: BudgetAccount[] }
interface Row { accountId: string; code: string; name: string; budget: number; actual: number; variance: number; variancePct?: number | null }
interface Section { title: string; rows: Row[]; budget: number; actual: number; variance: number }
interface Variance { budgetId: string; name: string; year: number; currency: string; fromMonth: number; toMonth: number; sections: Section[]; budgetedProfit: number; actualProfit: number; profitVariance: number }
interface Form { id?: string; name: string; year: number; currency: string; lines: BudgetAccount[]; pick: string }

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Plan the year, then see month by month how the books compare. Over budget on income is good, over budget on spending is not, and the colours say so. */
@Component({
  selector: 'app-budgets',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Budgets" subtitle="Plan income and spending for the year, then compare it with what your books say actually happened.">
      @if (auth.can('accounting.manage')) { <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New budget</button> }
    </app-page-header>

    @if (budgets().length) {
      <div class="card mb-5">
        <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
          <label class="block lg:col-span-2"><span class="label">Budget</span><select class="input" [ngModel]="selected()" (ngModelChange)="selected.set($event); compare()">@for (b of budgets(); track b.id) { <option [value]="b.id">{{ b.name }} · {{ b.year }} · {{ b.currency }}</option> }</select></label>
          <label class="block"><span class="label">From month</span><select class="input" [ngModel]="fromMonth()" (ngModelChange)="fromMonth.set(+$event); compare()">@for (m of months; track $index) { <option [value]="$index + 1">{{ m }}</option> }</select></label>
          <label class="block"><span class="label">To month</span><select class="input" [ngModel]="toMonth()" (ngModelChange)="toMonth.set(+$event); compare()">@for (m of months; track $index) { <option [value]="$index + 1">{{ m }}</option> }</select></label>
          <div class="flex items-end gap-2">@if (auth.can('accounting.manage') && current()) { <button class="btn-ghost flex-1" (click)="edit()">Edit</button><button class="icon-btn icon-btn-danger" (click)="remove()" aria-label="Delete budget"><app-icon name="trash" size="h-4 w-4" /></button> }</div>
        </div>
      </div>
    }

    @if (v(); as r) {
      <div class="mb-5 grid gap-4 sm:grid-cols-3">
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Planned profit</div><div class="text-xl font-semibold tabular-nums">{{ money(r.budgetedProfit) }} <span class="text-sm font-normal text-slate-500">{{ r.currency }}</span></div></div>
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Actual profit</div><div class="text-xl font-semibold tabular-nums">{{ money(r.actualProfit) }}</div></div>
        <div class="rounded-2xl border p-4" [class]="r.profitVariance >= 0 ? 'border-emerald-300 bg-emerald-50 dark:border-emerald-800 dark:bg-emerald-900/20' : 'border-red-300 bg-red-50 dark:border-red-800 dark:bg-red-900/20'">
          <div class="text-xs uppercase tracking-wide text-slate-500">{{ r.profitVariance >= 0 ? 'Ahead of plan' : 'Behind plan' }}</div><div class="text-xl font-semibold tabular-nums">{{ money(abs(r.profitVariance)) }}</div></div>
      </div>

      <div class="space-y-5">
        @for (s of r.sections; track s.title) {
          <section class="card p-0">
            <header class="border-b border-slate-100 px-5 py-3 dark:border-slate-800"><h2 class="text-base font-semibold">{{ s.title }}</h2></header>
            <div class="overflow-x-auto"><table class="table">
              <thead><tr><th>Account</th><th class="text-right">Budget</th><th class="text-right">Actual</th><th class="text-right">Variance</th><th class="w-48">Against plan</th></tr></thead>
              <tbody>
                @for (row of s.rows; track row.accountId) {
                  <tr><td><span class="font-mono text-xs text-slate-500">{{ row.code }}</span> {{ row.name }}@if (!row.budget) { <span class="badge badge-warn ml-2">Not budgeted</span> }</td>
                    <td class="text-right tabular-nums">{{ money(row.budget) }}</td><td class="text-right tabular-nums">{{ money(row.actual) }}</td>
                    <td class="text-right font-medium tabular-nums" [class.text-emerald-600]="row.variance > 0" [class.text-red-600]="row.variance < 0">{{ row.variance > 0 ? '+' : '' }}{{ money(row.variance) }}</td>
                    <td>@if (row.variancePct != null) { <div class="flex items-center gap-2"><span class="h-2 flex-1 overflow-hidden rounded-full bg-slate-100 dark:bg-slate-800"><span class="block h-full rounded-full" [class.bg-emerald-500]="row.variance >= 0" [class.bg-red-500]="row.variance < 0" [style.width.%]="bar(row.variancePct)"></span></span><span class="w-14 text-right text-xs tabular-nums">{{ row.variancePct > 0 ? '+' : '' }}{{ row.variancePct }}%</span></div> }</td></tr>
                } @empty { <tr><td colspan="5" class="py-6 text-center text-slate-500">Nothing planned or posted here.</td></tr> }
              </tbody>
              <tfoot><tr class="font-semibold"><td>Total {{ s.title.toLowerCase() }}</td><td class="text-right tabular-nums">{{ money(s.budget) }}</td><td class="text-right tabular-nums">{{ money(s.actual) }}</td><td class="text-right tabular-nums" [class.text-emerald-600]="s.variance > 0" [class.text-red-600]="s.variance < 0">{{ s.variance > 0 ? '+' : '' }}{{ money(s.variance) }}</td><td></td></tr></tfoot>
            </table></div>
          </section>
        }
      </div>
    } @else if (loaded() && !budgets().length) {
      <div class="card text-center"><h2 class="text-base font-semibold">No budget yet</h2><p class="text-sm text-slate-500">Plan your income and expenses for the year, then watch how the books compare.</p>
        @if (auth.can('accounting.manage')) { <button class="btn mt-4" (click)="start()">Create your first budget</button> }</div>
    }

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit budget' : 'New budget'" subtitle="Enter what you expect for each month. Use Spread to repeat one amount across the year." width="max-w-6xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-4">
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="100" placeholder="Operating plan" /></label>
            <label class="block"><span class="label">Year</span><input type="number" class="input" name="y" [(ngModel)]="f.year" min="2000" max="2100" required /></label>
            <label class="block"><span class="label">Currency</span><select class="input" name="c" [(ngModel)]="f.currency">@for (c of store.meta()?.currencies ?? []; track c) { <option [value]="c">{{ c }}</option> }</select></label>
          </div>
          <div class="mt-4 overflow-x-auto"><table class="w-full text-xs">
            <thead><tr class="text-left uppercase tracking-wide text-slate-500"><th class="min-w-44 pb-1">Account</th>@for (m of months; track $index) { <th class="pb-1 text-right">{{ m }}</th> }<th class="pb-1 text-right">Year</th><th></th></tr></thead>
            <tbody>
              @for (l of f.lines; track l.accountId; let i = $index) {
                <tr><td class="py-1 pr-2"><span class="font-mono text-slate-500">{{ l.code }}</span> {{ l.name }}</td>
                  @for (m of months; track $index; let k = $index) { <td class="py-1 pr-1"><input type="number" step="0.01" min="0" class="input h-8 w-20 px-2 py-0 text-right text-xs" [name]="'m' + i + '-' + k" [(ngModel)]="l.months[k]" /></td> }
                  <td class="py-1 pr-2 text-right font-medium tabular-nums">{{ money(sum(l)) }}</td>
                  <td class="py-1 whitespace-nowrap"><button type="button" class="btn-ghost btn-sm" (click)="spread(l)">Spread</button><button type="button" class="icon-btn" (click)="removeLine(i)" aria-label="Remove account"><app-icon name="x" size="h-4 w-4" /></button></td></tr>
              }
            </tbody>
          </table></div>
          <div class="mt-3 flex flex-wrap items-end gap-2">
            <label class="block"><span class="label">Add an account</span><select class="input" name="pick" [(ngModel)]="f.pick" (ngModelChange)="addLine($event)"><option value="">Choose income or expense…</option>@for (a of choices(); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }} ({{ a.type }})</option> }</select></label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !f.lines.length || !f.name.trim()">Save budget</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class BudgetsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly months = MONTHS;
  readonly budgets = signal<Budget[]>([]);
  readonly selected = signal('');
  readonly fromMonth = signal(1);
  readonly toMonth = signal(new Date().getMonth() + 1);
  readonly v = signal<Variance | null>(null);
  readonly loaded = signal(false);
  readonly form = signal<Form | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly current = computed(() => this.budgets().find(b => b.id === this.selected()) ?? null);
  readonly choices = computed(() => { const used = new Set(this.form()?.lines.map(l => l.accountId)); return this.store.postable().filter(a => a.isActive && (a.type === 'Income' || a.type === 'Expense') && !used.has(a.id)); });

  abs(n: number): number { return Math.abs(n); }
  sum(l: BudgetAccount): number { return l.months.reduce((n, m) => n + (Number(m) || 0), 0); }
  bar(pct: number): number { return Math.min(100, Math.abs(pct)); }

  async ngOnInit(): Promise<void> { try { await this.store.load(); } catch (e) { this.toast.err(e); } await this.load(); }

  async load(selectId?: string): Promise<void> {
    try {
      this.budgets.set(await this.api.get<Budget[]>('/api/v1/accounting/budgets'));
      const keep = selectId ?? this.selected();
      this.selected.set(this.budgets().some(b => b.id === keep) ? keep : this.budgets()[0]?.id ?? '');
      this.loaded.set(true);
      await this.compare();
    } catch (e) { this.toast.err(e); }
  }

  async compare(): Promise<void> {
    if (!this.selected()) { this.v.set(null); return; }
    try { this.v.set(await this.api.get<Variance>('/api/v1/accounting/budgets/' + this.selected() + '/variance', { fromMonth: this.fromMonth(), toMonth: Math.max(this.fromMonth(), this.toMonth()) })); } catch (e) { this.toast.err(e); }
  }

  start(): void {
    this.error.set('');
    this.form.set({ name: '', year: new Date().getFullYear(), currency: this.store.meta()?.defaultCurrency ?? 'USD', lines: [], pick: '' });
  }

  edit(): void {
    const b = this.current(); if (!b) return;
    this.error.set('');
    this.form.set({ id: b.id, name: b.name, year: b.year, currency: b.currency, lines: b.accounts.map(a => ({ ...a, months: [...a.months] })), pick: '' });
  }

  addLine(id: string): void {
    const a = this.store.accounts().find(x => x.id === id); const f = this.form();
    if (!a || !f) return;
    f.lines.push({ accountId: a.id, code: a.code, name: a.name, type: a.type, months: Array(12).fill(0) });
    this.form.set({ ...f, pick: '' });
  }
  removeLine(i: number): void { const f = this.form(); if (f) { f.lines.splice(i, 1); this.form.set({ ...f }); } }
  spread(l: BudgetAccount): void { const first = Number(l.months.find(m => Number(m) > 0) ?? l.months[0]) || 0; l.months = Array(12).fill(first); this.form.update(f => (f ? { ...f } : f)); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      const saved = await this.api.put<Budget>('/api/v1/accounting/budgets', { id: f.id ?? null, name: f.name.trim(), year: Number(f.year), currency: f.currency, lines: f.lines.map(l => ({ accountId: l.accountId, months: l.months.map(m => Number(m) || 0) })) });
      this.form.set(null); this.toast.ok('Budget saved.'); await this.load(saved.id);
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async remove(): Promise<void> {
    const b = this.current(); if (!b) return;
    if (!(await this.dialog.confirm('Delete the budget "' + b.name + '"?', 'Delete', true))) return;
    try { await this.api.delete('/api/v1/accounting/budgets/' + b.id); this.toast.ok('Budget deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
