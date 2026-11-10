import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, model } from '@angular/core';
import { BankingStore } from '../core/banking.store';

/** A currency pick list fed by the platform's currency module. Use [(value)]; a value no longer on the list stays selectable so old documents still open. */
@Component({
  selector: 'app-currency-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <select class="input" [id]="id()" [attr.aria-label]="label()" [value]="value()" (change)="value.set($any($event.target).value)">
      @for (c of options(); track c.code) { <option [value]="c.code" [selected]="c.code === value()">{{ c.code }}{{ c.name ? ' · ' + c.name : '' }}</option> }
    </select>`,
})
export class CurrencySelectComponent implements OnInit {
  private readonly store = inject(BankingStore);
  readonly value = model('USD');
  readonly id = input('');
  readonly label = input('Currency');
  readonly options = computed(() => {
    const list = this.store.currencies().map(c => ({ code: c.code, name: c.name }));
    const v = (this.value() ?? '').toUpperCase();
    return v && !list.some(c => c.code === v) ? [{ code: v, name: '' }, ...list] : list;
  });
  ngOnInit(): void { void this.store.loadCurrencies(); }
}
