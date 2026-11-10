import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

const RETRIED = new HttpContextToken<boolean>(() => false);
const PUBLIC = ['/api/v1/auth/login', '/api/v1/auth/refresh', '/api/v1/auth/logout'];

const withToken = (req: HttpRequest<unknown>, token: string | null) =>
  token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

/** Adds the bearer token to API calls and, on a 401, refreshes the session once and repeats the call. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  if (!req.url.startsWith('/api/') || PUBLIC.some(p => req.url.startsWith(p))) return next(req);

  return from(auth.validToken()).pipe(
    switchMap(token => next(withToken(req, token))),
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && !req.context.get(RETRIED)) {
        return from(auth.refresh()).pipe(
          switchMap(ok => {
            if (!ok) { auth.expire(); return throwError(() => err); }
            return from(auth.validToken()).pipe(switchMap(t => next(withToken(req.clone({ context: req.context.set(RETRIED, true) }), t))));
          }),
        );
      }
      // a new account that has not paid yet: send them to Billing, the only place that works
      if (err instanceof HttpErrorResponse && err.status === 402) { void inject(Router).navigateByUrl('/billing'); }
      return throwError(() => err);
    }),
  );
};
