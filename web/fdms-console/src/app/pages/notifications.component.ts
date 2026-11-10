import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { AppNotification } from '../core/models';
import { NotificationsStore } from '../core/notifications.store';
import { EmptyComponent } from '../shared/list.components';
import { noteIcon, noteTone } from '../shared/notification-bell.component';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';

const KIND_LABEL: Record<string, string> = {
  application: 'Registration', payment: 'Payment', golive: 'Go-live', backup: 'Backup', billing: 'Billing', approval: 'Approval',
  certificate: 'Certificate', dayclose: 'Fiscal day', daylong: 'Fiscal day', rejected: 'Receipt', queued: 'Receipts', usage: 'Plan', recurring: 'Recurring', email: 'Email',
};

/** Every notification, newest first: for a company its alerts, invoices and approvals; for the platform new registrations, payments to confirm, go-live requests and backups. */
@Component({
  selector: 'app-notifications',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, IconComponent, PageHeaderComponent, EmptyComponent],
  template: `
    <app-page-header title="Notifications" [subtitle]="store.platform() ? 'New registrations, payments to confirm, go-live requests and backups. Also emailed to the addresses under Settings, Email.' : 'Invoices to pay, approvals waiting for you, and warnings about your devices.'">
      @if (store.platform() && auth.can('settings.manage')) { <a routerLink="/admin/settings" class="btn-ghost"><app-icon name="mail" size="h-4 w-4" />Email settings</a> }
      @if (!store.platform() && auth.can('alerts.manage')) { <a routerLink="/alerts" class="btn-ghost"><app-icon name="mail" size="h-4 w-4" />Who is emailed</a> }
      <button class="btn" (click)="store.markRead()" [disabled]="!store.unread()">Mark all read</button>
    </app-page-header>

    <div class="mb-4 flex flex-wrap gap-2" role="tablist">
      @for (f of filters(); track f.key) {
        <button role="tab" class="rounded-full px-3.5 py-1.5 text-sm font-medium ring-1 transition" [attr.aria-selected]="filter() === f.key"
          [class]="filter() === f.key ? 'bg-brand-600 text-white ring-brand-600' : 'bg-white text-slate-600 ring-slate-200 hover:bg-slate-50 dark:bg-slate-900 dark:text-slate-300 dark:ring-slate-700'"
          (click)="filter.set(f.key)">{{ f.label }} <span class="ml-1 opacity-70">{{ f.count }}</span></button>
      }
    </div>

    <div class="card p-0!">
      <ul class="divide-y divide-slate-100 dark:divide-slate-800">
        @for (n of shown(); track n.id) {
          <li class="flex gap-4 px-5 py-4" [class.bg-brand-50]="!n.read">
            <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full ring-1" [class]="tone(n)"><app-icon [name]="icon(n)" size="h-5 w-5" /></span>
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2">
                <span class="text-sm" [class.font-semibold]="!n.read">{{ n.title }}</span>
                <span class="badge">{{ kind(n.kind) }}</span>
                @if (!n.read) { <span class="badge badge-warn">New</span> }
              </div>
              @if (n.body) { <p class="mt-1 text-sm text-slate-600 dark:text-slate-400">{{ n.body }}</p> }
              <div class="mt-2 flex flex-wrap items-center gap-3 text-xs text-slate-500">
                <span>{{ n.createdAt | date:'EEE d MMM y, HH:mm' }}</span>
                @if (n.link) { <a [routerLink]="n.link" (click)="read(n)" class="font-medium text-brand-600 underline">Open</a> }
                @if (!n.read) { <button class="font-medium text-slate-600 underline dark:text-slate-300" (click)="read(n)">Mark read</button> }
              </div>
            </div>
          </li>
        } @empty { <li><app-empty icon="bell" title="Nothing here" hint="New notifications appear here and in the bell at the top." /></li> }
      </ul>
    </div>
  `,
})
export class NotificationsComponent implements OnInit {
  readonly store = inject(NotificationsStore);
  readonly auth = inject(AuthService);
  readonly filter = signal<string>('all');
  readonly icon = noteIcon;
  readonly tone = noteTone;
  private readonly destroy = inject(DestroyRef);

  private readonly items = computed(() => this.store.data()?.items ?? []);
  readonly filters = computed(() => {
    const items = this.items();
    const kinds = [...new Set(items.map(n => n.kind))];
    return [
      { key: 'all', label: 'All', count: items.length },
      { key: 'unread', label: 'Unread', count: items.filter(n => !n.read).length },
      ...kinds.map(k => ({ key: k, label: this.kind(k), count: items.filter(n => n.kind === k).length })),
    ];
  });
  readonly shown = computed(() => {
    const f = this.filter();
    return this.items().filter(n => f === 'all' || (f === 'unread' ? !n.read : n.kind === f));
  });

  ngOnInit(): void { this.destroy.onDestroy(this.store.attach()); void this.store.load(200); }

  kind(k: string): string { return KIND_LABEL[k] ?? k; }
  read(n: AppNotification): void { if (!n.read) void this.store.markRead([n.id]); }
}
