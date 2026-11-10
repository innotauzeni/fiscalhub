import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Menu icons are stored by name in the database (SystemModule.Icon); this maps a name to inline SVG so no icon font or CDN is needed. */
const PATHS: Record<string, string> = {
  home: 'M3 12l9-9 9 9M5 10v10h5v-6h4v6h5V10',
  cpu: 'M9 3v2M15 3v2M9 19v2M15 19v2M3 9h2M3 15h2M19 9h2M19 15h2M7 5h10a2 2 0 012 2v10a2 2 0 01-2 2H7a2 2 0 01-2-2V7a2 2 0 012-2zM10 10h4v4h-4z',
  plus: 'M12 5v14M5 12h14',
  receipt: 'M6 3h12v18l-3-2-3 2-3-2-3 2V3zM9 8h6M9 12h6',
  upload: 'M12 16V4M7 9l5-5 5 5M4 20h16',
  download: 'M12 4v12M7 11l5 5 5-5M4 20h16',
  calendar: 'M5 5h14a2 2 0 012 2v12a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2zM3 10h18M8 3v4M16 3v4',
  'cloud-upload': 'M7 18a4 4 0 01-.5-7.97A5.5 5.5 0 0117.9 9 4.5 4.5 0 0117 18M12 12v7M9 14l3-3 3 3',
  key: 'M15 7a4 4 0 11-3.5 5.9L4 20.5V22h2v-2h2v-2h2l1.9-1.9A4 4 0 0115 7zM16 9h.01',
  bolt: 'M13 2L4 14h7l-1 8 9-12h-7l1-8z',
  building: 'M4 21V5l8-2v18M12 8h8v13M8 8h.01M8 12h.01M8 16h.01M16 12h.01M16 16h.01M3 21h18',
  users: 'M17 20v-1a4 4 0 00-4-4H7a4 4 0 00-4 4v1M10 11a3.5 3.5 0 100-7 3.5 3.5 0 000 7zM21 20v-1a4 4 0 00-3-3.87M16 4.13a3.5 3.5 0 010 6.74',
  shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6l8-3zM9 12l2 2 4-4',
  squares: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  clipboard: 'M9 4h6v3H9zM7 5H6a2 2 0 00-2 2v13a2 2 0 002 2h12a2 2 0 002-2V7a2 2 0 00-2-2h-1M8 12h8M8 16h5',
  chart: 'M4 20V10M10 20V4M16 20v-8M22 20H2',
  logout: 'M15 4h3a2 2 0 012 2v12a2 2 0 01-2 2h-3M10 17l5-5-5-5M15 12H3',
  sun: 'M12 16a4 4 0 100-8 4 4 0 000 8zM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4',
  moon: 'M21 12.8A9 9 0 1111.2 3a7 7 0 009.8 9.8z',
  menu: 'M4 6h16M4 12h16M4 18h16',
  x: 'M6 6l12 12M18 6L6 18',
  check: 'M5 12l5 5 9-10',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13M10 11v6M14 11v6',
  refresh: 'M20 11a8 8 0 00-14.9-3M4 4v4h4M4 13a8 8 0 0014.9 3M20 20v-4h-4',
  copy: 'M9 9h10v11H9zM5 15H4V4h11v1',
  search: 'M11 18a7 7 0 100-14 7 7 0 000 14zM21 21l-4.5-4.5',
  chevron: 'M6 9l6 6 6-6',
  alert: 'M12 3l10 18H2L12 3zM12 10v5M12 18h.01',
  circle: 'M12 8a4 4 0 100 8 4 4 0 000-8z',
  eye: 'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 15a3 3 0 100-6 3 3 0 000 6z',
  edit: 'M4 20h4L19 9l-4-4L4 16v4zM13 7l4 4',
  layers: 'M12 3l9 5-9 5-9-5 9-5zM3 13l9 5 9-5',
  'arrow-left': 'M19 12H5M11 6l-6 6 6 6',
  'chevron-right': 'M9 6l6 6-6 6',
  lock: 'M6 11h12v9H6zM8 11V8a4 4 0 118 0v3',
  stack: 'M12 3l9 5-9 5-9-5 9-5zM3 12l9 5 9-5M3 16l9 5 9-5',
  filter: 'M3 5h18l-7 8v6l-4-2v-4L3 5z',
  folder: 'M3 6h6l2 2h10v11H3V6z',
  'chevrons-left': 'M11 17l-5-5 5-5M18 17l-5-5 5-5',
  'chevrons-right': 'M13 17l5-5-5-5M6 17l5-5-5-5',
  'panel-left': 'M4 5h16v14H4zM9 5v14',
  toggle: 'M7 8h10a4 4 0 010 8H7A4 4 0 017 8zM7 14a2 2 0 100-4 2 2 0 000 4z',
  card: 'M3 6h18v12H3zM3 10h18M7 15h4',
  bell: 'M6 8a6 6 0 0112 0c0 7 3 9 3 9H3s3-2 3-9zM10 21a2 2 0 004 0',
  mail: 'M3 6h18v12H3zM3 7l9 6 9-6',
  send: 'M22 2L11 13M22 2l-7 20-4-9-9-4 20-7z',
  link: 'M10 14a4 4 0 005.7 0l3-3a4 4 0 00-5.7-5.7l-1 1M14 10a4 4 0 00-5.7 0l-3 3a4 4 0 005.7 5.7l1-1',
  message: 'M4 5h16v11H9l-5 4V5z',
  pulse: 'M3 12h4l3-8 4 16 3-8h4',
  repeat: 'M17 2l4 4-4 4M3 11V9a3 3 0 013-3h15M7 22l-4-4 4-4M21 13v2a3 3 0 01-3 3H3',
  cart: 'M3 4h2l2 12h11l2-8H6M9 20h.01M17 20h.01',
  minus: 'M5 12h14',
  'file-text': 'M7 3h7l5 5v13H7zM14 3v5h5M9 13h6M9 17h6',
  'user-check': 'M16 21v-2a4 4 0 00-4-4H6a4 4 0 00-4 4v2M9 11a4 4 0 100-8 4 4 0 000 8zM17 11l2 2 4-4',
};

/** Every icon name that can be typed into a menu entry. */
export const ICON_NAMES = Object.keys(PATHS).sort();

@Component({
  selector: 'app-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"
                  class="shrink-0" [class]="size()" aria-hidden="true"><path [attr.d]="d()" /></svg>`,
})
export class IconComponent {
  readonly name = input.required<string>();
  readonly size = input('h-5 w-5');
  readonly d = computed(() => PATHS[this.name()] ?? PATHS['circle']);
}

const OK = ['Submitted', 'Fiscalised', 'Processed', 'FiscalDayClosed', 'Online', 'ACTIVE', 'Active', 'Delivered', 'FileProcessingIsSuccessful'];
const WARN = ['Pending', 'Held', 'OfflineIssued', 'Uploaded', 'FiscalDayOpened', 'Offline', 'FiscalDayCloseInitiated', 'Preview'];
const BAD = ['Rejected', 'Cancelled', 'ProcessedWithErrors', 'FiscalDayCloseFailed', 'Failed', 'INACTIVE', 'Revoked', 'Suspended', 'Expired'];

/** Coloured status pill: green = done, amber = waiting, red = problem. */
@Component({
  selector: 'app-status',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [class.badge-ok]="tone() === 'ok'" [class.badge-warn]="tone() === 'warn'" [class.badge-bad]="tone() === 'bad'">{{ label() }}</span>`,
})
export class StatusComponent {
  readonly value = input.required<string>();
  readonly tone = computed(() => (OK.includes(this.value()) ? 'ok' : WARN.includes(this.value()) ? 'warn' : BAD.includes(this.value()) ? 'bad' : ''));
  readonly label = computed(() => this.value().replace(/^FiscalDay/, '').replace(/([a-z])([A-Z])/g, '$1 $2'));
}

@Component({
  selector: 'app-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="mb-5 flex flex-wrap items-end justify-between gap-3">
      <div>
        <h1 class="text-xl font-semibold tracking-tight">{{ title() }}</h1>
        @if (subtitle()) { <p class="mt-0.5 text-sm text-slate-500 dark:text-slate-400">{{ subtitle() }}</p> }
      </div>
      <div class="flex flex-wrap items-center gap-2"><ng-content /></div>
    </div>`,
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly subtitle = input('');
}
