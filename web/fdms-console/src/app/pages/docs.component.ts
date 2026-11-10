import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { marked } from 'marked';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { DeviceStore } from '../core/device.store';
import { ToastService } from '../core/ui.services';
import { PublicFooterComponent, PublicHeaderComponent } from '../shared/public.components';
import { IconComponent } from '../shared/ui.components';
import { apiOrigin } from '../core/env';

interface TocItem { id: string; text: string; level: 2 | 3; body: string; }
interface NavGroup { title: string; items: TocItem[]; }

const PLACEHOLDER = 'https://your-gateway.example.com';
const METHOD = /^(GET|POST|PUT|PATCH|DELETE)\s+(\/\S*)/;
const CALLOUT = /^(note|tip|important|warning|caution|heads up|remember)\b[:.!]?\s*/i;
const LANG_KEY = 'fdms.docs.lang';
const LANG_LABEL: Record<string, string> = { bash: 'cURL', sh: 'cURL', csharp: 'C#', cs: 'C#', js: 'JavaScript', javascript: 'JavaScript', ts: 'TypeScript', php: 'PHP', python: 'Python', java: 'Java', go: 'Go', json: 'JSON', http: 'HTTP', text: 'Text', '': 'Text' };

/** Which section each heading belongs to in the sidebar, and its icon. Headings not listed land in "More". */
const GROUPS: { title: string; slugs: string[] }[] = [
  { title: 'Get started', slugs: ['quick-start', 'any-language-the-5-rules', 'concepts', 'authentication', 'accounts-plans-and-billing'] },
  { title: 'Fiscalise', slugs: ['fiscalise-a-sale', 'receipt-statuses', 'read-search-and-print', 'credit-notes-and-corrections', 'fiscal-day', 'bulk-and-file-imports'] },
  { title: 'Zoho Books', slugs: ['zoho-books-which-option-is-for-you', 'zoho-books-switch-it-on', 'zoho-books-fiscalise-your-zoho-invoices', 'zoho-books-from-your-own-system', 'zoho-books-fields-for-each-record', 'zoho-bridge-for-partners', 'zoho-errors'] },
  { title: 'Events and reliability', slugs: ['webhooks', 'retries-safe-by-design'] },
  { title: 'Reference', slugs: ['errors', 'time-and-timezones', 'other-features'] },
  { title: 'Launch', slugs: ['going-live-checklist', 'troubleshooting'] },
];
const ICONS: Record<string, string> = {
  'quick-start': 'bolt', concepts: 'layers', authentication: 'key', 'accounts-plans-and-billing': 'card', 'fiscalise-a-sale': 'receipt', 'receipt-statuses': 'chart',
  'read-search-and-print': 'search', 'credit-notes-and-corrections': 'edit', 'fiscal-day': 'calendar', 'bulk-and-file-imports': 'upload', webhooks: 'cloud-upload',
  'retries-safe-by-design': 'refresh', errors: 'alert', 'time-and-timezones': 'sun', 'other-features': 'squares', 'any-language-the-5-rules': 'clipboard',
  'zoho-books-which-option-is-for-you': 'link', 'zoho-books-switch-it-on': 'toggle', 'zoho-books-fiscalise-your-zoho-invoices': 'receipt', 'zoho-books-from-your-own-system': 'repeat',
  'zoho-bridge-for-partners': 'users', 'zoho-books-fields-for-each-record': 'file-text', 'zoho-errors': 'alert', 'going-live-checklist': 'check', troubleshooting: 'shield',
};

/**
 * The integration guide as a public page (no sign-in needed). It renders docs/INTEGRATION.md - the same file that lives in the repository - and shows the
 * address you are reading it on in every example. Signed-in users also see their own devices, ready to copy.
 * The page adds: a grouped, searchable sidebar that opens the subsections of the section you are reading, a reading-progress bar, language tabs for code,
 * HTTP-method badges, call-out boxes, heading anchors, a right-hand rail and previous/next navigation.
 */
@Component({
  selector: 'app-docs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(window:scroll)': 'onScroll()' },
  imports: [RouterLink, IconComponent, PublicHeaderComponent, PublicFooterComponent],
  template: `
    <div class="min-h-screen bg-white text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      <app-public-header [wide]="true" />

      <!-- sub bar: contents (phones), search, reference links, reading progress -->
      <div class="sticky top-16 z-30 border-b border-slate-200 bg-white/90 backdrop-blur dark:border-slate-800 dark:bg-slate-950/90">
        <div class="mx-auto flex h-12 max-w-[88rem] items-center gap-2 px-4 lg:px-8">
          <button class="icon-btn lg:hidden" (click)="navOpen.set(!navOpen())" [attr.aria-expanded]="navOpen()" aria-label="Contents"><app-icon name="panel-left" size="h-4 w-4" /></button>
          <label class="relative block min-w-0 flex-1 sm:max-w-md">
            <span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400"><app-icon name="search" size="h-4 w-4" /></span>
            <input class="input rounded-xl bg-slate-50 py-1.5 pl-9 dark:bg-slate-900" type="search" placeholder="Search the guide…" [value]="query()" (input)="query.set($any($event.target).value)" aria-label="Search the guide" />
          </label>
          <span class="flex-1"></span>
          <a class="hidden items-center gap-1.5 rounded-lg px-2.5 py-1.5 text-sm text-slate-600 hover:bg-slate-100 hover:text-brand-700 sm:inline-flex dark:text-slate-300 dark:hover:bg-slate-800" [href]="origin() + '/swagger/index.html'" target="_blank" rel="noopener">API reference</a>
          <a class="hidden rounded-lg px-2.5 py-1.5 text-sm text-slate-600 hover:bg-slate-100 hover:text-brand-700 md:inline-flex dark:text-slate-300 dark:hover:bg-slate-800" [href]="origin() + '/swagger/v1/swagger.json'" target="_blank" rel="noopener">OpenAPI</a>
          <a class="hidden rounded-lg px-2.5 py-1.5 text-sm text-slate-600 hover:bg-slate-100 hover:text-brand-700 md:inline-flex dark:text-slate-300 dark:hover:bg-slate-800" [href]="origin() + '/api/v1/docs/integration'" target="_blank" rel="noopener">Markdown</a>
        </div>
        <div class="h-0.5 bg-transparent"><div class="h-full bg-linear-to-r from-blue-500 to-violet-500 transition-[width] duration-100" [style.width.%]="progress()"></div></div>
      </div>

      <!-- hero -->
      <section class="relative overflow-hidden border-b border-slate-100 dark:border-slate-900">
        <div class="pointer-events-none absolute inset-0 -z-10 bg-linear-to-br from-brand-50 via-white to-violet-50 dark:from-slate-900 dark:via-slate-950 dark:to-slate-900"></div>
        <div class="pointer-events-none absolute -right-24 -top-24 -z-10 h-80 w-80 rounded-full bg-linear-to-br from-blue-400/20 to-violet-500/20 blur-3xl"></div>
        <div class="mx-auto max-w-[88rem] px-4 py-10 sm:py-14 lg:px-8">
          <div class="max-w-3xl">
            <span class="inline-flex items-center gap-2 rounded-full border border-brand-100 bg-white px-3 py-1 text-xs font-semibold uppercase tracking-wide text-brand-700 shadow-sm dark:border-brand-500/30 dark:bg-slate-900 dark:text-brand-100">Developer documentation</span>
            <h1 class="mt-4 text-3xl font-bold tracking-tight sm:text-5xl">Connect your system to <span class="bg-linear-to-r from-blue-600 to-violet-600 bg-clip-text text-transparent">ZIMRA</span> in minutes</h1>
            <p class="mt-4 text-base leading-7 text-slate-600 sm:text-lg dark:text-slate-400">Everything an integrator needs: authentication, sending invoices, credit notes, offline mode and webhooks, with copy-and-paste examples in cURL, Python, JavaScript, PHP, C#, Java and Go, plus Zoho Books.</p>
            <div class="mt-6 flex flex-wrap items-center gap-3">
              <button type="button" class="flex max-w-full items-center gap-2 rounded-xl border border-slate-200 bg-white px-3 py-2 text-left shadow-sm transition hover:border-brand-500 dark:border-slate-700 dark:bg-slate-900" (click)="copy(origin())" title="Copy the base URL">
                <span class="text-xs font-semibold uppercase tracking-wide text-slate-500">Base URL</span>
                <span class="min-w-0 truncate font-mono text-sm">{{ origin() }}</span><app-icon name="copy" size="h-4 w-4" />
              </button>
              <a class="btn-ghost" [href]="origin() + '/swagger/index.html'" target="_blank" rel="noopener">Try it in Swagger</a>
              @if (!auth.isAuthenticated()) { <a class="btn" routerLink="/register">Get an account</a> }
            </div>
          </div>

          <!-- ways in -->
          <div class="mt-9 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            @for (c of paths; track c.id) {
              <a [href]="'#' + c.id" (click)="go($event, c.id)" class="group flex gap-3 rounded-2xl border border-slate-200 bg-white/80 p-4 shadow-sm backdrop-blur transition hover:-translate-y-0.5 hover:border-brand-500 hover:shadow-md dark:border-slate-800 dark:bg-slate-900/70">
                <span class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-linear-to-br text-white shadow-sm" [class]="c.tone"><app-icon [name]="c.icon" /></span>
                <span class="min-w-0"><span class="block font-semibold group-hover:text-brand-700 dark:group-hover:text-brand-100">{{ c.title }}</span><span class="mt-0.5 block text-sm text-slate-500 dark:text-slate-400">{{ c.text }}</span></span>
              </a>
            }
          </div>
        </div>
      </section>

      <div class="mx-auto flex max-w-[88rem] gap-10 px-4 py-8 lg:px-8">
        <!-- sidebar -->
        <nav class="fixed bottom-0 left-0 top-28 z-20 w-80 overflow-y-auto border-r border-slate-200 bg-white p-5 transition-transform dark:border-slate-800 dark:bg-slate-950 lg:sticky lg:top-32 lg:z-0 lg:h-[calc(100vh-9rem)] lg:w-64 lg:shrink-0 lg:translate-x-0 lg:border-0 lg:bg-transparent lg:p-0 lg:pr-3"
             [class.-translate-x-full]="!navOpen()" aria-label="Documentation">
          @if (query().trim()) {
            <div class="mb-2 text-xs font-semibold uppercase tracking-wider text-slate-400">{{ results().length }} {{ results().length === 1 ? 'match' : 'matches' }}</div>
            <ul class="space-y-1">
              @for (t of results(); track t.id) {
                <li><a [href]="'#' + t.id" (click)="go($event, t.id)" class="block rounded-lg px-3 py-2 transition hover:bg-slate-100 dark:hover:bg-slate-800">
                  <span class="block text-sm font-medium">{{ t.text }}</span><span class="mt-0.5 line-clamp-2 block text-xs text-slate-500">{{ snippet(t) }}</span></a></li>
              } @empty { <li class="px-3 py-2 text-sm text-slate-500">Nothing matches "{{ query() }}".</li> }
            </ul>
          } @else {
            @for (g of groups(); track g.title) {
              <div class="mb-5">
                <div class="mb-1.5 px-3 text-[11px] font-semibold uppercase tracking-wider text-slate-400">{{ g.title }}</div>
                <ul class="space-y-0.5">
                  @for (t of g.items; track t.id) {
                    <li>
                      <a [href]="'#' + t.id" (click)="go($event, t.id)"
                         class="group relative flex items-center gap-2.5 rounded-lg px-3 py-1.5 text-sm transition"
                         [class]="isCurrent(t) ? 'bg-brand-50 font-semibold text-brand-700 dark:bg-brand-500/15 dark:text-brand-100' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900 dark:text-slate-400 dark:hover:bg-slate-800/60 dark:hover:text-slate-100'">
                        @if (isCurrent(t)) { <span class="absolute inset-y-1.5 left-0 w-0.5 rounded-full bg-brand-600"></span> }
                        <span class="shrink-0" [class]="isCurrent(t) ? 'text-brand-600' : 'text-slate-400 group-hover:text-slate-600 dark:group-hover:text-slate-300'"><app-icon [name]="icon(t.id)" size="h-4 w-4" /></span>
                        <span class="truncate">{{ t.text }}</span>
                      </a>
                      @if (isCurrent(t) && subs(t).length) {
                        <ul class="mb-1 ml-[1.15rem] mt-0.5 space-y-0.5 border-l border-slate-200 pl-3 dark:border-slate-800">
                          @for (s of subs(t); track s.id) {
                            <li><a [href]="'#' + s.id" (click)="go($event, s.id)" class="block rounded-md px-2 py-1 text-[13px] transition"
                                   [class]="active() === s.id ? 'font-medium text-brand-700 dark:text-brand-100' : 'text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-slate-100'">{{ s.text }}</a></li>
                          }
                        </ul>
                      }
                    </li>
                  }
                </ul>
              </div>
            }
          }
          <a routerLink="/register" class="mt-2 block rounded-2xl bg-linear-to-br from-blue-600 to-violet-700 p-4 text-white shadow-md">
            <span class="block text-sm font-semibold">Ready to integrate?</span>
            <span class="mt-0.5 block text-xs text-blue-100">Register your company and get your first device token.</span>
          </a>
        </nav>
        @if (navOpen()) { <button type="button" class="fixed inset-0 top-28 z-10 bg-slate-900/40 backdrop-blur-sm lg:hidden" (click)="navOpen.set(false)" aria-label="Close contents"></button> }

        <main class="min-w-0 max-w-3xl flex-1">
          @if (auth.isAuthenticated() && devices().length) {
            <div class="mb-8 rounded-2xl border border-brand-100 bg-brand-50/60 p-5 dark:border-brand-500/30 dark:bg-brand-500/10">
              <div class="mb-1 font-semibold">Your devices</div>
              <p class="text-sm text-slate-600 dark:text-slate-300">Use one of your device ids below as <code class="rounded bg-white px-1.5 py-0.5 font-mono text-xs dark:bg-slate-900">{{ '{' }}deviceId{{ '}' }}</code>; create its token under <a routerLink="/tokens" class="text-brand-600 underline">API tokens</a>.</p>
              <div class="mt-3 flex flex-wrap gap-2">
                @for (d of devices(); track d.id) {
                  <button type="button" class="flex max-w-full items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-left text-xs hover:border-brand-500 dark:border-slate-700 dark:bg-slate-900" (click)="copy(d.id)" title="Copy the device id">
                    <span class="font-medium">Device {{ d.zimraDeviceId }}</span><span class="truncate font-mono text-slate-500">{{ d.id }}</span><app-icon name="copy" size="h-3.5 w-3.5" />
                  </button>
                }
              </div>
            </div>
          }

          @if (error()) { <div class="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">The documentation could not be loaded. Try again in a moment.</div> }
          @else if (!html()) { <div class="animate-pulse space-y-3"><div class="h-8 w-1/2 rounded bg-slate-200 dark:bg-slate-800"></div><div class="h-4 w-full rounded bg-slate-200 dark:bg-slate-800"></div><div class="h-4 w-5/6 rounded bg-slate-200 dark:bg-slate-800"></div></div> }
          @else {
            <article class="doc" [innerHTML]="html()" (click)="onClick($event)"></article>

            <!-- previous / next section -->
            <nav class="mt-14 grid gap-3 sm:grid-cols-2" aria-label="Previous and next section">
              @if (prev(); as p) { <a [href]="'#' + p.id" (click)="go($event, p.id)" class="group rounded-xl border border-slate-200 p-4 transition hover:border-brand-500 hover:shadow-sm dark:border-slate-800"><span class="text-xs text-slate-500">← Previous</span><span class="mt-0.5 block font-semibold group-hover:text-brand-700 dark:group-hover:text-brand-100">{{ p.text }}</span></a> } @else { <span></span> }
              @if (next(); as n) { <a [href]="'#' + n.id" (click)="go($event, n.id)" class="group rounded-xl border border-slate-200 p-4 text-right transition hover:border-brand-500 hover:shadow-sm sm:col-start-2 dark:border-slate-800"><span class="text-xs text-slate-500">Next →</span><span class="mt-0.5 block font-semibold group-hover:text-brand-700 dark:group-hover:text-brand-100">{{ n.text }}</span></a> }
            </nav>
          }

          <p class="mt-12 border-t border-slate-200 pt-6 text-sm text-slate-500 dark:border-slate-800">
            Need help? Contact the administrator of your gateway account and quote the <code class="code">traceId</code> from the error. Machine-readable reference: <a class="text-brand-600 underline" [href]="origin() + '/swagger/v1/swagger.json'">OpenAPI JSON</a>.
          </p>
        </main>

        <!-- right rail -->
        <aside class="sticky top-32 hidden h-[calc(100vh-9rem)] w-56 shrink-0 overflow-y-auto xl:block" aria-label="On this page">
          @if (currentSubs().length) {
            <div class="mb-2 text-[11px] font-semibold uppercase tracking-wider text-slate-400">On this page</div>
            <ul class="space-y-1 border-l border-slate-200 dark:border-slate-800">
              @for (s of currentSubs(); track s.id) {
                <li><a [href]="'#' + s.id" (click)="go($event, s.id)" class="-ml-px block border-l-2 py-0.5 pl-3 text-[13px] transition"
                       [class]="active() === s.id ? 'border-brand-600 font-medium text-brand-700 dark:text-brand-100' : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-900 dark:text-slate-400 dark:hover:text-slate-100'">{{ s.text }}</a></li>
              }
            </ul>
          }
          <div class="mt-6 rounded-2xl border border-slate-200 p-4 text-sm dark:border-slate-800">
            <div class="font-semibold">Resources</div>
            <ul class="mt-2 space-y-1.5 text-[13px]">
              <li><a class="text-brand-600 hover:underline" [href]="origin() + '/swagger/index.html'" target="_blank" rel="noopener">Interactive API reference</a></li>
              <li><a class="text-brand-600 hover:underline" [href]="origin() + '/swagger/v1/swagger.json'" target="_blank" rel="noopener">OpenAPI document</a></li>
              <li><a class="text-brand-600 hover:underline" [href]="origin() + '/api/v1/docs/integration'" target="_blank" rel="noopener">This guide as Markdown</a></li>
              <li><a class="text-brand-600 hover:underline" [href]="origin() + '/api/v1/public/templates/tax-invoice.docx'" download>Word invoice template</a></li>
              <li><a class="text-brand-600 hover:underline" routerLink="/register">Register a company</a></li>
            </ul>
          </div>
        </aside>
      </div>

      <app-public-footer />
    </div>
  `,
})
export class DocsComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly toast = inject(ToastService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly store = inject(DeviceStore);
  readonly auth = inject(AuthService);

  /** The address of the gateway itself (the API), as reported by the API - in development this differs from the address of this page. */
  readonly origin = signal(apiOrigin());
  readonly html = signal<SafeHtml | null>(null);
  readonly toc = signal<TocItem[]>([]);
  readonly active = signal('');
  readonly navOpen = signal(false);
  readonly error = signal(false);
  readonly query = signal('');
  readonly progress = signal(0);
  readonly devices = computed(() => this.store.devices());
  private observer?: IntersectionObserver;

  readonly paths = [
    { id: 'quick-start', icon: 'bolt', tone: 'from-blue-500 to-indigo-500', title: 'Quick start', text: 'Your first fiscal invoice in three calls, in your language.' },
    { id: 'authentication', icon: 'key', tone: 'from-violet-500 to-purple-500', title: 'Authentication', text: 'Per-device tokens and what they may do.' },
    { id: 'zoho-books-which-option-is-for-you', icon: 'link', tone: 'from-emerald-500 to-teal-500', title: 'Zoho Books', text: 'Fiscalise Zoho invoices, or connect your system to Zoho.' },
    { id: 'going-live-checklist', icon: 'check', tone: 'from-amber-500 to-orange-500', title: 'Go live', text: 'The checklist before you switch to production.' },
  ];

  private readonly mains = computed(() => this.toc().filter(t => t.level === 2));
  /** the sidebar: the guide's sections in groups (headings the groups do not know about collect under "More") */
  readonly groups = computed<NavGroup[]>(() => {
    const mains = this.mains(); const placed = new Set<string>();
    const out: NavGroup[] = GROUPS.map(g => ({ title: g.title, items: g.slugs.map(s => mains.find(m => m.id === s)).filter((m): m is TocItem => !!m) }));
    out.forEach(g => g.items.forEach(i => placed.add(i.id)));
    const rest = mains.filter(m => !placed.has(m.id));
    if (rest.length) out.push({ title: 'More', items: rest });
    return out.filter(g => g.items.length);
  });
  readonly results = computed(() => {
    const q = this.query().trim().toLowerCase();
    return q ? this.toc().filter(t => t.text.toLowerCase().includes(q) || t.body.toLowerCase().includes(q)) : [];
  });
  /** the h2 the reader is in (the one the active heading belongs to) */
  private readonly currentMain = computed(() => {
    const all = this.toc(); let i = all.findIndex(t => t.id === this.active());
    if (i < 0) i = 0;
    while (i > 0 && all[i].level !== 2) i--;
    return all[i]?.id ?? '';
  });
  readonly currentSubs = computed(() => this.subs({ id: this.currentMain() } as TocItem));
  readonly prev = computed(() => { const m = this.mains(); const i = m.findIndex(t => t.id === this.currentMain()); return i > 0 ? m[i - 1] : null; });
  readonly next = computed(() => { const m = this.mains(); const i = m.findIndex(t => t.id === this.currentMain()); return i >= 0 ? m[i + 1] ?? null : null; });

  icon(id: string): string { return ICONS[id] ?? 'folder'; }
  isCurrent(t: TocItem): boolean { return t.id === this.currentMain(); }
  /** the h3 headings under an h2 */
  subs(t: { id: string }): TocItem[] {
    const all = this.toc(); const i = all.findIndex(x => x.id === t.id); if (i < 0) return [];
    const out: TocItem[] = [];
    for (let j = i + 1; j < all.length && all[j].level === 3; j++) out.push(all[j]);
    return out;
  }
  /** a short piece of the section's text around the first hit, for the search results */
  snippet(t: TocItem): string {
    const q = this.query().trim().toLowerCase(); const at = t.body.toLowerCase().indexOf(q);
    if (at < 0) return t.body.slice(0, 90);
    return (at > 30 ? '…' : '') + t.body.slice(Math.max(0, at - 30), at + 80).trim() + '…';
  }

  onScroll(): void {
    const el = document.documentElement; const max = el.scrollHeight - el.clientHeight;
    this.progress.set(max > 0 ? Math.min(100, Math.round((el.scrollTop / max) * 1000) / 10) : 0);
  }

  async ngOnInit(): Promise<void> {
    if (this.auth.isAuthenticated() && this.auth.can('devices.view')) { try { await this.store.load(); } catch { /* the personal card is optional */ } }
    try {
      const res = await firstValueFrom(this.http.get('/api/v1/docs/integration', { responseType: 'text', observe: 'response' }));
      const gateway = res.headers.get('X-Gateway-Url') ?? apiOrigin();
      this.origin.set(gateway);
      const md = (res.body ?? '').split(PLACEHOLDER).join(gateway);   // the API already substitutes it; this only matters for an older server
      const doc = new DOMParser().parseFromString(marked.parse(md, { async: false, gfm: true }) as string, 'text/html');

      // the guide's own title and intro are replaced by the page hero
      doc.querySelector('h1')?.remove();

      // headings get ids (for the sidebar and deep links), a hover anchor, and each remembers its text for the search
      const toc: TocItem[] = [];
      const used = new Set<string>();
      doc.querySelectorAll('h2, h3').forEach(h => {
        let id = (h.textContent ?? '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'section';
        while (used.has(id)) id += '-2';
        used.add(id); h.id = id;
        let body = ''; let n = h.nextElementSibling;
        while (n && !/^H[23]$/.test(n.tagName)) { body += ' ' + (n.textContent ?? '').replace(/\s+/g, ' '); n = n.nextElementSibling; }
        toc.push({ id, text: h.textContent ?? '', level: h.tagName === 'H2' ? 2 : 3, body: body.trim().slice(0, 900) });
        const a = doc.createElement('a'); a.href = '#' + id; a.className = 'anchor'; a.setAttribute('aria-label', 'Link to this section'); a.textContent = '#'; h.appendChild(a);
      });

      this.wrapCode(doc);

      // "POST /api/..." in inline code becomes a coloured method badge followed by the path
      doc.querySelectorAll('code').forEach(c => {
        if (c.closest('pre')) return;
        const m = METHOD.exec((c.textContent ?? '').trim());
        if (!m) return;
        const badge = doc.createElement('span'); badge.className = `mbadge mbadge-${m[1].toLowerCase()}`; badge.textContent = m[1];
        const path = doc.createElement('code'); path.textContent = (c.textContent ?? '').trim().slice(m[1].length).trim();
        c.replaceWith(badge, path);
      });

      // blockquotes that start with Note / Tip / Warning become call-out boxes with a label
      doc.querySelectorAll('blockquote').forEach(q => {
        const first = q.querySelector('p > strong:first-child');
        const label = first && CALLOUT.test((first.textContent ?? '').trim()) ? (first.textContent ?? '').replace(/[:.!\s]+$/, '') : null;
        const word = (label ?? CALLOUT.exec((q.textContent ?? '').trim())?.[1] ?? 'Note').toLowerCase();
        const kind = /warn|caution|important/.test(word) ? 'warn' : /tip|remember/.test(word) ? 'tip' : 'info';
        if (label) first!.remove();
        q.classList.add('doc-callout', `doc-callout-${kind}`); q.setAttribute('data-label', word.charAt(0).toUpperCase() + word.slice(1));
      });

      // tables scroll sideways inside a rounded frame instead of breaking the page on a phone
      doc.querySelectorAll('table').forEach(t => { const w = doc.createElement('div'); w.className = 'doc-table'; t.replaceWith(w); w.appendChild(t); });

      this.toc.set(toc);
      this.html.set(this.sanitizer.bypassSecurityTrustHtml(doc.body.innerHTML));   // our own file, served from this site; the page's CSP blocks inline script anyway
      setTimeout(() => this.afterRender(), 0);
    } catch { this.error.set(true); }
  }

  /** every code block gets a header with its language and a copy button; runs of consecutive blocks become one tabbed block (cURL | C# | JavaScript ...) */
  private wrapCode(doc: Document): void {
    const langOf = (pre: Element) => /language-(\w+)/.exec(pre.querySelector('code')?.className ?? '')?.[1]?.toLowerCase() ?? '';
    const done = new Set<Element>();
    // only blocks in a programming language are offered as tabs of one another; a request line and its JSON body stay separate blocks
    const isCode = (pre: Element) => ['bash', 'sh', 'csharp', 'cs', 'js', 'javascript', 'ts', 'php', 'python', 'java', 'go'].includes(langOf(pre));
    doc.querySelectorAll('pre').forEach(first => {
      if (done.has(first)) return;
      const run: Element[] = [first]; done.add(first);
      if (isCode(first)) for (let n = first.nextElementSibling; n && n.tagName === 'PRE' && isCode(n); n = n.nextElementSibling) { run.push(n); done.add(n); }
      const box = doc.createElement('div'); box.className = run.length > 1 ? 'cb cb-tabs' : 'cb';
      const head = doc.createElement('div'); head.className = 'cb-head';
      const list = doc.createElement('div'); list.className = 'cb-tablist'; list.setAttribute('role', 'tablist');
      run.forEach((pre, i) => {
        const lang = langOf(pre); const label = LANG_LABEL[lang] ?? lang.toUpperCase();
        const tab = doc.createElement(run.length > 1 ? 'button' : 'span');
        tab.className = run.length > 1 ? 'cb-tab' + (i === 0 ? ' cb-active' : '') : 'cb-lang'; tab.textContent = label;
        if (run.length > 1) { (tab as HTMLButtonElement).type = 'button'; tab.setAttribute('data-lang', label); tab.setAttribute('role', 'tab'); }
        list.appendChild(tab);
        pre.classList.add('cb-panel'); if (i > 0) pre.classList.add('hidden');
      });
      const copy = doc.createElement('button'); copy.type = 'button'; copy.className = 'doc-copy'; copy.textContent = 'Copy';
      head.append(list, copy);
      first.before(box); box.appendChild(head); run.forEach(p => box.appendChild(p));
    });
  }

  ngOnDestroy(): void { this.observer?.disconnect(); }

  private afterRender(): void {
    const hash = location.hash.slice(1);
    if (hash) document.getElementById(hash)?.scrollIntoView();
    else this.active.set(this.toc()[0]?.id ?? '');
    this.observer = new IntersectionObserver(entries => {
      const visible = entries.filter(e => e.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
      if (visible) this.active.set(visible.target.id);
    }, { rootMargin: '-140px 0px -70% 0px' });
    this.host.nativeElement.querySelectorAll('.doc h2, .doc h3').forEach(h => this.observer!.observe(h));
    let lang: string | null = null;
    try { lang = localStorage.getItem(LANG_KEY); } catch { /* ignore */ }
    if (lang) this.selectLang(lang);
  }

  go(e: Event, id: string): void {
    e.preventDefault();
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    history.replaceState(null, '', `${location.pathname}#${id}`);
    this.active.set(id);
    this.navOpen.set(false);
  }

  /** switches every tabbed code block to the language the reader picked, and remembers it */
  private selectLang(label: string): void {
    this.host.nativeElement.querySelectorAll('.cb-tabs').forEach(box => {
      const tabs = Array.from(box.querySelectorAll<HTMLElement>('.cb-tab')); const panels = Array.from(box.querySelectorAll<HTMLElement>('.cb-panel'));
      const at = tabs.findIndex(t => t.dataset['lang'] === label); if (at < 0) return;
      tabs.forEach((t, i) => t.classList.toggle('cb-active', i === at)); panels.forEach((p, i) => p.classList.toggle('hidden', i !== at));
    });
  }

  async onClick(e: Event): Promise<void> {
    const el = e.target as HTMLElement;
    if (el.classList.contains('cb-tab')) {
      const label = el.dataset['lang'] ?? ''; this.selectLang(label);
      try { localStorage.setItem(LANG_KEY, label); } catch { /* ignore */ }
      return;
    }
    if (el.classList.contains('doc-copy')) {
      const box = el.closest('.cb'); const panel = box?.querySelector('.cb-panel:not(.hidden)') ?? box?.querySelector('.cb-panel');
      await this.copy(panel?.querySelector('code')?.textContent ?? '', el);
      return;
    }
    // links inside the guide: in-page anchors scroll, /app/... paths stay in the app, everything else navigates normally
    const a = el.closest('a');
    const href = a?.getAttribute('href');
    if (a && href?.startsWith('#')) { e.preventDefault(); this.go(e, href.slice(1)); }
  }

  async copy(text: string, button?: HTMLElement): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      if (button) { button.textContent = 'Copied'; setTimeout(() => (button.textContent = 'Copy'), 1500); } else this.toast.ok('Copied.');
    } catch { /* clipboard unavailable: the text is selectable */ }
  }
}
