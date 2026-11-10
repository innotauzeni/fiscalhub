import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { AccountingStore, ChartSettings, LedgerAccount } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { ExportDoc, ExportMenuComponent } from '../../shared/export-menu.component';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Form {
  id?: string; code: string; name: string; type: string; isActive: boolean; locked: boolean; parentId: string | null; isGroup: boolean; subType: string; description: string;
  hasPostings: boolean; system: boolean; suggestion: string; suggestionProblem: string;
}
interface RenumberRow { id: string; name: string; type: string; oldCode: string; newCode: string }
interface RenumberPreview { rows: RenumberRow[]; problems: string[]; changed: number }
interface ImportResult { created: number; updated: number; skipped: number; problems: string[] }

const TYPES = [
  { type: 'Asset', title: 'Assets', hint: 'What the company owns or is owed' },
  { type: 'Liability', title: 'Liabilities', hint: 'What the company owes' },
  { type: 'Equity', title: 'Equity', hint: 'The owners\' stake' },
  { type: 'Income', title: 'Income', hint: 'Sales and other earnings' },
  { type: 'Expense', title: 'Expenses', hint: 'What it costs to run the business' },
];
const FORMATS = ['9999', '99999', '999999', '99-9999', '9-999-99', '9.99.999'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

/**
 * The chart of accounts as a tree: headings group accounts, sub-accounts roll up into their parents in every report, and each account says where it appears in the
 * statements. The company chooses how codes look (digits, groups, first digit per type); codes are suggested in that format, and the whole chart can be renumbered or imported.
 */
@Component({
  selector: 'app-chart-of-accounts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Chart of accounts" subtitle="Your accounts as a tree. Sub-accounts add up into their parent in every report; headings only group others.">
      <app-export-menu [build]="exportDoc" />
      @if (canManage) {
        <button class="btn-ghost" (click)="openImport()"><app-icon name="upload" size="h-4 w-4" />Import</button>
        <button class="btn-ghost" (click)="openNumbering()"><app-icon name="layers" size="h-4 w-4" />Numbering</button>
        <button class="btn" (click)="start(null, 'Expense')"><app-icon name="plus" size="h-4 w-4" />New account</button>
      }
    </app-page-header>

    <div class="card mb-5">
      <div class="flex flex-wrap items-end gap-3">
        <label class="block min-w-0 flex-1 basis-56"><span class="label">Search</span>
          <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
            <input class="input pl-9" placeholder="Code, name or classification…" [ngModel]="q()" (ngModelChange)="q.set($event)" /></span></label>
        <div class="flex flex-wrap gap-1">
          <button type="button" class="rounded-full px-3 py-1 text-sm" [class]="typeFilter() === '' ? 'bg-brand-600 text-white' : 'bg-slate-100 dark:bg-slate-800'" (click)="typeFilter.set('')">All</button>
          @for (t of types; track t.type) {
            <button type="button" class="rounded-full px-3 py-1 text-sm" [class]="typeFilter() === t.type ? 'bg-brand-600 text-white' : 'bg-slate-100 dark:bg-slate-800'" (click)="typeFilter.set(t.type)">{{ t.title }}</button>
          }
        </div>
        <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" [ngModel]="showOff()" (ngModelChange)="showOff.set($event)" />Show switched off</label>
        <button type="button" class="btn-ghost" (click)="expandAll()">Expand all</button>
        <button type="button" class="btn-ghost" (click)="collapseAll()">Collapse all</button>
      </div>
      @if (store.setup(); as s) {
        <p class="hint mt-2">Codes: {{ s.example }}{{ s.settings.enforceCodeFormat ? ' (enforced)' : '' }} · Financial year from {{ months[s.settings.fiscalYearStartMonth - 1] }}</p>
      }
    </div>

    @for (t of shownTypes(); track t.type) {
      <section class="card mb-5 p-0">
        <header class="flex items-center justify-between gap-2 border-b border-slate-100 px-5 py-3 dark:border-slate-800">
          <div><h2 class="text-base font-semibold">{{ t.title }}</h2><p class="text-xs text-slate-500">{{ t.hint }}</p></div>
          @if (canManage) { <button class="btn-ghost text-sm" (click)="start(null, t.type)"><app-icon name="plus" size="h-4 w-4" />Add</button> }
        </header>
        <div class="overflow-x-auto">
          <table class="table w-full text-sm">
            <thead><tr><th class="w-40">Code</th><th>Account</th><th class="hidden md:table-cell">Appears under</th><th></th></tr></thead>
            <tbody>
              @for (a of rows(t.type); track a.id) {
                <tr [class.opacity-60]="!a.isActive">
                  <td class="whitespace-nowrap font-mono text-xs">
                    <span class="inline-flex items-center" [style.padding-left.rem]="searching() ? 0 : (a.level ?? 0) * 1.1">
                      @if (a.hasChildren && !searching()) {
                        <button type="button" class="icon-btn -ml-1 mr-0.5 h-6! w-6!" (click)="toggle(a.id)" [attr.aria-label]="collapsed().has(a.id) ? 'Expand' : 'Collapse'">
                          <app-icon [name]="collapsed().has(a.id) ? 'chevron-right' : 'chevron'" size="h-3.5 w-3.5" /></button>
                      } @else { <span class="inline-block w-6"></span> }
                      {{ a.code }}</span></td>
                  <td>
                    <div class="font-medium" [class.uppercase]="a.isGroup" [class.text-xs]="a.isGroup" [class.tracking-wide]="a.isGroup">{{ a.name }}</div>
                    <div class="flex flex-wrap gap-1.5 text-[11px]">
                      @if (a.isGroup) { <span class="badge">Heading</span> }
                      @if (a.bankAccountId) { <span class="badge badge-info">Bank account</span> } @else if (a.isSystem) { <span class="text-slate-400">Standard</span> }
                      @if (a.role && !a.bankAccountId) { <span class="text-slate-400">· posted automatically</span> }
                      @if (!a.isActive) { <span class="badge badge-warn">Switched off</span> }
                      @if (a.description) { <span class="text-slate-400">· {{ a.description }}</span> }
                    </div>
                  </td>
                  <td class="hidden text-slate-500 md:table-cell">{{ store.subTypeLabel(a.subType) }}</td>
                  <td class="whitespace-nowrap text-right">
                    @if (canManage) {
                      <button class="icon-btn" (click)="start(a, a.type)" title="Add a sub-account" aria-label="Add a sub-account"><app-icon name="plus" size="h-4 w-4" /></button>
                      <button class="icon-btn" (click)="edit(a)" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                      @if (!a.isSystem && !a.bankAccountId && !a.inUse && !a.hasChildren) { <button class="icon-btn icon-btn-danger" (click)="remove(a)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button> }
                    }
                  </td>
                </tr>
              } @empty { <tr><td colspan="4" class="py-5 text-center text-slate-500">Nothing here.</td></tr> }
            </tbody>
          </table>
        </div>
      </section>
    }

    <!-- an account -->
    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit account' : 'New account'" width="max-w-xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Type</span>
              <select class="input" name="type" [ngModel]="f.type" (ngModelChange)="setType($event)" [disabled]="f.locked">@for (t of types; track t.type) { <option [value]="t.type">{{ t.title }}</option> }</select></label>
            <label class="block"><span class="label">Sits under</span>
              <select class="input" name="parent" [ngModel]="f.parentId" (ngModelChange)="setParent($event)">
                <option [ngValue]="null">Nothing (a top account)</option>
                @for (p of parents(); track p.id) { <option [ngValue]="p.id">{{ '  '.repeat(p.level ?? 0) }}{{ p.code }} · {{ p.name }}{{ p.isGroup ? ' (heading)' : '' }}</option> }
              </select></label>
            <label class="block"><span class="label">Code</span>
              <span class="flex gap-2"><input class="input font-mono" name="code" [(ngModel)]="f.code" required maxlength="20" [placeholder]="f.suggestion || '5125'" />
                @if (f.suggestion && f.code !== f.suggestion) { <button type="button" class="btn-ghost whitespace-nowrap" (click)="f.code = f.suggestion">Use {{ f.suggestion }}</button> }</span>
              @if (f.suggestionProblem) { <span class="hint">{{ f.suggestionProblem }}</span> }</label>
            <label class="block"><span class="label">Name</span><input class="input" name="name" [(ngModel)]="f.name" required maxlength="100" placeholder="Water and rates" /></label>
            <label class="block sm:col-span-2"><span class="label">Appears in the statements under</span>
              <select class="input" name="sub" [(ngModel)]="f.subType">@for (s of subTypesOf(f.type); track s.key) { <option [value]="s.key">{{ s.label }}</option> }</select></label>
            <label class="block sm:col-span-2"><span class="label">Description (optional)</span><input class="input" name="desc" [(ngModel)]="f.description" maxlength="300" placeholder="What goes in this account" /></label>
            <label class="flex items-start gap-2 text-sm sm:col-span-2"><input type="checkbox" class="mt-0.5 h-4 w-4" name="group" [(ngModel)]="f.isGroup" [disabled]="f.hasPostings || f.system" />
              <span><strong>Heading</strong> — only groups the accounts under it; nothing is posted to it.@if (f.hasPostings || f.system) { <span class="block text-xs text-slate-500">Not possible: it has postings or the system posts to it.</span> }</span></label>
            <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" class="h-4 w-4" name="active" [(ngModel)]="f.isActive" /> In use (switch off to hide it from new entries)</label>
          </div>
          @if (f.locked) { <p class="hint">Standard and bank accounts keep their type. You can rename, renumber, move or switch them off.</p> }
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }

    <!-- numbering -->
    @if (rules(); as r) {
      <app-modal title="Account numbering" width="max-w-3xl" (closed)="rules.set(null)">
        <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Choose how your account codes look. Write <strong>9</strong> for each digit and <strong>-</strong>, <strong>.</strong> or <strong>/</strong> between groups.
          New codes are suggested in this format, under their parent. To change the codes you already have, preview and renumber below.</p>
        <div class="grid gap-4 sm:grid-cols-3">
          <label class="block"><span class="label">Code format</span><input class="input font-mono" [(ngModel)]="r.codeFormat" maxlength="20" placeholder="9999" />
            <span class="mt-1 flex flex-wrap gap-1">@for (x of formats; track x) { <button type="button" class="rounded bg-slate-100 px-1.5 py-0.5 font-mono text-[11px] dark:bg-slate-800" (click)="r.codeFormat = x">{{ x }}</button> }</span></label>
          <label class="block"><span class="label">Gap between suggested codes</span><input class="input" type="number" min="1" [(ngModel)]="r.codeStep" />
            <span class="hint">10 gives 1010, 1020, 1030…</span></label>
          <label class="block"><span class="label">Financial year starts in</span>
            <select class="input" [(ngModel)]="r.fiscalYearStartMonth">@for (m of months; track $index) { <option [ngValue]="$index + 1">{{ m }}</option> }</select></label>
        </div>
        <div class="mt-4"><span class="label">First digit(s) of each type</span>
          <div class="grid grid-cols-2 gap-3 sm:grid-cols-5">
            @for (t of types; track t.type) { <label class="block"><span class="text-xs text-slate-500">{{ t.title }}</span><input class="input font-mono" [(ngModel)]="r.classStarts[t.type]" maxlength="4" inputmode="numeric" /></label> }
          </div></div>
        <label class="mt-4 flex items-start gap-2 text-sm"><input type="checkbox" class="mt-0.5 h-4 w-4" [(ngModel)]="r.enforceCodeFormat" />
          <span><strong>Enforce the format</strong> — new and changed codes must follow it and start with their type's digits.</span></label>
        <p class="hint mt-2">Example: {{ example(r) }}</p>

        @if (preview(); as p) {
          <div class="mt-4 rounded-lg border border-slate-200 p-3 dark:border-slate-700">
            <div class="mb-2 text-sm font-medium">{{ p.changed }} account code(s) would change</div>
            @for (x of p.problems; track $index) { <p class="text-sm text-red-600">{{ x }}</p> }
            <div class="max-h-64 overflow-auto"><table class="table w-full text-xs">
              <thead><tr><th>Account</th><th>Now</th><th>Becomes</th></tr></thead>
              <tbody>@for (x of p.rows; track x.id) { @if (x.oldCode !== x.newCode) { <tr><td>{{ x.name }}</td><td class="font-mono">{{ x.oldCode }}</td><td class="font-mono font-semibold">{{ x.newCode }}</td></tr> } }</tbody>
            </table></div>
          </div>
        }
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
        <div class="mt-6 flex flex-wrap justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
          <button class="btn-ghost" type="button" (click)="rules.set(null)">Close</button>
          <button class="btn-ghost" type="button" [disabled]="busy()" (click)="previewRenumber()">Preview renumbering</button>
          @if (preview(); as p) { @if (!p.problems.length && p.changed) { <button class="btn-ghost text-amber-700" type="button" [disabled]="busy()" (click)="renumber()">Renumber {{ p.changed }} account(s)</button> } }
          <button class="btn" type="button" [disabled]="busy()" (click)="saveRules()">Save format only</button>
        </div>
      </app-modal>
    }

    <!-- import -->
    @if (importing()) {
      <app-modal title="Import a chart of accounts" width="max-w-2xl" (closed)="importing.set(false)">
        <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">A CSV file with the columns <code class="code">code, name, type, sub-type, parent code, heading, description</code>. Accounts are matched by code:
          existing ones are updated, new ones created. Type is Asset, Liability, Equity, Income or Expense; heading is yes or no.</p>
        <div class="mb-3 flex flex-wrap gap-2">
          <label class="btn-ghost cursor-pointer"><app-icon name="upload" size="h-4 w-4" />Choose file<input type="file" accept=".csv,text/csv,text/plain" class="hidden" (change)="readFile($event)" /></label>
          <button type="button" class="btn-ghost" (click)="template()"><app-icon name="download" size="h-4 w-4" />Template</button>
        </div>
        <textarea class="input font-mono text-xs" rows="10" [(ngModel)]="csv" placeholder="code,name,type,subtype,parent,heading,description&#10;5200,Motor vehicle expenses,Expense,operating_expense,,yes,&#10;5210,Fuel,Expense,,5200,,"></textarea>
        @if (importResult(); as r) {
          <div class="mt-3 rounded-lg bg-slate-50 p-3 text-sm dark:bg-slate-800/50">
            <div>{{ r.created }} created, {{ r.updated }} updated, {{ r.skipped }} skipped.</div>
            @for (x of r.problems; track $index) { <div class="text-xs text-amber-700 dark:text-amber-300">{{ x }}</div> }
          </div>
        }
        <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
          <button class="btn-ghost" type="button" (click)="importing.set(false)">Close</button>
          <button class="btn" type="button" [disabled]="busy() || !csv.trim()" (click)="runImport()">Import</button>
        </div>
      </app-modal>
    }
  `,
})
export class ChartOfAccountsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly store = inject(AccountingStore);
  readonly auth = inject(AuthService);
  readonly canManage = this.auth.can('accounting.manage');
  readonly types = TYPES;
  readonly formats = FORMATS;
  readonly months = MONTHS;
  readonly q = signal('');
  readonly typeFilter = signal('');
  readonly showOff = signal(true);
  readonly collapsed = signal(new Set<string>());
  readonly form = signal<Form | null>(null);
  readonly rules = signal<ChartSettings | null>(null);
  readonly preview = signal<RenumberPreview | null>(null);
  readonly importing = signal(false);
  readonly importResult = signal<ImportResult | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  csv = '';

  readonly searching = computed(() => this.q().trim().length > 0);
  readonly shownTypes = computed(() => TYPES.filter(t => !this.typeFilter() || t.type === this.typeFilter()));
  private readonly byId = computed(() => new Map(this.store.accounts().map(a => [a.id, a])));

  /** The accounts of a type in tree order, hiding those under a collapsed parent (or, when searching, the matches). */
  rows(type: string): LedgerAccount[] {
    const q = this.q().trim().toLowerCase(); const off = this.showOff(); const closed = this.collapsed(); const byId = this.byId();
    return this.store.accounts().filter(a => {
      if (a.type !== type || (!off && !a.isActive)) return false;
      if (q) return (a.code + ' ' + a.name + ' ' + this.store.subTypeLabel(a.subType) + ' ' + (a.description ?? '')).toLowerCase().includes(q);
      for (let p = a.parentId ? byId.get(a.parentId) : undefined, n = 0; p && n < 12; p = p.parentId ? byId.get(p.parentId) : undefined, n++) if (closed.has(p.id)) return false;
      return true;
    });
  }

  toggle(id: string): void { const s = new Set(this.collapsed()); if (s.has(id)) s.delete(id); else s.add(id); this.collapsed.set(s); }
  expandAll(): void { this.collapsed.set(new Set()); }
  collapseAll(): void { this.collapsed.set(new Set(this.store.accounts().filter(a => a.hasChildren).map(a => a.id))); }

  subTypesOf(type: string) { return (this.store.setup()?.subTypes ?? []).filter(s => s.type === type); }

  /** The accounts this one may sit under: the same type, never itself or anything below it. */
  readonly parents = computed(() => {
    const f = this.form(); if (!f) return [];
    const below = new Set<string>(); if (f.id) { below.add(f.id); let grew = true; while (grew) { grew = false; for (const a of this.store.accounts()) if (a.parentId && below.has(a.parentId) && !below.has(a.id)) { below.add(a.id); grew = true; } } }
    return this.store.accounts().filter(a => a.type === f.type && !below.has(a.id));
  });

  async ngOnInit(): Promise<void> { try { await this.store.load(); await this.store.loadSetup(); } catch (e) { this.toast.err(e); } }

  private defaultSubType(type: string, parent?: LedgerAccount | null): string {
    if (parent?.subType && parent.type === type) return parent.subType;
    return ({ Asset: 'current_asset', Liability: 'current_liability', Equity: 'equity', Income: 'revenue', Expense: 'operating_expense' } as Record<string, string>)[type];
  }

  private async suggest(): Promise<void> {
    const f = this.form(); if (!f) return;
    try {
      const r = await this.api.get<{ code: string | null; problem: string | null }>('/api/v1/accounting/chart/next-code', { type: f.type, parentId: f.parentId ?? '' });
      const cur = this.form(); if (!cur) return;
      const blank = !cur.id && (!cur.code || cur.code === cur.suggestion);
      this.form.set({ ...cur, suggestion: r.code ?? '', suggestionProblem: r.problem ?? '', code: blank ? (r.code ?? cur.code) : cur.code });
    } catch { /* a suggestion is a convenience */ }
  }

  start(parent: LedgerAccount | null, type: string): void {
    this.error.set('');
    this.form.set({ code: '', name: '', type: parent?.type ?? type, isActive: true, locked: false, parentId: parent?.id ?? null, isGroup: false, subType: this.defaultSubType(parent?.type ?? type, parent),
      description: '', hasPostings: false, system: false, suggestion: '', suggestionProblem: '' });
    void this.suggest();
  }

  edit(a: LedgerAccount): void {
    this.error.set('');
    this.form.set({ id: a.id, code: a.code, name: a.name, type: a.type, isActive: a.isActive, locked: a.isSystem || !!a.bankAccountId, parentId: a.parentId ?? null, isGroup: !!a.isGroup,
      subType: a.subType ?? this.defaultSubType(a.type), description: a.description ?? '', hasPostings: a.inUse, system: !!a.role || !!a.bankAccountId, suggestion: '', suggestionProblem: '' });
  }

  setType(type: string): void { const f = this.form(); if (!f) return; this.form.set({ ...f, type, parentId: null, subType: this.defaultSubType(type) }); void this.suggest(); }
  setParent(id: string | null): void {
    const f = this.form(); if (!f) return;
    const p = id ? this.byId().get(id) : null;
    this.form.set({ ...f, parentId: id, subType: f.id ? f.subType : this.defaultSubType(f.type, p) });
    void this.suggest();
  }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.put('/api/v1/accounting/accounts', { id: f.id ?? null, code: f.code.trim(), name: f.name.trim(), type: f.type, isActive: f.isActive, parentId: f.parentId, isGroup: f.isGroup, subType: f.subType || null, description: f.description.trim() || null });
      this.form.set(null); this.toast.ok('Account saved.'); await this.store.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async remove(a: LedgerAccount): Promise<void> {
    if (!(await this.dialog.confirm('Delete the account ' + a.code + ' ' + a.name + '?', 'Delete', true))) return;
    try { await this.api.delete('/api/v1/accounting/accounts/' + a.id); this.toast.ok('Account deleted.'); await this.store.load(); } catch (e) { this.toast.err(e); }
  }

  // ---- numbering ----

  async openNumbering(): Promise<void> {
    this.error.set(''); this.preview.set(null);
    try { const s = (await this.store.loadSetup()).settings; this.rules.set({ ...s, classStarts: { ...s.classStarts } }); } catch (e) { this.toast.err(e); }
  }

  example(r: ChartSettings): string {
    const fmt = (start: string): string => { const digits = start.padEnd([...r.codeFormat].filter(c => c === '9' || c === '0').length, '0'); let i = 0; return [...r.codeFormat].map(c => (c === '9' || c === '0' ? digits[i++] ?? '0' : c)).join(''); };
    return TYPES.map(t => `${t.title} ${fmt(r.classStarts[t.type] ?? '')}`).join(', ');
  }

  private body(): ChartSettings { const r = this.rules()!; return { ...r, codeFormat: r.codeFormat.trim(), codeStep: Number(r.codeStep) || 10, fiscalYearStartMonth: Number(r.fiscalYearStartMonth) || 1 }; }

  async saveRules(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try { await this.api.put('/api/v1/accounting/chart/settings', this.body()); await this.store.loadSetup(); this.rules.set(null); this.toast.ok('Numbering saved. New codes follow it.'); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async previewRenumber(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try { this.preview.set(await this.api.post<RenumberPreview>('/api/v1/accounting/chart/renumber/preview', this.body())); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async renumber(): Promise<void> {
    const p = this.preview(); if (!p) return;
    if (!(await this.dialog.confirm(`Renumber ${p.changed} account(s)? Entries and reports keep working: only the codes change. Lists printed before will show the old codes.`, 'Renumber'))) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.post('/api/v1/accounting/chart/renumber', this.body()); await Promise.all([this.store.load(), this.store.loadSetup()]); this.rules.set(null); this.toast.ok('The chart is renumbered.'); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  // ---- import and export ----

  openImport(): void { this.csv = ''; this.importResult.set(null); this.importing.set(true); }

  readFile(ev: Event): void {
    const file = (ev.target as HTMLInputElement).files?.[0]; if (!file) return;
    if (file.size > 500_000) { this.toast.err(new Error('Keep the file under 500 KB.')); return; }
    const reader = new FileReader(); reader.onload = () => { this.csv = String(reader.result ?? ''); this.importResult.set(null); }; reader.readAsText(file);
  }

  async runImport(): Promise<void> {
    this.busy.set(true);
    try { const r = await this.api.post<ImportResult>('/api/v1/accounting/chart/import', { csv: this.csv }); this.importResult.set(r); await this.store.load(); this.toast.ok(`${r.created} created, ${r.updated} updated.`); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  private download(name: string, rows: unknown[][]): void {
    const esc = (v: unknown): string => { const s = v == null ? '' : String(v); return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
    const url = URL.createObjectURL(new Blob(['﻿' + rows.map(r => r.map(esc).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a'); a.href = url; a.download = name; document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }

  template(): void { this.download('chart-template.csv', [['code', 'name', 'type', 'subtype', 'parent', 'heading', 'description'], ['5200', 'Motor vehicle expenses', 'Expense', 'operating_expense', '', 'yes', ''], ['5210', 'Fuel', 'Expense', '', '5200', '', '']]); }

  readonly exportDoc = (): ExportDoc => {
    const byId = this.byId();
    return {
      title: 'Chart of accounts', landscape: true,
      columns: [{ header: 'Code' }, { header: 'Name' }, { header: 'Type' }, { header: 'Appears under' }, { header: 'Parent' }, { header: 'Heading' }, { header: 'Active' }],
      rows: this.store.accounts().map(a => ({ cells: [a.code, a.name, a.type, this.store.subTypeLabel(a.subType), a.parentId ? byId.get(a.parentId)?.code ?? '' : '', a.isGroup ? 'yes' : '', a.isActive ? 'yes' : 'no'],
        indent: a.level ?? 0, style: a.isGroup ? 'bold' as const : 'normal' as const })),
    };
  };

  /** The chart in the import layout, so it can be edited and imported again. */
  exportCsv(): void {
    const byId = this.byId();
    this.download('chart-of-accounts.csv', [['code', 'name', 'type', 'subtype', 'parent', 'heading', 'description', 'active'],
      ...this.store.accounts().map(a => [a.code, a.name, a.type, a.subType ?? '', a.parentId ? byId.get(a.parentId)?.code ?? '' : '', a.isGroup ? 'yes' : '', a.description ?? '', a.isActive ? 'yes' : 'no'])]);
  }
}
