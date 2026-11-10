import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AskReportResponse, ReportMeta, ReportResult, ReportSpec, SavedReport } from '../core/ai.models';
import { Api, errorMessage } from '../core/api.service';
import { DialogService, ToastService } from '../core/ui.services';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

const fmt = (v: unknown, kind: string): string => {
  if (typeof v !== 'number') return v == null ? '' : String(v);
  return kind === 'money' ? v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : v.toLocaleString(undefined, { maximumFractionDigits: 2 });
};

/**
 * Ask a question about your sales in plain words. The AI only chooses what to report (a measure, a grouping, a period); the figures are always worked out
 * by the gateway from your receipts, so they cannot be invented. The same report can be built by hand below, and saved reports run again for free.
 */
@Component({
  selector: 'app-ai-reports',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, PageHeaderComponent],
  template: `
    <app-page-header title="AI reports" subtitle="Ask about your sales in plain words, or build the report yourself. The numbers always come from your fiscal receipts." />

    <!-- ask -->
    <section class="card mb-5 border-brand-100 bg-linear-to-br from-brand-50/60 to-white dark:border-brand-500/20 dark:from-brand-500/10 dark:to-slate-900">
      <form class="flex flex-col gap-3 sm:flex-row" (ngSubmit)="ask()">
        <div class="relative flex-1">
          <span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-brand-600"><app-icon name="bolt" size="h-5 w-5" /></span>
          <input class="input pl-10 text-base" name="q" [(ngModel)]="question" maxlength="500" autocomplete="off" [disabled]="!meta()?.aiAvailable"
            [placeholder]="meta()?.aiAvailable ? 'e.g. Top 10 items sold this month' : 'The AI is not switched on. Build a report below.'" />
        </div>
        <button class="btn" type="submit" [disabled]="busy() || !meta()?.aiAvailable || question.trim().length < 3">{{ busy() === 'ask' ? 'Thinking…' : 'Ask' }}</button>
      </form>
      @if (meta()?.aiAvailable) {
        <div class="mt-3 flex flex-wrap gap-2">
          @for (e of meta()!.examples; track e) { <button type="button" class="rounded-full border border-slate-200 bg-white px-3 py-1 text-xs text-slate-600 transition hover:border-brand-400 hover:text-brand-700 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300" (click)="question = e; ask()">{{ e }}</button> }
        </div>
        <p class="mt-3 text-xs text-slate-500">Only your question and the names of your machines go to Google Gemini. Your receipts and customers' details never do.</p>
      }
      @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
    </section>

    <!-- result -->
    @if (result(); as r) {
      <section class="card mb-5">
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div><h2 class="text-lg font-semibold">{{ r.title }}</h2><p class="text-sm text-slate-500">{{ r.from === r.to ? r.from : r.from + ' to ' + r.to }} · {{ r.documentsCounted }} receipts looked at</p></div>
          <div class="flex flex-wrap gap-2">
            <button type="button" class="btn-ghost btn-sm" (click)="tweak()"><app-icon name="edit" size="h-4 w-4" />Adjust</button>
            <button type="button" class="btn-ghost btn-sm" (click)="exportCsv()" [disabled]="!r.rows.length"><app-icon name="download" size="h-4 w-4" />CSV</button>
            <button type="button" class="btn btn-sm" (click)="save()"><app-icon name="plus" size="h-4 w-4" />Save report</button>
          </div>
        </div>

        @if (summary()) {
          <div class="mt-4 flex gap-3 rounded-xl bg-brand-50 px-4 py-3 text-sm text-brand-900 dark:bg-brand-500/10 dark:text-brand-100">
            <span class="mt-0.5 text-brand-600"><app-icon name="bolt" size="h-4 w-4" /></span>
            <div><div>{{ summary() }}</div><div class="mt-1 text-xs opacity-70">AI comment. The figures below are calculated by the system.</div></div>
          </div>
        }

        @if (r.totals.length) {
          <div class="mt-4 flex flex-wrap gap-3">
            @for (t of r.totals; track t.currency) {
              <div class="rounded-xl border border-slate-200 px-4 py-2 dark:border-slate-700"><div class="text-xs text-slate-500">{{ t.label }} {{ r.metricLabel.toLowerCase() }}</div>
                <div class="text-xl font-semibold tabular-nums">{{ money(t.value) }} <span class="text-sm font-normal text-slate-500">{{ isMoney(r) ? t.currency : '' }}</span></div></div>
            }
          </div>
        }

        @if (chartRows().length > 1 && r.chart !== 'none') {
          <div class="mt-5">
            @if (r.chart === 'line') {
              <svg viewBox="0 0 600 180" class="h-48 w-full" role="img" [attr.aria-label]="r.title">
                <polyline [attr.points]="linePoints()" fill="none" stroke="currentColor" class="text-brand-600" stroke-width="2.5" stroke-linejoin="round" stroke-linecap="round" />
                @for (p of dots(); track $index) { <circle [attr.cx]="p.x" [attr.cy]="p.y" r="3.5" class="fill-brand-600"><title>{{ p.label }}</title></circle> }
              </svg>
              <div class="flex justify-between text-xs text-slate-500"><span>{{ chartRows()[0][0] }}</span><span>{{ chartRows()[chartRows().length - 1][0] }}</span></div>
            } @else {
              <ul class="space-y-1.5">
                @for (row of chartRows().slice(0, 15); track $index) {
                  <li class="flex items-center gap-3 text-sm"><span class="w-32 shrink-0 truncate text-slate-600 dark:text-slate-300 sm:w-44" [title]="'' + row[0]">{{ row[0] }}</span>
                    <span class="h-5 flex-1 overflow-hidden rounded bg-slate-100 dark:bg-slate-800"><span class="block h-full rounded bg-brand-500" [style.width.%]="barWidth(row[2])"></span></span>
                    <span class="w-24 shrink-0 text-right tabular-nums">{{ fmt(row[2], r.columns[2].kind) }}</span></li>
                }
              </ul>
            }
            @if (r.totals.length > 1) { <p class="mt-1 text-xs text-slate-500">The chart shows {{ chartCurrency() }}; other currencies are in the table.</p> }
          </div>
        }

        <div class="mt-5 overflow-x-auto">
          <table class="w-full text-sm">
            <thead><tr class="border-b border-slate-200 text-left text-xs uppercase tracking-wide text-slate-500 dark:border-slate-700">
              @for (c of r.columns; track c.key) { <th class="px-3 py-2 font-medium" [class.text-right]="c.kind !== 'text'">{{ c.label }}</th> }</tr></thead>
            <tbody>
              @for (row of r.rows; track $index) {
                <tr class="border-b border-slate-100 dark:border-slate-800">
                  @for (c of r.columns; track c.key; let i = $index) { <td class="px-3 py-2" [class]="c.kind !== 'text' ? 'text-right tabular-nums' : ''">{{ fmt(row[i], c.kind) }}</td> }
                </tr>
              } @empty { <tr><td [attr.colspan]="r.columns.length" class="px-3 py-8 text-center text-slate-500">No receipts matched this question in that period.</td></tr> }
            </tbody>
          </table>
        </div>
        @for (n of r.notes; track n) { <p class="mt-2 text-xs text-amber-700 dark:text-amber-300">{{ n }}</p> }
        @if (notice()) { <p class="mt-2 text-xs text-amber-700 dark:text-amber-300">{{ notice() }}</p> }
      </section>
    }

    <div class="grid gap-5 lg:grid-cols-3">
      <!-- builder -->
      <section class="card lg:col-span-2" id="builder">
        <h2 class="text-base font-semibold">Build a report yourself</h2>
        <p class="text-sm text-slate-500">No AI involved, so it costs nothing and is always available.</p>
        @if (meta(); as m) {
          <form class="mt-4 grid gap-4 sm:grid-cols-2" (ngSubmit)="run()">
            <label class="block"><span class="label">Measure</span><select class="input" name="metric" [(ngModel)]="spec.metric">@for (o of m.metrics; track o.id) { <option [value]="o.id">{{ o.label }}</option> }</select></label>
            <label class="block"><span class="label">Split by</span><select class="input" name="groupBy" [(ngModel)]="spec.groupBy">@for (o of m.groupBys; track o.id) { <option [value]="o.id">{{ o.label }}</option> }</select>
              @if (help()) { <span class="hint">{{ help() }}</span> }</label>
            <label class="block"><span class="label">Period</span><select class="input" name="period" [(ngModel)]="spec.period">@for (o of m.periods; track o.id) { <option [value]="o.id">{{ o.label }}</option> }</select></label>
            @if (spec.period === 'custom') {
              <div class="grid grid-cols-2 gap-2"><label class="block"><span class="label">From</span><input type="date" class="input" name="from" [(ngModel)]="from" /></label>
                <label class="block"><span class="label">To</span><input type="date" class="input" name="to" [(ngModel)]="to" /></label></div>
            } @else { <div></div> }
            <label class="block"><span class="label">Only this item (optional)</span><input class="input" name="item" [(ngModel)]="item" maxlength="60" placeholder="part of the item name" /></label>
            <label class="block"><span class="label">Only this customer (optional)</span><input class="input" name="buyer" [(ngModel)]="buyer" maxlength="80" placeholder="name or TIN" /></label>
            <label class="block"><span class="label">Payment method</span><select class="input" name="pm" [(ngModel)]="payment"><option value="">Any</option>@for (o of m.paymentMethods; track o) { <option [value]="o">{{ o }}</option> }</select></label>
            <div class="grid grid-cols-2 gap-2">
              <label class="block"><span class="label">Top rows</span><select class="input" name="top" [(ngModel)]="spec.top"><option [ngValue]="0">All</option><option [ngValue]="5">5</option><option [ngValue]="10">10</option><option [ngValue]="20">20</option></select></label>
              <label class="block"><span class="label">Chart</span><select class="input" name="chart" [(ngModel)]="spec.chart">@for (o of m.charts; track o.id) { <option [value]="o.id">{{ o.label }}</option> }</select></label>
            </div>
            @if (m.devices.length > 1) {
              <fieldset class="sm:col-span-2"><legend class="label">Machines</legend>
                <div class="flex flex-wrap gap-2">
                  @for (d of m.devices; track d.id) {
                    <label class="flex cursor-pointer items-center gap-2 rounded-lg border px-3 py-1.5 text-sm" [class]="devices().has(d.id) ? 'border-brand-500 bg-brand-50 dark:bg-brand-500/10' : 'border-slate-200 dark:border-slate-700'">
                      <input type="checkbox" class="accent-brand-600" [checked]="devices().has(d.id)" (change)="toggleDevice(d.id)" />{{ d.label }}</label>
                  }
                </div><span class="hint">Leave all unticked for every machine.</span></fieldset>
            }
            <div class="flex justify-end sm:col-span-2"><button class="btn" type="submit" [disabled]="busy() === 'run'">{{ busy() === 'run' ? 'Working…' : 'Run report' }}</button></div>
          </form>
        } @else { <p class="mt-4 text-sm text-slate-500">Loading…</p> }
      </section>

      <!-- saved -->
      <section class="card">
        <h2 class="text-base font-semibold">Saved reports</h2>
        <p class="text-sm text-slate-500">Run again any time with fresh dates, free of charge.</p>
        <ul class="mt-3 space-y-2">
          @for (s of saved(); track s.id) {
            <li class="flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 dark:border-slate-700">
              <button type="button" class="min-w-0 flex-1 text-left" (click)="runSaved(s)"><div class="truncate text-sm font-medium">{{ s.name }}</div><div class="truncate text-xs text-slate-500">{{ s.question || label(s.spec) }}</div></button>
              <button type="button" class="icon-btn" (click)="removeSaved(s)" [attr.aria-label]="'Delete ' + s.name"><app-icon name="trash" size="h-4 w-4" /></button>
            </li>
          } @empty { <li class="text-sm text-slate-500">Nothing saved yet. Run a report and press Save report.</li> }
        </ul>
      </section>
    </div>
  `,
})
export class AiReportsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly meta = signal<ReportMeta | null>(null);
  readonly result = signal<ReportResult | null>(null);
  readonly summary = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly saved = signal<SavedReport[]>([]);
  readonly busy = signal<'' | 'ask' | 'run'>('');
  readonly error = signal('');
  readonly devices = signal<Set<string>>(new Set());
  private lastQuestion: string | null = null;

  question = '';
  spec: ReportSpec = { metric: 'sales', groupBy: 'day', period: 'last_30_days', top: 0, order: 'auto', chart: 'auto' };
  from = ''; to = ''; item = ''; buyer = ''; payment = '';

  readonly fmt = fmt;
  isMoney(r: ReportResult): boolean { return r.columns[2]?.kind === 'money'; }
  money(v: number): string { return v.toLocaleString(undefined, { maximumFractionDigits: 2 }); }
  help(): string { return this.meta()?.groupBys.find(g => g.id === this.spec.groupBy)?.help ?? ''; }
  label(s: ReportSpec): string { const m = this.meta(); return `${m?.metrics.find(x => x.id === s.metric)?.label ?? s.metric}, ${m?.groupBys.find(x => x.id === s.groupBy)?.label.toLowerCase() ?? s.groupBy}`; }

  /** The chart plots one currency (the first, which is the largest block of rows); the table shows all. */
  readonly chartCurrency = computed(() => this.result()?.rows[0]?.[1] as string ?? '');
  readonly chartRows = computed(() => { const r = this.result(); if (!r) return []; const c = r.rows[0]?.[1]; return r.rows.filter(x => x[1] === c); });
  private readonly max = computed(() => Math.max(0, ...this.chartRows().map(r => Math.abs(Number(r[2])))));
  barWidth(v: unknown): number { const m = this.max(); return m ? Math.max(2, (Math.abs(Number(v)) / m) * 100) : 0; }
  readonly dots = computed(() => {
    const rows = this.chartRows(); const max = this.max() || 1; const n = rows.length;
    return rows.map((r, i) => ({ x: 12 + (n === 1 ? 288 : (576 * i) / (n - 1)), y: 168 - (Math.max(0, Number(r[2])) / max) * 150, label: `${r[0]}: ${fmt(r[2], 'number')}` }));
  });
  linePoints(): string { return this.dots().map(p => `${p.x},${p.y}`).join(' '); }

  async ngOnInit(): Promise<void> {
    try { this.meta.set(await this.api.get<ReportMeta>('/api/v1/ai/reports/meta')); } catch (e) { this.toast.err(e); }
    await this.loadSaved();
  }

  private async loadSaved(): Promise<void> { try { this.saved.set(await this.api.get<SavedReport[]>('/api/v1/ai/reports/saved')); } catch { /* the list is optional */ } }

  toggleDevice(id: string): void { this.devices.update(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; }); }

  private build(): ReportSpec {
    return {
      metric: this.spec.metric, groupBy: this.spec.groupBy, period: this.spec.period, from: this.spec.period === 'custom' ? this.from : null, to: this.spec.period === 'custom' ? this.to : null,
      top: this.spec.top, order: this.spec.order, chart: this.spec.chart, title: null,
      filters: { deviceIds: [...this.devices()], item: this.item.trim() || null, buyer: this.buyer.trim() || null, paymentMethod: this.payment || null, currency: null, documentType: null },
    };
  }

  private show(spec: ReportSpec, result: ReportResult, summary: string | null, notice: string | null): void {
    this.result.set(result); this.summary.set(summary); this.notice.set(notice);
    this.spec = { ...spec }; this.from = spec.from ?? ''; this.to = spec.to ?? ''; this.item = spec.filters?.item ?? ''; this.buyer = spec.filters?.buyer ?? ''; this.payment = spec.filters?.paymentMethod ?? '';
    this.devices.set(new Set(spec.filters?.deviceIds ?? []));
  }

  async ask(): Promise<void> {
    const q = this.question.trim(); if (q.length < 3 || this.busy()) return;
    this.busy.set('ask'); this.error.set('');
    try {
      const r = await this.api.post<AskReportResponse>('/api/v1/ai/reports/ask', { question: q });
      this.lastQuestion = q; this.show(r.spec, r.result, r.summary ?? null, r.notice ?? null);
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(''); }
  }

  async run(): Promise<void> {
    this.busy.set('run'); this.error.set('');
    try { const spec = this.build(); const r = await this.api.post<ReportResult>('/api/v1/ai/reports/run', { spec }); this.lastQuestion = null; this.show(spec, r, null, null); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(''); }
  }

  tweak(): void { document.getElementById('builder')?.scrollIntoView({ behavior: 'smooth', block: 'start' }); }

  async save(): Promise<void> {
    const r = this.result(); if (!r) return;
    const name = await this.dialog.prompt('Name this report', r.title, 'Save');
    if (!name?.trim()) return;
    try { await this.api.post('/api/v1/ai/reports/saved', { name: name.trim(), question: this.lastQuestion, spec: this.build() }); this.toast.ok('Report saved.'); await this.loadSaved(); } catch (e) { this.toast.err(e); }
  }

  async runSaved(s: SavedReport): Promise<void> {
    this.error.set('');
    try { const r = await this.api.post<ReportResult>(`/api/v1/ai/reports/saved/${s.id}/run`); this.lastQuestion = s.question ?? null; this.show(s.spec, r, null, null); window.scrollTo({ top: 0, behavior: 'smooth' }); }
    catch (e) { this.toast.err(e); }
  }

  async removeSaved(s: SavedReport): Promise<void> {
    if (!(await this.dialog.confirm(`Delete the saved report "${s.name}"?`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/ai/reports/saved/${s.id}`); await this.loadSaved(); } catch (e) { this.toast.err(e); }
  }

  exportCsv(): void {
    const r = this.result(); if (!r) return;
    const esc = (v: unknown): string => { const s = v == null ? '' : String(v); return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s; };
    const text = [r.columns.map(c => esc(c.label)).join(','), ...r.rows.map(row => row.map(esc).join(','))].join('\r\n');
    const url = URL.createObjectURL(new Blob(['﻿' + text], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a'); a.href = url; a.download = `${r.title.replace(/[^A-Za-z0-9]+/g, '-').toLowerCase()}.csv`; document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }
}
