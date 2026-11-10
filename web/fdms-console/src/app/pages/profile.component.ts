import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Profile, SessionInfo } from '../core/ai.models';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { AvatarService } from '../core/avatar.service';
import { DialogService, ToastService } from '../core/ui.services';
import { SecurityCardComponent } from './security.component';
import { LogoCropperComponent } from '../shared/logo-cropper.component';
import { IconComponent, PageHeaderComponent } from '../shared/ui.components';
import { WhatsAppLinkComponent } from './whatsapp-link.component';
import { SmsAlertsComponent } from './sms-alerts.component';

/** "My profile": who you are in the system, your picture, your password and where you are signed in. Everything here is about the signed-in person only. */
@Component({
  selector: 'app-profile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, DatePipe, IconComponent, PageHeaderComponent, LogoCropperComponent, SecurityCardComponent, WhatsAppLinkComponent, SmsAlertsComponent],
  template: `
    <app-page-header title="My profile" subtitle="Your details, picture, password and the devices you are signed in on." />

    @if (auth.mfaSetupRequired()) {
      <div class="mb-5 rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-500/40 dark:bg-amber-500/10 dark:text-amber-200" role="alert">
        <b>Your company requires extra protection.</b> Turn on two-step sign-in with an authenticator app below (or add a passkey and sign in with it) to continue to the rest of the system.
      </div>
    }

    @if (me(); as p) {
      <div class="grid gap-5 lg:grid-cols-3">
        <!-- identity -->
        <section class="card lg:col-span-1">
          <div class="flex flex-col items-center text-center">
            <div class="relative">
              @if (avatar.url(); as a) { <img [src]="a" alt="Your picture" class="h-28 w-28 rounded-full border-4 border-white object-cover shadow-md dark:border-slate-800" /> }
              @else { <span class="flex h-28 w-28 items-center justify-center rounded-full bg-linear-to-br from-blue-500 to-violet-500 text-3xl font-semibold text-white shadow-md">{{ initials() }}</span> }
              <button type="button" class="absolute -bottom-1 -right-1 flex h-9 w-9 items-center justify-center rounded-full border border-slate-200 bg-white text-slate-600 shadow hover:text-brand-600 dark:border-slate-700 dark:bg-slate-800" (click)="cropping.set(true)" aria-label="Change picture"><app-icon name="edit" size="h-4 w-4" /></button>
            </div>
            <h2 class="mt-4 text-lg font-semibold">{{ p.name }} {{ p.surname }}</h2>
            <p class="text-sm text-slate-500">{{ p.email }}</p>
            <div class="mt-2 flex flex-wrap justify-center gap-1.5">@for (r of p.roles; track r) { <span class="badge badge-info">{{ r }}</span> }</div>
            @if (p.hasAvatar) { <button type="button" class="btn-ghost btn-sm mt-3" (click)="removePicture()">Remove picture</button> }
          </div>
          <dl class="mt-5 space-y-2 border-t border-slate-100 pt-4 text-sm dark:border-slate-800">
            <div class="flex justify-between gap-3"><dt class="text-slate-500">Company</dt><dd class="text-right font-medium">{{ p.companyName ?? 'Platform operator' }}</dd></div>
            <div class="flex justify-between gap-3"><dt class="text-slate-500">Sign-in name</dt><dd class="truncate text-right">{{ p.userName }}</dd></div>
            <div class="flex justify-between gap-3"><dt class="text-slate-500">Last signed in</dt><dd class="text-right">{{ p.lastLoginDate ? (p.lastLoginDate | date: 'd MMM y, HH:mm') : 'This is your first visit' }}</dd></div>
            <div class="flex justify-between gap-3"><dt class="text-slate-500">Member since</dt><dd class="text-right">{{ p.createdAt | date: 'd MMM y' }}</dd></div>
          </dl>
        </section>

        <div class="space-y-5 lg:col-span-2">
          <!-- details -->
          <form class="card" (ngSubmit)="save()">
            <h2 class="text-base font-semibold">Your details</h2>
            <div class="mt-4 grid gap-4 sm:grid-cols-2">
              <label class="block"><span class="label">First name</span><input class="input" name="name" [(ngModel)]="name" maxlength="60" required autocomplete="given-name" /></label>
              <label class="block"><span class="label">Surname</span><input class="input" name="surname" [(ngModel)]="surname" maxlength="60" required autocomplete="family-name" /></label>
              <label class="block"><span class="label">Phone</span><input class="input" name="phone" [(ngModel)]="phone" maxlength="25" autocomplete="tel" placeholder="+263 77 123 4567" /></label>
            </div>
            @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
            <div class="mt-4 flex justify-end"><button class="btn" type="submit" [disabled]="busy() || !name.trim() || !surname.trim()">{{ busy() ? 'Saving…' : 'Save details' }}</button></div>
          </form>

          <!-- how you sign in -->
          <section class="card">
            <h2 class="text-base font-semibold">How you sign in</h2>
            <p class="text-sm text-slate-500">Sign in with your email or your user name, whichever you prefer.</p>
            <form class="mt-4 grid gap-3 sm:grid-cols-[1fr_auto] sm:items-end" (ngSubmit)="saveUserName()">
              <label class="block"><span class="label">User name</span>
                <input class="input" name="un" [(ngModel)]="userName" maxlength="32" autocomplete="username" placeholder="e.g. Inno" />
                <span class="hint">3 to 32 letters or digits (dots, dashes, underscores allowed). Leave empty to sign in with your email only.</span></label>
              <button class="btn" type="submit" [disabled]="busyName() || userName.trim() === shownUserName()">{{ busyName() ? 'Saving…' : 'Save user name' }}</button>
            </form>
            <div class="mt-5 border-t border-slate-100 pt-4 dark:border-slate-800">
              <div class="flex flex-wrap items-center justify-between gap-3">
                <div><div class="label mb-0">Email</div><div class="text-sm font-medium">{{ p.email }}</div></div>
                @if (emailStep() === 'idle') { <button type="button" class="btn-ghost" (click)="emailStep.set('ask'); newEmail = ''; emailPassword = ''; emailCode = ''"><app-icon name="edit" size="h-4 w-4" />Change email</button> }
              </div>
              @if (emailStep() === 'ask') {
                <form class="mt-3 grid gap-3 sm:grid-cols-2" (ngSubmit)="askEmail()">
                  <label class="block"><span class="label">New email</span><input class="input" name="ne" type="email" [(ngModel)]="newEmail" autocomplete="email" required /></label>
                  <label class="block"><span class="label">Your current password</span><input class="input" name="ep" type="password" [(ngModel)]="emailPassword" autocomplete="current-password" required /></label>
                  <div class="flex justify-end gap-2 sm:col-span-2"><button type="button" class="btn-ghost" (click)="emailStep.set('idle')">Cancel</button><button class="btn" type="submit" [disabled]="busyEmail() || !newEmail.trim() || !emailPassword">{{ busyEmail() ? 'Sending…' : 'Send code to the new email' }}</button></div>
                </form>
              }
              @if (emailStep() === 'code') {
                <form class="mt-3 space-y-3" (ngSubmit)="confirmEmail()">
                  <p class="text-sm text-slate-600 dark:text-slate-300">{{ emailNote() }}</p>
                  <input class="input max-w-xs py-2.5 text-center font-mono text-xl tracking-[0.4em]" name="ec" [(ngModel)]="emailCode" inputmode="numeric" maxlength="6" autocomplete="one-time-code" placeholder="000000" aria-label="Code from the email" />
                  <div class="flex gap-2"><button type="button" class="btn-ghost" (click)="emailStep.set('idle')">Cancel</button><button class="btn" type="submit" [disabled]="busyEmail() || emailCode.trim().length !== 6">{{ busyEmail() ? 'Checking…' : 'Confirm new email' }}</button></div>
                </form>
              }
            </div>
          </section>

          <!-- security -->
          <section class="card">
            <div class="flex flex-wrap items-center justify-between gap-3">
              <div><h2 class="text-base font-semibold">Password and sign-ins</h2><p class="text-sm text-slate-500">If you do not recognise a device, sign it out and change your password.</p></div>
              <a routerLink="/change-password" class="btn-ghost"><app-icon name="lock" size="h-4 w-4" />Change password</a>
            </div>
            <ul class="mt-4 divide-y divide-slate-100 dark:divide-slate-800">
              @for (s of sessions(); track s.id) {
                <li class="flex flex-wrap items-center gap-3 py-3">
                  <span class="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-slate-500 dark:bg-slate-800"><app-icon name="cpu" size="h-4 w-4" /></span>
                  <div class="min-w-0 flex-1">
                    <div class="text-sm font-medium">{{ s.browser || 'Unknown device' }} @if (s.current) { <span class="badge badge-ok ml-1">This device</span> }</div>
                    <div class="text-xs text-slate-500">{{ s.ipAddress || 'unknown address' }} · signed in {{ s.createdAt | date: 'd MMM, HH:mm' }}</div>
                  </div>
                  @if (!s.current) { <button type="button" class="btn-ghost btn-sm" (click)="signOutSession(s)">Sign out</button> }
                </li>
              } @empty { <li class="py-3 text-sm text-slate-500">No other sign-ins are recorded.</li> }
            </ul>
            @if (others() > 0) { <div class="mt-3 flex justify-end"><button type="button" class="btn-danger btn-sm" (click)="signOutOthers()">Sign out all other devices ({{ others() }})</button></div> }
          </section>

          <app-security-card />
          <app-whatsapp-link />
          <app-sms-alerts />

          <!-- access -->
          @if (p.companyName) {
            <section class="card">
              <h2 class="text-base font-semibold">What you can work with</h2>
              <p class="text-sm text-slate-500">{{ p.accessScope === 'restricted' ? 'Your administrator gave you access to these machines only.' : 'You can work with every fiscal machine of ' + p.companyName + '.' }}</p>
              <ul class="mt-3 grid gap-2 sm:grid-cols-2">
                @for (d of p.devices; track d.id) {
                  <li class="flex items-center gap-3 rounded-xl border border-slate-200 px-3 py-2 dark:border-slate-700"><app-icon name="cpu" size="h-4 w-4" />
                    <div class="min-w-0"><div class="truncate text-sm font-medium">{{ d.name || 'Device ' + d.zimraDeviceId }}</div><div class="truncate text-xs text-slate-500">ZIMRA {{ d.zimraDeviceId }}{{ d.branch ? ' · ' + d.branch : '' }}</div></div></li>
                } @empty { <li class="text-sm text-slate-500">No machines yet.</li> }
              </ul>
              <p class="mt-4 text-xs text-slate-500">Your role decides which menus you see: {{ p.roles.join(', ') }}. Ask your administrator if something is missing.</p>
            </section>
          }
        </div>
      </div>
    }

    @if (cropping()) { <app-logo-cropper title="Your picture" [square]="true" useLabel="Use this picture" [maxBytes]="90000" [maxWidth]="256" (done)="upload($event)" (closed)="cropping.set(false)" /> }
  `,
})
export class ProfileComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly http = inject(HttpClient);
  readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  readonly avatar = inject(AvatarService);

  readonly me = signal<Profile | null>(null);
  readonly sessions = signal<SessionInfo[]>([]);
  readonly cropping = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  name = ''; surname = ''; phone = '';
  userName = ''; newEmail = ''; emailPassword = ''; emailCode = '';
  readonly busyName = signal(false);
  readonly busyEmail = signal(false);
  readonly emailStep = signal<'idle' | 'ask' | 'code'>('idle');
  readonly emailNote = signal('');
  /** The user name as it shows here: empty when they simply sign in with the email. */
  shownUserName(): string { const p = this.me(); return !p || p.userName.toLowerCase() === p.email.toLowerCase() ? '' : p.userName; }

  others(): number { return this.sessions().filter(s => !s.current).length; }
  initials(): string { const p = this.me(); return p ? ((p.name[0] ?? '') + (p.surname[0] ?? '') || p.email.slice(0, 2)).toUpperCase() : '?'; }

  async ngOnInit(): Promise<void> { await Promise.all([this.load(), this.loadSessions()]); }

  private async load(): Promise<void> {
    try {
      const p = await this.api.get<Profile>('/api/v1/profile');
      this.me.set(p); this.name = p.name; this.surname = p.surname; this.phone = p.phone ?? ''; this.userName = this.shownUserName();
    } catch (e) { this.toast.err(e); }
  }

  private async loadSessions(): Promise<void> {
    let token = ''; try { token = sessionStorage.getItem('fdms.refresh') ?? ''; } catch { /* the current device cannot be marked */ }
    try { this.sessions.set(await firstValueFrom(this.http.get<SessionInfo[]>('/api/v1/profile/sessions', { headers: token ? { 'X-Refresh-Token': token } : {} }))); } catch (e) { this.toast.err(e); }
  }

  async save(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      const p = await this.api.put<Profile>('/api/v1/profile', { name: this.name.trim(), surname: this.surname.trim(), phone: this.phone.trim() });
      this.me.set(p); this.toast.ok('Profile saved.'); this.auth.updateName(p.name, p.surname);
    } catch (e) { this.error.set(this.msg(e)); } finally { this.busy.set(false); }
  }

  async upload(blob: Blob): Promise<void> {
    this.cropping.set(false);
    try {
      const fd = new FormData(); fd.append('file', blob, blob.type === 'image/jpeg' ? 'me.jpg' : 'me.png');
      await this.api.post('/api/v1/profile/avatar', fd);
      this.toast.ok('Picture saved.'); await this.avatar.refresh(); await this.load();
    } catch (e) { this.toast.err(e); }
  }

  async removePicture(): Promise<void> {
    try { await this.api.delete('/api/v1/profile/avatar'); this.avatar.clear(); await this.load(); this.toast.ok('Picture removed.'); } catch (e) { this.toast.err(e); }
  }

  async signOutSession(s: SessionInfo): Promise<void> {
    if (!(await this.dialog.confirm(`Sign out ${s.browser || 'that device'}?`, 'Sign out', true))) return;
    try { await this.api.delete(`/api/v1/profile/sessions/${s.id}`); await this.loadSessions(); this.toast.ok('Signed out.'); } catch (e) { this.toast.err(e); }
  }

  async signOutOthers(): Promise<void> {
    if (!(await this.dialog.confirm('Sign out every device except this one?', 'Sign them out', true))) return;
    let token: string | null = null; try { token = sessionStorage.getItem('fdms.refresh'); } catch { /* none */ }
    try { const r = await this.api.post<{ revoked: number }>('/api/v1/profile/sessions/revoke-others', { currentRefreshToken: token }); await this.loadSessions(); this.toast.ok(`${r.revoked} device(s) signed out.`); } catch (e) { this.toast.err(e); }
  }

  async saveUserName(): Promise<void> {
    this.busyName.set(true);
    try {
      const p = await this.api.put<Profile>('/api/v1/profile/username', { userName: this.userName.trim() });
      this.me.set(p); this.userName = this.shownUserName();
      this.toast.ok(this.userName ? `You can now sign in as ${this.userName}.` : 'You sign in with your email.');
    } catch (e) { this.toast.err(e); } finally { this.busyName.set(false); }
  }

  async askEmail(): Promise<void> {
    this.busyEmail.set(true);
    try {
      const r = await this.api.post<{ message: string }>('/api/v1/profile/email', { newEmail: this.newEmail.trim(), password: this.emailPassword });
      this.emailNote.set(r.message); this.emailPassword = ''; this.emailCode = ''; this.emailStep.set('code');
    } catch (e) { this.toast.err(e); } finally { this.busyEmail.set(false); }
  }

  async confirmEmail(): Promise<void> {
    this.busyEmail.set(true);
    try {
      const p = await this.api.post<Profile>('/api/v1/profile/email/confirm', { newEmail: this.newEmail.trim(), code: this.emailCode.trim() });
      this.me.set(p); this.userName = this.shownUserName(); this.emailStep.set('idle');
      this.toast.ok(`Your email is now ${p.email}.`);
    } catch (e) { this.toast.err(e); this.emailCode = ''; } finally { this.busyEmail.set(false); }
  }

  private msg(e: unknown): string { return (e as { error?: { title?: string } })?.error?.title ?? 'Could not save. Check the details and try again.'; }
}
