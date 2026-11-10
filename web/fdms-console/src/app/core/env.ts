import { HttpInterceptorFn } from '@angular/common/http';

declare global { interface Window { FDMS_CONFIG?: { apiBase?: string } } }

/**
 * Where the gateway API lives, from config.js next to index.html: '' when the console is served by the API itself (/app/), or the API's
 * address when the console has its own site (e.g. https://fiscalapi.example.com). No trailing slash.
 */
export function apiBase(): string {
  const v = (typeof window !== 'undefined' ? window.FDMS_CONFIG?.apiBase : '') ?? '';
  return v.trim().replace(/\/+$/, '');
}

/** The address the API answers on, for showing in examples (the API itself when it is elsewhere, else this site). */
export function apiOrigin(): string { return apiBase() || location.origin; }

/** "/api/v1/..." -> the full address on the API (unchanged when the API is on this site). */
export function apiUrl(path: string): string { return path.startsWith('/api/') ? apiBase() + path : path; }

/** A page of this console as a full link, e.g. appUrl('/register?ref=X') -> https://site/app/register?ref=X (or https://site/register?... at a site root). */
export function appUrl(path = ''): string {
  const base = new URL(document.baseURI).href.replace(/\/+$/, '');
  return base + (path && !path.startsWith('/') ? '/' : '') + path;
}

/** Sends every "/api/..." request to the API's address. Registered after the auth interceptor, which looks at the relative path. */
export const apiBaseInterceptor: HttpInterceptorFn = (req, next) =>
  req.url.startsWith('/api/') && apiBase() ? next(req.clone({ url: apiBase() + req.url })) : next(req);
