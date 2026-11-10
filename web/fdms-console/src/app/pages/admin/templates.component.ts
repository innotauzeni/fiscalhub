import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api.service';
import { Branch, InvoiceTemplate, TemplateAnalysis, TemplateSettings } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { BankPickerComponent } from '../../shared/bank-picker.component';
import { BankAccount, BankingStore } from '../../core/banking.store';
import { IconComponent, PageHeaderComponent } from '../../shared/ui.components';
import { LogoCropperComponent } from '../../shared/logo-cropper.component';
import { CompanyBrand } from '../../core/models';

interface Editing { id?: string; branchId?: string; name: string; settings: TemplateSettings; hasLogo: boolean; deviceCount?: number; }

export function defaultSettings(): TemplateSettings {
  return {
    accent: '#2563eb', showLogo: true, logoAlign: 'left', showBuyer: true, showPayments: true, showHsCode: false, showQuantity: true, showUnitPrice: true, showLineTax: true,
    headerStyle: 'plain', titleStyle: 'band', titleAlign: 'center', tableStyle: 'lines', partiesLayout: 'stacked', totalsAlign: 'full', qrPosition: 'center', qrSize: 'medium', rowSpacing: 'normal', logoSize: 'medium', margins: 'normal', fontSize: 10, columns: null,
  };
}

/** Customise how invoices print: company default plus optional per-branch looks, with a live preview rendered by the same engine as the real PDF. */
@Component({
  selector: 'app-templates',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, BankPickerComponent, FormsModule, IconComponent, PageHeaderComponent, LogoCropperComponent],
  template: `
    <app-page-header title="Invoice templates" subtitle="Your logo, colours and wording on the invoices you issue. The fiscal details ZIMRA requires are always printed." />

    @if (brand(); as b) {
      <div class="card mb-6">
        <div class="flex flex-wrap items-end gap-4">
          <div class="min-w-0 flex-1">
            <div class="card-title mb-1">Your own sign-in page</div>
            <p class="text-sm text-slate-600 dark:text-slate-300">Give your staff this link: the sign-in page shows <strong>your logo and colour</strong> (the ones from the default template below).</p>
            <div class="mt-3 flex flex-wrap items-center gap-2">
              <span class="font-mono text-xs text-slate-500">…/login?company=</span>
              <input class="input w-48 font-mono" [(ngModel)]="slug" name="slug" maxlength="40" aria-label="Sign-in word" />
              <button class="btn btn-sm" type="button" (click)="saveSlug()" [disabled]="slug === b.slug">Save</button>
              @if (b.signInUrl) { <button class="btn-ghost btn-sm" type="button" (click)="copySignIn(b.signInUrl)">Copy link</button> }
            </div>
          </div>
        </div>
      </div>
    }

    <div class="grid gap-6 xl:grid-cols-12">
      <!-- list -->
      <div class="space-y-3 xl:col-span-3">
        <div class="card p-3">
          <div class="mb-2 px-2 text-xs font-semibold uppercase tracking-wide text-slate-400">Templates</div>
          @for (t of templates(); track t.id) {
            <button type="button" class="mb-1 flex w-full items-center gap-3 rounded-xl px-3 py-2.5 text-left transition"
                    [class]="editing()?.id === t.id ? 'bg-brand-50 text-brand-700 dark:bg-brand-500/15 dark:text-brand-100' : 'hover:bg-slate-100 dark:hover:bg-slate-800'" (click)="open(t)">
              <span class="h-7 w-7 shrink-0 rounded-lg" [style.background]="t.settings.accent"></span>
              <span class="min-w-0 flex-1"><span class="block truncate text-sm font-medium">{{ t.name }}@if (t.settings.canvas?.elements?.length) { <span class="badge badge-info ml-1">Built design</span> }</span><span class="block truncate text-xs text-slate-500">{{ t.branchId ? 'Branch: ' + (t.branchName ?? '') : 'Company default' }}</span>
                <span class="mt-0.5 block text-[11px] font-medium" [class]="t.deviceCount ? 'text-emerald-600' : 'text-amber-600'">{{ t.deviceCount ? 'Used by ' + t.deviceCount + (t.deviceCount === 1 ? ' device' : ' devices') : 'Not used by any device yet' }}</span></span>
            </button>
          } @empty { <p class="px-2 py-3 text-sm text-slate-500">Nothing customised yet. Invoices use the standard layout.</p> }
          <div class="mt-2 flex flex-col gap-2 border-t border-slate-100 pt-3 dark:border-slate-800">
            @if (!hasDefault()) { <button class="btn-ghost btn-sm justify-start" (click)="startNew(null)"><app-icon name="plus" size="h-4 w-4" />Company default</button> }
            @if (freeBranches().length) {
              <label class="block"><span class="label">Add a branch look</span>
                <select class="input" [ngModel]="''" (ngModelChange)="startNew($event)"><option value="">Choose a branch…</option>@for (b of freeBranches(); track b.id) { <option [value]="b.id">{{ b.name }}</option> }</select></label>
            }
          </div>
        </div>
        <p class="hint px-1">A device's invoices use its branch's template, otherwise the company default.</p>

        <div class="card border-brand-100 bg-linear-to-br from-brand-50/60 to-white p-4 dark:border-brand-500/20 dark:from-brand-500/10 dark:to-slate-900">
          <div class="flex items-center gap-2 text-sm font-semibold"><app-icon name="bolt" size="h-4 w-4" />Copy your current invoice</div>
          <p class="mt-1 text-xs text-slate-600 dark:text-slate-300">Using SAP, Sage, Pastel or another system? Upload a sample invoice (PDF, PNG or JPEG). The AI reads its design and sets this template up to match. You can adjust everything afterwards.</p>
          <label class="btn btn-sm mt-3 w-full cursor-pointer justify-center" [class.opacity-60]="analysing()">
            <app-icon name="upload" size="h-4 w-4" />{{ analysing() ? 'Reading your invoice…' : 'Upload a sample invoice' }}
            <input type="file" class="sr-only" accept="application/pdf,image/png,image/jpeg" [disabled]="analysing()" (change)="analyse($any($event.target))" />
          </label>
          <p class="mt-2 text-[11px] text-slate-500">The sample is sent to Google Gemini to be read and is not kept by us. Use a sample without real customer details if you can.</p>
        </div>
      </div>

      <!-- editor -->
      <div class="xl:col-span-5">
        @if (editing(); as f) {
          <form class="card space-y-6" (ngSubmit)="save()" (input)="bump()" (change)="bump()">
            <div class="grid gap-4 sm:grid-cols-2">
              <label class="block"><span class="label">Template name</span><input class="input" name="tname" [(ngModel)]="f.name" required /></label>
              <div class="block"><span class="label">Applies to</span><div class="input flex items-center bg-slate-50 dark:bg-slate-900">{{ f.branchId ? branchName(f.branchId) : 'Whole company (default)' }}</div></div>
            </div>

            @if (f.id && f.deviceCount === 0) {
              <div class="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-100" role="status">
                <div class="font-medium">No device prints with this template yet</div>
                <p class="mt-0.5 text-xs">{{ f.branchId ? 'It belongs to the branch ' + branchName(f.branchId) + ', and no device is in that branch. Devices without a branch use the company default.' : 'Every device has a branch look of its own, so none falls back to this one.' }}</p>
                <div class="mt-2 flex flex-wrap gap-2">
                  @if (f.branchId && !hasDefault()) { <button type="button" class="btn btn-sm" (click)="makeCompanyDefault()">Use it for the whole company</button> }
                  @if (f.branchId) { <a routerLink="/admin/branches" class="btn-ghost btn-sm">Put devices in {{ branchName(f.branchId) }}</a> }
                </div>
              </div>
            }

            @if (f.id) {
              <div class="flex flex-wrap items-center gap-3 rounded-xl border border-brand-200 bg-linear-to-r from-brand-50 to-white px-4 py-3 dark:border-brand-500/30 dark:from-brand-500/10 dark:to-slate-900">
                <app-icon name="layers" size="h-6 w-6 text-brand-600" />
                <div class="min-w-0 flex-1">
                  <div class="text-sm font-semibold">{{ f.settings.canvas?.elements?.length ? 'A4 invoices use a design from the invoice builder' : 'Want full control of the layout?' }}</div>
                  <p class="text-xs text-slate-600 dark:text-slate-300">{{ f.settings.canvas?.elements?.length ? 'The logo, payment details, terms and footer below are still used by it; the 80 mm roll keeps the standard layout.' : 'Place the logo, texts, pictures, boxes, the items table and totals anywhere on the page, then export as PDF, Word or JSON.' }}</p>
                </div>
                <a class="btn btn-sm" [routerLink]="['/admin/templates/builder', f.id]"><app-icon name="edit" size="h-4 w-4" />Open the invoice builder</a>
              </div>
            } @else { <p class="hint">Create the template first; then you can also design it freely in the invoice builder.</p> }

            <fieldset class="space-y-4">
              <legend class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Branding</legend>
              <div class="flex flex-wrap items-center gap-4">
                <div class="flex h-20 w-32 items-center justify-center overflow-hidden rounded-xl border border-dashed border-slate-300 bg-slate-50 dark:border-slate-700 dark:bg-slate-900">
                  @if (logoUrl(); as u) { <img [src]="u" alt="Logo" class="max-h-full max-w-full object-contain" /> } @else { <span class="text-xs text-slate-400">No logo</span> }
                </div>
                <div class="space-y-2">
                  @if (f.id) {
                    <div class="flex gap-2"><button type="button" class="btn-ghost btn-sm" (click)="cropping.set(true)">{{ f.hasLogo ? 'Replace logo' : 'Upload logo' }}</button>
                      @if (f.hasLogo) { <button type="button" class="btn-ghost btn-sm" (click)="removeLogo()">Remove</button> }</div>
                    <p class="hint mt-0">Any picture: you crop and resize it before it is saved.</p>
                  } @else { <p class="hint mt-0">Save the template first, then add a logo.</p> }
                </div>
              </div>
              <div class="grid gap-4 sm:grid-cols-3">
                <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="showLogo" [(ngModel)]="f.settings.showLogo" /> Show logo</label>
                <label class="block"><span class="label">Logo position (A4)</span>
                  <select class="input" name="logoAlign" [(ngModel)]="f.settings.logoAlign"><option value="left">Left</option><option value="center">Centre</option><option value="right">Right</option></select></label>
                <label class="block"><span class="label">Accent colour</span>
                  <span class="flex gap-2"><input type="color" class="h-10 w-12 cursor-pointer rounded-lg border border-slate-300 bg-transparent p-1 dark:border-slate-700" name="accentpick" [(ngModel)]="f.settings.accent" /><input class="input font-mono" name="accent" [(ngModel)]="f.settings.accent" maxlength="7" /></span></label>
              </div>
            </fieldset>


            @if (importNotes().length) {
              <div class="rounded-xl border border-brand-100 bg-brand-50 px-4 py-3 text-sm dark:border-brand-500/30 dark:bg-brand-500/10">
                <div class="flex items-start justify-between gap-2"><div class="font-medium">Matched to your invoice{{ importSummary() ? ': ' + importSummary() : '' }}</div>
                  <button type="button" class="icon-btn h-6 w-6" (click)="importNotes.set([])" aria-label="Dismiss"><app-icon name="x" size="h-3.5 w-3.5" /></button></div>
                <ul class="mt-1 list-disc space-y-0.5 pl-5 text-xs text-slate-600 dark:text-slate-300">@for (n of importNotes(); track n) { <li>{{ n }}</li> }</ul>
                @if (sampleFile() && f.id) { <button type="button" class="btn-ghost btn-sm mt-2" (click)="cropSample.set(true)">Cut my logo out of the sample</button> }
              </div>
            }

            <fieldset class="space-y-4">
              <legend class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Layout (A4)</legend>
              <div class="grid gap-4 sm:grid-cols-2">
                <label class="block"><span class="label">Company block</span><select class="input" name="hstyle" [(ngModel)]="f.settings.headerStyle"><option value="plain">Plain</option><option value="band">On a coloured bar</option></select></label>
                <label class="block"><span class="label">Logo size</span><select class="input" name="lsize" [(ngModel)]="f.settings.logoSize"><option value="small">Small</option><option value="medium">Medium</option><option value="large">Large</option></select></label>
                <label class="block"><span class="label">Title style</span><select class="input" name="tstyle" [(ngModel)]="f.settings.titleStyle"><option value="band">On a coloured bar</option><option value="plain">Plain text</option><option value="underline">Text with a line</option></select></label>
                <label class="block"><span class="label">Title position</span><select class="input" name="talign" [(ngModel)]="f.settings.titleAlign"><option value="left">Left</option><option value="center">Centre</option><option value="right">Right</option></select></label>
                <label class="block"><span class="label">Invoice and customer details</span><select class="input" name="parties" [(ngModel)]="f.settings.partiesLayout"><option value="stacked">One list</option><option value="columns">Two columns</option></select></label>
                <label class="block"><span class="label">Table style</span><select class="input" name="tbl" [(ngModel)]="f.settings.tableStyle"><option value="lines">Simple lines</option><option value="striped">Striped rows</option><option value="grid">Grid</option><option value="solid">Coloured header</option></select></label>
                <label class="block"><span class="label">Totals</span><select class="input" name="totals" [(ngModel)]="f.settings.totalsAlign"><option value="full">Across the page</option><option value="right">Compact, on the right</option></select></label>
                <label class="block"><span class="label">Verification QR position</span><select class="input" name="qrpos" [(ngModel)]="f.settings.qrPosition"><option value="top-right">Top right, beside the details</option><option value="top-left">Top left, beside the details</option><option value="with-totals">Beside the totals</option><option value="left">Bottom left</option><option value="center">Bottom centre</option><option value="right">Bottom right</option></select></label>
                <label class="block"><span class="label">QR size</span><select class="input" name="qrsize" [(ngModel)]="f.settings.qrSize"><option value="small">Small</option><option value="medium">Medium</option><option value="large">Large</option></select></label>
                <label class="block"><span class="label">Row spacing and borders</span><select class="input" name="rowsp" [(ngModel)]="f.settings.rowSpacing"><option value="compact">Compact</option><option value="normal">Normal</option><option value="relaxed">Relaxed</option></select></label>
                <label class="block"><span class="label">Margins</span><select class="input" name="margins" [(ngModel)]="f.settings.margins"><option value="narrow">Narrow</option><option value="normal">Normal</option><option value="wide">Wide</option></select></label>
                <label class="block"><span class="label">Text size</span><select class="input" name="fsize" [(ngModel)]="f.settings.fontSize"><option [ngValue]="8">8 pt</option><option [ngValue]="9">9 pt</option><option [ngValue]="10">10 pt</option><option [ngValue]="11">11 pt</option><option [ngValue]="12">12 pt</option><option [ngValue]="13">13 pt</option></select></label>
              </div>

              <div>
                <div class="flex items-center justify-between"><span class="label mb-0">Table columns, in print order</span>
                  @if (f.settings.columns) { <button type="button" class="text-xs text-brand-600 underline" (click)="resetColumns()">Use the standard columns</button> }</div>
                <ul class="mt-2 divide-y divide-slate-100 rounded-xl border border-slate-200 dark:divide-slate-800 dark:border-slate-700">
                  @for (c of columnList(); track c.key; let i = $index) {
                    <li class="flex items-center gap-3 px-3 py-1.5 text-sm">
                      <input type="checkbox" class="h-4 w-4" [checked]="c.on" [disabled]="c.key === 'item' || c.key === 'total'" (change)="toggleColumn(c.key)" [attr.aria-label]="'Show ' + c.label" />
                      <span class="flex-1" [class.text-slate-400]="!c.on">{{ c.label }}</span>
                      <button type="button" class="icon-btn h-7 w-7" [disabled]="i === 0 || c.key === 'item' || c.key === 'total'" (click)="moveColumn(c.key, -1)" aria-label="Move up"><app-icon name="chevron" size="h-3.5 w-3.5" class="rotate-180" /></button>
                      <button type="button" class="icon-btn h-7 w-7" [disabled]="c.key === 'item' || c.key === 'total'" (click)="moveColumn(c.key, 1)" aria-label="Move down"><app-icon name="chevron" size="h-3.5 w-3.5" /></button>
                    </li>
                  }
                </ul>
                <p class="hint">The item is always first and the total always last.</p>
              </div>

              <details class="rounded-xl border border-slate-200 px-3 py-2 dark:border-slate-700">
                <summary class="cursor-pointer text-sm font-medium">Your own words for the labels</summary>
                <div class="mt-3 grid gap-3 sm:grid-cols-3">
                  <label class="block"><span class="label">Invoice number</span><input class="input" name="l1" [(ngModel)]="f.settings.labelInvoiceNo" maxlength="30" placeholder="Invoice No" /></label>
                  <label class="block"><span class="label">Date</span><input class="input" name="l2" [(ngModel)]="f.settings.labelDate" maxlength="30" placeholder="Date" /></label>
                  <label class="block"><span class="label">Customer</span><input class="input" name="l3" [(ngModel)]="f.settings.labelBuyer" maxlength="30" placeholder="Buyer" /></label>
                  <label class="block"><span class="label">Item column</span><input class="input" name="l4" [(ngModel)]="f.settings.labelItem" maxlength="30" placeholder="Item" /></label>
                  <label class="block"><span class="label">HS code column</span><input class="input" name="l5" [(ngModel)]="f.settings.labelHsCode" maxlength="30" placeholder="HS code" /></label>
                  <label class="block"><span class="label">Quantity column</span><input class="input" name="l6" [(ngModel)]="f.settings.labelQuantity" maxlength="30" placeholder="Qty" /></label>
                  <label class="block"><span class="label">Price column</span><input class="input" name="l7" [(ngModel)]="f.settings.labelPrice" maxlength="30" placeholder="Price" /></label>
                  <label class="block"><span class="label">Tax column</span><input class="input" name="l8" [(ngModel)]="f.settings.labelTax" maxlength="30" placeholder="VAT" /></label>
                  <label class="block"><span class="label">Total column</span><input class="input" name="l9" [(ngModel)]="f.settings.labelTotal" maxlength="30" placeholder="Total" /></label>
                  <label class="block"><span class="label">Grand total line</span><input class="input" name="l10" [(ngModel)]="f.settings.labelGrandTotal" maxlength="30" placeholder="TOTAL" /></label>
                </div>
              </details>
            </fieldset>

            <fieldset class="space-y-4">
              <legend class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Header</legend>
              <div class="grid gap-4 sm:grid-cols-2">
                <label class="block"><span class="label">Name to print (optional)</span><input class="input" name="dname" [(ngModel)]="f.settings.displayName" placeholder="Trading name instead of the registered name" maxlength="120" /></label>
                <label class="block"><span class="label">Invoice title (optional)</span><input class="input" name="title" [(ngModel)]="f.settings.invoiceTitle" placeholder="FISCAL TAX INVOICE" maxlength="40" /><span class="hint">Must contain FISCAL TAX INVOICE (ZIMRA), e.g. “FISCAL TAX INVOICE - ORIGINAL”. Empty = FISCAL TAX INVOICE.</span></label>
              </div>
              <label class="block"><span class="label">Address, phone, email, website (one per line)</span><textarea class="input min-h-20" name="header" [(ngModel)]="f.settings.headerText" maxlength="500"></textarea>
                <span class="hint">You can write placeholders such as [Company Address], [Telephone], [Email], [Website], [TAXPAYER TIN], [VAT NUMBER], [Company Name] or [Branch]. In the payment details and terms you can also use [Payment Method], [Bank Name], [Account Number], [Account Name], [Invoice No], [Date], [Total] and [Currency] (the bank is your default account in the invoice's currency, from Banks). They are filled in when the invoice prints, and left out when there is nothing to put there.</span></label>
              <div class="rounded-xl border border-slate-200 p-3 dark:border-slate-700">
                <div class="text-sm font-medium">Contact details for the placeholders</div>
                <p class="hint mt-0.5">Leave a box empty to use your branch's details, then the ones ZIMRA holds for the device (press Refresh config on the device to load them).</p>
                <div class="mt-3 grid gap-3 sm:grid-cols-2">
                  <label class="block sm:col-span-2"><span class="label">Address</span><input class="input" name="caddr" [(ngModel)]="f.settings.address" maxlength="300" placeholder="12 Samora Machel Ave, Harare, Zimbabwe" /></label>
                  <label class="block"><span class="label">Telephone</span><input class="input" name="cphone" [(ngModel)]="f.settings.phone" maxlength="50" placeholder="+263 242 700 000" /></label>
                  <label class="block"><span class="label">Email</span><input class="input" name="cemail" [(ngModel)]="f.settings.email" maxlength="120" placeholder="accounts@company.co.zw" /></label>
                  <label class="block sm:col-span-2"><span class="label">Website</span><input class="input" name="cweb" [(ngModel)]="f.settings.website" maxlength="120" placeholder="www.company.co.zw" /></label>
                </div>
              </div>
            </fieldset>

            <fieldset class="space-y-3">
              <legend class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">What to show</legend>
              <div class="grid gap-2 sm:grid-cols-2">
                <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="sbuyer" [(ngModel)]="f.settings.showBuyer" /> Buyer details</label>
                <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="spay" [(ngModel)]="f.settings.showPayments" /> Payment method</label>
                <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="shs" [(ngModel)]="f.settings.showHsCode" /> HS code column (A4)</label>
                <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="sqty" [(ngModel)]="f.settings.showQuantity" /> Quantity column</label>
                <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="sprice" [(ngModel)]="f.settings.showUnitPrice" /> Unit price column</label>
                <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="h-4 w-4" name="stax" [(ngModel)]="f.settings.showLineTax" /> Tax rate on each line</label>
              </div>
              <p class="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-500 dark:bg-slate-900"><app-icon name="lock" size="h-3.5 w-3.5" /> Always printed (required by ZIMRA): invoice and receipt numbers, device ID, tax summary and total, verification QR, device signature, ZIMRA receipt ID and any validation notices.</p>
            </fieldset>

            <fieldset class="space-y-4">
              <legend class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Wording</legend>
              <div class="mb-3"><span class="label">Fill from your banks</span><app-bank-picker (picked)="fillBank($event)" /></div>
              <label class="block"><span class="label">Payment details (bank account, mobile wallet…)</span><textarea class="input min-h-16" name="bank" [(ngModel)]="f.settings.bankDetails" maxlength="500"></textarea></label>
              <label class="block"><span class="label">Terms</span><textarea class="input min-h-16" name="terms" [(ngModel)]="f.settings.terms" maxlength="1000"></textarea></label>
              <label class="block"><span class="label">Footer message</span><input class="input" name="footer" [(ngModel)]="f.settings.footerText" maxlength="300" placeholder="Thank you for your business" /></label>
            </fieldset>

            <div class="flex flex-wrap justify-between gap-2 border-t border-slate-100 pt-4 dark:border-slate-800">
              <span>@if (f.id) { <button type="button" class="btn-danger" (click)="remove()">Delete template</button> }</span>
              <span class="flex gap-2"><button type="button" class="btn-ghost" (click)="editing.set(null)">Close</button><button class="btn" type="submit" [disabled]="busy()">{{ f.id ? 'Save changes' : 'Create template' }}</button></span>
            </div>
          </form>
        } @else {
          <div class="card flex flex-col items-center gap-2 py-16 text-center">
            <div class="flex h-12 w-12 items-center justify-center rounded-2xl bg-slate-100 text-slate-400 dark:bg-slate-800"><app-icon name="receipt" size="h-6 w-6" /></div>
            <p class="font-medium">Choose a template, or create the company default</p>
            <p class="max-w-sm text-sm text-slate-500">You will see your invoice update as you change things.</p>
          </div>
        }
      </div>

      <!-- preview -->
      <div class="xl:col-span-4">
        <div class="card xl:sticky xl:top-0">
          <div class="mb-3 flex items-center justify-between">
            <div class="card-title mb-0">Live preview</div>
            <div class="flex items-center gap-2">
            <button type="button" class="btn-ghost btn-sm" (click)="enlarge()" [disabled]="!previewUrl()" title="See the invoice full size"><app-icon name="eye" size="h-4 w-4" />Enlarge</button>
            <div class="inline-flex rounded-lg border border-slate-200 p-0.5 text-xs dark:border-slate-700">
              <button type="button" class="rounded-md px-3 py-1" [class]="format() === 'a4' ? 'bg-brand-600 text-white' : 'text-slate-500'" (click)="setFormat('a4')">A4</button>
              <button type="button" class="rounded-md px-3 py-1" [class]="format() === 'receipt' ? 'bg-brand-600 text-white' : 'text-slate-500'" (click)="setFormat('receipt')">80 mm receipt</button>
            </div>
            </div>
          </div>
          <div class="flex min-h-64 items-start justify-center overflow-auto rounded-xl bg-slate-100 p-3 dark:bg-slate-950">
            @if (previewUrl(); as u) { <img [src]="u" alt="Invoice preview. Click to enlarge." class="max-w-full cursor-zoom-in rounded bg-white shadow-md" [class.opacity-60]="previewing()" (click)="enlarge()" /> }
            @else { <p class="self-center text-sm text-slate-500">{{ editing() ? (previewError() || 'Rendering…') : 'Open a template to see a preview.' }}</p> }
          </div>
          @if (previewError() && previewUrl()) { <p class="hint text-red-600">{{ previewError() }}</p> }
          <p class="hint">Sample figures. Your real invoices use their own lines and totals.</p>
        </div>
      </div>
    </div>
    @if (big()) {
      <div class="fixed inset-0 z-[60] flex flex-col bg-slate-900/90 backdrop-blur-sm" role="dialog" aria-modal="true" aria-label="Invoice preview, full size" (keydown.escape)="big.set(false)" tabindex="-1">
        <div class="flex items-center justify-between gap-3 px-5 py-3 text-white">
          <div class="flex items-center gap-3"><span class="font-semibold">Preview</span>
            <span class="inline-flex rounded-lg border border-white/30 p-0.5 text-xs"><button type="button" class="rounded-md px-3 py-1" [class]="format() === 'a4' ? 'bg-white text-slate-900' : 'text-white/80'" (click)="setFormat('a4'); loadBig()">A4</button><button type="button" class="rounded-md px-3 py-1" [class]="format() === 'receipt' ? 'bg-white text-slate-900' : 'text-white/80'" (click)="setFormat('receipt'); loadBig()">80 mm receipt</button></span></div>
          <div class="flex items-center gap-2"><button type="button" class="rounded-lg border border-white/30 px-3 py-1.5 text-sm text-white hover:bg-white/10" (click)="fitWidth.set(!fitWidth())">{{ fitWidth() ? 'Actual size' : 'Fit to screen' }}</button><button type="button" class="rounded-lg p-2 text-white hover:bg-white/10" (click)="big.set(false)" aria-label="Close"><app-icon name="x" size="h-5 w-5" /></button></div>
        </div>
        <div class="flex-1 overflow-auto px-4 pb-6" (click)="big.set(false)">
          @if (bigUrl(); as u) { <img [src]="u" alt="Invoice preview, full size" class="mx-auto rounded bg-white shadow-2xl" [class]="fitWidth() ? 'max-h-full max-w-full object-contain' : 'max-w-none'" (click)="$event.stopPropagation()" /> }
          @else { <p class="pt-24 text-center text-white/80">Drawing the full-size preview…</p> }
        </div>
      </div>
    }
    @if (cropping()) { <app-logo-cropper title="Invoice logo" (done)="uploadLogo($event)" (closed)="cropping.set(false)" /> }
    @if (cropSample() && sampleFile(); as sample) { <app-logo-cropper title="Cut your logo out of the sample" [file]="sample" (done)="cropSample.set(false); uploadLogo($event)" (closed)="cropSample.set(false)" /> }
  `,
})
export class TemplatesComponent implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);

  readonly templates = signal<InvoiceTemplate[]>([]);
  readonly branches = signal<Branch[]>([]);
  readonly editing = signal<Editing | null>(null);
  readonly busy = signal(false);
  readonly format = signal<'a4' | 'receipt'>('a4');
  readonly previewUrl = signal<string | null>(null);
  readonly previewing = signal(false);
  readonly previewError = signal('');
  readonly logoUrl = signal<string | null>(null);
  readonly cropping = signal(false);
  readonly brand = signal<CompanyBrand | null>(null);
  slug = '';

  private async loadBrand(): Promise<void> {
    try { const b = await this.api.get<CompanyBrand>('/api/v1/company/brand'); this.brand.set(b); this.slug = b.slug ?? ''; } catch { /* the card is just not shown */ }
  }
  async saveSlug(): Promise<void> {
    try { const b = await this.api.put<CompanyBrand>('/api/v1/company/brand', { slug: this.slug.trim().toLowerCase() }); this.brand.set(b); this.slug = b.slug ?? ''; this.toast.ok('Sign-in link saved.'); } catch (e) { this.toast.err(e); }
  }
  async copySignIn(url: string): Promise<void> {
    try { await navigator.clipboard.writeText(url); this.toast.ok('Link copied.'); } catch { this.toast.err(new Error('Could not copy. Select the link and copy it yourself: ' + url)); }
  }
  private timer: ReturnType<typeof setTimeout> | undefined;
  private seq = 0;

  readonly hasDefault = computed(() => this.templates().some(t => !t.branchId));
  readonly freeBranches = computed(() => this.branches().filter(b => !this.templates().some(t => t.branchId === b.id)));

  branchName(id: string): string { return this.branches().find(b => b.id === id)?.name ?? ''; }

  async ngOnInit(): Promise<void> {
    void this.loadBrand();
    try { this.branches.set(await this.api.get<Branch[]>('/api/v1/branches')); } catch { /* no branch permission: company default only */ }
    await this.load();
    const first = this.templates()[0];
    if (first) this.open(first);
  }

  ngOnDestroy(): void { clearTimeout(this.timer); this.release(this.previewUrl()); this.release(this.logoUrl()); }

  private release(url: string | null): void { if (url) URL.revokeObjectURL(url); }

  async load(): Promise<void> {
    try { this.templates.set(await this.api.get<InvoiceTemplate[]>('/api/v1/templates')); } catch (e) { this.toast.err(e); }
  }

  open(t: InvoiceTemplate): void {
    this.editing.set({ id: t.id, branchId: t.branchId, name: t.name, settings: { ...defaultSettings(), ...t.settings }, hasLogo: t.hasLogo, deviceCount: t.deviceCount });
    void this.loadLogo(); this.bump(0);
  }

  /** A branch look that no device uses becomes the company default, so every device without a look of its own prints with it. */
  async makeCompanyDefault(): Promise<void> {
    const f = this.editing(); if (!f?.id || !f.branchId) return;
    this.editing.set({ ...f, branchId: undefined });
    await this.save();
  }

  startNew(branchId: string | null): void {
    if (branchId === '') return;
    const basis = this.templates().find(t => !t.branchId)?.settings;   // a branch look starts as a copy of the company default
    this.editing.set({ branchId: branchId ?? undefined, name: branchId ? this.branchName(branchId) + ' look' : 'Company default', settings: { ...defaultSettings(), ...basis }, hasLogo: false });
    this.release(this.logoUrl()); this.logoUrl.set(null); this.bump(0);
  }

  setFormat(f: 'a4' | 'receipt'): void { this.format.set(f); this.bump(0); }

  /** Re-render the preview shortly after the last change. */
  bump(delay = 500): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.renderPreview(), delay);
  }

  private async renderPreview(): Promise<void> {
    const f = this.editing(); if (!f) return;
    const mine = ++this.seq;
    this.previewing.set(true);
    try {
      const blob = await this.api.postBlob('/api/v1/templates/preview', { settings: this.clean(f.settings), format: this.format(), templateId: f.id ?? null });
      if (mine !== this.seq) return;   // a newer render is already on its way
      const old = this.previewUrl();
      this.previewUrl.set(URL.createObjectURL(blob)); this.release(old); this.previewError.set('');
    } catch (e) {
      if (mine === this.seq) this.previewError.set(await this.readable(e));
    } finally { if (mine === this.seq) this.previewing.set(false); }
  }

  private async readable(e: unknown): Promise<string> {
    const body = (e as { error?: Blob }).error;
    if (body instanceof Blob) { try { const p = JSON.parse(await body.text()) as { title?: string }; if (p.title) return p.title; } catch { /* not JSON */ } }
    return 'The preview could not be drawn.';
  }

  private clean(s: TemplateSettings): TemplateSettings {
    const blank = (v?: string) => (v && v.trim() ? v : undefined);
    return { ...s, address: blank(s.address), phone: blank(s.phone), email: blank(s.email), website: blank(s.website), displayName: blank(s.displayName), headerText: blank(s.headerText), invoiceTitle: blank(s.invoiceTitle), bankDetails: blank(s.bankDetails), terms: blank(s.terms), footerText: blank(s.footerText) };
  }

  private async loadLogo(): Promise<void> {
    const f = this.editing();
    this.release(this.logoUrl()); this.logoUrl.set(null);
    if (!f?.id || !f.hasLogo) return;
    try { this.logoUrl.set(URL.createObjectURL(await this.api.blob(`/api/v1/templates/${f.id}/logo`))); } catch { /* shown as "No logo" */ }
  }

  async save(): Promise<void> {
    const f = this.editing(); if (!f) return;
    this.busy.set(true);
    try {
      const saved = await this.api.put<InvoiceTemplate>('/api/v1/templates', { id: f.id ?? null, branchId: f.branchId ?? null, name: f.name.trim(), settings: this.clean(f.settings) });
      this.toast.ok('Template saved. New PDFs use it straight away.');
      await this.load(); this.open(this.templates().find(t => t.id === saved.id) ?? saved);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async remove(): Promise<void> {
    const f = this.editing(); if (!f?.id) return;
    if (!(await this.dialog.confirm(`Delete "${f.name}"? Invoices go back to ${f.branchId ? 'the company default' : 'the standard layout'}.`, 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/templates/${f.id}`); this.toast.ok('Template deleted.'); this.editing.set(null); this.release(this.previewUrl()); this.previewUrl.set(null); await this.load(); }
    catch (e) { this.toast.err(e); }
  }


  // ---- inherit the look of an existing invoice (read by the AI, applied here, adjusted by hand) ----
  readonly big = signal(false);
  readonly bigUrl = signal<string | null>(null);
  readonly fitWidth = signal(true);

  /** The whole invoice on a big screen: drawn again at a higher resolution so small print stays readable. */
  enlarge(): void { this.big.set(true); void this.loadBig(); }
  async loadBig(): Promise<void> {
    const f = this.editing(); if (!f) return;
    this.release(this.bigUrl()); this.bigUrl.set(null);
    try { this.bigUrl.set(URL.createObjectURL(await this.api.postBlob('/api/v1/templates/preview', { settings: this.clean(f.settings), format: this.format(), templateId: f.id ?? null, large: true }))); }
    catch (e) { this.big.set(false); this.toast.err(new Error(await this.readable(e))); }
  }

  readonly analysing = signal(false);
  readonly importNotes = signal<string[]>([]);
  readonly importSummary = signal('');
  readonly sampleFile = signal<File | null>(null);
  readonly cropSample = signal(false);

  async analyse(input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0]; input.value = '';
    if (!file) return;
    if (file.size > 4_000_000) { this.toast.err(new Error('The file must be at most 4 MB.')); return; }
    this.analysing.set(true);
    try {
      const fd = new FormData(); fd.append('file', file, file.name);
      const r = await this.api.post<TemplateAnalysis>('/api/v1/templates/analyze', fd);
      if (!this.editing()) this.startNew(null);
      const f = this.editing(); if (!f) return;
      this.editing.set({ ...f, settings: { ...defaultSettings(), ...r.settings, columns: r.settings.columns ?? null } });
      this.sampleFile.set(file.type.startsWith('image/') ? file : null);
      this.importSummary.set(r.summary ?? ''); this.importNotes.set(r.notes.length ? r.notes : ['Check the preview and adjust anything that is not quite right.']);
      this.bump(0); this.toast.ok('Your invoice was read. Check the preview, adjust, then save.');
    } catch (e) { this.toast.err(e); } finally { this.analysing.set(false); }
  }

  private static readonly COLUMN_LABELS: Record<string, string> = { item: 'Item', hs: 'HS code', qty: 'Quantity', price: 'Unit price', tax: 'Tax rate', total: 'Total' };

  /** Every column with whether it is printed, in print order (the standard order until the person changes it). */
  columnList(): { key: string; label: string; on: boolean }[] {
    const s = this.editing()?.settings; if (!s) return [];
    const all = ['item', 'hs', 'qty', 'price', 'tax', 'total'];
    const active = s.columns?.length ? s.columns : ['item', ...(s.showHsCode ? ['hs'] : []), ...(s.showQuantity ? ['qty'] : []), ...(s.showUnitPrice ? ['price'] : []), 'total'];
    const order = [...active.filter(k => k !== 'total'), ...all.filter(k => !active.includes(k) && k !== 'total'), 'total'];
    return order.map(k => ({ key: k, label: TemplatesComponent.COLUMN_LABELS[k], on: active.includes(k) || k === 'item' || k === 'total' }));
  }

  private setColumns(list: { key: string; on: boolean }[]): void {
    const f = this.editing(); if (!f) return;
    const cols = list.filter(c => c.on).map(c => c.key);
    this.editing.set({ ...f, settings: { ...f.settings, columns: cols, showHsCode: cols.includes('hs'), showQuantity: cols.includes('qty'), showUnitPrice: cols.includes('price') } });
    this.bump(0);
  }
  toggleColumn(key: string): void { this.setColumns(this.columnList().map(c => (c.key === key ? { ...c, on: !c.on } : c))); }
  moveColumn(key: string, dir: -1 | 1): void {
    const list = this.columnList(); const i = list.findIndex(c => c.key === key); const j = i + dir;
    if (i < 0 || j < 1 || j > list.length - 2) return;   // item stays first, total stays last
    [list[i], list[j]] = [list[j], list[i]];
    this.setColumns(list);
  }
  resetColumns(): void { const f = this.editing(); if (!f) return; this.editing.set({ ...f, settings: { ...f.settings, columns: null } }); this.bump(0); }

  private readonly banking = inject(BankingStore);

  /** Choosing a bank and an account writes its details into the payment-details box (editable afterwards). */
  fillBank(a: BankAccount): void {
    const f = this.editing(); if (!f) return;
    this.editing.set({ ...f, settings: { ...f.settings, bankDetails: this.banking.describe(a).slice(0, 500) } });
    this.bump(0);
  }

  async uploadLogo(blob: Blob): Promise<void> {
    this.cropping.set(false);
    const f = this.editing();
    if (!f?.id) return;
    try {
      const fd = new FormData(); fd.append('file', blob, blob.type === 'image/jpeg' ? 'logo.jpg' : 'logo.png');
      await this.api.post(`/api/v1/templates/${f.id}/logo`, fd);
      this.toast.ok('Logo uploaded.');
      await this.load(); this.editing.update(x => (x ? { ...x, hasLogo: true } : x)); await this.loadLogo(); this.bump(0);
    } catch (e) { this.toast.err(e); }
  }

  async removeLogo(): Promise<void> {
    const f = this.editing(); if (!f?.id) return;
    try { await this.api.delete(`/api/v1/templates/${f.id}/logo`); await this.load(); this.editing.update(x => (x ? { ...x, hasLogo: false } : x)); await this.loadLogo(); this.bump(0); }
    catch (e) { this.toast.err(e); }
  }
}
