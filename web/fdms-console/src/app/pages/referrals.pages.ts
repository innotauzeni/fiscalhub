import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { appUrl } from '../core/env';
import { money } from '../core/accounting.store';
import { DialogService, ToastService } from '../core/ui.services';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

interface Amount { currency: string; amount: number }
const sum = (a: Amount[] | null | undefined): string => (a?.length ? a.map(x => `${money(x.amount)} ${x.currency}`).join(' + ') : '0.00');

interface Tier { key: string; label: string; referrals: number; freeMonths: number; perk?: string | null; bonusCoins: number }
interface TierProgress extends Tier { reached: boolean; grantedAt?: string | null }
interface ShopItem { key: string; title: string; description?: string | null; cost: number; kind: string; value: number; enabled: boolean }
interface CoinEntry { id: string; amount: number; kind: string; description: string; createdAt: string }
interface CoinInvoice { id: string; number: string; total: number; currency: string; coins: number }
interface Overview {
  enabled: boolean; code: string; referralPath: string; percent: number; invoicesRewarded: number; description?: string | null; referred: number; pending: number; paying: number;
  earned: Amount[]; paid: Amount[]; owed: Amount[]; tiers: TierProgress[];
  companies: { name: string; status: string; paying: boolean; since: string }[];
  rewards: { id: string; company: string; amount: number; currency: string; percent: number; status: string; createdAt: string; paidAt?: string | null; reference?: string | null; coins: number }[];
  rewardType: 'coins' | 'money'; coinsPerUnit: number; coins: number; shop?: ShopItem[]; coinHistory?: CoinEntry[]; bonus?: { devices: number; users: number; branches: number }; payableInvoices?: CoinInvoice[]; shareLink?: string | null;
}
interface Program { enabled: boolean; percent: number; invoicesRewarded: number; description?: string | null; tiers: Tier[]; rewardType: 'coins' | 'money'; coinsPerUnit: number; shop?: ShopItem[] }
interface AdminReward { id: string; referrer: string; referred: string; amount: number; currency: string; status: string; createdAt: string; paidAt?: string | null; reference?: string | null; coins: number }
interface Holder { clientId: string; company: string; balance: number }
interface Admin { program: Program; referrers: number; referredCompanies: number; earned: Amount[]; paid: Amount[]; owed: Amount[]; rewards: AdminReward[]; coinsIssued: number; coinsSpent: number; holders?: Holder[] }

const KINDS: Record<string, string> = { free_days: 'Extra days', extra_device: 'Extra devices', extra_user: 'Extra users', extra_branch: 'Extra branches', pay_invoice: 'Pay an invoice' };
const KIND_ICON: Record<string, string> = { free_days: 'calendar', extra_device: 'cpu', extra_user: 'users', extra_branch: 'building', pay_invoice: 'receipt' };

/** Refer and earn: the company's code and link, its coins and what they buy, the companies it brought, and the levels it unlocks. */
@Component({
  selector: 'app-referrals',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, IconComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Refer and earn" subtitle="Bring other companies to the gateway and earn rewards you can spend here." />
    @if (o(); as d) {
      @if (!d.enabled) { <div class="mb-5 rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-200">The referral program is switched off at the moment.</div> }
      <section class="relative overflow-hidden rounded-3xl bg-linear-to-br from-blue-600 to-violet-700 p-6 text-white shadow-lg sm:p-8">
        <div class="pointer-events-none absolute -right-16 -top-16 h-56 w-56 rounded-full bg-white/10 blur-2xl"></div>
        <div class="grid gap-6 lg:grid-cols-[1fr_auto] lg:items-end">
          <div>
            <div class="text-sm font-medium text-blue-100">Refer a company, earn</div>
            <div class="mt-1 text-3xl font-bold">{{ d.rewardType === 'coins' ? d.percent + '% back in coins' : d.percent + '% of what they pay' }}</div>
            <p class="mt-1 max-w-xl text-sm text-blue-100">{{ d.description || defaultText(d) }}</p>
          </div>
          @if (d.rewardType === 'coins') {
            <div class="rounded-2xl bg-white/15 px-5 py-4 text-right">
              <div class="text-xs font-semibold uppercase tracking-wider text-blue-100">Your coins</div>
              <div class="text-4xl font-extrabold tabular-nums">{{ d.coins.toLocaleString() }}</div>
              <div class="text-xs text-blue-100">{{ d.coinsPerUnit }} coins = 1.00</div>
            </div>
          }
        </div>
        <div class="mt-5 grid gap-3 lg:grid-cols-[auto_1fr]">
          <button type="button" class="flex items-center gap-3 rounded-2xl bg-white/15 px-4 py-3 text-left transition hover:bg-white/25" (click)="copy(d.code, 'Code copied.')">
            <span><span class="block text-xs text-blue-100">Your code</span><span class="font-mono text-2xl font-bold tracking-widest">{{ d.code }}</span></span><app-icon name="copy" />
          </button>
          <div class="flex min-w-0 flex-wrap items-center gap-2 rounded-2xl bg-white/15 px-4 py-3">
            <span class="min-w-0 flex-1 truncate font-mono text-sm">{{ link() }}</span>
            <button type="button" class="rounded-xl bg-white px-3 py-1.5 text-sm font-semibold text-brand-700" (click)="copy(link(), 'Link copied.')">Copy link</button>
            <a class="rounded-xl bg-white/20 px-3 py-1.5 text-sm font-semibold" [href]="whatsApp()" target="_blank" rel="noopener">WhatsApp</a>
          </div>
        </div>
      </section>

      <div class="mt-5 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <div class="card"><div class="text-xs text-slate-500">Referred</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ d.referred }}</div><div class="text-xs text-slate-500">{{ d.pending }} waiting for approval</div></div>
        <div class="card"><div class="text-xs text-slate-500">Paying</div><div class="mt-1 text-2xl font-semibold tabular-nums text-emerald-600">{{ d.paying }}</div><div class="text-xs text-slate-500">count towards your level</div></div>
        @if (d.rewardType === 'coins') {
          <div class="card"><div class="text-xs text-slate-500">Coins earned</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ earnedCoins().toLocaleString() }}</div></div>
          <div class="card"><div class="text-xs text-slate-500">Extra slots</div><div class="mt-1 text-lg font-semibold">{{ d.bonus?.devices ?? 0 }} devices · {{ d.bonus?.users ?? 0 }} users · {{ d.bonus?.branches ?? 0 }} branches</div></div>
        } @else {
          <div class="card"><div class="text-xs text-slate-500">Earned</div><div class="mt-1 text-lg font-semibold tabular-nums">{{ sum(d.earned) }}</div></div>
          <div class="card"><div class="text-xs text-slate-500">To be paid to you</div><div class="mt-1 text-lg font-semibold tabular-nums text-amber-700 dark:text-amber-300">{{ sum(d.owed) }}</div><div class="text-xs text-slate-500">paid so far {{ sum(d.paid) }}</div></div>
        }
      </div>

      @if (d.shop?.length) {
        <section class="mt-5 card">
          <div class="flex flex-wrap items-center justify-between gap-2"><h2 class="text-base font-semibold">Spend your coins</h2><span class="text-sm text-slate-500">You have <b class="tabular-nums">{{ d.coins.toLocaleString() }}</b> coins</span></div>
          <div class="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            @for (s of d.shop; track s.key) {
              <div class="flex flex-col rounded-2xl border border-slate-200 p-4 dark:border-slate-700">
                <div class="flex items-center gap-3">
                  <span class="flex h-10 w-10 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-500/15"><app-icon [name]="kindIcon(s.kind)" /></span>
                  <div class="min-w-0"><div class="font-semibold">{{ s.title }}</div><div class="text-xs text-slate-500">{{ s.description }}</div></div>
                </div>
                <div class="mt-auto flex items-center justify-between gap-2 pt-4">
                  @if (s.kind === 'pay_invoice') {
                    @if (d.payableInvoices?.length) {
                      <select class="input py-1.5 text-sm" [(ngModel)]="invoiceChoice" [name]="'inv-' + s.key">
                        @for (i of d.payableInvoices; track i.id) { <option [value]="i.id">{{ i.number }} · {{ i.coins.toLocaleString() }} coins</option> }
                      </select>
                    } @else { <span class="text-sm text-slate-500">No open invoice</span> }
                  } @else { <span class="text-lg font-bold tabular-nums">{{ s.cost.toLocaleString() }} <span class="text-xs font-medium text-slate-500">coins</span></span> }
                  <button type="button" class="btn btn-sm" [disabled]="busy() || !canBuy(s, d)" (click)="redeem(s, d)">Get it</button>
                </div>
              </div>
            }
          </div>
        </section>
      }

      @if (d.tiers.length) {
        <section class="mt-5 card">
          <h2 class="text-base font-semibold">Your level</h2>
          @if (next(); as n) { <p class="mt-1 text-sm font-medium text-brand-700 dark:text-brand-100">{{ n.left }} more paying {{ n.left === 1 ? 'company' : 'companies' }} to reach {{ n.tier.label }}{{ n.tier.perk ? ': ' + n.tier.perk : '' }}</p> }
          <div class="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            @for (t of d.tiers; track t.key) {
              <div class="rounded-2xl border p-4" [class]="t.reached ? 'border-emerald-300 bg-emerald-50/60 dark:border-emerald-500/40 dark:bg-emerald-500/10' : 'border-slate-200 dark:border-slate-700'">
                <div class="flex items-center gap-2"><span [class]="t.reached ? 'text-emerald-600' : 'text-slate-400'"><app-icon [name]="t.reached ? 'check' : 'lock'" size="h-4 w-4" /></span><span class="font-semibold">{{ t.label }}</span></div>
                <div class="mt-1 text-sm text-slate-600 dark:text-slate-300">{{ t.referrals }} paying {{ t.referrals === 1 ? 'company' : 'companies' }}</div>
                <div class="mt-1 text-xs text-slate-500">{{ perkText(t) }}</div>
                @if (t.reached && t.grantedAt) { <div class="mt-2 text-xs font-medium text-emerald-700 dark:text-emerald-300">Unlocked {{ t.grantedAt | date: 'd MMM y' }}</div> }
                <div class="mt-3 h-1.5 rounded-full bg-slate-200 dark:bg-slate-700"><div class="h-full rounded-full bg-linear-to-r from-blue-500 to-violet-500" [style.width.%]="pct(d.paying, t.referrals)"></div></div>
              </div>
            }
          </div>
        </section>
      }

      <div class="mt-5 grid gap-5 lg:grid-cols-2">
        <section class="card">
          <h2 class="text-base font-semibold">Companies you referred</h2>
          <ul class="mt-2 divide-y divide-slate-100 text-sm dark:divide-slate-800">
            @for (c of d.companies; track $index) { <li class="flex items-center justify-between gap-3 py-2.5"><span class="min-w-0"><span class="block truncate font-medium">{{ c.name }}</span><span class="text-xs text-slate-500">since {{ c.since | date: 'd MMM y' }}</span></span><span class="badge" [class]="c.paying ? 'badge-ok' : c.status === 'Suspended' ? 'badge-bad' : 'badge-warn'">{{ c.status }}</span></li> }
            @empty { <li class="py-4 text-slate-500">Nobody yet. Share your link or code with a company that needs to fiscalise.</li> }
          </ul>
        </section>
        @if (d.rewardType === 'coins') {
          <section class="card">
            <h2 class="text-base font-semibold">Coin history</h2>
            <ul class="mt-2 divide-y divide-slate-100 text-sm dark:divide-slate-800">
              @for (c of d.coinHistory ?? []; track c.id) { <li class="flex items-center justify-between gap-3 py-2.5"><span class="min-w-0"><span class="block truncate font-medium">{{ c.description }}</span><span class="text-xs text-slate-500">{{ c.createdAt | date: 'd MMM y, HH:mm' }}</span></span><span class="font-semibold tabular-nums" [class]="c.amount > 0 ? 'text-emerald-600' : 'text-slate-600 dark:text-slate-300'">{{ c.amount > 0 ? '+' : '' }}{{ c.amount.toLocaleString() }}</span></li> }
              @empty { <li class="py-4 text-slate-500">Coins appear here when a company you referred pays its invoice.</li> }
            </ul>
          </section>
        } @else {
          <section class="card">
            <h2 class="text-base font-semibold">Rewards</h2>
            <ul class="mt-2 divide-y divide-slate-100 text-sm dark:divide-slate-800">
              @for (r of d.rewards; track r.id) { <li class="flex items-center justify-between gap-3 py-2.5"><span class="min-w-0"><span class="block truncate font-medium">{{ r.company }}</span><span class="text-xs text-slate-500">{{ r.percent }}% · {{ r.createdAt | date: 'd MMM y' }}{{ r.reference ? ' · ' + r.reference : '' }}</span></span><span class="text-right"><span class="block font-semibold tabular-nums">{{ r.coins ? r.coins + ' coins' : money(r.amount) + ' ' + r.currency }}</span><span class="badge" [class]="r.status === 'Earned' ? 'badge-warn' : 'badge-ok'">{{ r.status === 'Earned' ? 'Pending' : r.status }}</span></span></li> }
              @empty { <li class="py-4 text-slate-500">Rewards appear here when a company you referred pays its invoice.</li> }
            </ul>
          </section>
        }
      </div>
    }
  `,
})
export class ReferralsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly o = signal<Overview | null>(null);
  readonly busy = signal(false);
  readonly sum = sum;
  readonly money = money;
  invoiceChoice = '';
  readonly link = computed(() => this.o()?.shareLink || appUrl(this.o()?.referralPath ?? ''));
  readonly whatsApp = computed(() => 'https://wa.me/?text=' + encodeURIComponent(`Fiscalise your invoices with FDMS Gateway. Register with my code ${this.o()?.code}: ${this.link()}`));
  readonly earnedCoins = computed(() => (this.o()?.coinHistory ?? []).filter(c => c.amount > 0).reduce((s, c) => s + c.amount, 0));
  readonly next = computed(() => {
    const d = this.o(); if (!d) return null;
    const t = d.tiers.find(x => !x.reached);
    return t ? { tier: t, left: Math.max(1, t.referrals - d.paying) } : null;
  });

  defaultText(d: Overview): string {
    const inv = d.invoicesRewarded === 1 ? 'the first paid invoice' : `the first ${d.invoicesRewarded} paid invoices`;
    return d.rewardType === 'coins' ? `On ${inv} of every company you bring, as coins to spend on extra time, more devices or users, or your own invoice.` : `On ${inv} of every company you bring.`;
  }
  perkText(t: Tier): string { return [t.perk, t.freeMonths ? `${t.freeMonths} free month(s)` : '', t.bonusCoins ? `+${t.bonusCoins} coins` : ''].filter(Boolean).join(' · '); }
  kindIcon(k: string): string { return KIND_ICON[k] ?? 'bolt'; }
  pct(have: number, need: number): number { return need <= 0 ? 100 : Math.min(100, Math.round((have / need) * 100)); }
  canBuy(s: ShopItem, d: Overview): boolean {
    if (s.kind !== 'pay_invoice') return d.coins >= s.cost;
    const inv = d.payableInvoices?.find(i => i.id === (this.invoiceChoice || d.payableInvoices?.[0]?.id));
    return !!inv && d.coins >= inv.coins;
  }

  async ngOnInit(): Promise<void> { await this.load(); }
  private async load(): Promise<void> {
    try { const d = await this.api.get<Overview>('/api/v1/referrals'); this.o.set(d); if (!this.invoiceChoice && d.payableInvoices?.length) this.invoiceChoice = d.payableInvoices[0].id; } catch (e) { this.toast.err(e); }
  }

  async redeem(s: ShopItem, d: Overview): Promise<void> {
    const inv = s.kind === 'pay_invoice' ? d.payableInvoices?.find(i => i.id === (this.invoiceChoice || d.payableInvoices?.[0]?.id)) : undefined;
    const cost = inv ? inv.coins : s.cost;
    if (!(await this.dialog.confirm(`${s.title}${inv ? ' ' + inv.number : ''} for ${cost.toLocaleString()} coins?`, 'Spend coins'))) return;
    this.busy.set(true);
    try { const r = await this.api.post<{ message: string; balance: number }>('/api/v1/referrals/redeem', { key: s.key, invoiceId: inv?.id ?? null }); this.toast.ok(r.message); await this.load(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async copy(text: string, done: string): Promise<void> {
    try { await navigator.clipboard.writeText(text); this.toast.ok(done); } catch { this.toast.err('Could not copy. Select the text and copy it by hand.'); }
  }
}

/** The operator's side: how rewards are given (coins or money), the levels, the coins shop, coin balances, and paying out money rewards. */
@Component({
  selector: 'app-referral-program',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, DecimalPipe, FormsModule, PageHeaderComponent],
  template: `
    <app-page-header title="Referral program" subtitle="Reward companies that bring other companies: coins to spend here (costs you no cash) or money you pay out." />
    @if (a(); as d) {
      <div class="mb-5 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <div class="card"><div class="text-xs text-slate-500">Companies referring</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ d.referrers }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Companies brought</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ d.referredCompanies }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Coins issued / spent</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ d.coinsIssued.toLocaleString() }} / {{ d.coinsSpent.toLocaleString() }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Money to pay out</div><div class="mt-1 text-lg font-semibold tabular-nums text-amber-700 dark:text-amber-300">{{ sum(d.owed) }}</div><div class="text-xs text-slate-500">paid {{ sum(d.paid) }}</div></div>
      </div>

      <section class="card">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <h2 class="text-base font-semibold">Rules</h2>
          <label class="flex items-center gap-2 text-sm font-medium"><input type="checkbox" class="h-4 w-4 rounded" [(ngModel)]="p.enabled" name="en" />Program is on</label>
        </div>
        <div class="mt-4 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <label class="block"><span class="label">Reward given as</span>
            <select class="input" [(ngModel)]="p.rewardType" name="rt"><option value="coins">Coins (spent in the gateway)</option><option value="money">Money (you pay it out)</option></select></label>
          <label class="block"><span class="label">Reward (% of the invoice before tax)</span><input class="input" type="number" min="0" max="100" step="0.5" [(ngModel)]="p.percent" name="pc" /></label>
          <label class="block"><span class="label">Invoices rewarded per company</span><input class="input" type="number" min="1" max="24" [(ngModel)]="p.invoicesRewarded" name="ir" /></label>
          <label class="block"><span class="label">Coins per 1.00</span><input class="input" type="number" min="1" max="1000" [(ngModel)]="p.coinsPerUnit" name="cpu" [disabled]="p.rewardType !== 'coins'" /></label>
          <label class="block sm:col-span-2 lg:col-span-4"><span class="label">Message shown to companies (optional)</span><input class="input" maxlength="500" [(ngModel)]="p.description" name="ds" /></label>
        </div>
        @if (p.rewardType === 'coins') { <p class="hint">Example: a 100.00 invoice at {{ p.percent }}% earns {{ (100 * p.percent / 100 * p.coinsPerUnit) | number: '1.0-0' }} coins.</p> }

        <h3 class="mt-6 text-sm font-semibold">Levels</h3>
        <p class="text-xs text-slate-500">Reaching a level adds its free months to the company's subscription and its bonus coins, once.</p>
        <div class="mt-3 space-y-3">
          @for (t of p.tiers; track $index; let i = $index) {
            <div class="grid gap-3 rounded-xl border border-slate-200 p-3 sm:grid-cols-[1fr_7rem_7rem_7rem_2fr_auto] dark:border-slate-700">
              <label class="block"><span class="label">Name</span><input class="input" [(ngModel)]="t.label" [name]="'l' + i" /></label>
              <label class="block"><span class="label">Paying needed</span><input class="input" type="number" min="1" [(ngModel)]="t.referrals" [name]="'r' + i" /></label>
              <label class="block"><span class="label">Free months</span><input class="input" type="number" min="0" max="24" [(ngModel)]="t.freeMonths" [name]="'m' + i" /></label>
              <label class="block"><span class="label">Bonus coins</span><input class="input" type="number" min="0" [(ngModel)]="t.bonusCoins" [name]="'b' + i" /></label>
              <label class="block"><span class="label">Perk shown</span><input class="input" [(ngModel)]="t.perk" [name]="'p' + i" /></label>
              <button type="button" class="btn-ghost self-end text-red-600" (click)="p.tiers.splice(i, 1)">Remove</button>
            </div>
          }
        </div>
        <button type="button" class="btn-ghost mt-3" (click)="addTier()">Add level</button>

        <h3 class="mt-6 text-sm font-semibold">Coins shop</h3>
        <p class="text-xs text-slate-500">What companies can buy with coins. "Pay an invoice" is priced by the invoice (total × coins per 1.00).</p>
        <div class="mt-3 space-y-3">
          @for (s of shop; track $index; let i = $index) {
            <div class="grid gap-3 rounded-xl border border-slate-200 p-3 sm:grid-cols-[2fr_10rem_6rem_6rem_2fr_auto_auto] dark:border-slate-700">
              <label class="block"><span class="label">Name</span><input class="input" [(ngModel)]="s.title" [name]="'st' + i" maxlength="60" /></label>
              <label class="block"><span class="label">Kind</span><select class="input" [(ngModel)]="s.kind" [name]="'sk' + i">@for (k of kinds; track k[0]) { <option [value]="k[0]">{{ k[1] }}</option> }</select></label>
              <label class="block"><span class="label">{{ unitLabel(s.kind) }}</span><input class="input" type="number" min="1" [(ngModel)]="s.value" [name]="'sv' + i" [disabled]="s.kind === 'pay_invoice'" /></label>
              <label class="block"><span class="label">Price</span><input class="input" type="number" min="1" [(ngModel)]="s.cost" [name]="'sc' + i" [disabled]="s.kind === 'pay_invoice'" /></label>
              <label class="block"><span class="label">Description</span><input class="input" [(ngModel)]="s.description" [name]="'sd' + i" maxlength="200" /></label>
              <label class="flex items-center gap-2 self-end pb-2 text-sm"><input type="checkbox" class="h-4 w-4 rounded" [(ngModel)]="s.enabled" [name]="'se' + i" />On</label>
              <button type="button" class="btn-ghost self-end text-red-600" (click)="shop.splice(i, 1)">Remove</button>
            </div>
          }
        </div>
        <div class="mt-4 flex flex-wrap gap-2">
          <button type="button" class="btn-ghost" (click)="addShop()">Add reward</button>
          <span class="flex-1"></span>
          <button type="button" class="btn" [disabled]="saving()" (click)="save()">{{ saving() ? 'Saving…' : 'Save rules' }}</button>
        </div>
      </section>

      <section class="mt-5 card">
        <h2 class="text-base font-semibold">Coin balances</h2>
        <div class="overflow-x-auto"><table class="table mt-2"><thead><tr><th>Company</th><th class="text-right">Coins</th><th></th></tr></thead>
          <tbody>
            @for (h of d.holders ?? []; track h.clientId) {
              <tr><td class="font-medium">{{ h.company }}</td><td class="text-right tabular-nums">{{ h.balance.toLocaleString() }}</td>
                <td class="text-right"><button type="button" class="btn-ghost text-sm" (click)="adjust(h)">Give or take</button></td></tr>
            }
            @if (!(d.holders?.length)) { <tr><td colspan="3" class="py-5 text-center text-slate-500">No company has coins yet.</td></tr> }
          </tbody></table></div>
      </section>

      <section class="mt-5 card">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <h2 class="text-base font-semibold">Rewards</h2>
          <div class="inline-flex rounded-xl border border-slate-200 bg-slate-100 p-1 text-sm dark:border-slate-700 dark:bg-slate-800">
            @for (s of statuses; track s.v) { <button type="button" class="rounded-lg px-3 py-1 font-medium" [class]="status() === s.v ? 'bg-white shadow-sm dark:bg-slate-900' : 'text-slate-500'" (click)="setStatus(s.v)">{{ s.l }}</button> }
          </div>
        </div>
        <div class="overflow-x-auto"><table class="table mt-3"><thead><tr><th>Date</th><th>Referrer</th><th>Brought</th><th class="text-right">Reward</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (r of d.rewards; track r.id) {
              <tr><td class="whitespace-nowrap">{{ r.createdAt | date: 'd MMM y' }}</td><td class="font-medium">{{ r.referrer }}</td><td>{{ r.referred }}</td>
                <td class="text-right tabular-nums">{{ r.coins ? r.coins.toLocaleString() + ' coins' : money(r.amount) + ' ' + r.currency }}</td>
                <td><span class="badge" [class]="r.status === 'Earned' ? 'badge-warn' : 'badge-ok'">{{ r.status === 'Earned' ? 'To pay' : r.status === 'Credited' ? 'As coins' : r.status }}</span>@if (r.reference) { <div class="text-xs text-slate-500">{{ r.reference }}</div> }</td>
                <td class="text-right">@if (r.status === 'Earned') { <button type="button" class="btn-ghost text-sm" (click)="pay(r)">Mark paid</button> }</td></tr>
            }
            @if (!d.rewards.length) { <tr><td colspan="6" class="py-5 text-center text-slate-500">No rewards yet.</td></tr> }
          </tbody></table></div>
      </section>
    }
  `,
})
export class ReferralProgramComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly a = signal<Admin | null>(null);
  readonly status = signal('');
  readonly saving = signal(false);
  readonly statuses = [{ v: '', l: 'All' }, { v: 'Earned', l: 'To pay' }, { v: 'Credited', l: 'As coins' }, { v: 'Paid', l: 'Paid' }];
  readonly kinds = Object.entries(KINDS);
  readonly sum = sum;
  readonly money = money;
  p: Program = { enabled: true, percent: 10, invoicesRewarded: 1, description: '', tiers: [], rewardType: 'coins', coinsPerUnit: 10 };
  shop: ShopItem[] = [];

  unitLabel(k: string): string { return k === 'free_days' ? 'Days' : k === 'pay_invoice' ? '—' : 'How many'; }

  async ngOnInit(): Promise<void> { await this.load(); }

  private async load(): Promise<void> {
    try {
      const d = await this.api.get<Admin>('/api/v1/referral-program', { status: this.status() || undefined });
      this.p = { ...d.program, tiers: d.program.tiers.map(t => ({ ...t, bonusCoins: t.bonusCoins ?? 0 })) };
      this.shop = (d.program.shop ?? []).map(s => ({ ...s }));
      this.a.set(d);
    } catch (e) { this.toast.err(e); }
  }

  setStatus(v: string): void { this.status.set(v); void this.load(); }
  addTier(): void { this.p.tiers.push({ key: '', label: '', referrals: (this.p.tiers.at(-1)?.referrals ?? 0) + 1, freeMonths: 0, perk: '', bonusCoins: 0 }); }
  addShop(): void { this.shop.push({ key: '', title: '', description: '', cost: 100, kind: 'free_days', value: 7, enabled: true }); }

  async save(): Promise<void> {
    this.saving.set(true);
    try {
      const slug = (t: string) => t.trim().toLowerCase().replace(/[^a-z0-9]/g, '');
      const tiers = this.p.tiers.filter(t => t.label.trim()).map(t => ({ ...t, key: slug(t.label), label: t.label.trim(), perk: t.perk?.trim() || null, bonusCoins: Number(t.bonusCoins) || 0 }));
      const shop = this.shop.filter(s => s.title.trim()).map(s => ({ ...s, key: s.key || slug(s.title), title: s.title.trim(), description: s.description?.trim() || null, cost: s.kind === 'pay_invoice' ? 0 : Number(s.cost) || 0, value: s.kind === 'pay_invoice' ? 0 : Number(s.value) || 0 }));
      await this.api.put('/api/v1/referral-program', {
        enabled: this.p.enabled, percent: Number(this.p.percent) || 0, invoicesRewarded: Number(this.p.invoicesRewarded) || 1, description: this.p.description?.trim() || null,
        tiers, rewardType: this.p.rewardType, coinsPerUnit: Number(this.p.coinsPerUnit) || 10, shop,
      });
      this.toast.ok('Rules saved.');
      await this.load();
    } catch (e) { this.toast.err(e); } finally { this.saving.set(false); }
  }

  async adjust(h: Holder): Promise<void> {
    const v = await this.dialog.prompt(`Give coins to ${h.company} (a negative number takes them back). They have ${h.balance.toLocaleString()}.`, 'e.g. 100 or -50', 'Apply');
    const n = Number((v ?? '').trim());
    if (!v || !Number.isInteger(n) || n === 0) return;
    try { const r = await this.api.post<{ message: string }>('/api/v1/referral-program/coins', { clientId: h.clientId, amount: n, note: n > 0 ? 'Coins from the operator' : 'Coins taken by the operator' }); this.toast.ok(r.message); await this.load(); }
    catch (e) { this.toast.err(e); }
  }

  async pay(r: AdminReward): Promise<void> {
    const ref = await this.dialog.prompt(`Pay ${money(r.amount)} ${r.currency} to ${r.referrer}. Payment reference (optional):`, 'Bank or EcoCash reference', 'Mark as paid');
    if (ref === null) return;
    try { await this.api.post(`/api/v1/referral-program/rewards/${r.id}/pay`, { reference: ref.trim() || null }); this.toast.ok('Marked as paid.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
