import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import { money } from '../../core/accounting.store';
import { DatePipe } from '@angular/common';
import { AuthService } from '../../core/auth.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { DocumentsPanelComponent } from '../../shared/documents-panel.component';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

export interface Supplier { id: string; name: string; tin?: string | null; email?: string | null; phone?: string | null; address?: string | null; termsDays: number; isActive: boolean; openBills: number; outstanding: { currency: string; amount: number }[] }
interface Statement { supplier: string; tin?: string | null; currency: string; from: string; to: string; opening: number; lines: { date: string; kind: string; reference: string; description: string; debit: number; credit: number; balance: number }[]; closing: number }
interface Form { id?: string; name: string; tin: string; email: string; phone: string; address: string; termsDays: number; isActive: boolean }

/** The people the company buys from, and what is still owed to each. */
@Component({
  selector: 'app-suppliers',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, DocumentsPanelComponent, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Suppliers" subtitle="Who you buy from and what you still owe them. Enter their invoices under Bills.">
      @if (auth.can('accounting.manage')) { <button class="btn" (click)="start()"><app-icon name="plus" size="h-4 w-4" />New supplier</button> }
    </app-page-header>

    <div class="mb-5 card"><label class="block"><span class="label">Search</span><input class="input" placeholder="Search by name, TIN or email…" [ngModel]="q()" (ngModelChange)="q.set($event)" /></label></div>

    <div class="grid gap-5 md:grid-cols-2 xl:grid-cols-3">
      @for (s of rows(); track s.id) {
        <article class="card flex flex-col p-0" [class.opacity-60]="!s.isActive">
          <header class="flex items-start gap-3 border-b border-slate-100 p-5 dark:border-slate-800">
            <span class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-brand-50 text-brand-600 dark:bg-brand-500/15"><app-icon name="building" size="h-6 w-6" /></span>
            <div class="min-w-0 flex-1"><h2 class="truncate text-base font-semibold">{{ s.name }}</h2><p class="truncate text-xs text-slate-500">{{ s.tin ? 'TIN ' + s.tin : 'No TIN on file' }} · {{ s.termsDays }}-day terms</p></div>
            @if (!s.isActive) { <span class="badge badge-warn">Off</span> }
            @if (auth.can('accounting.manage')) { <button class="icon-btn" (click)="edit(s)" aria-label="Edit supplier"><app-icon name="edit" size="h-4 w-4" /></button> }
          </header>
          <div class="flex-1 space-y-2 p-5 text-sm">
            @if (s.email) { <div class="flex items-center gap-2 text-slate-600 dark:text-slate-300"><app-icon name="mail" size="h-4 w-4" /><span class="truncate">{{ s.email }}</span></div> }
            @if (s.phone) { <div class="flex items-center gap-2 text-slate-600 dark:text-slate-300"><app-icon name="message" size="h-4 w-4" />{{ s.phone }}</div> }
            @if (s.address) { <div class="text-xs text-slate-500">{{ s.address }}</div> }
            <div class="rounded-xl bg-slate-50 px-3 py-2.5 dark:bg-slate-800/50">
              <div class="text-xs text-slate-500">You owe</div>
              @for (o of s.outstanding; track o.currency) { <div class="text-lg font-semibold tabular-nums text-amber-700 dark:text-amber-300">{{ money(o.amount) }} <span class="text-sm font-normal text-slate-500">{{ o.currency }}</span></div> }
              @if (!s.outstanding.length) { <div class="text-sm font-medium text-emerald-600">Nothing owing</div> }
            </div>
          </div>
          <footer class="border-t border-slate-100 p-3 dark:border-slate-800"><div class="flex gap-2"><button class="btn-ghost btn-sm flex-1 justify-center" type="button" (click)="docs.set({ id: s.id, label: s.name })"><app-icon name="file-text" size="h-4 w-4" />Documents</button><button class="btn-ghost btn-sm flex-1 justify-center" type="button" (click)="openStatement(s)"><app-icon name="file-text" size="h-4 w-4" />Statement</button><a routerLink="/accounting/bills" [queryParams]="{ supplier: s.id }" class="btn-ghost btn-sm w-full justify-center"><app-icon name="file-text" size="h-4 w-4" />{{ s.openBills ? s.openBills + ' open bill' + (s.openBills === 1 ? '' : 's') : 'See bills' }}</a></div></footer>
        </article>
      } @empty { <div class="card text-center text-slate-500 md:col-span-2 xl:col-span-3">{{ loaded() ? 'No suppliers yet.' : 'Loading…' }}</div> }
    </div>

    @if (form(); as f) {
      <app-modal [title]="f.id ? 'Edit supplier' : 'New supplier'" width="max-w-lg" (closed)="form.set(null)">
        <form (ngSubmit)="save()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block sm:col-span-2"><span class="label">Name</span><input class="input" name="n" [(ngModel)]="f.name" required maxlength="120" /></label>
            <label class="block"><span class="label">TIN (optional)</span><input class="input font-mono" name="t" [(ngModel)]="f.tin" maxlength="20" /></label>
            <label class="block"><span class="label">Payment terms (days)</span><input type="number" min="0" max="365" class="input" name="d" [(ngModel)]="f.termsDays" /></label>
            <label class="block"><span class="label">Email</span><input class="input" name="e" [(ngModel)]="f.email" maxlength="120" /></label>
            <label class="block"><span class="label">Phone</span><input class="input" name="p" [(ngModel)]="f.phone" maxlength="40" /></label>
            <label class="block sm:col-span-2"><span class="label">Address</span><input class="input" name="a" [(ngModel)]="f.address" maxlength="300" /></label>
            <label class="flex items-center gap-2 text-sm sm:col-span-2"><input type="checkbox" class="h-4 w-4" name="act" [(ngModel)]="f.isActive" /> In use (switch off to stop new bills)</label>
          </div>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Save</button></div>
        </form>
      </app-modal>
    }

    @if (statement(); as st) {
      <app-modal title="Supplier statement" [subtitle]="st.supplier + (st.tin ? ' · TIN ' + st.tin : '')" width="max-w-3xl" (closed)="statement.set(null)">
        <div class="mb-3 flex flex-wrap items-end gap-3 print:hidden">
          <label class="block"><span class="label">Currency</span>
            <select class="input" [ngModel]="stCur()" (ngModelChange)="stCur.set($event)">@for (c of stCurrencies(); track c) { <option [value]="c">{{ c }}</option> }</select></label>
          <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="stFrom()" (ngModelChange)="stFrom.set($event)" /></label>
          <label class="block"><span class="label">To</span><input type="date" class="input" [ngModel]="stTo()" (ngModelChange)="stTo.set($event)" /></label>
          <button class="btn" type="button" (click)="reloadStatement()">Show</button><span class="flex-1"></span><button class="btn-ghost" type="button" (click)="print()"><app-icon name="file-text" size="h-4 w-4" />Print</button>
          <button class="btn-ghost" type="button" (click)="statementPdf()" [disabled]="stBusy()"><app-icon name="download" size="h-4 w-4" />PDF</button>
          <button class="btn-ghost" type="button" (click)="emailStatement()" [disabled]="stBusy()"><app-icon name="mail" size="h-4 w-4" />Email</button>
        </div>
        <table class="table"><thead><tr><th>Date</th><th>Reference</th><th>Description</th><th class="text-right">Billed</th><th class="text-right">Paid</th><th class="text-right">Balance owed</th></tr></thead>
          <tbody><tr class="text-slate-500"><td colspan="5">Balance brought forward</td><td class="text-right tabular-nums">{{ money(st.opening) }}</td></tr>
            @for (l of st.lines; track $index) { <tr><td class="whitespace-nowrap">{{ l.date | date: 'd MMM y' }}</td><td class="font-mono text-xs">{{ l.reference }}</td><td>{{ l.description }}</td><td class="text-right tabular-nums">{{ l.credit ? money(l.credit) : '' }}</td><td class="text-right tabular-nums">{{ l.debit ? money(l.debit) : '' }}</td><td class="text-right font-medium tabular-nums">{{ money(l.balance) }}</td></tr> }
            @if (!st.lines.length) { <tr><td colspan="6" class="py-5 text-center text-slate-500">No movements in this period.</td></tr> }</tbody>
          <tfoot><tr class="font-semibold"><td colspan="5">Balance owed to {{ st.supplier }}</td><td class="text-right tabular-nums">{{ money(st.closing) }}</td></tr></tfoot></table>
      </app-modal>
    }
    @if (docs(); as d) {
      <app-modal [title]="'Documents: ' + d.label" subtitle="Scans and papers kept with this record." width="max-w-2xl" (closed)="docs.set(null)">
        <app-documents-panel linkType="supplier" [linkId]="d.id" />
      </app-modal>
    }
  `,
})
export class SuppliersComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly auth = inject(AuthService);
  readonly money = money;
  readonly items = signal<Supplier[]>([]);
  readonly loaded = signal(false);
  readonly q = signal('');
  readonly form = signal<Form | null>(null);
  readonly statement = signal<Statement | null>(null);
  readonly docs = signal<{ id: string; label: string } | null>(null);
  readonly stSupplier = signal<Supplier | null>(null);
  readonly stCur = signal('USD');
  readonly stFrom = signal(`${new Date().getFullYear()}-01-01`);
  readonly stTo = signal(new Date().toISOString().slice(0, 10));
  readonly stCurrencies = computed(() => Array.from(new Set([...(this.stSupplier()?.outstanding.map(o => o.currency) ?? []), 'USD', 'ZWG'])));
  readonly busy = signal(false);
  readonly error = signal('');
  readonly rows = computed(() => { const q = this.q().trim().toLowerCase(); return this.items().filter(s => !q || (s.name + ' ' + (s.tin ?? '') + ' ' + (s.email ?? '')).toLowerCase().includes(q)); });

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> {
    try { this.items.set(await this.api.get<Supplier[]>('/api/v1/accounting/suppliers')); this.loaded.set(true); } catch (e) { this.toast.err(e); }
  }

  readonly stBusy = signal(false);
  async statementPdf(): Promise<void> {
    const s = this.stSupplier(); if (!s) return;
    this.stBusy.set(true);
    try { await this.api.open('/api/v1/accounting/suppliers/statement/pdf', { supplierId: s.id, currency: this.stCur(), from: this.stFrom(), to: this.stTo() }); } catch (e) { this.toast.err(e); } finally { this.stBusy.set(false); }
  }
  async emailStatement(): Promise<void> {
    const s = this.stSupplier(); if (!s) return;
    const to = await this.dialog.prompt(`Email the statement to ${s.name}. Address:`, s.email ?? '', 'Send');
    if (to === null) return;
    this.stBusy.set(true);
    try { const r = await this.api.post<{ sentTo: string }>('/api/v1/accounting/statements/email', { supplierId: s.id, currency: this.stCur(), from: this.stFrom(), to: this.stTo(), email: to.trim() || null }); this.toast.ok('Statement sent to ' + r.sentTo + '.'); }
    catch (e) { this.toast.err(e); } finally { this.stBusy.set(false); }
  }
  async openStatement(s: Supplier): Promise<void> { this.stSupplier.set(s); this.stCur.set(s.outstanding[0]?.currency ?? 'USD'); await this.reloadStatement(); }

  async reloadStatement(): Promise<void> {
    const s = this.stSupplier(); if (!s) return;
    try { this.statement.set(await this.api.get<Statement>('/api/v1/accounting/suppliers/statement', { supplierId: s.id, currency: this.stCur(), from: this.stFrom(), to: this.stTo() })); } catch (e) { this.toast.err(e); }
  }

  print(): void { window.print(); }

  start(): void { this.error.set(''); this.form.set({ name: '', tin: '', email: '', phone: '', address: '', termsDays: 30, isActive: true }); }
  edit(s: Supplier): void { this.error.set(''); this.form.set({ id: s.id, name: s.name, tin: s.tin ?? '', email: s.email ?? '', phone: s.phone ?? '', address: s.address ?? '', termsDays: s.termsDays, isActive: s.isActive }); }

  async save(): Promise<void> {
    const f = this.form(); if (!f) return;
    this.busy.set(true); this.error.set('');
    try {
      await this.api.put('/api/v1/accounting/suppliers', { id: f.id ?? null, name: f.name.trim(), tin: f.tin.trim() || null, email: f.email.trim() || null, phone: f.phone.trim() || null, address: f.address.trim() || null, termsDays: Number(f.termsDays), isActive: f.isActive });
      this.form.set(null); this.toast.ok('Supplier saved.'); await this.load();
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }
}
