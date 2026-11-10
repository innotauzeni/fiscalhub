import { ChangeDetectionStrategy, Component, ElementRef, OnInit, effect, inject, input, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AssistantFields, AssistantPlan, AssistantReply, AssistantStatus } from '../core/ai.models';
import { Api, errorMessage } from '../core/api.service';
import { IconComponent } from '../shared/ui.components';
import { PublicFooterComponent, PublicHeaderComponent } from '../shared/public.components';

interface Bubble { role: 'user' | 'assistant'; text: string }

const FIELDS: { key: keyof AssistantFields; label: string; optional?: boolean }[] = [
  { key: 'companyName', label: 'Company' }, { key: 'tin', label: 'TIN', optional: true }, { key: 'contactName', label: 'First name' }, { key: 'contactSurname', label: 'Surname' },
  { key: 'email', label: 'Email' }, { key: 'phone', label: 'Phone', optional: true }, { key: 'planId', label: 'Plan' }, { key: 'billingCycle', label: 'Billing', optional: true },
];

/**
 * The sign-up conversation. The assistant only asks questions and proposes values; the server checks every one, and the account is made by the ordinary registration
 * request once the visitor has typed a password in the private box (never in the chat). Used full-page and in the floating panel.
 */
@Component({
  selector: 'app-assistant-chat',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent],
  template: `
    @if (status() && !status()!.enabled) {
      <div class="card text-center">
        <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800"><app-icon name="message" size="h-6 w-6" /></div>
        <h2 class="mt-3 text-base font-semibold">The assistant is resting</h2>
        <p class="mt-1 text-sm text-slate-500">You can still register in a few minutes with the form.</p>
        <a routerLink="/register" class="btn mt-4 inline-flex">Open the registration form</a>
      </div>
    } @else if (done()) {
      <div class="card text-center">
        <div class="mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-emerald-100 text-emerald-600 dark:bg-emerald-900/40"><app-icon name="check" size="h-7 w-7" /></div>
        <h2 class="mt-4 text-xl font-semibold">You are registered!</h2>
        <p class="mt-2 text-sm text-slate-600 dark:text-slate-400"><strong>{{ fields().companyName }}</strong> is now waiting for review. We will email <strong>{{ fields().email }}</strong> as soon as it is approved. Until then you can sign in with that email and the password you just chose.</p>
        <div class="mt-5 flex flex-wrap justify-center gap-2"><a routerLink="/login" class="btn">Go to sign in</a><a routerLink="/docs" class="btn-ghost">Read the documentation</a></div>
      </div>
    } @else {
      <div [class]="compact() ? 'grid gap-5' : 'grid gap-5 lg:grid-cols-5'">
        <!-- chat -->
        <section [class]="'flex flex-col overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900 ' + (compact() ? 'h-[28rem]' : 'h-[34rem] lg:col-span-3')">
          <header class="flex items-center gap-3 border-b border-slate-100 bg-linear-to-r from-brand-600 to-violet-600 px-4 py-3 text-white dark:border-slate-800">
            <span class="flex h-9 w-9 items-center justify-center rounded-full bg-white/20"><app-icon name="bolt" size="h-5 w-5" /></span>
            <div class="leading-tight"><div class="text-sm font-semibold">Sign-up assistant</div><div class="flex items-center gap-1.5 text-xs text-white/80"><span class="h-2 w-2 rounded-full bg-emerald-300"></span>Online · takes about 2 minutes</div></div>
          </header>
          @if (compact()) { <div class="h-1 shrink-0 bg-slate-100 dark:bg-slate-800" role="progressbar" [attr.aria-valuenow]="filled()" aria-valuemin="0" [attr.aria-valuemax]="rows.length"><div class="h-full bg-brand-500 transition-all" [style.width.%]="(filled() / rows.length) * 100"></div></div> }

          <div #scroll class="flex-1 space-y-3 overflow-y-auto bg-slate-50/60 px-4 py-4 dark:bg-slate-950/40" aria-live="polite">
            @for (m of bubbles(); track $index) {
              <div class="flex" [class.justify-end]="m.role === 'user'">
                <div class="max-w-[85%] whitespace-pre-line rounded-2xl px-3.5 py-2 text-sm leading-relaxed shadow-sm"
                  [class]="m.role === 'user' ? 'rounded-br-md bg-brand-600 text-white' : 'rounded-bl-md border border-slate-200 bg-white text-slate-800 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-100'">{{ m.text }}</div>
              </div>
            }
            @if (busy()) { <div class="flex"><div class="rounded-2xl rounded-bl-md border border-slate-200 bg-white px-4 py-3 dark:border-slate-700 dark:bg-slate-800" aria-label="The assistant is typing"><span class="flex gap-1"><i class="dot"></i><i class="dot" style="animation-delay:.15s"></i><i class="dot" style="animation-delay:.3s"></i></span></div></div> }

            @if (showPlans()) {
              <div class="grid gap-2 sm:grid-cols-2">
                @for (p of plans(); track p.id) {
                  <button type="button" class="rounded-xl border border-slate-200 bg-white p-3 text-left transition hover:border-brand-400 hover:shadow dark:border-slate-700 dark:bg-slate-800" (click)="send('I choose the ' + p.name + ' plan')">
                    <div class="flex items-baseline justify-between"><span class="text-sm font-semibold">{{ p.name }}</span><span class="text-xs font-medium text-brand-600">{{ p.price }}</span></div>
                    <div class="mt-0.5 text-xs text-slate-500">{{ p.summary }}</div></button>
                }
              </div>
            }
            @if (showCycles()) {
              <div class="flex gap-2"><button type="button" class="chip" (click)="send('Monthly billing')">Monthly</button><button type="button" class="chip" (click)="send('Yearly billing')">Yearly</button></div>
            }
            @if (showSkip()) { <div><button type="button" class="chip" (click)="send('Skip this one')">Skip this one</button></div> }
            @if (error()) { <p class="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }} <a routerLink="/register" class="font-medium underline">Use the form instead</a></p> }
          </div>

          <form class="flex items-center gap-2 border-t border-slate-100 p-3 dark:border-slate-800" (ngSubmit)="send(input)">
            <input class="input flex-1" name="msg" [(ngModel)]="input" maxlength="500" autocomplete="off" [disabled]="busy() || !status()" placeholder="Type your answer…" aria-label="Your message" />
            <button class="btn" type="submit" [disabled]="busy() || !input.trim()" aria-label="Send"><app-icon name="send" size="h-4 w-4" /></button>
          </form>
        </section>

        <!-- the board (in the small floating panel it appears only for the last step) -->
        @if (!compact() || stage() === 'confirm') {
        <aside [class]="compact() ? 'space-y-4' : 'space-y-4 lg:col-span-2'">
          <section class="card">
            <div class="flex items-center justify-between"><h2 class="text-base font-semibold">Your registration</h2><span class="text-xs text-slate-500">{{ filled() }} of {{ rows.length }}</span></div>
            <div class="mt-2 h-1.5 overflow-hidden rounded-full bg-slate-100 dark:bg-slate-800"><div class="h-full rounded-full bg-brand-500 transition-all" [style.width.%]="(filled() / rows.length) * 100"></div></div>
            <ul class="mt-4 space-y-2.5">
              @for (r of rows; track r.key) {
                <li class="flex items-center gap-3 text-sm">
                  <span class="flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-[11px]" [class]="has(r.key) ? 'bg-emerald-500 text-white' : 'border border-dashed border-slate-300 text-slate-400 dark:border-slate-600'">
                    @if (has(r.key)) { <app-icon name="check" size="h-3.5 w-3.5" /> }</span>
                  <span class="w-20 shrink-0 text-slate-500">{{ r.label }}</span>
                  <span class="min-w-0 flex-1 truncate font-medium">{{ show(r.key) }}@if (!has(r.key) && r.optional) { <span class="font-normal text-slate-400">optional</span> }</span>
                </li>
              }
            </ul>

            @if (stage() === 'confirm') {
              <form class="mt-5 border-t border-slate-100 pt-4 dark:border-slate-800" (ngSubmit)="register()">
                <p class="text-sm font-medium">Last step: choose a password</p>
                <p class="mb-3 text-xs text-slate-500">Type it here, not in the chat. It goes straight to your account and nobody, including the assistant, ever sees it.</p>
                <input class="input" type="password" name="pw" [(ngModel)]="password" autocomplete="new-password" placeholder="Password" aria-label="Password" />
                <input class="input mt-2" type="password" name="pw2" [(ngModel)]="password2" autocomplete="new-password" placeholder="Repeat password" aria-label="Repeat password" />
                <ul class="mt-2 grid grid-cols-2 gap-x-3 text-xs">
                  @for (r of rules(); track r.text) { <li [class]="r.ok ? 'text-emerald-600' : 'text-slate-400'">{{ r.ok ? '✓' : '○' }} {{ r.text }}</li> }
                </ul>
                @if (password2 && password !== password2) { <p class="mt-1 text-xs text-red-600">The two passwords do not match.</p> }
                <label class="mt-3 flex items-start gap-2 text-xs text-slate-600 dark:text-slate-400"><input type="checkbox" class="mt-0.5 accent-brand-600" name="agree" [(ngModel)]="agree" />I confirm these details are correct and I am allowed to register this company.</label>
                <!-- honeypot for bots -->
                <div class="absolute -left-[9999px] h-0 w-0 overflow-hidden" aria-hidden="true"><input name="website" tabindex="-1" autocomplete="off" [(ngModel)]="website" /></div>
                @if (regError()) { <p class="mt-2 rounded-lg bg-red-50 px-3 py-2 text-xs text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ regError() }}</p> }
                <button class="btn mt-4 w-full" type="submit" [disabled]="registering() || !passwordOk() || !agree">{{ registering() ? 'Creating…' : 'Create my account' }}</button>
              </form>
            } @else {
              <p class="mt-4 text-xs text-slate-500">Something wrong? Just tell the assistant, for example "my email is ada@shop.co.zw".</p>
            }
          </section>
          <p class="px-1 text-xs text-slate-500">Prefer a form? <a routerLink="/register" class="font-medium text-brand-600 underline">Register the classic way</a>. We review every registration before it goes live.</p>
        </aside>
        }
      </div>
    }
  `,
  styles: [`.chip { border: 1px solid rgb(203 213 225); background: white; border-radius: 9999px; padding: .25rem .85rem; font-size: .8rem; } .chip:hover { border-color: #2563eb; color: #2563eb; }
    .dot { display:block; width:6px; height:6px; border-radius:9999px; background:#94a3b8; animation: bounce 1s infinite ease-in-out; } @keyframes bounce { 0%,80%,100% { transform: translateY(0); opacity:.5 } 40% { transform: translateY(-4px); opacity:1 } }`],
})
export class AssistantChatComponent implements OnInit {
  private readonly api = inject(Api);
  readonly compact = input(false);
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroll');

  readonly status = signal<AssistantStatus | null>(null);
  readonly bubbles = signal<Bubble[]>([]);
  readonly fields = signal<AssistantFields>({});
  readonly plans = signal<AssistantPlan[]>([]);
  readonly stage = signal<'collecting' | 'confirm'>('collecting');
  readonly busy = signal(false);
  readonly error = signal('');
  readonly regError = signal('');
  readonly registering = signal(false);
  readonly done = signal(false);
  readonly rows = FIELDS;
  input = ''; password = ''; password2 = ''; website = ''; agree = false;

  constructor() {
    effect(() => { this.bubbles(); this.busy(); queueMicrotask(() => { const el = this.scroller()?.nativeElement; if (el) el.scrollTop = el.scrollHeight; }); });
  }

  async ngOnInit(): Promise<void> {
    try {
      const s = await this.api.get<AssistantStatus>('/api/v1/public/assistant/status');
      this.status.set(s);
      if (s.enabled) this.bubbles.set([{ role: 'assistant', text: s.greeting }]);
    } catch { this.status.set({ enabled: false, greeting: '' }); }
  }

  private last(): string { const b = this.bubbles(); return b.length ? b[b.length - 1].text : ''; }
  has(k: keyof AssistantFields): boolean { const v = this.fields()[k]; return v !== null && v !== undefined && v !== ''; }
  show(k: keyof AssistantFields): string {
    const v = this.fields()[k];
    if (!this.has(k)) return '';
    if (k === 'planId') return this.plans().find(p => p.id === v)?.name ?? String(v);
    if (k === 'billingCycle') return v === 'yearly' ? 'Yearly' : 'Monthly';
    return String(v);
  }
  filled(): number { return FIELDS.filter(r => this.has(r.key)).length; }
  showPlans(): boolean { return !this.busy() && this.plans().length > 0 && !this.has('planId') && this.has('contactName') && /plan|package|choose|pick/i.test(this.last()); }
  showCycles(): boolean { return !this.busy() && this.has('planId') && !this.has('billingCycle') && /month|year|billing/i.test(this.last()); }
  showSkip(): boolean { return !this.busy() && this.stage() === 'collecting' && /optional|skip|tin\b|phone/i.test(this.last()) && !this.showPlans(); }

  async send(text: string): Promise<void> {
    text = (text ?? '').trim();
    if (!text || this.busy()) return;
    this.input = ''; this.error.set('');
    const next: Bubble[] = [...this.bubbles(), { role: 'user', text: text.slice(0, 500) }];
    this.bubbles.set(next); this.busy.set(true);
    try {
      const r = await this.api.post<AssistantReply>('/api/v1/public/assistant/chat', { messages: next.slice(-24).map(b => ({ role: b.role, text: b.text })), fields: this.fields() });
      this.fields.set(r.fields); this.plans.set(r.plans); this.stage.set(r.stage);
      this.bubbles.set([...next, { role: 'assistant', text: r.reply }]);
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(false); }
  }

  rules(): { text: string; ok: boolean }[] {
    const p = this.password;
    return [{ text: '10+ characters', ok: p.length >= 10 }, { text: 'Upper-case letter', ok: /[A-Z]/.test(p) }, { text: 'Lower-case letter', ok: /[a-z]/.test(p) }, { text: 'A digit', ok: /\d/.test(p) }];
  }
  passwordOk(): boolean { return this.rules().every(r => r.ok) && this.password === this.password2; }

  async register(): Promise<void> {
    if (this.registering() || !this.passwordOk() || !this.agree) return;
    const f = this.fields();
    this.registering.set(true); this.regError.set('');
    try {
      await this.api.post('/api/v1/public/register', {
        companyName: f.companyName, tin: f.tin || null, contactName: f.contactName, contactSurname: f.contactSurname, email: f.email, phone: f.phone || null,
        password: this.password, planId: f.planId ?? null, billingCycle: f.billingCycle || 'monthly', message: f.message || 'Registered with the sign-up assistant', website: this.website, logoData: null,
      });
      this.password = ''; this.password2 = ''; this.done.set(true);
    } catch (e) { this.regError.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.registering.set(false); }
  }
}

/** The public "nice board": the whole sign-up conversation on a page of its own. */
@Component({
  selector: 'app-assistant-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PublicHeaderComponent, PublicFooterComponent, AssistantChatComponent],
  template: `
    <div class="min-h-screen bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      <app-public-header />
      <main class="mx-auto max-w-6xl px-4 py-8 lg:px-8 lg:py-12">
        <div class="mb-8 text-center">
          <h1 class="text-3xl font-bold tracking-tight">Let's get your company set up</h1>
          <p class="mx-auto mt-2 max-w-xl text-slate-600 dark:text-slate-400">Chat with our assistant. It asks a few simple questions, fills in your registration as you answer, and you choose your password at the end.</p>
        </div>
        <app-assistant-chat />
      </main>
      <app-public-footer />
    </div>
  `,
})
export class AssistantPageComponent {}

/** A round button in the corner of the public pages that opens the sign-up assistant without leaving the page. */
@Component({
  selector: 'app-assistant-launcher',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AssistantChatComponent, IconComponent],
  template: `
    @if (available()) {
      @if (open()) {
        <div class="fixed bottom-24 right-4 z-50 w-[min(26rem,calc(100vw-2rem))]" role="dialog" aria-label="Sign-up assistant">
          <div class="max-h-[calc(100dvh-8rem)] overflow-y-auto rounded-2xl bg-white shadow-2xl dark:bg-slate-900"><app-assistant-chat [compact]="true" /></div>
        </div>
      }
      <button type="button" class="fixed bottom-5 right-4 z-50 flex h-14 items-center gap-2 rounded-full bg-linear-to-br from-brand-600 to-violet-600 px-5 text-sm font-semibold text-white shadow-xl transition hover:scale-105 focus-visible:outline-2 focus-visible:outline-offset-2"
        (click)="open.set(!open())" [attr.aria-expanded]="open()">
        <app-icon [name]="open() ? 'x' : 'message'" size="h-5 w-5" />{{ open() ? 'Close' : 'Register with our assistant' }}
      </button>
    }
  `,
})
export class AssistantLauncherComponent implements OnInit {
  private readonly api = inject(Api);
  readonly open = signal(false);
  readonly available = signal(false);
  async ngOnInit(): Promise<void> {
    try { this.available.set((await this.api.get<AssistantStatus>('/api/v1/public/assistant/status')).enabled); } catch { /* no assistant: no button */ }
  }
}
