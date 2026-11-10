import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { ComplianceCheck, ComplianceReport } from '../core/models';
import { ToastService } from '../core/ui.services';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

/**
 * Everything that makes ZIMRA reject, block or flag a company, in one list: certificates about to expire, fiscal days left open, receipts waiting or rejected,
 * offline files with errors, and the state of the subscription. Each line says what is wrong, in plain words, and links to the page that fixes it.
 */
@Component({
  selector: 'app-compliance',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, IconComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Compliance centre" subtitle="Is everything in order with ZIMRA? Fix anything red or amber before it stops you invoicing.">
      <button class="btn-ghost" (click)="load()" [disabled]="busy()"><app-icon name="refresh" size="h-4 w-4" />{{ busy() ? 'Checking…' : 'Check again' }}</button>
    </app-page-header>

    @if (r(); as c) {
      <div class="mb-6 grid gap-5 lg:grid-cols-3">
        <div class="card flex items-center gap-6 lg:col-span-1">
          <div class="relative h-28 w-28 shrink-0">
            <svg viewBox="0 0 120 120" class="h-28 w-28 -rotate-90" aria-hidden="true">
              <circle cx="60" cy="60" r="52" fill="none" stroke-width="12" class="stroke-slate-100 dark:stroke-slate-800" />
              <circle cx="60" cy="60" r="52" fill="none" stroke-width="12" stroke-linecap="round" [attr.stroke-dasharray]="dash(c.score)" [class]="ring(c.status)" />
            </svg>
            <div class="absolute inset-0 flex flex-col items-center justify-center"><span class="text-3xl font-bold tabular-nums">{{ c.score }}</span><span class="text-[10px] uppercase tracking-wide text-slate-500">of 100</span></div>
          </div>
          <div>
            <div class="text-lg font-semibold" [class]="c.status === 'critical' ? 'text-red-600' : c.status === 'warn' ? 'text-amber-600' : 'text-emerald-600'">{{ headline(c.status) }}</div>
            <p class="mt-1 text-sm text-slate-600 dark:text-slate-400">{{ c.critical }} to fix now · {{ c.warnings }} to watch · {{ c.ok }} fine</p>
            <p class="hint">Checked {{ c.checkedAt | date: 'HH:mm' }}</p>
          </div>
        </div>
        <div class="grid grid-cols-3 gap-4 lg:col-span-2">
          @for (s of summary(); track s.label) {
            <div class="rounded-2xl border p-5" [class]="s.box"><div class="text-xs font-medium uppercase tracking-wide opacity-70">{{ s.label }}</div><div class="mt-2 text-4xl font-bold tabular-nums">{{ s.value }}</div></div>
          }
        </div>
      </div>

      @for (g of groups(); track g.status) {
        @if (g.items.length) {
          <h2 class="mb-2 mt-6 flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-slate-500"><span class="h-2 w-2 rounded-full" [class]="dot(g.status)"></span>{{ g.title }} ({{ g.items.length }})</h2>
          <div class="grid gap-3">
            @for (k of g.items; track k.id) {
              <div class="flex flex-wrap items-center gap-4 rounded-2xl border bg-white p-4 dark:bg-slate-900" [class]="border(k.status)">
                <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl" [class]="tile(k.status)"><app-icon [name]="icon(k.status)" /></span>
                <div class="min-w-0 flex-1">
                  <div class="flex flex-wrap items-center gap-2"><span class="font-medium">{{ k.title }}</span><span class="badge">{{ k.area }}</span>@if (k.device) { <span class="text-xs text-slate-500">{{ k.device }}</span> }</div>
                  <p class="mt-0.5 text-sm text-slate-600 dark:text-slate-400">{{ k.detail }}</p>
                </div>
                @if (k.route && (k.status === 'warn' || k.status === 'critical' || k.id === 'devices')) { <a class="btn-ghost btn-sm" [routerLink]="k.route">Fix</a> }
              </div>
            }
          </div>
        }
      }
    } @else { <div class="card animate-pulse"><div class="h-28 rounded-xl bg-slate-100 dark:bg-slate-800"></div></div> }
  `,
})
export class ComplianceComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly r = signal<ComplianceReport | null>(null);
  readonly busy = signal(false);

  readonly groups = computed(() => {
    const items = this.r()?.checks ?? [];
    const by = (s: string) => items.filter(c => c.status === s);
    return [{ status: 'critical', title: 'Fix now', items: by('critical') }, { status: 'warn', title: 'Keep an eye on', items: by('warn') }, { status: 'info', title: 'Good to know', items: by('info') }, { status: 'ok', title: 'All good', items: by('ok') }] as { status: string; title: string; items: ComplianceCheck[] }[];
  });
  readonly summary = computed(() => {
    const c = this.r(); if (!c) return [];
    return [{ label: 'Fix now', value: c.critical, box: c.critical ? 'border-red-200 bg-red-50 text-red-700 dark:border-red-900/50 dark:bg-red-900/20 dark:text-red-300' : 'border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900' },
      { label: 'Watch', value: c.warnings, box: c.warnings ? 'border-amber-200 bg-amber-50 text-amber-700 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-300' : 'border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900' },
      { label: 'Fine', value: c.ok, box: 'border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-900/50 dark:bg-emerald-900/20 dark:text-emerald-300' }];
  });

  dash(score: number): string { const len = 2 * Math.PI * 52; return `${(score / 100) * len} ${len}`; }
  ring(s: string): string { return s === 'critical' ? 'stroke-red-500' : s === 'warn' ? 'stroke-amber-500' : 'stroke-emerald-500'; }
  headline(s: string): string { return s === 'critical' ? 'Action needed' : s === 'warn' ? 'Mostly fine' : 'In good order'; }
  dot(s: string): string { return s === 'critical' ? 'bg-red-500' : s === 'warn' ? 'bg-amber-500' : s === 'info' ? 'bg-sky-500' : 'bg-emerald-500'; }
  border(s: string): string { return s === 'critical' ? 'border-red-200 dark:border-red-900/50' : s === 'warn' ? 'border-amber-200 dark:border-amber-900/50' : 'border-slate-200 dark:border-slate-800'; }
  tile(s: string): string { return s === 'critical' ? 'bg-red-100 text-red-600 dark:bg-red-900/30' : s === 'warn' ? 'bg-amber-100 text-amber-600 dark:bg-amber-900/30' : s === 'info' ? 'bg-sky-100 text-sky-600 dark:bg-sky-900/30' : 'bg-emerald-100 text-emerald-600 dark:bg-emerald-900/30'; }
  icon(s: string): string { return s === 'ok' ? 'check' : s === 'info' ? 'circle' : 'alert'; }

  ngOnInit(): Promise<void> { return this.load(); }
  async load(): Promise<void> {
    this.busy.set(true);
    try { this.r.set(await this.api.get<ComplianceReport>('/api/v1/compliance')); } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
