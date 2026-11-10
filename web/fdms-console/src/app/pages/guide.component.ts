import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, ViewEncapsulation, computed, inject, signal } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ActivatedRoute, Router } from '@angular/router';
import { marked } from 'marked';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { apiUrl } from '../core/env';
import { PublicFooterComponent, PublicHeaderComponent } from '../shared/public.components';
import { IconComponent } from '../shared/ui.components';

type Part = 'company' | 'owner' | 'partner';
interface Heading { id: string; text: string; }

const PARTS: { key: Part; label: string; hint: string }[] = [
  { key: 'company', label: 'For companies', hint: 'Fiscalise sales, devices, invoices, billing, WhatsApp, Zoho' },
  { key: 'owner', label: 'For the platform owner', hint: 'Clients, plans and prices, approvals, revenue, settings' },
  { key: 'partner', label: 'For partners', hint: 'Your companies, commission and the Zoho bridge' },
];

/**
 * The user guide with screenshots: public (no sign-in), one part each for company users, the platform owner and partners, each also as a PDF.
 * The text is docs/guide/{part}.md, served by the API. With ?print=1 the page drops its header and menus (that is how the PDFs are made).
 */
@Component({
  selector: 'app-guide',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  imports: [IconComponent, PublicHeaderComponent, PublicFooterComponent],
  styles: [`
    .guide h1 { font-size: 1.9rem; font-weight: 800; letter-spacing: -0.02em; margin: 0 0 0.75rem; }
    .guide h2 { font-size: 1.35rem; font-weight: 700; margin: 2.5rem 0 0.75rem; padding-top: 1rem; border-top: 1px solid rgb(226 232 240); scroll-margin-top: 5rem; }
    .guide h3 { font-size: 1.05rem; font-weight: 650; margin: 1.5rem 0 0.5rem; }
    .guide p, .guide li { line-height: 1.7; color: rgb(51 65 85); }
    .dark .guide p, .dark .guide li { color: rgb(203 213 225); }
    .guide ul { list-style: disc; padding-left: 1.4rem; margin: 0.5rem 0 1rem; }
    .guide ol { list-style: decimal; padding-left: 1.4rem; margin: 0.5rem 0 1rem; }
    .guide li { margin: 0.2rem 0; }
    .guide p { margin: 0.6rem 0; }
    .guide strong { font-weight: 650; color: inherit; }
    .guide code { font-family: ui-monospace, monospace; font-size: 0.85em; background: rgb(241 245 249); padding: 0.1rem 0.35rem; border-radius: 0.35rem; }
    .dark .guide code { background: rgb(30 41 59); }
    .guide img { display: block; width: 100%; max-width: 980px; margin: 1rem 0 0.4rem; border: 1px solid rgb(226 232 240); border-radius: 0.75rem; box-shadow: 0 6px 24px -12px rgb(15 23 42 / 0.35); }
    .guide img + em, .guide p > em:only-child { display: block; font-size: 0.85rem; color: rgb(100 116 139); margin-bottom: 1rem; }
    .guide blockquote { margin: 1rem 0; padding: 0.75rem 1rem; border-left: 4px solid rgb(59 130 246); background: rgb(239 246 255); border-radius: 0.5rem; }
    .dark .guide blockquote { background: rgb(30 58 138 / 0.25); }
    .guide blockquote p { margin: 0; }
    .guide table { width: 100%; border-collapse: collapse; margin: 1rem 0; font-size: 0.9rem; }
    .guide th, .guide td { border: 1px solid rgb(226 232 240); padding: 0.45rem 0.6rem; text-align: left; vertical-align: top; }
    .guide th { background: rgb(248 250 252); font-weight: 600; }
    .dark .guide th { background: rgb(15 23 42); }
    .dark .guide th, .dark .guide td, .dark .guide h2, .dark .guide img { border-color: rgb(51 65 85); }
    @media print {
      .guide h2 { break-before: page; border-top: 0; margin-top: 0; }
      .guide img { break-inside: avoid; box-shadow: none; max-width: 100%; }
      .guide h3, .guide blockquote, .guide tr { break-inside: avoid; }
      .guide h2, .guide h3 { break-after: avoid; }
    }
  `],
  template: `
    <div class="min-h-screen bg-white text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      @if (!print()) { <app-public-header [wide]="true" /> }
      <div class="mx-auto max-w-6xl px-4 py-8 lg:px-8" [class.py-0]="print()">
        @if (!print()) {
          <div class="mb-6 flex flex-wrap items-end justify-between gap-4">
            <div>
              <span class="inline-flex items-center gap-2 rounded-full border border-brand-100 bg-white px-3 py-1 text-xs font-semibold uppercase tracking-wide text-brand-700 dark:border-brand-500/30 dark:bg-slate-900 dark:text-brand-100">User guide</span>
              <h1 class="mt-3 text-3xl font-bold tracking-tight">How to use FiscalHub</h1>
              <p class="mt-1 text-slate-600 dark:text-slate-400">Step by step, with pictures of every screen.</p>
            </div>
            <a class="btn" [href]="pdfUrl()" target="_blank" rel="noopener"><app-icon name="download" size="h-4 w-4" />Download PDF</a>
          </div>
          <div class="mb-8 grid gap-3 sm:grid-cols-3" role="tablist">
            @for (p of parts; track p.key) {
              <button type="button" role="tab" [attr.aria-selected]="part() === p.key" (click)="choose(p.key)"
                class="rounded-xl border p-4 text-left transition" [class]="part() === p.key ? 'border-brand-500 bg-brand-50 ring-1 ring-brand-500 dark:bg-brand-500/10' : 'border-slate-200 hover:border-brand-300 dark:border-slate-800'">
                <div class="font-semibold">{{ p.label }}</div><div class="mt-0.5 text-xs text-slate-500">{{ p.hint }}</div>
              </button>
            }
          </div>
        }
        @if (error()) { <div class="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">The guide could not be loaded. Try again in a moment.</div> }
        @else if (!html()) { <div class="animate-pulse space-y-3"><div class="h-8 w-1/2 rounded bg-slate-200 dark:bg-slate-800"></div><div class="h-4 w-full rounded bg-slate-200 dark:bg-slate-800"></div></div> }
        @else {
          <div class="flex gap-10">
            @if (!print() && headings().length) {
              <nav class="sticky top-24 hidden max-h-[calc(100vh-7rem)] w-60 shrink-0 self-start overflow-y-auto lg:block" aria-label="Contents">
                <div class="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">Contents</div>
                @for (h of headings(); track h.id) {
                  <a [href]="'#' + h.id" (click)="jump($event, h.id)" class="block rounded-lg px-2 py-1 text-sm text-slate-600 hover:bg-slate-100 hover:text-brand-700 dark:text-slate-300 dark:hover:bg-slate-800">{{ h.text }}</a>
                }
              </nav>
            }
            <article class="guide min-w-0 flex-1" [innerHTML]="html()"></article>
          </div>
        }
      </div>
      @if (!print()) { <app-public-footer /> }
    </div>
  `,
})
export class GuideComponent implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  readonly parts = PARTS;
  readonly part = signal<Part>('company');
  readonly print = signal(false);
  private readonly sanitizer = inject(DomSanitizer);
  readonly html = signal<SafeHtml | null>(null);
  readonly error = signal(false);
  readonly headings = signal<Heading[]>([]);
  readonly pdfUrl = computed(() => apiUrl(`/api/v1/docs/guide/${this.part()}/pdf`));

  ngOnInit(): void {
    const q = this.route.snapshot.queryParamMap;
    this.print.set(q.get('print') === '1');
    const asked = q.get('part') as Part | null;
    // without a choice: the part for who is signed in
    const roles = this.auth.user()?.roles ?? [];
    const guess: Part = roles.includes('SuperAdmin') ? 'owner' : roles.includes('Partner') ? 'partner' : 'company';
    void this.load(asked && PARTS.some(p => p.key === asked) ? asked : guess);
  }

  choose(p: Part): void { void this.router.navigate([], { queryParams: { part: p }, replaceUrl: true }); void this.load(p); }

  private async load(p: Part): Promise<void> {
    this.part.set(p); this.html.set(null); this.error.set(false);
    try {
      const md = await firstValueFrom(this.http.get(apiUrl(`/api/v1/docs/guide/${p}`), { responseType: 'text' }));
      // headings get anchors for the contents list
      const doc = new DOMParser().parseFromString(await marked.parse(md, { gfm: true }), 'text/html');
      const heads: Heading[] = [];
      doc.querySelectorAll('h2, h3').forEach(h => {
        const text = (h.textContent ?? '').trim();
        h.id = text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'section';
        if (h.tagName === 'H2') heads.push({ id: h.id, text });
      });
      // our own text from the repository (docs/guide), so the heading anchors may stay
      this.html.set(this.sanitizer.bypassSecurityTrustHtml(doc.body.innerHTML));
      this.headings.set(heads);
    } catch { this.error.set(true); }
  }

  jump(e: Event, id: string): void { e.preventDefault(); document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
}
