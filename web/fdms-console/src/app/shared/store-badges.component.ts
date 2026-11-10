import { ChangeDetectionStrategy, Component, Injectable, OnInit, computed, inject, input, signal } from '@angular/core';
import { Api } from '../core/api.service';

export interface AppLinks { apkPure: string | null; playStore: string | null; appStore: string | null; version: string | null; }

/** The download links set by the platform operator, loaded once per visit. */
@Injectable({ providedIn: 'root' })
export class AppLinksStore {
  private readonly api = inject(Api);
  readonly links = signal<AppLinks | null>(null);
  private loading: Promise<void> | null = null;

  load(force = false): Promise<void> {
    if (this.loading && !force) return this.loading;
    this.loading = this.api.get<AppLinks>('/api/v1/public/app-links', { t: Date.now() }).then(l => this.links.set(l)).catch(() => this.links.set(null));
    return this.loading;
  }
}

/** A small "Android app" button for the top bar: the first store that has a link (APKPure, then Google Play, then the App Store). */
@Component({
  selector: 'app-store-pill',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (target(); as t) {
      <a [href]="t.url" target="_blank" rel="noopener noreferrer" [attr.aria-label]="'Get the app on ' + t.store"
         class="inline-flex items-center gap-1.5 rounded-lg bg-slate-900 px-3 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-slate-800 dark:bg-white dark:text-slate-900 dark:hover:bg-slate-200">
        @if (t.store === 'App Store') {
          <svg viewBox="0 0 24 24" class="h-4 w-4" aria-hidden="true"><path fill="currentColor" d="M16.4 12.6c0-2.4 2-3.6 2.1-3.7a4.6 4.6 0 00-3.6-1.9c-1.5-.2-3 .9-3.7.9-.8 0-2-.9-3.3-.9a4.9 4.9 0 00-4.1 2.5c-1.8 3.1-.5 7.6 1.2 10.1.8 1.2 1.8 2.6 3.1 2.5 1.2 0 1.7-.8 3.2-.8s1.9.8 3.2.8c1.3 0 2.2-1.2 3-2.4a10 10 0 001.4-2.8 4.3 4.3 0 01-2.5-4.3zM14 5.4a4.3 4.3 0 001-3.1 4.4 4.4 0 00-2.9 1.5 4.1 4.1 0 00-1 3 3.6 3.6 0 002.9-1.4z"/></svg>
        } @else {
          <svg viewBox="0 0 24 24" class="h-4 w-4" aria-hidden="true"><path fill="#3ddc84" d="M17.6 9.48l1.84-3.18a.38.38 0 10-.66-.38l-1.86 3.22a11.5 11.5 0 00-9.84 0L5.22 5.92a.38.38 0 10-.66.38L6.4 9.48A10.8 10.8 0 001 18h22a10.8 10.8 0 00-5.4-8.52zM7 15.25a1.25 1.25 0 110-2.5 1.25 1.25 0 010 2.5zm10 0a1.25 1.25 0 110-2.5 1.25 1.25 0 010 2.5z"/></svg>
        }
        {{ t.store === 'App Store' ? 'iPhone app' : 'Android app' }}
      </a>
    }
  `,
})
export class StorePillComponent implements OnInit {
  readonly store = inject(AppLinksStore);
  readonly target = computed(() => {
    const l = this.store.links();
    return l?.apkPure ? { url: l.apkPure, store: 'APKPure' } : l?.playStore ? { url: l.playStore, store: 'Google Play' } : l?.appStore ? { url: l.appStore, store: 'App Store' } : null;
  });
  ngOnInit(): void { void this.store.load(); }
}

/**
 * "Get the app" buttons: APKPure, Google Play and the App Store. Only the stores with a link are shown; nothing at all when none is set.
 * The links open the store's own page in a new tab, where the app is downloaded.
 */
@Component({
  selector: 'app-store-badges',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (any()) {
      <div class="flex flex-wrap items-center gap-3" [class.justify-center]="center()">
        @if (store.links()?.apkPure; as url) {
          <a [href]="url" target="_blank" rel="noopener noreferrer" class="badge-store" aria-label="Download the Android app on APKPure">
            <svg viewBox="0 0 24 24" class="h-7 w-7 shrink-0" aria-hidden="true"><path fill="#3ddc84" d="M17.6 9.48l1.84-3.18a.38.38 0 10-.66-.38l-1.86 3.22a11.5 11.5 0 00-9.84 0L5.22 5.92a.38.38 0 10-.66.38L6.4 9.48A10.8 10.8 0 001 18h22a10.8 10.8 0 00-5.4-8.52zM7 15.25a1.25 1.25 0 110-2.5 1.25 1.25 0 010 2.5zm10 0a1.25 1.25 0 110-2.5 1.25 1.25 0 010 2.5z"/></svg>
            <span class="text-left leading-tight"><span class="block text-[10px] uppercase tracking-wide opacity-80">Download on</span><span class="block text-base font-semibold">APKPure</span></span>
          </a>
        }
        @if (store.links()?.playStore; as url) {
          <a [href]="url" target="_blank" rel="noopener noreferrer" class="badge-store" aria-label="Get the app on Google Play">
            <svg viewBox="0 0 24 24" class="h-7 w-7 shrink-0" aria-hidden="true">
              <path fill="#34a853" d="M3.6 1.8L13.4 11.6 3.6 21.4A1.5 1.5 0 013 20.2V3a1.5 1.5 0 01.6-1.2z"/>
              <path fill="#fbbc04" d="M16.8 8.2l-3.4 3.4 3.4 3.4 3.9-2.2a1.4 1.4 0 000-2.4z"/>
              <path fill="#ea4335" d="M3.6 21.4l9.8-9.8 3.4 3.4L5 21.6a1.5 1.5 0 01-1.4-.2z"/>
              <path fill="#4285f4" d="M3.6 1.8A1.5 1.5 0 015 1.6l11.8 6.6-3.4 3.4z"/>
            </svg>
            <span class="text-left leading-tight"><span class="block text-[10px] uppercase tracking-wide opacity-80">Get it on</span><span class="block text-base font-semibold">Google Play</span></span>
          </a>
        }
        @if (store.links()?.appStore; as url) {
          <a [href]="url" target="_blank" rel="noopener noreferrer" class="badge-store" aria-label="Download the app on the App Store">
            <svg viewBox="0 0 24 24" class="h-7 w-7 shrink-0" aria-hidden="true"><path fill="currentColor" d="M16.4 12.6c0-2.4 2-3.6 2.1-3.7a4.6 4.6 0 00-3.6-1.9c-1.5-.2-3 .9-3.7.9-.8 0-2-.9-3.3-.9a4.9 4.9 0 00-4.1 2.5c-1.8 3.1-.5 7.6 1.2 10.1.8 1.2 1.8 2.6 3.1 2.5 1.2 0 1.7-.8 3.2-.8s1.9.8 3.2.8c1.3 0 2.2-1.2 3-2.4a10 10 0 001.4-2.8 4.3 4.3 0 01-2.5-4.3zM14 5.4a4.3 4.3 0 001-3.1 4.4 4.4 0 00-2.9 1.5 4.1 4.1 0 00-1 3 3.6 3.6 0 002.9-1.4z"/></svg>
            <span class="text-left leading-tight"><span class="block text-[10px] uppercase tracking-wide opacity-80">Download on the</span><span class="block text-base font-semibold">App Store</span></span>
          </a>
        }
      </div>
      @if (showVersion() && store.links()?.version) { <p class="mt-2 text-xs text-slate-500" [class.text-center]="center()">Version {{ store.links()?.version }}</p> }
    }
  `,
  styles: [`
    .badge-store { display: inline-flex; align-items: center; gap: .6rem; min-width: 11rem; padding: .55rem 1rem; border-radius: .75rem; background: #0b0f19; color: #fff;
      border: 1px solid rgba(255,255,255,.18); box-shadow: 0 6px 16px -8px rgba(15,23,42,.6); transition: transform .15s ease, box-shadow .15s ease; text-decoration: none; }
    .badge-store:hover { transform: translateY(-1px); box-shadow: 0 10px 22px -10px rgba(15,23,42,.7); }
    .badge-store:focus-visible { outline: 2px solid #3b82f6; outline-offset: 2px; }
  `],
})
export class StoreBadgesComponent implements OnInit {
  readonly store = inject(AppLinksStore);
  readonly center = input(false);
  readonly showVersion = input(false);
  readonly any = computed(() => { const l = this.store.links(); return !!(l?.apkPure || l?.playStore || l?.appStore); });
  ngOnInit(): void { void this.store.load(); }
}
