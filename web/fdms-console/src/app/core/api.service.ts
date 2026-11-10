import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Problem } from './models';

/** Turns whatever the server (or the network) said into one readable sentence. */
export function errorMessage(e: unknown): string {
  if (e instanceof HttpErrorResponse) {
    if (e.status === 0) return 'Cannot reach the gateway. Check your connection.';
    const p = e.error as Problem | null;
    if (p && typeof p === 'object' && p.title) return (p.errorCode ? `[${p.errorCode}] ` : '') + p.title;
    if (e.status === 403) return 'You do not have permission to do that.';
    if (e.status === 429) return 'Too many requests - wait a moment and try again.';
    return `${e.status} ${e.statusText}`;
  }
  return e instanceof Error ? e.message : String(e);
}

export type Query = Record<string, string | number | boolean | undefined | null>;

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  private params(q?: Query): HttpParams {
    let p = new HttpParams();
    for (const [k, v] of Object.entries(q ?? {})) if (v !== undefined && v !== null && v !== '') p = p.set(k, String(v));
    return p;
  }

  get<T>(url: string, q?: Query): Promise<T> { return firstValueFrom(this.http.get<T>(url, { params: this.params(q) })); }
  post<T>(url: string, body?: unknown, q?: Query): Promise<T> { return firstValueFrom(this.http.post<T>(url, body ?? null, { params: this.params(q) })); }
  put<T>(url: string, body?: unknown): Promise<T> { return firstValueFrom(this.http.put<T>(url, body ?? null)); }
  delete<T>(url: string): Promise<T> { return firstValueFrom(this.http.delete<T>(url)); }

  /** POST that answers with a file (e.g. the template preview PNG). */
  postBlob(url: string, body: unknown): Promise<Blob> { return firstValueFrom(this.http.post(url, body, { responseType: 'blob' })); }

  /** Binary download (PDF, PNG, xlsx). */
  blob(url: string, q?: Query): Promise<Blob> { return firstValueFrom(this.http.get(url, { params: this.params(q), responseType: 'blob' })); }

  /** Opens a protected file in a new tab (the request carries the bearer token, so a plain link would not work). */
  async open(url: string, q?: Query): Promise<void> {
    const url2 = URL.createObjectURL(await this.blob(url, q));
    window.open(url2, '_blank', 'noopener');
    setTimeout(() => URL.revokeObjectURL(url2), 60_000);
  }

  async download(url: string, fileName: string): Promise<void> {
    const href = URL.createObjectURL(await this.blob(url));
    const a = document.createElement('a');
    a.href = href; a.download = fileName; document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(href), 10_000);
  }
}
