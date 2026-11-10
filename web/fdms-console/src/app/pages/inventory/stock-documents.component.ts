import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import { isoDate, money } from '../../core/accounting.store';
import { AuthService } from '../../core/auth.service';
import { InventoryStore, StockDocument, qty } from '../../core/inventory.store';
import { DialogService, ToastService } from '../../core/ui.services';
import { ModalComponent } from '../../shared/modal.components';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

type Kind = 'grv' | 'transfer' | 'adjustment';
interface Supplier { id: string; name: string; isActive: boolean }
interface LineForm { text: string; productId: string | null; quantity: number | null; unitCost: number | null; vatPercent: number | null; counted: number | null }
interface DocForm { id: string | null; date: string; locationId: string; toLocationId: string; supplierId: string; supplierReference: string; mode: string; reason: string; notes: string; lines: LineForm[] }
interface LineFile { lines: { productId: string; code?: string | null; name: string; quantity: number; unitCost?: number | null; vatPercent?: number | null; counted?: number | null }[]; problems: string[] }

const TEXT: Record<Kind, { title: string; sub: string; one: string; none: string }> = {
  grv: { title: 'Goods received (GRV)', sub: 'Stock delivered by a supplier. Posting brings it into stock at its cost, and raises the supplier\'s bill (inventory and VAT input against accounts payable).', one: 'GRV', none: 'No GRVs yet.' },
  transfer: { title: 'Stock transfers', sub: 'Stock moved from one location to another, at average cost. Nothing changes in the books.', one: 'transfer', none: 'No transfers yet.' },
  adjustment: { title: 'Adjustments and stock takes', sub: 'Write off damaged, expired or missing stock, add stock found, or count a location and post the differences. Posted against stock adjustments.', one: 'adjustment', none: 'No adjustments or stock takes yet.' },
};

/** GRVs, transfers, and adjustments with stock takes: a list, an editor with lines from Excel, and posting, voiding and printing. */
@Component({
  selector: 'app-stock-documents',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, RouterLink, IconComponent, ModalComponent, PageHeaderComponent],
  template: `
    <app-page-header [title]="text().title" [subtitle]="text().sub">
      @if (auth.can('inventory.manage')) {
        @if (kind() === 'adjustment') { <button class="btn-ghost" (click)="start('count')"><app-icon name="clipboard" size="h-4 w-4" />New stock take</button> }
        <button class="btn" (click)="start('adjust')"><app-icon name="plus" size="h-4 w-4" />New {{ text().one }}</button>
      }
    </app-page-header>

    @if (!store.activeLocations().length && loaded()) {
      <p class="mb-5 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-100">Add a location first (a shop or a warehouse) under <a routerLink="/inventory/locations" class="underline">Locations</a>.</p>
    }

    <div class="card mb-5">
      <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <label class="block"><span class="label">Status</span><select class="input" [ngModel]="status()" (ngModelChange)="status.set($event); load()"><option value="">All</option><option value="Draft">Drafts</option><option value="Posted">Posted</option><option value="Void">Void</option></select></label>
        <label class="block"><span class="label">Location</span><select class="input" [ngModel]="location()" (ngModelChange)="location.set($event); load()"><option value="">All</option>@for (l of store.locations(); track l.id) { <option [value]="l.id">{{ l.code }} · {{ l.name }}</option> }</select></label>
        @if (kind() === 'grv') { <label class="block"><span class="label">Supplier</span><select class="input" [ngModel]="supplier()" (ngModelChange)="supplier.set($event); load()"><option value="">All</option>@for (s of suppliers(); track s.id) { <option [value]="s.id">{{ s.name }}</option> }</select></label> }
        <label class="block"><span class="label">From</span><input type="date" class="input" [ngModel]="from()" (ngModelChange)="from.set($event); load()" /></label>
        <label class="block"><span class="label">Search</span><input class="input" placeholder="Number, reference, reason" [ngModel]="q()" (ngModelChange)="q.set($event)" (keyup.enter)="load()" /></label>
      </div>
    </div>

    <div class="space-y-3">
      @for (d of docs(); track d.id) {
        <article class="card p-0" [class.opacity-60]="d.status === 'Void'">
          <button type="button" class="flex w-full flex-wrap items-center gap-3 px-5 py-3 text-left" (click)="open.set(open() === d.id ? '' : d.id)">
            <div class="min-w-0 flex-1">
              <div class="truncate text-sm font-semibold">{{ headline(d) }} <span class="font-mono text-xs font-normal text-slate-500">· {{ d.number || 'Draft' }}</span></div>
              <div class="text-xs text-slate-500">{{ d.date | date: 'd MMM y' }} · {{ d.lines.length }} line(s){{ d.supplierReference ? ' · their ref ' + d.supplierReference : '' }}{{ d.reason ? ' · ' + d.reason : '' }}</div>
            </div>
            <span class="badge" [class.badge-ok]="d.status === 'Posted'" [class.badge-warn]="d.status !== 'Posted'">{{ d.mode === 'count' ? 'Stock take · ' : '' }}{{ d.status }}</span>
            <div class="w-36 text-right text-sm font-semibold tabular-nums">{{ d.kind === 'transfer' && d.status !== 'Posted' ? '' : money(d.kind === 'adjustment' ? signedTotal(d) : d.total) }}</div>
            <app-icon name="chevron" size="h-4 w-4" class="text-slate-400 transition" [class.rotate-180]="open() === d.id" />
          </button>
          @if (open() === d.id) {
            <div class="space-y-3 border-t border-slate-100 px-5 py-3 text-sm dark:border-slate-800">
              <div class="overflow-x-auto"><table class="table w-full text-sm">
                <thead><tr><th>Item</th>@if (d.mode === 'count') { <th class="text-right">System</th><th class="text-right">Counted</th><th class="text-right">Difference</th> } @else { <th class="text-right">{{ d.kind === 'adjustment' ? 'Change' : 'Quantity' }}</th> }
                  <th class="text-right">Unit cost</th>@if (d.kind === 'grv') { <th class="text-right">VAT %</th> }<th class="text-right">Amount</th></tr></thead>
                <tbody>@for (l of d.lines; track l.id) {
                  <tr><td>{{ l.name }} <span class="font-mono text-xs text-slate-500">{{ l.code }}</span></td>
                    @if (d.mode === 'count') { <td class="text-right tabular-nums">{{ qty(l.expected) }}</td><td class="text-right tabular-nums">{{ qty(l.counted) }}</td><td class="text-right tabular-nums" [class.text-red-600]="l.quantity < 0">{{ d.status === 'Posted' ? qty(l.quantity) : '' }}</td> }
                    @else { <td class="text-right tabular-nums">{{ qty(l.quantity) }} {{ l.unit }}</td> }
                    <td class="text-right tabular-nums">{{ money(l.unitCost) }}</td>@if (d.kind === 'grv') { <td class="text-right">{{ l.vatPercent }}</td> }<td class="text-right tabular-nums">{{ money(l.amount) }}</td></tr>
                }</tbody>
              </table></div>
              @if (d.kind === 'grv') { <div class="flex justify-end gap-6 text-sm"><span>Before VAT <b class="tabular-nums">{{ money(d.subtotal) }}</b></span><span>VAT <b class="tabular-nums">{{ money(d.vat) }}</b></span><span>Total <b class="tabular-nums">{{ money(d.total) }}</b></span></div> }
              @if (d.notes) { <p class="text-slate-600 dark:text-slate-300">{{ d.notes }}</p> }
              <div class="flex flex-wrap items-center justify-between gap-2 text-xs text-slate-500">
                <span>{{ d.journalNumber ? 'Posted to the books as ' + d.journalNumber : d.status === 'Posted' ? 'No value moved in the books' : 'Not posted yet' }}@if (d.billId) { · <a routerLink="/accounting/bills" class="text-brand-600 hover:underline">supplier bill</a> }{{ d.createdBy ? ' · by ' + d.createdBy : '' }}</span>
                <span class="flex flex-wrap gap-2">
                  <button class="btn-ghost btn-sm" (click)="pdf(d)"><app-icon name="download" size="h-4 w-4" />{{ d.mode === 'count' && d.status === 'Draft' ? 'Count sheet' : 'PDF' }}</button>
                  @if (auth.can('inventory.manage')) {
                    @if (d.status === 'Draft') {
                      <button class="btn-ghost btn-sm" (click)="edit(d)"><app-icon name="edit" size="h-4 w-4" />Edit</button>
                      <button class="btn-ghost btn-sm" (click)="remove(d)"><app-icon name="trash" size="h-4 w-4" />Delete</button>
                      <button class="btn btn-sm" (click)="post(d)"><app-icon name="check" size="h-4 w-4" />Post</button>
                    }
                    @if (d.status === 'Posted') { <button class="btn-ghost btn-sm" (click)="voidDoc(d)">Void</button> }
                  }
                </span>
              </div>
            </div>
          }
        </article>
      } @empty { <div class="card text-center text-slate-500">{{ loaded() ? text().none : 'Loading…' }}</div> }
    </div>

    @if (form(); as f) {
      <app-modal [title]="formTitle(f)" [subtitle]="f.id ? 'Draft: saved until you post it' : 'Saved as a draft; nothing moves until you post it'" width="max-w-5xl" (closed)="form.set(null)">
        <form (ngSubmit)="save(false)">
          <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            <label class="block"><span class="label">Date</span><input type="date" class="input" name="d" [(ngModel)]="f.date" required /></label>
            <label class="block"><span class="label">{{ kind() === 'transfer' ? 'From' : kind() === 'grv' ? 'Received into' : 'Location' }}</span><select class="input" name="l" [(ngModel)]="f.locationId" required><option value="">Choose…</option>@for (l of store.activeLocations(); track l.id) { <option [value]="l.id">{{ l.code }} · {{ l.name }}</option> }</select></label>
            @if (kind() === 'transfer') { <label class="block"><span class="label">To</span><select class="input" name="t" [(ngModel)]="f.toLocationId" required><option value="">Choose…</option>@for (l of store.activeLocations(); track l.id) { @if (l.id !== f.locationId) { <option [value]="l.id">{{ l.code }} · {{ l.name }}</option> } }</select></label> }
            @if (kind() === 'grv') {
              <label class="block"><span class="label">Supplier</span><select class="input" name="s" [(ngModel)]="f.supplierId" required><option value="">Choose…</option>@for (s of suppliers(); track s.id) { @if (s.isActive) { <option [value]="s.id">{{ s.name }}</option> } }</select><span class="hint"><a routerLink="/accounting/suppliers" class="text-brand-600 hover:underline">Add a supplier</a></span></label>
              <label class="block"><span class="label">Their invoice / delivery note</span><input class="input" name="r" [(ngModel)]="f.supplierReference" maxlength="50" /><span class="hint">Becomes the bill's number.</span></label>
            }
            @if (kind() !== 'grv') { <label class="block" [class.lg:col-span-2]="kind() === 'adjustment'"><span class="label">Reason</span><input class="input" name="re" [(ngModel)]="f.reason" maxlength="200" [placeholder]="f.mode === 'count' ? 'Month-end count' : kind() === 'transfer' ? 'Restock the Bulawayo shop' : 'Water damage'" /></label> }
          </div>

          <div class="mt-4 flex flex-wrap items-center gap-2">
            <label class="btn-ghost btn-sm cursor-pointer"><app-icon name="upload" size="h-4 w-4" />Lines from Excel or CSV<input type="file" class="hidden" accept=".xlsx,.csv,.txt" (change)="upload(f, $event)" /></label>
            <button type="button" class="btn-ghost btn-sm" (click)="template(f)"><app-icon name="download" size="h-4 w-4" />{{ f.mode === 'count' ? 'Count sheet for this location' : 'Excel template' }}</button>
            @if (f.mode === 'count' || kind() === 'transfer') { <button type="button" class="btn-ghost btn-sm" (click)="loadAll(f)" [disabled]="!f.locationId"><app-icon name="stack" size="h-4 w-4" />Every item at this location</button> }
          </div>
          @for (p of fileProblems(); track $index) { <p class="mt-1 text-xs text-red-700 dark:text-red-300">{{ p }}</p> }

          <div class="mt-3 max-h-[26rem] overflow-auto">
            <table class="w-full text-sm">
              <thead class="sticky top-0 bg-white dark:bg-slate-900"><tr class="text-left text-xs text-slate-500">
                <th class="pb-1">Item</th><th class="w-24 pb-1 text-right">On hand</th>
                @if (f.mode === 'count') { <th class="w-28 pb-1 text-right">Counted</th> } @else { <th class="w-28 pb-1 text-right">{{ kind() === 'adjustment' ? 'Change (+/-)' : 'Quantity' }}</th> }
                @if (kind() !== 'transfer' && f.mode !== 'count') { <th class="w-28 pb-1 text-right">Unit cost</th> }
                @if (kind() === 'grv') { <th class="w-20 pb-1 text-right">VAT %</th><th class="w-28 pb-1 text-right">Amount</th> }
                <th class="w-8"></th></tr></thead>
              <tbody>
                @for (l of f.lines; track $index; let i = $index) {
                  <tr class="align-top">
                    <td class="py-1 pr-2"><input class="input" [name]="'it' + i" [(ngModel)]="l.text" (change)="pick(l)" list="stock-items" placeholder="Code, barcode or name" [class.border-red-400]="l.text && !l.productId" /></td>
                    <td class="py-1 pr-2 pt-3 text-right text-xs tabular-nums text-slate-500">{{ onHand(l.productId, f.locationId) }}</td>
                    @if (f.mode === 'count') { <td class="py-1 pr-2"><input type="number" step="any" min="0" class="input text-right" [name]="'c' + i" [(ngModel)]="l.counted" /></td> }
                    @else { <td class="py-1 pr-2"><input type="number" step="any" class="input text-right" [name]="'q' + i" [(ngModel)]="l.quantity" /></td> }
                    @if (kind() !== 'transfer' && f.mode !== 'count') { <td class="py-1 pr-2"><input type="number" step="0.0001" min="0" class="input text-right" [name]="'u' + i" [(ngModel)]="l.unitCost" [placeholder]="kind() === 'adjustment' ? 'average' : ''" /></td> }
                    @if (kind() === 'grv') { <td class="py-1 pr-2"><input type="number" step="0.01" min="0" max="100" class="input text-right" [name]="'v' + i" [(ngModel)]="l.vatPercent" /></td><td class="py-1 pt-3 text-right tabular-nums">{{ money(lineAmount(l)) }}</td> }
                    <td class="py-1"><button type="button" class="btn-ghost btn-sm" (click)="removeLine(f, i)" aria-label="Remove"><app-icon name="x" size="h-4 w-4" /></button></td>
                  </tr>
                }
              </tbody>
            </table>
            <datalist id="stock-items">@for (it of store.stockItems(); track it.id) { <option [value]="store.label(it)"></option> }</datalist>
          </div>
          <button type="button" class="btn-ghost btn-sm mt-2" (click)="addLine(f)"><app-icon name="plus" size="h-4 w-4" />Add a line</button>
          @if (kind() === 'grv') { <p class="mt-2 text-right text-sm">Before VAT <b class="tabular-nums">{{ money(sub(f)) }}</b> · VAT <b class="tabular-nums">{{ money(vat(f)) }}</b> · Total <b class="tabular-nums">{{ money(sub(f) + vat(f)) }} {{ store.settings()?.currency }}</b></p> }
          <label class="mt-3 block"><span class="label">Notes (optional)</span><input class="input" name="n" [(ngModel)]="f.notes" maxlength="500" /></label>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
            <button class="btn-ghost" type="button" (click)="form.set(null)">Cancel</button>
            <button class="btn-ghost" type="submit" [disabled]="busy()">Save draft</button>
            <button class="btn" type="button" (click)="save(true)" [disabled]="busy()">Save and post</button>
          </div>
        </form>
      </app-modal>
    }
  `,
})
export class StockDocumentsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly route = inject(ActivatedRoute);
  readonly store = inject(InventoryStore);
  readonly auth = inject(AuthService);
  readonly money = money; readonly qty = qty;
  readonly kind = signal<Kind>('grv');
  readonly text = computed(() => TEXT[this.kind()]);
  readonly docs = signal<StockDocument[]>([]);
  readonly suppliers = signal<Supplier[]>([]);
  readonly loaded = signal(false);
  readonly open = signal('');
  readonly status = signal(''); readonly location = signal(''); readonly supplier = signal(''); readonly q = signal('');
  readonly from = signal(isoDate(new Date(new Date().getFullYear(), new Date().getMonth() - 2, 1)));
  readonly form = signal<DocForm | null>(null);
  readonly fileProblems = signal<string[]>([]);
  readonly busy = signal(false); readonly error = signal('');

  async ngOnInit(): Promise<void> {
    this.kind.set((this.route.snapshot.data['kind'] as Kind) ?? 'grv');
    try {
      await this.store.loadAll();
      if (this.kind() === 'grv') this.suppliers.set(await this.api.get<Supplier[]>('/api/v1/accounting/suppliers'));
    } catch (e) { this.toast.err(e); }
    await this.load();
  }

  async load(): Promise<void> {
    try {
      this.docs.set(await this.api.get<StockDocument[]>('/api/v1/inventory/documents', { kind: this.kind(), status: this.status(), locationId: this.location(), supplierId: this.supplier(), q: this.q(), from: this.from() }));
      this.loaded.set(true);
    } catch (e) { this.toast.err(e); }
  }

  headline(d: StockDocument): string {
    if (d.kind === 'grv') return (d.supplier ?? '') + ' → ' + d.location;
    if (d.kind === 'transfer') return d.location + ' → ' + (d.toLocation ?? '');
    return (d.mode === 'count' ? 'Stock take at ' : 'Adjustment at ') + d.location;
  }
  signedTotal(d: StockDocument): number { return d.lines.reduce((s, l) => s + (l.quantity < 0 ? -l.amount : l.amount) * (d.status === 'Posted' ? 1 : 0), 0); }
  formTitle(f: DocForm): string { return (f.id ? 'Edit ' : 'New ') + (f.mode === 'count' ? 'stock take' : this.text().one); }
  onHand(productId: string | null, locationId: string): string {
    if (!productId) return '';
    const it = this.store.item(productId); if (!it) return '';
    return qty(it.levels.filter(l => l.locationId === locationId).reduce((s, l) => s + l.quantity, 0));
  }
  lineAmount(l: LineForm): number { return Math.round((Number(l.quantity) || 0) * (Number(l.unitCost) || 0) * 100) / 100; }
  sub(f: DocForm): number { return Math.round(f.lines.reduce((s, l) => s + this.lineAmount(l), 0) * 100) / 100; }
  vat(f: DocForm): number { return Math.round(f.lines.reduce((s, l) => s + Math.round(this.lineAmount(l) * (Number(l.vatPercent) || 0)) / 100, 0) * 100) / 100; }

  private blank(): LineForm { return { text: '', productId: null, quantity: null, unitCost: null, vatPercent: this.kind() === 'grv' ? 15 : null, counted: null }; }
  private refresh(): void { this.form.update(x => (x ? { ...x } : x)); }
  addLine(f: DocForm): void { f.lines.push(this.blank()); this.refresh(); }
  removeLine(f: DocForm, i: number): void { f.lines.splice(i, 1); if (!f.lines.length) f.lines.push(this.blank()); this.refresh(); }
  pick(l: LineForm): void {
    const it = this.store.find(l.text);
    l.productId = it?.id ?? null;
    if (it) { l.text = this.store.label(it); if (this.kind() === 'grv' && l.unitCost == null && it.lastCost) l.unitCost = it.lastCost; }
    this.refresh();
  }

  start(mode: string): void {
    this.error.set(''); this.fileProblems.set([]);
    const def = this.store.settings()?.defaultLocationId ?? this.store.activeLocations()[0]?.id ?? '';
    this.form.set({ id: null, date: isoDate(new Date()), locationId: def, toLocationId: '', supplierId: this.supplier(), supplierReference: '', mode: this.kind() === 'adjustment' ? mode : 'adjust', reason: '', notes: '', lines: [this.blank()] });
  }
  edit(d: StockDocument): void {
    this.error.set(''); this.fileProblems.set([]);
    this.form.set({ id: d.id, date: d.date.slice(0, 10), locationId: d.locationId, toLocationId: d.toLocationId ?? '', supplierId: d.supplierId ?? '', supplierReference: d.supplierReference ?? '', mode: d.mode ?? 'adjust', reason: d.reason ?? '', notes: d.notes ?? '',
      lines: d.lines.map(l => { const it = this.store.item(l.productId); return { text: it ? this.store.label(it) : l.name, productId: l.productId, quantity: l.quantity || null, unitCost: d.kind === 'transfer' ? null : l.unitCost || null, vatPercent: l.vatPercent, counted: l.counted ?? null }; }) });
  }

  loadAll(f: DocForm): void {
    const have = new Set(f.lines.filter(l => l.productId).map(l => l.productId));
    const add = this.store.stockItems().filter(i => !have.has(i.id) && (f.mode === 'count' || i.levels.some(l => l.locationId === f.locationId && l.quantity > 0)))
      .map(i => ({ ...this.blank(), text: this.store.label(i), productId: i.id }));
    f.lines = [...f.lines.filter(l => l.productId), ...add];
    if (!f.lines.length) f.lines.push(this.blank());
    this.refresh();
  }

  async template(f: DocForm): Promise<void> {
    try {
      if (f.mode === 'count') { if (!f.locationId) return; await this.api.download('/api/v1/inventory/templates/count?locationId=' + f.locationId, 'count-sheet.xlsx'); }
      else await this.api.download('/api/v1/inventory/templates/' + this.kind(), `fiscalhub-${this.kind()}-template.xlsx`);
    } catch (e) { this.toast.err(e); }
  }

  async upload(f: DocForm, ev: Event): Promise<void> {
    const input = ev.target as HTMLInputElement; const file = input.files?.[0]; input.value = '';
    if (!file) return;
    try {
      const fd = new FormData(); fd.append('file', file, file.name);
      const r = await this.api.post<LineFile>('/api/v1/inventory/import/lines/' + (f.mode === 'count' ? 'count' : this.kind()), fd);
      this.fileProblems.set(r.problems);
      const lines = r.lines.map(l => { const it = this.store.item(l.productId); return { text: it ? this.store.label(it) : l.name, productId: l.productId, quantity: l.quantity || null, unitCost: l.unitCost ?? null, vatPercent: l.vatPercent ?? (this.kind() === 'grv' ? 15 : null), counted: l.counted ?? null }; });
      f.lines = [...f.lines.filter(l => l.productId), ...lines];
      if (!f.lines.length) f.lines.push(this.blank());
      this.refresh(); this.toast.ok(`${r.lines.length} line(s) read from ${file.name}.`);
    } catch (e) { this.toast.err(e); }
  }

  async save(post: boolean): Promise<void> {
    const f = this.form(); if (!f) return;
    const missing = f.lines.find(l => l.text.trim() && !l.productId);
    if (missing) { this.error.set(`"${missing.text}" is not one of your stock items.`); return; }
    this.busy.set(true); this.error.set('');
    try {
      const doc = await this.api.put<StockDocument>('/api/v1/inventory/documents', {
        id: f.id, kind: this.kind(), date: f.date, locationId: f.locationId, toLocationId: f.toLocationId || null, supplierId: f.supplierId || null, supplierReference: f.supplierReference.trim() || null,
        mode: f.mode, reason: f.reason.trim() || null, notes: f.notes.trim() || null,
        lines: f.lines.filter(l => l.productId).map(l => ({ productId: l.productId, quantity: Number(l.quantity) || 0, unitCost: l.unitCost == null || l.unitCost === ('' as unknown) ? null : Number(l.unitCost), vatPercent: l.vatPercent == null ? null : Number(l.vatPercent), counted: l.counted == null ? null : Number(l.counted) })),
      });
      if (post) { const done = await this.api.post<StockDocument>('/api/v1/inventory/documents/' + doc.id + '/post'); this.toast.ok(`${done.number} posted.`); }
      else this.toast.ok('Draft saved.');
      this.form.set(null); this.open.set(doc.id); await Promise.all([this.load(), this.store.loadItems()]);
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  async post(d: StockDocument): Promise<void> {
    const what = d.kind === 'grv' ? 'The stock comes in and the supplier\'s bill is raised.' : d.kind === 'transfer' ? 'The stock moves now.' : 'The differences are posted to the books.';
    if (!(await this.dialog.confirm(`Post this ${d.mode === 'count' ? 'stock take' : this.text().one}? ${what}`, 'Post'))) return;
    try { const done = await this.api.post<StockDocument>('/api/v1/inventory/documents/' + d.id + '/post'); this.toast.ok(`${done.number} posted.`); await Promise.all([this.load(), this.store.loadItems()]); } catch (e) { this.toast.err(e); }
  }
  async voidDoc(d: StockDocument): Promise<void> {
    if (!(await this.dialog.confirm(`Void ${d.number}? Its stock moves back${d.journalNumber ? ' and its posting is reversed' : ''}${d.billId ? ', and the supplier\'s bill is voided' : ''}.`, 'Void', true))) return;
    try { await this.api.post('/api/v1/inventory/documents/' + d.id + '/void'); this.toast.ok(`${d.number} voided.`); await Promise.all([this.load(), this.store.loadItems()]); } catch (e) { this.toast.err(e); }
  }
  async remove(d: StockDocument): Promise<void> {
    if (!(await this.dialog.confirm('Delete this draft?', 'Delete', true))) return;
    try { await this.api.delete('/api/v1/inventory/documents/' + d.id); await this.load(); } catch (e) { this.toast.err(e); }
  }
  async pdf(d: StockDocument): Promise<void> { try { await this.api.open('/api/v1/inventory/documents/' + d.id + '/pdf'); } catch (e) { this.toast.err(e); } }
}
