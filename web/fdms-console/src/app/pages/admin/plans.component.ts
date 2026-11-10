import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { PLAN_MODULES, Plan } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { EmptyComponent } from '../../shared/list.components';
import { ModalComponent } from '../../shared/modal.components';
import { limit, money } from '../../shared/public.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../../shared/ui.components';
import { UsagePricingComponent } from './usage-pricing.component';

interface PlanForm {
  id: number; code: string; name: string; description: string; priceMonthly: number; priceYearly: number; currency: string; maxDevices: number; maxUsers: number;
  maxBranches: number; maxReceiptsPerMonth: number; pricePerExtraReceipt: number; pricePerDeviceMonthly: number; pricePerThousandReceipts: number; pricePerDeviceYearly: number; receiptPricing: 'flat' | 'metered' | 'tiered';
  receiptMinimumMonthly: number; receiptCapMonthly: number; receiptTiers: { upTo: number | null; price: number }[]; modules: string[]; features: string; trialDays: number; isActive: boolean; isPublic: boolean; sortOrder: number; kind: string;
}

/** What you sell: prices, limits, trial and the feature list shown on the website and when a company registers. A limit of 0 means unlimited. */
@Component({
  selector: 'app-plans',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, EmptyComponent, UsagePricingComponent],
  template: `
    <app-page-header title="Plans" subtitle="Prices and limits shown on the website. Changing a plan does not alter invoices already issued.">
      <button class="btn" (click)="startNew()"><app-icon name="plus" size="h-4 w-4" />New plan</button>
    </app-page-header>

    <div class="card">
      <div class="overflow-x-auto">
        <table class="table">
          <thead><tr><th>Plan</th><th>Monthly</th><th>Yearly</th><th>Devices</th><th>Users</th><th>Branches</th><th>Receipts / mo</th><th>Trial</th><th>Subscribers</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
          <tbody>
            @for (p of plans(); track p.id) {
              <tr>
                <td><div class="font-medium">{{ p.name }}</div><div class="hint mt-0"><span class="code">{{ p.code }}</span>@if (p.kind === 'connector') { <span class="badge badge-info ml-1">Zoho connector</span> }@if (p.kind === 'accounting') { <span class="badge badge-info ml-1">Accounting only</span> }@if (!p.isPublic) { <span class="badge ml-1">Hidden</span> }</div></td>
                <td class="whitespace-nowrap">{{ fmt(p.priceMonthly, p.currency) }}</td>
                <td class="whitespace-nowrap">{{ fmt(p.priceYearly, p.currency) }}</td>
                <td>{{ lim(p.maxDevices) }}</td><td>{{ lim(p.maxUsers) }}</td><td>{{ lim(p.maxBranches) }}</td><td>{{ lim(p.maxReceiptsPerMonth) }}</td>
                <td>{{ p.trialDays ? p.trialDays + ' d' : '-' }}</td>
                <td><span class="badge badge-info">{{ p.subscribers }}</span></td>
                <td><app-status [value]="p.isActive ? 'ACTIVE' : 'INACTIVE'" /></td>
                <td><div class="flex justify-end gap-1.5">
                  <button class="icon-btn" (click)="edit(p)" title="Edit" aria-label="Edit"><app-icon name="edit" size="h-4 w-4" /></button>
                  <button class="icon-btn icon-btn-danger" (click)="remove(p)" title="Delete" aria-label="Delete"><app-icon name="trash" size="h-4 w-4" /></button>
                </div></td>
              </tr>
            } @empty { <tr><td colspan="11"><app-empty icon="layers" title="No plans yet" hint="Create at least one plan before approving companies." /></td></tr> }
          </tbody>
        </table>
      </div>
    </div>

    <app-usage-pricing />

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit plan' : 'New plan'" subtitle="A limit of 0 means unlimited." width="max-w-3xl" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-3">
            <label class="block"><span class="label">Code</span><input class="input font-mono lowercase" name="code" [(ngModel)]="f.code" required maxlength="30" placeholder="business" /></label>
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="name" [(ngModel)]="f.name" required maxlength="80" /></label>
            <label class="block sm:col-span-3"><span class="label">What the plan is for</span>
              <select class="input" name="kind" [(ngModel)]="f.kind">
                <option value="fiscal">Fiscalisation (the full product: devices, invoices, ZIMRA)</option>
                <option value="connector">Zoho Books connector only (no fiscal devices; only the connector's menus)</option>
                <option value="accounting">Accounting only (the books, invoices without VAT; no fiscal devices)</option>
              </select></label>
            <label class="block sm:col-span-3"><span class="label">Short description</span><input class="input" name="desc" [(ngModel)]="f.description" maxlength="300" /></label>
            <label class="block"><span class="label">Monthly price</span><input class="input" type="number" min="0" step="0.01" name="pm" [(ngModel)]="f.priceMonthly" required /></label>
            <label class="block"><span class="label">Yearly price</span><input class="input" type="number" min="0" step="0.01" name="py" [(ngModel)]="f.priceYearly" required /></label>
            <label class="block"><span class="label">Currency</span><input class="input uppercase" name="cur" [(ngModel)]="f.currency" required maxlength="3" /></label>
            <label class="block"><span class="label">Devices</span><input class="input" type="number" min="0" name="md" [(ngModel)]="f.maxDevices" /></label>
            <label class="block"><span class="label">Users</span><input class="input" type="number" min="0" name="mu" [(ngModel)]="f.maxUsers" /></label>
            <label class="block"><span class="label">Branches</span><input class="input" type="number" min="0" name="mb" [(ngModel)]="f.maxBranches" /></label>
            <label class="block"><span class="label">Receipts / month included</span><input class="input" type="number" min="0" name="mr" [(ngModel)]="f.maxReceiptsPerMonth" /></label>
            <label class="block"><span class="label">Price per extra receipt</span><input class="input" type="number" min="0" step="0.0001" name="pxr" [(ngModel)]="f.pricePerExtraReceipt" [disabled]="!f.maxReceiptsPerMonth" /><span class="hint">Charged on the next invoice for each receipt above the allowance. 0 = never.</span></label>
            <div class="sm:col-span-3 rounded-lg border border-dashed border-slate-300 p-3 dark:border-slate-700">
              <div class="mb-2 text-sm font-medium">Usage pricing (added to the plan price, which can be 0)</div>
              <div class="grid gap-3 sm:grid-cols-2">
                <label class="block"><span class="label">Per live device, per month</span><input class="input" type="number" min="0" step="0.01" name="ppd" [(ngModel)]="f.pricePerDeviceMonthly" /></label>
                <label class="block"><span class="label">Per live device, per year (yearly subscriptions)</span><input class="input" type="number" min="0" step="0.01" name="ppdy" [(ngModel)]="f.pricePerDeviceYearly" placeholder="0 = 12 x monthly" /></label>
                <label class="block sm:col-span-2"><span class="label">Receipt pricing</span>
                  <select class="input" name="rp" [(ngModel)]="f.receiptPricing">
                    <option value="flat">Included in the plan price (flat)</option>
                    <option value="metered">Per 1,000 receipts (pay as you go)</option>
                    <option value="tiered">Monthly volume bands</option>
                  </select></label>
                @if (f.receiptPricing === 'metered') {
                  <label class="block"><span class="label">Per 1,000 receipts</span><input class="input" type="number" min="0" step="0.01" name="ppk" [(ngModel)]="f.pricePerThousandReceipts" /></label>
                  <label class="block"><span class="label">Minimum per month</span><input class="input" type="number" min="0" step="0.01" name="rmin" [(ngModel)]="f.receiptMinimumMonthly" placeholder="0 = none" /></label>
                  <label class="block"><span class="label">Cap per month</span><input class="input" type="number" min="0" step="0.01" name="rcap" [(ngModel)]="f.receiptCapMonthly" placeholder="0 = no cap" /></label>
                  <p class="hint sm:col-span-2">A company that issues few receipts still pays the minimum; one that issues many never pays more than the cap.</p>
                }
                @if (f.receiptPricing === 'tiered') {
                  <div class="sm:col-span-2 space-y-2">
                    @for (t of f.receiptTiers; track $index; let i = $index; let last = $last) {
                      <div class="flex flex-wrap items-end gap-2">
                        <label class="block"><span class="label">{{ last ? 'Up to (empty = any number)' : 'Up to' }} receipts / month</span><input class="input w-40" type="number" min="1" [name]="'tu' + i" [(ngModel)]="t.upTo" /></label>
                        <label class="block"><span class="label">Price per month</span><input class="input w-32" type="number" min="0" step="0.01" [name]="'tp' + i" [(ngModel)]="t.price" /></label>
                        <button type="button" class="btn-ghost btn-sm" (click)="f.receiptTiers.splice(i, 1)">Remove</button>
                      </div>
                    }
                    <button type="button" class="btn-ghost btn-sm" [disabled]="f.receiptTiers.length >= 10" (click)="f.receiptTiers.push({ upTo: null, price: 0 })">Add band</button>
                    <p class="hint">Bands go up, e.g. up to 1,000 = 10, up to 5,000 = 30, then an empty last band = 60. The company pays the band its average monthly volume falls in.</p>
                  </div>
                }
              </div>
              <span class="hint">Usage is charged on the next invoice for what happened since the last one. Test devices are always free.</span>
            </div>
            <label class="block"><span class="label">Free trial (days)</span><input class="input" type="number" min="0" max="90" name="td" [(ngModel)]="f.trialDays" /></label>
            <label class="block"><span class="label">Order on the website</span><input class="input" type="number" name="so" [(ngModel)]="f.sortOrder" /></label>
            <div class="sm:col-span-3"><span class="label">Modules included (fiscalising is always included)</span>
              <div class="grid gap-2 sm:grid-cols-3">
                @for (m of modules; track m.key) {
                  <label class="flex items-start gap-2 rounded-lg border border-slate-200 p-2 text-sm dark:border-slate-700"><input type="checkbox" class="mt-1 h-4 w-4" [checked]="f.modules.includes(m.key)" (change)="toggleModule(f, m.key)" [disabled]="m.key === 'zoho' && f.kind === 'connector'" />
                    <span><span class="font-medium">{{ m.label }}</span><br /><span class="text-xs text-slate-500">{{ m.help }}</span></span></label>
                }
              </div>
              <span class="hint">Companies only see and can use the ticked modules. Moving a company to a plan without a module switches it off at once (its data is kept).</span></div>
            <label class="block sm:col-span-3"><span class="label">Features (one per line, shown on the pricing card)</span><textarea class="input" rows="5" name="feat" [(ngModel)]="f.features"></textarea></label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="act" [(ngModel)]="f.isActive" /> Active (can be chosen)</label>
            <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" class="h-4 w-4" name="pub" [(ngModel)]="f.isPublic" /> Show on the website and registration form</label>
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save plan</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class PlansComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly plans = signal<Plan[]>([]);
  readonly form = signal<PlanForm | null>(null);
  readonly busy = signal(false);
  readonly lim = limit;
  readonly modules = PLAN_MODULES;
  toggleModule(f: PlanForm, key: string): void { f.modules = f.modules.includes(key) ? f.modules.filter(k => k !== key) : [...f.modules, key]; }
  fmt(v: number, c: string): string { return v === 0 ? 'Free' : money(v, c); }

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> { try { this.plans.set(await this.api.get<Plan[]>('/api/v1/plans')); } catch (e) { this.toast.err(e); } }

  startNew(): void {
    this.form.set({ id: 0, code: '', name: '', description: '', priceMonthly: 0, priceYearly: 0, currency: 'USD', maxDevices: 1, maxUsers: 1, maxBranches: 1, maxReceiptsPerMonth: 0, pricePerExtraReceipt: 0, pricePerDeviceMonthly: 0, pricePerThousandReceipts: 0, pricePerDeviceYearly: 0, receiptPricing: 'flat', receiptMinimumMonthly: 0, receiptCapMonthly: 0, receiptTiers: [], modules: PLAN_MODULES.map(x => x.key), features: '', trialDays: 14, isActive: true, isPublic: true, sortOrder: (this.plans().length + 1), kind: 'fiscal' });
  }
  edit(p: Plan): void { this.form.set({ ...p, pricePerExtraReceipt: p.pricePerExtraReceipt ?? 0, pricePerDeviceMonthly: p.pricePerDeviceMonthly ?? 0, pricePerThousandReceipts: p.pricePerThousandReceipts ?? 0, pricePerDeviceYearly: p.pricePerDeviceYearly ?? 0,
      receiptPricing: p.receiptPricing ?? 'flat', receiptMinimumMonthly: p.receiptMinimumMonthly ?? 0, receiptCapMonthly: p.receiptCapMonthly ?? 0, receiptTiers: (p.receiptTiers ?? []).map(t => ({ ...t })), modules: p.modules ?? PLAN_MODULES.map(x => x.key), description: p.description ?? '', features: p.features.join('\n'), kind: p.kind || 'fiscal' }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true);
    try {
      await this.api.put('/api/v1/plans', {
        ...f, code: f.code.trim().toLowerCase(), name: f.name.trim(), description: f.description.trim() || null, currency: f.currency.trim().toUpperCase(),
        pricePerExtraReceipt: Number(f.pricePerExtraReceipt) || 0, pricePerDeviceMonthly: Number(f.pricePerDeviceMonthly) || 0, pricePerThousandReceipts: Number(f.pricePerThousandReceipts) || 0, pricePerDeviceYearly: Number(f.pricePerDeviceYearly) || 0,
        receiptMinimumMonthly: Number(f.receiptMinimumMonthly) || 0, receiptCapMonthly: Number(f.receiptCapMonthly) || 0,
        receiptTiers: f.receiptTiers.map(t => ({ upTo: t.upTo === null || (t.upTo as unknown) === '' ? null : Number(t.upTo), price: Number(t.price) || 0 })), features: f.features.split('\n').map(x => x.trim()).filter(Boolean),
      });
      this.toast.ok('Plan saved.'); this.form.set(null); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(p: Plan): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the plan "${p.name}"? A plan with subscribers cannot be deleted; deactivate it instead.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/plans/${p.id}`); this.toast.ok('Plan deleted.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
