import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { AlertSettings, NotificationList, OutboxItem } from '../core/models';
import { ToastService } from '../core/ui.services';
import { EmptyComponent } from '../shared/list.components';
import { PageHeaderComponent } from '../shared/ui.components';

/** Everything the system wants the company to know, and who gets told by email about what. */
@Component({
  selector: 'app-alerts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, PageHeaderComponent, EmptyComponent],
  template: `
    <app-page-header title="Alerts" subtitle="Warnings about your devices, fiscal days and plan. Each one is raised once, shown here and emailed.">
      <button class="btn-ghost" (click)="readAll()" [disabled]="!(list()?.unread)">Mark all read</button>
    </app-page-header>

    @if (outbox().length) {
      <div class="card mb-6">
        <div class="card-title">Emails waiting to be delivered</div>
        <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">When the mail server says “try later”, the email waits here and is tried again by itself (after 2 min, 10 min, 30 min, 1 h, and so on). You are alerted if it is finally given up on.</p>
        <ul class="divide-y divide-slate-100 dark:divide-slate-800">
          @for (m of outbox(); track m.id) {
            <li class="flex flex-wrap items-center gap-3 py-2.5">
              <span class="badge" [class]="m.status === 'Sent' ? 'badge-ok' : m.status === 'Failed' ? 'badge-bad' : 'badge-warn'">{{ m.status === 'Pending' ? 'Waiting' : m.status }}</span>
              <div class="min-w-0 flex-1"><div class="truncate text-sm font-medium">{{ m.subject }}</div>
                <div class="truncate text-xs text-slate-500">to {{ m.to }} · {{ m.attempts }} {{ m.attempts === 1 ? 'try' : 'tries' }}@if (m.status === 'Pending') { · next try {{ m.nextAttemptAt | date:'HH:mm' }} }@if (m.status === 'Sent' && m.sentAt) { · sent {{ m.sentAt | date:'d MMM HH:mm' }} }</div>
                @if (m.lastError && m.status !== 'Sent') { <div class="text-xs text-amber-700 dark:text-amber-300">{{ m.lastError }}</div> }</div>
              @if (m.status === 'Pending' || m.status === 'Failed') {
                <button class="btn-ghost btn-sm" (click)="retry(m)">Try now</button><button class="btn-ghost btn-sm" (click)="cancel(m)">Cancel</button>
              }
            </li>
          }
        </ul>
      </div>
    }

    <div class="grid gap-6 xl:grid-cols-5">
      <div class="card xl:col-span-3">
        <div class="card-title">Recent alerts</div>
        <ul class="divide-y divide-slate-100 dark:divide-slate-800">
          @for (n of list()?.items ?? []; track n.id) {
            <li class="flex gap-3 py-3">
              <span class="mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full" [class]="n.severity === 'bad' ? 'bg-red-500' : n.severity === 'warn' ? 'bg-amber-500' : 'bg-sky-500'"></span>
              <div class="min-w-0 flex-1">
                <div class="flex flex-wrap items-center gap-2"><span class="text-sm" [class.font-semibold]="!n.read">{{ n.title }}</span><span class="badge">{{ kindLabel(n.kind) }}</span></div>
                @if (n.body) { <p class="mt-0.5 text-sm text-slate-600 dark:text-slate-400">{{ n.body }}</p> }
                <div class="mt-1 flex items-center gap-3 text-xs text-slate-500">{{ n.createdAt | date:'medium' }}@if (n.link) { <a [routerLink]="n.link" class="font-medium text-brand-600 underline">Fix it</a> }</div>
              </div>
            </li>
          } @empty { <li><app-empty icon="shield" title="No alerts" hint="When something needs attention it appears here." /></li> }
        </ul>
      </div>

      @if (settings(); as s) {
        <form class="card xl:col-span-2" (ngSubmit)="save()">
          <div class="card-title">Who is emailed, and about what</div>
          @if (!s.emailConfigured) { <p class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-200">Email is not set up on this server yet, so alerts only appear here.</p> }
          <label class="block"><span class="label">Email addresses (up to 5, separated by commas)</span>
            <input class="input" name="emails" [(ngModel)]="emails" placeholder="ops@yourcompany.co.zw, boss@yourcompany.co.zw" /></label>
          <p class="hint">{{ s.fallbackNote }}</p>
          <div class="mt-4 space-y-2">
            @for (k of s.kinds; track k.id) {
              <label class="flex items-start gap-3 rounded-xl border border-slate-200 p-3 text-sm dark:border-slate-700">
                <input type="checkbox" class="mt-1 h-4 w-4" [name]="'k' + k.id" [checked]="enabled().has(k.id)" (change)="toggle(k.id)" />
                <span><span class="block font-medium">{{ k.label }}</span><span class="block text-slate-500">{{ k.help }}</span></span>
              </label>
            }
          </div>
          <div class="mt-5 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Save' }}</button></div>
        </form>
      }
    </div>
  `,
})
export class AlertsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly list = signal<NotificationList | null>(null);
  readonly settings = signal<AlertSettings | null>(null);
  readonly enabled = signal<Set<string>>(new Set());
  readonly busy = signal(false);
  readonly outbox = signal<OutboxItem[]>([]);
  emails = '';

  async ngOnInit(): Promise<void> {
    try {
      this.list.set(await this.api.get<NotificationList>('/api/v1/notifications', { take: 100 }));
      const s = await this.api.get<AlertSettings>('/api/v1/notifications/settings');
      this.settings.set(s); this.emails = s.emails ?? ''; this.enabled.set(new Set(s.enabledKinds));
    } catch (e) { this.toast.err(e); }
    await this.loadOutbox();
  }

  private async loadOutbox(): Promise<void> { try { this.outbox.set(await this.api.get<OutboxItem[]>('/api/v1/notifications/outbox')); } catch { /* the card is just not shown */ } }
  async retry(m: OutboxItem): Promise<void> { try { await this.api.post(`/api/v1/notifications/outbox/${m.id}/retry`); this.toast.ok('It will be tried again within a minute.'); await this.loadOutbox(); } catch (e) { this.toast.err(e); } }
  async cancel(m: OutboxItem): Promise<void> { try { await this.api.delete(`/api/v1/notifications/outbox/${m.id}`); await this.loadOutbox(); } catch (e) { this.toast.err(e); } }

  kindLabel(id: string): string { return this.settings()?.kinds.find(k => k.id === id)?.label ?? id; }
  toggle(id: string): void { const s = new Set(this.enabled()); if (!s.delete(id)) s.add(id); this.enabled.set(s); }

  async readAll(): Promise<void> {
    try { await this.api.post('/api/v1/notifications/read', {}); this.list.set(await this.api.get<NotificationList>('/api/v1/notifications', { take: 100 })); } catch (e) { this.toast.err(e); }
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try { const s = await this.api.put<AlertSettings>('/api/v1/notifications/settings', { emails: this.emails.trim() || null, enabledKinds: [...this.enabled()] }); this.settings.set(s); this.emails = s.emails ?? ''; this.toast.ok('Alert settings saved.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
