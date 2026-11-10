import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { createPasskey, passkeysSupported } from '../core/webauthn';
import { ToastService } from '../core/ui.services';
import { ModalComponent } from '../shared/modal.components';
import { IconComponent } from '../shared/ui.components';

interface Passkey { id: string; name: string; createdAt: string; lastUsedAt?: string | null }
interface Status { mfaEnabled: boolean; recoveryCodesLeft: number; passkeys: Passkey[] }
interface Setup { secret: string; uri: string; qrImage: string }

/** "Extra protection" on My profile: an authenticator app (two-step sign-in) and passkeys. */
@Component({
  selector: 'app-security-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, ModalComponent],
  template: `
    @if (status(); as s) {
      <section class="card">
        <h2 class="text-base font-semibold">Extra protection</h2>
        <p class="text-sm text-slate-500">Two ways to keep your account safe even if someone learns your password.</p>

        <!-- authenticator app -->
        <div class="mt-4 flex flex-wrap items-center gap-3 rounded-xl border border-slate-200 p-4 dark:border-slate-700">
          <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg" [class]="s.mfaEnabled ? 'bg-emerald-50 text-emerald-600 dark:bg-emerald-500/15' : 'bg-slate-100 text-slate-500 dark:bg-slate-800'"><app-icon name="shield" size="h-5 w-5" /></span>
          <div class="min-w-0 flex-1">
            <div class="text-sm font-medium">Two-step sign-in with an authenticator app @if (s.mfaEnabled) { <span class="badge badge-ok ml-1">On</span> } @else { <span class="badge badge-warn ml-1">Off</span> }</div>
            <div class="text-xs text-slate-500">{{ s.mfaEnabled ? 'After your password you type a 6-digit code from your phone. ' + s.recoveryCodesLeft + ' recovery code(s) left.' : 'Google Authenticator, Microsoft Authenticator, Authy and similar apps work.' }}</div>
          </div>
          @if (s.mfaEnabled) {
            <button type="button" class="btn-ghost btn-sm" (click)="askPassword('New recovery codes', 'Making new codes cancels the old ones.', 'codes')">New recovery codes</button>
            <button type="button" class="btn-danger btn-sm" (click)="askPassword('Turn off two-step sign-in', 'Enter your password to turn it off.', 'off')">Turn off</button>
          } @else { <button type="button" class="btn btn-sm" (click)="startSetup()">Turn on</button> }
        </div>

        <!-- passkeys -->
        <div class="mt-4 rounded-xl border border-slate-200 p-4 dark:border-slate-700">
          <div class="flex flex-wrap items-center gap-3">
            <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-slate-500 dark:bg-slate-800"><app-icon name="key" size="h-5 w-5" /></span>
            <div class="min-w-0 flex-1"><div class="text-sm font-medium">Passkeys</div><div class="text-xs text-slate-500">Sign in with your fingerprint, face, screen lock or a security key instead of typing a password.</div></div>
            @if (supported) { <button type="button" class="btn btn-sm" [disabled]="busy()" (click)="addPasskey()"><app-icon name="plus" size="h-4 w-4" />Add a passkey</button> }
          </div>
          @if (!supported) { <p class="mt-3 text-xs text-amber-600">This browser cannot make passkeys. Use a current version of Chrome, Edge, Safari or Firefox over a secure (https) connection.</p> }
          <ul class="mt-3 divide-y divide-slate-100 dark:divide-slate-800">
            @for (p of s.passkeys; track p.id) {
              <li class="flex flex-wrap items-center gap-3 py-2.5">
                <div class="min-w-0 flex-1"><div class="truncate text-sm font-medium">{{ p.name }}</div><div class="text-xs text-slate-500">Added {{ p.createdAt | date: 'd MMM y' }} · {{ p.lastUsedAt ? 'last used ' + (p.lastUsedAt | date: 'd MMM y, HH:mm') : 'not used yet' }}</div></div>
                <button type="button" class="btn-ghost btn-sm" (click)="askPassword('Remove passkey', 'Enter your password to remove ' + p.name + '.', 'remove', p)">Remove</button>
              </li>
            } @empty { <li class="py-2.5 text-sm text-slate-500">No passkeys yet.</li> }
          </ul>
        </div>
      </section>
    }

    @if (setup(); as st) {
      <app-modal title="Turn on two-step sign-in" width="max-w-md" (closed)="setup.set(null)">
        <ol class="space-y-3 text-sm">
          <li><b>1.</b> Open your authenticator app and add an account by scanning this picture.<img [src]="st.qrImage" alt="QR code for your authenticator app" class="mx-auto mt-2 h-44 w-44 rounded-lg border border-slate-200 bg-white p-1" />
            <div class="mt-1 text-center text-xs text-slate-500">Cannot scan? Type this key in instead:<br /><span class="select-all font-mono text-sm text-slate-800 dark:text-slate-200">{{ st.secret }}</span></div></li>
          <li><b>2.</b> Type the 6-digit code the app shows now.
            <input class="input mt-2 text-center font-mono text-lg tracking-widest" [(ngModel)]="code" maxlength="7" inputmode="numeric" autocomplete="one-time-code" placeholder="000000" (keyup.enter)="confirmSetup()" /></li>
        </ol>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
        <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="setup.set(null)">Cancel</button><button class="btn" type="button" [disabled]="busy() || code.trim().length < 6" (click)="confirmSetup()">Turn on</button></div>
      </app-modal>
    }

    @if (codes(); as c) {
      <app-modal title="Your recovery codes" subtitle="Keep these somewhere safe. Each works once if you lose your phone." width="max-w-md" (closed)="codes.set(null)">
        <div class="grid grid-cols-2 gap-2 rounded-xl bg-slate-50 p-4 font-mono text-sm dark:bg-slate-800/50">@for (x of c; track x) { <span class="select-all text-center">{{ x }}</span> }</div>
        <p class="mt-3 text-xs text-slate-500">They will not be shown again.</p>
        <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="copyCodes(c)">Copy</button><button class="btn" type="button" (click)="codes.set(null)">I have saved them</button></div>
      </app-modal>
    }

    @if (pw(); as a) {
      <app-modal [title]="a.title" [subtitle]="a.text" width="max-w-sm" (closed)="pw.set(null)">
        <form (ngSubmit)="runPassword()">
          <label class="block"><span class="label">Your password</span><input class="input" type="password" name="pw" [(ngModel)]="password" autocomplete="current-password" autofocus required /></label>
          @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="pw.set(null)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !password">Continue</button></div>
        </form>
      </app-modal>
    }
  `,
})
export class SecurityCardComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);
  readonly supported = passkeysSupported();
  readonly status = signal<Status | null>(null);
  readonly setup = signal<Setup | null>(null);
  readonly codes = signal<string[] | null>(null);
  readonly pw = signal<{ title: string; text: string; kind: 'off' | 'codes' | 'remove'; passkey?: Passkey } | null>(null);
  readonly busy = signal(false);
  readonly error = signal('');
  code = '';
  password = '';

  ngOnInit(): Promise<void> { return this.load(); }

  private async load(): Promise<void> {
    try { this.status.set(await this.api.get<Status>('/api/v1/security')); } catch (e) { this.toast.err(e); }
  }

  private msg(e: unknown): string { return errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, ''); }

  async startSetup(): Promise<void> {
    this.error.set(''); this.code = '';
    try { this.setup.set(await this.api.post<Setup>('/api/v1/security/mfa/setup', {})); } catch (e) { this.toast.err(e); }
  }

  async confirmSetup(): Promise<void> {
    if (this.busy() || this.code.trim().length < 6) return;
    this.busy.set(true); this.error.set('');
    try {
      const r = await this.api.post<{ recoveryCodes: string[] }>('/api/v1/security/mfa/enable', { code: this.code.trim() });
      this.setup.set(null); this.codes.set(r.recoveryCodes); await this.load();
      if (this.auth.mfaSetupRequired()) await this.auth.refresh();   // the company's rule is now met: the rest of the system opens
    } catch (e) { this.error.set(this.msg(e)); } finally { this.busy.set(false); }
  }

  askPassword(title: string, text: string, kind: 'off' | 'codes' | 'remove', passkey?: Passkey): void {
    this.error.set(''); this.password = ''; this.pw.set({ title, text, kind, passkey });
  }

  async runPassword(): Promise<void> {
    const a = this.pw(); if (!a || this.busy()) return;
    this.busy.set(true); this.error.set('');
    try {
      if (a.kind === 'off') { await this.api.post('/api/v1/security/mfa/disable', { password: this.password }); this.toast.ok('Two-step sign-in is off.'); }
      else if (a.kind === 'codes') { this.codes.set((await this.api.post<{ recoveryCodes: string[] }>('/api/v1/security/mfa/recovery-codes', { password: this.password })).recoveryCodes); }
      else { await this.api.post(`/api/v1/security/passkeys/${a.passkey!.id}/remove`, { password: this.password }); this.toast.ok('Passkey removed.'); }
      this.pw.set(null); await this.load();
    } catch (e) { this.error.set(this.msg(e)); } finally { this.busy.set(false); }
  }

  async addPasskey(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true);
    try {
      const begin = await this.api.post<{ sessionId: string; options: Record<string, any> }>('/api/v1/security/passkeys/options', {});
      const credential = await createPasskey(begin.options);
      const name = (navigator.userAgent.match(/Windows|Android|iPhone|iPad|Mac OS X|Linux/)?.[0] ?? 'This device').replace('Mac OS X', 'Mac') + ' passkey';
      await this.api.post(`/api/v1/security/passkeys/${begin.sessionId}`, { name, credential });
      await this.load();
      if (this.auth.mfaSetupRequired()) { this.toast.ok('Passkey added. Sign in again with it to continue.'); await this.auth.logout(); return; }
      this.toast.ok('Passkey added. You can now sign in with it.');
    } catch (e) {
      if ((e as { name?: string })?.name === 'NotAllowedError') return;   // the person closed the prompt
      this.toast.err(e);
    } finally { this.busy.set(false); }
  }

  async copyCodes(c: string[]): Promise<void> {
    try { await navigator.clipboard.writeText(c.join('\n')); this.toast.ok('Copied.'); } catch { this.toast.err('Could not copy. Select the codes and copy them by hand.'); }
  }
}
