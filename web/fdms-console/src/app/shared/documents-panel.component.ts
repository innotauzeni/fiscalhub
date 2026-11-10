import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DialogService, ToastService } from '../core/ui.services';
import { ModalComponent } from './modal.components';
import { IconComponent } from './ui.components';

export interface DocItem { id: string; title: string; fileName: string; contentType: string; sizeBytes: number; category: string; linkType: string; linkId?: string | null; linkLabel?: string | null; notes?: string | null; retainUntil: string; canDelete: boolean; uploadedBy?: string | null; uploadedAt: string }
interface Summary { files: number; totalBytes: number; limitBytes: number; yearsKept: number }
interface EditForm { id: string; title: string; category: string; notes: string; retainUntil: string; minRetain: string }

export const DOC_CATEGORIES = ['Supplier invoice', 'Contract', 'Tax', 'Bank', 'Payroll', 'Asset', 'Other'];

const size = (b: number): string => (b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB');

/**
 * The company's file cabinet. On its own page it shows every document; opened from a bill, an asset or a supplier (linkType + linkId) it shows only that one's
 * documents and new uploads are attached to it. Records are kept for years: the delete button only works once the keep-until date has passed.
 */
@Component({
  selector: 'app-documents-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, IconComponent, ModalComponent],
  template: `
    @if (auth.can('accounting.manage')) {
      <section class="mb-5 rounded-xl border border-dashed border-slate-300 p-4 dark:border-slate-600" (dragover)="$event.preventDefault()" (drop)="onDrop($event)">
        <div class="grid gap-3 sm:grid-cols-6">
          <label class="block sm:col-span-6"><span class="label">File</span><input type="file" class="input" accept=".pdf,.png,.jpg,.jpeg,.xlsx,.docx,.csv,.txt" (change)="pickFile($event)" /><span class="hint">PDF, picture, Excel, Word, CSV or text. Up to 8 MB.</span></label>
          <label class="block sm:col-span-3"><span class="label">Title</span><input class="input" [(ngModel)]="title" maxlength="150" placeholder="Taken from the file name" /></label>
          <label class="block sm:col-span-3"><span class="label">Kind</span><select class="input" [(ngModel)]="category">@for (c of categories; track c) { <option [value]="c">{{ c }}</option> }</select></label>
          <div class="flex items-center justify-between gap-3 sm:col-span-6">
            <span class="text-xs text-slate-500">@if (summary(); as s) { {{ s.files }} file(s) · {{ fmt(s.totalBytes) }} of {{ fmt(s.limitBytes) }} used · kept for {{ s.yearsKept }} years }</span>
            <button class="btn" type="button" [disabled]="busy() || !file()" (click)="upload()"><app-icon name="cloud-upload" size="h-4 w-4" />{{ busy() ? 'Uploading…' : 'Upload' }}</button>
          </div>
        </div>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
      </section>
    }

    @if (!linkType()) {
      <div class="mb-3 flex flex-wrap gap-3">
        <label class="block min-w-48 flex-1"><span class="label">Search</span><input class="input" placeholder="Title, file name or notes…" [ngModel]="q()" (ngModelChange)="q.set($event); reload()" /></label>
        <label class="block"><span class="label">Kind</span><select class="input" [ngModel]="cat()" (ngModelChange)="cat.set($event); reload()"><option value="">All</option>@for (c of categories; track c) { <option [value]="c">{{ c }}</option> }</select></label>
      </div>
    }

    <ul class="divide-y divide-slate-100 dark:divide-slate-800">
      @for (d of items(); track d.id) {
        <li class="flex flex-wrap items-center gap-3 py-3">
          <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-slate-500 dark:bg-slate-800"><app-icon name="file-text" size="h-5 w-5" /></span>
          <div class="min-w-0 flex-1">
            <div class="truncate text-sm font-medium">{{ d.title }}</div>
            <div class="truncate text-xs text-slate-500">{{ d.category }} · {{ d.fileName }} · {{ fmt(d.sizeBytes) }}@if (d.linkLabel && !linkType()) { · {{ d.linkLabel }} }</div>
            <div class="text-xs text-slate-500">Added {{ d.uploadedAt | date: 'd MMM y' }}@if (d.uploadedBy) { by {{ d.uploadedBy }} } · keep until {{ d.retainUntil | date: 'd MMM y' }}</div>
          </div>
          @if (openable(d)) { <button type="button" class="btn-ghost btn-sm" (click)="view(d)">Open</button> }
          <button type="button" class="btn-ghost btn-sm" (click)="download(d)">Download</button>
          @if (auth.can('accounting.manage')) {
            <button type="button" class="icon-btn" (click)="edit(d)" aria-label="Edit details"><app-icon name="edit" size="h-4 w-4" /></button>
            <button type="button" class="icon-btn" [disabled]="!d.canDelete" [title]="d.canDelete ? 'Delete' : 'Kept until ' + (d.retainUntil | date: 'd MMM y')" (click)="remove(d)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
          }
        </li>
      } @empty { <li class="py-6 text-center text-sm text-slate-500">{{ loaded() ? 'No documents yet.' : 'Loading…' }}</li> }
    </ul>

    @if (editing(); as f) {
      <app-modal title="Document details" width="max-w-md" (closed)="editing.set(null)">
        <form (ngSubmit)="saveEdit()" class="grid gap-3">
          <label class="block"><span class="label">Title</span><input class="input" name="t" [(ngModel)]="f.title" required maxlength="150" /></label>
          <label class="block"><span class="label">Kind</span><select class="input" name="c" [(ngModel)]="f.category">@for (c of categories; track c) { <option [value]="c">{{ c }}</option> }</select></label>
          <label class="block"><span class="label">Notes</span><input class="input" name="n" [(ngModel)]="f.notes" maxlength="500" /></label>
          <label class="block"><span class="label">Keep until</span><input type="date" class="input" name="r" [(ngModel)]="f.retainUntil" [min]="f.minRetain" /><span class="hint">You can extend how long it is kept, never shorten it.</span></label>
          @if (error()) { <p class="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="flex justify-end gap-2 border-t border-slate-100 pt-3 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="editing.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class DocumentsPanelComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly auth = inject(AuthService);
  readonly linkType = input<string | null>(null);
  readonly linkId = input<string | null>(null);

  readonly categories = DOC_CATEGORIES;
  readonly items = signal<DocItem[]>([]);
  readonly summary = signal<Summary | null>(null);
  readonly loaded = signal(false);
  readonly file = signal<File | null>(null);
  readonly q = signal('');
  readonly cat = signal('');
  readonly busy = signal(false);
  readonly error = signal('');
  readonly editing = signal<EditForm | null>(null);
  readonly fmt = size;
  readonly count = computed(() => this.items().length);
  title = '';
  category = 'Supplier invoice';

  ngOnInit(): Promise<void> { return this.reload(); }

  async reload(): Promise<void> {
    const q: Record<string, string> = {};
    if (this.linkType()) { q['linkType'] = this.linkType()!; if (this.linkId()) q['linkId'] = this.linkId()!; }
    if (this.q().trim()) q['q'] = this.q().trim();
    if (this.cat()) q['category'] = this.cat();
    try {
      this.items.set(await this.api.get<DocItem[]>('/api/v1/accounting/documents', q));
      this.summary.set(await this.api.get<Summary>('/api/v1/accounting/documents/summary'));
      this.loaded.set(true);
    } catch (e) { this.toast.err(e); }
  }

  pickFile(ev: Event): void { const f = (ev.target as HTMLInputElement).files?.[0] ?? null; this.setFile(f); }
  onDrop(ev: DragEvent): void { ev.preventDefault(); this.setFile(ev.dataTransfer?.files?.[0] ?? null); }
  private setFile(f: File | null): void { this.file.set(f); this.error.set(''); if (f && !this.title.trim()) this.title = f.name.replace(/\.[^.]+$/, ''); }

  async upload(): Promise<void> {
    const f = this.file(); if (!f || this.busy()) return;
    if (f.size > 8 * 1024 * 1024) { this.error.set('A file can be at most 8 MB.'); return; }
    this.busy.set(true); this.error.set('');
    try {
      const fd = new FormData();
      fd.append('File', f, f.name); fd.append('Category', this.category);
      if (this.title.trim()) fd.append('Title', this.title.trim());
      if (this.linkType()) { fd.append('LinkType', this.linkType()!); if (this.linkId()) fd.append('LinkId', this.linkId()!); }
      await this.api.post('/api/v1/accounting/documents', fd);
      this.toast.ok('Document stored.'); this.file.set(null); this.title = '';
      await this.reload();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  openable(d: DocItem): boolean { return d.contentType === 'application/pdf' || d.contentType.startsWith('image/'); }
  async view(d: DocItem): Promise<void> { try { await this.api.open(`/api/v1/accounting/documents/${d.id}/file`, { inline: 'true' }); } catch (e) { this.toast.err(e); } }
  async download(d: DocItem): Promise<void> { try { await this.api.download(`/api/v1/accounting/documents/${d.id}/file`, d.fileName); } catch (e) { this.toast.err(e); } }

  edit(d: DocItem): void { this.error.set(''); this.editing.set({ id: d.id, title: d.title, category: d.category, notes: d.notes ?? '', retainUntil: d.retainUntil.slice(0, 10), minRetain: d.retainUntil.slice(0, 10) }); }

  async saveEdit(): Promise<void> {
    const f = this.editing(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.put(`/api/v1/accounting/documents/${f.id}`, { title: f.title.trim(), category: f.category, notes: f.notes.trim() || null, retainUntil: f.retainUntil }); this.editing.set(null); await this.reload(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async remove(d: DocItem): Promise<void> {
    if (!(await this.dialog.confirm(`Delete "${d.title}"? This cannot be undone.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/accounting/documents/${d.id}`); this.toast.ok('Deleted.'); await this.reload(); } catch (e) { this.toast.err(e); }
  }
}
