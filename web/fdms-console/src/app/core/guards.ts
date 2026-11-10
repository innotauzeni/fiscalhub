import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Signed in (and, if the account is flagged, made to choose a new password first). */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.isAuthenticated()) return router.parseUrl('/login');
  if (auth.mustChangePassword() && !state.url.startsWith('/change-password')) return router.parseUrl('/change-password');
  // the company requires extra protection: until it is set up, only My profile (where it is set up) is open
  if (auth.mfaSetupRequired() && !state.url.startsWith('/profile') && !state.url.startsWith('/change-password')) return router.parseUrl('/profile');
  return true;
};

/** Route data { permission: 'receipts.view' } - the same names the server checks with [HasPermission]. */
export const permissionGuard: CanActivateFn = route => {
  const auth = inject(AuthService);
  const needed = route.data['permission'] as string | undefined;
  return auth.can(needed) ? true : inject(Router).parseUrl('/forbidden');
};

/** Login page is for people who are not signed in. */
export const guestGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() ? inject(Router).parseUrl(auth.homeUrl()) : true;
};

/** "/" goes to the first page this user's menu offers - so each role lands somewhere that makes sense. */
export const homeGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return inject(Router).parseUrl(auth.homeUrl());
};

/** The public front page is for visitors; someone already signed in is sent to their own first page instead. */
export const landingGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated() ? inject(Router).parseUrl(auth.homeUrl()) : true;
};
