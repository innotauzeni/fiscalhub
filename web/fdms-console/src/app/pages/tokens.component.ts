import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../core/api.service';
import { DeviceStore } from '../core/device.store';
import { ApiToken, CreatedToken, Scope } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { DeviceSelectComponent } from '../shared/device-select.component';
import { ModalComponent } from '../shared/modal.components';
import { PageHeaderComponent, StatusComponent } from '../shared/ui.components';
import { apiOrigin } from '../core/env';

@Component({
  selector: 'app-tokens',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DeviceSelectComponent, ModalComponent, PageHeaderComponent, StatusComponent],
  template: `
    <app-page-header title="API tokens" subtitle="A token works for ONE device only. Give it to the system that should fiscalise through that device.">
      <button class="btn" (click)="creating.set(true)">New token</button>
    </app-page-header>

    @if (created(); as c) {
      <div class="card mb-5 border-emerald-300 dark:border-emerald-700">
        <div class="card-title">Token created — copy it now, it is shown only once</div>
        <div class="flex gap-2">
          <input class="input font-mono text-xs" readonly [value]="c.secret" #secret (focus)="secret.select()" />
          <button class="btn-ghost" (click)="copy(c.secret)">Copy</button>
        </div>
        <p class="hint">Use it on every call:</p>
        <pre class="mt-1 overflow-x-auto rounded-lg bg-slate-50 p-3 text-xs dark:bg-slate-950">curl {{ origin }}/api/v1/devices/{{ c.token.deviceId }}/day -H "Authorization: Bearer {{ c.secret }}"</pre>
        <button class="btn-ghost btn-sm mt-3" (click)="created.set(null)">I have saved it</button>
      </div>
    }

    @if (creating()) {
      <app-modal title="New device token" subtitle="It works for the chosen device only." (closed)="creating.set(false)">
      <form (ngSubmit)="create()">
        <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <app-device-select label="Device this token is for" />
          <label class="block"><span class="label">Name (which system uses it)</span><input class="input" name="name" [(ngModel)]="name" required placeholder="e.g. POS till 1" /></label>
          <label class="block"><span class="label">Expires (optional)</span><input type="date" class="input" name="exp" [(ngModel)]="expires" /></label>
        </div>
        <div class="label mt-4">What this token may do</div>
        <div class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
          @for (s of scopes(); track s.name) {
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" [checked]="picked().has(s.name)" (change)="toggle(s.name)" /> {{ s.displayName }}</label>
          }
        </div>
        <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="creating.set(false)">Cancel</button><button class="btn" type="submit" [disabled]="busy()">Create token</button></div>
      </form>
      </app-modal>
    }

    <div class="card overflow-x-auto">
      <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0">Tokens</div><app-device-select label="Filter by device" (changed)="load()" /></div>
      <table class="table">
        <thead><tr><th>Name</th><th>Device</th><th>Token</th><th>Scopes</th><th>Last used</th><th>Status</th><th></th></tr></thead>
        <tbody>
          @for (t of tokens(); track t.id) {
            <tr>
              <td class="font-medium">{{ t.name }}<div class="hint mt-0">by {{ t.createdBy ?? '—' }} · {{ t.createdAt | date:'mediumDate' }}</div></td>
              <td>{{ t.zimraDeviceId }}</td>
              <td class="font-mono text-xs">{{ t.prefix }}…</td>
              <td class="max-w-xs text-xs">{{ t.scopes.join(', ') }}</td>
              <td class="whitespace-nowrap">{{ t.lastUsedAt ? (t.lastUsedAt | date:'short') : 'never' }}</td>
              <td><app-status [value]="statusOf(t)" />@if (t.expiresAt) { <div class="hint">expires {{ t.expiresAt | date:'mediumDate' }}</div> }</td>
              <td class="text-right">@if (!t.revokedAt) { <button class="btn-danger btn-sm" (click)="revoke(t)">Revoke</button> }</td>
            </tr>
          } @empty { <tr><td colspan="7" class="text-slate-500">No tokens for this device yet.</td></tr> }
        </tbody>
      </table>
    </div>
  `,
})
export class TokensComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly store = inject(DeviceStore);

  readonly origin = apiOrigin();
  readonly tokens = signal<ApiToken[]>([]);
  readonly scopes = signal<Scope[]>([]);
  readonly picked = signal<Set<string>>(new Set(['receipts.submit', 'receipts.view']));
  readonly creating = signal(false);
  readonly busy = signal(false);
  readonly created = signal<CreatedToken | null>(null);
  name = ''; expires = '';

  async ngOnInit(): Promise<void> {
    try { await this.store.load(); this.scopes.set(await this.api.get<Scope[]>('/api/v1/tokens/scopes')); } catch (e) { this.toast.err(e); }
    await this.load();
  }

  statusOf(t: ApiToken): string {
    if (t.revokedAt) return 'Revoked';
    if (t.expiresAt && new Date(t.expiresAt) < new Date()) return 'Expired';
    return 'Active';
  }

  toggle(name: string): void {
    this.picked.update(s => { const n = new Set(s); if (n.has(name)) n.delete(name); else n.add(name); return n; });
  }

  async load(): Promise<void> {
    try { this.tokens.set(await this.api.get<ApiToken[]>('/api/v1/tokens', { deviceId: this.store.selected()?.id })); } catch (e) { this.toast.err(e); }
  }

  async create(): Promise<void> {
    const d = this.store.selected();
    if (!d) { this.toast.err(new Error('Choose a device first.')); return; }
    if (!this.picked().size) { this.toast.err(new Error('Tick at least one permission.')); return; }
    this.busy.set(true);
    try {
      this.created.set(await this.api.post<CreatedToken>(`/api/v1/devices/${d.id}/tokens`, {
        name: this.name.trim(), scopes: [...this.picked()], expiresAt: this.expires ? new Date(this.expires + 'T23:59:59').toISOString() : null,
      }));
      this.name = ''; this.expires = ''; this.creating.set(false);
      await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async copy(text: string): Promise<void> {
    try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { this.toast.err(new Error('Copy failed - select the text and copy it manually.')); }
  }

  async revoke(t: ApiToken): Promise<void> {
    if (!(await this.dialog.confirm(`Revoke "${t.name}"? Any system using it stops working immediately.`, 'Revoke', true))) return;
    try { await this.api.delete(`/api/v1/tokens/${t.id}`); this.toast.ok('Token revoked.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
