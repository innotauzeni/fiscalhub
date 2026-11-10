import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { ToastService } from '../../core/ui.services';
import { Developer, Legal } from '../legal.component';

/** The terms and conditions, privacy policy and developer details every app shows (web, mobile, registration). */
@Component({
  selector: 'app-legal-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink],
  template: `
    <form class="card mt-6" (ngSubmit)="save()">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <div class="card-title">Terms, privacy and developer details</div>
        <div class="flex gap-3 text-sm"><a routerLink="/terms" target="_blank" class="text-brand-600 underline">View terms</a><a routerLink="/privacy" target="_blank" class="text-brand-600 underline">View privacy</a><a routerLink="/developer" target="_blank" class="text-brand-600 underline">View developer</a></div>
      </div>
      <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Write <code class="code"># Heading</code> lines and leave a blank line between paragraphs. Clear a document to go back to the starting text.</p>
      <div class="grid gap-5 lg:grid-cols-2">
        <label class="block"><span class="label">Terms and conditions</span><textarea class="input font-mono text-xs" rows="16" name="terms" [(ngModel)]="terms"></textarea></label>
        <label class="block"><span class="label">Privacy policy</span><textarea class="input font-mono text-xs" rows="16" name="privacy" [(ngModel)]="privacy"></textarea></label>
      </div>
      <h3 class="mt-6 text-sm font-semibold">Developer details</h3>
      <div class="mt-3 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <label class="block"><span class="label">Name</span><input class="input" name="dn" [(ngModel)]="dev.name" required /></label>
        <label class="block"><span class="label">Email</span><input class="input" type="email" name="de" [(ngModel)]="dev.email" /></label>
        <label class="block"><span class="label">Phone</span><input class="input" type="tel" name="dp" [(ngModel)]="dev.phone" /></label>
        <label class="block"><span class="label">WhatsApp</span><input class="input" type="tel" name="dw" [(ngModel)]="dev.whatsApp" /></label>
        <label class="block"><span class="label">Website</span><input class="input" name="ds" [(ngModel)]="dev.website" placeholder="https://" /></label>
        <label class="block"><span class="label">Address</span><input class="input" name="da" [(ngModel)]="dev.address" /></label>
        <label class="block sm:col-span-2 lg:col-span-3"><span class="label">About</span><textarea class="input" rows="2" name="db" [(ngModel)]="dev.about"></textarea></label>
      </div>
      <div class="mt-5 flex justify-end border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn" type="submit" [disabled]="busy()">{{ busy() ? 'Saving…' : 'Save' }}</button></div>
    </form>
  `,
})
export class LegalSettingsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly busy = signal(false);
  terms = '';
  privacy = '';
  dev: Developer = { name: '' };

  async ngOnInit(): Promise<void> {
    try { this.fill(await this.api.get<Legal>('/api/v1/public/legal')); } catch (e) { this.toast.err(e); }
  }

  private fill(l: Legal): void { this.terms = l.terms.body; this.privacy = l.privacy.body; this.dev = { ...l.developer }; }

  async save(): Promise<void> {
    if (!this.dev.name?.trim()) { this.toast.err('The developer name is required.'); return; }
    this.busy.set(true);
    try { this.fill(await this.api.put<Legal>('/api/v1/settings/legal', { terms: this.terms, privacy: this.privacy, developer: this.dev })); this.toast.ok('Saved. Every app shows the new text.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
