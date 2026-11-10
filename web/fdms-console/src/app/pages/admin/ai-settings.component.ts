import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AiSettings } from '../../core/ai.models';
import { Api, errorMessage } from '../../core/api.service';
import { DialogService, ToastService } from '../../core/ui.services';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';

/** Connects the platform to Google Gemini (platform operator). The key is stored encrypted in the database and is never shown again, only its last four characters. */
@Component({
  selector: 'app-ai-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, IconComponent, PageHeaderComponent],
  template: `
    <app-page-header title="AI assistant" subtitle="Switch on the AI report builder for companies and the sign-up assistant on the public site. Both use Google Gemini with your own key." />

    @if (s(); as cfg) {
      <div class="grid gap-5 lg:grid-cols-3">
        <form class="card lg:col-span-2" (ngSubmit)="save()">
          <h2 class="text-base font-semibold">Gemini connection</h2>
          <div class="mt-4 space-y-4">
            <label class="block"><span class="label">API key</span>
              <input class="input font-mono" name="key" type="password" [(ngModel)]="key" autocomplete="off" spellcheck="false"
                [placeholder]="cfg.hasKey ? 'Saved (' + cfg.keyHint + '). Type a new key to replace it.' : 'Paste your Gemini API key'" />
              <span class="hint">Get one free at Google AI Studio (aistudio.google.com, "Get API key"). It is kept encrypted in the database and cannot be read back from this screen.</span></label>
            <label class="block"><span class="label">Model</span><input class="input" name="model" [(ngModel)]="model" maxlength="60" list="models" />
              <datalist id="models"><option value="gemini-2.5-flash"></option><option value="gemini-2.5-flash-lite"></option><option value="gemini-2.5-pro"></option></datalist>
              <span class="hint">gemini-2.5-flash is fast and cheap, and is plenty for this.</span></label>
          </div>

          <h2 class="mt-6 text-base font-semibold">What is switched on</h2>
          <div class="mt-3 space-y-2">
            <label class="flex items-start gap-3 rounded-xl border border-slate-200 p-3 dark:border-slate-700"><input type="checkbox" class="mt-1 accent-brand-600" name="chat" [(ngModel)]="chat" />
              <span><span class="block text-sm font-medium">Public sign-up assistant</span><span class="text-xs text-slate-500">A chat on the public site that asks a visitor for their details and registers them. Anyone can use it, so keep the daily limit sensible.</span></span></label>
            <label class="flex items-start gap-3 rounded-xl border border-slate-200 p-3 dark:border-slate-700"><input type="checkbox" class="mt-1 accent-brand-600" name="reports" [(ngModel)]="reports" />
              <span><span class="block text-sm font-medium">AI reports for companies</span><span class="text-xs text-slate-500">Companies ask about their sales in plain words. Reports built by hand are always available.</span></span></label>
            <label class="flex items-start gap-3 rounded-xl border border-slate-200 p-3 dark:border-slate-700"><input type="checkbox" class="mt-1 accent-brand-600" name="sum" [(ngModel)]="summaries" />
              <span><span class="block text-sm font-medium">Written comment on each report</span><span class="text-xs text-slate-500">One extra short call that comments on the figures. Only totals per group are sent, and never when a report is split by customer.</span></span></label>
          </div>

          <h2 class="mt-6 text-base font-semibold">Daily limits</h2>
          <div class="mt-3 grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Sign-up chat messages per day (everyone)</span><input type="number" class="input" name="cl" [(ngModel)]="chatLimit" min="0" max="100000" /><span class="hint">0 switches the chat off.</span></label>
            <label class="block"><span class="label">AI questions per company per day</span><input type="number" class="input" name="rl" [(ngModel)]="reportLimit" min="0" max="10000" /><span class="hint">0 switches AI questions off.</span></label>
          </div>

          @if (error()) { <p class="mt-4 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
          <div class="mt-5 flex flex-wrap items-center justify-between gap-2">
            <div class="flex gap-2">
              <button type="button" class="btn-ghost" (click)="test()" [disabled]="busy() || !cfg.hasKey">{{ busy() === 'test' ? 'Testing…' : 'Test the key' }}</button>
              @if (cfg.hasKey) { <button type="button" class="btn-ghost text-red-600" (click)="clearKey()">Remove key</button> }
            </div>
            <button class="btn" type="submit" [disabled]="busy()">{{ busy() === 'save' ? 'Saving…' : 'Save' }}</button>
          </div>
          @if (testResult(); as t) { <p class="mt-3 rounded-lg px-3 py-2 text-sm" [class]="t.ok ? 'bg-emerald-50 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-200' : 'bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-300'">{{ t.message }}</p> }
        </form>

        <div class="space-y-5">
          <section class="card">
            <h2 class="flex items-center gap-2 text-base font-semibold"><span class="h-2.5 w-2.5 rounded-full" [class]="cfg.hasKey ? 'bg-emerald-500' : 'bg-slate-300'"></span>{{ cfg.hasKey ? 'Connected' : 'Not connected' }}</h2>
            <dl class="mt-3 space-y-2 text-sm">
              <div class="flex justify-between"><dt class="text-slate-500">Key</dt><dd class="font-mono">{{ cfg.keyHint || 'none' }}</dd></div>
              <div class="flex justify-between"><dt class="text-slate-500">Model</dt><dd>{{ cfg.model }}</dd></div>
              <div class="flex justify-between"><dt class="text-slate-500">Chat messages today</dt><dd class="tabular-nums">{{ cfg.usage.chats }} / {{ cfg.dailyChatLimit }}</dd></div>
              <div class="flex justify-between"><dt class="text-slate-500">AI questions today</dt><dd class="tabular-nums">{{ cfg.usage.reports }}</dd></div>
              <div class="flex justify-between"><dt class="text-slate-500">Failed calls today</dt><dd class="tabular-nums" [class.text-red-600]="cfg.usage.failures > 0">{{ cfg.usage.failures }}</dd></div>
              @if (cfg.updatedAt) { <div class="flex justify-between"><dt class="text-slate-500">Last changed</dt><dd>{{ cfg.updatedAt | date: 'd MMM, HH:mm' }}</dd></div> }
            </dl>
          </section>
          <section class="card text-sm text-slate-600 dark:text-slate-300">
            <h2 class="flex items-center gap-2 text-base font-semibold text-slate-900 dark:text-slate-100"><app-icon name="shield" size="h-4 w-4" />What leaves your system</h2>
            <ul class="mt-2 list-disc space-y-1.5 pl-5">
              <li><strong>Reports:</strong> the question typed, and the names of the company's machines. The AI never calculates; the figures come from your own database.</li>
              <li><strong>Comments:</strong> only the grouped totals of a report, no receipts, never customer names.</li>
              <li><strong>Invoice design copy:</strong> the sample invoice a company uploads (PDF or picture) is sent to be read and is not stored. It counts toward the company's daily AI allowance.</li>
              <li><strong>Sign-up chat:</strong> what the visitor types and the list of public plans. A password is never typed into the chat.</li>
              <li>Google's free tier may use submitted content to improve its products. Use a paid key if that matters to you.</li>
            </ul>
          </section>
        </div>
      </div>
    } @else { <p class="text-sm text-slate-500">Loading…</p> }
  `,
})
export class AiSettingsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly s = signal<AiSettings | null>(null);
  readonly busy = signal<'' | 'save' | 'test'>('');
  readonly error = signal('');
  readonly testResult = signal<{ ok: boolean; message: string } | null>(null);
  key = ''; model = 'gemini-2.5-flash'; chat = true; reports = true; summaries = true; chatLimit = 500; reportLimit = 40;

  async ngOnInit(): Promise<void> { await this.load(); }

  private async load(): Promise<void> {
    try {
      const c = await this.api.get<AiSettings>('/api/v1/admin/ai');
      this.s.set(c); this.model = c.model; this.chat = c.chatEnabled; this.reports = c.reportsEnabled; this.summaries = c.summariesEnabled; this.chatLimit = c.dailyChatLimit; this.reportLimit = c.dailyReportLimitPerCompany;
    } catch (e) { this.toast.err(e); }
  }

  private body(clear = false): object {
    return { apiKey: this.key.trim() || null, clearKey: clear, model: this.model.trim(), chatEnabled: this.chat, reportsEnabled: this.reports, summariesEnabled: this.summaries, dailyChatLimit: Number(this.chatLimit), dailyReportLimitPerCompany: Number(this.reportLimit) };
  }

  async save(): Promise<void> {
    this.busy.set('save'); this.error.set(''); this.testResult.set(null);
    try { await this.api.put('/api/v1/admin/ai', this.body()); this.key = ''; this.toast.ok('Saved.'); await this.load(); }
    catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); } finally { this.busy.set(''); }
  }

  async test(): Promise<void> {
    this.busy.set('test'); this.testResult.set(null);
    try { this.testResult.set(await this.api.post<{ ok: boolean; message: string }>('/api/v1/admin/ai/test')); await this.load(); } catch (e) { this.toast.err(e); } finally { this.busy.set(''); }
  }

  async clearKey(): Promise<void> {
    if (!(await this.dialog.confirm('Remove the Gemini key? The AI features stop working until a new key is saved.', 'Remove key', true))) return;
    try { await this.api.put('/api/v1/admin/ai', this.body(true)); this.key = ''; this.toast.ok('Key removed.'); await this.load(); } catch (e) { this.toast.err(e); }
  }
}
