import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api.service';
import { money } from '../../core/accounting.store';
import { ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

interface Amount { currency: string; amount: number }
interface Partner { id: string; name: string; code: string; contactName?: string | null; email?: string | null; phone?: string | null; commissionPercent: number; kind?: 'commission' | 'reseller'; discountPercent?: number; isActive: boolean; notes?: string | null; clients: number; staff: number; earned: Amount[]; paid: Amount[]; owed: Amount[] }
interface Staff { id: string; name: string; email: string; status: string; lastLogin?: string | null }
interface Link { clientId: string; name: string; partnerId?: string | null; partnerName?: string | null; percent: number }
interface Statement { partner: string; lines: { date: string; company: string; invoice: string; currency: string; invoiceAmount: number; percent: number; commission: number }[]; payouts: { id: string; date: string; amount: number; currency: string; reference?: string | null }[]; earned: Amount[]; paid: Amount[]; owed: Amount[] }
interface Form { id?: string; name: string; code: string; contactName: string; email: string; phone: string; commissionPercent: number; kind: 'commission' | 'reseller'; discountPercent: number; isActive: boolean; notes: string }

/** Resellers and accounting firms: who they are, their staff, the companies they brought, and the commission paid out to them. */
@Component({
  selector: 'app-partners',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Partners" subtitle="Accounting firms and resellers that bring companies, and the commission they earn on each paid subscription invoice.">
      <button class="btn-ghost" (click)="openLinks()"><app-icon name="building" size="h-4 w-4" />Link a company</button>
      <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New partner</button>
    </app-page-header>

    <div class="grid gap-5 md:grid-cols-2 xl:grid-cols-3">
      @for (p of items(); track p.id) {
        <article class="card flex flex-col p-0" [class.opacity-60]="!p.isActive">
          <header class="flex items-start gap-3 border-b border-slate-100 p-5 dark:border-slate-800">
            <span class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-teal-50 text-teal-600 dark:bg-teal-500/15"><app-icon name="users" size="h-6 w-6" /></span>
            <div class="min-w-0 flex-1"><h2 class="truncate text-base font-semibold">{{ p.name }}</h2><p class="truncate text-xs text-slate-500">Code <span class="font-mono font-medium">{{ p.code }}</span> · {{ p.kind === 'reseller' ? 'Reseller, ' + (p.discountPercent ?? 0) + '% off for its companies' : p.commissionPercent + '% commission' }}</p></div>
            @if (!p.isActive) { <span class="badge badge-warn">Off</span> }
            <button class="icon-btn" (click)="edit(p)" aria-label="Edit partner"><app-icon name="edit" size="h-4 w-4" /></button>
          </header>
          <div class="flex-1 space-y-3 p-5 text-sm">
            <div class="grid grid-cols-2 gap-3 text-center">
              <div class="rounded-xl bg-slate-50 px-3 py-2 dark:bg-slate-800/50"><div class="text-lg font-semibold tabular-nums">{{ p.clients }}</div><div class="text-xs text-slate-500">companies</div></div>
              <div class="rounded-xl bg-slate-50 px-3 py-2 dark:bg-slate-800/50"><div class="text-lg font-semibold tabular-nums">{{ p.staff }}</div><div class="text-xs text-slate-500">staff logins</div></div>
            </div>
            <div class="rounded-xl bg-slate-50 px-3 py-2.5 dark:bg-slate-800/50">
              <div class="flex justify-between text-xs text-slate-500"><span>Owed to the partner</span><span>earned {{ sum(p.earned) }} · paid {{ sum(p.paid) }}</span></div>
              @for (o of p.owed; track o.currency) { <div class="text-lg font-semibold tabular-nums text-amber-700 dark:text-amber-300">{{ money(o.amount) }} <span class="text-sm font-normal text-slate-500">{{ o.currency }}</span></div> }
              @if (!p.owed.length) { <div class="text-sm font-medium text-emerald-600">Nothing owed</div> }
            </div>
            @if (p.email || p.contactName) { <div class="truncate text-xs text-slate-500">{{ p.contactName }} {{ p.email }}</div> }
          </div>
          <footer class="flex gap-2 border-t border-slate-100 p-3 dark:border-slate-800">
            <button class="btn-ghost btn-sm flex-1 justify-center" (click)="openStaff(p)">Staff</button>
            <button class="btn-ghost btn-sm flex-1 justify-center" (click)="openStatement(p)">Statement</button>
            <button class="btn-ghost btn-sm flex-1 justify-center" [disabled]="!p.owed.length" (click)="openPayout(p)">Pay out</button>
          </footer>
        </article>
      } @empty { <div class="card text-center text-slate-500 md:col-span-2 xl:col-span-3">{{ loaded() ? 'No partners yet. Add the first accounting firm or reseller.' : 'Loading…' }}</div> }
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit partner' : 'New partner'" width="max-w-lg" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="150" /></label>
            <label class="block"><span class="label">Referral code</span><input class="input font-mono uppercase" name="c" [(ngModel)]="f.code" maxlength="20" placeholder="made from the name" /><span class="hint">Used in /register?ref=CODE</span></label>
            <label class="block"><span class="label">Kind of partner</span>
              <select class="input" name="kind" [(ngModel)]="f.kind"><option value="commission">Commission: earns a share of what its companies pay</option><option value="reseller">Reseller: its companies pay a lower price</option></select></label>
            @if (f.kind === 'reseller') {
              <label class="block"><span class="label">Reseller discount (%)</span><input type="number" step="0.01" min="0" max="90" class="input text-right" name="d" [(ngModel)]="f.discountPercent" required /><span class="hint">Its companies pay your plan prices less this. It charges them for its own services; it earns no commission from you.</span></label>
            } @else {
              <label class="block"><span class="label">Commission (%)</span><input type="number" step="0.01" min="0" max="100" class="input text-right" name="p" [(ngModel)]="f.commissionPercent" required /><span class="hint">Of each paid invoice before tax</span></label>
            }
            <label class="block"><span class="label">Contact person</span><input class="input" name="cn" [(ngModel)]="f.contactName" maxlength="120" /></label>
            <label class="block"><span class="label">Phone</span><input class="input" name="ph" [(ngModel)]="f.phone" maxlength="40" /></label>
            <label class="block sm:col-span-2"><span class="label">Email</span><input class="input" name="e" [(ngModel)]="f.email" maxlength="200" /></label>
            <label class="block sm:col-span-2"><span class="label">Notes</span><input class="input" name="no" [(ngModel)]="f.notes" maxlength="1000" /></label>
            <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" class="h-4 w-4" name="a" [(ngModel)]="f.isActive" /> Active (an inactive partner's code is ignored on registration)</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }

    @if (staffFor(); as p) {
      <app-modal [title]="'Staff of ' + p.name" subtitle="People who sign in to the Partner portal. They see only this partner's companies." width="max-w-xl" (closed)="staffFor.set(null)">
        <ul class="divide-y divide-slate-100 dark:divide-slate-800">
          @for (s of staff(); track s.id) { <li class="flex items-center gap-3 py-2.5 text-sm"><div class="min-w-0 flex-1"><div class="font-medium">{{ s.name }}</div><div class="truncate text-xs text-slate-500">{{ s.email }}</div></div><span class="text-xs text-slate-500">{{ s.lastLogin ? 'last in ' + (s.lastLogin | date: 'd MMM y') : 'never signed in' }}</span></li> }
          @empty { <li class="py-3 text-sm text-slate-500">No staff yet.</li> }
        </ul>
        <form class="mt-4 grid gap-3 border-t border-slate-100 pt-4 sm:grid-cols-2 dark:border-slate-800" (ngSubmit)="addStaff(p)">
          <h3 class="text-sm font-semibold sm:col-span-2">Add a person</h3>
          <label class="block"><span class="label">First name</span><input class="input" name="sn" [(ngModel)]="sf.name" required /></label>
          <label class="block"><span class="label">Surname</span><input class="input" name="ss" [(ngModel)]="sf.surname" /></label>
          <label class="block"><span class="label">Email</span><input class="input" name="se" [(ngModel)]="sf.email" required /></label>
          <label class="block"><span class="label">Temporary password</span><input class="input font-mono" name="sp" [(ngModel)]="sf.password" required minlength="10" /></label>
          @if (error()) { <p class="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 sm:col-span-2 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="flex justify-end sm:col-span-2"><button class="btn" type="submit" [disabled]="busy()">Add</button></div>
        </form>
      </app-modal>
    }

    @if (statement(); as s) {
      <app-modal [title]="'Commission: ' + s.partner" width="max-w-3xl" (closed)="statement.set(null)">
        <table class="table"><thead><tr><th>Date</th><th>Company</th><th>Invoice</th><th class="text-right">Invoice (before tax)</th><th class="text-right">%</th><th class="text-right">Commission</th></tr></thead>
          <tbody>@for (l of s.lines; track $index) { <tr><td class="whitespace-nowrap">{{ l.date | date: 'd MMM y' }}</td><td>{{ l.company }}</td><td class="font-mono text-xs">{{ l.invoice }}</td><td class="text-right tabular-nums">{{ money(l.invoiceAmount) }} {{ l.currency }}</td><td class="text-right">{{ l.percent }}</td><td class="text-right font-medium tabular-nums">{{ money(l.commission) }}</td></tr> }
            @if (!s.lines.length) { <tr><td colspan="6" class="py-5 text-center text-slate-500">No paid invoices from this partner's companies yet.</td></tr> }</tbody></table>
        @if (s.payouts.length) {
          <h3 class="mt-4 text-sm font-semibold">Paid out</h3>
          <ul class="mt-1 text-sm">@for (x of s.payouts; track x.id) { <li class="flex justify-between border-b border-slate-100 py-1.5 dark:border-slate-800"><span>{{ x.date | date: 'd MMM y' }} · {{ x.reference || 'no reference' }}</span><span class="tabular-nums">{{ money(x.amount) }} {{ x.currency }}</span></li> }</ul>
        }
        <p class="mt-3 text-right text-sm font-semibold">Owed: {{ sum(s.owed) }}</p>
      </app-modal>
    }

    @if (payout(); as d) {
      <app-modal [title]="'Pay out ' + d.partner.name" [subtitle]="'Owed: ' + sum(d.partner.owed)" width="max-w-md" (closed)="payout.set(null)">
        <form (ngSubmit)="savePayout()" class="grid gap-3 sm:grid-cols-2">
          <label class="block"><span class="label">Currency</span><select class="input" name="cu" [(ngModel)]="d.currency">@for (o of d.partner.owed; track o.currency) { <option [value]="o.currency">{{ o.currency }} ({{ money(o.amount) }} owed)</option> }</select></label>
          <label class="block"><span class="label">Amount</span><input type="number" step="0.01" min="0.01" class="input text-right" name="am" [(ngModel)]="d.amount" required /></label>
          <label class="block"><span class="label">Date paid</span><input type="date" class="input" name="dt" [(ngModel)]="d.date" required /></label>
          <label class="block"><span class="label">Reference</span><input class="input" name="rf" [(ngModel)]="d.reference" maxlength="100" /></label>
          @if (error()) { <p class="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 sm:col-span-2 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="flex justify-end gap-2 sm:col-span-2"><button class="btn-ghost" type="button" (click)="payout.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Record payout</button></div>
        </form>
      </app-modal>
    }

    @if (linking()) {
      <app-modal title="Link a company to a partner" subtitle="For a company that did not use the partner's referral code. The rate is copied to the company and can differ from the partner's own." width="max-w-3xl" (closed)="linking.set(false)">
        <input class="input mb-3" placeholder="Search companies…" [ngModel]="lq()" (ngModelChange)="lq.set($event)" />
        <div class="max-h-96 overflow-y-auto">
          <table class="table"><thead><tr><th>Company</th><th>Partner</th><th class="w-24 text-right">%</th><th></th></tr></thead>
            <tbody>@for (l of linkRows(); track l.clientId) {
              <tr><td class="font-medium">{{ l.name }}</td>
                <td><select class="input" [ngModel]="pick()[l.clientId] ?? l.partnerId ?? ''" (ngModelChange)="setPick(l.clientId, $event)"><option value="">None (direct)</option>@for (p of items(); track p.id) { <option [value]="p.id">{{ p.name }}</option> }</select></td>
                <td><input type="number" step="0.01" min="0" max="100" class="input text-right" [ngModel]="pct()[l.clientId] ?? l.percent" (ngModelChange)="setPct(l.clientId, $event)" /></td>
                <td class="text-right"><button class="btn btn-sm" [disabled]="busy()" (click)="saveLink(l)">Save</button></td></tr> }</tbody></table>
        </div>
      </app-modal>
    }
  `,
})
export class PartnersComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly money = money;
  readonly items = signal<Partner[]>([]);
  readonly loaded = signal(false);
  readonly form = signal<Form | null>(null);
  readonly staffFor = signal<Partner | null>(null);
  readonly staff = signal<Staff[]>([]);
  readonly statement = signal<Statement | null>(null);
  readonly payout = signal<{ partner: Partner; currency: string; amount: number | null; date: string; reference: string } | null>(null);
  readonly linking = signal(false);
  readonly links = signal<Link[]>([]);
  readonly lq = signal('');
  readonly pick = signal<Record<string, string | undefined>>({});
  readonly pct = signal<Record<string, number | undefined>>({});
  readonly busy = signal(false);
  readonly error = signal('');
  sf = { name: '', surname: '', email: '', password: '' };
  readonly linkRows = computed(() => { const q = this.lq().trim().toLowerCase(); return this.links().filter(l => !q || l.name.toLowerCase().includes(q)); });

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> {
    try { this.items.set(await this.api.get<Partner[]>('/api/v1/partners')); this.loaded.set(true); } catch (e) { this.toast.err(e); }
  }

  sum(a: Amount[]): string { return a.length ? a.map(x => `${money(x.amount)} ${x.currency}`).join(' + ') : '0.00'; }
  private msg(e: unknown): string { return errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, ''); }

  start(): void { this.error.set(''); this.form.set({ name: '', code: '', contactName: '', email: '', phone: '', commissionPercent: 10, kind: 'commission', discountPercent: 0, isActive: true, notes: '' }); }
  edit(p: Partner): void { this.error.set(''); this.form.set({ id: p.id, name: p.name, code: p.code, contactName: p.contactName ?? '', email: p.email ?? '', phone: p.phone ?? '', commissionPercent: p.commissionPercent, kind: p.kind ?? 'commission', discountPercent: p.discountPercent ?? 0, isActive: p.isActive, notes: p.notes ?? '' }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.put('/api/v1/partners', { id: f.id ?? null, name: f.name.trim(), code: f.code.trim() || null, contactName: f.contactName.trim() || null, email: f.email.trim() || null, phone: f.phone.trim() || null, commissionPercent: f.kind === 'reseller' ? 0 : Number(f.commissionPercent), kind: f.kind, discountPercent: Number(f.discountPercent) || 0, isActive: f.isActive, notes: f.notes.trim() || null });
      this.form.set(null); this.toast.ok('Partner saved.'); await this.load();
    } catch (e) { this.error.set(this.msg(e)); } finally { this.busy.set(false); }
  }

  async openStaff(p: Partner): Promise<void> {
    this.error.set(''); this.sf = { name: '', surname: '', email: '', password: '' }; this.staffFor.set(p);
    try { this.staff.set(await this.api.get<Staff[]>(`/api/v1/partners/${p.id}/staff`)); } catch (e) { this.toast.err(e); }
  }

  async addStaff(p: Partner): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      await this.api.post(`/api/v1/partners/${p.id}/staff`, { email: this.sf.email.trim(), name: this.sf.name.trim(), surname: this.sf.surname.trim(), password: this.sf.password });
      this.toast.ok('Added. They choose their own password at first sign-in.'); await this.openStaff(p); await this.load();
    } catch (e) { this.error.set(this.msg(e)); } finally { this.busy.set(false); }
  }

  async openStatement(p: Partner): Promise<void> {
    try { this.statement.set(await this.api.get<Statement>(`/api/v1/partners/${p.id}/statement`)); } catch (e) { this.toast.err(e); }
  }

  openPayout(p: Partner): void {
    this.error.set('');
    this.payout.set({ partner: p, currency: p.owed[0]?.currency ?? 'USD', amount: p.owed[0]?.amount ?? null, date: new Date().toISOString().slice(0, 10), reference: '' });
  }

  async savePayout(): Promise<void> {
    const d = this.payout(); if (!d) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.post(`/api/v1/partners/${d.partner.id}/payouts`, { date: d.date, amount: Number(d.amount), currency: d.currency, reference: d.reference.trim() || null, notes: null });
      this.payout.set(null); this.toast.ok('Payout recorded.'); await this.load();
    } catch (e) { this.error.set(this.msg(e)); } finally { this.busy.set(false); }
  }

  async openLinks(): Promise<void> {
    this.pick.set({}); this.pct.set({}); this.lq.set('');
    try { this.links.set(await this.api.get<Link[]>('/api/v1/partners/clients')); this.linking.set(true); } catch (e) { this.toast.err(e); }
  }

  setPick(id: string, v: string): void { this.pick.update(m => ({ ...m, [id]: v })); const p = this.items().find(x => x.id === v); if (p) this.pct.update(m => ({ ...m, [id]: p.commissionPercent })); }
  setPct(id: string, v: number): void { this.pct.update(m => ({ ...m, [id]: v })); }

  async saveLink(l: Link): Promise<void> {
    const partnerId = (this.pick()[l.clientId] ?? l.partnerId ?? '') || null;
    this.busy.set(true);
    try {
      await this.api.put(`/api/v1/partners/clients/${l.clientId}`, { partnerId, percent: partnerId ? Number(this.pct()[l.clientId] ?? l.percent) : null });
      this.toast.ok('Saved.'); this.links.set(await this.api.get<Link[]>('/api/v1/partners/clients')); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
