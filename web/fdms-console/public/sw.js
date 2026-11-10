/*
 * FDMS Gateway service worker.
 *
 * What it does: makes the app installable and lets it open when the network is gone (the app shell and its static files are kept).
 * What it never does: store anything from the API. Calls to /api/ always go to the network, so no invoice, token or personal data is ever
 * written to the device's cache, and a signed-out user can never see another session's data from it.
 */
const VERSION = 'v4';
const STATIC_CACHE = `fdms-static-${VERSION}`;
const SHELL_CACHE = `fdms-shell-${VERSION}`;
const SCOPE = self.registration.scope;               // e.g. https://host/app/
const SHELL = SCOPE;                                   // the single-page app: every route returns this document
const OFFLINE = SCOPE + 'offline.html';
const PRECACHE = [SHELL, OFFLINE, SCOPE + 'manifest.webmanifest', SCOPE + 'icons/icon-192.png', SCOPE + 'brand/emblem.png', SCOPE + 'offline.js'];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(SHELL_CACHE).then(c => c.addAll(PRECACHE)).catch(() => undefined));
  // a new version waits until the page says so (see PwaService), so an open session is never swapped under the user
});

self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    for (const key of await caches.keys()) if (![STATIC_CACHE, SHELL_CACHE].includes(key)) await caches.delete(key);
    await self.clients.claim();
  })());
});

self.addEventListener('message', event => { if (event.data === 'SKIP_WAITING') self.skipWaiting(); });

const isStaticAsset = url => /\.(?:js|css|png|jpg|jpeg|svg|webp|ico|woff2?|ttf|webmanifest)$/i.test(url.pathname);

self.addEventListener('fetch', event => {
  const req = event.request;
  if (req.method !== 'GET') return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;                          // other sites: untouched
  if (!url.pathname.startsWith(new URL(SCOPE).pathname)) return;           // the API, Swagger and everything outside /app/: untouched

  // opening a page: the network first (always the newest version), the saved app shell when offline
  if (req.mode === 'navigate') {
    event.respondWith((async () => {
      try {
        const res = await fetch(req);
        if (res.ok && res.headers.get('content-type')?.includes('text/html')) (await caches.open(SHELL_CACHE)).put(SHELL, res.clone());
        return res;
      } catch {
        return (await caches.match(SHELL)) || (await caches.match(OFFLINE)) || Response.error();
      }
    })());
    return;
  }

  // scripts, styles, images, fonts: show what is saved at once and refresh it in the background
  if (isStaticAsset(url)) {
    event.respondWith((async () => {
      const cache = await caches.open(STATIC_CACHE);
      const hit = await cache.match(req);
      const refresh = fetch(req).then(res => { if (res.ok && !res.headers.get('cache-control')?.includes('no-store')) cache.put(req, res.clone()); return res; }).catch(() => hit);
      return hit || refresh;
    })());
  }
});
