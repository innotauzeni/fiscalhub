import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { usePasskey } from './webauthn';
import { LoginResponse, MenuNode, SectionNode, UserInfo } from './models';

const REFRESH_KEY = 'fdms.refresh';

/**
 * The signed-in session. The short-lived access token lives only in memory; the refresh token is kept in sessionStorage so a page reload
 * keeps you signed in but closing the tab does not. The menu tree and permissions come from the login response (dara-style), so the
 * sidebar and every guard are driven by what the server says this user's roles allow.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly session = signal<LoginResponse | null>(null);
  private inFlightRefresh: Promise<boolean> | null = null;

  readonly user = computed<UserInfo | null>(() => this.session()?.user ?? null);
  readonly permissions = computed(() => new Set(this.session()?.permissions ?? []));
  readonly sections = computed<SectionNode[]>(() => this.session()?.sectionMenus ?? []);
  readonly menus = computed<MenuNode[]>(() => this.session()?.menus ?? []);
  readonly billingLocked = computed(() => this.session()?.billingLocked ?? false);
  readonly demoOnly = computed(() => this.session()?.demoOnly ?? false);
  readonly isAuthenticated = computed(() => this.session() !== null);
  readonly mfaSetupRequired = computed(() => this.session()?.mfaSetupRequired ?? false);
  readonly mustChangePassword = computed(() => this.session()?.user.mustChangePassword ?? false);
  readonly displayName = computed(() => {
    const u = this.user();
    return u ? `${u.name} ${u.surname}`.trim() || u.email : '';
  });

  can(permission: string | undefined | null): boolean { return !permission || this.permissions().has(permission); }
  canAny(...permissions: string[]): boolean { return permissions.some(p => this.permissions().has(p)); }

  /** The first page this user's menu offers (used to land after login). */
  homeUrl(): string {
    if (this.mfaSetupRequired()) return '/profile';
    const first = this.menus()[0];
    return first ? '/' + first.slugname : '/forbidden';
  }

  /** After "My profile" is saved: the header shows the new name without signing in again. */
  updateName(name: string, surname: string): void {
    this.session.update(s => (s ? { ...s, user: { ...s.user, name, surname } } : s));
  }

  private store(r: LoginResponse): void {
    this.session.set(r);
    try { sessionStorage.setItem(REFRESH_KEY, r.refreshToken); } catch { /* storage unavailable: session lasts until reload */ }
  }

  private clear(): void {
    this.session.set(null);
    try { sessionStorage.removeItem(REFRESH_KEY); } catch { /* ignore */ }
  }

  async login(identifier: string, password: string): Promise<LoginResponse> {
    const r = await firstValueFrom(this.http.post<LoginResponse>('/api/v1/auth/login', { identifier, password }));
    if (!r.mfaRequired) this.store(r);   // with two-step sign-in nothing is signed in until the code is checked
    return r;
  }

  /** Second step: the code from the authenticator app (or a recovery code). */
  async loginMfa(mfaToken: string, code: string): Promise<LoginResponse> {
    const r = await firstValueFrom(this.http.post<LoginResponse>('/api/v1/auth/login/mfa', { mfaToken, code }));
    this.store(r);
    return r;
  }

  /** Sign in with a passkey: no password, no code. */
  async loginPasskey(): Promise<LoginResponse> {
    const begin = await firstValueFrom(this.http.post<{ sessionId: string; options: Record<string, any> }>('/api/v1/auth/passkey/options', {}));
    const credential = await usePasskey(begin.options);
    const r = await firstValueFrom(this.http.post<LoginResponse>('/api/v1/auth/passkey/login', { sessionId: begin.sessionId, credential }));
    this.store(r);
    return r;
  }

  /** Called once at start-up: turns a stored refresh token back into a session. */
  async restore(): Promise<void> {
    let token: string | null = null;
    try { token = sessionStorage.getItem(REFRESH_KEY); } catch { /* ignore */ }
    if (token) await this.refresh();
  }

  /**
   * Refresh tokens rotate, so two parallel refreshes would look like token theft and end the session. Everyone who needs a refresh
   * therefore shares one in-flight request.
   */
  refresh(): Promise<boolean> {
    if (this.inFlightRefresh) return this.inFlightRefresh;
    let token: string | null = null;
    try { token = sessionStorage.getItem(REFRESH_KEY); } catch { /* ignore */ }
    if (!token) return Promise.resolve(false);

    this.inFlightRefresh = firstValueFrom(this.http.post<LoginResponse>('/api/v1/auth/refresh', { refreshToken: token }))
      .then(r => { this.store(r); return true; })
      .catch(() => { this.clear(); return false; })
      .finally(() => { this.inFlightRefresh = null; });
    return this.inFlightRefresh;
  }

  /** The access token to send, refreshing first when it is about to expire. */
  async validToken(): Promise<string | null> {
    const s = this.session();
    if (!s) return null;
    if (new Date(s.expiresAt).getTime() - Date.now() < 30_000) {
      if (!(await this.refresh())) return null;
    }
    return this.session()?.accessToken ?? null;
  }

  async changePassword(currentPassword: string, newPassword: string): Promise<void> {
    const r = await firstValueFrom(this.http.post<LoginResponse>('/api/v1/auth/change-password', { currentPassword, newPassword }));
    this.store(r);
  }

  async logout(): Promise<void> {
    let token: string | null = null;
    try { token = sessionStorage.getItem(REFRESH_KEY); } catch { /* ignore */ }
    if (token) await firstValueFrom(this.http.post('/api/v1/auth/logout', { refreshToken: token })).catch(() => undefined);
    this.clear();
    await this.router.navigateByUrl('/login');
  }

  /** The server no longer accepts this session. */
  expire(): void {
    this.clear();
    void this.router.navigateByUrl('/login');
  }
}
