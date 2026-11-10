import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../../core/api.service';
import { Backup, BackupList } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent } from '../../shared/list.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

/**
 * Database backups for the platform operator: back up now, see the schedule, download a file to keep somewhere safe, delete old ones.
 * A backup holds every company's data, so downloads are audited and the files never leave the server by any other route.
 */
@Component({
  selector: 'app-backups',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, IconComponent, PageHeaderComponent, EmptyComponent],
  template: `
    <app-page-header title="Backups" subtitle="A complete copy of the database. Download copies and keep them somewhere other than this server.">
      <button class="btn-ghost" (click)="structure()" [disabled]="!data()?.info?.supported" title="The empty database structure as one SQL script, for setting up a new server"><app-icon name="download" size="h-4 w-4" />Structure script (.sql)</button>
      <button class="btn" (click)="create()" [disabled]="busy() || !data()?.info?.supported"><app-icon name="cloud-upload" size="h-4 w-4" />{{ busy() ? 'Backing up…' : 'Back up now' }}</button>
    </app-page-header>

    @if (data(); as d) {
      @if (!d.info.supported) {
        <div class="card mb-5 border-amber-300 bg-amber-50 text-sm text-amber-900 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-200">Backups need a MySQL database; this gateway is running on another one.</div>
      }
      <div class="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <div class="card"><div class="label">Last good backup</div><div class="mt-1 text-lg font-semibold">{{ d.info.lastOkAt ? (d.info.lastOkAt | date:'medium') : 'None yet' }}</div></div>
        <div class="card"><div class="label">Schedule</div><div class="mt-1 text-lg font-semibold">{{ d.info.scheduleEnabled ? 'Every ' + d.info.intervalHours + ' h' : 'Off' }}</div>
          @if (d.info.nextDueAt) { <div class="hint">Next due about {{ d.info.nextDueAt | date:'short' }}</div> }</div>
        <div class="card"><div class="label">Kept</div><div class="mt-1 text-lg font-semibold">Newest {{ d.info.keepLast }}</div><div class="hint">Older ones are deleted automatically</div></div>
        <div class="card"><div class="label">Stored in</div><div class="mt-1 break-all font-mono text-xs">{{ d.info.folder }}</div></div>
      </div>

      <div class="card">
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>When</th><th>File</th><th>Kind</th><th>Size</th><th>Contents</th><th>Result</th><th></th></tr></thead>
            <tbody>
              @for (b of d.items; track b.id) {
                <tr>
                  <td class="whitespace-nowrap">{{ b.createdAt | date:'medium' }}<div class="text-xs text-slate-500">{{ b.createdBy }}</div></td>
                  <td class="max-w-64 break-all font-mono text-xs">{{ b.fileName }}@if (b.sha256) { <div class="text-[10px] text-slate-400" [title]="b.sha256">sha256 {{ b.sha256.slice(0, 16) }}…</div> }</td>
                  <td><span class="badge" [class]="b.kind === 'scheduled' ? 'badge-info' : ''">{{ b.kind === 'scheduled' ? 'Scheduled' : 'Manual' }}</span></td>
                  <td class="whitespace-nowrap tabular-nums">{{ size(b.sizeBytes) }}</td>
                  <td class="whitespace-nowrap text-xs text-slate-500">{{ b.tables }} tables · {{ b.rows }} rows</td>
                  <td><span class="badge" [class]="b.status === 'Ok' ? 'badge-ok' : b.status === 'Failed' ? 'badge-bad' : 'badge-warn'">{{ b.status === 'Ok' ? (b.fileExists ? 'Done' : 'File missing') : b.status }}</span>
                    @if (b.error) { <div class="mt-1 max-w-64 text-xs text-red-600">{{ b.error }}</div> }</td>
                  <td class="whitespace-nowrap text-right">
                    @if (b.status === 'Ok' && b.fileExists) { <button class="icon-btn" (click)="download(b)" title="Download (compressed .gz)" aria-label="Download"><app-icon name="download" size="h-4 w-4" /></button><button class="icon-btn" (click)="downloadSql(b)" title="Download as a plain .sql file (for Windows servers)" aria-label="Download as .sql"><app-icon name="file-text" size="h-4 w-4" /></button> }
                    <button class="icon-btn text-red-600" (click)="remove(b)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                  </td>
                </tr>
              } @empty { <tr><td colspan="7"><app-empty icon="cloud-upload" title="No backups yet" hint="Press Back up now. One is also made automatically on the schedule above." /></td></tr> }
            </tbody>
          </table>
        </div>
      </div>

      <div class="card mt-6 text-sm text-slate-600 dark:text-slate-300">
        <div class="card-title">Restoring</div>
        <p>Restoring replaces the whole database, so it is done on purpose from the server, never from this page: stop the gateway, then load the file with the MySQL client.</p>
        <pre class="mt-2 overflow-x-auto rounded-lg bg-slate-50 p-3 text-xs dark:bg-slate-950">gunzip -c fdms-YYYYMMDD-HHMMSS-xxxxxx.sql.gz | mysql -u USER -p DATABASE</pre>
        <p class="mt-2">The encryption keys that protect saved secrets (the DataProtection key folder) are not in the database: back that folder up too, or saved gateway credentials cannot be read after a restore.</p>
      </div>
    }
  `,
})
export class BackupsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly data = signal<BackupList | null>(null);
  readonly busy = signal(false);

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> {
    try { this.data.set(await this.api.get<BackupList>('/api/v1/backups')); } catch (e) { this.toast.err(e); }
  }

  async create(): Promise<void> {
    this.busy.set(true);
    try { await this.api.post('/api/v1/backups', {}); this.toast.ok('Backup finished'); } catch (e) { this.toast.err(e); }
    this.busy.set(false);
    await this.load();
  }

  downloadSql(b: Backup): Promise<void> { return this.api.download(`/api/v1/backups/${b.id}/download?plain=true`, b.fileName.replace(/\.gz$/i, '')).catch(e => this.toast.err(e)); }
  structure(): Promise<void> { return this.api.download('/api/v1/backups/schema', `fdms-structure-${new Date().toISOString().slice(0, 10).replace(/-/g, '')}.sql`).catch(e => this.toast.err(e)); }

  download(b: Backup): Promise<void> { return this.api.download(`/api/v1/backups/${b.id}/download`, b.fileName).catch(e => this.toast.err(e)); }

  async remove(b: Backup): Promise<void> {
    if (!(await this.dialog.confirm(`Delete ${b.fileName}? The file is removed from the server.`, 'Delete backup', true))) return;
    try { await this.api.delete(`/api/v1/backups/${b.id}`); await this.load(); } catch (e) { this.toast.err(e); }
  }

  size(n: number): string { return n >= 1_048_576 ? (n / 1_048_576).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB'; }
}
