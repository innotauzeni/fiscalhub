import { Injectable, computed, isDevMode, signal } from '@angular/core';

/** The browser's install prompt (Chrome, Edge, Samsung Internet on Android and desktop). Safari/iOS has none: people use Share > Add to Home Screen. */
interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

/**
 * Makes the console an installable app (PWA): registers the service worker, offers the install prompt where the browser has one, explains Add to Home Screen
 * on iPhone and iPad, tells the user when they are offline, and when a new version is waiting. The service worker never stores API data (see public/sw.js).
 */
@Injectable({ providedIn: 'root' })
export class PwaService {
  private readonly deferred = signal<BeforeInstallPromptEvent | null>(null);
  private registration?: ServiceWorkerRegistration;

  /** running as an installed app (home-screen icon, own window) */
  readonly installed = signal(this.isStandalone());
  readonly online = signal(typeof navigator === 'undefined' ? true : navigator.onLine);
  /** a new version has been downloaded and waits for a reload */
  readonly updateReady = signal(false);
  readonly ios = this.detectIos();
  /** the browser can show its own install dialog */
  readonly canInstall = computed(() => !!this.deferred() && !this.installed());
  /** iPhone/iPad Safari: no prompt exists, so we show the steps */
  readonly iosHint = computed(() => this.ios && !this.installed());
  /** anything to offer at all */
  readonly installable = computed(() => this.canInstall() || this.iosHint());

  constructor() {
    if (typeof window === 'undefined') return;
    window.addEventListener('beforeinstallprompt', e => { e.preventDefault(); this.deferred.set(e as BeforeInstallPromptEvent); });
    window.addEventListener('appinstalled', () => { this.deferred.set(null); this.installed.set(true); });
    window.addEventListener('online', () => this.online.set(true));
    window.addEventListener('offline', () => this.online.set(false));
    window.matchMedia('(display-mode: standalone)').addEventListener?.('change', () => this.installed.set(this.isStandalone()));
  }

  private isStandalone(): boolean {
    if (typeof window === 'undefined') return false;
    return window.matchMedia?.('(display-mode: standalone)').matches || (navigator as unknown as { standalone?: boolean }).standalone === true;
  }

  private detectIos(): boolean {
    if (typeof navigator === 'undefined') return false;
    const ua = navigator.userAgent;
    // iPadOS reports itself as a Mac with a touch screen
    return /iphone|ipad|ipod/i.test(ua) || (/Macintosh/i.test(ua) && navigator.maxTouchPoints > 1);
  }

  /** Called once at start-up. Skipped while developing (ng serve) so stale files are never served from a cache. */
  register(): void {
    if (isDevMode() || typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return;
    const base = document.baseURI;   // https://host/app/
    navigator.serviceWorker.register(base + 'sw.js', { scope: base }).then(reg => {
      this.registration = reg;
      if (reg.waiting && navigator.serviceWorker.controller) this.updateReady.set(true);
      reg.addEventListener('updatefound', () => {
        const worker = reg.installing;
        worker?.addEventListener('statechange', () => { if (worker.state === 'installed' && navigator.serviceWorker.controller) this.updateReady.set(true); });
      });
      // look for a new version now and then while the app stays open
      setInterval(() => void reg.update().catch(() => undefined), 60 * 60 * 1000);
    }).catch(() => undefined);
  }

  /** Shows the browser's install dialog. Returns true when the user accepted. */
  async install(): Promise<boolean> {
    const e = this.deferred();
    if (!e) return false;
    await e.prompt();
    const { outcome } = await e.userChoice;
    this.deferred.set(null);
    return outcome === 'accepted';
  }

  /** Switches to the new version: the waiting worker takes over and the page reloads once. */
  applyUpdate(): void {
    const waiting = this.registration?.waiting;
    if (!waiting) { location.reload(); return; }
    navigator.serviceWorker.addEventListener('controllerchange', () => location.reload(), { once: true });
    waiting.postMessage('SKIP_WAITING');
  }
}
