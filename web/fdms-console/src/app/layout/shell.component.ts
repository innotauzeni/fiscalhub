import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { AuthService } from '../core/auth.service';
import { PwaService } from '../core/pwa.service';
import { AvatarService } from '../core/avatar.service';
import { InstallButtonComponent } from '../shared/install.components';
import { NotificationBellComponent } from '../shared/notification-bell.component';
import { Api } from '../core/api.service';
import { MenuNode, SubmoduleNode } from '../core/models';
import { DialogService, ThemeService, ToastService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';

const RAIL_KEY = 'fdms.sidebar.rail';

function read(key: string): string | null { try { return localStorage.getItem(key); } catch { return null; } }
function write(key: string, value: string): void { try { localStorage.setItem(key, value); } catch { /* storage unavailable: not remembered */ } }

/**
 * The application frame. The sidebar is not hard-coded: it is built from the section / module / sub-module tree the server returned at
 * sign-in for this user's roles (tables SectionModule, SystemModule, Submodule, RoleMenu), so changing a role's menu changes this sidebar.
 *
 * Desktop: the whole sidebar collapses to an icon rail; sections fold; modules that have submodules expand like an accordion (and open by
 * themselves when you are on one of their pages). Mobile: a slide-in drawer.
 */
@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, FormsModule, IconComponent, InstallButtonComponent, NotificationBellComponent],
  template: `
    <div class="flex h-dvh overflow-hidden">
      @if (open()) { <div class="fixed inset-0 z-30 bg-slate-900/50 backdrop-blur-sm lg:hidden" (click)="open.set(false)"></div> }

      <aside class="fixed inset-y-0 left-0 z-40 flex shrink-0 flex-col border-r border-slate-200 bg-white shadow-xl transition-[width,transform] duration-200 ease-out dark:border-slate-800 dark:bg-slate-900 lg:static lg:translate-x-0 lg:shadow-none"
             [class]="rail() ? 'w-64 lg:w-[4.5rem]' : 'w-64'" [class.translate-x-0]="open()" [class.-translate-x-full]="!open()">

        <!-- brand -->
        <div class="flex h-16 shrink-0 items-center gap-3 border-b border-slate-200 px-4 dark:border-slate-800" [class.justify-center]="rail()" [class.px-0]="rail()">
          @if (logo(); as l) { <img [src]="l" alt="" class="h-9 w-9 shrink-0 rounded-xl border border-slate-200 bg-white object-contain p-0.5 dark:border-slate-700" /> }
          @else { <img src="brand/emblem.png" alt="FiscalHub" class="h-9 w-9 shrink-0 object-contain" /> }
          @if (!rail()) {
            <div class="min-w-0 flex-1 leading-tight">
              <div class="truncate text-sm font-extrabold tracking-tight"><span class="text-blue-800 dark:text-blue-300">Fiscal</span><span class="text-green-600 dark:text-green-400">Hub</span></div>
              <div class="truncate text-[11px] text-slate-500 dark:text-slate-400">{{ auth.user()?.clientName ?? 'Platform operator' }}</div>
            </div>
            <button class="icon-btn lg:hidden" (click)="open.set(false)" aria-label="Close menu"><app-icon name="x" size="h-4 w-4" /></button>
          }
        </div>

        <!-- menu -->
        <nav class="flex-1 overflow-y-auto overflow-x-hidden px-3 py-4" [class.px-2]="rail()" aria-label="Main">
          @for (section of auth.sections(); track section.id) {
            <div class="mb-3">
              @if (rail()) {
                <div class="mx-auto mb-2 mt-3 h-0.5 w-6 rounded-full opacity-70" [style.background]="section.color" [title]="section.name"></div>
              } @else {
                <button type="button" class="group mb-1 flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-[11px] font-semibold uppercase tracking-wider text-slate-400 transition hover:bg-slate-50 hover:text-slate-600 dark:hover:bg-slate-800/60 dark:hover:text-slate-300"
                        (click)="toggleSection(section.id)" [attr.aria-expanded]="!folded().has(section.id)">
                  <span class="h-2 w-2 shrink-0 rounded-full" [style.background]="section.color"></span>
                  <span class="flex-1 truncate text-left">{{ section.name }}</span>
                  <span class="inline-flex transition-transform duration-200" [class.-rotate-90]="folded().has(section.id)"><app-icon name="chevron" size="h-3.5 w-3.5" /></span>
                </button>
              }

              <div class="grid transition-[grid-template-rows] duration-200 ease-out" [class]="rail() || !folded().has(section.id) ? 'grid-rows-[1fr]' : 'grid-rows-[0fr]'">
                <div class="space-y-0.5 overflow-hidden">
                  @for (m of section.children; track m.id) {
                    @if (m.children.length > 1 && !rail()) {
                      <!-- module with submodules: accordion -->
                      <button type="button" class="group relative flex w-full items-center gap-3 rounded-xl px-2.5 py-2 text-sm font-medium transition"
                              [class]="active(m) ? 'text-brand-700 dark:text-brand-100' : 'text-slate-700 hover:bg-slate-100 dark:text-slate-200 dark:hover:bg-slate-800'"
                              (click)="toggleModule(m.id)" [attr.aria-expanded]="expanded().has(m.id)">
                        <span class="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg transition"
                              [class]="active(m) ? 'bg-brand-600 text-white shadow-sm' : 'bg-slate-100 text-slate-500 group-hover:bg-white dark:bg-slate-800 dark:text-slate-400 dark:group-hover:bg-slate-700'"><app-icon [name]="m.icon" size="h-[18px] w-[18px]" /></span>
                        <span class="flex-1 truncate text-left">{{ m.name }}</span>
                        <span class="inline-flex text-slate-400 transition-transform duration-200" [class.rotate-180]="expanded().has(m.id)"><app-icon name="chevron" size="h-4 w-4" /></span>
                      </button>
                      <div class="grid transition-[grid-template-rows] duration-200 ease-out" [class]="expanded().has(m.id) ? 'grid-rows-[1fr]' : 'grid-rows-[0fr]'">
                        <div class="overflow-hidden">
                          <div class="relative ml-[1.35rem] space-y-0.5 border-l border-slate-200 py-1 pl-3 dark:border-slate-700">
                            @for (s of m.children; track s.id) {
                              <a [routerLink]="'/' + s.route" (click)="open.set(false)"
                                 class="flex items-center gap-2.5 rounded-lg px-2.5 py-1.5 text-[13px] transition"
                                 [class]="subActive(s) ? 'bg-brand-50 font-semibold text-brand-700 dark:bg-brand-500/15 dark:text-brand-100' : 'text-slate-500 hover:bg-slate-100 hover:text-slate-900 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-slate-100'">
                                <app-icon [name]="s.icon" size="h-4 w-4" />{{ s.name }}
                              </a>
                            }
                          </div>
                        </div>
                      </div>
                    } @else {
                      <!-- plain module (or the icon rail): a single link -->
                      <a [routerLink]="'/' + target(m)" (click)="open.set(false)" [title]="rail() ? m.name : ''"
                         class="group relative flex items-center gap-3 rounded-xl px-2.5 py-2 text-sm font-medium transition" [class.justify-center]="rail()"
                         [class]="active(m) ? 'bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-100' : 'text-slate-700 hover:bg-slate-100 dark:text-slate-200 dark:hover:bg-slate-800'">
                        @if (active(m)) { <span class="absolute -left-3 top-1/2 h-5 w-1 -translate-y-1/2 rounded-r bg-brand-600" [class.-left-2]="rail()"></span> }
                        <span class="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg transition"
                              [class]="active(m) ? 'bg-brand-600 text-white shadow-sm' : 'bg-slate-100 text-slate-500 group-hover:bg-white dark:bg-slate-800 dark:text-slate-400 dark:group-hover:bg-slate-700'"><app-icon [name]="m.icon" size="h-[18px] w-[18px]" /></span>
                        @if (!rail()) { <span class="flex-1 truncate">{{ m.name }}</span> }
                      </a>
                    }
                  }
                </div>
              </div>
            </div>
          } @empty {
            @if (!rail()) { <p class="px-2 text-sm text-slate-500">Your role has no menu entries yet. Ask your administrator.</p> }
          }
        </nav>

        <!-- collapse / expand (desktop) -->
        <div class="hidden shrink-0 border-t border-slate-200 p-3 dark:border-slate-800 lg:block">
          <button type="button" class="flex w-full items-center gap-3 rounded-xl px-2.5 py-2 text-sm font-medium text-slate-500 transition hover:bg-slate-100 hover:text-slate-800 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-slate-100"
                  [class.justify-center]="rail()" (click)="toggleRail()" [title]="rail() ? 'Expand menu' : 'Collapse menu'" [attr.aria-label]="rail() ? 'Expand menu' : 'Collapse menu'">
            <app-icon [name]="rail() ? 'chevrons-right' : 'chevrons-left'" />
            @if (!rail()) { <span>Collapse menu</span> }
          </button>
        </div>
      </aside>

      <div class="flex min-w-0 flex-1 flex-col">
        <header class="flex h-16 shrink-0 items-center gap-3 border-b border-slate-200 bg-white px-4 dark:border-slate-800 dark:bg-slate-900">
          <button class="icon-btn lg:hidden" (click)="open.set(true)" aria-label="Open menu"><app-icon name="menu" size="h-4 w-4" /></button>
          <button class="icon-btn hidden lg:inline-flex" (click)="toggleRail()" [attr.aria-label]="rail() ? 'Expand menu' : 'Collapse menu'"><app-icon name="panel-left" size="h-4 w-4" /></button>
          <div class="flex-1"></div>
          <app-notification-bell />
          <app-install-button variant="ghost" />
          <button class="icon-btn" (click)="theme.toggle()" [attr.aria-label]="theme.dark() ? 'Switch to light mode' : 'Switch to dark mode'">
            <app-icon [name]="theme.dark() ? 'sun' : 'moon'" size="h-4 w-4" />
          </button>
          <div class="relative">
            <button class="flex items-center gap-2 rounded-xl px-2 py-1 hover:bg-slate-100 dark:hover:bg-slate-800" (click)="menuOpen.set(!menuOpen())">
              @if (avatar.url(); as a) { <img [src]="a" alt="" class="h-8 w-8 rounded-full object-cover" /> }
              @else { <span class="flex h-8 w-8 items-center justify-center rounded-full bg-linear-to-br from-blue-500 to-violet-500 text-xs font-semibold text-white">{{ initials() }}</span> }
              <span class="hidden text-left leading-tight sm:block">
                <span class="block text-sm font-medium">{{ auth.displayName() }}</span>
                <span class="block text-[11px] text-slate-500 dark:text-slate-400">{{ auth.user()?.roles?.join(', ') }}</span>
              </span>
              <app-icon name="chevron" size="h-4 w-4" />
            </button>
            @if (menuOpen()) {
              <div class="absolute right-0 z-50 mt-2 w-56 rounded-xl border border-slate-200 bg-white p-1 shadow-lg dark:border-slate-700 dark:bg-slate-900">
                <div class="px-3 py-2 text-xs text-slate-500">{{ auth.user()?.email }}</div>
                <a routerLink="/profile" (click)="menuOpen.set(false)" class="block rounded-md px-3 py-2 text-sm hover:bg-slate-100 dark:hover:bg-slate-800">My profile</a>
                <a routerLink="/change-password" (click)="menuOpen.set(false)" class="block rounded-md px-3 py-2 text-sm hover:bg-slate-100 dark:hover:bg-slate-800">Change password</a>
                <button (click)="signOut()" class="flex w-full items-center gap-2 rounded-md px-3 py-2 text-left text-sm hover:bg-slate-100 dark:hover:bg-slate-800"><app-icon name="logout" size="h-4 w-4" />Sign out</button>
              </div>
            }
          </div>
        </header>

        <main class="flex-1 overflow-y-auto p-4 sm:p-6">
          @if (!pwa.online()) {
            <div class="mb-4 flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-2.5 text-sm text-amber-900 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-100" role="status">
              <app-icon name="alert" size="h-4 w-4" /><span><strong>You are offline.</strong> Showing the app as it was; data will refresh when you are back online.</span>
            </div>
          }
          @if (pwa.updateReady()) {
            <div class="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-xl border border-brand-100 bg-brand-50 px-4 py-2.5 text-sm dark:border-brand-500/30 dark:bg-brand-500/10" role="status">
              <span><strong>A new version is ready.</strong></span><button class="btn btn-sm" (click)="pwa.applyUpdate()">Update now</button>
            </div>
          }
          @if (auth.demoOnly()) {
            <div class="mb-4 flex flex-wrap items-center justify-between gap-2 rounded-xl border border-sky-200 bg-sky-50 px-4 py-2.5 text-sm text-sky-900 dark:border-sky-900/50 dark:bg-sky-900/20 dark:text-sky-100">
              <span><strong>Demo mode.</strong> Your trial works with test devices only. Pay for your plan to register live devices.</span>
              <a routerLink="/billing" class="font-medium underline">Go to Billing</a>
            </div>
          }
          <router-outlet />
        </main>
      </div>
    </div>

    <!-- toasts -->
    <div class="pointer-events-none fixed bottom-4 right-4 z-[60] flex w-80 max-w-[calc(100vw-2rem)] flex-col gap-2" aria-live="polite">
      @for (t of toast.toasts(); track t.id) {
        <div class="pointer-events-auto flex items-start gap-2 rounded-lg px-3 py-2.5 text-sm shadow-lg"
             [class]="t.kind === 'err' ? 'bg-red-600 text-white' : 'bg-slate-900 text-white dark:bg-slate-100 dark:text-slate-900'">
          <span class="flex-1">{{ t.text }}</span>
          <button (click)="toast.dismiss(t.id)" aria-label="Dismiss"><app-icon name="x" size="h-4 w-4" /></button>
        </div>
      }
    </div>

    <!-- confirm / prompt dialog -->
    @if (dialog.state(); as d) {
      <div class="fixed inset-0 z-[70] flex items-center justify-center bg-slate-900/50 p-4" (click)="cancel()">
        <div class="card w-full max-w-md" (click)="$event.stopPropagation()" role="dialog" aria-modal="true">
          <p class="text-sm">{{ d.message }}</p>
          @if (d.ask) { <input class="input mt-3" [(ngModel)]="answer" [placeholder]="d.placeholder ?? ''" (keydown.enter)="ok()" /> }
          <div class="mt-4 flex justify-end gap-2">
            <button class="btn-ghost" (click)="cancel()">Cancel</button>
            <button [class]="d.danger ? 'btn-danger' : 'btn'" (click)="ok()">{{ d.confirmLabel }}</button>
          </div>
        </div>
      </div>
    }
  `,
})
export class ShellComponent {
  readonly auth = inject(AuthService);
  private readonly api = inject(Api);
  /** The company's own logo (from its default invoice template) in place of the product mark; the product mark stays when there is none. */
  readonly logo = signal<string | null>(null);
  readonly theme = inject(ThemeService);
  readonly avatar = inject(AvatarService);
  readonly pwa = inject(PwaService);
  readonly toast = inject(ToastService);
  readonly dialog = inject(DialogService);
  private readonly router = inject(Router);

  readonly open = signal(false);
  readonly menuOpen = signal(false);
  answer = '';

  // ---- sidebar state ----
  private readonly wide = signal(false);                                       // lg breakpoint: the rail only exists on desktop
  private readonly collapsed = signal(read(RAIL_KEY) === '1');
  readonly rail = () => this.collapsed() && this.wide();
  readonly expanded = signal<Set<number>>(new Set());                            // modules whose submodules are showing
  readonly unfolded = signal<Set<number>>(new Set());                            // sections opened by a click; all start folded
  readonly folded = computed(() => new Set(this.auth.sections().map(s => s.id).filter(id => !this.unfolded().has(id))));

  private readonly path = toSignal(
    this.router.events.pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd), map(() => this.router.url.split(/[?#]/)[0])),
    { initialValue: this.router.url.split(/[?#]/)[0] });

  constructor() {
    const mq = window.matchMedia('(min-width: 1024px)');
    this.wide.set(mq.matches);
    const onChange = (e: MediaQueryListEvent) => this.wide.set(e.matches);
    mq.addEventListener('change', onChange);
    inject(DestroyRef).onDestroy(() => mq.removeEventListener('change', onChange));

    // the company's own logo, when it has one (a missing logo is just a 404 and the product mark stays)
    effect(() => { const cid = this.auth.user()?.clientId; untracked(() => void this.loadLogo(cid)); });
    // the person's own picture, when they have one
    effect(() => { const id = this.auth.user()?.id; untracked(() => { if (id) void this.avatar.refresh(); else this.avatar.clear(); }); });

    // open the module (and its section) that contains the current page
    effect(() => {
      const p = this.path();
      const sections = this.auth.sections();
      untracked(() => {
        const open = new Set(this.expanded());
        let changed = false;
        for (const s of sections) for (const m of s.children) {
          if (m.children.length > 1 && this.isIn(m, p)) {
            if (!open.has(m.id)) { open.add(m.id); changed = true; }
          }
        }
        if (changed) this.expanded.set(open);
      });
    });
  }

  private isIn(m: MenuNode, p: string): boolean {
    return m.children.length > 1 ? m.children.some(s => p === '/' + s.route) : p === '/' + m.slugname || p.startsWith('/' + m.slugname + '/');
  }

  active(m: MenuNode): boolean { return this.isIn(m, this.path()); }
  subActive(s: SubmoduleNode): boolean { return this.path() === '/' + s.route; }
  /** Where a module's link goes: its own route, or (in the icon rail) the first submodule's page. */
  target(m: MenuNode): string { return m.children.length > 1 ? m.children[0].route : m.slugname; }

  toggleRail(): void { const next = !this.collapsed(); this.collapsed.set(next); write(RAIL_KEY, next ? '1' : '0'); }

  toggleModule(id: number): void { this.expanded.update(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; }); }

  toggleSection(id: number): void {
    this.unfolded.update(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  }

  private async loadLogo(clientId?: string): Promise<void> {
    const old = this.logo(); if (old) URL.revokeObjectURL(old);
    this.logo.set(null);
    if (!clientId) return;
    try { this.logo.set(URL.createObjectURL(await this.api.blob('/api/v1/company/logo'))); } catch { /* no logo: the product mark stays */ }
  }


  initials(): string {
    const u = this.auth.user();
    return u ? ((u.name[0] ?? '') + (u.surname[0] ?? '') || u.email.slice(0, 2)).toUpperCase() : '?';
  }

  ok(): void {
    const d = this.dialog.state();
    if (!d) return;
    const answer = this.answer;
    this.answer = '';
    this.dialog.close();
    d.resolve(d.ask ? answer : true);
  }

  cancel(): void {
    const d = this.dialog.state();
    this.answer = '';
    this.dialog.close();
    d?.resolve(d.ask ? null : false);
  }

  async signOut(): Promise<void> {
    this.menuOpen.set(false);
    await this.auth.logout();
    void this.router.navigateByUrl('/login');
  }
}
