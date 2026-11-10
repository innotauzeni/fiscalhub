import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { PublicFooterComponent, PublicHeaderComponent } from '../shared/public.components';
import { IconComponent } from '../shared/ui.components';

export interface LegalDoc { title: string; body: string; updatedAt: string }
export interface Developer { name: string; about?: string | null; email?: string | null; phone?: string | null; whatsApp?: string | null; website?: string | null; address?: string | null }
export interface Legal { terms: LegalDoc; privacy: LegalDoc; developer: Developer }

interface Block { heading?: string; text?: string }

/** "# Heading" lines and paragraphs separated by blank lines: the simple format the operator writes the documents in. Rendered as text, never as HTML. */
export function legalBlocks(body: string): Block[] {
  const out: Block[] = [];
  for (const chunk of body.replace(/\r/g, '').split(/\n\s*\n/)) {
    const lines = chunk.trim().split('\n');
    let i = 0;
    while (i < lines.length && lines[i].startsWith('#')) { out.push({ heading: lines[i].replace(/^#+\s*/, '') }); i++; }
    const rest = lines.slice(i).join('\n').trim();
    if (rest) out.push({ text: rest });
  }
  return out;
}

/** The public terms and conditions, privacy policy and developer details (/terms, /privacy, /developer). The same words the mobile app shows. */
@Component({
  selector: 'app-legal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, IconComponent, PublicHeaderComponent, PublicFooterComponent],
  template: `
    <div class="min-h-screen bg-white text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      <app-public-header />
      <main class="mx-auto max-w-3xl px-4 py-10 sm:py-14 lg:px-8">
        <nav class="mb-8 flex flex-wrap gap-2 text-sm" aria-label="Legal">
          @for (t of tabs; track t.id) {
            <a [routerLink]="'/' + t.id" class="rounded-full border px-3.5 py-1.5 font-medium transition"
               [class]="which() === t.id ? 'border-brand-600 bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-100' : 'border-slate-200 text-slate-600 hover:border-slate-300 dark:border-slate-700 dark:text-slate-300'">{{ t.label }}</a>
          }
        </nav>
        @if (error()) { <div class="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-800">This page could not be loaded. Try again in a moment.</div> }
        @else if (!data()) { <div class="animate-pulse space-y-3"><div class="h-8 w-1/2 rounded bg-slate-200 dark:bg-slate-800"></div><div class="h-4 w-full rounded bg-slate-200 dark:bg-slate-800"></div><div class="h-4 w-5/6 rounded bg-slate-200 dark:bg-slate-800"></div></div> }
        @else if (which() === 'developer') {
          @if (data()!.developer; as d) {
            <section class="rounded-3xl bg-linear-to-br from-blue-600 to-violet-700 p-6 text-white shadow-lg sm:p-8">
              <span class="flex h-12 w-12 items-center justify-center rounded-2xl bg-white/20"><app-icon name="user-check" /></span>
              <h1 class="mt-4 text-2xl font-bold sm:text-3xl">{{ d.name }}</h1>
              @if (d.about) { <p class="mt-2 max-w-xl text-blue-100">{{ d.about }}</p> }
            </section>
            <div class="mt-6 grid gap-3 sm:grid-cols-2">
              @if (d.email) { <a class="card flex items-center gap-3 transition hover:border-brand-500" [href]="'mailto:' + d.email"><span class="text-xs text-slate-500 w-20">Email</span><span class="min-w-0 truncate font-medium">{{ d.email }}</span></a> }
              @if (d.phone) { <a class="card flex items-center gap-3 transition hover:border-brand-500" [href]="'tel:' + d.phone.replace(' ', '')"><span class="text-xs text-slate-500 w-20">Phone</span><span class="font-medium">{{ d.phone }}</span></a> }
              @if (d.whatsApp) { <a class="card flex items-center gap-3 transition hover:border-brand-500" [href]="'https://wa.me/' + digits(d.whatsApp)" target="_blank" rel="noopener"><span class="text-xs text-slate-500 w-20">WhatsApp</span><span class="font-medium">{{ d.whatsApp }}</span></a> }
              @if (d.website) { <a class="card flex items-center gap-3 transition hover:border-brand-500" [href]="d.website" target="_blank" rel="noopener"><span class="text-xs text-slate-500 w-20">Website</span><span class="min-w-0 truncate font-medium">{{ d.website }}</span></a> }
              @if (d.address) { <div class="card flex items-center gap-3 sm:col-span-2"><span class="text-xs text-slate-500 w-20">Address</span><span class="font-medium">{{ d.address }}</span></div> }
            </div>
          }
        } @else {
          <h1 class="text-3xl font-bold tracking-tight">{{ doc()?.title }}</h1>
          @if (doc()?.updatedAt) { <p class="mt-1 text-sm text-slate-500">Last updated {{ doc()!.updatedAt | date: 'd MMMM y' }}</p> }
          <article class="mt-6">
            @for (b of blocks(); track $index) {
              @if (b.heading) { <h2 class="mb-2 mt-8 text-lg font-semibold">{{ b.heading }}</h2> }
              @else { <p class="mb-3 whitespace-pre-line leading-7 text-slate-700 dark:text-slate-300">{{ b.text }}</p> }
            }
          </article>
        }
      </main>
      <app-public-footer />
    </div>
  `,
})
export class LegalComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  readonly tabs = [{ id: 'terms', label: 'Terms and conditions' }, { id: 'privacy', label: 'Privacy policy' }, { id: 'developer', label: 'Developer' }] as const;
  readonly which = signal<'terms' | 'privacy' | 'developer'>('terms');
  readonly data = signal<Legal | null>(null);
  readonly error = signal(false);
  readonly doc = computed(() => { const d = this.data(); const w = this.which(); return d && w !== 'developer' ? d[w] : null; });
  readonly blocks = computed(() => legalBlocks(this.doc()?.body ?? ''));

  digits(s: string): string { return s.replace(/[^0-9]/g, ''); }

  async ngOnInit(): Promise<void> {
    this.route.data.subscribe(d => { this.which.set(d['which'] ?? 'terms'); window.scrollTo({ top: 0 }); });
    try { this.data.set(await this.api.get<Legal>('/api/v1/public/legal')); } catch { this.error.set(true); }
  }
}
