import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { isoDate } from '../../core/accounting.store';
import { ToastService } from '../../core/ui.services';
import { ExportDoc, ExportMenuComponent } from '../../shared/export-menu.component';
import { PageHeaderComponent } from '../../shared/ui.components';

interface Row { id: number; at: string; user?: string | null; action: string; what: string; detail?: string | null; ipAddress?: string | null }

/** Every change made to the books: who, when, from where, and what. Posted entries are never edited, so this and the journals tell the whole story. */
@Component({
  selector: 'app-books-audit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, PageHeaderComponent, ExportMenuComponent],
  template: `
    <app-page-header title="Audit trail" subtitle="Who changed the books, when and from where: journals, invoices, bills, payments, accounts, periods and settings.">
      <app-export-menu [build]="exportDoc" [disabled]="!rows().length" />
    </app-page-header>
    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event)" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="to()" (ngModelChange)="to.set($event)" /></label>
        <label class="block"><span class="label">User</span><input class="input" placeholder="Name or email" [ngModel]="user()" (ngModelChange)="user.set($event)" (keyup.enter)="load()" /></label>
        <label class="block"><span class="label">Search</span><input class="input" placeholder="Number, amount, account" [ngModel]="q()" (ngModelChange)="q.set($event)" (keyup.enter)="load()" /></label>
        <div class="flex items-end"><button class="btn w-full" (click)="load()">Show</button></div>
      </div>
    </div>
    <div class="card p-0"><div class="overflow-x-auto"><table class="table w-full text-sm">
      <thead><tr><th class="w-40">When</th><th>Who</th><th>What</th><th>Detail</th><th>From</th></tr></thead>
      <tbody>
        @for (r of rows(); track r.id) {
          <tr><td class="whitespace-nowrap tabular-nums">{{ r.at | date: 'd MMM y, HH:mm' }}</td><td>{{ r.user || '—' }}</td><td><span class="font-medium">{{ r.what }}</span><div class="font-mono text-[11px] text-slate-400">{{ r.action }}</div></td>
            <td class="max-w-md break-words">{{ r.detail }}</td><td class="font-mono text-xs text-slate-500">{{ r.ipAddress }}</td></tr>
        } @empty { <tr><td colspan="5" class="py-6 text-center text-slate-500">{{ loaded() ? 'Nothing in this period.' : 'Loading…' }}</td></tr> }
      </tbody>
    </table></div></div>
  `,
})
export class BooksAuditComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly rows = signal<Row[]>([]);
  readonly loaded = signal(false);
  readonly from = signal(isoDate(new Date(new Date().getFullYear(), new Date().getMonth() - 1, 1)));
  readonly to = signal(isoDate(new Date()));
  readonly user = signal('');
  readonly q = signal('');

  async ngOnInit(): Promise<void> { await this.load(); }
  async load(): Promise<void> {
    try { this.rows.set(await this.api.get<Row[]>('/api/v1/accounting/audit', { from: this.from(), to: this.to(), user: this.user(), q: this.q(), take: 1000 })); this.loaded.set(true); } catch (e) { this.toast.err(e); }
  }
  readonly exportDoc = (): ExportDoc => ({
    title: 'Audit trail of the books', subtitle: `${this.from()} to ${this.to()}`, landscape: true,
    columns: [{ header: 'When' }, { header: 'Who' }, { header: 'What' }, { header: 'Detail' }, { header: 'From' }],
    rows: this.rows().map(r => ({ cells: [r.at.replace('T', ' ').slice(0, 16), r.user ?? '', r.what, r.detail ?? '', r.ipAddress ?? ''] })),
  });
}
