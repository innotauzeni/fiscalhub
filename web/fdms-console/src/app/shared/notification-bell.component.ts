import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AppNotification } from '../core/models';
import { NotificationsStore } from '../core/notifications.store';
import { IconComponent } from './ui.components';

/** Icon and colour of a notification, by kind and severity (shared with the dashboard panels and the notifications page). */
export function noteIcon(n: AppNotification): string {
  switch (n.kind) {
    case 'application': return 'building';
    case 'payment': case 'billing': return 'card';
    case 'golive': return 'bolt';
    case 'approval': return 'check';
    case 'backup': return 'stack';
    case 'demo': return 'trash';
    case 'certificate': return 'shield';
    case 'email': return 'mail';
    default: return 'bell';
  }
}
export function noteTone(n: AppNotification): string {
  return n.severity === 'bad' ? 'bg-red-50 text-red-600 ring-red-200 dark:bg-red-900/30 dark:text-red-300 dark:ring-red-800'
    : n.severity === 'warn' ? 'bg-amber-50 text-amber-600 ring-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:ring-amber-800'
    : 'bg-sky-50 text-sky-600 ring-sky-200 dark:bg-sky-900/30 dark:text-sky-300 dark:ring-sky-800';
}

/** The bell in the header: how many notifications are unread, the latest few, and a link to all of them. Checks quietly once a minute. */
@Component({
  selector: 'app-notification-bell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, IconComponent],
  host: { '(document:keydown.escape)': 'open.set(false)' },
  template: `
    @if (store.visible()) {
      <div class="relative">
        <button class="icon-btn relative" (click)="toggle()" [attr.aria-label]="'Notifications' + (store.unread() ? ', ' + store.unread() + ' unread' : '')" [attr.aria-expanded]="open()">
          <app-icon name="bell" size="h-4 w-4" />
          @if (store.unread() > 0) { <span class="absolute -right-1 -top-1 flex h-4 min-w-4 animate-pulse items-center justify-center rounded-full bg-red-600 px-1 text-[10px] font-semibold text-white">{{ store.unread() > 9 ? '9+' : store.unread() }}</span> }
        </button>
        @if (open()) {
          <div class="fixed inset-0 z-40" (click)="open.set(false)"></div>
          <div class="absolute right-0 z-50 mt-2 w-96 max-w-[calc(100vw-2rem)] overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-xl dark:border-slate-700 dark:bg-slate-900" role="dialog" aria-label="Notifications">
            <div class="flex items-center justify-between border-b border-slate-100 px-4 py-3 dark:border-slate-800">
              <span class="text-sm font-semibold">Notifications @if (store.unread()) { <span class="ml-1 rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-700 dark:bg-red-900/40 dark:text-red-300">{{ store.unread() }} new</span> }</span>
              @if (store.unread() > 0) { <button class="text-xs font-medium text-brand-600" (click)="store.markRead()">Mark all read</button> }
            </div>
            <ul class="max-h-[26rem] divide-y divide-slate-100 overflow-y-auto dark:divide-slate-800">
              @for (n of latest(); track n.id) {
                <li>
                  <a [routerLink]="n.link || allLink()" (click)="openItem(n)" class="flex gap-3 px-4 py-3 hover:bg-slate-50 dark:hover:bg-slate-800/60" [class.bg-brand-50]="!n.read">
                    <span class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full ring-1" [class]="tone(n)"><app-icon [name]="icon(n)" size="h-4 w-4" /></span>
                    <span class="min-w-0 flex-1"><span class="block text-sm" [class.font-semibold]="!n.read">{{ n.title }}</span>
                      @if (n.body) { <span class="line-clamp-2 block text-xs text-slate-600 dark:text-slate-400">{{ n.body }}</span> }
                      <span class="block text-[11px] text-slate-400">{{ n.createdAt | date:'d MMM, HH:mm' }}</span></span>
                    @if (!n.read) { <span class="mt-1.5 h-2 w-2 shrink-0 rounded-full bg-brand-600"></span> }
                  </a>
                </li>
              } @empty { <li class="px-4 py-10 text-center text-sm text-slate-500">You are all caught up.</li> }
            </ul>
            <a [routerLink]="allLink()" (click)="open.set(false)" class="block border-t border-slate-100 px-4 py-2.5 text-center text-sm font-medium text-brand-600 dark:border-slate-800">See all notifications</a>
          </div>
        }
      </div>
    }
  `,
})
export class NotificationBellComponent implements OnInit {
  readonly store = inject(NotificationsStore);
  readonly open = signal(false);
  readonly latest = computed(() => (this.store.data()?.items ?? []).slice(0, 8));
  readonly icon = noteIcon;
  readonly tone = noteTone;
  private readonly destroy = inject(DestroyRef);

  ngOnInit(): void { this.destroy.onDestroy(this.store.attach()); }

  allLink(): string { return '/notifications'; }
  toggle(): void { this.open.set(!this.open()); if (this.open()) void this.store.load(); }
  openItem(n: AppNotification): void { this.open.set(false); if (!n.read) void this.store.markRead([n.id]); }
}
