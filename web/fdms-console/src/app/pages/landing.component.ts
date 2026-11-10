import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { Plan, PublicInfo, planModuleLabels, usagePricingLines } from '../core/models';
import { PwaService } from '../core/pwa.service';
import { InstallButtonComponent, RevealDirective } from '../shared/install.components';
import { AppLinksStore, StoreBadgesComponent } from '../shared/store-badges.component';
import { AssistantLauncherComponent } from './assistant.component';
import { PublicFooterComponent, PublicHeaderComponent, limit, money } from '../shared/public.components';
import { IconComponent } from '../shared/ui.components';

type Lang = 'curl' | 'csharp' | 'js' | 'python';

/** The public front page: what the gateway is, how it works, what it costs, how to install it as an app, and where to register, sign in or read the docs. */
@Component({
  selector: 'app-landing',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent, PublicHeaderComponent, PublicFooterComponent, InstallButtonComponent, RevealDirective, AssistantLauncherComponent, StoreBadgesComponent],
  template: `
    <div class="min-h-screen overflow-x-clip bg-white text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      <app-public-header />

      <!-- ============================== hero ============================== -->
      <section class="relative isolate overflow-hidden">
        <div class="pointer-events-none absolute inset-0 -z-10 bg-linear-to-b from-brand-50 via-white to-white dark:from-slate-900 dark:via-slate-950 dark:to-slate-950"></div>
        <div class="bg-grid pointer-events-none absolute inset-0 -z-10"></div>
        <div class="anim-drift pointer-events-none absolute -left-24 top-10 -z-10 h-80 w-80 rounded-full bg-blue-400/30 blur-3xl dark:bg-blue-500/20"></div>
        <div class="anim-drift pointer-events-none absolute -right-16 top-40 -z-10 h-96 w-96 rounded-full bg-violet-400/30 blur-3xl [animation-delay:-6s] dark:bg-violet-500/20"></div>

        <div class="mx-auto grid max-w-7xl items-center gap-14 px-4 pb-20 pt-14 sm:pt-20 lg:grid-cols-12 lg:px-8 lg:pb-32 lg:pt-24">
          <div class="min-w-0 lg:col-span-6">
            <a routerLink="/docs" class="group inline-flex items-center gap-2 rounded-full border border-slate-200 bg-white/80 py-1 pl-1 pr-3 text-xs font-medium text-slate-600 shadow-sm backdrop-blur transition hover:border-brand-500 dark:border-slate-800 dark:bg-slate-900/80 dark:text-slate-300">
              <span class="rounded-full bg-linear-to-r from-blue-600 to-violet-600 px-2 py-0.5 text-white">New</span> Installable app · offline-ready · EcoCash, ZimSwitch & cards
              <app-icon name="chevron-right" size="h-3.5 w-3.5 transition group-hover:translate-x-0.5" />
            </a>
            <h1 class="mt-6 text-4xl font-extrabold leading-[1.05] tracking-tight sm:text-5xl lg:text-6xl">
              Fiscalise every invoice,<br /><span class="bg-linear-to-r from-blue-600 via-indigo-600 to-violet-600 bg-clip-text text-transparent">from any system.</span>
            </h1>
            <p class="mt-6 max-w-xl text-lg leading-8 text-slate-600 dark:text-slate-300">
              One gateway between your billing, POS or ERP and ZIMRA FDMS. Register your device once, call a simple API with a token, and get back a signed, QR-coded fiscal invoice. No certificates to manage. No servers to run.
            </p>
            <div class="mt-9 flex flex-wrap items-center gap-3">
              <a routerLink="/register" class="btn px-6 py-3 text-base shadow-lg shadow-blue-600/25">Start free <app-icon name="chevron-right" size="h-4 w-4" /></a>
              <a routerLink="/assistant" class="btn-ghost px-6 py-3 text-base"><app-icon name="message" size="h-4 w-4" />Register by chatting</a>
              <a routerLink="/docs" class="btn-ghost px-6 py-3 text-base">Read the docs</a>
              <app-install-button variant="large" />
            </div>
            <div class="mt-5"><app-store-badges /></div>
            <ul class="mt-9 grid max-w-lg grid-cols-2 gap-x-6 gap-y-2.5 text-sm text-slate-600 dark:text-slate-400">
              @for (t of trust; track t) { <li class="flex items-center gap-2"><span class="flex h-5 w-5 items-center justify-center rounded-full bg-emerald-100 text-emerald-600 dark:bg-emerald-900/40"><app-icon name="check" size="h-3 w-3" /></span>{{ t }}</li> }
            </ul>
          </div>

          <!-- product picture: the console and the phone -->
          <div class="relative lg:col-span-6" aria-hidden="true">
            <div class="relative mx-auto max-w-xl rounded-2xl border border-slate-200 bg-white shadow-2xl shadow-blue-900/10 dark:border-slate-800 dark:bg-slate-900">
              <div class="flex items-center gap-1.5 border-b border-slate-100 px-4 py-3 dark:border-slate-800">
                <span class="h-2.5 w-2.5 rounded-full bg-red-400"></span><span class="h-2.5 w-2.5 rounded-full bg-amber-400"></span><span class="h-2.5 w-2.5 rounded-full bg-emerald-400"></span>
                <span class="ml-3 flex-1 truncate rounded-md bg-slate-100 px-3 py-1 text-[11px] text-slate-500 dark:bg-slate-800">gateway.yourcompany.com/app/dashboard</span>
              </div>
              <div class="grid gap-3 p-4 sm:p-5">
                <div class="grid grid-cols-3 gap-3">
                  @for (k of kpis; track k.label) {
                    <div class="rounded-xl border border-slate-100 p-3 dark:border-slate-800"><div class="text-[10px] font-medium uppercase tracking-wide text-slate-500">{{ k.label }}</div><div class="mt-1 text-xl font-bold tabular-nums" [class]="k.cls">{{ k.value }}</div></div>
                  }
                </div>
                <div class="rounded-xl border border-slate-100 p-4 dark:border-slate-800">
                  <div class="mb-3 flex items-center justify-between text-xs"><span class="font-semibold">Receipts, last 7 days</span><span class="text-slate-500">all submitted</span></div>
                  <div class="flex h-24 items-end gap-2">
                    @for (h of bars; track $index) { <div class="flex-1 rounded-t-md bg-linear-to-t from-blue-600 to-violet-500" [style.height.%]="h"></div> }
                  </div>
                </div>
                <div class="divide-y divide-slate-100 rounded-xl border border-slate-100 text-xs dark:divide-slate-800 dark:border-slate-800">
                  @for (r of recent; track r.no) {
                    <div class="flex items-center justify-between gap-3 px-3 py-2.5"><span class="font-mono">{{ r.no }}</span><span class="tabular-nums text-slate-500">{{ r.amount }}</span><span class="badge badge-ok">Submitted</span></div>
                  }
                </div>
              </div>
            </div>

            <!-- the phone -->
            <div class="anim-float absolute -bottom-14 -right-2 w-36 rounded-[1.75rem] border-[5px] border-slate-900 bg-white p-2 shadow-2xl sm:-right-5 sm:w-40 dark:border-slate-700 dark:bg-slate-900">
              <div class="mx-auto mb-2 h-1.5 w-12 rounded-full bg-slate-900 dark:bg-slate-700"></div>
              <div class="rounded-2xl bg-linear-to-br from-emerald-500 to-teal-600 p-3 text-center text-white">
                <div class="relative mx-auto flex h-8 w-8 items-center justify-center"><span class="anim-ping absolute inset-0 rounded-full bg-white/50"></span><span class="relative flex h-8 w-8 items-center justify-center rounded-full bg-white/25"><app-icon name="check" size="h-4 w-4" /></span></div>
                <div class="mt-1.5 text-xs font-semibold">Fiscalised</div><div class="text-lg font-bold tabular-nums">USD 115.00</div>
              </div>
              <div class="mt-2.5 grid grid-cols-11 gap-px rounded-lg bg-white p-1.5 dark:bg-slate-100">
                @for (row of qr; track $index) { @for (c of row; track $index) { <span class="aspect-square" [class]="c === '1' ? 'bg-slate-900' : 'bg-white'"></span> } }
              </div>
              <div class="mt-1.5 text-center text-[9px] text-slate-500">Scan to verify on ZIMRA</div>
            </div>
          </div>
        </div>
      </section>

      <!-- ============================== works with ============================== -->
      <section class="border-y border-slate-100 bg-slate-50/70 py-8 dark:border-slate-900 dark:bg-slate-900/30">
        <div class="mx-auto max-w-7xl px-4 lg:px-8">
          <p class="text-center text-xs font-semibold uppercase tracking-widest text-slate-500">Works with anything that can make an HTTP request</p>
          <ul class="mt-5 flex flex-wrap items-center justify-center gap-x-3 gap-y-2.5">
            @for (w of works; track w) { <li class="rounded-full border border-slate-200 bg-white px-4 py-1.5 text-sm font-medium text-slate-600 shadow-sm dark:border-slate-800 dark:bg-slate-900 dark:text-slate-300">{{ w }}</li> }
          </ul>
        </div>
      </section>

      <!-- ============================== features ============================== -->
      <section id="features" class="scroll-mt-20 py-20 sm:py-28">
        <div class="mx-auto max-w-7xl px-4 lg:px-8">
          <div appReveal class="mx-auto max-w-2xl text-center">
            <p class="text-sm font-semibold uppercase tracking-widest text-brand-600">Features</p>
            <h2 class="mt-3 text-3xl font-bold tracking-tight sm:text-4xl lg:text-5xl">Everything between your system and ZIMRA</h2>
            <p class="mt-5 text-lg text-slate-600 dark:text-slate-400">The hard parts of fiscalisation are handled once, in one place, so your software only has to say what was sold.</p>
          </div>

          <div class="mt-14 grid gap-5 md:grid-cols-6">
            <div appReveal class="relative overflow-hidden rounded-3xl border border-slate-200 bg-linear-to-br from-slate-900 to-slate-800 p-7 text-white md:col-span-4 dark:border-slate-800">
              <div class="flex h-11 w-11 items-center justify-center rounded-xl bg-white/10"><app-icon name="bolt" /></div>
              <h3 class="mt-5 text-xl font-semibold">One simple API</h3>
              <p class="mt-2 max-w-md text-sm leading-6 text-slate-300">Send what was sold as JSON. The gateway numbers, signs, submits to ZIMRA and returns the fiscal invoice with its verification QR code. Retries are safe: your own reference never fiscalises twice.</p>
              <pre class="mt-5 overflow-x-auto rounded-xl bg-black/40 p-4 text-[12.5px] leading-6 text-slate-200"><code>POST /api/v1/devices/{{ '{' }}id{{ '}' }}/receipts
Authorization: Bearer fdms_••••••••

&#123; "externalRef": "ERP-2041", "invoiceNo": "INV-2041",
  "currency": "USD", "lines": [ … ] &#125;
<span class="text-emerald-400">→ 201 Submitted · QR ready</span></code></pre>
            </div>
            @for (f of features; track f.title) {
              <div appReveal class="group rounded-3xl border border-slate-200 bg-white p-7 transition hover:-translate-y-1 hover:shadow-xl dark:border-slate-800 dark:bg-slate-900" [class]="f.span">
                <div class="flex h-11 w-11 items-center justify-center rounded-xl bg-linear-to-br text-white shadow-sm" [class]="f.tone"><app-icon [name]="f.icon" /></div>
                <h3 class="mt-5 text-lg font-semibold">{{ f.title }}</h3>
                <p class="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-400">{{ f.text }}</p>
              </div>
            }
          </div>
        </div>
      </section>

      <!-- ============================== developers ============================== -->
      <section class="bg-slate-950 py-20 text-white sm:py-28">
        <div class="mx-auto grid max-w-7xl items-center gap-12 px-4 lg:grid-cols-2 lg:px-8">
          <div appReveal>
            <p class="text-sm font-semibold uppercase tracking-widest text-blue-400">For developers</p>
            <h2 class="mt-3 text-3xl font-bold tracking-tight sm:text-4xl">Integrate in an afternoon</h2>
            <p class="mt-5 text-lg text-slate-300">A clean REST API, a token per device, signed webhooks, an OpenAPI reference and examples in the language you use. Test on a demo device, then go live when you are ready.</p>
            <ul class="mt-6 space-y-3 text-slate-300">
              @for (d of dev; track d) { <li class="flex gap-3"><span class="mt-1 text-emerald-400"><app-icon name="check" size="h-4 w-4" /></span>{{ d }}</li> }
            </ul>
            <a routerLink="/docs" class="btn mt-8 px-6 py-3">Open the documentation <app-icon name="chevron-right" size="h-4 w-4" /></a>
          </div>
          <div appReveal class="min-w-0 overflow-hidden rounded-2xl border border-white/10 bg-slate-900 shadow-2xl">
            <div class="flex items-center gap-1 border-b border-white/10 bg-slate-900 px-3 py-2">
              @for (t of tabs; track t.id) { <button type="button" class="rounded-md px-3 py-1.5 text-xs font-medium transition" [class]="lang() === t.id ? 'bg-white/15 text-white' : 'text-slate-400 hover:bg-white/10 hover:text-white'" (click)="lang.set(t.id)">{{ t.label }}</button> }
            </div>
            <pre class="overflow-x-auto p-5 text-[13px] leading-6 text-slate-200"><code>{{ snippet() }}</code></pre>
          </div>
        </div>
      </section>

      <!-- ============================== how it works ============================== -->
      <section id="how" class="scroll-mt-20 py-20 sm:py-28">
        <div class="mx-auto max-w-7xl px-4 lg:px-8">
          <div appReveal class="mx-auto max-w-2xl text-center">
            <p class="text-sm font-semibold uppercase tracking-widest text-brand-600">How it works</p>
            <h2 class="mt-3 text-3xl font-bold tracking-tight sm:text-4xl lg:text-5xl">Live in four steps</h2>
          </div>
          <ol class="relative mt-14 grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
            <div class="pointer-events-none absolute left-0 right-0 top-6 hidden h-px bg-linear-to-r from-transparent via-slate-300 to-transparent lg:block dark:via-slate-700"></div>
            @for (s of steps; track s.title; let i = $index) {
              <li appReveal class="relative rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
                <span class="relative z-10 flex h-12 w-12 items-center justify-center rounded-2xl bg-linear-to-br from-blue-600 to-violet-600 text-lg font-bold text-white shadow-lg shadow-blue-600/30">{{ i + 1 }}</span>
                <h3 class="mt-5 text-lg font-semibold">{{ s.title }}</h3>
                <p class="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-400">{{ s.text }}</p>
              </li>
            }
          </ol>
        </div>
      </section>

      <!-- ============================== security ============================== -->
      <section class="border-y border-slate-100 bg-slate-50 py-20 sm:py-24 dark:border-slate-900 dark:bg-slate-900/30">
        <div class="mx-auto max-w-7xl px-4 lg:px-8">
          <div appReveal class="mx-auto max-w-2xl text-center">
            <p class="text-sm font-semibold uppercase tracking-widest text-brand-600">Security</p>
            <h2 class="mt-3 text-3xl font-bold tracking-tight sm:text-4xl">Built so a mistake cannot become a breach</h2>
          </div>
          <div class="mt-12 grid gap-5 sm:grid-cols-2 lg:grid-cols-4">
            @for (s of security; track s.title) {
              <div appReveal class="rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
                <span class="flex h-10 w-10 items-center justify-center rounded-xl bg-emerald-100 text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-300"><app-icon [name]="s.icon" /></span>
                <h3 class="mt-4 font-semibold">{{ s.title }}</h3>
                <p class="mt-2 text-sm leading-6 text-slate-600 dark:text-slate-400">{{ s.text }}</p>
              </div>
            }
          </div>
        </div>
      </section>

      <!-- ============================== install the app ============================== -->
      <section id="install" class="scroll-mt-20 py-20 sm:py-28">
        <div class="mx-auto max-w-7xl px-4 lg:px-8">
          <div class="relative overflow-hidden rounded-[2rem] border border-slate-200 bg-linear-to-br from-brand-50 via-white to-violet-50 p-8 sm:p-12 dark:border-slate-800 dark:from-slate-900 dark:via-slate-950 dark:to-slate-900">
            <div class="grid items-center gap-10 lg:grid-cols-2">
              <div appReveal>
                <p class="text-sm font-semibold uppercase tracking-widest text-brand-600">Install the app</p>
                <h2 class="mt-3 text-3xl font-bold tracking-tight sm:text-4xl">On your phone, tablet and desktop</h2>
                <p class="mt-4 text-lg text-slate-600 dark:text-slate-400">No app store needed. Install FDMS Gateway like an app: its own icon, full screen, quick shortcuts, and it opens even with a weak connection.</p>
                <div class="mt-7 flex flex-wrap items-center gap-3">
                  @if (pwa.installable()) { <app-install-button variant="large" /> }
                  @else if (pwa.installed()) { <span class="badge badge-ok px-4 py-2 text-sm">Installed on this device</span> }
                  @else { <span class="text-sm text-slate-500">Follow the steps for your device below.</span> }
                </div>
                @if (appLinks.links()?.apkPure || appLinks.links()?.playStore || appLinks.links()?.appStore) {
                  <div class="mt-6 rounded-2xl border border-slate-200 bg-white/80 p-4 dark:border-slate-800 dark:bg-slate-900/70">
                    <div class="mb-3 text-sm font-semibold">Or get the phone app</div>
                    <app-store-badges [showVersion]="true" />
                  </div>
                }
                <div class="mt-8 grid gap-3 sm:grid-cols-3">
                  @for (p of platforms; track p.name) {
                    <div class="rounded-2xl border border-slate-200 bg-white/80 p-4 dark:border-slate-800 dark:bg-slate-900/70">
                      <div class="text-sm font-semibold">{{ p.name }}</div><p class="mt-1.5 text-xs leading-5 text-slate-600 dark:text-slate-400">{{ p.how }}</p>
                    </div>
                  }
                </div>
              </div>
              <div appReveal class="relative mx-auto h-80 w-full max-w-md" aria-hidden="true">
                <div class="absolute left-0 top-6 w-56 rounded-2xl border border-slate-200 bg-white p-3 shadow-xl dark:border-slate-800 dark:bg-slate-900">
                  <div class="flex items-center gap-2 border-b border-slate-100 pb-2 dark:border-slate-800"><span class="flex h-6 w-6 items-center justify-center rounded-md bg-linear-to-br from-blue-500 to-violet-600 text-[10px] font-bold text-white">F</span><span class="text-xs font-semibold">FDMS Gateway</span></div>
                  <div class="mt-3 space-y-2">@for (w of [70, 90, 55, 80]; track $index) { <div class="h-2 rounded bg-slate-100 dark:bg-slate-800" [style.width.%]="w"></div> }</div>
                  <div class="mt-4 grid grid-cols-2 gap-2"><div class="h-14 rounded-lg bg-brand-50 dark:bg-brand-500/10"></div><div class="h-14 rounded-lg bg-emerald-50 dark:bg-emerald-900/20"></div></div>
                </div>
                <div class="anim-float absolute bottom-0 right-2 w-40 rounded-[1.75rem] border-[6px] border-slate-900 bg-white p-2 shadow-2xl dark:border-slate-700 dark:bg-slate-900">
                  <div class="grid grid-cols-3 gap-2 p-1">
                    @for (i of icons; track i.l) { <div class="text-center"><div class="mx-auto flex h-10 w-10 items-center justify-center rounded-xl bg-linear-to-br text-white" [class]="i.c">{{ i.l }}</div><div class="mt-1 truncate text-[8px] text-slate-500">{{ i.n }}</div></div> }
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      <!-- ============================== pricing ============================== -->
      <section id="pricing" class="scroll-mt-20 pb-20 sm:pb-28">
        <div class="mx-auto max-w-7xl px-4 lg:px-8">
          <div appReveal class="mx-auto max-w-2xl text-center">
            <p class="text-sm font-semibold uppercase tracking-widest text-brand-600">Pricing</p>
            <h2 class="mt-3 text-3xl font-bold tracking-tight sm:text-4xl lg:text-5xl">Simple plans that grow with you</h2>
            <p class="mt-5 text-lg text-slate-600 dark:text-slate-400">Start with a free trial on a demo device. Pay by card, ZimSwitch, EcoCash, OneMoney, PayPal or bank transfer.</p>
            <div class="mx-auto mt-7 inline-flex rounded-xl border border-slate-200 bg-white p-1 text-sm shadow-sm dark:border-slate-700 dark:bg-slate-900" role="group" aria-label="Billing period">
              <button type="button" class="rounded-lg px-5 py-1.5 font-medium transition" [class]="!yearly() ? 'bg-brand-600 text-white shadow-sm' : 'text-slate-600 dark:text-slate-300'" (click)="yearly.set(false)">Monthly</button>
              <button type="button" class="rounded-lg px-5 py-1.5 font-medium transition" [class]="yearly() ? 'bg-brand-600 text-white shadow-sm' : 'text-slate-600 dark:text-slate-300'" (click)="yearly.set(true)">Yearly @if (saving() > 0) { <span class="ml-1 rounded-full bg-emerald-100 px-1.5 py-0.5 text-[10px] font-semibold text-emerald-700">save {{ saving() }}%</span> }</button>
            </div>
          </div>

          @if (plans().length) {
            <div class="mt-14 grid gap-6 md:grid-cols-2 xl:grid-cols-3" [class.xl:grid-cols-4]="plans().length > 3">
              @for (p of plans(); track p.id) {
                <div appReveal class="relative flex flex-col rounded-3xl border bg-white p-8 dark:bg-slate-900" [class]="popular() === p.id ? 'border-brand-500 shadow-2xl shadow-blue-900/10 ring-1 ring-brand-500 xl:-my-3 xl:py-11' : 'border-slate-200 dark:border-slate-800'">
                  @if (popular() === p.id) { <span class="absolute -top-3 left-8 rounded-full bg-linear-to-r from-blue-600 to-violet-600 px-3 py-1 text-xs font-semibold text-white">Most popular</span> }
                  <h3 class="text-lg font-semibold">{{ p.name }}</h3>
                  @if (p.description) { <p class="mt-1 min-h-10 text-sm text-slate-600 dark:text-slate-400">{{ p.description }}</p> }
                  <div class="mt-6 flex items-end gap-1"><span class="text-5xl font-extrabold tracking-tight">{{ price(p) }}</span><span class="mb-2 text-sm text-slate-500">/ {{ yearly() ? 'year' : 'month' }}</span></div>
                  @if (p.trialDays > 0) { <p class="mt-1 text-xs font-medium text-emerald-600">{{ p.trialDays }}-day free trial on a demo device</p> }
                  <ul class="mt-7 flex-1 space-y-3 text-sm">
                    <li class="flex gap-2.5"><span class="text-emerald-600"><app-icon name="check" size="h-4 w-4" /></span><span><strong>{{ lim(p.maxDevices) }}</strong> fiscal {{ p.maxDevices === 1 ? 'device' : 'devices' }}</span></li>
                    <li class="flex gap-2.5"><span class="text-emerald-600"><app-icon name="check" size="h-4 w-4" /></span><span><strong>{{ lim(p.maxUsers) }}</strong> {{ p.maxUsers === 1 ? 'user' : 'users' }}</span></li>
                    <li class="flex gap-2.5"><span class="text-emerald-600"><app-icon name="check" size="h-4 w-4" /></span><span><strong>{{ lim(p.maxBranches) }}</strong> {{ p.maxBranches === 1 ? 'branch' : 'branches' }}</span></li>
                    <li class="flex gap-2.5"><span class="text-emerald-600"><app-icon name="check" size="h-4 w-4" /></span><span><strong>{{ lim(p.maxReceiptsPerMonth) }}</strong> receipts / month</span></li>
                    @for (l of usageLines(p); track l) { <li class="flex gap-2.5"><span class="text-emerald-600"><app-icon name="check" size="h-4 w-4" /></span><span>{{ l }}</span></li> }
                    @for (label of moduleLabels(p.modules); track label) { <li class="flex gap-2.5"><span class="text-emerald-600"><app-icon name="check" size="h-4 w-4" /></span><span>{{ label }}</span></li> }
                    @for (f of p.features; track f) { <li class="flex gap-2.5 text-slate-600 dark:text-slate-400"><span class="text-emerald-600"><app-icon name="check" size="h-4 w-4" /></span><span>{{ f }}</span></li> }
                  </ul>
                  <a [routerLink]="'/register'" [queryParams]="{ plan: p.id, cycle: yearly() ? 'yearly' : 'monthly' }" class="mt-8 w-full py-3" [class]="popular() === p.id ? 'btn' : 'btn-ghost'">{{ p.trialDays > 0 ? 'Start free trial' : 'Choose ' + p.name }}</a>
                </div>
              }
            </div>
          } @else if (plansError()) {
            <p class="mt-10 text-center text-sm text-slate-500">Pricing is not available right now. <a routerLink="/register" class="text-brand-600 underline">Register</a> and we will get back to you.</p>
          } @else {
            <div class="mt-14 grid animate-pulse gap-6 md:grid-cols-3"><div class="h-96 rounded-3xl bg-slate-100 dark:bg-slate-900"></div><div class="h-96 rounded-3xl bg-slate-100 dark:bg-slate-900"></div><div class="h-96 rounded-3xl bg-slate-100 dark:bg-slate-900"></div></div>
          }

          <div appReveal class="mx-auto mt-14 max-w-4xl text-center">
            <p class="text-sm font-medium text-slate-500">Pay the way you already do</p>
            <ul class="mt-4 flex flex-wrap justify-center gap-2.5">
              @for (m of methods; track m) { <li class="rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold shadow-sm dark:border-slate-700 dark:bg-slate-900">{{ m }}</li> }
            </ul>
          </div>
        </div>
      </section>

      <!-- ============================== faq ============================== -->
      <section id="faq" class="scroll-mt-20 border-t border-slate-100 bg-slate-50/70 py-20 sm:py-28 dark:border-slate-900 dark:bg-slate-900/20">
        <div class="mx-auto max-w-3xl px-4 lg:px-8">
          <h2 appReveal class="text-center text-3xl font-bold tracking-tight sm:text-4xl">Questions, answered</h2>
          <div appReveal class="mt-10 divide-y divide-slate-200 overflow-hidden rounded-2xl border border-slate-200 bg-white dark:divide-slate-800 dark:border-slate-800 dark:bg-slate-900">
            @for (q of faq; track q.q) {
              <details class="group p-5 open:bg-slate-50/60 dark:open:bg-slate-800/30">
                <summary class="flex cursor-pointer list-none items-center justify-between gap-4 font-medium">{{ q.q }}<app-icon name="chevron" size="h-4 w-4 shrink-0 transition group-open:rotate-180" /></summary>
                <p class="mt-3 text-sm leading-6 text-slate-600 dark:text-slate-400">{{ q.a }}</p>
              </details>
            }
          </div>
        </div>
      </section>

      <!-- ============================== call to action ============================== -->
      <section class="px-4 py-20 lg:px-8 lg:py-28">
        <div appReveal class="relative mx-auto max-w-5xl overflow-hidden rounded-[2rem] bg-linear-to-br from-blue-600 via-indigo-600 to-violet-700 px-6 py-14 text-center text-white shadow-2xl shadow-blue-900/30 sm:px-12">
          <div class="anim-drift pointer-events-none absolute -right-10 -top-10 h-56 w-56 rounded-full bg-white/10 blur-2xl"></div>
          <h2 class="relative text-3xl font-bold tracking-tight sm:text-4xl">Ready to fiscalise?</h2>
          <p class="relative mx-auto mt-4 max-w-xl text-blue-100">Register your company. Once approved, add a demo device, create an API token and send your first invoice the same day.</p>
          <div class="relative mt-8 flex flex-wrap justify-center gap-3">
            <a routerLink="/register" class="inline-flex items-center justify-center rounded-xl bg-white px-6 py-3 text-sm font-semibold text-blue-700 shadow hover:bg-blue-50">Register your company</a>
            <a routerLink="/docs" class="inline-flex items-center justify-center rounded-xl border border-white/40 px-6 py-3 text-sm font-semibold text-white hover:bg-white/10">Developer documentation</a>
          </div>
        </div>
      </section>

      <app-assistant-launcher />
      <app-public-footer [name]="info()?.name ?? 'FiscalHub'" [email]="info()?.email" [phone]="info()?.phone" />
    </div>
  `,
})
export class LandingComponent implements OnInit {
  readonly moduleLabels = planModuleLabels;
  readonly usageLines = usagePricingLines;
  readonly appLinks = inject(AppLinksStore);
  private readonly api = inject(Api);
  readonly pwa = inject(PwaService);

  readonly plans = signal<Plan[]>([]);
  readonly plansError = signal(false);
  readonly info = signal<PublicInfo | null>(null);
  readonly yearly = signal(false);
  readonly lang = signal<Lang>('curl');
  /** the middle plan of three (or the second of any list) is highlighted */
  readonly popular = computed(() => { const p = this.plans(); return p.length >= 2 ? p[1].id : p[0]?.id; });
  readonly saving = computed(() => {
    const p = this.plans().find(x => x.priceMonthly > 0);
    return p ? Math.max(0, Math.round((1 - p.priceYearly / (p.priceMonthly * 12)) * 100)) : 0;
  });

  readonly lim = limit;
  price(p: Plan): string { const v = this.yearly() ? p.priceYearly : p.priceMonthly; return v === 0 ? 'Free' : money(v, p.currency); }

  readonly trust = ['No software to install', 'Works with any system', 'Signed, QR-coded invoices', 'Offline mode included'];
  readonly works = ['ERP', 'POS', 'E-commerce', 'Accounting', 'Billing', 'Mobile apps', 'Excel', 'PDF invoices', 'Custom software'];

  readonly kpis = [{ label: 'Today', value: '128', cls: '' }, { label: 'Submitted', value: '128', cls: 'text-emerald-600' }, { label: 'Pending', value: '0', cls: 'text-slate-400' }];
  readonly bars = [38, 52, 44, 68, 59, 81, 92];
  readonly recent = [{ no: 'INV-2041', amount: 'USD 115.00' }, { no: 'INV-2040', amount: 'USD 42.50' }, { no: 'INV-2039', amount: 'ZWG 1,280.00' }];
  /** a decorative QR-like pattern (not a real code) */
  readonly qr = ['11111110101', '10000010010', '10111010111', '10111011001', '10111010101', '10000010110', '11111110101', '00000000010', '10110101101', '01001110010', '11010011101'].map(r => r.split(''));

  readonly features = [
    { icon: 'key', tone: 'from-violet-500 to-purple-500', span: 'md:col-span-2', title: 'A token per device', text: 'Every integration gets its own revocable token that works for one fiscal device only, with exactly the permissions it needs.' },
    { icon: 'cloud-upload', tone: 'from-amber-500 to-orange-500', span: 'md:col-span-2', title: 'Offline mode', text: 'If ZIMRA cannot be reached, invoices are issued offline and uploaded automatically, in the order the rules require.' },
    { icon: 'building', tone: 'from-emerald-500 to-teal-500', span: 'md:col-span-2', title: 'Branches and teams', text: 'Group tills by branch and limit each person to the branches they work in, with fully configurable roles and menus.' },
    { icon: 'receipt', tone: 'from-rose-500 to-pink-500', span: 'md:col-span-3', title: 'Your invoice, your look', text: 'Printable invoice PDFs with your logo, colours, bank details and wording, per company and per branch.' },
    { icon: 'upload', tone: 'from-sky-500 to-blue-600', span: 'md:col-span-3', title: 'Import and webhooks', text: 'Fiscalise from Excel or PDF when there is no integration, and get signed webhooks the moment a receipt changes state.' },
  ];

  readonly dev = ['Per-device bearer tokens with scoped permissions', 'Idempotent: retry with the same reference, never a duplicate', 'Signed webhooks with automatic retries', 'OpenAPI reference and a Swagger playground'];
  readonly tabs: { id: Lang; label: string }[] = [{ id: 'curl', label: 'cURL' }, { id: 'csharp', label: 'C#' }, { id: 'js', label: 'JavaScript' }, { id: 'python', label: 'Python' }];
  private readonly snippets: Record<Lang, string> = {
    curl: `curl -X POST "$GATEWAY/api/v1/devices/$DEVICE/receipts" \\
  -H "Authorization: Bearer $TOKEN" \\
  -H "Content-Type: application/json" \\
  -d '{ "externalRef": "ERP-2041", "invoiceNo": "INV-2041",
        "receiptType": "FiscalInvoice", "currency": "USD",
        "linesTaxInclusive": true, "autoOpenDay": true,
        "lines": [{ "name": "Bread 700g", "hsCode": "19059010",
                    "quantity": 10, "unitPrice": 11.5, "taxId": 1 }] }'`,
    csharp: `var http = new HttpClient { BaseAddress = new Uri(gateway) };
http.DefaultRequestHeaders.Authorization = new("Bearer", token);

var res = await http.PostAsJsonAsync($"api/v1/devices/{deviceId}/receipts", new {
    externalRef = sale.Id.ToString(), invoiceNo = sale.Number,
    receiptType = "FiscalInvoice", currency = "USD", autoOpenDay = true,
    lines = sale.Lines.Select(l => new { name = l.Name, hsCode = l.Hs, quantity = l.Qty, unitPrice = l.Price, taxId = 1 }),
});
var receipt = await res.Content.ReadFromJsonAsync<JsonElement>();`,
    js: `const res = await fetch(\`\${GATEWAY}/api/v1/devices/\${deviceId}/receipts\`, {
  method: 'POST',
  headers: { Authorization: \`Bearer \${token}\`, 'Content-Type': 'application/json' },
  body: JSON.stringify({
    externalRef: sale.id, invoiceNo: sale.number,
    receiptType: 'FiscalInvoice', currency: 'USD', autoOpenDay: true,
    lines: sale.lines.map(l => ({ name: l.name, hsCode: l.hs, quantity: l.qty, unitPrice: l.price, taxId: 1 })),
  }),
});
const receipt = await res.json();`,
    python: `import requests

r = requests.post(f"{GATEWAY}/api/v1/devices/{device_id}/receipts",
    headers={"Authorization": f"Bearer {token}"},
    json={"externalRef": sale["id"], "invoiceNo": sale["number"],
          "receiptType": "FiscalInvoice", "currency": "USD", "autoOpenDay": True,
          "lines": [{"name": l["name"], "hsCode": l["hs"], "quantity": l["qty"],
                     "unitPrice": l["price"], "taxId": 1} for l in sale["lines"]]})
receipt = r.json()`,
  };
  readonly snippet = computed(() => this.snippets[this.lang()]);

  readonly steps = [
    { title: 'Register', text: 'Tell us about your company and choose a plan. We review it and approve your account.' },
    { title: 'Add your device', text: 'Enter the device id and activation key ZIMRA gave you. The gateway registers it and keeps its certificate safe.' },
    { title: 'Create a token', text: 'Create an API token for that device, with only the permissions your system needs, and paste it into your software.' },
    { title: 'Send invoices', text: 'Post each sale to the API. You get the fiscal invoice and QR code back, and see everything in the console.' },
  ];

  readonly security = [
    { icon: 'key', title: 'Tokens that cannot roam', text: 'A token works for one device only. Revoke it any time; it stops working at once.' },
    { icon: 'lock', title: 'Keys stay encrypted', text: 'Device keys, gateway credentials and webhook secrets are encrypted at rest and never shown again.' },
    { icon: 'shield', title: 'Company isolation', text: 'Every company, user, branch and token is separated. Nobody sees another company’s data.' },
    { icon: 'clipboard', title: 'Full audit trail', text: 'Who did what, when and from where is recorded, and only the platform operator can read it.' },
  ];

  readonly platforms = [
    { name: 'Android', how: 'Open in Chrome and tap Install app (or the menu, then Install app).' },
    { name: 'iPhone / iPad', how: 'Open in Safari, tap Share, then Add to Home Screen.' },
    { name: 'Windows / Mac', how: 'In Chrome or Edge, click the install icon in the address bar.' },
  ];
  readonly icons = [
    { l: 'F', n: 'FDMS', c: 'from-blue-500 to-violet-600' }, { l: '▦', n: 'Camera', c: 'from-slate-400 to-slate-600' }, { l: '✉', n: 'Mail', c: 'from-sky-400 to-blue-500' },
    { l: '☼', n: 'Weather', c: 'from-amber-400 to-orange-500' }, { l: '♫', n: 'Music', c: 'from-rose-400 to-pink-600' }, { l: '⌂', n: 'Home', c: 'from-emerald-400 to-teal-600' },
  ];

  readonly methods = ['Visa', 'Mastercard', 'ZimSwitch', 'EcoCash', 'OneMoney', 'InnBucks', 'PayPal', 'Bank transfer'];

  readonly faq = [
    { q: 'Do I need to change my billing software?', a: 'No. If your software can make an HTTP request, it can use the gateway. If it cannot, you can upload an Excel or PDF file, or type invoices straight into the console.' },
    { q: 'What do I need to get started?', a: 'A fiscal device registered with ZIMRA (its device id and activation key), and an approved account here. Registration takes a few minutes; approval is done by our team.' },
    { q: 'What is the difference between demo and live?', a: 'ZIMRA FDMS has a demo (test) environment and a live one. You test on a demo device first, free during your trial. When you are ready, pay for your plan and send a go-live request: we switch on live devices.' },
    { q: 'What if the internet or ZIMRA is down?', a: 'The gateway can issue invoices in offline mode and uploads them when the connection returns, so your till is never blocked.' },
    { q: 'Can I install it like an app?', a: 'Yes. FDMS Gateway installs on Android, iPhone, iPad, Windows and Mac straight from the browser, with its own icon and full-screen window. No app store required.' },
    { q: 'How do I pay?', a: 'By card (Visa, Mastercard), ZimSwitch or mobile wallet (EcoCash, OneMoney) through Paynow or EcoCash directly, by PayPal, or by cash or bank transfer, which we confirm and activate with a code.' },
  ];

  async ngOnInit(): Promise<void> {
    try { this.info.set(await this.api.get<PublicInfo>('/api/v1/public/info')); } catch { /* the footer falls back to the product name */ }
    try { this.plans.set(await this.api.get<Plan[]>('/api/v1/public/plans')); } catch { this.plansError.set(true); }
  }
}
