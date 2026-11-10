import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AppNotification } from '../core/models';
import { NotificationsStore } from '../core/notifications.store';
import { noteIcon, noteTone } from './notification-bell.component';
import { IconComponent } from './ui.components';

/** The dashboard's "what needs you" panel: the newest unread notifications (falls back to the latest ones), each one click away from the page that deals with it. */
@Component({
  selector: 'app-notifications-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, IconComponent],
  template: `
    @if (store.visible() && (store.data()?.items?.length ?? 0) > 0) {
      <section class="rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900" [attr.aria-label]="title()">
        <div class="mb-3 flex items-center justify-between gap-3">
          <h2 class="flex items-center gap-2 text-sm font-semibold uppercase tracking-wide text-slate-500">
            <app-icon name="bell" size="h-4 w-4" />{{ title() }}
            @if (store.unread()) { <span class="rounded-full bg-red-600 px-2 py-0.5 text-[11px] font-semibold normal-case text-white">{{ store.unread() }} new</span> }
          </h2>
          <a routerLink="/notifications" class="text-sm font-medium text-brand-600">See all</a>
        </div>
        <ul class="divide-y divide-slate-100 dark:divide-slate-800">
          @for (n of items(); track n.id) {
            <li class="flex items-start gap-3 py-2.5">
              <span class="flex h-8 w-8 shrink-0 items-center justify-center rounded-full ring-1" [class]="tone(n)"><app-icon [name]="icon(n)" size="h-4 w-4" /></span>
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm" [class.font-semibold]="!n.read">{{ n.title }}</div>
                @if (n.body) { <div class="line-clamp-1 text-xs text-slate-500">{{ n.body }}</div> }
              </div>
              <span class="hidden shrink-0 text-xs text-slate-400 sm:block">{{ n.createdAt | date:'d MMM HH:mm' }}</span>
              @if (n.link) { <a [routerLink]="n.link" (click)="read(n)" class="shrink-0 rounded-lg px-2.5 py-1 text-sm font-medium text-brand-600 ring-1 ring-brand-200 hover:bg-brand-50 dark:ring-brand-800">{{ action(n) }}</a> }
            </li>
          } @empty { <li class="py-6 text-center text-sm text-slate-500">You are all caught up.</li> }
        </ul>
      </section>
    }
  `,
})
export class NotificationsPanelComponent implements OnInit {
  readonly title = input('Notifications');
  readonly take = input(6);
  readonly store = inject(NotificationsStore);
  readonly icon = noteIcon;
  readonly tone = noteTone;
  private readonly destroy = inject(DestroyRef);
  readonly items = computed(() => {
    const all = this.store.data()?.items ?? [];
    const unread = all.filter(n => !n.read);
    return (unread.length ? unread : all).slice(0, this.take());
  });

  ngOnInit(): void { this.destroy.onDestroy(this.store.attach()); }

  action(n: AppNotification): string {
    switch (n.kind) {
      case 'application': case 'golive': return 'Review';
      case 'payment': return n.link === '/admin/payments' ? 'Confirm' : 'Open';
      case 'billing': return n.title.includes('ready to pay') ? 'Pay now' : 'Open';
      case 'approval': return 'Decide';
      default: return 'Open';
    }
  }
  read(n: AppNotification): void { if (!n.read) void this.store.markRead([n.id]); }
}
