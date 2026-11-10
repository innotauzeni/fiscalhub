import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { BankAccount, BankingStore } from '../core/banking.store';

/** Choose a bank and its accounts appear; choose an account and it is handed to the page. Banks and accounts are kept under Banks. */
@Component({
  selector: 'app-bank-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (store.banks().length) {
      <div class="grid gap-3 sm:grid-cols-2">
        <label class="block"><span class="label">Bank</span>
          <select class="input" [value]="bankId()" (change)="pickBank($any($event.target).value)">
            <option value="">Choose a bank…</option>
            @for (b of banks(); track b.id) { <option [value]="b.id">{{ b.name }}</option> }
          </select></label>
        <label class="block"><span class="label">Account</span>
          <select class="input" [value]="accountId()" [disabled]="!bankId()" (change)="pickAccount($any($event.target).value)">
            <option value="">{{ bankId() ? (accounts().length ? 'Choose an account…' : 'No accounts for this bank') : 'Choose a bank first' }}</option>
            @for (a of accounts(); track a.id) { <option [value]="a.id">{{ a.currency }} · {{ a.accountNumber }} · {{ a.accountName }}{{ a.isDefault ? ' (default)' : '' }}</option> }
          </select></label>
      </div>
    } @else {
      <p class="rounded-xl bg-slate-50 px-4 py-3 text-sm text-slate-600 dark:bg-slate-800/50 dark:text-slate-300">
        No banks yet. @if (auth.can('banks.manage')) { <a routerLink="/admin/banks" class="font-medium text-brand-600 underline">Add your banks and accounts</a> } @else { Ask your administrator to add them. }</p>
    }`,
})
export class BankPickerComponent implements OnInit {
  readonly store = inject(BankingStore);
  readonly auth = inject(AuthService);
  /** Only offer accounts in this currency (optional). */
  readonly currency = input<string | null>(null);
  readonly picked = output<BankAccount>();
  readonly bankId = signal('');
  readonly accountId = signal('');

  readonly banks = computed(() => this.store.banks().filter(b => b.isActive && b.accounts.some(a => a.isActive)));
  readonly accounts = computed(() => {
    const cur = this.currency()?.toUpperCase();
    return (this.store.banks().find(b => b.id === this.bankId())?.accounts ?? []).filter(a => a.isActive && (!cur || a.currency === cur));
  });

  ngOnInit(): void { void this.store.loadBanks(); }

  pickBank(id: string): void {
    this.bankId.set(id); this.accountId.set('');
    const only = this.accounts();
    if (only.length === 1) this.pickAccount(only[0].id);   // a bank with a single account: nothing more to choose
  }

  pickAccount(id: string): void {
    this.accountId.set(id);
    const a = this.accounts().find(x => x.id === id);
    if (a) this.picked.emit(a);
  }
}
