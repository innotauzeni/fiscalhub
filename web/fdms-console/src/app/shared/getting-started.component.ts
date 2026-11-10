import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Checklist, UsageMeter } from '../core/models';
import { IconComponent } from './ui.components';

const KEY = 'fdms.checklist.hidden';
function read(): boolean { try { return localStorage.getItem(KEY) === '1'; } catch { return false; } }

/** The first-run list: what is left to do before the company is fully set up, each step ticking itself off. Can be hidden once the person has seen it. */
@Component({
  selector: 'app-getting-started',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent],
  template: `
    @if (checklist(); as c) {
      @if (c.done < c.total && !hidden()) {
        <section class="mb-6 rounded-2xl border border-brand-100 bg-brand-50/60 p-5 dark:border-brand-500/30 dark:bg-brand-500/10" aria-label="Getting started">
          <div class="flex flex-wrap items-center justify-between gap-3">
            <div><h2 class="text-lg font-semibold">Getting started</h2><p class="text-sm text-slate-600 dark:text-slate-300">{{ c.done }} of {{ c.total }} done. A few steps and you are fully set up.</p></div>
            <button class="text-sm text-slate-500 underline" (click)="hide()">Hide this</button>
          </div>
          <div class="mt-3 h-2 overflow-hidden rounded-full bg-white/70 dark:bg-slate-800"><div class="h-full rounded-full bg-brand-600 transition-all" [style.width.%]="c.done / c.total * 100"></div></div>
          <ol class="mt-4 grid gap-2 md:grid-cols-2">
            @for (s of c.steps; track s.id) {
              <li class="flex items-start gap-3 rounded-xl bg-white/80 p-3 dark:bg-slate-900/60" [class.opacity-60]="s.done">
                <span class="mt-0.5 flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-white" [class]="s.done ? 'bg-emerald-500' : 'border-2 border-slate-300 bg-transparent dark:border-slate-600'">@if (s.done) { <app-icon name="check" size="h-3.5 w-3.5" /> }</span>
                <div class="min-w-0 flex-1"><div class="text-sm font-medium" [class.line-through]="s.done">{{ s.title }}</div>
                  @if (!s.done) { <p class="text-xs text-slate-600 dark:text-slate-400">{{ s.help }}</p><a [routerLink]="s.route" class="mt-1.5 inline-block text-xs font-semibold text-brand-600 underline">{{ s.action }} →</a> }</div>
              </li>
            }
          </ol>
        </section>
      }
    }
    @if (showUsage(); as u) {
      <section class="mb-6 rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900" aria-label="Plan usage">
        <div class="mb-3 flex flex-wrap items-center justify-between gap-2"><h2 class="text-sm font-semibold uppercase tracking-wide text-slate-500">Your {{ u.plan }} plan</h2>
          <a routerLink="/billing" class="text-sm font-medium text-brand-600 underline">{{ worst() === 'over' ? 'Upgrade your plan' : 'See plans' }}</a></div>
        <div class="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
          @for (i of u.items; track i.key) {
            @if (i.limit > 0) {
              <div><div class="flex items-baseline justify-between text-sm"><span>{{ i.label }}</span><span class="tabular-nums text-slate-500">{{ i.used }} / {{ i.limit }}</span></div>
                <div class="mt-1.5 h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-slate-800" role="progressbar" [attr.aria-valuenow]="i.percent" aria-valuemin="0" aria-valuemax="100">
                  <div class="h-full rounded-full transition-all" [class]="i.level === 'over' ? 'bg-red-500' : i.level === 'warn' ? 'bg-amber-500' : 'bg-emerald-500'" [style.width.%]="i.percent > 100 ? 100 : i.percent"></div></div>
                @if (i.level !== 'ok') { <p class="mt-1 text-xs" [class]="i.level === 'over' ? 'text-red-600' : 'text-amber-600'">{{ i.level === 'over' ? 'Over your plan. Invoices still go through; please upgrade.' : i.percent + '% used' }}</p> }
              </div>
            }
          }
        </div>
      </section>
    }
  `,
})
export class GettingStartedComponent {
  readonly checklist = input<Checklist | null | undefined>(null);
  readonly usage = input<UsageMeter | null | undefined>(null);
  readonly hidden = signal(read());

  /** Usage is only worth a card when it is close to a limit (the Billing page always shows it). */
  readonly showUsage = computed(() => { const u = this.usage(); return u && u.limited && u.items.some(i => i.limit > 0 && i.level !== 'ok') ? u : null; });
  worst(): string { return this.usage()?.items.some(i => i.level === 'over') ? 'over' : 'warn'; }

  hide(): void { this.hidden.set(true); try { localStorage.setItem(KEY, '1'); } catch { /* not remembered */ } }
}
