import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent, num } from '../../shared/export-menu.component';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';
import { MoneyPage, RANGES, rangeDates } from './money.component';

const KINDS: Record<string, string> = { payment: 'Spend money', receipt: 'Receive money', transfer: 'Transfer' };

/** Drafts from spend money, receive money and transfers waiting to go into the books: review them, then post the chosen ones in one go. */
@Component({
  selector: 'app-post-to-ledger',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Post to ledger" subtitle="Cashbook drafts waiting to go into the books. Check them, tick the ones that are right and post them together.">
      <app-export-menu [build]="exportDoc" [disabled]="!page()?.count" />
      @if (canManage) { <button class="btn" (click)="postChosen()" [disabled]="busy() || !chosen().size"><app-icon name="check" size="h-4 w-4" />Post {{ chosen().size || '' }} selected</button> }
    </app-page-header>

    <div class="card mb-4 flex flex-wrap items-end gap-3">
      <label class="block"><span class="label">Status</span><select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); load()"><option value="Draft">Waiting (drafts)</option><option value="Posted">Posted</option><option value="Void">Void</option><option value="">All</option></select></label>
      <label class="block"><span class="label">Kind</span><select class="input" [ngModel]="kind()" (ngModelChange)="kind.set($event); load()"><option value="">All kinds</option>@for (k of kindKeys; track k) { <option [value]="k">{{ kinds[k] }}</option> }</select></label>
      <div class="flex flex-wrap gap-1">@for (r of ranges; track r.key) { <button type="button" class="rounded-full px-3 py-1 text-xs" [class]="range() === r.key ? 'bg-brand-600 text-white' : 'bg-slate-100 dark:bg-slate-800'" (click)="pick(r.key)">{{ r.label }}</button> }</div>
      <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event); range.set('custom')" /></label>
      <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event); range.set('custom')" /></label>
      <button class="btn-ghost" (click)="load()" [disabled]="busy()">Apply</button>
    </div>

    <div class="card p-0">
      <table class="table w-full text-sm">
        <thead><tr>
          <th class="w-8">@if (canManage && status() === 'Draft') { <input type="checkbox" class="h-4 w-4" [checked]="allChosen()" (change)="toggleAll()" aria-label="Select all" /> }</th>
          <th>Number</th><th>Kind</th><th>Date</th><th>Who / accounts</th><th>Entered by</th><th class="text-right">Amount</th><th>Status</th></tr></thead>
        <tbody>
          @for (d of page()?.items ?? []; track d.id) {
            <tr>
              <td>@if (canManage && d.status === 'Draft') { <input type="checkbox" class="h-4 w-4" [checked]="chosen().has(d.id)" (change)="toggle(d.id)" /> }</td>
              <td class="font-mono text-xs">{{ d.number }}</td><td>{{ kinds[d.kind] }}</td><td class="whitespace-nowrap">{{ d.date | date: 'd MMM y' }}</td>
              <td class="max-w-72 truncate text-xs">{{ d.contact ? d.contact + ' · ' : '' }}{{ d.accountName }}{{ d.toAccountName ? ' → ' + d.toAccountName : '' }}</td>
              <td class="text-xs text-slate-500">{{ d.createdBy }}<div>{{ d.createdAt | date: 'd MMM, HH:mm' }}</div></td>
              <td class="whitespace-nowrap text-right tabular-nums">{{ money(d.total) }} <span class="text-xs text-slate-500">{{ d.currency }}</span></td>
              <td><span class="badge" [class.badge-warn]="d.status === 'Draft'" [class.badge-ok]="d.status === 'Posted'">{{ d.status }}</span>@if (d.journalNumber) { <div class="font-mono text-[10px] text-slate-400">{{ d.journalNumber }}</div> }</td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="py-12 text-center text-slate-500"><app-icon name="check" size="h-8 w-8" class="mx-auto mb-2 text-slate-300" />
              <div class="font-medium">{{ status() === 'Draft' ? 'Nothing waiting to post' : 'Nothing matches' }}</div><div class="text-xs">Payments, receipts and transfers saved as drafts appear here.</div></td></tr>
          }
        </tbody>
      </table>
    </div>
    @if (problems().length) {
      <div class="mt-3 rounded-lg bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-100">
        <div class="font-medium">Not posted:</div>@for (p of problems(); track $index) { <div class="text-xs">{{ p }}</div> }
      </div>
    }
  `,
})
export class PostToLedgerComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly canManage = inject(AuthService).can('accounting.manage');
  readonly money = money;
  readonly kinds = KINDS;
  readonly kindKeys = Object.keys(KINDS);
  readonly ranges = RANGES;
  readonly status = signal('Draft');
  readonly kind = signal('');
  readonly range = signal('all');
  readonly from = signal('');
  readonly to = signal('');
  readonly page = signal<MoneyPage | null>(null);
  readonly chosen = signal(new Set<string>());
  readonly problems = signal<string[]>([]);
  readonly busy = signal(false);
  private readonly drafts = computed(() => (this.page()?.items ?? []).filter(d => d.status === 'Draft').map(d => d.id));
  readonly allChosen = computed(() => this.drafts().length > 0 && this.drafts().every(id => this.chosen().has(id)));

  ngOnInit(): Promise<void> { return this.load(); }

  pick(key: string): void { const [f, t] = rangeDates(key); this.range.set(key); this.from.set(f); this.to.set(t); void this.load(); }

  async load(): Promise<void> {
    this.busy.set(true);
    try { this.page.set(await this.api.get<MoneyPage>('/api/v1/accounting/money', { status: this.status(), kind: this.kind(), from: this.from(), to: this.to() })); this.chosen.set(new Set()); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  toggle(id: string): void { const s = new Set(this.chosen()); if (s.has(id)) s.delete(id); else s.add(id); this.chosen.set(s); }
  toggleAll(): void { this.chosen.set(this.allChosen() ? new Set() : new Set(this.drafts())); }

  async postChosen(): Promise<void> {
    const ids = [...this.chosen()];
    if (!(await this.dialog.confirm(`Post ${ids.length} document(s) to the books?`, 'Post'))) return;
    this.busy.set(true); this.problems.set([]);
    try {
      const r = await this.api.post<{ posted: number; problems: string[] }>('/api/v1/accounting/money/post-batch', { ids });
      this.problems.set(r.problems); this.toast.ok(`${r.posted} posted.`); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  readonly exportDoc = (): ExportDoc | null => {
    const p = this.page(); if (!p) return null;
    return {
      title: 'Post to ledger', subtitle: (this.status() || 'All') + (this.from() ? ` · ${this.from()} to ${this.to()}` : ''), landscape: true,
      columns: [{ header: 'Number' }, { header: 'Kind' }, { header: 'Date' }, { header: 'Who / accounts' }, { header: 'Entered by' }, { header: 'Status' }, { header: 'Currency' }, { header: 'Amount', kind: 'money' }],
      rows: p.items.map(d => ({ cells: [d.number, KINDS[d.kind], d.date.slice(0, 10), [d.contact, d.accountName, d.toAccountName].filter(Boolean).join(' · '), d.createdBy ?? '', d.status, d.currency, num(d.total)] })),
    };
  };
}
