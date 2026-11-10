import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { ImportItem, ImportResult } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { DeviceSelectComponent } from '../shared/device-select.component';
import { PageHeaderComponent, StatusComponent } from '../shared/ui.components';

@Component({
  selector: 'app-receipt-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DecimalPipe, DeviceSelectComponent, PageHeaderComponent, StatusComponent],
  template: `
    <app-page-header title="Import invoices" subtitle="Upload an Excel sheet, a filled Word invoice or a digital PDF. Preview first; nothing is sent until you tick “Send to ZIMRA”.">
      <button class="btn-ghost" (click)="template()">Excel template</button>
      <button class="btn" (click)="wordTemplate(false)">Word invoice template</button>
    </app-page-header>

    <div class="card mb-5 overflow-hidden p-0">
      <div class="grid items-center gap-0 md:grid-cols-5">
        <div class="p-6 md:col-span-3">
          <div class="flex items-center gap-2"><span class="badge badge-info">No invoice system?</span></div>
          <h2 class="mt-2 text-lg font-semibold">Use our ready-made Word tax invoice</h2>
          <p class="mt-1 text-sm text-slate-600 dark:text-slate-400">A clean A4 invoice with every field a ZIMRA fiscal invoice carries: your name, TIN and VAT number, the customer's TIN and VAT number, a line table with HS code, quantity, unit price and VAT %, totals, and a box where the fiscal details and QR code appear once it is fiscalised.</p>
          <ol class="mt-3 list-decimal space-y-1 pl-5 text-sm text-slate-600 dark:text-slate-400">
            <li>Download the template and fill it in (click a grey field and type).</li>
            <li>Upload the .docx here, or save it as PDF and upload that.</li>
            <li>Check the preview, then tick <strong>Send to ZIMRA</strong>.</li>
          </ol>
          <div class="mt-4 flex flex-wrap gap-2">
            <button class="btn" (click)="wordTemplate(false)">Download template (.docx)</button>
            <button class="btn-ghost" (click)="wordTemplate(true)">See a filled example</button>
          </div>
        </div>
        <div class="hidden bg-linear-to-br from-brand-50 to-violet-50 p-6 md:col-span-2 md:block dark:from-slate-900 dark:to-slate-800" aria-hidden="true">
          <div class="mx-auto w-56 rotate-2 rounded-lg border border-slate-200 bg-white p-3 text-[7px] leading-tight shadow-xl dark:border-slate-700 dark:bg-slate-900">
            <div class="flex justify-between"><div><div class="text-[9px] font-bold">Acme Trading</div><div class="text-slate-400">TIN 1234567890</div></div><div class="text-right text-[10px] font-extrabold text-brand-600">TAX INVOICE</div></div>
            <div class="my-1.5 h-px bg-brand-600"></div>
            <div class="rounded bg-slate-100 p-1.5 dark:bg-slate-800"><div class="font-bold">BILL TO</div><div>Moyo Retailers</div><div class="text-slate-400">TIN 2001234567</div></div>
            <div class="mt-1.5 rounded-t bg-brand-600 px-1 py-0.5 font-bold text-white">#  Description  HS  Qty  Price  VAT  Amount</div>
            @for (r of [1, 2, 3]; track r) { <div class="flex justify-between border-b border-slate-100 px-1 py-0.5 dark:border-slate-800"><span>{{ r }}. Item {{ r }}</span><span>19059010</span><span>10</span><span>1.15</span><span>15%</span></div> }
            <div class="mt-1.5 ml-auto w-24 rounded bg-slate-100 p-1 text-right font-bold text-brand-600 dark:bg-slate-800">Total Due 50.50</div>
            <div class="mt-1.5 flex gap-1.5"><div class="grow rounded bg-slate-100 p-1.5 dark:bg-slate-800">Fiscal device ID ____<br />Receipt global no ____</div><div class="flex h-9 w-9 items-center justify-center rounded border border-dashed border-slate-300">QR</div></div>
          </div>
        </div>
      </div>
    </div>

    <div class="card">
      <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <app-device-select />
        <label class="block lg:col-span-2"><span class="label">File (.xlsx, .docx or .pdf)</span>
          <input type="file" accept=".xlsx,.docx,.pdf" class="input" (change)="pick($any($event.target).files)" /></label>
        <label class="block"><span class="label">Default tax % (when blank)</span><input class="input" type="number" step="any" [(ngModel)]="defaultTax" placeholder="e.g. 15" /></label>
        <label class="block"><span class="label">Default currency</span><input class="input uppercase" maxlength="3" [(ngModel)]="defaultCurrency" placeholder="USD" /></label>
        <label class="block"><span class="label">Default HS code</span><input class="input" [(ngModel)]="defaultHs" placeholder="8 digits - only if correct for the goods" /></label>
        @if (isDoc()) { <label class="block"><span class="label">Invoice number (if the document has none)</span><input class="input" [(ngModel)]="invoiceNo" /></label> }
      </div>
      <div class="mt-4 flex flex-wrap items-center gap-4">
        <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" [(ngModel)]="inclusive" /> Prices include tax</label>
        <label class="flex items-center gap-2 text-sm font-medium"><input type="checkbox" class="h-4 w-4" [(ngModel)]="send" /> Send to ZIMRA now (untick to preview only)</label>
        <button class="btn ml-auto" (click)="run()" [disabled]="busy() || !file">{{ send ? 'Import and send' : 'Preview' }}</button>
      </div>
      <p class="hint">Word and PDF invoices are read best-effort (digital text only, no scans) — always check the preview. Uploading the same invoice number again never fiscalises it twice.</p>
    </div>

    @if (result(); as r) {
      <div class="card mt-5">
        <div class="card-title">{{ r.submitted ? 'Sent' : 'Preview (nothing sent)' }} — {{ r.succeeded }} ok, {{ r.failed }} failed of {{ r.total }}</div>
        @for (e of r.errors; track e) { <div class="mb-1 rounded-lg bg-red-50 px-3 py-1.5 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">{{ e }}</div> }
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>Invoice</th><th>Status</th><th class="text-right">Total</th><th>Notes</th></tr></thead>
            <tbody>
              @for (i of r.items; track $index) {
                <tr>
                  <td class="font-medium">{{ i.invoiceNo ?? '(none)' }}</td>
                  <td><app-status [value]="i.status" /></td>
                  <td class="text-right tabular-nums">{{ i.total | number:'1.2-2' }}</td>
                  <td>
                    @if (i.error) { <div class="text-sm text-red-600">[{{ i.errorCode }}] {{ i.error }}</div> }
                    @for (w of i.warnings; track w) { <div class="text-sm text-amber-700 dark:text-amber-300">{{ w }}</div> }
                    @if (i.request && !r.submitted) { <button class="btn-ghost btn-sm mt-1" (click)="draft.set(i)">Show draft request</button> }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (draft(); as d) { <pre class="mt-3 max-h-72 overflow-auto rounded-lg bg-slate-50 p-3 text-xs dark:bg-slate-950">{{ json(d.request) }}</pre> }
      </div>
    }
  `,
})
export class ReceiptImportComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly store = inject(DeviceStore);

  file: File | null = null;
  defaultTax: number | null = null; defaultCurrency = ''; defaultHs = ''; invoiceNo = '';
  inclusive = true; send = false;
  readonly busy = signal(false);
  readonly result = signal<ImportResult | null>(null);
  readonly draft = signal<ImportItem | null>(null);

  async ngOnInit(): Promise<void> { try { await this.store.load(); } catch (e) { this.toast.err(e); } }

  isPdf(): boolean { return !!this.file && this.file.name.toLowerCase().endsWith('.pdf'); }
  isWord(): boolean { return !!this.file && this.file.name.toLowerCase().endsWith('.docx'); }
  /** a single-invoice document (the reader works from its text) rather than a spreadsheet */
  isDoc(): boolean { return this.isPdf() || this.isWord(); }
  pick(files: FileList | null): void { this.file = files?.[0] ?? null; this.result.set(null); this.draft.set(null); }
  json(v: unknown): string { return JSON.stringify(v, null, 2); }
  wordTemplate(sample: boolean): Promise<void> {
    return this.api.download('/api/v1/import/word-template' + (sample ? '?sample=true' : ''), sample ? 'tax-invoice-example.docx' : 'tax-invoice-template.docx').catch(e => this.toast.err(e));
  }
  template(): Promise<void> { return this.api.download('/api/v1/import/excel-template', 'invoice-import-template.xlsx').catch(e => this.toast.err(e)); }

  async run(): Promise<void> {
    const d = this.store.selected();
    if (!this.file || !d) return;
    if (this.send && !(await this.dialog.confirm('This will fiscalise the invoices in the file with ZIMRA. Continue?', 'Send to ZIMRA'))) return;
    this.busy.set(true); this.draft.set(null);
    try {
      const fd = new FormData();
      fd.append('file', this.file);
      const kind = this.isPdf() ? 'pdf' : this.isWord() ? 'word' : 'excel';
      this.result.set(await this.api.post<ImportResult>(`/api/v1/devices/${d.id}/import/${kind}`, fd, {
        submit: this.send, taxInclusive: this.inclusive, defaultTaxPercent: this.defaultTax ?? undefined,
        defaultCurrency: this.defaultCurrency.trim() || undefined, defaultHsCode: this.defaultHs.trim() || undefined, invoiceNo: this.isDoc() ? this.invoiceNo.trim() || undefined : undefined,
      }));
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
