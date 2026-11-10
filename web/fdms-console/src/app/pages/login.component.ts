import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api.service';
import { PublicBrand } from '../core/models';
import { AuthService } from '../core/auth.service';
import { passkeysSupported } from '../core/webauthn';
import { ThemeService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';
import { apiUrl } from '../core/env';
import { StoreBadgesComponent } from '../shared/store-badges.component';

type Mode = 'login' | 'mfa' | 'forgot' | 'reset';

/**
 * Sign-in, two-step code, and the forgotten-password flow (6-digit code by email, then a new password), on a split screen: the brand panel on
 * the left (hidden on phones) and the form on the right. /login?company=acme shows that company's own name, colour and logo.
 */
@Component({
  selector: 'app-login',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, StoreBadgesComponent],
  template: `
    <div class="flex min-h-screen bg-white text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      <!-- brand panel -->
      <aside class="relative hidden w-[46%] overflow-hidden bg-linear-to-br from-blue-700 via-indigo-600 to-violet-600 p-12 text-white lg:flex lg:flex-col" [style.background]="brand()?.accent">
        <div class="pointer-events-none absolute -right-32 -top-32 h-96 w-96 rounded-full bg-white/10"></div>
        <div class="pointer-events-none absolute -bottom-40 -left-24 h-96 w-96 rounded-full bg-white/5"></div>
        <a routerLink="/" class="relative flex items-center gap-3">
          @if (brand()) { <span class="flex h-11 w-11 items-center justify-center rounded-2xl border border-white/25 bg-white/15"><app-icon name="receipt" /></span> } @else { <img src="brand/emblem.png" alt="" class="h-14 w-14 object-contain drop-shadow-lg" /> }
          <span class="text-lg font-bold">@if (brand(); as b) { {{ b.name }} } @else { FiscalHub<span class="block text-xs font-medium tracking-[0.2em] text-white/70">FISCALISATION SIMPLIFIED</span> }</span>
        </a>
        <div class="relative my-auto max-w-md">
          <h1 class="text-4xl font-extrabold leading-tight tracking-tight">Fiscalise every invoice with ZIMRA, from anywhere.</h1>
          <p class="mt-4 text-lg text-white/80">One gateway for your tills, your ERP and your phone.</p>
          <ul class="mt-10 space-y-6">
            @for (f of features; track f.title) {
              <li class="flex gap-4">
                <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-white/15"><app-icon [name]="f.icon" /></span>
                <span><span class="block font-semibold">{{ f.title }}</span><span class="text-sm text-white/75">{{ f.text }}</span></span>
              </li>
            }
          </ul>
        </div>
        <p class="relative text-xs text-white/60">© {{ year }} FiscalHub · <a routerLink="/terms" class="underline">Terms</a> · <a routerLink="/privacy" class="underline">Privacy</a></p>
      </aside>

      <!-- form -->
      <main class="flex flex-1 flex-col">
        <div class="flex items-center justify-between px-6 py-5 lg:px-10">
          <a routerLink="/" class="flex items-center gap-2.5 lg:invisible">
            @if (brand()) { <span class="flex h-9 w-9 items-center justify-center rounded-xl bg-linear-to-br from-blue-600 to-violet-600 text-white" [style.background]="brand()?.accent"><app-icon name="receipt" size="h-4 w-4" /></span> } @else { <img src="brand/emblem.png" alt="" class="h-10 w-10 object-contain" /> }
            <span class="whitespace-nowrap font-bold">{{ brand()?.name ?? 'FiscalHub' }}</span>
          </a>
          <span class="hidden text-sm text-slate-500 sm:inline">New here? <a routerLink="/register" class="font-semibold text-brand-600 hover:underline">Register your company</a></span>
        </div>

        <div class="flex flex-1 items-center justify-center px-6 pb-10">
          <form class="w-full max-w-sm" (ngSubmit)="submit()" autocomplete="on">
            @if (brand(); as b) {
              @if (b.hasLogo) { <img [src]="apiUrl('/api/v1/public/brand/' + b.slug + '/logo')" [alt]="b.name" class="mb-6 max-h-14 max-w-48 object-contain" /> }
            }
            <h2 class="text-3xl font-extrabold tracking-tight">{{ title() }}</h2>
            <p class="mt-2 text-slate-500 dark:text-slate-400">{{ subtitle() }}</p>

            @if (note()) { <p class="mt-6 flex gap-2 rounded-xl border border-emerald-200 bg-emerald-50 px-3 py-2.5 text-sm text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-200"><app-icon name="check" size="h-4 w-4" />{{ note() }}</p> }
            @if (error()) { <p class="mt-6 flex gap-2 rounded-xl border border-red-200 bg-red-50 px-3 py-2.5 text-sm text-red-700 dark:border-red-500/30 dark:bg-red-900/30 dark:text-red-300" role="alert"><app-icon name="alert" size="h-4 w-4" />{{ error() }}</p>
              @if (unconfirmed()) { <a [routerLink]="confirmLink()" [queryParams]="confirmQuery()" class="btn mt-3 w-full justify-center">Enter my confirmation code</a> } }

            <div class="mt-7 space-y-4">
              @switch (mode()) {
                @case ('login') {
                  <label class="block"><span class="label">Email or user name</span>
                    <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="mail" size="h-4 w-4" /></span>
                      <input id="identifier" name="identifier" class="input py-2.5 pl-10" [(ngModel)]="identifier" autocomplete="username" autofocus required /></span></label>
                  <div>
                    <div class="flex items-center justify-between"><label class="label" for="password">Password</label><button type="button" class="mb-1 text-sm font-medium text-brand-600 hover:underline" (click)="go('forgot')">Forgot password?</button></div>
                    <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="lock" size="h-4 w-4" /></span>
                      <input id="password" name="password" class="input py-2.5 pl-10 pr-16" [type]="show() ? 'text' : 'password'" [(ngModel)]="password" autocomplete="current-password" required />
                      <button type="button" class="absolute right-3 top-1/2 -translate-y-1/2 text-xs font-semibold text-slate-500 hover:text-slate-800 dark:hover:text-slate-200" (click)="show.set(!show())">{{ show() ? 'Hide' : 'Show' }}</button></span></div>
                  <button class="btn w-full justify-center py-3 text-base" type="submit" [disabled]="busy() || !identifier || !password" [style.background]="brand()?.accent" [style.borderColor]="brand()?.accent">{{ busy() ? 'Signing in…' : 'Sign in' }}<app-icon name="chevron-right" size="h-4 w-4" /></button>
                  @if (canPasskey) {
                    <div class="flex items-center gap-3 text-xs text-slate-400"><span class="h-px flex-1 bg-slate-200 dark:bg-slate-700"></span>or<span class="h-px flex-1 bg-slate-200 dark:bg-slate-700"></span></div>
                    <button class="btn-ghost w-full justify-center py-3" type="button" [disabled]="busy()" (click)="passkey()"><app-icon name="key" size="h-4 w-4" />Sign in with a passkey</button>
                  }
                }
                @case ('mfa') {
                  <input id="code" name="code" class="input py-3 text-center font-mono text-2xl tracking-[0.5em]" [(ngModel)]="code" inputmode="text" autocomplete="one-time-code" placeholder="000000" autofocus required />
                  <p class="text-center text-sm text-slate-500">Lost your phone? Type one of your recovery codes instead.</p>
                  <button class="btn w-full justify-center py-3 text-base" type="submit" [disabled]="busy() || !code.trim()" [style.background]="brand()?.accent" [style.borderColor]="brand()?.accent">{{ busy() ? 'Checking…' : 'Verify and sign in' }}</button>
                  <button class="btn-ghost w-full justify-center" type="button" (click)="backToPassword()">Use a different account</button>
                }
                @case ('forgot') {
                  <label class="block"><span class="label">Email or user name</span>
                    <span class="relative block"><span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="mail" size="h-4 w-4" /></span>
                      <input name="email" type="text" class="input py-2.5 pl-10" [(ngModel)]="email" autocomplete="username" autofocus required /></span></label>
                  <button class="btn w-full justify-center py-3 text-base" type="submit" [disabled]="busy() || !email.trim()">{{ busy() ? 'Sending…' : 'Send me a code' }}<app-icon name="send" size="h-4 w-4" /></button>
                  <button class="btn-ghost w-full justify-center" type="button" (click)="go('login')">Back to sign in</button>
                }
                @case ('reset') {
                  <input name="rcode" class="input py-3 text-center font-mono text-2xl tracking-[0.5em]" [(ngModel)]="code" inputmode="numeric" maxlength="6" autocomplete="one-time-code" placeholder="000000" autofocus required />
                  <label class="block"><span class="label">New password</span>
                    <input name="np" class="input py-2.5" [type]="show() ? 'text' : 'password'" [(ngModel)]="newPassword" autocomplete="new-password" required /></label>
                  <ul class="grid grid-cols-2 gap-1 text-xs">
                    @for (r of rules(); track r.text) { <li class="flex items-center gap-1.5" [class]="r.ok ? 'text-emerald-600' : 'text-slate-500'"><app-icon [name]="r.ok ? 'check' : 'circle'" size="h-3.5 w-3.5" />{{ r.text }}</li> }
                  </ul>
                  <label class="block"><span class="label">New password again</span>
                    <input name="np2" class="input py-2.5" [type]="show() ? 'text' : 'password'" [(ngModel)]="newPassword2" autocomplete="new-password" required /></label>
                  <button class="btn w-full justify-center py-3 text-base" type="submit" [disabled]="busy() || code.trim().length < 6 || !rulesOk() || newPassword !== newPassword2">{{ busy() ? 'Saving…' : 'Change password' }}</button>
                  <p class="text-center text-sm text-slate-500">No email? <button type="button" class="font-medium text-brand-600 hover:underline" (click)="go('forgot')">Send a new code</button></p>
                }
              }
            </div>

            @if (mode() === 'login') { <a routerLink="/register" class="btn-ghost mt-4 w-full justify-center py-3 sm:hidden"><app-icon name="building" size="h-4 w-4" />Register your company</a> }
            @if (mode() === 'login') { <div class="mt-6"><app-store-badges [center]="true" /></div> }
            <div class="mt-8 flex flex-wrap justify-center gap-x-5 gap-y-2 text-xs font-medium text-slate-500">
              <span class="flex items-center gap-1.5"><app-icon name="shield" size="h-3.5 w-3.5" />ZIMRA FDMS</span>
              <span class="flex items-center gap-1.5"><app-icon name="lock" size="h-3.5 w-3.5" />Encrypted</span>
              <span class="flex items-center gap-1.5"><app-icon name="cpu" size="h-3.5 w-3.5" />Phone and web</span>
            </div>
            @if (brand()) { <p class="hint mt-4 text-center">Powered by FiscalHub</p> }
            <p class="mt-4 text-center text-xs text-slate-500"><a routerLink="/docs" class="hover:text-brand-600">Developer documentation</a> · <a routerLink="/terms" class="hover:text-brand-600">Terms</a> · <a routerLink="/privacy" class="hover:text-brand-600">Privacy</a></p>
          </form>
        </div>
      </main>
    </div>
  `,
})
export class LoginComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  readonly brand = signal<PublicBrand | null>(null);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  readonly theme = inject(ThemeService);
  readonly year = new Date().getFullYear();
  readonly apiUrl = apiUrl;
  readonly features = [
    { icon: 'bolt', title: 'Fiscalise in seconds', text: 'Invoices, credit and debit notes signed and sent to ZIMRA FDMS.' },
    { icon: 'cloud-upload', title: 'Never lose a receipt', text: 'Offline files, retries and a full archive with QR codes and PDFs.' },
    { icon: 'shield', title: 'Secure by design', text: 'Two-step sign-in, per-device tokens and an audit trail of every action.' },
  ];

  identifier = '';
  /** Signed in before entering the emailed code: offer the way to enter it. */
  readonly unconfirmed = signal(false);
  confirmLink(): string { return '/register'; }
  confirmQuery(): Record<string, string> { return { verify: this.identifier.includes('@') ? this.identifier.trim() : '' }; }
  password = '';
  email = '';
  newPassword = '';
  newPassword2 = '';
  code = '';
  readonly mode = signal<Mode>('login');
  readonly show = signal(false);
  readonly mfaToken = signal('');
  readonly canPasskey = passkeysSupported();
  readonly busy = signal(false);
  readonly error = signal('');
  readonly note = signal('');

  readonly title = computed(() => ({ login: 'Welcome back', mfa: 'Two-step sign-in', forgot: 'Forgot your password?', reset: 'Check your email' })[this.mode()]);
  readonly subtitle = computed(() => ({
    login: 'Sign in to fiscalise, track and manage your invoices.',
    mfa: 'Enter the 6-digit code from your authenticator app.',
    forgot: 'Enter your email and we will send you a code to choose a new password.',
    reset: `Enter the 6-digit code we sent to ${this.email.trim()} and your new password.`,
  })[this.mode()]);

  rules(): { text: string; ok: boolean }[] {
    const p = this.newPassword;
    return [{ text: '10+ characters', ok: p.length >= 10 }, { text: 'Upper-case letter', ok: /[A-Z]/.test(p) }, { text: 'Lower-case letter', ok: /[a-z]/.test(p) }, { text: 'A number', ok: /\d/.test(p) }];
  }
  rulesOk(): boolean { return this.rules().every(r => r.ok); }

  async ngOnInit(): Promise<void> {
    const slug = this.route.snapshot.queryParamMap.get('company')?.trim().toLowerCase();
    if (!slug || !/^[a-z0-9-]{3,40}$/.test(slug)) return;
    try { this.brand.set(await this.api.get<PublicBrand>(`/api/v1/public/brand/${slug}`)); } catch { /* not a known company */ }
  }

  go(m: Mode): void {
    if (m === 'forgot' && !this.email && this.identifier.trim()) this.email = this.identifier.trim();
    this.mode.set(m); this.error.set(''); this.note.set(''); this.code = '';
  }

  private clean(e: unknown): string { return errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, ''); }

  async submit(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true); this.error.set('');
    try {
      switch (this.mode()) {
        case 'forgot': {
          const r = await this.api.post<{ message: string }>('/api/v1/auth/forgot-password', { email: this.email.trim() });
          this.mode.set('reset'); this.code = ''; this.newPassword = ''; this.newPassword2 = ''; this.note.set(r.message);
          return;
        }
        case 'reset': {
          await this.api.post('/api/v1/auth/reset-password', { email: this.email.trim(), code: this.code.trim(), newPassword: this.newPassword });
          this.identifier = this.email.trim(); this.password = ''; this.mode.set('login'); this.note.set('Password changed. Sign in with your new password.');
          return;
        }
        default: {
          this.note.set('');
          const r = this.mfaToken() ? await this.auth.loginMfa(this.mfaToken(), this.code.trim()) : await this.auth.login(this.identifier, this.password);
          if (r.mfaRequired) { this.mfaToken.set(r.mfaToken ?? ''); this.code = ''; this.mode.set('mfa'); return; }
          await this.finish();
        }
      }
    } catch (e) {
      const msg = this.clean(e);
      this.error.set(msg);
      this.unconfirmed.set(errorMessage(e).includes('EMAIL_NOT_CONFIRMED'));
      if (this.mode() === 'mfa') { if (/took too long/i.test(msg)) this.backToPassword(); this.code = ''; } else if (this.mode() === 'login') this.password = '';
    } finally { this.busy.set(false); }
  }

  async passkey(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true); this.error.set('');
    try { await this.auth.loginPasskey(); await this.finish(); }
    catch (e) {
      // closing the browser's passkey prompt is not an error worth showing
      if ((e as { name?: string })?.name === 'NotAllowedError') return;
      this.error.set(this.clean(e));
    } finally { this.busy.set(false); }
  }

  backToPassword(): void { this.mfaToken.set(''); this.code = ''; this.password = ''; this.mode.set('login'); }

  private async finish(): Promise<void> {
    await this.router.navigateByUrl(this.auth.mustChangePassword() ? '/change-password' : this.auth.homeUrl());
  }
}
