import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ErrorLogEntry, ErrorLogPage } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent, PagerComponent } from '../../shared/list.components';
import { ModalComponent, StatCardComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';

/** What went wrong inside the gateway (platform operator): errors with the request, person, machine and stack trace, and a resolve workflow. */
@Component({
  selector: 'app-system-logs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent, StatCardComponent, PageHeaderComponent, StatusComponent, PagerComponent, EmptyComponent],
  template: `
    <app-page-header title="System logs" subtitle="Errors the gateway ran into. The same error repeating is counted on one line.">
      @if (auth.can('logs.manage')) {
        <button class="btn-ghost" (click)="resolveAll()" [disabled]="!(data()?.stats?.unresolved)"><app-icon name="check" size="h-4 w-4" />Resolve all</button>
        <button class="btn-danger" (click)="purge()"><app-icon name="trash" size="h-4 w-4" />Delete resolved</button>
      }
    </app-page-header>

    @if (data(); as d) {
      <div class="mb-5 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <app-stat-card label="Unresolved" [value]="d.stats.unresolved" icon="alert" tone="amber" hint="still need a look" />
        <app-stat-card label="Critical open" [value]="d.stats.criticalUnresolved" icon="bolt" tone="rose" hint="act on these first" />
        <app-stat-card label="Seen today" [value]="d.stats.today" icon="calendar" tone="blue" hint="since midnight UTC" />
        <app-stat-card label="Total kept" [value]="d.stats.total" icon="clipboard" tone="violet" hint="older than 90 days are removed" />
      </div>
    }

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-6">
        <label class="block lg:col-span-2"><span class="label">Search</span>
          <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
            <input class="input pl-9" placeholder="Message, source, path, exception or reference…" [(ngModel)]="search" (keydown.enter)="reload()" /></span></label>
        <label class="block"><span class="label">Severity</span>
          <select class="input" [(ngModel)]="severity" (change)="reload()"><option value="">All</option><option>Critical</option><option>Error</option><option>Warning</option><option>Info</option></select></label>
        <label class="block"><span class="label">Status</span>
          <select class="input" [(ngModel)]="resolved" (change)="reload()"><option value="">All</option><option value="false">Unresolved</option><option value="true">Resolved</option></select></label>
        <label class="block"><span class="label">From</span><input type="date" class="input" [(ngModel)]="from" (change)="reload()" /></label>
        <label class="block"><span class="label">To</span><input type="date" class="input" [(ngModel)]="to" (change)="reload()" /></label>
      </div>
      <div class="mt-3 flex gap-2"><button class="btn" (click)="reload()">Search</button><button class="btn-ghost" (click)="clear()">Clear</button></div>
    </div>

    <div class="card">
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Last seen</th><th>Severity</th><th>Where</th><th>What happened</th><th>Times</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (e of data()?.items ?? []; track e.id) {
              <tr class="cursor-pointer" (click)="show(e)">
                <td class="whitespace-nowrap">{{ e.lastSeenAt | date:'medium' }}</td>
                <td><span class="badge" [class]="sev(e.severity)">{{ e.severity }}</span></td>
                <td><div class="max-w-48 truncate text-sm">{{ shortSource(e.source) }}</div><div class="hint mt-0 max-w-48 truncate font-mono">{{ e.path }}</div></td>
                <td><div class="max-w-md truncate font-medium">{{ e.message }}</div><div class="hint mt-0 truncate">{{ shortType(e.exceptionType) }}</div></td>
                <td>@if (e.occurrences > 1) { <span class="badge badge-warn">×{{ e.occurrences }}</span> } @else { <span class="text-slate-400">1</span> }</td>
                <td><app-status [value]="e.isResolved ? 'Active' : 'Failed'" /> <span class="text-xs text-slate-500">{{ e.isResolved ? 'Resolved' : 'Open' }}</span></td>
                <td><div class="flex justify-end gap-1.5" (click)="$event.stopPropagation()">
                  <button class="icon-btn icon-btn-primary" (click)="show(e)" title="Details" aria-label="Details"><app-icon name="eye" size="h-4 w-4" /></button>
                  @if (auth.can('logs.manage')) {
                    @if (!e.isResolved) { <button class="icon-btn" (click)="startResolve(e)" title="Mark as resolved" aria-label="Mark as resolved"><app-icon name="check" size="h-4 w-4" /></button> }
                    <button class="icon-btn icon-btn-danger" (click)="remove(e)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                  }
                </div></td>
              </tr>
            } @empty { <tr><td colspan="7"><app-empty icon="check" title="No errors match" hint="Nothing has gone wrong that fits these filters." /></td></tr> }
          </tbody>
        </table>
      </div>
      <app-pager [page]="pageNo()" [pageSize]="size" [total]="data()?.total ?? 0" (pageChange)="go($event)" />
    </div>

    @if (detail(); as e) {
      <app-modal [title]="e.severity + ' · ' + e.uuid" [subtitle]="(e.createdAt | date:'medium') + ' — last seen ' + (e.lastSeenAt | date:'medium')" width="max-w-4xl" (closed)="detail.set(null)">
        <div class="mb-4 flex flex-wrap items-center gap-2"><span class="badge" [class]="sev(e.severity)">{{ e.severity }}</span>
          <span class="badge" [class.badge-ok]="e.isResolved" [class.badge-warn]="!e.isResolved">{{ e.isResolved ? 'Resolved' : 'Open' }}</span>
          @if (e.occurrences > 1) { <span class="badge badge-warn">happened {{ e.occurrences }} times</span> }</div>
        <p class="rounded-xl bg-slate-50 p-4 text-sm font-medium dark:bg-slate-950">{{ e.message }}</p>

        <dl class="mt-5 grid gap-x-6 gap-y-3 text-sm sm:grid-cols-2 lg:grid-cols-3">
          <div><dt class="label">Source</dt><dd class="break-all">{{ e.source ?? '—' }}</dd></div>
          <div><dt class="label">Exception</dt><dd class="break-all font-mono text-xs">{{ e.exceptionType ?? '—' }}</dd></div>
          <div><dt class="label">Request</dt><dd class="break-all font-mono text-xs">{{ e.method }} {{ e.path ?? '—' }}</dd></div>
          <div><dt class="label">Person / system</dt><dd>{{ e.userName ?? '—' }}</dd></div>
          <div><dt class="label">Client</dt><dd>{{ e.clientName ?? '—' }}</dd></div>
          <div><dt class="label">From address</dt><dd class="font-mono">{{ e.ipAddress ?? '—' }}</dd></div>
          <div><dt class="label">Machine</dt><dd>{{ e.machineName ?? '—' }} <span class="text-xs text-slate-500">{{ e.environment }}</span></dd></div>
          <div><dt class="label">Trace id</dt><dd class="break-all font-mono text-xs">{{ e.traceId ?? '—' }}</dd></div>
        </dl>

        @if (e.stackTrace) {
          <div class="mt-5"><div class="mb-1 flex items-center justify-between"><span class="label mb-0">Stack trace</span><button class="btn-ghost btn-sm" (click)="copy(e)"><app-icon name="copy" size="h-3.5 w-3.5" />Copy details</button></div>
            <pre class="max-h-72 overflow-auto rounded-xl bg-slate-900 p-4 text-xs leading-relaxed text-slate-100">{{ e.stackTrace }}</pre></div>
        }

        @if (e.isResolved) {
          <div class="mt-5 rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm dark:border-emerald-900/50 dark:bg-emerald-900/20">
            <div class="font-medium text-emerald-800 dark:text-emerald-200">Resolved {{ e.resolvedAt | date:'medium' }} by {{ e.resolvedBy ?? 'unknown' }}</div>
            @if (e.resolution) { <p class="mt-1 text-emerald-900 dark:text-emerald-100">{{ e.resolution }}</p> }
          </div>
        } @else if (auth.can('logs.manage')) {
          <form class="mt-5" (ngSubmit)="resolve(e)">
            <label class="block"><span class="label">What was done about it? (optional)</span><textarea class="input min-h-16" name="note" [(ngModel)]="note" maxlength="1000"></textarea></label>
            <div class="mt-3 flex justify-end gap-2"><button class="btn-ghost" type="button" (click)="detail.set(null)">Close</button><button class="btn" type="submit" [disabled]="busy()">Mark as resolved</button></div>
          </form>
        } @else { <div class="mt-6 flex justify-end"><button class="btn-ghost" (click)="detail.set(null)">Close</button></div> }
      </app-modal>
    }
  `,
})
export class SystemLogsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly auth = inject(AuthService);

  readonly size = 20;
  readonly data = signal<ErrorLogPage | null>(null);
  readonly pageNo = signal(1);
  readonly detail = signal<ErrorLogEntry | null>(null);
  readonly busy = signal(false);
  search = ''; severity = ''; resolved = ''; from = ''; to = ''; note = '';

  ngOnInit(): Promise<void> { return this.load(); }
  reload(): void { this.pageNo.set(1); void this.load(); }
  go(p: number): void { this.pageNo.set(p); void this.load(); }
  clear(): void { this.search = this.severity = this.resolved = this.from = this.to = ''; this.reload(); }

  async load(): Promise<void> {
    const start = this.from ? new Date(this.from + 'T00:00:00') : null;
    const end = this.to ? new Date(new Date(this.to + 'T00:00:00').getTime() + 86_400_000) : null;
    try {
      this.data.set(await this.api.get<ErrorLogPage>('/api/v1/system-logs', {
        search: this.search, severity: this.severity, resolved: this.resolved, from: start?.toISOString(), to: end?.toISOString(), page: this.pageNo(), pageSize: this.size,
      }));
    } catch (e) { this.toast.err(e); }
  }

  sev(s: string): string { return s === 'Critical' ? 'badge-bad' : s === 'Error' ? 'badge-bad' : s === 'Warning' ? 'badge-warn' : 'badge-info'; }
  shortSource(s?: string): string { return s ? s.split('.').slice(-2).join('.') : '—'; }
  shortType(t?: string): string { return t ? t.split('.').pop()! : ''; }

  show(e: ErrorLogEntry): void { this.note = ''; this.detail.set(e); }
  startResolve(e: ErrorLogEntry): void { this.show(e); }

  async resolve(e: ErrorLogEntry): Promise<void> {
    this.busy.set(true);
    try { await this.api.put(`/api/v1/system-logs/${e.id}/resolve`, { resolution: this.note.trim() || null }); this.toast.ok('Marked as resolved.'); this.detail.set(null); await this.load(); }
    catch (err) { this.toast.err(err); } finally { this.busy.set(false); }
  }

  async resolveAll(): Promise<void> {
    if (!(await this.dialog.confirm('Mark every open error as resolved?', 'Resolve all'))) return;
    try { const r = await this.api.post<{ resolved: number }>('/api/v1/system-logs/resolve-all'); this.toast.ok(`${r.resolved} marked as resolved.`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async purge(): Promise<void> {
    if (!(await this.dialog.confirm('Permanently delete all resolved entries?', 'Delete resolved', true))) return;
    try { const r = await this.api.delete<{ deleted: number }>('/api/v1/system-logs/resolved'); this.toast.ok(`${r.deleted} deleted.`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  async remove(e: ErrorLogEntry): Promise<void> {
    if (!(await this.dialog.confirm('Delete this log entry?', 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/system-logs/${e.id}`); this.toast.ok('Entry deleted.'); await this.load(); } catch (err) { this.toast.err(err); }
  }

  async copy(e: ErrorLogEntry): Promise<void> {
    const text = [`${e.severity} ${e.uuid}`, e.message, `${e.method ?? ''} ${e.path ?? ''}`.trim(), `Seen ${e.occurrences}x, last ${e.lastSeenAt}`, `Machine ${e.machineName ?? ''} (${e.environment ?? ''})`, '', e.stackTrace ?? ''].join('\n');
    try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { this.toast.err(new Error('Copy failed - select the text and copy it manually.')); }
  }
}
