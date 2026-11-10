import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { IconComponent } from './ui.components';

export interface Crumb { label: string; link?: unknown[] | string; }

/** Section › Module › Submodule trail with a back link, like dara's drill-down pages. */
@Component({
  selector: 'app-crumbs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent],
  template: `
    <nav class="mb-3 flex flex-wrap items-center gap-1 text-sm text-slate-500 dark:text-slate-400" aria-label="Breadcrumb">
      @for (c of items(); track $index; let last = $last) {
        @if (c.link && !last) { <a [routerLink]="c.link" class="rounded-md px-1.5 py-0.5 font-medium text-brand-600 hover:bg-brand-50 dark:text-brand-100 dark:hover:bg-slate-800">{{ c.label }}</a> }
        @else { <span class="px-1.5 py-0.5" [class.font-semibold]="last" [class.text-slate-900]="last" [class.dark:text-slate-100]="last">{{ c.label }}</span> }
        @if (!last) { <app-icon name="chevron-right" size="h-3.5 w-3.5" /> }
      }
    </nav>`,
})
export class CrumbsComponent {
  readonly items = input.required<Crumb[]>();
}

/** « ‹ 1 2 3 › » pager with a "Showing x–y of z" label. */
@Component({
  selector: 'app-pager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="mt-3 flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-3 text-sm text-slate-500 dark:border-slate-800 dark:text-slate-400">
      <span>Showing <strong class="text-slate-700 dark:text-slate-200">{{ from() }}</strong>–<strong class="text-slate-700 dark:text-slate-200">{{ to() }}</strong> of <strong class="text-slate-700 dark:text-slate-200">{{ total() }}</strong></span>
      @if (pages() > 1) {
        <div class="flex items-center gap-1">
          <button type="button" class="pg" [disabled]="page() <= 1" (click)="go(1)" aria-label="First page">«</button>
          <button type="button" class="pg" [disabled]="page() <= 1" (click)="go(page() - 1)" aria-label="Previous page">‹</button>
          @for (n of window(); track n) { <button type="button" class="pg" [class.pg-active]="n === page()" (click)="go(n)">{{ n }}</button> }
          <button type="button" class="pg" [disabled]="page() >= pages()" (click)="go(page() + 1)" aria-label="Next page">›</button>
          <button type="button" class="pg" [disabled]="page() >= pages()" (click)="go(pages())" aria-label="Last page">»</button>
        </div>
      }
    </div>`,
})
export class PagerComponent {
  readonly page = input.required<number>();
  readonly pageSize = input(10);
  readonly total = input.required<number>();
  readonly pageChange = output<number>();

  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize())));
  readonly from = computed(() => (this.total() === 0 ? 0 : (this.page() - 1) * this.pageSize() + 1));
  readonly to = computed(() => Math.min(this.total(), this.page() * this.pageSize()));
  readonly window = computed(() => {
    const out: number[] = [];
    for (let n = Math.max(1, this.page() - 2); n <= Math.min(this.pages(), this.page() + 2); n++) out.push(n);
    return out;
  });

  go(n: number): void { this.pageChange.emit(Math.min(Math.max(1, n), this.pages())); }
}

/** Shared slice helper: the rows of one page. */
export function slicePage<T>(rows: T[], page: number, size: number): T[] { return rows.slice((page - 1) * size, page * size); }

/** Empty-state block for tables and grids. */
@Component({
  selector: 'app-empty',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    <div class="flex flex-col items-center gap-2 px-4 py-12 text-center">
      <div class="flex h-12 w-12 items-center justify-center rounded-2xl bg-slate-100 text-slate-400 dark:bg-slate-800"><app-icon [name]="icon()" size="h-6 w-6" /></div>
      <p class="font-medium text-slate-700 dark:text-slate-200">{{ title() }}</p>
      @if (hint()) { <p class="max-w-sm text-sm text-slate-500">{{ hint() }}</p> }
      <ng-content />
    </div>`,
})
export class EmptyComponent {
  readonly icon = input('folder');
  readonly title = input.required<string>();
  readonly hint = input('');
}
