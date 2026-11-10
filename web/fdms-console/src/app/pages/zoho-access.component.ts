import { ChangeDetectionStrategy, Component, OnInit, inject, output, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { ToastService } from '../core/ui.services';
import { IconComponent } from '../shared/ui.components';

export interface IntegrationAccess { id: string | null; clientId: string; company: string; status: string; uses: string[]; note: string | null; requestedBy: string | null; requestedAt: string | null;
  decidedBy: string | null; decidedAt: string | null; decisionNote: string | null; setupFee: number; currency: string; invoiceId: string | null; invoiceNumber: string | null; invoiceStatus: string | null;
  activatedAt: string | null; suspendedAt: string | null; provider?: string; providerLabel?: string; }

export const USES: { key: string; label: string; help: string }[] = [
  { key: 'fiscalise', label: 'Fiscalise my Zoho Books invoices', help: 'Invoices and credit notes raised in Zoho are fiscalised with ZIMRA and the result is written back.' },
  { key: 'own_api', label: 'Connect my own system to Zoho Books', help: 'Our ERP, shop or POS pushes data to Zoho and pulls it back through FiscalHub with an API key.' },
  { key: 'partners', label: 'Share Zoho Books with a partner', help: 'A partner (accountant, software vendor) reaches the modules we choose.' },
];

/** Before using the Zoho Books integration a company applies; the platform owner approves it, and with a set-up fee it switches on when that invoice is paid. */
@Component({
  selector: 'app-zoho-access',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, DecimalPipe, RouterLink, IconComponent],
  template: `
    @if (a(); as a) {
      <div class="card">
        <div class="card-title flex items-center gap-2"><app-icon name="link" size="h-4 w-4" />Zoho Books integration</div>
        @switch (a.status) {
          @case ('Requested') {
            <p class="text-sm">⏳ Your application was sent on {{ a.requestedAt | date: 'd MMM y, HH:mm' }} and is being reviewed. You will get a notification and an email as soon as it is decided.</p>
            <p class="hint mt-2">For: {{ labels(a.uses) }}</p>
          }
          @case ('AwaitingPayment') {
            <p class="text-sm">✅ Approved. You can connect Zoho Books, choose your settings and create API keys below now. They start working as soon as invoice <strong>{{ a.invoiceNumber }}</strong> (set-up {{ a.setupFee | number: '1.2-2' }} {{ a.currency }} plus VAT) is paid.</p>
            @if (a.decisionNote) { <p class="hint mt-1">{{ a.decisionNote }}</p> }
            <a class="btn mt-3" routerLink="/billing"><app-icon name="card" size="h-4 w-4" />Pay under Billing</a>
          }
          @case ('Suspended') {
            <p class="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300">The integration is suspended for your company: {{ a.decisionNote }}. Contact the platform operator.</p>
          }
          @default {
            @if (a.status === 'Rejected') { <p class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-900/25 dark:text-amber-200">Your last application was not approved: {{ a.decisionNote }}. You can apply again.</p> }
            <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">Connect Zoho Books to FiscalHub. Tell us what you need it for; we review every application and switch it on
              @if (a.setupFee > 0) { after a one-time set-up fee of {{ a.setupFee | number: '1.2-2' }} {{ a.currency }} } @else { once approved }.</p>
            <form (ngSubmit)="apply()">
              <div class="space-y-2">
                @for (u of uses; track u.key) {
                  <label class="flex items-start gap-2 text-sm"><input type="checkbox" class="mt-1" [checked]="chosen.has(u.key)" (change)="toggle(u.key)" />
                    <span><strong>{{ u.label }}</strong><br /><span class="text-slate-500">{{ u.help }}</span></span></label>
                }
              </div>
              <label class="mt-3 block"><span class="label">Tell us about your system (optional)</span>
                <textarea class="input" rows="3" name="note" [(ngModel)]="note" maxlength="1000" placeholder="e.g. We use Sage and want new customers and invoices to go to Zoho Books."></textarea></label>
              <div class="mt-3 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Apply</button></div>
            </form>
          }
        }
      </div>
    }
  `,
})
export class ZohoAccessComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly changed = output<void>();
  readonly a = signal<IntegrationAccess | null>(null);
  readonly busy = signal(false);
  readonly uses = USES;
  chosen = new Set<string>(['fiscalise']);
  note = '';

  ngOnInit(): void { void this.load(); }
  async load(): Promise<void> {
    try {
      const a = await this.api.get<IntegrationAccess>('/api/v1/integrations/access');
      this.a.set(a);
      if (a.status === 'Active') this.changed.emit();
    } catch (e) { this.toast.err(e); }
  }
  labels(keys: string[]): string { return keys.map(k => USES.find(u => u.key === k)?.label ?? k).join('; '); }
  toggle(k: string): void { if (this.chosen.has(k)) this.chosen.delete(k); else this.chosen.add(k); }

  async apply(): Promise<void> {
    this.busy.set(true);
    try { this.a.set(await this.api.post<IntegrationAccess>('/api/v1/integrations/access', { uses: [...this.chosen], note: this.note || null })); this.toast.ok('Application sent. We will let you know.'); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
