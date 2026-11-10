import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { ItemSuggestion, QuoteLine } from '../core/models';
import { IconComponent } from './ui.components';

let seq = 0;

/** The list of items on a quote or a recurring invoice: name (with suggestions from your catalogue and past invoices), HS code, quantity, price, VAT and a running total. */
@Component({
  selector: 'app-line-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DecimalPipe, IconComponent],
  template: `
    <div class="overflow-x-auto rounded-xl border border-slate-200 dark:border-slate-800">
      <table class="table">
        <thead><tr><th>Item</th><th>HS code</th><th class="text-right">Qty</th><th class="text-right">Unit price</th><th>VAT %</th><th></th></tr></thead>
        <tbody>
          @for (l of lines(); track $index; let i = $index) {
            <tr>
              <td><input class="input min-w-40" [name]="id + 'n' + i" [(ngModel)]="l.name" [attr.list]="id + 'dl'" autocomplete="off" (input)="suggest(l.name)" (change)="pick(l)" placeholder="Item or service" /></td>
              <td><input class="input w-28 font-mono" [name]="id + 'h' + i" [(ngModel)]="l.hsCode" inputmode="numeric" maxlength="8" [class.border-red-500]="bad(l)" /></td>
              <td><input class="input w-20 text-right" type="number" min="0" step="any" [name]="id + 'q' + i" [(ngModel)]="l.quantity" /></td>
              <td><input class="input w-28 text-right" type="number" min="0" step="any" [name]="id + 'p' + i" [(ngModel)]="l.unitPrice" /></td>
              <td><div class="flex items-center gap-2">
                <input class="input w-20" type="number" min="0" max="100" step="any" [name]="id + 't' + i" [(ngModel)]="l.taxPercent" [disabled]="l.isExempt" />
                <label class="flex items-center gap-1 text-xs text-slate-500"><input type="checkbox" class="h-3.5 w-3.5" [name]="id + 'e' + i" [(ngModel)]="l.isExempt" />Exempt</label></div></td>
              <td class="text-right">@if (lines().length > 1) { <button type="button" class="icon-btn text-red-600" (click)="remove(i)" aria-label="Remove line"><app-icon name="trash" size="h-4 w-4" /></button> }</td>
            </tr>
          }
        </tbody>
      </table>
    </div>
    <datalist [id]="id + 'dl'">@for (s of suggestions(); track s.name) { <option [value]="s.name">{{ s.hsCode }} · {{ s.unitPrice }} {{ s.currency }}</option> }</datalist>
    <div class="mt-2 flex flex-wrap items-center justify-between gap-2">
      <button type="button" class="btn-ghost btn-sm" (click)="add()"><app-icon name="plus" size="h-4 w-4" />Add line</button>
      <div class="text-right"><div class="text-lg font-semibold tabular-nums">{{ total() | number:'1.2-2' }} {{ currency() }}</div><div class="hint mt-0">{{ inclusive() ? 'Prices include VAT' : 'VAT is added to the prices' }}</div></div>
    </div>
  `,
})
export class LineEditorComponent {
  private readonly api = inject(Api);
  readonly lines = model.required<QuoteLine[]>();
  readonly inclusive = input(true);
  readonly currency = input('USD');
  readonly id = 'le' + (++seq);
  readonly suggestions = signal<ItemSuggestion[]>([]);
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor() { inject(DestroyRef).onDestroy(() => clearTimeout(this.timer)); this.suggest(''); }

  static blank(tax = 15): QuoteLine { return { name: '', hsCode: '', quantity: 1, unitPrice: 0, taxPercent: tax, isExempt: false }; }

  bad(l: QuoteLine): boolean { const h = (l.hsCode ?? '').trim(); return !!h && !/^(\d{4}|\d{6}|\d{8})$/.test(h); }
  add(): void { this.lines.update(l => [...l, LineEditorComponent.blank()]); }
  remove(i: number): void { this.lines.update(l => l.filter((_, k) => k !== i)); }

  total(): number {
    return this.lines().reduce((s, l) => {
      const gross = (Number(l.quantity) || 0) * (Number(l.unitPrice) || 0), rate = l.isExempt || l.taxPercent == null ? 0 : Number(l.taxPercent) / 100;
      return s + (this.inclusive() ? gross : gross * (1 + rate));
    }, 0);
  }

  suggest(q: string): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(async () => { try { this.suggestions.set(await this.api.get<ItemSuggestion[]>('/api/v1/suggest/items', { q: q.trim() || undefined, take: 12 })); } catch { /* typing still works */ } }, 200);
  }

  /** A suggestion picked from the list fills the HS code, price and tax. */
  pick(l: QuoteLine): void {
    const s = this.suggestions().find(x => x.name.toLowerCase() === l.name.trim().toLowerCase());
    if (!s) return;
    if (!(l.hsCode ?? '').trim() && s.hsCode) l.hsCode = s.hsCode;
    if (!Number(l.unitPrice)) l.unitPrice = s.unitPrice;
    l.isExempt = s.isExempt; if (!s.isExempt && s.taxPercent != null) l.taxPercent = s.taxPercent;
    this.lines.update(ls => [...ls]);
  }
}
