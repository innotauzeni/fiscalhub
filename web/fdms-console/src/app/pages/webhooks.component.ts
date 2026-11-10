import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { WebhookConfig, WebhookDelivery } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { PageHeaderComponent, StatusComponent } from '../shared/ui.components';

@Component({
  selector: 'app-webhooks',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, PageHeaderComponent, StatusComponent],
  template: `
    <app-page-header title="Webhooks" subtitle="We POST an event to your https URL when a receipt is fiscalised or rejected. Each call is signed with HMAC-SHA256." />

    @if (secret(); as s) {
      <div class="card mb-5 border-emerald-300 dark:border-emerald-700">
        <div class="card-title">New signing secret — copy it now, it is shown only once</div>
        <input class="input font-mono text-xs" readonly [value]="s" #sec (focus)="sec.select()" />
        <button class="btn-ghost btn-sm mt-3" (click)="secret.set(null)">I have saved it</button>
      </div>
    }

    <div class="card mb-5">
      <div class="card-title">Endpoint</div>
      <div class="flex flex-wrap items-end gap-3">
        <label class="block min-w-72 flex-1"><span class="label">https URL</span><input class="input" [(ngModel)]="url" placeholder="https://example.com/fdms-events" /></label>
        <button class="btn" (click)="save()" [disabled]="busy() || !url.trim()">Save (issues a new secret)</button>
        @if (cfg()?.url) { <button class="btn-danger" (click)="clear()">Remove</button> }
      </div>
      <p class="hint">Saving always creates a fresh secret, so rotating it is just saving again.</p>
    </div>

    <div class="card overflow-x-auto">
      <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0">Recent deliveries</div><button class="btn-ghost btn-sm" (click)="load()">Refresh</button></div>
      <table class="table">
        <thead><tr><th>When</th><th>Event</th><th>Status</th><th>Attempts</th><th>Last error</th></tr></thead>
        <tbody>
          @for (d of deliveries(); track d.id) {
            <tr><td class="whitespace-nowrap">{{ d.createdAt | date:'short' }}</td><td>{{ d.eventType }}</td><td><app-status [value]="d.status" /></td><td>{{ d.attempts }}</td><td class="text-sm text-red-600">{{ d.lastError }}</td></tr>
          } @empty { <tr><td colspan="5" class="text-slate-500">No deliveries yet.</td></tr> }
        </tbody>
      </table>
    </div>
  `,
})
export class WebhooksComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly cfg = signal<WebhookConfig | null>(null);
  readonly deliveries = signal<WebhookDelivery[]>([]);
  readonly secret = signal<string | null>(null);
  readonly busy = signal(false);
  url = '';

  ngOnInit(): Promise<void> { return this.load(); }

  async load(): Promise<void> {
    try {
      const c = await this.api.get<WebhookConfig>('/api/v1/webhook');
      this.cfg.set(c); this.url = c.url ?? this.url;
      this.deliveries.set(await this.api.get<WebhookDelivery[]>('/api/v1/webhook/deliveries', { take: 50 }));
    } catch (e) { this.toast.err(e); }
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const c = await this.api.put<WebhookConfig>('/api/v1/webhook', { url: this.url.trim() });
      this.cfg.set(c); this.secret.set(c.secret ?? null); this.toast.ok('Webhook saved.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async clear(): Promise<void> {
    if (!(await this.dialog.confirm('Stop sending webhook events?', 'Remove', true))) return;
    try { await this.api.delete('/api/v1/webhook'); this.cfg.set(null); this.url = ''; this.toast.ok('Webhook removed.'); } catch (e) { this.toast.err(e); }
  }
}
