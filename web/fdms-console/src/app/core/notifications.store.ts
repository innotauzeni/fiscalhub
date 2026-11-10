import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from './api.service';
import { AuthService } from './auth.service';
import { NotificationList } from './models';

/**
 * The bell's data, shared by the header bell, the dashboard panels and the notifications page so they always agree.
 * A company sees its own alerts (invoices to pay, approvals, devices ...); the platform's staff see the platform inbox (new registrations, payments to confirm, go-live requests, failed backups).
 */
@Injectable({ providedIn: 'root' })
export class NotificationsStore {
  private readonly api = inject(Api);
  private readonly auth = inject(AuthService);
  readonly data = signal<NotificationList | null>(null);
  readonly unread = computed(() => this.data()?.unread ?? 0);
  private timer: ReturnType<typeof setInterval> | undefined;
  private users = 0;

  /** Company people, and the platform staff who handle registrations or payments. Partners have no bell yet. */
  readonly visible = computed(() => {
    const u = this.auth.user();
    if (!u) return false;
    const may = this.auth.can('notifications.view') || this.auth.can('devices.view');
    return may && (!!u.clientId || this.platform());
  });
  readonly platform = computed(() => !this.auth.user()?.clientId && (this.auth.can('applications.manage') || this.auth.can('payments.manage') || this.auth.can('clients.manage')));

  /** Starts the once-a-minute check (shared: stops when the last user goes). */
  attach(): () => void {
    if (this.users++ === 0) {
      void this.load();
      this.timer = setInterval(() => { if (!document.hidden) void this.load(); }, 60_000);
    }
    return () => { if (--this.users === 0) clearInterval(this.timer); };
  }

  async load(take = 20): Promise<void> {
    if (!this.visible()) return;
    try { this.data.set(await this.api.get<NotificationList>('/api/v1/notifications', { take })); } catch { /* keep what we had */ }
  }

  async markRead(ids?: string[]): Promise<void> {
    try {
      await this.api.post('/api/v1/notifications/read', { ids: ids ?? null });
      const d = this.data();
      if (d) {
        const items = d.items.map(n => !ids || ids.includes(n.id) ? { ...n, read: true } : n);
        this.data.set({ items, unread: ids ? Math.max(0, d.unread - d.items.filter(n => !n.read && ids.includes(n.id)).length) : 0 });
      }
    } catch { /* ignore */ }
  }
}
