import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { Receipt, doneOrHeld } from '../core/models';
import { ToastService } from '../core/ui.services';
import { ModalComponent } from '../shared/modal.components';
import { IconComponent } from '../shared/ui.components';

interface DetailLine { receiptLineType: string; receiptLineNo: number; receiptLineHSCode?: string; receiptLineName: string; receiptLinePrice: number; receiptLineQuantity: number; receiptLineTotal: number; taxPercent?: number; }
interface DebitLine { name: string; hs: string; qty: number; price: number; tax: string; }
interface Detail { receipt: Receipt; lines: DetailLine[]; payments: { moneyTypeCode: string; paymentAmount: number }[]; creditNoteId?: string; linesTaxInclusive?: boolean; }

/** What ZIMRA's validation codes mean in plain words (anything else is shown as the code, to look up in the FDMS error list). */
export const VALIDATION_HELP: Record<string, string> = {
  RCPT048: 'An item has an HS code of the wrong length. Use 4, 6 or 8 digits (ZIMRA tariff codes are 8).',
};

const HS = /^(\d{4}|\d{6}|\d{8})$/;

/**
 * Changes an invoice that is already fiscalised (an invoice cannot be edited). A CREDIT note reverses it, and a corrected invoice can replace it, with only the
 * HS codes you change differing from the original (used for receipts ZIMRA flagged in red, which stop the fiscal day from closing). A DEBIT note adds charges
 * the invoice left out. ZIMRA expects all three document types: invoice, credit note and debit note.
 */
@Component({
  selector: 'app-receipt-correct-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DecimalPipe, IconComponent, ModalComponent],
  template: `
    <app-modal [title]="'Credit or debit note for ' + invoiceNo()" subtitle="A fiscalised invoice cannot be edited. Reverse it with a credit note, or add to it with a debit note." width="max-w-3xl" (closed)="closed.emit()">
      @if (loading()) { <p class="py-10 text-center text-sm text-slate-500">Loading what was sent to ZIMRA…</p> }
      @else if (detail(); as d) {
        <div class="mb-5 grid grid-cols-2 gap-2 rounded-xl bg-slate-100 p-1 text-sm dark:bg-slate-800" role="tablist">
          <button type="button" role="tab" [attr.aria-selected]="mode() === 'credit'" class="rounded-lg px-3 py-2 text-left transition" [class]="mode() === 'credit' ? 'bg-white shadow-sm dark:bg-slate-900' : 'text-slate-500'" (click)="mode.set('credit')">
            <span class="block font-semibold">Credit note</span><span class="block text-xs font-normal text-slate-500">Reverse or correct the invoice</span></button>
          <button type="button" role="tab" [attr.aria-selected]="mode() === 'debit'" class="rounded-lg px-3 py-2 text-left transition" [class]="mode() === 'debit' ? 'bg-white shadow-sm dark:bg-slate-900' : 'text-slate-500'" (click)="mode.set('debit')">
            <span class="block font-semibold">Debit note</span><span class="block text-xs font-normal text-slate-500">Add charges the invoice missed</span></button>
        </div>

        @if (mode() === 'debit') {
          <form (ngSubmit)="submitDebit()">
            <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">List only the <strong>additional</strong> charges. Tax and currency ({{ d.receipt.currency }}) follow invoice {{ invoiceNo() }}.</p>
            <div class="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-800">
              <table class="table">
                <thead><tr><th>Item</th><th>HS code</th><th class="text-right">Qty</th><th class="text-right">Price</th><th>VAT</th><th></th></tr></thead>
                <tbody>
                  @for (l of dlines(); track $index; let i = $index) {
                    <tr>
                      <td><input class="input min-w-40" [name]="'dn' + i" [ngModel]="l.name" (ngModelChange)="setLine(i, 'name', $event)" maxlength="200" placeholder="e.g. Delivery charge" /></td>
                      <td><input class="input w-28 font-mono" [name]="'dh' + i" [ngModel]="l.hs" (ngModelChange)="setLine(i, 'hs', $event)" maxlength="8" inputmode="numeric" [class.border-red-500]="!!l.hs.trim() && !hsOk(l.hs)" /></td>
                      <td><input class="input w-20 text-right" type="number" min="0" step="any" [name]="'dq' + i" [ngModel]="l.qty" (ngModelChange)="setLine(i, 'qty', $event)" /></td>
                      <td><input class="input w-28 text-right" type="number" min="0" step="any" [name]="'dp' + i" [ngModel]="l.price" (ngModelChange)="setLine(i, 'price', $event)" /></td>
                      <td><select class="input w-28" [name]="'dt' + i" [ngModel]="l.tax" (ngModelChange)="setLine(i, 'tax', $event)">@for (t of taxOptions(); track t.value) { <option [value]="t.value">{{ t.label }}</option> }</select></td>
                      <td class="text-right">@if (dlines().length > 1) { <button type="button" class="icon-btn text-red-600" (click)="removeLine(i)" aria-label="Remove line"><app-icon name="trash" size="h-4 w-4" /></button> }</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <button type="button" class="btn-ghost btn-sm mt-2" (click)="addLine()"><app-icon name="plus" size="h-4 w-4" />Add line</button>

            <div class="mt-4 grid gap-4 sm:grid-cols-2">
              <label class="block sm:col-span-2"><span class="label">Reason (printed on the debit note)</span><input class="input" name="dreason" [(ngModel)]="debitReason" maxlength="200" placeholder="e.g. Delivery charge left off the invoice" /></label>
              <label class="block"><span class="label">Paid by</span>
                <select class="input" name="dpay" [(ngModel)]="debitPay">@for (m of moneyTypes; track m.value) { <option [value]="m.value">{{ m.label }}</option> }</select></label>
              <label class="block"><span class="label">Debit note number</span><input class="input" name="dno" [(ngModel)]="debitNo" maxlength="50" [placeholder]="'DN-' + invoiceNo()" /></label>
            </div>
            <div class="mt-5 rounded-xl bg-slate-50 p-4 text-sm dark:bg-slate-950">
              <div class="mb-1 font-medium">What will happen</div>
              <p class="text-slate-600 dark:text-slate-300">A debit note for <strong>{{ debitTotal() | number:'1.2-2' }} {{ d.receipt.currency }}</strong> is sent to ZIMRA and added against invoice <strong>{{ invoiceNo() }}</strong>. The invoice itself is not changed.</p>
              <p class="hint">Sending the same note twice never adds the charge twice.</p>
            </div>
            @if (debitProblem(); as why) { <p class="mt-3 text-sm text-amber-700 dark:text-amber-300">{{ why }}</p> }
            <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
              <button class="btn-ghost" type="button" (click)="closed.emit()">Cancel</button>
              <button class="btn" type="submit" [disabled]="busy() || !!debitProblem()">{{ busy() ? 'Sending to ZIMRA…' : 'Issue debit note' }}</button>
            </div>
          </form>
        } @else {
        @if (d.creditNoteId) {
          <div class="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900 dark:border-emerald-900/50 dark:bg-emerald-900/20 dark:text-emerald-100">
            This invoice already has a credit note. Look for the corrected invoice in Receipts; if the replacement is missing, tick the box below and confirm again (nothing is credited twice).
          </div>
        }

        @if (d.receipt.validationErrors.length) {
          <div class="mb-4 space-y-1">
            @for (v of d.receipt.validationErrors; track v.code) {
              <div class="flex items-start gap-2 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800 dark:bg-red-900/25 dark:text-red-200"><app-icon name="alert" size="h-4 w-4 mt-0.5" />
                <span><strong>{{ v.code }}</strong> ({{ v.color }}) — {{ help(v.code) }}</span></div>
            }
          </div>
        }

        <form (ngSubmit)="submit()">
          <div class="label">Items on the invoice <span class="font-normal text-slate-400">— change an HS code if it is wrong</span></div>
          <div class="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-800">
            <table class="table">
              <thead><tr><th>#</th><th>Item</th><th class="text-right">Qty</th><th class="text-right">Price</th><th class="text-right">Total</th><th>HS code</th></tr></thead>
              <tbody>
                @for (l of d.lines; track l.receiptLineNo) {
                  <tr>
                    <td class="text-slate-400">{{ l.receiptLineNo }}</td>
                    <td>{{ l.receiptLineName }}@if (l.taxPercent !== undefined && l.taxPercent !== null) { <span class="hint mt-0"> [{{ l.taxPercent }}%]</span> }</td>
                    <td class="text-right tabular-nums">{{ l.receiptLineQuantity }}</td>
                    <td class="text-right tabular-nums">{{ l.receiptLinePrice | number:'1.2-2' }}</td>
                    <td class="text-right tabular-nums">{{ l.receiptLineTotal | number:'1.2-2' }}</td>
                    <td><input class="input w-32 font-mono" [name]="'hs' + l.receiptLineNo" [ngModel]="codes()[l.receiptLineNo]" (ngModelChange)="setCode(l.receiptLineNo, $event)" maxlength="8" inputmode="numeric"
                               [class.border-red-500]="bad(l.receiptLineNo)" [title]="bad(l.receiptLineNo) ? 'Use 4, 6 or 8 digits' : ''" /></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <div class="mt-4 grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Reason (printed on the credit note)</span><input class="input" name="reason" [(ngModel)]="reason" required maxlength="200" /></label>
            <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" class="h-4 w-4" name="repl" [(ngModel)]="replacement" /> Also issue a corrected invoice with the same items and payment</label>
            @if (replacement) { <label class="block"><span class="label">Number for the corrected invoice</span><input class="input" name="newno" [(ngModel)]="newNo" maxlength="50" /></label> }
          </div>

          <div class="mt-5 rounded-xl bg-slate-50 p-4 text-sm dark:bg-slate-950">
            <div class="mb-1 font-medium">What will happen</div>
            <ol class="list-decimal space-y-0.5 pl-5 text-slate-600 dark:text-slate-300">
              <li>A credit note <strong>CN-{{ invoiceNo() }}</strong> reverses <strong>{{ d.receipt.total | number:'1.2-2' }} {{ d.receipt.currency }}</strong> and is sent to ZIMRA.</li>
              @if (replacement) { <li>A new invoice <strong>{{ newNo || invoiceNo() + '-R' }}</strong> for the same amount is issued with the corrected HS codes.</li> }
            </ol>
            <p class="hint">Then close the fiscal day again. If ZIMRA still refuses, the message above will say why.</p>
          </div>

          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
            <button class="btn-ghost" type="button" (click)="closed.emit()">Cancel</button>
            <button class="btn" type="submit" [disabled]="busy() || anyBad() || (!!d.creditNoteId && !replacement)">{{ busy() ? 'Sending to ZIMRA…' : replacement ? 'Credit and re-issue' : 'Issue credit note' }}</button>
          </div>
        </form>
        }
      }
    </app-modal>
  `,
})
export class ReceiptCorrectDialogComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);

  readonly receiptId = input.required<string>();
  readonly invoiceNo = input.required<string>();
  readonly closed = output<void>();
  readonly done = output<void>();

  readonly detail = signal<Detail | null>(null);
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly codes = signal<Record<number, string>>({});
  readonly anyBad = computed(() => Object.keys(this.codes()).some(n => this.bad(Number(n))));
  reason = ''; replacement = true; newNo = '';

  readonly mode = signal<'credit' | 'debit'>('credit');
  readonly dlines = signal<DebitLine[]>([]);
  debitReason = ''; debitPay = 'Cash'; debitNo = '';
  readonly moneyTypes = [{ value: 'Cash', label: 'Cash' }, { value: 'Card', label: 'Card' }, { value: 'MobileWallet', label: 'Mobile wallet' }, { value: 'BankTransfer', label: 'Bank transfer' }, { value: 'Credit', label: 'On credit' }, { value: 'Other', label: 'Other' }];

  /** Only the taxes already on the invoice can be used on a note (ZIMRA rule RCPT036). */
  readonly taxOptions = computed(() => {
    const seen = new Map<string, string>();
    for (const l of this.detail()?.lines ?? []) { const v = l.taxPercent === undefined || l.taxPercent === null ? 'exempt' : String(l.taxPercent); seen.set(v, v === 'exempt' ? 'Exempt' : v + '%'); }
    return [...seen.entries()].map(([value, label]) => ({ value, label }));
  });

  hsOk(v: string): boolean { return HS.test(v.trim()); }
  setLine(i: number, key: keyof DebitLine, v: unknown): void { this.dlines.update(a => a.map((l, n) => n === i ? { ...l, [key]: key === 'qty' || key === 'price' ? Number(v) || 0 : String(v ?? '') } : l)); }
  addLine(): void { this.dlines.update(a => [...a, this.blankLine()]); }
  removeLine(i: number): void { this.dlines.update(a => a.filter((_, n) => n !== i)); }
  private blankLine(): DebitLine { return { name: '', hs: '', qty: 1, price: 0, tax: this.taxOptions()[0]?.value ?? 'exempt' }; }

  /** Rough total for the "what will happen" box; ZIMRA's own calculation on the server is the real one. */
  debitTotal(): number {
    const inclusive = this.detail()?.linesTaxInclusive !== false;
    return this.dlines().reduce((sum, l) => {
      const gross = l.qty * l.price; const rate = l.tax === 'exempt' ? 0 : Number(l.tax) / 100;
      return sum + (inclusive ? gross : gross * (1 + rate));
    }, 0);
  }
  debitProblem(): string {
    const ls = this.dlines();
    if (!ls.length || ls.some(l => !l.name.trim() || l.qty <= 0 || l.price <= 0)) return 'Every line needs a name, a quantity and a price above zero.';
    if (ls.some(l => !!l.hs.trim() && !this.hsOk(l.hs))) return 'An HS code must have 4, 6 or 8 digits.';
    if (this.debitReason.trim().length < 3) return 'Say why the debit note is raised.';
    return '';
  }

  help(code: string): string { return VALIDATION_HELP[code] ?? 'ZIMRA flagged this receipt. Look the code up in the FDMS error list.'; }
  bad(n: number): boolean { const v = (this.codes()[n] ?? '').trim(); return !!v && !HS.test(v); }
  setCode(n: number, v: string): void { this.codes.update(c => ({ ...c, [n]: v })); }

  async ngOnInit(): Promise<void> {
    try {
      const d = await this.api.get<Detail>(`/api/v1/receipts/${this.receiptId()}/detail`);
      this.detail.set(d);
      this.codes.set(Object.fromEntries(d.lines.map(l => [l.receiptLineNo, l.receiptLineHSCode ?? ''])));
      this.newNo = d.receipt.invoiceNo + '-R';
      this.reason = d.receipt.validationErrors.some(v => v.code === 'RCPT048') ? 'HS code had the wrong length' : 'Correction of invoice ' + d.receipt.invoiceNo;
      this.replacement = true;
      this.dlines.set([this.blankLine()]);
    } catch (e) { this.toast.err(e); this.closed.emit(); }
    finally { this.loading.set(false); }
  }

  async submitDebit(): Promise<void> {
    if (this.busy() || this.debitProblem()) return;
    this.busy.set(true);
    try {
      const res = await this.api.post(`/api/v1/receipts/${this.receiptId()}/debit-note`, {
        reason: this.debitReason.trim(), moneyType: this.debitPay, invoiceNo: this.debitNo.trim() || null,
        lines: this.dlines().map(l => ({ name: l.name.trim(), hsCode: l.hs.trim() || null, quantity: l.qty, unitPrice: l.price, ...(l.tax === 'exempt' ? { isExempt: true } : { taxPercent: Number(l.tax) }) })),
      });
      this.toast.ok(doneOrHeld(res, 'Debit note issued.'));
      this.done.emit();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async submit(): Promise<void> {
    const d = this.detail(); if (!d) return;
    // only send the codes that were changed
    const fixes = d.lines.filter(l => (this.codes()[l.receiptLineNo] ?? '').trim() !== (l.receiptLineHSCode ?? '')).map(l => ({ lineNo: l.receiptLineNo, hsCode: this.codes()[l.receiptLineNo].trim() }));
    this.busy.set(true);
    try {
      const res = await this.api.post(`/api/v1/receipts/${this.receiptId()}/correct`, { reason: this.reason.trim(), fixes, issueReplacement: this.replacement, replacementInvoiceNo: this.replacement ? this.newNo.trim() || null : null });
      this.toast.ok(doneOrHeld(res, this.replacement ? 'Credit note and corrected invoice issued.' : 'Credit note issued.'));
      this.done.emit();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
