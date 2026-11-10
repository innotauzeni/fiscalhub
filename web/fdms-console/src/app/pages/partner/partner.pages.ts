import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api } from '../../core/api.service';
import { appUrl } from '../../core/env';
import { money } from '../../core/accounting.store';
import { ToastService } from '../../core/ui.services';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Amount { currency: string; amount: number }
const sum = (a: Amount[]): string => (a.length ? a.map(x => `${money(x.amount)} ${x.currency}`).join(' + ') : '0.00');

interface Summary { name: string; code: string; commissionPercent: number; kind?: 'commission' | 'reseller'; discountPercent?: number; referralPath: string; clients: number; activeClients: number; pendingReferrals: number; earned: Amount[]; paid: Amount[]; owed: Amount[] }
interface PClient { name: string; status: string; plan?: string | null; devices: number; receipts30Days: number; lastReceiptAt?: string | null; since: string; percent: number; earned: Amount[] }
interface Referral { companyName: string; contactName: string; status: string; createdAt: string }
interface Statement { partner: string; lines: { date: string; company: string; invoice: string; currency: string; invoiceAmount: number; percent: number; commission: number }[]; payouts: { id: string; date: string; amount: number; currency: string; reference?: string | null }[]; earned: Amount[]; paid: Amount[]; owed: Amount[] }

/** The partner's home: how many companies it has brought, what it has earned, and its referral link. */
@Component({
  selector: 'app-partner-overview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, IconComponent, PageHeaderComponent],
  template: `
    <app-page-header [title]="s()?.name ?? 'Partner'" subtitle="The companies you brought to the gateway and the commission you have earned." />
    @if (s(); as p) {
      <div class="mb-5 grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <div class="card"><div class="text-xs text-slate-500">Companies</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ p.clients }}</div><div class="text-xs text-slate-500">{{ p.activeClients }} active</div></div>
        <div class="card"><div class="text-xs text-slate-500">Waiting for approval</div><div class="mt-1 text-2xl font-semibold tabular-nums">{{ p.pendingReferrals }}</div><div class="text-xs text-slate-500">registered with your link</div></div>
        <div class="card"><div class="text-xs text-slate-500">Earned</div><div class="mt-1 text-lg font-semibold tabular-nums">{{ sum(p.earned) }}</div><div class="text-xs text-slate-500">{{ p.kind === 'reseller' ? 'Reseller: your companies pay ' + (p.discountPercent ?? 0) + '% less' : p.commissionPercent + '% of paid invoices' }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Still owed to you</div><div class="mt-1 text-lg font-semibold tabular-nums text-amber-700 dark:text-amber-300">{{ sum(p.owed) }}</div><div class="text-xs text-slate-500">paid so far {{ sum(p.paid) }}</div></div>
      </div>
      <section class="card">
        <h2 class="text-base font-semibold">Your referral link</h2>
        <p class="text-sm text-slate-500">Send this link to a company. When it registers and is approved, it becomes one of yours and you earn commission on what it pays.</p>
        <div class="mt-3 flex flex-wrap items-center gap-2">
          <input class="input flex-1 font-mono text-sm" readonly [value]="link()" (focus)="$any($event.target).select()" />
          <button class="btn" type="button" (click)="copy()"><app-icon name="check" size="h-4 w-4" />Copy</button>
        </div>
        <p class="mt-2 text-xs text-slate-500">Or give them your code <span class="font-mono font-medium">{{ p.code }}</span> to type in on the registration page.</p>
      </section>
      <section class="mt-5 card">
        <h2 class="text-base font-semibold">Registrations from your link</h2>
        <table class="table mt-2"><thead><tr><th>Company</th><th>Contact</th><th>Status</th><th>Date</th></tr></thead>
          <tbody>@for (r of refs(); track $index) { <tr><td class="font-medium">{{ r.companyName }}</td><td>{{ r.contactName }}</td><td><span class="badge" [class]="r.status === 'Approved' ? 'badge-ok' : r.status === 'Pending' ? 'badge-warn' : 'badge-bad'">{{ r.status }}</span></td><td class="text-xs text-slate-500">{{ r.createdAt | date: 'd MMM y' }}</td></tr> }
            @if (!refs().length) { <tr><td colspan="4" class="py-5 text-center text-slate-500">No registrations yet.</td></tr> }</tbody></table>
      </section>
    }
  `,
})
export class PartnerOverviewComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly s = signal<Summary | null>(null);
  readonly refs = signal<Referral[]>([]);
  readonly sum = sum;
  link(): string { return appUrl(this.s()?.referralPath ?? ''); }

  async ngOnInit(): Promise<void> {
    try { this.s.set(await this.api.get<Summary>('/api/v1/partner/summary')); this.refs.set(await this.api.get<Referral[]>('/api/v1/partner/referrals')); } catch (e) { this.toast.err(e); }
  }

  async copy(): Promise<void> {
    try { await navigator.clipboard.writeText(this.link()); this.toast.ok('Link copied.'); } catch { this.toast.err('Could not copy. Select the link and copy it by hand.'); }
  }
}

/** The companies the partner brought: how they are doing, never what is in their books. */
@Component({
  selector: 'app-partner-clients',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, PageHeaderComponent],
  template: `
    <app-page-header title="Our companies" subtitle="How the companies you brought are doing. Their receipts and books stay private to them." />
    <div class="grid gap-5 md:grid-cols-2 xl:grid-cols-3">
      @for (c of items(); track c.name) {
        <article class="card">
          <div class="flex items-start justify-between gap-3"><h2 class="min-w-0 truncate text-base font-semibold">{{ c.name }}</h2><span class="badge" [class]="c.status === 'Active' ? 'badge-ok' : c.status === 'Trial' ? 'badge-info' : 'badge-bad'">{{ c.status }}</span></div>
          <p class="text-xs text-slate-500">{{ c.plan ?? 'No plan' }} · with you since {{ c.since | date: 'MMM y' }} · {{ c.percent }}%</p>
          <div class="mt-3 grid grid-cols-2 gap-3 text-center">
            <div class="rounded-xl bg-slate-50 px-3 py-2 dark:bg-slate-800/50"><div class="text-lg font-semibold tabular-nums">{{ c.devices }}</div><div class="text-xs text-slate-500">devices</div></div>
            <div class="rounded-xl bg-slate-50 px-3 py-2 dark:bg-slate-800/50"><div class="text-lg font-semibold tabular-nums">{{ c.receipts30Days }}</div><div class="text-xs text-slate-500">receipts, 30 days</div></div>
          </div>
          <p class="mt-3 text-xs text-slate-500">Last receipt: {{ c.lastReceiptAt ? (c.lastReceiptAt | date: 'd MMM y') : 'none yet' }}</p>
          <p class="text-sm">Commission earned: <b>{{ sum(c.earned) }}</b></p>
        </article>
      } @empty { <div class="card text-center text-slate-500 md:col-span-2 xl:col-span-3">{{ loaded() ? 'No companies yet. Share your referral link from the Overview page.' : 'Loading…' }}</div> }
    </div>
  `,
})
export class PartnerClientsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly items = signal<PClient[]>([]);
  readonly loaded = signal(false);
  readonly sum = sum;
  async ngOnInit(): Promise<void> {
    try { this.items.set(await this.api.get<PClient[]>('/api/v1/partner/clients')); this.loaded.set(true); } catch (e) { this.toast.err(e); }
  }
}

/** What the partner has earned, invoice by invoice, what has been paid out and what is still owed. */
@Component({
  selector: 'app-partner-commission',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, PageHeaderComponent],
  template: `
    <app-page-header title="Commission" subtitle="Earned on every subscription invoice your companies pay, before tax." />
    @if (s(); as st) {
      <div class="mb-5 grid gap-4 sm:grid-cols-3">
        <div class="card"><div class="text-xs text-slate-500">Earned</div><div class="mt-1 text-lg font-semibold tabular-nums">{{ sum(st.earned) }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Paid out</div><div class="mt-1 text-lg font-semibold tabular-nums">{{ sum(st.paid) }}</div></div>
        <div class="card"><div class="text-xs text-slate-500">Owed to you</div><div class="mt-1 text-lg font-semibold tabular-nums text-amber-700 dark:text-amber-300">{{ sum(st.owed) }}</div></div>
      </div>
      <section class="card">
        <h2 class="text-base font-semibold">Invoices paid</h2>
        <div class="overflow-x-auto"><table class="table mt-2"><thead><tr><th>Date</th><th>Company</th><th>Invoice</th><th class="text-right">Invoice (before tax)</th><th class="text-right">%</th><th class="text-right">Commission</th></tr></thead>
          <tbody>@for (l of st.lines; track $index) { <tr><td class="whitespace-nowrap">{{ l.date | date: 'd MMM y' }}</td><td>{{ l.company }}</td><td class="font-mono text-xs">{{ l.invoice }}</td><td class="text-right tabular-nums">{{ money(l.invoiceAmount) }} {{ l.currency }}</td><td class="text-right">{{ l.percent }}</td><td class="text-right font-medium tabular-nums">{{ money(l.commission) }} {{ l.currency }}</td></tr> }
            @if (!st.lines.length) { <tr><td colspan="6" class="py-5 text-center text-slate-500">Nothing earned yet. Commission appears when a company pays its invoice.</td></tr> }</tbody></table></div>
      </section>
      <section class="mt-5 card">
        <h2 class="text-base font-semibold">Paid to you</h2>
        <ul class="mt-2 text-sm">@for (x of st.payouts; track x.id) { <li class="flex justify-between border-b border-slate-100 py-2 dark:border-slate-800"><span>{{ x.date | date: 'd MMM y' }} · {{ x.reference || 'no reference' }}</span><span class="tabular-nums">{{ money(x.amount) }} {{ x.currency }}</span></li> }
          @if (!st.payouts.length) { <li class="py-3 text-slate-500">No payouts yet.</li> }</ul>
      </section>
    }
  `,
})
export class PartnerCommissionComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly s = signal<Statement | null>(null);
  readonly sum = sum;
  readonly money = money;
  async ngOnInit(): Promise<void> {
    try { this.s.set(await this.api.get<Statement>('/api/v1/partner/statement')); } catch (e) { this.toast.err(e); }
  }
}
