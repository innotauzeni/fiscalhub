import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, isoDate, money } from '../../core/accounting.store';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface StatementLine { id: string; date: string; description: string; reference?: string | null; amount: number; matched: boolean; journalLineId?: string | null }
interface LedgerLine { journalLineId: string; entryId: string; date: string; number: string; description: string; reference?: string | null; amount: number; matched: boolean }
interface Reconciliation {
  account: { id: string; code: string; name: string }; asAt: string; ledgerBalance: number; statementBalance?: number | null; unmatchedLedger: number; unmatchedStatement: number; difference?: number | null;
  statementLines: StatementLine[]; ledgerLines: LedgerLine[]; suggestions: { statementLineId: string; journalLineId: string }[];
}

/**
 * Bank reconciliation: bring in the bank's statement, match each line with what the books recorded, and turn what the books missed (fees, interest, deposits nobody entered) into entries.
 * When the statement balance you typed equals the books' balance adjusted for what is still unmatched, the difference is nil and you are reconciled.
 */
@Component({
  selector: 'app-reconcile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Bank reconciliation" subtitle="Match your bank statement with your books, line by line, until nothing is left unexplained.">
      <button class="btn-ghost" (click)="addOpen.set(true)" [disabled]="!accountId()"><app-icon name="plus" size="h-4 w-4" />Add a line</button>
      <button class="btn" (click)="importOpen.set(true)" [disabled]="!accountId()"><app-icon name="upload" size="h-4 w-4" />Import statement</button>
    </app-page-header>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label class="block lg:col-span-2"><span class="label">Bank account</span><select class="input" [ngModel]="accountId()" (ngModelChange)="accountId.set($event); load()">@for (a of accounts(); track a.id) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> }</select></label>
        <label class="block"><span class="label">As at</span><input type="date" class="input" [ngModel]="asAt()" (ngModelChange)="asAt.set($event); load()" /></label>
        <label class="block"><span class="label">Closing balance on the statement</span><input type="number" step="0.01" class="input text-right" [ngModel]="balance()" (ngModelChange)="balance.set($event)" (change)="load()" placeholder="0.00" /></label>
      </div>
      @if (!accounts().length) { <p class="mt-3 text-sm text-amber-700 dark:text-amber-300">No bank account yet. Add your bank and its accounts under Banks, and each gets its own ledger account here.</p> }
    </div>

    @if (rec(); as r) {
      <div class="mb-5 grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Books say</div><div class="text-xl font-semibold tabular-nums">{{ money(r.ledgerBalance) }}</div></div>
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">Bank says</div><div class="text-xl font-semibold tabular-nums">{{ r.statementBalance == null ? '—' : money(r.statementBalance) }}</div></div>
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">In books, not on the statement</div><div class="text-xl font-semibold tabular-nums" [class.text-amber-600]="r.unmatchedLedger !== 0">{{ money(r.unmatchedLedger) }}</div></div>
        <div class="rounded-2xl border border-slate-200 bg-white p-4 dark:border-slate-800 dark:bg-slate-900"><div class="text-xs uppercase tracking-wide text-slate-500">On the statement, not in books</div><div class="text-xl font-semibold tabular-nums" [class.text-amber-600]="r.unmatchedStatement !== 0">{{ money(r.unmatchedStatement) }}</div></div>
        <div class="rounded-2xl border p-4" [class]="r.difference == null ? 'border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900' : r.difference === 0 ? 'border-emerald-300 bg-emerald-50 dark:border-emerald-800 dark:bg-emerald-900/20' : 'border-red-300 bg-red-50 dark:border-red-800 dark:bg-red-900/20'">
          <div class="text-xs uppercase tracking-wide text-slate-500">Difference</div>
          <div class="text-xl font-semibold tabular-nums">{{ r.difference == null ? 'Enter the balance' : r.difference === 0 ? 'Reconciled' : money(r.difference) }}</div></div>
      </div>

      <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
        <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" [ngModel]="showMatched()" (ngModelChange)="showMatched.set($event)" /> Show matched lines too</label>
        <button class="btn" (click)="autoMatch()" [disabled]="busy() || !r.suggestions.length"><app-icon name="bolt" size="h-4 w-4" />Match {{ r.suggestions.length }} suggested {{ r.suggestions.length === 1 ? 'pair' : 'pairs' }}</button>
      </div>

      <div class="grid gap-5 xl:grid-cols-2">
        <section class="card p-0">
          <header class="border-b border-slate-100 px-5 py-3 dark:border-slate-800"><h2 class="text-base font-semibold">Bank statement</h2><p class="text-xs text-slate-500">What the bank says happened</p></header>
          <ul class="divide-y divide-slate-100 dark:divide-slate-800">
            @for (s of statementRows(); track s.id) {
              <li class="px-5 py-3" [class.bg-emerald-50]="s.matched">
                <div class="flex items-start gap-3">
                  <div class="min-w-0 flex-1"><div class="truncate text-sm font-medium">{{ s.description }}</div><div class="text-xs text-slate-500">{{ s.date | date: 'd MMM y' }}{{ s.reference ? ' · ' + s.reference : '' }}</div></div>
                  <div class="text-right text-sm font-semibold tabular-nums" [class.text-emerald-700]="s.amount > 0" [class.text-red-700]="s.amount < 0">{{ money(s.amount) }}</div>
                </div>
                <div class="mt-2 flex flex-wrap items-center gap-2 text-xs">
                  @if (s.matched) { <span class="badge badge-ok">Matched</span><button class="btn-ghost btn-sm" (click)="unmatch(s)">Un-match</button> }
                  @else {
                    @if (suggestion(s); as sg) { <span class="text-slate-500">Looks like <strong>{{ sg.number }}</strong> {{ sg.date | date: 'd MMM' }}</span><button class="btn btn-sm" (click)="match(s, sg.journalLineId)">Match</button> }
                    @else if (candidates(s).length) { <select class="input h-8 w-auto py-0 text-xs" (change)="pick(s, $any($event.target).value)"><option value="">Match with…</option>@for (c of candidates(s); track c.journalLineId) { <option [value]="c.journalLineId">{{ c.number }} · {{ c.date | date: 'd MMM' }} · {{ c.description }}</option> }</select> }
                    <button class="btn-ghost btn-sm" (click)="createFor(s)">Record in books</button>
                    <button class="icon-btn h-7 w-7" (click)="removeLine(s)" aria-label="Delete statement line"><app-icon name="trash" size="h-3.5 w-3.5" /></button>
                  }
                </div>
              </li>
            } @empty { <li class="px-5 py-8 text-center text-sm text-slate-500">{{ r.statementLines.length ? 'Every statement line is matched.' : 'No statement lines yet. Import your statement as CSV.' }}</li> }
          </ul>
        </section>

        <section class="card p-0">
          <header class="border-b border-slate-100 px-5 py-3 dark:border-slate-800"><h2 class="text-base font-semibold">Your books</h2><p class="text-xs text-slate-500">What {{ r.account.code }} · {{ r.account.name }} says happened</p></header>
          <ul class="divide-y divide-slate-100 dark:divide-slate-800">
            @for (l of ledgerRows(); track l.journalLineId) {
              <li class="flex items-start gap-3 px-5 py-3" [class.opacity-60]="l.matched">
                <div class="min-w-0 flex-1"><div class="truncate text-sm font-medium">{{ l.description }}</div><div class="text-xs text-slate-500"><span class="font-mono">{{ l.number }}</span> · {{ l.date | date: 'd MMM y' }}{{ l.reference ? ' · ' + l.reference : '' }}</div></div>
                @if (l.matched) { <span class="badge badge-ok">Matched</span> }
                <div class="w-24 text-right text-sm font-semibold tabular-nums" [class.text-emerald-700]="l.amount > 0" [class.text-red-700]="l.amount < 0">{{ money(l.amount) }}</div>
              </li>
            } @empty { <li class="px-5 py-8 text-center text-sm text-slate-500">Nothing left unmatched in your books.</li> }
          </ul>
        </section>
      </div>
    }

    @if (importOpen()) {
      <app-modal title="Import a bank statement" subtitle="Paste the CSV or choose the file. The first row names the columns: Date, Description and Amount (or Debit and Credit)." width="max-w-2xl" (closed)="importOpen.set(false)">
        <label class="btn-ghost btn-sm mb-3 inline-flex cursor-pointer"><app-icon name="upload" size="h-4 w-4" />Choose a CSV file<input type="file" class="sr-only" accept=".csv,text/csv,text/plain" (change)="readFile($any($event.target))" /></label>
        <textarea class="input min-h-56 font-mono text-xs" [ngModel]="csv()" (ngModelChange)="csv.set($event)" placeholder="Date,Description,Amount&#10;2026-10-01,DEPOSIT,100.00&#10;2026-10-03,SERVICE FEE,-5.00"></textarea>
        <p class="hint">Dates may be 2026-10-05 or 05/10/2026 (day first). Importing the same statement twice adds nothing.</p>
        @if (importError()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ importError() }}</p> }
        <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="importOpen.set(false)">Cancel</button><button class="btn" (click)="importCsv()" [disabled]="busy() || !csv().trim()">Import</button></div>
      </app-modal>
    }

    @if (addOpen()) {
      <app-modal title="Add a statement line" width="max-w-md" (closed)="addOpen.set(false)">
        <form (ngSubmit)="addLine()"><div class="grid gap-4 sm:grid-cols-2">
          <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="line.date" required /></label>
          <label class="block"><span class="label">Amount</span><input type="number" step="0.01" class="input text-right" name="a" [(ngModel)]="line.amount" required placeholder="negative = money out" /></label>
          <label class="block sm:col-span-2"><span class="label">Description</span><input class="input" name="m" [(ngModel)]="line.description" required maxlength="200" /></label></div>
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="addOpen.set(false)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !line.amount">Add</button></div></form>
      </app-modal>
    }

    @if (creating(); as s) {
      <app-modal title="Record this line in your books" [subtitle]="s.description + ' · ' + money(s.amount)" width="max-w-md" (closed)="creating.set(null)">
        <form (ngSubmit)="create()">
          <label class="block"><span class="label">{{ s.amount > 0 ? 'Received for' : 'Paid for' }}</span>
            <select class="input" name="c" [(ngModel)]="counter" required><option value="">Choose an account…</option>@for (a of store.postable(); track a.id) { @if (a.isActive && a.id !== accountId()) { <option [value]="a.id">{{ a.code }} · {{ a.name }}</option> } }</select></label>
          <label class="mt-3 block"><span class="label">Description</span><input class="input" name="m" [(ngModel)]="memo" maxlength="200" /></label>
          <p class="hint">An entry is posted to your books and matched with this line. For a bank fee choose Bank charges.</p>
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="creating.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !counter">Record and match</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class ReconcileComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly money = money;
  readonly accountId = signal('');
  readonly asAt = signal(isoDate(new Date()));
  readonly balance = signal<number | null>(null);
  readonly rec = signal<Reconciliation | null>(null);
  readonly busy = signal(false);
  readonly showMatched = signal(false);
  readonly importOpen = signal(false);
  readonly addOpen = signal(false);
  readonly creating = signal<StatementLine | null>(null);
  readonly csv = signal('');
  readonly importError = signal('');
  line = { date: isoDate(new Date()), description: '', amount: null as number | null };
  counter = ''; memo = '';

  readonly accounts = computed(() => (this.store.meta()?.cashAccounts ?? []).filter(a => !!a.bankAccountId || a.role === 'bank'));
  readonly statementRows = computed(() => (this.rec()?.statementLines ?? []).filter(s => this.showMatched() || !s.matched));
  readonly ledgerRows = computed(() => (this.rec()?.ledgerLines ?? []).filter(l => this.showMatched() || !l.matched));

  suggestion(s: StatementLine): LedgerLine | null {
    const id = this.rec()?.suggestions.find(x => x.statementLineId === s.id)?.journalLineId;
    return id ? this.rec()?.ledgerLines.find(l => l.journalLineId === id) ?? null : null;
  }
  candidates(s: StatementLine): LedgerLine[] { return (this.rec()?.ledgerLines ?? []).filter(l => !l.matched && l.amount === s.amount); }

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); } catch (e) { this.toast.err(e); return; }
    this.accountId.set((this.accounts().find(a => !!a.bankAccountId) ?? this.accounts()[0])?.id ?? '');   // a real bank account first, the general "Bank" ledger only if there is none
    if (this.accountId()) await this.load();
  }

  async load(): Promise<void> {
    if (!this.accountId()) return;
    try { this.rec.set(await this.api.get<Reconciliation>('/api/v1/accounting/bank/reconciliation', { accountId: this.accountId(), asAt: this.asAt(), statementBalance: this.balance() ?? undefined })); }
    catch (e) { this.toast.err(e); }
  }

  private async run(work: () => Promise<unknown>, ok?: string): Promise<boolean> {
    this.busy.set(true);
    try { await work(); if (ok) this.toast.ok(ok); await this.load(); return true; } catch (e) { this.toast.err(e); return false; } finally { this.busy.set(false); }
  }

  match(s: StatementLine, journalLineId: string): Promise<boolean> { return this.run(() => this.api.post('/api/v1/accounting/bank/match', { statementLineId: s.id, journalLineId }), 'Matched.'); }
  pick(s: StatementLine, id: string): void { if (id) void this.match(s, id); }
  unmatch(s: StatementLine): Promise<boolean> { return this.run(() => this.api.post('/api/v1/accounting/bank/unmatch/' + s.id)); }
  async autoMatch(): Promise<void> { await this.run(async () => { const r = await this.api.post<{ matched: number }>('/api/v1/accounting/bank/auto-match', { accountId: this.accountId() }); this.toast.ok(r.matched + ' pair(s) matched.'); }); }

  async removeLine(s: StatementLine): Promise<void> {
    if (!(await this.dialog.confirm('Delete this statement line?', 'Delete', true))) return;
    await this.run(() => this.api.delete('/api/v1/accounting/bank/statement/' + s.id));
  }

  readFile(input: HTMLInputElement): void {
    const f = input.files?.[0]; input.value = '';
    if (!f) return;
    if (f.size > 1_500_000) { this.importError.set('That file is too large. Import it a month at a time.'); return; }
    void f.text().then(t => { this.csv.set(t); this.importError.set(''); });
  }

  async importCsv(): Promise<void> {
    this.importError.set('');
    this.busy.set(true);
    try {
      const r = await this.api.post<{ imported: number; duplicates: number; skipped: number; problems: string[] }>('/api/v1/accounting/bank/statement/import', { accountId: this.accountId(), csv: this.csv() });
      this.toast.ok(r.imported + ' line(s) imported' + (r.duplicates ? ', ' + r.duplicates + ' already there' : '') + (r.skipped ? ', ' + r.skipped + ' skipped' : '') + '.');
      if (r.problems.length) this.importError.set(r.problems.join(' '));
      else { this.importOpen.set(false); this.csv.set(''); }
      await this.load();
    } catch (e) { this.importError.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async addLine(): Promise<void> {
    const ok = await this.run(() => this.api.post('/api/v1/accounting/bank/statement/line', { accountId: this.accountId(), date: this.line.date, description: this.line.description.trim(), reference: null, amount: Number(this.line.amount) }), 'Line added.');
    if (ok) { this.addOpen.set(false); this.line = { date: isoDate(new Date()), description: '', amount: null }; }
  }

  createFor(s: StatementLine): void { this.counter = ''; this.memo = s.description; this.creating.set(s); }

  async create(): Promise<void> {
    const s = this.creating(); if (!s) return;
    const ok = await this.run(() => this.api.post('/api/v1/accounting/bank/create-entry', { statementLineId: s.id, counterAccountId: this.counter, memo: this.memo.trim() || null }), 'Recorded and matched.');
    if (ok) this.creating.set(null);
  }
}
