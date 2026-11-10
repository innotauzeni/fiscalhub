import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TrendPoint } from '../core/models';

/** Last-7-days bars (pure SVG, no chart library). The red part of a bar is the share ZIMRA rejected. */
@Component({
  selector: 'app-trend-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <svg viewBox="0 0 700 240" class="h-60 w-full" role="img" aria-label="Receipts per day, last 7 days">
      @for (g of grid(); track g.y) {
        <line x1="36" x2="690" [attr.y1]="g.y" [attr.y2]="g.y" class="stroke-slate-200 dark:stroke-slate-800" stroke-dasharray="4 4" />
        <text x="28" [attr.y]="g.y + 4" text-anchor="end" class="fill-slate-400 text-[11px]">{{ g.v }}</text>
      }
      @for (b of bars(); track b.date) {
        <g>
          <title>{{ b.date }}: {{ b.count }} receipts{{ b.rejected ? ', ' + b.rejected + ' rejected' : '' }}</title>
          <rect [attr.x]="b.x" [attr.y]="b.y" [attr.width]="b.w" [attr.height]="b.h" rx="6" class="fill-blue-500 transition-opacity hover:opacity-80 dark:fill-blue-400" />
          @if (b.rejH > 0) { <rect [attr.x]="b.x" [attr.y]="b.y + b.h - b.rejH" [attr.width]="b.w" [attr.height]="b.rejH" rx="6" class="fill-rose-500" /> }
          <text [attr.x]="b.x + b.w / 2" [attr.y]="b.y - 6" text-anchor="middle" class="fill-slate-600 text-[12px] font-medium dark:fill-slate-300">{{ b.count || '' }}</text>
          <text [attr.x]="b.x + b.w / 2" y="228" text-anchor="middle" class="fill-slate-500 text-[11px]">{{ b.date | date:'EEE' }}</text>
        </g>
      }
    </svg>`,
})
export class TrendChartComponent {
  readonly points = input.required<TrendPoint[]>();
  private readonly max = computed(() => Math.max(4, ...this.points().map(p => p.count)));
  readonly grid = computed(() => [0, 0.5, 1].map(f => ({ v: Math.round(this.max() * f), y: 200 - f * 170 })));
  readonly bars = computed(() => {
    const pts = this.points(), n = Math.max(pts.length, 1), slot = 654 / n, w = Math.min(54, slot * 0.6), m = this.max();
    return pts.map((p, i) => {
      const h = (p.count / m) * 170;
      return { date: p.date, count: p.count, rejected: p.rejected, x: 36 + i * slot + (slot - w) / 2, w, h, y: 200 - h, rejH: p.count ? (p.rejected / p.count) * h : 0 };
    });
  });
}

export interface DonutSegment { label: string; value: number; stroke: string; dot: string; }

/** Ring chart with a legend; segments with a zero value are skipped. */
@Component({
  selector: 'app-donut',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex flex-wrap items-center gap-6">
      <svg viewBox="0 0 42 42" class="h-40 w-40 shrink-0 -rotate-90" role="img" aria-label="Receipts by status">
        <circle cx="21" cy="21" r="15.9155" fill="none" stroke-width="5" class="stroke-slate-100 dark:stroke-slate-800" />
        @for (s of arcs(); track s.label) {
          <circle cx="21" cy="21" r="15.9155" fill="none" stroke-width="5" [attr.stroke-dasharray]="s.dash" [attr.stroke-dashoffset]="s.offset" [class]="s.stroke" />
        }
      </svg>
      <div class="min-w-36 flex-1 space-y-2 text-sm">
        <div class="text-2xl font-semibold tabular-nums">{{ total() }}<span class="ml-1 text-xs font-normal text-slate-500">receipts</span></div>
        @for (s of segments(); track s.label) {
          <div class="flex items-center justify-between gap-3"><span class="flex items-center gap-2"><span class="h-2.5 w-2.5 rounded-full" [class]="s.dot"></span>{{ s.label }}</span><span class="tabular-nums text-slate-500">{{ s.value }}</span></div>
        }
      </div>
    </div>`,
})
export class DonutComponent {
  readonly segments = input.required<DonutSegment[]>();
  readonly total = computed(() => this.segments().reduce((a, s) => a + s.value, 0));
  readonly arcs = computed(() => {
    const t = this.total();
    let acc = 0;
    return t === 0 ? [] : this.segments().filter(s => s.value > 0).map(s => {
      const len = (s.value / t) * 100, shown = Math.max(len - 0.6, 0.1);
      const arc = { label: s.label, stroke: s.stroke, dash: `${shown} ${100 - shown}`, offset: -acc };
      acc += len;
      return arc;
    });
  });
}
