import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { BridgeGrant, BridgeGrants } from '../core/integrations.models';
import { DialogService, ToastService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';

/** The company decides which partners' systems may use its Zoho Books through FiscalHub: which modules, read only or read and write. */
@Component({
  selector: 'app-zoho-bridge-grants',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DecimalPipe, IconComponent],
  template: `
    @if (g(); as g) {
      <div class="card mt-4">
        <div class="card-title flex items-center gap-2"><app-icon name="link" size="h-4 w-4" />Partner access (Zoho bridge)</div>
        <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Let a partner's system (an ERP, a shop, a payroll or CRM system) read or update your Zoho Books through FiscalHub.
          They never get your Zoho password or sign-in, only what you tick below, and you can withdraw it at any time.</p>
        @if (!g.zohoFullAccess) {
          <p class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-900/25 dark:text-amber-200">Your Zoho connection was made before the bridge existed. Disconnect and connect Zoho Books once more so partners can reach the modules you share.</p>
        }
        @if (g.charged) { <p class="hint mb-3">Sharing costs {{ g.monthlyFee | number: '1.2-2' }} {{ g.currency }} a month, added to your subscription invoice.</p> }

        <ul class="mb-4 divide-y divide-slate-100 text-sm dark:divide-slate-800">
          @for (x of g.grants; track x.id) {
            <li class="flex flex-wrap items-center gap-3 py-2">
              <div class="min-w-0 flex-1"><div class="font-medium">{{ x.partnerName }} <span class="text-xs text-slate-500">{{ x.partnerCode }}</span></div>
                <div class="text-xs text-slate-500">{{ labels(x) }} · {{ x.canWrite ? 'read and write' : 'read only' }} · {{ x.callsThisMonth }} calls this month</div></div>
              <button type="button" class="btn-ghost px-2! py-1! text-xs" (click)="edit(x)">Change</button>
              <button type="button" class="btn-ghost px-2! py-1! text-xs text-red-600" (click)="revoke(x)">Withdraw</button>
            </li>
          } @empty { <li class="py-3 text-slate-500">No partner has access.</li> }
        </ul>

        <form class="rounded-lg border border-slate-200 p-3 dark:border-slate-700" (ngSubmit)="save()">
          <div class="grid gap-3 sm:grid-cols-2">
            <label class="block"><span class="label">Partner code</span><input class="input uppercase" name="code" [(ngModel)]="code" placeholder="The code the partner gave you" /></label>
            <label class="mt-6 flex items-center gap-2 text-sm"><input type="checkbox" name="write" [(ngModel)]="canWrite" />May also create and update records</label>
          </div>
          <div class="mt-3 grid grid-cols-2 gap-1 text-sm sm:grid-cols-3">
            @for (m of g.modules; track m.key) { <label class="flex items-center gap-2"><input type="checkbox" [checked]="chosen.has(m.key)" (change)="toggle(m.key)" />{{ m.label }}</label> }
          </div>
          <div class="mt-3 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Share</button></div>
        </form>
      </div>
    }
  `,
})
export class ZohoBridgeGrantsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly g = signal<BridgeGrants | null>(null);
  readonly busy = signal(false);
  code = '';
  canWrite = false;
  chosen = new Set<string>(['contacts', 'items']);

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> { try { this.g.set(await this.api.get<BridgeGrants>('/api/v1/integrations/zoho/bridge')); } catch (e) { this.toast.err(e); } }

  labels(x: BridgeGrant): string { const all = this.g()?.modules ?? []; return x.modules.map(k => all.find(m => m.key === k)?.label ?? k).join(', '); }
  toggle(key: string): void { if (this.chosen.has(key)) this.chosen.delete(key); else this.chosen.add(key); }
  edit(x: BridgeGrant): void { this.code = x.partnerCode; this.canWrite = x.canWrite; this.chosen = new Set(x.modules); }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const r = await this.api.put<BridgeGrant>('/api/v1/integrations/zoho/bridge', { partnerCode: this.code, modules: [...this.chosen], canWrite: this.canWrite });
      this.toast.ok(`${r.partnerName} can now use what you ticked.`); this.code = ''; await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async revoke(x: BridgeGrant): Promise<void> {
    if (!(await this.dialog.confirm(`Withdraw ${x.partnerName}'s access to your Zoho Books? Their system stops at once.`, 'Withdraw', true))) return;
    try { await this.api.delete(`/api/v1/integrations/zoho/bridge/${x.id}`); this.toast.ok('Access withdrawn.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
