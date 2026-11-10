import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { Plan } from '../core/models';
import { IconComponent } from '../shared/ui.components';
import { AssistantLauncherComponent } from './assistant.component';
import { LogoCropperComponent } from '../shared/logo-cropper.component';
import { PublicFooterComponent, PublicHeaderComponent, limit, money } from '../shared/public.components';

interface Step { title: string; hint: string; icon: string; }

/**
 * Company onboarding as a four-step wizard: the company, the person and their sign-in, the plan, then a review before sending.
 * Each step is checked before the next opens. Then the email is confirmed with a 6-digit code, and (with self-service sign-up) the company opens at once:
 * a free demo with its first invoice ready to pay. Without self-service, the registration waits for an administrator.
 */
@Component({
  selector: 'app-register',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, RouterLink, IconComponent, LogoCropperComponent, PublicHeaderComponent, PublicFooterComponent, AssistantLauncherComponent],
  template: `
    <div class="min-h-screen bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      <app-public-header />

      <main class="mx-auto max-w-5xl px-4 py-8 lg:px-8 lg:py-12">
        @if (phase() === 'verify') {
          <div class="mx-auto max-w-md rounded-2xl border border-slate-200 bg-white p-8 shadow-sm dark:border-slate-800 dark:bg-slate-900">
            <div class="mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-brand-50 text-brand-600 dark:bg-brand-500/15"><app-icon name="mail" size="h-7 w-7" /></div>
            <h1 class="mt-4 text-center text-2xl font-semibold tracking-tight">Confirm your email</h1>
            <p class="mt-2 text-center text-sm text-slate-600 dark:text-slate-400">We sent a 6-digit code to <strong>{{ email || 'your email' }}</strong>. Enter it below. It works for 15 minutes.</p>
            <form class="mt-6 space-y-4" (ngSubmit)="verify()">
              @if (!email) { <label class="block"><span class="label">Email you registered with</span><input class="input" name="vemail" type="email" [(ngModel)]="email" autocomplete="email" required /></label> }
              <input class="input py-3 text-center font-mono text-2xl tracking-[0.5em]" name="vcode" [(ngModel)]="code" inputmode="numeric" maxlength="6" autocomplete="one-time-code" placeholder="000000" autofocus required aria-label="6-digit code" />
              @if (error()) { <p class="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
              @if (note()) { <p class="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-200">{{ note() }}</p> }
              <button class="btn w-full justify-center py-3 text-base" type="submit" [disabled]="busy() || code.trim().length !== 6 || !email.trim()">{{ busy() ? 'Checking…' : 'Confirm and open my account' }}</button>
            </form>
            <div class="mt-4 flex flex-wrap items-center justify-between gap-2 text-sm">
              <button type="button" class="font-medium text-brand-600 disabled:text-slate-400" [disabled]="cooldown() > 0 || busy() || !email.trim()" (click)="resend()">{{ cooldown() > 0 ? 'Send a new code in ' + cooldown() + ' s' : 'Send a new code' }}</button>
              <button type="button" class="text-slate-500 underline" (click)="phase.set('form'); error.set('')">Change my details</button>
            </div>
            <p class="mt-4 text-center text-xs text-slate-500">No email? Check the spam folder.</p>
          </div>
        } @else if (phase() === 'ready') {
          <div class="mx-auto max-w-2xl rounded-2xl border border-slate-200 bg-white p-8 text-center shadow-sm dark:border-slate-800 dark:bg-slate-900 sm:p-10">
            <div class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-emerald-100 text-emerald-600 dark:bg-emerald-900/40"><app-icon name="check" size="h-8 w-8" /></div>
            <h1 class="mt-5 text-2xl font-semibold tracking-tight">{{ company || 'Your company' }} is ready</h1>
            <p class="mt-2 text-sm text-slate-600 dark:text-slate-400">Your email is confirmed. Sign in as <strong>{{ ready()?.signInName }}</strong>.</p>
            <div class="mx-auto mt-6 grid max-w-lg gap-3 text-left sm:grid-cols-2">
              <div class="rounded-xl bg-sky-50 p-4 dark:bg-sky-900/20"><div class="text-xs font-semibold uppercase tracking-wide text-sky-700 dark:text-sky-300">Free demo</div>
                <div class="mt-1 text-sm">Try everything with ZIMRA's test environment{{ ready()?.trialEndsAt ? ' until ' + (ready()!.trialEndsAt | date:'d MMM y') : '' }}.</div></div>
              <div class="rounded-xl bg-amber-50 p-4 dark:bg-amber-900/20"><div class="text-xs font-semibold uppercase tracking-wide text-amber-700 dark:text-amber-300">Your invoice</div>
                <div class="mt-1 text-sm">{{ ready()?.invoiceNumber ? ready()!.invoiceNumber + ' is under Billing.' : 'Choose a plan under Billing.' }} Pay when you are ready to go live; the paid period starts when the demo ends.</div></div>
            </div>
            <p class="mx-auto mt-4 max-w-lg text-xs text-slate-500">A demo that is still unpaid a week after it ends is deleted with its data.</p>
            <div class="mt-6 flex flex-wrap justify-center gap-3">
              @if (signedIn()) { <a routerLink="/" class="btn px-6">Start my demo</a><a routerLink="/billing" class="btn-ghost">Pay now</a> }
              @else { <a routerLink="/login" class="btn px-6">Sign in</a> }
            </div>
          </div>
        } @else if (done()) {
          <div class="mx-auto max-w-2xl rounded-2xl border border-slate-200 bg-white p-8 shadow-sm dark:border-slate-800 dark:bg-slate-900 sm:p-10">
            <div class="mx-auto flex h-16 w-16 items-center justify-center rounded-full bg-emerald-100 text-emerald-600 dark:bg-emerald-900/40"><app-icon name="check" size="h-8 w-8" /></div>
            <h1 class="mt-5 text-center text-2xl font-semibold tracking-tight">Thank you, we have your registration</h1>
            <p class="mt-2 text-center text-sm text-slate-600 dark:text-slate-400"><strong>{{ company }}</strong> is now waiting for review. This is what happens next.</p>
            <ol class="mt-8 space-y-0">
              @for (n of whatNext; track n.title; let last = $last) {
                <li class="relative flex gap-4 pb-6">
                  @if (!last) { <span class="absolute left-[15px] top-8 h-full w-px bg-slate-200 dark:bg-slate-700" aria-hidden="true"></span> }
                  <span class="relative z-10 flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-sm font-semibold" [class]="n.done ? 'bg-emerald-600 text-white' : 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300'">
                    @if (n.done) { <app-icon name="check" size="h-4 w-4" /> } @else { {{ n.no }} }
                  </span>
                  <div><div class="font-medium">{{ n.title }}</div><p class="text-sm text-slate-600 dark:text-slate-400">{{ n.text }}</p></div>
                </li>
              }
            </ol>
            <div class="mt-2 flex flex-wrap justify-center gap-3"><a routerLink="/login" class="btn">Go to sign in</a><a routerLink="/docs" class="btn-ghost">Read the documentation</a></div>
          </div>
        } @else {
          <div class="mb-8 text-center">
            <h1 class="text-3xl font-bold tracking-tight">Register your company</h1>
            <p class="mx-auto mt-2 max-w-xl text-slate-600 dark:text-slate-400">Four short steps, then confirm your email with the code we send and start your free demo straight away.</p>
          </div>

          <!-- progress -->
          <nav aria-label="Registration steps" class="mx-auto mb-8 max-w-3xl">
            <ol class="hidden items-start sm:flex">
              @for (s of steps; track s.title; let i = $index; let last = $last) {
                <li class="flex flex-1 items-start" [class.flex-none]="last">
                  <button type="button" class="group flex flex-col items-center gap-2 text-center" [disabled]="i > furthest()" (click)="goTo(i)" [attr.aria-current]="i === step() ? 'step' : null">
                    <span class="flex h-10 w-10 items-center justify-center rounded-full border-2 text-sm font-semibold transition"
                      [class]="i < step() || (i <= furthest() && i !== step()) ? 'border-brand-600 bg-brand-600 text-white' : i === step() ? 'border-brand-600 bg-white text-brand-600 ring-4 ring-brand-100 dark:bg-slate-900 dark:ring-brand-500/20' : 'border-slate-300 bg-white text-slate-400 dark:border-slate-700 dark:bg-slate-900'">
                      @if (i < step() || (i <= furthest() && i !== step() && stepOk(i))) { <app-icon name="check" size="h-5 w-5" /> } @else { <app-icon [name]="s.icon" size="h-5 w-5" /> }
                    </span>
                    <span class="w-28"><span class="block text-sm font-semibold" [class]="i === step() ? 'text-slate-900 dark:text-white' : 'text-slate-500'">{{ s.title }}</span><span class="block text-xs text-slate-400">{{ s.hint }}</span></span>
                  </button>
                  @if (!last) { <span class="mx-2 mt-5 h-0.5 flex-1 rounded transition" [class]="i < step() ? 'bg-brand-600' : 'bg-slate-200 dark:bg-slate-700'" aria-hidden="true"></span> }
                </li>
              }
            </ol>
            <!-- phones: a compact bar -->
            <div class="sm:hidden">
              <div class="flex items-center justify-between text-sm"><span class="font-semibold">{{ steps[step()].title }}</span><span class="text-slate-500">Step {{ step() + 1 }} of {{ steps.length }}</span></div>
              <div class="mt-2 h-2 overflow-hidden rounded-full bg-slate-200 dark:bg-slate-800"><div class="h-full rounded-full bg-brand-600 transition-all" [style.width.%]="(step() + 1) / steps.length * 100"></div></div>
            </div>
          </nav>

          <div class="grid gap-8 lg:grid-cols-3">
            <form class="card p-6 sm:p-8 lg:col-span-2" (ngSubmit)="onEnter()" autocomplete="on" novalidate>
              @switch (step()) {
                @case (0) {
                  <h2 class="text-xl font-semibold">About your company</h2>
                  <p class="mt-1 text-sm text-slate-500">The business that will be fiscalising invoices.</p>
                  <div class="mt-6 grid gap-4 sm:grid-cols-2">
                    <label class="block sm:col-span-2"><span class="label">Company name *</span>
                      <input class="input" name="company" [(ngModel)]="company" maxlength="150" autocomplete="organization" placeholder="Acme Trading (Pvt) Ltd" [class.border-red-400]="tried() && !company.trim()" />
                      @if (tried() && !company.trim()) { <span class="mt-1 block text-xs text-red-600">Enter your company name.</span> }</label>
                    <label class="block"><span class="label">TIN (tax number)</span>
                      <input class="input" name="tin" [(ngModel)]="tin" maxlength="30" inputmode="numeric" placeholder="10 digits" />
                      <span class="hint">Optional now; ZIMRA needs it when you register a device.</span></label>
                    <label class="block"><span class="label">Company phone</span>
                      <input class="input" name="phone" [(ngModel)]="phone" maxlength="40" type="tel" autocomplete="tel" placeholder="0771234567" /></label>
                    <label class="block"><span class="label">Referral code (optional)</span>
                      <input class="input font-mono uppercase" name="ref" [ngModel]="partnerCode()" (ngModelChange)="partnerCode.set(($event ?? '').toUpperCase().trim())" maxlength="20" placeholder="REF…" />
                      <span class="hint">The code of the company or partner that sent you.</span></label>
                    <div class="sm:col-span-2">
                      <span class="label">Company logo (optional)</span>
                      <div class="flex flex-wrap items-center gap-4">
                        <div class="flex h-20 w-40 items-center justify-center overflow-hidden rounded-xl border border-dashed border-slate-300 bg-slate-50 dark:border-slate-700 dark:bg-slate-900">
                          @if (logoUrl(); as u) { <img [src]="u" alt="Your logo" class="max-h-full max-w-full object-contain" /> } @else { <span class="text-xs text-slate-400">No logo yet</span> }
                        </div>
                        <div class="space-y-2">
                          <div class="flex gap-2"><button type="button" class="btn-ghost btn-sm" (click)="cropping.set(true)"><app-icon name="upload" size="h-4 w-4" />{{ logoUrl() ? 'Change logo' : 'Add logo' }}</button>
                            @if (logoUrl()) { <button type="button" class="btn-ghost btn-sm" (click)="clearLogo()">Remove</button> }</div>
                          <p class="hint mt-0">Printed on your invoices. Any picture works: you crop and resize it next.</p>
                        </div>
                      </div>
                    </div>
                  </div>
                }
                @case (1) {
                  <h2 class="text-xl font-semibold">You and your sign-in</h2>
                  <p class="mt-1 text-sm text-slate-500">You become the company administrator. You can add colleagues later.</p>
                  <div class="mt-6 grid gap-4 sm:grid-cols-2">
                    <label class="block"><span class="label">First name *</span>
                      <input class="input" name="first" [(ngModel)]="first" maxlength="100" autocomplete="given-name" [class.border-red-400]="tried() && !first.trim()" />
                      @if (tried() && !first.trim()) { <span class="mt-1 block text-xs text-red-600">Enter your first name.</span> }</label>
                    <label class="block"><span class="label">Surname</span><input class="input" name="last" [(ngModel)]="last" maxlength="100" autocomplete="family-name" /></label>
                    <label class="block"><span class="label">Email *</span>
                      <input class="input" name="email" type="email" [(ngModel)]="email" maxlength="200" autocomplete="email" placeholder="you@company.co.zw" [class.border-red-400]="tried() && !emailOk()" />
                      @if (tried() && !emailOk()) { <span class="mt-1 block text-xs text-red-600">Enter a valid email address.</span> }</label>
                    <label class="block"><span class="label">User name <span class="font-normal text-slate-400">(optional)</span></span>
                      <input class="input" name="username" [(ngModel)]="userName" maxlength="32" autocomplete="username" placeholder="e.g. Inno" [class.border-red-400]="tried() && !userNameOk()" />
                      @if (tried() && !userNameOk()) { <span class="mt-1 block text-xs text-red-600">3 to 32 letters or digits (dots, dashes, underscores allowed), no spaces or &#64;.</span> }
                      @else { <span class="hint">Sign in with this instead of your email.</span> }</label>
                    <label class="block"><span class="label">Password *</span>
                      <input class="input" name="pw" [type]="show() ? 'text' : 'password'" [(ngModel)]="password" autocomplete="new-password" /></label>
                    <label class="block"><span class="label">Repeat password *</span>
                      <input class="input" name="pw2" [type]="show() ? 'text' : 'password'" [(ngModel)]="password2" autocomplete="new-password" [class.border-red-400]="tried() && password !== password2" />
                      @if (tried() && password !== password2) { <span class="mt-1 block text-xs text-red-600">The two passwords do not match.</span> }</label>
                    <div class="sm:col-span-2">
                      <div class="flex items-center gap-3">
                        <div class="flex flex-1 gap-1" aria-hidden="true">@for (i of [1,2,3,4]; track i) { <span class="h-1.5 flex-1 rounded-full" [class]="i <= strength() ? strengthColor() : 'bg-slate-200 dark:bg-slate-700'"></span> }</div>
                        <span class="w-14 text-right text-xs font-medium text-slate-500">{{ password ? strengthLabel() : '' }}</span>
                        <button type="button" class="text-xs font-medium text-brand-600 underline" (click)="show.set(!show())">{{ show() ? 'Hide' : 'Show' }}</button>
                      </div>
                      <ul class="mt-3 grid gap-1 text-xs sm:grid-cols-2">
                        @for (r of rules(); track r.text) {
                          <li class="flex items-center gap-1.5" [class]="r.ok ? 'text-emerald-600' : 'text-slate-500'"><app-icon [name]="r.ok ? 'check' : 'circle'" size="h-3.5 w-3.5" />{{ r.text }}</li>
                        }
                      </ul>
                    </div>
                  </div>
                }
                @case (2) {
                  <h2 class="text-xl font-semibold">Choose your plan</h2>
                  <p class="mt-1 text-sm text-slate-500">You can change it later. Prices are shown for the billing period you pick.</p>
                  <div class="mt-5 inline-flex rounded-xl border border-slate-200 bg-slate-100 p-1 text-sm dark:border-slate-700 dark:bg-slate-800" role="group" aria-label="Billing period">
                    <button type="button" class="rounded-lg px-4 py-1.5 font-medium transition" [class]="cycle === 'monthly' ? 'bg-white shadow-sm dark:bg-slate-900' : 'text-slate-500'" (click)="cycle = 'monthly'">Monthly</button>
                    <button type="button" class="rounded-lg px-4 py-1.5 font-medium transition" [class]="cycle === 'yearly' ? 'bg-white shadow-sm dark:bg-slate-900' : 'text-slate-500'" (click)="cycle = 'yearly'">Yearly <span class="ml-1 text-xs text-emerald-600">cheaper</span></button>
                  </div>
                  <div class="mt-5 grid gap-3 sm:grid-cols-2" role="radiogroup" aria-label="Plan">
                    @for (p of plans(); track p.id) {
                      <button type="button" role="radio" [attr.aria-checked]="planId() === p.id" (click)="planId.set(p.id)"
                        class="relative rounded-2xl border-2 p-4 text-left transition focus:outline-none focus-visible:ring-2 focus-visible:ring-brand-500"
                        [class]="planId() === p.id ? 'border-brand-600 bg-brand-50/60 dark:bg-brand-500/10' : 'border-slate-200 hover:border-slate-300 dark:border-slate-700 dark:hover:border-slate-600'">
                        @if (planId() === p.id) { <span class="absolute right-3 top-3 flex h-6 w-6 items-center justify-center rounded-full bg-brand-600 text-white"><app-icon name="check" size="h-4 w-4" /></span> }
                        <div class="font-semibold">{{ p.name }}</div>
                        <div class="mt-1 text-2xl font-bold">{{ priceOf(p) }}<span class="text-sm font-normal text-slate-500"> / {{ cycle === 'yearly' ? 'yr' : 'mo' }}</span></div>
                        @if (p.trialDays > 0) { <div class="mt-1 text-xs font-medium text-emerald-600">{{ p.trialDays }}-day free trial</div> }
                        @if (p.description) { <p class="mt-2 text-xs text-slate-600 dark:text-slate-400">{{ p.description }}</p> }
                        <p class="mt-2 text-xs text-slate-500">{{ lim(p.maxDevices) }} devices · {{ lim(p.maxUsers) }} users · {{ lim(p.maxBranches) }} branches</p>
                      </button>
                    }
                  </div>
                  <button type="button" class="mt-3 text-sm text-slate-500 underline" (click)="planId.set(null)" [class.font-semibold]="planId() === null">Not sure yet, recommend one for me</button>
                }
                @case (3) {
                  <h2 class="text-xl font-semibold">Review and send</h2>
                  <p class="mt-1 text-sm text-slate-500">Check your details. Use Edit to change anything.</p>
                  <div class="mt-6 space-y-4">
                    @for (g of review(); track g.title; let i = $index) {
                      <section class="rounded-xl border border-slate-200 p-4 dark:border-slate-700">
                        <div class="mb-2 flex items-center justify-between"><h3 class="text-sm font-semibold uppercase tracking-wide text-slate-500">{{ g.title }}</h3>
                          <button type="button" class="inline-flex items-center gap-1 text-sm font-medium text-brand-600" (click)="goTo(g.step)"><app-icon name="edit" size="h-3.5 w-3.5" />Edit</button></div>
                        @if (g.step === 0 && logoUrl(); as u) { <img [src]="u" alt="Your logo" class="mb-3 h-12 max-w-40 object-contain" /> }
                        <dl class="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
                          @for (row of g.rows; track row[0]) { <div><dt class="text-xs text-slate-500">{{ row[0] }}</dt><dd class="break-words font-medium">{{ row[1] || '—' }}</dd></div> }
                        </dl>
                      </section>
                    }
                    <label class="block"><span class="label">Anything we should know? (optional)</span>
                      <textarea class="input" name="msg" rows="3" [(ngModel)]="message" maxlength="1000" placeholder="Number of tills and branches, the system you want to connect…"></textarea></label>
                    <label class="flex items-start gap-3 text-sm"><input type="checkbox" class="mt-1 h-4 w-4 rounded border-slate-300" name="agree" [(ngModel)]="agree" />
                      <span class="text-slate-700 dark:text-slate-300">I confirm these details are correct, that I am allowed to register this company, and I accept the <a routerLink="/terms" target="_blank" class="text-brand-600 underline">terms and conditions</a> and <a routerLink="/privacy" target="_blank" class="text-brand-600 underline">privacy policy</a>.</span></label>
                  </div>
                }
              }

              <!-- honeypot: invisible to people, tempting to bots. Anything typed here makes the server drop the request. -->
              <div class="absolute -left-[9999px] h-0 w-0 overflow-hidden" aria-hidden="true"><label>Website <input name="website" tabindex="-1" autocomplete="off" [(ngModel)]="website" /></label></div>

              @if (error()) { <p class="mt-5 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }

              <div class="mt-8 flex items-center justify-between gap-3 border-t border-slate-100 pt-5 dark:border-slate-800">
                @if (step() > 0) { <button type="button" class="btn-ghost" (click)="back()"><app-icon name="arrow-left" size="h-4 w-4" />Back</button> } @else { <a routerLink="/login" class="text-sm text-slate-500 underline">Already registered? Sign in</a> }
                @if (step() < steps.length - 1) {
                  <button type="button" class="btn px-6" (click)="next()">Continue<app-icon name="chevron-right" size="h-4 w-4" /></button>
                } @else {
                  <button type="submit" class="btn px-6" [disabled]="busy() || !agree">{{ busy() ? 'Sending…' : 'Submit registration' }}</button>
                }
              </div>
            </form>

            <aside class="space-y-5">
              @if (selected(); as p) {
                <div class="rounded-2xl border border-brand-100 bg-brand-50/70 p-5 dark:border-brand-500/30 dark:bg-brand-500/10">
                  <div class="text-xs font-semibold uppercase tracking-wide text-brand-700 dark:text-brand-100">Your plan</div>
                  <div class="mt-1 text-lg font-semibold">{{ p.name }}</div>
                  <div class="text-2xl font-bold">{{ priceOf(p) }} <span class="text-sm font-normal text-slate-500">/ {{ cycle === 'yearly' ? 'year' : 'month' }}</span></div>
                  @if (p.trialDays > 0) { <p class="mt-1 text-xs font-medium text-emerald-600">{{ p.trialDays }}-day free trial, no payment now</p> }
                </div>
              }
              <div class="rounded-2xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
                <div class="mb-3 flex items-center gap-2 text-sm font-semibold"><app-icon name="shield" size="h-4 w-4" />Why register</div>
                <ul class="space-y-3 text-sm">
                  @for (s of points; track s) { <li class="flex gap-2.5"><span class="mt-0.5 text-emerald-600"><app-icon name="check" size="h-4 w-4" /></span><span class="text-slate-700 dark:text-slate-300">{{ s }}</span></li> }
                </ul>
              </div>
            </aside>
          </div>
        }
      </main>

      @if (cropping()) { <app-logo-cropper title="Your company logo" (done)="logoReady($event)" (closed)="cropping.set(false)" /> }

      <app-assistant-launcher />
      <app-public-footer />
    </div>
  `,
})
export class RegisterComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  /** form, then verify (enter the emailed code), then ready (account open) — or done (waiting for the operator). */
  readonly phase = signal<'form' | 'verify' | 'ready'>('form');
  readonly ready = signal<{ signInName?: string; trialEndsAt?: string; invoiceNumber?: string } | null>(null);
  readonly signedIn = signal(false);
  readonly note = signal('');
  readonly cooldown = signal(0);
  private timer: ReturnType<typeof setInterval> | undefined;
  code = ''; userName = '';

  readonly steps: Step[] = [
    { title: 'Company', hint: 'Who you are', icon: 'building' },
    { title: 'Your account', hint: 'Sign-in details', icon: 'user-check' },
    { title: 'Plan', hint: 'What you need', icon: 'layers' },
    { title: 'Review', hint: 'Check and send', icon: 'check' },
  ];
  readonly step = signal(0);
  /** The furthest step reached, so finished steps can be revisited from the progress bar. */
  readonly furthest = signal(0);
  readonly tried = signal(false);

  readonly plans = signal<Plan[]>([]);
  readonly planId = signal<number | null>(null);
  readonly selected = computed(() => this.plans().find(p => p.id === this.planId()) ?? null);
  readonly busy = signal(false);
  readonly done = signal(false);
  readonly error = signal('');
  readonly show = signal(false);
  readonly cropping = signal(false);
  readonly logoUrl = signal<string | null>(null);
  /** The cropped picture as a data URL, ready to send with the registration. */
  private logoData: string | null = null;

  company = ''; tin = ''; first = ''; last = ''; email = ''; phone = ''; password = ''; password2 = ''; cycle: 'monthly' | 'yearly' = 'monthly'; message = ''; website = ''; agree = false;
  readonly lim = limit;
  readonly points = ['Free trial on plans that include one', 'Pay by card, ZimSwitch, EcoCash, OneMoney or bank transfer', 'Cancel or change plan any time', 'Your data stays separate from every other company'];
  readonly whatNext = [
    { no: 1, done: true, title: 'Registration received', text: 'Your details are with us.' },
    { no: 2, done: false, title: 'We review it', text: 'Usually within one working day. We may contact you if something is unclear.' },
    { no: 3, done: false, title: 'You get an email', text: 'As soon as the company is approved. Try signing in meanwhile with the email and password you chose and it will tell you where things stand.' },
    { no: 4, done: false, title: 'Set up and go live', text: 'Pay your first invoice if your plan needs it, register your ZIMRA device and start with the test environment.' },
  ];

  async logoReady(blob: Blob): Promise<void> {
    this.cropping.set(false);
    this.logoData = await new Promise<string>((ok, bad) => { const r = new FileReader(); r.onload = () => ok(String(r.result)); r.onerror = () => bad(r.error); r.readAsDataURL(blob); });
    const old = this.logoUrl(); if (old) URL.revokeObjectURL(old);
    this.logoUrl.set(URL.createObjectURL(blob));
  }
  clearLogo(): void { const old = this.logoUrl(); if (old) URL.revokeObjectURL(old); this.logoUrl.set(null); this.logoData = null; }

  priceOf(p: Plan): string { const v = this.cycle === 'yearly' ? p.priceYearly : p.priceMonthly; return v === 0 ? 'Free' : money(v, p.currency); }

  emailOk(): boolean { return /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(this.email.trim()); }
  userNameOk(): boolean { const u = this.userName.trim(); return !u || /^[A-Za-z0-9][A-Za-z0-9._-]{2,31}$/.test(u); }
  rules(): { text: string; ok: boolean }[] {
    const p = this.password;
    return [
      { text: 'At least 10 characters', ok: p.length >= 10 },
      { text: 'An upper-case letter', ok: /[A-Z]/.test(p) },
      { text: 'A lower-case letter', ok: /[a-z]/.test(p) },
      { text: 'A digit', ok: /\d/.test(p) },
    ];
  }
  strength(): number { const n = this.rules().filter(r => r.ok).length; return this.password.length >= 14 && n === 4 ? 4 : Math.min(n, 3) || (this.password ? 1 : 0); }
  strengthLabel(): string { return ['', 'Weak', 'Fair', 'Good', 'Strong'][this.strength()]; }
  strengthColor(): string { return ['', 'bg-red-500', 'bg-amber-500', 'bg-lime-500', 'bg-emerald-500'][this.strength()]; }

  stepOk(i: number): boolean {
    switch (i) {
      case 0: return !!this.company.trim();
      case 1: return !!this.first.trim() && this.emailOk() && this.userNameOk() && this.rules().every(r => r.ok) && this.password === this.password2;
      default: return true;
    }
  }

  review(): { title: string; step: number; rows: [string, string][] }[] {
    const p = this.selected();
    return [
      { title: 'Company', step: 0, rows: [['Company name', this.company.trim()], ['TIN', this.tin.trim()], ['Phone', this.phone.trim()], ['Referral code', this.partnerCode()]] },
      { title: 'Your account', step: 1, rows: [['Name', `${this.first.trim()} ${this.last.trim()}`.trim()], ['Email', this.email.trim()], ['Sign in with', this.userName.trim() || this.email.trim()], ['Password', '•'.repeat(Math.min(this.password.length, 12))]] },
      { title: 'Plan', step: 2, rows: [['Plan', p ? p.name : 'Not sure yet, we will recommend one'], ['Billing', this.cycle === 'yearly' ? 'Yearly' : 'Monthly'], ['Price', p ? this.priceOf(p) + (this.cycle === 'yearly' ? ' / year' : ' / month') : '—']] },
    ];
  }

  /** /register?ref=CODE: the accounting firm or reseller that sent this company. An unknown code is simply ignored by the server. */
  readonly partnerCode = signal('');

  async ngOnInit(): Promise<void> {
    const ref = (this.route.snapshot.queryParamMap.get('ref') ?? '').trim().toUpperCase();
    if (/^[A-Z0-9]{3,20}$/.test(ref)) this.partnerCode.set(ref);
    const q = this.route.snapshot.queryParamMap;
    if (q.get('cycle') === 'yearly') this.cycle = 'yearly';
    // /register?verify=you@company.com: came from sign-in with an unconfirmed email
    if (q.has('verify')) { this.email = (q.get('verify') ?? '').trim(); this.phase.set('verify'); }
    try {
      this.plans.set(await this.api.get<Plan[]>('/api/v1/public/plans'));
      const wanted = Number(q.get('plan'));
      if (wanted && this.plans().some(p => p.id === wanted)) this.planId.set(wanted);
    } catch { /* the plan choice is optional */ }
  }

  private move(i: number): void { this.step.set(i); this.furthest.set(Math.max(this.furthest(), i)); this.tried.set(false); this.error.set(''); window.scrollTo({ top: 0, behavior: 'smooth' }); }
  next(): void { if (!this.stepOk(this.step())) { this.tried.set(true); return; } this.move(this.step() + 1); }
  back(): void { this.move(Math.max(0, this.step() - 1)); }
  goTo(i: number): void { if (i <= this.furthest() && (i < this.step() || this.stepOk(this.step()))) this.move(i); else this.tried.set(true); }
  /** Enter moves forward on the first steps and only sends on the last, so a stray Enter never submits early. */
  onEnter(): void { if (this.step() < this.steps.length - 1) this.next(); else void this.submit(); }

  async submit(): Promise<void> {
    if (this.busy()) return;
    if (!this.stepOk(0)) { this.move(0); this.tried.set(true); return; }
    if (!this.stepOk(1)) { this.move(1); this.tried.set(true); return; }
    if (!this.agree) { this.error.set('Please tick the confirmation box.'); return; }
    this.busy.set(true); this.error.set('');
    try {
      const r = await this.api.post<{ status: string; verificationRequired: boolean }>('/api/v1/public/register', {
        companyName: this.company.trim(), tin: this.tin.trim() || null, contactName: this.first.trim(), contactSurname: this.last.trim(), email: this.email.trim(),
        phone: this.phone.trim() || null, password: this.password, planId: this.planId(), billingCycle: this.cycle, message: this.message.trim() || null, website: this.website, logoData: this.logoData, partnerCode: this.partnerCode() || null,
        userName: this.userName.trim() || null,
      });
      if (r?.verificationRequired) { this.code = ''; this.note.set(''); this.phase.set('verify'); this.startCooldown(); } else this.done.set(true);
      window.scrollTo({ top: 0 });
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  private startCooldown(): void {
    clearInterval(this.timer);
    this.cooldown.set(60);
    this.timer = setInterval(() => { this.cooldown.update(n => Math.max(0, n - 1)); if (this.cooldown() === 0) clearInterval(this.timer); }, 1000);
  }

  async resend(): Promise<void> {
    this.error.set(''); this.note.set('');
    try { const r = await this.api.post<{ message: string }>('/api/v1/public/register/resend', { email: this.email.trim() }); this.note.set(r.message); this.startCooldown(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); }
  }

  async verify(): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true); this.error.set(''); this.note.set('');
    try {
      const r = await this.api.post<{ status: string; message: string; signInName?: string; trialEndsAt?: string; invoiceNumber?: string }>('/api/v1/public/register/verify', { email: this.email.trim(), code: this.code.trim() });
      if (r.status !== 'Active') { this.done.set(true); this.phase.set('form'); return; }
      this.ready.set(r); this.phase.set('ready');
      // the password was typed a moment ago on this page: sign straight in (from a link, they sign in themselves)
      if (this.password && r.signInName) {
        try { const s = await this.auth.login(r.signInName, this.password); this.signedIn.set(!s.mfaRequired); } catch { /* they can sign in by hand */ }
      }
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); this.code = ''; }
    finally { this.busy.set(false); }
  }

}
