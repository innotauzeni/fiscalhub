import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { IconComponent } from './ui.components';

/** Popup form. Closes with the X, Escape or the form's own Cancel button (a click on the backdrop does not, so typed data is never lost by accident). */
@Component({
  selector: 'app-modal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  host: { '(document:keydown.escape)': 'closed.emit()' },
  template: `
    <!-- never taller than the window: the title and close button stay put and a long form scrolls inside, so its top can never be cut off -->
    <div class="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/60 p-2 backdrop-blur-sm sm:p-4" role="dialog" aria-modal="true" [attr.aria-label]="title()">
      <div class="flex max-h-[calc(100dvh-1rem)] w-full flex-col rounded-2xl border border-slate-200 bg-white shadow-2xl sm:max-h-[calc(100dvh-2rem)] dark:border-slate-700 dark:bg-slate-900" [class]="width()">
        <div class="flex shrink-0 items-start justify-between gap-4 border-b border-slate-100 px-6 py-4 dark:border-slate-800">
          <div>
            <h2 class="text-base font-semibold">{{ title() }}</h2>
            @if (subtitle()) { <p class="mt-0.5 text-sm text-slate-500 dark:text-slate-400">{{ subtitle() }}</p> }
          </div>
          <button type="button" class="rounded-lg p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-800 dark:hover:text-slate-200" (click)="closed.emit()" aria-label="Close"><app-icon name="x" /></button>
        </div>
        <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain px-6 py-5"><ng-content /></div>
      </div>
    </div>`,
})
export class ModalComponent {
  readonly title = input.required<string>();
  readonly subtitle = input('');
  readonly width = input('max-w-2xl');
  readonly closed = output<void>();
}

const TONE_ICON: Record<string, string> = {
  blue: 'from-blue-500 to-indigo-500', emerald: 'from-emerald-500 to-teal-500', amber: 'from-amber-500 to-orange-500',
  rose: 'from-rose-500 to-pink-500', violet: 'from-violet-500 to-purple-500',
};

/** Headline number with a coloured icon tile. */
@Component({
  selector: 'app-stat-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    <div class="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm transition hover:-translate-y-0.5 hover:shadow-md dark:border-slate-800 dark:bg-slate-900">
      <div class="flex items-start justify-between">
        <div>
          <div class="text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">{{ label() }}</div>
          <div class="mt-2 text-3xl font-semibold tracking-tight tabular-nums">{{ value() }}</div>
        </div>
        <div class="flex h-11 w-11 items-center justify-center rounded-xl bg-linear-to-br text-white shadow-sm" [class]="tile()"><app-icon [name]="icon()" /></div>
      </div>
      @if (hint()) { <div class="mt-3 text-xs text-slate-500 dark:text-slate-400">{{ hint() }}</div> }
    </div>`,
})
export class StatCardComponent {
  readonly label = input.required<string>();
  readonly value = input.required<string | number>();
  readonly hint = input('');
  readonly icon = input('chart');
  readonly tone = input<'blue' | 'emerald' | 'amber' | 'rose' | 'violet'>('blue');
  readonly tile = computed(() => TONE_ICON[this.tone()]);
}
