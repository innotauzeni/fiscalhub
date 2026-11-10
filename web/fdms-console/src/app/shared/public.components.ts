import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { ThemeService } from '../core/ui.services';
import { InstallButtonComponent } from './install.components';
import { StorePillComponent } from './store-badges.component';
import { IconComponent } from './ui.components';
import { apiUrl } from '../core/env';

/** Top bar of every public page (landing, registration, documentation): brand, links, theme switch, sign in / open console. Collapses to a menu on phones. */
@Component({
  selector: 'app-public-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, InstallButtonComponent, StorePillComponent],
  template: `
    <header class="sticky top-0 z-40 border-b border-slate-200/70 bg-white/85 backdrop-blur dark:border-slate-800/70 dark:bg-slate-950/85">
      <div class="mx-auto flex h-16 items-center gap-3 px-4 lg:px-8" [class]="wide() ? 'max-w-[88rem]' : 'max-w-7xl'">
        <a routerLink="/" class="flex items-center gap-2.5" aria-label="Home">
          <img src="brand/emblem.png" alt="" class="h-10 w-10 object-contain" />
          <span class="text-lg font-extrabold tracking-tight"><span class="text-blue-800 dark:text-blue-300">Fiscal</span><span class="text-green-600 dark:text-green-400">Hub</span></span>
        </a>

        <nav class="ml-8 hidden items-center gap-1 md:flex" aria-label="Main">
          @for (l of links; track l.label) {
            @if (l.route) { <a [routerLink]="l.route" [fragment]="l.fragment" class="rounded-lg px-3 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-100 hover:text-slate-900 dark:text-slate-300 dark:hover:bg-slate-800 dark:hover:text-white">{{ l.label }}</a> }
          }
        </nav>

        <span class="flex-1"></span>
        <app-store-pill />
        <app-install-button class="hidden sm:block" />
        <button class="icon-btn" (click)="theme.toggle()" [attr.aria-label]="theme.dark() ? 'Switch to light mode' : 'Switch to dark mode'"><app-icon [name]="theme.dark() ? 'sun' : 'moon'" size="h-4 w-4" /></button>
        @if (auth.isAuthenticated()) {
          <a class="btn btn-sm hidden sm:inline-flex" [routerLink]="auth.homeUrl()">Open console</a>
        } @else {
          <a class="hidden rounded-lg px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-100 sm:inline-block dark:text-slate-200 dark:hover:bg-slate-800" routerLink="/login">Sign in</a>
          <a class="btn btn-sm hidden sm:inline-flex" routerLink="/register">Get started</a>
        }
        <button class="icon-btn md:hidden" (click)="open.set(!open())" [attr.aria-expanded]="open()" aria-label="Menu"><app-icon [name]="open() ? 'x' : 'menu'" size="h-4 w-4" /></button>
      </div>

      @if (open()) {
        <nav class="border-t border-slate-200 bg-white px-4 pb-4 pt-2 md:hidden dark:border-slate-800 dark:bg-slate-950" aria-label="Mobile">
          @for (l of links; track l.label) {
            <a [routerLink]="l.route" [fragment]="l.fragment" (click)="open.set(false)" class="block rounded-lg px-3 py-2.5 text-sm font-medium text-slate-700 hover:bg-slate-100 dark:text-slate-200 dark:hover:bg-slate-800">{{ l.label }}</a>
          }
          <div class="mt-3 grid grid-cols-2 gap-2">
            @if (auth.isAuthenticated()) { <a class="btn col-span-2" [routerLink]="auth.homeUrl()">Open console</a> }
            @else { <a class="btn-ghost" routerLink="/login">Sign in</a><a class="btn" routerLink="/register">Get started</a> }
          </div>
        </nav>
      }
    </header>
  `,
})
export class PublicHeaderComponent {
  /** the documentation uses a wider layout (sidebar + text + rail), and the header lines up with it */
  readonly wide = input(false);
  readonly auth = inject(AuthService);
  readonly theme = inject(ThemeService);
  readonly open = signal(false);
  readonly links = [
    { label: 'Features', route: '/', fragment: 'features' },
    { label: 'How it works', route: '/', fragment: 'how' },
    { label: 'Pricing', route: '/', fragment: 'pricing' },
    { label: 'User guide', route: '/guide', fragment: undefined },
    { label: 'Documentation', route: '/docs', fragment: undefined },
    { label: 'FAQ', route: '/', fragment: 'faq' },
  ];
}

@Component({
  selector: 'app-public-footer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <footer class="border-t border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-900/50">
      <div class="mx-auto grid max-w-7xl gap-8 px-4 py-12 sm:grid-cols-2 lg:grid-cols-4 lg:px-8">
        <div class="sm:col-span-2">
          <div class="flex items-center gap-2.5">
            <img src="brand/emblem.png" alt="" class="h-10 w-10 object-contain" />
            <span class="text-base font-semibold">{{ name() }}</span>
          </div>
          <p class="mt-3 max-w-sm text-sm text-slate-600 dark:text-slate-400">A fiscalisation gateway for ZIMRA's FDMS. Connect any billing, POS or ERP system once, and every invoice is fiscalised, signed and archived.</p>
          @if (email() || phone()) {
            <p class="mt-3 text-sm text-slate-600 dark:text-slate-400">@if (email()) { <a class="hover:text-brand-600" [href]="'mailto:' + email()">{{ email() }}</a> } @if (email() && phone()) { · } {{ phone() }}</p>
          }
        </div>
        <div>
          <div class="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">Product</div>
          <ul class="space-y-2 text-sm text-slate-600 dark:text-slate-400">
            <li><a routerLink="/" fragment="features" class="hover:text-brand-600">Features</a></li>
            <li><a routerLink="/" fragment="pricing" class="hover:text-brand-600">Pricing</a></li>
            <li><a routerLink="/" fragment="faq" class="hover:text-brand-600">FAQ</a></li>
          </ul>
        </div>
        <div>
          <div class="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">Get going</div>
          <ul class="space-y-2 text-sm text-slate-600 dark:text-slate-400">
            <li><a routerLink="/guide" class="hover:text-brand-600">User guide (PDF)</a></li>
            <li><a routerLink="/docs" class="hover:text-brand-600">Developer documentation</a></li>
            <li><a [href]="docx" download class="hover:text-brand-600">Free Word invoice template</a></li>
            <li><a routerLink="/register" class="hover:text-brand-600">Register your company</a></li>
            <li><a routerLink="/login" class="hover:text-brand-600">Sign in</a></li>
          </ul>
        </div>
      </div>
      <div class="border-t border-slate-200 px-4 py-4 text-center text-xs text-slate-500 dark:border-slate-800">
        <div class="mb-2 flex flex-wrap justify-center gap-x-4 gap-y-1">
          <a routerLink="/terms" class="hover:text-brand-600">Terms and conditions</a>
          <a routerLink="/privacy" class="hover:text-brand-600">Privacy policy</a>
          <a routerLink="/developer" class="hover:text-brand-600">Developer</a>
        </div>
        © {{ year }} {{ name() }}. ZIMRA and FDMS are the property of their respective owners.
      </div>
    </footer>
  `,
})
export class PublicFooterComponent {
  readonly name = input('FDMS Gateway');
  readonly email = input<string | undefined>(undefined);
  readonly phone = input<string | undefined>(undefined);
  readonly year = new Date().getFullYear();
  readonly docx = apiUrl('/api/v1/public/templates/tax-invoice.docx');
}

/** Tiny helper used by the pricing cards and billing screens. */
export function money(amount: number, currency: string): string {
  try { return new Intl.NumberFormat(undefined, { style: 'currency', currency, maximumFractionDigits: amount % 1 === 0 ? 0 : 2 }).format(amount); }
  catch { return `${amount.toFixed(2)} ${currency}`; }
}

/** 0 means unlimited on the server; this is how it is shown. */
export function limit(n: number): string { return n === 0 ? 'Unlimited' : n.toLocaleString(); }
