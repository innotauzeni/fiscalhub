import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { BankingStore, Currency } from '../../core/banking.store';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface CurrencyForm { code: string; name: string; symbol: string; isActive: boolean; sortOrder: number; isNew: boolean }

/** The platform's list of currencies (platform operator). Every currency pick list in the console, and every bank account, uses this list. */
@Component({
  selector: 'app-currencies',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Currencies" subtitle="The currencies companies can choose from on invoices, quotes and bank accounts.">
      <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />Add currency</button>
    </app-page-header>

    <div class="card">
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Code</th><th>Name</th><th>Symbol</th><th>Order</th><th>Bank accounts</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (c of items(); track c.code) {
              <tr>
                <td><span class="code">{{ c.code }}</span></td><td class="font-medium">{{ c.name }}</td><td>{{ c.symbol }}</td><td>{{ c.sortOrder }}</td><td>{{ c.accountsUsing }}</td>
                <td><span class="badge" [class.badge-ok]="c.isActive" [class.badge-warn]="!c.isActive">{{ c.isActive ? 'Offered' : 'Switched off' }}</span></td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn" (click)="edit(c)" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-danger" (click)="remove(c)" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="7" class="text-slate-500">Loading…</td></tr> }
          </tbody>
        </table>
      </div>
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.isNew ? 'Add currency' : 'Edit ' + f.code" width="max-w-md" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Code</span><input class="input font-mono uppercase" name="cc" [(ngModel)]="f.code" [disabled]="!f.isNew" required maxlength="3" placeholder="USD" /></label>
            <label class="block"><span class="label">Symbol</span><input class="input" name="cs" [(ngModel)]="f.symbol" maxlength="6" placeholder="$" /></label>
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="cn" [(ngModel)]="f.name" required maxlength="60" placeholder="US Dollar" /></label>
            <label class="block"><span class="label">Order in lists</span><input type="number" class="input" name="co" [(ngModel)]="f.sortOrder" min="0" max="9999" /></label>
            <label class="flex items-center gap-2 pt-6 text-sm"><input type="checkbox" class="h-4 w-4" name="ca" [(ngModel)]="f.isActive" /> Offered to companies</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class CurrenciesComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly store = inject(BankingStore);
  readonly items = signal<Currency[]>([]);
  readonly form = signal<CurrencyForm | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');

  ngOnInit(): Promise<void> { return this.load(); }

  private async load(): Promise<void> {
    try { this.items.set(await this.api.get<Currency[]>('/api/v1/currencies/all')); await this.store.loadCurrencies(true); } catch (e) { this.toast.err(e); }
  }

  start(): void { this.error.set(''); this.form.set({ code: '', name: '', symbol: '', isActive: true, sortOrder: (this.items().at(-1)?.sortOrder ?? 0) + 1, isNew: true }); }
  edit(c: Currency): void { this.error.set(''); this.form.set({ ...c, isNew: false }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try { await this.api.put('/api/v1/currencies', { code: f.code.trim(), name: f.name.trim(), symbol: f.symbol.trim() || null, isActive: f.isActive, sortOrder: Number(f.sortOrder) }); this.form.set(null); this.toast.ok('Currency saved.'); await this.load(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async remove(c: Currency): Promise<void> {
    if (!(await this.dialog.confirm(`Delete ${c.code}? Switching it off instead keeps old documents readable.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/currencies/${c.code}`); this.toast.ok('Currency deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
