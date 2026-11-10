import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { DialogService, ToastService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';

interface PolicyUser { id: string; name: string; email: string; mfaEnabled: boolean; passkeys: number; compliant: boolean }
interface Policy { requireMfa: boolean; users: PolicyUser[] }

/** On the Users page: the company decides whether everyone must use two-step sign-in (or a passkey), and sees who has not yet. */
@Component({
  selector: 'app-security-policy',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent],
  template: `
    @if (policy(); as p) {
      <section class="mb-5 card">
        <div class="flex flex-wrap items-center gap-3">
          <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg" [class]="p.requireMfa ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/15' : 'bg-slate-100 text-slate-500 dark:bg-slate-800'"><app-icon name="shield" size="h-5 w-5" /></span>
          <div class="min-w-0 flex-1">
            <h2 class="text-sm font-semibold">Require extra protection for everyone @if (p.requireMfa) { <span class="badge badge-ok ml-1">On</span> } @else { <span class="badge badge-warn ml-1">Off</span> }</h2>
            <p class="text-xs text-slate-500">When on, everyone in the company must use an authenticator app or a passkey. Until they set it up, they can only open their own profile.</p>
          </div>
          <button type="button" class="btn btn-sm" [class.btn-danger]="p.requireMfa" [disabled]="busy()" (click)="toggle(p)">{{ p.requireMfa ? 'Turn off' : 'Turn on' }}</button>
        </div>
        @if (missing().length) {
          <div class="mt-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">
            <b>{{ missing().length }} of {{ p.users.length }} still to set up:</b> {{ names() }}
          </div>
        } @else if (p.users.length) { <p class="mt-3 text-xs text-emerald-600">Everyone already has extra protection.</p> }
      </section>
    }
  `,
})
export class SecurityPolicyComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly auth = inject(AuthService);
  readonly policy = signal<Policy | null>(null);
  readonly busy = signal(false);
  readonly missing = computed(() => (this.policy()?.users ?? []).filter(u => !u.compliant));
  readonly names = computed(() => this.missing().map(u => u.name || u.email).join(', '));

  async ngOnInit(): Promise<void> {
    if (!this.auth.user()?.clientId) return;   // the platform operator has no company
    try { this.policy.set(await this.api.get<Policy>('/api/v1/security-policy')); } catch (e) { this.toast.err(e); }
  }

  async toggle(p: Policy): Promise<void> {
    const turnOn = !p.requireMfa;
    const ok = turnOn
      ? await this.dialog.confirm('Require extra protection for everyone in the company? People who have not set it up are limited to their profile until they do.', 'Turn on')
      : await this.dialog.confirm('Make extra protection optional again?', 'Turn off', true);
    if (!ok) return;
    this.busy.set(true);
    try { this.policy.set(await this.api.put<Policy>('/api/v1/security-policy', { requireMfa: turnOn })); this.toast.ok(turnOn ? 'Extra protection is now required.' : 'Extra protection is optional again.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
