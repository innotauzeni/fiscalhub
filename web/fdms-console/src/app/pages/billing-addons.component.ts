import { ChangeDetectionStrategy, Component, OnInit, inject, output, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { DialogService, ToastService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';

interface Addons { devices: number; users: number; branches: number; devicePrice: number; userPrice: number; branchPrice: number; currency: string; available: boolean; unavailableReason: string | null;
  pendingInvoiceId: string | null; pendingInvoiceNumber: string | null; pendingTotal: number | null; pendingSummary: string | null; periodEnd: string | null; }

/** Extra devices, users and branches on top of the plan, paid monthly with the subscription. Bought on an invoice for the rest of the period; active once paid. */
@Component({
  selector: 'app-billing-addons',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DecimalPipe, IconComponent],
  template: `
    @if (a(); as a) {
      @if (a.available || a.devices + a.users + a.branches > 0) {
        <div class="card mb-5">
          <div class="card-title flex items-center gap-2"><app-icon name="plus" size="h-4 w-4" />Add-ons</div>
          <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Need a little more room without changing plan? Add devices, users or branches. You pay for the rest of this period now
            @if (a.periodEnd) { (to {{ a.periodEnd | date: 'd MMM yyyy' }}) }, and then with each renewal.</p>
          @if (a.devices + a.users + a.branches > 0) {
            <p class="mb-3 text-sm">You have: <strong>{{ have(a) }}</strong>.</p>
          }
          @if (a.pendingInvoiceId) {
            <div class="mb-3 flex flex-wrap items-center justify-between gap-2 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-900/25 dark:text-amber-200">
              <span>Waiting for payment: <strong>{{ a.pendingSummary }}</strong> on invoice {{ a.pendingInvoiceNumber }} ({{ a.pendingTotal | number: '1.2-2' }} {{ a.currency }}). Pay it above and they switch on at once.</span>
              <button type="button" class="btn-ghost" (click)="cancel()">Cancel</button>
            </div>
          }
          @if (a.available && !a.pendingInvoiceId) {
            <div class="grid gap-3 sm:grid-cols-3">
              @if (a.devicePrice > 0) { <label class="block"><span class="label">Extra devices · {{ a.devicePrice | number: '1.2-2' }} {{ a.currency }}/month each</span><input class="input" type="number" min="0" max="100" name="d" [(ngModel)]="q.devices" /></label> }
              @if (a.userPrice > 0) { <label class="block"><span class="label">Extra users · {{ a.userPrice | number: '1.2-2' }} {{ a.currency }}/month each</span><input class="input" type="number" min="0" max="100" name="u" [(ngModel)]="q.users" /></label> }
              @if (a.branchPrice > 0) { <label class="block"><span class="label">Extra branches · {{ a.branchPrice | number: '1.2-2' }} {{ a.currency }}/month each</span><input class="input" type="number" min="0" max="100" name="b" [(ngModel)]="q.branches" /></label> }
            </div>
            <div class="mt-3 flex flex-wrap items-center justify-between gap-2">
              <span class="text-sm text-slate-500">{{ monthly(a) > 0 ? (monthly(a) | number: '1.2-2') + ' ' + a.currency + ' a month (plus VAT)' : '' }}</span>
              <div class="flex gap-2">
                @if (a.devices + a.users + a.branches > 0) { <button type="button" class="btn-ghost" [disabled]="busy() || total() === 0" (click)="remove()">Remove these</button> }
                <button type="button" class="btn" [disabled]="busy() || total() === 0" (click)="buy()">Add</button>
              </div>
            </div>
          } @else if (!a.available) {
            <p class="hint">{{ a.unavailableReason }}</p>
          }
        </div>
      }
    }
  `,
})
export class BillingAddonsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly changed = output<void>();
  readonly a = signal<Addons | null>(null);
  readonly busy = signal(false);
  q = { devices: 0, users: 0, branches: 0 };

  ngOnInit(): void { void this.load(); }
  async load(): Promise<void> { try { this.a.set(await this.api.get<Addons>('/api/v1/billing/addons')); } catch { /* the card stays hidden */ } }

  have(a: Addons): string {
    return [[a.devices, 'device'], [a.users, 'user'], [a.branches, 'branch']].filter(x => (x[0] as number) > 0)
      .map(([n, w]) => `${n} extra ${w}${n === 1 ? '' : w === 'branch' ? 'es' : 's'}`).join(', ');
  }
  total(): number { return (Number(this.q.devices) || 0) + (Number(this.q.users) || 0) + (Number(this.q.branches) || 0); }
  monthly(a: Addons): number { return (Number(this.q.devices) || 0) * a.devicePrice + (Number(this.q.users) || 0) * a.userPrice + (Number(this.q.branches) || 0) * a.branchPrice; }
  private body() { return { devices: Number(this.q.devices) || 0, users: Number(this.q.users) || 0, branches: Number(this.q.branches) || 0 }; }

  async buy(): Promise<void> {
    this.busy.set(true);
    try { this.a.set(await this.api.post<Addons>('/api/v1/billing/addons', this.body())); this.q = { devices: 0, users: 0, branches: 0 }; this.toast.ok('Invoice ready: pay it and the add-ons switch on at once.'); this.changed.emit(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(): Promise<void> {
    if (!await this.dialog.confirm('Stop paying for these add-ons? The room goes now; nothing is refunded for this period.', 'Remove', true)) return;
    this.busy.set(true);
    try { this.a.set(await this.api.post<Addons>('/api/v1/billing/addons/remove', this.body())); this.q = { devices: 0, users: 0, branches: 0 }; this.toast.ok('Removed.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async cancel(): Promise<void> {
    try { this.a.set(await this.api.delete<Addons>('/api/v1/billing/addons/pending')); this.toast.ok('Add-on invoice cancelled.'); this.changed.emit(); } catch (e) { this.toast.err(e); }
  }
}
