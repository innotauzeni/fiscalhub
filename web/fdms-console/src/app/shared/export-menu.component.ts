import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { ToastService } from '../core/ui.services';
import { IconComponent } from './ui.components';

/** A table to export: columns (text, money or number) and rows (style: normal, bold, total, section or muted; indent for sub-accounts). */
export interface ExportDoc {
  title: string; subtitle?: string | null; landscape?: boolean; fileName?: string;
  columns: { header: string; kind?: 'text' | 'money' | 'number' }[];
  rows: { cells: (string | number | null | undefined)[]; style?: 'normal' | 'bold' | 'total' | 'section' | 'muted'; indent?: number }[];
}

/** Numbers as plain text with two decimals (Excel turns them back into numbers). */
export const num = (v: number | null | undefined, blankZero = false): string => (v == null || (blankZero && !v) ? '' : (Math.round(v * 100) / 100).toFixed(2));

/** Downloads a table as PDF, Excel or CSV through the API, with the company's name on top. */
export async function exportTable(api: Api, format: 'pdf' | 'xlsx' | 'csv', doc: ExportDoc): Promise<void> {
  const body = { ...doc, format, rows: doc.rows.map(r => ({ cells: r.cells.map(c => (c == null ? null : String(c))), style: r.style ?? 'normal', indent: r.indent ?? 0 })), columns: doc.columns.map(c => ({ header: c.header, kind: c.kind ?? 'text' })) };
  const blob = await api.postBlob('/api/v1/accounting/export', body);
  const name = (doc.fileName ?? doc.title).toLowerCase().replace(/[^a-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '') || 'report';
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a'); a.href = url; a.download = name + '.' + format; document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/** Three buttons: PDF, Excel, CSV. The page hands over a function that builds the table at the moment of the click. */
@Component({
  selector: 'app-export-menu',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    <span class="inline-flex overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700 print:hidden">
      <span class="flex items-center gap-1 bg-slate-50 px-2 text-xs text-slate-500 dark:bg-slate-800"><app-icon name="download" size="h-3.5 w-3.5" />Export</span>
      @for (f of formats; track f.key) {
        <button type="button" class="border-l border-slate-200 px-2.5 py-1.5 text-sm hover:bg-slate-50 disabled:opacity-50 dark:border-slate-700 dark:hover:bg-slate-800" [disabled]="busy() || disabled()" (click)="run(f.key)">{{ busy() === f.key ? '…' : f.label }}</button>
      }
    </span>
  `,
})
export class ExportMenuComponent {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly build = input.required<() => ExportDoc | null>();
  readonly disabled = input(false);
  readonly busy = signal<string>('');
  readonly formats = [{ key: 'pdf' as const, label: 'PDF' }, { key: 'xlsx' as const, label: 'Excel' }, { key: 'csv' as const, label: 'CSV' }];

  async run(format: 'pdf' | 'xlsx' | 'csv'): Promise<void> {
    const doc = this.build()(); if (!doc) { this.toast.err(new Error('Show the report first.')); return; }
    this.busy.set(format);
    try { await exportTable(this.api, format, doc); } catch (e) { this.toast.err(e); } finally { this.busy.set(''); }
  }
}
