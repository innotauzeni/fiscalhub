import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { BillingProfile, BillingProfileInfo } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';
import { LogoCropperComponent } from '../../shared/logo-cropper.component';

/**
 * How the platform's own invoices look and how customers pay them: company details, logo, colour, bank details, terms - with a live preview of the
 * PDF - and whether Paynow (cards, ZimSwitch, EcoCash, OneMoney) is connected.
 */
@Component({
  selector: 'app-billing-profile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, LogoCropperComponent],
  template: `
    <div class="mb-3 mt-8">
      <h2 class="text-lg font-semibold tracking-tight">Billing and invoice template</h2>
      <p class="text-sm text-slate-500 dark:text-slate-400">The invoices you send to companies, and how they can pay you.</p>
    </div>

    @if (p(); as f) {
      <div class="grid gap-6 xl:grid-cols-5">
        <form class="space-y-6 xl:col-span-3" (ngSubmit)="save()">
          <div class="card">
            <div class="card-title">Your company on the invoice</div>
            <div class="grid gap-4 sm:grid-cols-2">
              <label class="block sm:col-span-2"><span class="label">Company name</span><input class="input" name="cn" [(ngModel)]="f.companyName" (ngModelChange)="changed()" required maxlength="150" /></label>
              <label class="block sm:col-span-2"><span class="label">Address (one line per row)</span><textarea class="input" rows="3" name="ad" [(ngModel)]="f.address" (ngModelChange)="changed()"></textarea></label>
              <label class="block"><span class="label">Tax / VAT number</span><input class="input" name="tn" [(ngModel)]="f.taxNumber" (ngModelChange)="changed()" /></label>
              <label class="block"><span class="label">Email</span><input class="input" name="em" type="email" [(ngModel)]="f.email" (ngModelChange)="changed()" /></label>
              <label class="block"><span class="label">Phone</span><input class="input" name="ph" [(ngModel)]="f.phone" (ngModelChange)="changed()" /></label>
              <label class="block"><span class="label">Website</span><input class="input" name="wb" [(ngModel)]="f.website" (ngModelChange)="changed()" /></label>
              <div class="block"><span class="label">Colour</span>
                <div class="flex items-center gap-2"><input type="color" class="h-9 w-12 cursor-pointer rounded-lg border border-slate-300 bg-transparent p-0.5 dark:border-slate-700" name="ac" [(ngModel)]="f.accent" (ngModelChange)="changed()" aria-label="Accent colour" /><code class="code">{{ f.accent }}</code></div></div>
              <label class="block"><span class="label">Invoice number prefix</span><input class="input uppercase" name="pf" [(ngModel)]="f.invoicePrefix" (ngModelChange)="changed()" maxlength="10" /></label>
              <label class="block"><span class="label">Tax added to invoices (%)</span><input class="input" type="number" min="0" max="100" step="0.01" name="tx" [(ngModel)]="f.taxPercent" (ngModelChange)="changed()" /></label>
              <div class="block sm:col-span-2"><span class="label">Logo</span>
                <div class="flex flex-wrap items-center gap-3">
                  <button type="button" class="btn-ghost btn-sm" (click)="cropping.set(true)"><app-icon name="upload" size="h-4 w-4" />{{ info()?.hasLogo ? 'Replace logo' : 'Upload logo' }}</button>
                  @if (info()?.hasLogo) { <button type="button" class="btn-ghost btn-sm" (click)="removeLogo()"><app-icon name="trash" size="h-4 w-4" />Remove logo</button> }
                </div></div>
            </div>
          </div>

          <div class="card">
            <div class="card-title">Bank transfer details</div>
            <p class="hint -mt-2 mb-3">Shown on invoices and in the pay dialog. Bank transfer is offered only when an account number is set.</p>
            <div class="grid gap-4 sm:grid-cols-2">
              <label class="block"><span class="label">Bank</span><input class="input" name="bn" [(ngModel)]="f.bankName" (ngModelChange)="changed()" /></label>
              <label class="block"><span class="label">Account name</span><input class="input" name="an" [(ngModel)]="f.accountName" (ngModelChange)="changed()" /></label>
              <label class="block"><span class="label">Account number</span><input class="input font-mono" name="ano" [(ngModel)]="f.accountNumber" (ngModelChange)="changed()" /></label>
              <label class="block"><span class="label">Branch code</span><input class="input" name="bc" [(ngModel)]="f.branchCode" (ngModelChange)="changed()" /></label>
              <label class="block"><span class="label">SWIFT</span><input class="input" name="sw" [(ngModel)]="f.swift" (ngModelChange)="changed()" /></label>
              <label class="block sm:col-span-2"><span class="label">Extra payment instructions</span><textarea class="input" rows="2" name="pi" [(ngModel)]="f.paymentInstructions" (ngModelChange)="changed()"></textarea></label>
            </div>
          </div>

          <div class="card">
            <div class="card-title">Terms and footer</div>
            <div class="grid gap-4">
              <label class="block"><span class="label">Terms</span><textarea class="input" rows="3" name="te" [(ngModel)]="f.terms" (ngModelChange)="changed()"></textarea></label>
              <label class="block"><span class="label">Footer line</span><input class="input" name="fo" [(ngModel)]="f.footer" (ngModelChange)="changed()" /></label>
            </div>
            <div class="mt-5 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn" type="submit" [disabled]="busy()">Save billing settings</button></div>
          </div>
        </form>

        <div class="space-y-6 xl:col-span-2">
          <div class="card">
            <div class="card-title">Online payments (Paynow)</div>
            @if (info()?.paynowConfigured) {
              <p class="text-sm"><span class="badge badge-ok">Connected</span> <span class="ml-1 text-slate-600 dark:text-slate-400">Customers can pay by Visa, Mastercard, ZimSwitch, EcoCash and OneMoney.</span></p>
            } @else {
              <p class="text-sm"><span class="badge badge-warn">Not connected</span></p>
              <p class="mt-2 text-sm text-slate-600 dark:text-slate-400">Create a merchant integration at paynow.co.zw, then enter its Integration ID and key under <strong>Payment gateways</strong> above. No restart is needed.</p>
            }
          </div>

          <div class="card">
            <div class="mb-3 flex items-center justify-between"><div class="card-title mb-0">Preview</div><button type="button" class="btn-ghost btn-sm" (click)="preview()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button></div>
            @if (previewUrl()) { <img [src]="previewUrl()" alt="Invoice preview" class="w-full rounded-lg border border-slate-200 shadow-sm dark:border-slate-700" /> }
            @else { <div class="flex h-64 items-center justify-center rounded-lg border border-dashed border-slate-300 text-sm text-slate-500 dark:border-slate-700">Preview loading…</div> }
          </div>
        </div>
      </div>
    }
    @if (cropping()) { <app-logo-cropper title="Invoice logo" (done)="uploadLogo($event)" (closed)="cropping.set(false)" /> }
  `,
})
export class BillingProfileComponent implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly info = signal<BillingProfileInfo | null>(null);
  readonly p = signal<BillingProfile | null>(null);
  readonly busy = signal(false);
  readonly previewUrl = signal<string | null>(null);
  private timer?: ReturnType<typeof setTimeout>;

  ngOnInit(): Promise<void> { return this.load(); }
  ngOnDestroy(): void { clearTimeout(this.timer); const u = this.previewUrl(); if (u) URL.revokeObjectURL(u); }

  async load(): Promise<void> {
    try { const i = await this.api.get<BillingProfileInfo>('/api/v1/settings/billing'); this.info.set(i); this.p.set({ ...i.profile }); await this.preview(); } catch (e) { this.toast.err(e); }
  }

  /** re-draw the preview shortly after the last keystroke */
  changed(): void { clearTimeout(this.timer); this.timer = setTimeout(() => void this.preview(), 600); }

  async preview(): Promise<void> {
    const f = this.p(); if (!f) return;
    try {
      const blob = await this.api.postBlob('/api/v1/settings/billing/preview', this.clean(f));
      const old = this.previewUrl(); this.previewUrl.set(URL.createObjectURL(blob)); if (old) URL.revokeObjectURL(old);
    } catch { /* the preview is a convenience; saving reports real validation problems */ }
  }

  /** empty text boxes are sent as null so the server stores "not set" */
  private clean(f: BillingProfile): BillingProfile {
    const n = (v?: string | null) => (v && v.trim() ? v.trim() : undefined);
    return { ...f, companyName: f.companyName.trim(), address: n(f.address), taxNumber: n(f.taxNumber), email: n(f.email), phone: n(f.phone), website: n(f.website), bankName: n(f.bankName), accountName: n(f.accountName),
      accountNumber: n(f.accountNumber), branchCode: n(f.branchCode), swift: n(f.swift), paymentInstructions: n(f.paymentInstructions), terms: n(f.terms), footer: n(f.footer), taxPercent: Number(f.taxPercent) || 0 };
  }

  async save(): Promise<void> {
    const f = this.p(); if (!f) return;
    this.busy.set(true);
    try { const i = await this.api.put<BillingProfileInfo>('/api/v1/settings/billing', this.clean(f)); this.info.set(i); this.p.set({ ...i.profile }); this.toast.ok('Billing settings saved.'); await this.preview(); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  readonly cropping = signal(false);

  async uploadLogo(blob: Blob): Promise<void> {
    this.cropping.set(false);
    try { const form = new FormData(); form.append('file', blob, blob.type === 'image/jpeg' ? 'logo.jpg' : 'logo.png'); this.info.set(await this.api.post<BillingProfileInfo>('/api/v1/settings/billing/logo', form)); this.toast.ok('Logo saved.'); await this.preview(); }
    catch (err) { this.toast.err(err); }
  }

  async removeLogo(): Promise<void> {
    if (!(await this.dialog.confirm('Remove the logo from your invoices?', 'Remove', true))) return;
    try { this.info.set(await this.api.delete<BillingProfileInfo>('/api/v1/settings/billing/logo')); await this.preview(); } catch (e) { this.toast.err(e); }
  }
}
