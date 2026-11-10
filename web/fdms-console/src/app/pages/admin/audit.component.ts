import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { AuditPage, AuditRow, Client } from '../../core/models';
import { ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

/** The trail of who did what: people at the console, integrating systems (by API token) and the system itself. */
@Component({
  selector: 'app-audit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, PageHeaderComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="Audit log" subtitle="Who did what, to which record, and from where. Times are shown in your own timezone.">
      <button class="btn-ghost" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <label class="block lg:col-span-2"><span class="label">Search</span>
          <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
            <input class="input pl-9" placeholder="Action, details, person or record id…" [(ngModel)]="search" (keydown.enter)="reload()" /></span></label>
        <label class="block"><span class="label">Action</span>
          <select class="input" [(ngModel)]="action" (change)="reload()"><option value="">Any action</option>
            @for (c of categories(); track c) { <option [value]="c">All {{ c }} …</option> }
            @for (a of page()?.actions ?? []; track a) { <option [value]="a">{{ a }}</option> }</select></label>
        <label class="block"><span class="label">Done by</span>
          <select class="input" [(ngModel)]="actorType" (change)="reload()"><option value="">Anyone</option><option value="user">People</option><option value="token">Integrations (API tokens)</option><option value="system">The system</option></select></label>
        <label class="block"><span class="label">Name or email</span><input class="input" [(ngModel)]="actor" (keydown.enter)="reload()" placeholder="e.g. boss@acme.test or till 7" /></label>
        @if (!auth.user()?.clientId) {
          <label class="block"><span class="label">Client</span>
            <select class="input" [(ngModel)]="clientId" (change)="reload()"><option value="">All clients</option>@for (c of clients(); track c.id) { <option [value]="c.id">{{ c.name }}</option> }</select></label>
        }
        <label class="block"><span class="label">From</span><input type="date" class="input" [(ngModel)]="from" (change)="reload()" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [(ngModel)]="to" (change)="reload()" /></label>
        <div class="flex items-end gap-2"><button class="btn" (click)="reload()">Search</button><button class="btn-ghost" (click)="clear()">Clear</button></div>
      </div>
    </div>

    <div class="card">
      <div class="mb-3 flex items-center gap-3"><div class="card-title mb-0 flex items-center gap-2"><app-icon name="clipboard" size="h-4 w-4" />Activity</div><span class="badge">{{ page()?.total ?? 0 }} entries</span></div>
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>When</th><th>Who</th><th>Action</th><th>Record</th><th>Details</th>@if (!auth.user()?.clientId) { <th>Client</th> }<th>From</th><th></th></tr></thead>
          <tbody>
            @for (a of page()?.items ?? []; track a.id) {
              <tr class="cursor-pointer" (click)="open.set(a)">
                <td class="whitespace-nowrap">{{ a.at | date:'medium' }}</td>
                <td><div class="flex items-center gap-2">
                  <span class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-xs font-semibold" [class]="chip(a.actorType)"><app-icon [name]="icon(a.actorType)" size="h-4 w-4" /></span>
                  <span class="min-w-0"><span class="block max-w-52 truncate font-medium">{{ a.userName ?? (a.actorType === 'system' ? 'System' : 'Unknown') }}</span><span class="block text-xs text-slate-500">{{ label(a.actorType) }}</span></span></div></td>
                <td><span class="code">{{ a.action }}</span></td>
                <td>@if (a.entity) { <span class="badge">{{ a.entity }}</span> <span class="font-mono text-xs text-slate-400">{{ short(a.entityId) }}</span> }</td>
                <td class="max-w-sm truncate text-sm text-slate-600 dark:text-slate-300">{{ a.detail }}</td>
                @if (!auth.user()?.clientId) { <td>{{ a.clientName }}</td> }
                <td class="whitespace-nowrap font-mono text-xs text-slate-500">{{ a.ipAddress }}</td>
                <td class="text-right"><button class="icon-btn" (click)="open.set(a); $event.stopPropagation()" aria-label="Details"><app-icon name="eye" size="h-4 w-4" /></button></td>
              </tr>
            } @empty { <tr><td colspan="8"><app-empty icon="clipboard" title="Nothing matches" hint="Change the filters or the dates." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="pageNo()" [pageSize]="size" [total]="page()?.total ?? 0" (pageChange)="go($event)" />
    </div>

    @if (open(); as a) {
      <app-modal title="Audit entry" [subtitle]="(a.at | date:'full') ?? ''" width="max-w-2xl" (closed)="open.set(null)">
        <dl class="grid gap-x-6 gap-y-4 text-sm sm:grid-cols-2">
          <div><dt class="label">Done by</dt><dd class="font-medium">{{ a.userName ?? 'System' }} <span class="badge">{{ label(a.actorType) }}</span></dd></div>
          <div><dt class="label">Action</dt><dd><span class="code">{{ a.action }}</span></dd></div>
          <div><dt class="label">Record</dt><dd>{{ a.entity ?? '—' }} <span class="font-mono text-xs text-slate-500">{{ a.entityId }}</span></dd></div>
          <div><dt class="label">From address</dt><dd class="font-mono">{{ a.ipAddress ?? '—' }}</dd></div>
          @if (a.clientName) { <div><dt class="label">Client</dt><dd>{{ a.clientName }}</dd></div> }
          <div><dt class="label">Time (UTC)</dt><dd class="font-mono text-xs">{{ a.at }}</dd></div>
          <div class="sm:col-span-2"><dt class="label">Details</dt><dd class="whitespace-pre-wrap rounded-lg bg-slate-50 p-3 dark:bg-slate-950">{{ a.detail || '—' }}</dd></div>
        </dl>
        <div class="mt-6 flex justify-end border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" (click)="open.set(null)">Close</button></div>
      </app-modal>
    }
  `,
})
export class AuditComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly auth = inject(AuthService);

  readonly size = 25;
  readonly page = signal<AuditPage | null>(null);
  readonly pageNo = signal(1);
  readonly clients = signal<Client[]>([]);
  readonly open = signal<AuditRow | null>(null);
  search = ''; action = ''; actor = ''; actorType = ''; clientId = ''; from = ''; to = '';

  /** Group headings from the action names, e.g. receipt.submit -> "receipt". */
  readonly categories = computed(() => [...new Set((this.page()?.actions ?? []).map(a => a.split('.')[0]))]);

  async ngOnInit(): Promise<void> {
    if (!this.auth.user()?.clientId) { try { this.clients.set(await this.api.get<Client[]>('/api/v1/clients')); } catch { /* list stays empty */ } }
    await this.load();
  }

  reload(): void { this.pageNo.set(1); void this.load(); }
  go(p: number): void { this.pageNo.set(p); void this.load(); }
  clear(): void { this.search = this.action = this.actor = this.actorType = this.clientId = this.from = this.to = ''; this.reload(); }

  async load(): Promise<void> {
    // the date boxes are the viewer's local days; the API compares UTC moments
    const start = this.from ? new Date(this.from + 'T00:00:00') : null;
    const end = this.to ? new Date(new Date(this.to + 'T00:00:00').getTime() + 86_400_000) : null;
    try {
      this.page.set(await this.api.get<AuditPage>('/api/v1/audit/search', {
        search: this.search, action: this.action, actor: this.actor, actorType: this.actorType, clientId: this.clientId,
        from: start?.toISOString(), to: end?.toISOString(), page: this.pageNo(), pageSize: this.size,
      }));
    } catch (e) { this.toast.err(e); }
  }

  label(t: string): string { return t === 'user' ? 'Person' : t === 'token' ? 'Integration' : 'System'; }
  icon(t: string): string { return t === 'user' ? 'users' : t === 'token' ? 'key' : 'cpu'; }
  chip(t: string): string {
    return t === 'user' ? 'bg-blue-100 text-blue-700 dark:bg-blue-500/20 dark:text-blue-200'
      : t === 'token' ? 'bg-violet-100 text-violet-700 dark:bg-violet-500/20 dark:text-violet-200' : 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300';
  }
  short(id?: string): string { return id && id.length > 13 ? id.slice(0, 8) + '…' : id ?? ''; }
}
