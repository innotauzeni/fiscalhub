import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { BillingInvoice, BillingOverview, GoLiveStatus, PayMethod, PaymentRow, Plan, planModuleLabels, usagePricingLines } from '../core/models';
import { DialogService, ToastService } from '../core/ui.services';
import { EmptyComponent } from '../shared/list.components';
import { ModalComponent } from '../shared/modal.components';
import { limit, money } from '../shared/public.components';
import { IconComponent, PageHeaderComponent, StatusComponent } from '../shared/ui.components';
import { BillingAddonsComponent } from './billing-addons.component';


/** A company's subscription: plan and usage, invoices, changing plan, and paying by Paynow (cards, ZimSwitch, EcoCash, OneMoney) or bank transfer. */
@Component({
  selector: 'app-billing',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, DatePipe, RouterLink, IconComponent, ModalComponent, PageHeaderComponent, StatusComponent, EmptyComponent, BillingAddonsComponent],
  template: `
    <app-page-header title="Billing" subtitle="Your plan, what you are using, and your invoices.">
      <button class="btn-ghost" (click)="load()"><app-icon name="refresh" size="h-4 w-4" />Refresh</button>
    </app-page-header>

    @if (o()?.access; as ac) {
      @if (ac.locked) {
        <div class="mb-5 rounded-2xl border p-5" [class]="ac.reason === 'NEW_ACCOUNT' ? 'border-brand-100 bg-brand-50/70 dark:border-brand-500/30 dark:bg-brand-500/10' : 'border-red-200 bg-red-50 dark:border-red-900/50 dark:bg-red-900/20'">
          @if (ac.reason === 'NEW_ACCOUNT') {
            <div class="font-semibold">Welcome! One last step.</div>
            <p class="mt-1 text-sm text-slate-600 dark:text-slate-300">Your account is approved. Pay your first invoice below (or enter the activation code we gave you) and the rest of the console opens straight away.</p>
          } @else if (ac.reason === 'TRIAL_EXPIRED') {
            <div class="font-semibold text-red-700 dark:text-red-300">Your trial has ended</div>
            <p class="mt-1 text-sm text-slate-700 dark:text-slate-300">Everything except Billing is closed until you pay for a plan. Your data is safe.</p>
          } @else {
            <div class="font-semibold text-red-700 dark:text-red-300">Your subscription expired{{ ac.expiredAt ? ' on ' + (ac.expiredAt | date: 'd MMM yyyy') : '' }}</div>
            <p class="mt-1 text-sm text-slate-700 dark:text-slate-300">Everything except Billing is closed until the renewal invoice is settled. Your data is safe.</p>
          }
          @if (!openInvoices().length) { <button class="btn mt-3" [disabled]="busy()" (click)="renew()">Create my renewal invoice</button> }
        </div>
      }
    }
    @if (auth.demoOnly()) {
      <div class="mb-5 rounded-2xl border border-sky-200 bg-sky-50 p-4 text-sm dark:border-sky-900/50 dark:bg-sky-900/20">You are on a free trial: you can try everything with <strong>demo (test) devices</strong>. Pay for your plan to use live devices.</div>
    }
    @if (loaded() && !o()?.subscription && !o()?.invoices?.length) {
      <div class="card"><app-empty icon="card" title="No subscription yet" hint="Your company is not on a plan, so nothing is billed and nothing is limited." /></div>
    } @else if (o(); as ov) {
      @if (toPay().length) {
        <div class="mb-5 flex flex-wrap items-center justify-between gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-4 dark:border-amber-900/50 dark:bg-amber-900/20">
          <div class="flex items-center gap-3">
            <span class="text-amber-600"><app-icon name="alert" /></span>
            <div class="text-sm"><strong>{{ toPay().length }} unpaid {{ toPay().length === 1 ? 'invoice' : 'invoices' }}</strong> totalling <strong>{{ fmt(owing(), ov.options.currency) }}</strong>.</div>
          </div>
          <button class="btn" (click)="startPay(toPay()[0])">Pay now</button>
        </div>
      }
      @if (awaiting().length) {
        <div class="mb-5 flex items-start gap-3 rounded-2xl border border-sky-200 bg-sky-50 p-4 text-sm dark:border-sky-900/50 dark:bg-sky-900/20">
          <span class="text-sky-600"><app-icon name="check" /></span>
          <div><strong>Payment sent</strong> for {{ awaitingNumbers() }}. We confirm it once it reaches us, usually within one working day, and email you.
            If we sent you an activation code, enter it below to switch everything on now.</div>
        </div>
      }

      @if (ov.subscription; as s) {
        <div class="mb-5 grid gap-5 lg:grid-cols-3">
          <div class="card lg:col-span-1">
            <div class="flex items-start justify-between">
              <div><div class="text-xs font-medium uppercase tracking-wide text-slate-500">Current plan</div><div class="mt-1 text-2xl font-semibold">{{ s.planName }}</div></div>
              <app-status [value]="s.status" />
            </div>
            <dl class="mt-4 space-y-2 text-sm">
              <div class="flex justify-between"><dt class="text-slate-500">Billing</dt><dd class="font-medium capitalize">{{ s.billingCycle }}</dd></div>
              @if (ov.plan) { <div class="flex justify-between"><dt class="text-slate-500">Price</dt><dd class="font-medium">{{ price(ov.plan, s.billingCycle) }} / {{ s.billingCycle === 'yearly' ? 'year' : 'month' }}</dd></div> }
              @if (ov.resellerDiscountPercent) { <div class="rounded-lg bg-emerald-50 px-2 py-1 text-xs text-emerald-800 dark:bg-emerald-900/25 dark:text-emerald-200">Reseller price through {{ ov.resellerName }}: {{ ov.resellerDiscountPercent }}% off the list price</div> }
              @if (s.status === 'Trial' && s.trialEndsAt) { <div class="flex justify-between"><dt class="text-slate-500">Trial ends</dt><dd class="font-medium">{{ s.trialEndsAt | date: 'd MMM yyyy' }}</dd></div> }
              <div class="flex justify-between"><dt class="text-slate-500">{{ s.status === 'Trial' ? 'First invoice' : 'Paid until' }}</dt><dd class="font-medium">{{ s.currentPeriodEnd | date: 'd MMM yyyy' }}</dd></div>
              @if (s.outstanding > 0) { <div class="flex justify-between"><dt class="text-slate-500">Outstanding</dt><dd class="font-semibold text-amber-600">{{ fmt(s.outstanding, ov.options.currency) }}</dd></div> }
            </dl>
            <button class="btn-ghost mt-5 w-full" (click)="plansOpen.set(true)">Change plan</button>
          </div>

          <div class="card lg:col-span-2">
            <div class="card-title">Usage</div>
            <div class="grid gap-5 sm:grid-cols-2">
              @for (u of usage(); track u.label) {
                <div>
                  <div class="mb-1 flex justify-between text-sm"><span class="font-medium">{{ u.label }}</span><span class="text-slate-500">{{ u.used.toLocaleString() }} / {{ lim(u.max) }}</span></div>
                  <div class="h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-slate-800"><div class="h-full rounded-full transition-all" [class]="u.pct >= 100 ? 'bg-red-500' : u.pct >= 80 ? 'bg-amber-500' : 'bg-emerald-500'" [style.width.%]="u.max === 0 ? 4 : Math.min(100, u.pct)"></div></div>
                </div>
              }
            </div>
            <p class="hint mt-4">Devices, users and branches cannot go beyond your plan. Change plan to make room.</p>
          </div>
        </div>
      }

      @if (ov.subscription) { <app-billing-addons (changed)="load()" /> }

      @if (openInvoices().length) {
        <div class="card mb-5">
          <div class="card-title">Have an activation code?</div>
          <p class="text-sm text-slate-600 dark:text-slate-400">If you paid in cash or by bank transfer and we sent you a code, type it here to settle the invoice and switch everything on.</p>
          <form class="mt-3 flex flex-wrap items-end gap-3" (ngSubmit)="redeem()">
            <label class="block grow sm:max-w-xs"><span class="label">Activation code</span>
              <input class="input font-mono uppercase tracking-widest" name="code" [(ngModel)]="activation" placeholder="XXXX-XXXX-XXXX" maxlength="20" autocomplete="off" autocapitalize="characters" spellcheck="false" /></label>
            <button class="btn" type="submit" [disabled]="busy() || activation.trim().length < 12">Activate</button>
          </form>
        </div>
      }

      @if (goLive(); as g) {
        <div class="card mb-5">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0">
              <div class="card-title mb-1">Live FDMS devices</div>
              @if (g.approved) {
                <p class="text-sm text-slate-600 dark:text-slate-400"><span class="badge badge-ok">Approved</span> Your company can register live (Production) devices with ZIMRA. Demo (Test) devices always work.</p>
              } @else {
                <p class="text-sm text-slate-600 dark:text-slate-400">ZIMRA FDMS has a <strong>demo</strong> environment for testing and a <strong>live</strong> one for real invoices. Every company starts on a demo device.
                  To go live, ZIMRA asks for <strong>six test documents</strong> from your demo device. When all six are ticked below, send them to us: we forward them to ZIMRA, and when ZIMRA issues your live device we give you its ID and activation key here.</p>
                @if (!g.latest || g.latest.status === 'Rejected') {
                  <div class="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
                    @for (d of g.testDocuments ?? []; track d.slot) {
                      <div class="flex items-start gap-2 rounded-lg border px-3 py-2 text-sm" [class]="d.receiptId ? 'border-emerald-200 bg-emerald-50/60 dark:border-emerald-900/50 dark:bg-emerald-900/20' : 'border-slate-200 dark:border-slate-700'">
                        <app-icon [name]="d.receiptId ? 'check' : 'circle'" size="h-4 w-4" [class]="d.receiptId ? 'mt-0.5 text-emerald-600' : 'mt-0.5 text-slate-400'" />
                        <div class="min-w-0"><div class="font-medium">{{ d.slot }}</div>
                          @if (d.receiptId) { <div class="truncate text-xs text-slate-500">{{ d.invoiceNo }} · {{ d.total }} {{ d.currency }}{{ d.original ? ' · against ' + d.original : '' }}</div> }
                          @else { <div class="text-xs text-slate-500">{{ d.problem }}</div> }</div>
                      </div>
                    }
                  </div>
                  @if (!g.testSetComplete) { <p class="hint mt-2">Issue them on your demo device under <a routerLink="/receipts/new" class="text-brand-600 hover:underline">New invoice</a>: an invoice in USD, then a credit note and a debit note against it (use <em>Correct…</em> and <em>Debit note</em> on the invoice), and the same in ZWG.</p> }
                }
                @if (g.latest?.status === 'Pending') { <p class="mt-2 text-sm"><span class="badge badge-warn">Request sent</span> We have your six test documents and are forwarding them to ZIMRA. We will contact {{ g.latest!.contactName }} on {{ g.latest!.contactPhone }}.</p> }
                @if (g.latest?.status === 'SentToZimra') { <p class="mt-2 text-sm"><span class="badge badge-info">With ZIMRA</span> Your test documents were sent to ZIMRA on {{ g.latest!.sentToZimraAt | date: 'd MMM y' }}. We let you know as soon as ZIMRA issues your live device.</p> }
                @if (g.latest?.status === 'AwaitingPayment') { <p class="mt-2 text-sm"><span class="badge badge-ok">Approved</span> Pay invoice <strong>{{ g.latest!.invoiceNumber }}</strong> above and live devices switch on at once.</p> }
                @if (g.latest?.status === 'Rejected') { <p class="mt-2 text-sm text-red-600">Your last request was declined: {{ g.latest!.decisionReason }}</p> }
                @if (!g.canRequest && g.blocked && g.latest?.status !== 'Pending') { <p class="hint">{{ g.blocked }}</p> }
              }
            </div>
            @if (!g.approved && g.canRequest) { <button class="btn" (click)="goLiveOpen.set(true)">Send test documents to go live</button> }
          </div>
          @if (g.latest?.liveDevice; as dev) {
            <div class="mt-4 rounded-xl border border-emerald-200 bg-emerald-50 p-4 dark:border-emerald-900/50 dark:bg-emerald-900/20">
              <div class="mb-1 font-semibold text-emerald-800 dark:text-emerald-200">Your live ZIMRA device is ready</div>
              <div class="flex flex-wrap gap-x-6 gap-y-1 text-sm"><span>Device ID <strong class="font-mono">{{ dev.deviceId }}</strong></span>
                <span>Activation key <strong class="font-mono">{{ showKey() ? dev.activationKey : '••••••••' }}</strong> <button type="button" class="text-xs text-brand-600 hover:underline" (click)="showKey.set(!showKey())">{{ showKey() ? 'hide' : 'show' }}</button></span>
                @if (dev.serialNo) { <span>Serial <strong class="font-mono">{{ dev.serialNo }}</strong></span> }</div>
              @if (g.approved) { <button type="button" class="btn mt-3" (click)="registerLive(dev)"><app-icon name="cpu" size="h-4 w-4" />Register the live device</button> }
              @else { <p class="hint">It can be registered once the go-live invoice above is paid.</p> }
            </div>
          }
        </div>
      }

      <div class="card">
        <div class="card-title">Invoices</div>
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>Invoice</th><th>Period</th><th>Amount</th><th>Due</th><th>Status</th><th class="text-right">Actions</th></tr></thead>
            <tbody>
              @for (i of ov.invoices; track i.id) {
                <tr>
                  <td><div class="font-medium">{{ i.number }}</div><div class="hint mt-0 max-w-64 truncate">{{ i.description }}</div></td>
                  <td class="whitespace-nowrap">{{ i.periodStart | date: 'd MMM' }} – {{ i.periodEnd | date: 'd MMM yyyy' }}</td>
                  <td class="whitespace-nowrap font-medium">{{ fmt(i.total, i.currency) }}</td>
                  <td class="whitespace-nowrap">{{ i.dueAt | date: 'd MMM yyyy' }}</td>
                  <td>
                    <app-status [value]="i.status === 'Open' ? 'Pending' : i.status === 'Paid' ? 'Active' : 'Cancelled'" />
                    @if (waiting(i)) { <div class="hint">Payment awaiting confirmation</div> }
                    @if (checking(i)) { <div class="hint flex items-center gap-1 font-medium text-sky-700 dark:text-sky-300"><span class="h-2 w-2 animate-pulse rounded-full bg-sky-500"></span>Checking your payment…</div> }
                    @if (codeSent(i)) { <div class="hint font-medium text-brand-700 dark:text-brand-100">Payment confirmed: enter your activation code above</div> }
                    @if (i.status === 'Paid' && i.paymentMethod) { <div class="hint">{{ i.paymentMethod }}</div> }
                  </td>
                  <td><div class="flex justify-end gap-1.5">
                    <button class="icon-btn" (click)="pdf(i)" title="Download PDF" aria-label="Download PDF"><app-icon name="download" size="h-4 w-4" /></button>
                    @if (i.status === 'Open') {
                      @if (checking(i)) { <button class="btn btn-sm" [disabled]="busy()" (click)="checkInvoice(i)">Check now</button><button class="btn-ghost btn-sm" (click)="startPay(i)">Pay again</button> }
                      @else if (waiting(i)) { <button class="btn-ghost btn-sm" (click)="startPay(i)">Pay another way</button> }
                      @else { <button class="btn btn-sm" (click)="startPay(i)">Pay</button> }
                    }
                  </div></td>
                </tr>
              } @empty { <tr><td colspan="6"><app-empty icon="receipt" title="No invoices yet" hint="Your first invoice appears when your trial ends or your plan is billed." /></td></tr> }
            </tbody>
          </table>
        </div>
      </div>
    }

    <!-- pay -->
    @if (paying(); as inv) {
      <app-modal [title]="'Pay invoice ' + inv.number" [subtitle]="fmt(inv.total, inv.currency) + ' · ' + inv.description" width="max-w-xl" (closed)="closePay()">
        @if (result(); as r) {
          <div class="rounded-xl border border-slate-200 p-5 text-center dark:border-slate-800">
            @if (r.status === 'Paid') {
              <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-emerald-100 text-emerald-600 dark:bg-emerald-900/40"><app-icon name="check" size="h-6 w-6" /></div>
              <p class="mt-3 font-semibold">Payment received. Thank you!</p>
            } @else if (r.status === 'AwaitingConfirmation') {
              <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-sky-100 text-sky-600 dark:bg-sky-900/40"><app-icon name="check" size="h-6 w-6" /></div>
              <p class="mt-3 font-semibold">We have your payment details</p>
              <p class="mt-1 text-sm text-slate-600 dark:text-slate-400">We will confirm the payment once it reaches us and email you. This usually takes one working day.</p>
            } @else if (r.status === 'Pending') {
              <p class="font-semibold">Waiting for your payment…</p>
              @if (r.instructions) { <p class="mt-2 text-sm text-slate-600 dark:text-slate-400">{{ r.instructions }}</p> }
              <p class="hint mt-2">This page updates by itself when the payment goes through.</p>
              <button class="btn-ghost mt-4" (click)="check(r)">Check now</button>
            } @else {
              <p class="font-semibold text-red-600">The payment did not go through</p>
              @if (r.notes) { <p class="mt-1 text-sm text-slate-600 dark:text-slate-400">{{ r.notes }}</p> }
              <button class="btn mt-4" (click)="result.set(null)">Try again</button>
            }
          </div>
          <div class="mt-5 flex justify-end"><button class="btn-ghost" (click)="closePay()">Close</button></div>
        } @else {
          @if (!methods().length) {
            <p class="rounded-lg bg-amber-50 p-3 text-sm text-amber-800 dark:bg-amber-900/25 dark:text-amber-200">No payment method is available yet. Please contact support.</p>
          } @else {
            <div class="mb-4 flex flex-wrap gap-1 border-b border-slate-200 dark:border-slate-800" role="tablist">
              @for (m of methods(); track m.id) { <button type="button" class="tab" [class.tab-active]="tab() === m.id" (click)="tab.set(m.id)">{{ m.name }}</button> }
            </div>

            @if (method(); as m) {
              @if (m.kind === 'redirect') {
                <p class="text-sm text-slate-600 dark:text-slate-400">
                  @if (m.id === 'stripe') { You will be taken to Stripe's secure page to pay with an <strong>international Visa or Mastercard</strong>. Then you come straight back here. }
                  @else { You will be taken to Paynow's secure page to pay with <strong>Visa, Mastercard, ZimSwitch</strong> or a wallet. Then you come straight back here. }
                </p>
                <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" (click)="closePay()">Cancel</button><button class="btn" [disabled]="busy()" (click)="payRedirect(m)">Pay {{ fmt(inv.total, inv.currency) }}</button></div>
              } @else if (m.kind === 'mobile') {
                <p class="text-sm text-slate-600 dark:text-slate-400">Enter the {{ m.name }} number that will pay. You will get a prompt on that phone to approve the payment.</p>
                <label class="mt-4 block"><span class="label">Mobile number</span><input class="input" name="phone" type="tel" inputmode="tel" placeholder="0771234567" [(ngModel)]="phone" autocomplete="tel" /></label>
                <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" (click)="closePay()">Cancel</button><button class="btn" [disabled]="busy() || phone.length < 9" (click)="payMobile(m)">Send payment prompt</button></div>
              } @else {
                <div class="rounded-xl border border-slate-200 p-4 text-sm dark:border-slate-800">
                  @if (m.id === 'bank' && o()?.options?.bank; as b) {
                    <div class="mb-2 font-semibold">Pay into this account</div>
                    <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
                      @if (b.bankName) { <dt class="text-slate-500">Bank</dt><dd>{{ b.bankName }}</dd> }
                      @if (b.accountName) { <dt class="text-slate-500">Account name</dt><dd>{{ b.accountName }}</dd> }
                      <dt class="text-slate-500">Account number</dt><dd class="font-mono">{{ b.accountNumber }}</dd>
                      @if (b.branchCode) { <dt class="text-slate-500">Branch code</dt><dd>{{ b.branchCode }}</dd> }
                      @if (b.swift) { <dt class="text-slate-500">SWIFT</dt><dd>{{ b.swift }}</dd> }
                      <dt class="text-slate-500">Reference</dt><dd class="font-mono font-semibold">{{ inv.number }}</dd>
                    </dl>
                    @if (b.instructions) { <p class="mt-3 text-slate-600 dark:text-slate-400">{{ b.instructions }}</p> }
                  } @else if (m.id === 'ecocash-manual') {
                    <div class="mb-2 font-semibold">Send money with EcoCash</div>
                    <p class="text-slate-600 dark:text-slate-400">Send <strong>{{ fmt(inv.total, inv.currency) }}</strong> to <strong class="font-mono">{{ m.details }}</strong>. On your phone dial:</p>
                    <div class="mt-2 flex items-center gap-2"><code class="code flex-1 break-all">{{ ussd(m, inv) }}</code><button type="button" class="icon-btn" (click)="copy(ussd(m, inv))" aria-label="Copy"><app-icon name="copy" size="h-4 w-4" /></button></div>
                    <p class="hint">Use the invoice number <span class="font-mono">{{ inv.number }}</span> as the reference if asked.</p>
                  } @else if (m.id === 'paypal') {
                    <div class="mb-2 font-semibold">Pay with PayPal</div>
                    <p class="text-slate-600 dark:text-slate-400">Send <strong>{{ fmt(inv.total, inv.currency) }}</strong> to <strong class="font-mono">{{ m.details }}</strong> and put <span class="font-mono">{{ inv.number }}</span> in the note.</p>
                  }
                </div>
                <p class="mt-4 text-sm text-slate-600 dark:text-slate-400">After you have paid, tell us here so we can match it. The reference from {{ m.name.toLowerCase() }} is required (each reference can be used once); add a photo or PDF of the proof of payment if you have one.</p>
                <div class="mt-3 grid gap-3">
                  <label class="block"><span class="label">Payment reference (required)</span><input class="input font-mono" name="ref" [(ngModel)]="bankRef" maxlength="150" placeholder="e.g. FT26283XYZ or the EcoCash confirmation code" required /></label>
                  <label class="block"><span class="label">Proof of payment: PDF or picture, up to 5 MB (optional)</span><input class="input" type="file" accept=".pdf,.png,.jpg,.jpeg,.webp,application/pdf,image/png,image/jpeg,image/webp" (change)="pick($event)" /></label>
                  <label class="block"><span class="label">Note (optional)</span><input class="input" name="note" [(ngModel)]="bankNote" maxlength="500" /></label>
                </div>
                <div class="mt-5 flex justify-end gap-2"><button class="btn-ghost" (click)="closePay()">Cancel</button><button class="btn" [disabled]="busy() || !bankRef.trim()" (click)="payManual(m)">I have paid</button></div>
              }
            }
          }
        }
      </app-modal>
    }

    <!-- go live -->
    @if (goLiveOpen()) {
      <app-modal title="Go live with ZIMRA" subtitle="Your six test documents go to ZIMRA with this request. ZIMRA then issues your live device." width="max-w-lg" (closed)="goLiveOpen.set(false)">
        <form (ngSubmit)="submitGoLive()">
          <div class="grid gap-4 sm:grid-cols-2">
            <label class="block"><span class="label">Contact person *</span><input class="input" name="gn" [(ngModel)]="glName" required maxlength="120" /></label>
            <label class="block"><span class="label">Phone *</span><input class="input" name="gp" type="tel" [(ngModel)]="glPhone" required maxlength="40" placeholder="0771234567" /></label>
            <label class="block sm:col-span-2"><span class="label">Company TIN registered with ZIMRA</span><input class="input" name="gt" [(ngModel)]="glTin" maxlength="30" inputmode="numeric" /></label>
            <label class="block sm:col-span-2"><span class="label">Anything we should know? (tills, branches, go-live date)</span><textarea class="input" rows="3" name="gx" [(ngModel)]="glNotes" maxlength="1000"></textarea></label>
          </div>
          <div class="mt-6 flex justify-end gap-2 border-t border-slate-100 pt-4 dark:border-slate-800"><button class="btn-ghost" type="button" (click)="goLiveOpen.set(false)">Cancel</button><button class="btn" type="submit" [disabled]="busy() || !glName.trim() || glPhone.trim().length < 7">Send to go live</button></div>
        </form>
      </app-modal>
    }

    <!-- change plan -->
    @if (plansOpen() && o(); as ov) {
      <app-modal title="Change plan" subtitle="You are billed the full price of the new plan, and it starts when that invoice is paid." width="max-w-4xl" (closed)="plansOpen.set(false)">
        <div class="grid gap-4 md:grid-cols-3">
          @for (p of ov.plans; track p.id) {
            <div class="flex flex-col rounded-xl border p-4" [class]="p.id === ov.subscription?.planId ? 'border-brand-500 ring-1 ring-brand-500' : 'border-slate-200 dark:border-slate-800'">
              <div class="font-semibold">{{ p.name }}</div>
              <div class="mt-1 text-xl font-bold">{{ price(p, cycle()) }}<span class="text-xs font-normal text-slate-500"> / {{ cycle() === 'yearly' ? 'year' : 'month' }}</span></div>
              <ul class="mt-3 flex-1 space-y-1 text-xs text-slate-600 dark:text-slate-400">
                <li>{{ lim(p.maxDevices) }} devices</li><li>{{ lim(p.maxUsers) }} users</li><li>{{ lim(p.maxBranches) }} branches</li><li>{{ lim(p.maxReceiptsPerMonth) }} receipts / month</li>
                @for (l of usageLines(p); track l) { <li>{{ l }}</li> }
                @for (label of moduleLabels(p.modules); track label) { <li>✓ {{ label }}</li> }
              </ul>
              @if (p.id === ov.subscription?.planId && cycle() === ov.subscription?.billingCycle) { <span class="badge badge-ok mt-3 self-start">Current plan</span> }
              @else { <button class="btn btn-sm mt-3" [disabled]="busy()" (click)="changePlan(p)">Switch to {{ p.name }}</button> }
            </div>
          }
        </div>
        <div class="mt-4 inline-flex rounded-lg border border-slate-200 p-0.5 text-sm dark:border-slate-700">
          <button type="button" class="rounded-md px-3 py-1" [class]="cycle() === 'monthly' ? 'bg-brand-600 text-white' : ''" (click)="cycle.set('monthly')">Monthly</button>
          <button type="button" class="rounded-md px-3 py-1" [class]="cycle() === 'yearly' ? 'bg-brand-600 text-white' : ''" (click)="cycle.set('yearly')">Yearly</button>
        </div>
      </app-modal>
    }
  `,
})
export class BillingComponent implements OnInit, OnDestroy {
  readonly moduleLabels = planModuleLabels;
  readonly usageLines = usagePricingLines;
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly auth = inject(AuthService);

  readonly Math = Math;
  readonly lim = limit;
  readonly o = signal<BillingOverview | null>(null);
  readonly loaded = signal(false);
  readonly busy = signal(false);
  readonly paying = signal<BillingInvoice | null>(null);
  readonly tab = signal('');
  readonly methods = computed<PayMethod[]>(() => this.o()?.options?.methods ?? []);
  readonly method = computed(() => this.methods().find(m => m.id === this.tab()) ?? this.methods()[0] ?? null);
  readonly result = signal<PaymentRow | null>(null);
  readonly plansOpen = signal(false);
  readonly goLive = signal<GoLiveStatus | null>(null);
  readonly goLiveOpen = signal(false);
  readonly showKey = signal(false);
  /** Opens device registration with ZIMRA's live device details filled in. */
  registerLive(dev: { deviceId: number; activationKey?: string | null; serialNo?: string | null }): void {
    void this.router.navigate(['/devices/register'], { queryParams: { zimraDeviceId: dev.deviceId, activationKey: dev.activationKey ?? '', serialNo: dev.serialNo ?? '', environment: 'Production' } });
  }
  activation = ''; glName = ''; glPhone = ''; glTin = ''; glNotes = '';
  readonly cycle = signal<'monthly' | 'yearly'>('monthly');
  phone = ''; bankRef = ''; bankNote = ''; proof: File | null = null;
  private timer?: ReturnType<typeof setInterval>;

  readonly openInvoices = computed(() => (this.o()?.invoices ?? []).filter(i => i.status === 'Open'));
  /** Open invoices whose payment was sent and waits for our confirmation: nothing more to pay for them. */
  readonly awaiting = computed(() => this.openInvoices().filter(i => this.waiting(i)));
  readonly awaitingNumbers = computed(() => this.awaiting().map(i => i.number).join(', '));
  /** Open invoices that still need paying. */
  readonly toPay = computed(() => this.openInvoices().filter(i => !this.waiting(i)));
  readonly owing = computed(() => this.toPay().reduce((s, i) => s + i.total, 0));
  readonly usage = computed(() => {
    const ov = this.o(); const p = ov?.plan; if (!ov || !p) return [];
    const row = (label: string, used: number, max: number) => ({ label, used, max, pct: max === 0 ? 0 : Math.round((used / max) * 100) });
    return [row('Devices', ov.usage.devices, p.maxDevices), row('Users', ov.usage.users, p.maxUsers), row('Branches', ov.usage.branches, p.maxBranches), row('Receipts this month', ov.usage.receiptsThisMonth, p.maxReceiptsPerMonth)];
  });

  fmt(v: number, c: string): string { return money(v, c); }
  unit(v: number, currency: string): string { return money(v, currency); }
  /** The plan's price for this company: a reseller's companies pay the list price less the reseller's discount. */
  price(p: Plan, cycle: string): string { const list = cycle === 'yearly' ? p.priceYearly : p.priceMonthly; const v = Math.round(list * (100 - (this.o()?.resellerDiscountPercent ?? 0))) / 100; return v === 0 ? 'Free' : money(v, p.currency); }
  codeSent(i: BillingInvoice): boolean { return i.status === 'Open' && i.payments.some(p => p.status === 'CodeSent'); }
  waiting(i: BillingInvoice): boolean { return i.status === 'Open' && i.payments.some(p => p.status === 'AwaitingConfirmation'); }
  /** a card / Paynow / EcoCash payment started in the last 30 minutes and not decided yet: show that it is being checked, not a fresh Pay button */
  checking(i: BillingInvoice): boolean { return i.status === 'Open' && !!this.pendingOf(i); }
  private pendingOf(i: BillingInvoice): PaymentRow | undefined {
    return i.payments.find(p => p.status === 'Pending' && (p.method === 'Paynow' || p.method === 'EcoCash' || p.method === 'Stripe') && Date.now() - new Date(p.createdAt).getTime() < 30 * 60_000);
  }
  async checkInvoice(i: BillingInvoice): Promise<void> {
    const p = this.pendingOf(i); if (!p) return;
    this.busy.set(true);
    try {
      const fresh = await this.api.get<PaymentRow>(`/api/v1/billing/payments/${p.id}`);
      if (fresh.status === 'Paid') this.toast.ok('Payment received. Thank you!');
      else if (fresh.status === 'Pending') this.toast.ok('Not confirmed yet. We keep checking; it updates by itself.');
      else this.toast.err(new Error('The payment did not go through. Try again or pay another way.'));
      await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
  private watchTimer?: ReturnType<typeof setInterval>;
  /** while an invoice is being checked, look again every 6 seconds (up to 3 minutes) so the page turns to Paid without a refresh */
  private watchPending(): void {
    if (this.watchTimer || !(this.o()?.invoices ?? []).some(i => this.checking(i))) return;
    let n = 0;
    this.watchTimer = setInterval(async () => {
      const pend = (this.o()?.invoices ?? []).map(i => this.pendingOf(i)).filter((p): p is PaymentRow => !!p);
      if (!pend.length || ++n > 30) { clearInterval(this.watchTimer); this.watchTimer = undefined; return; }
      try { for (const p of pend) { const f = await this.api.get<PaymentRow>(`/api/v1/billing/payments/${p.id}`); if (f.status !== 'Pending') { await this.load(); if (f.status === 'Paid') this.toast.ok('Payment received. Thank you!'); break; } } } catch { /* try again next tick */ }
    }, 6000);
  }

  async ngOnInit(): Promise<void> {
    await this.load();
    const id = this.route.snapshot.queryParamMap.get('payment');
    if (id) { await this.router.navigate([], { queryParams: {}, replaceUrl: true }); await this.returnedFromPaynow(id); }
  }

  ngOnDestroy(): void { this.stopPolling(); if (this.watchTimer) clearInterval(this.watchTimer); }

  async load(): Promise<void> {
    try {
      const ov = await this.api.get<BillingOverview>('/api/v1/billing');
      this.o.set(ov); if (ov.subscription) this.cycle.set(ov.subscription.billingCycle);
      this.watchPending();
      // a company on an accounting-only plan has no fiscal devices, so nothing to take live
      if (!ov.access?.locked && !this.auth.menus().some(m => m.slugname === 'accounting/sales')) { try { this.goLive.set(await this.api.get<GoLiveStatus>('/api/v1/billing/go-live')); } catch { /* optional card */ } } else this.goLive.set(null);
      // the first invoice was paid: pick up the full menu (and live-device access) from a fresh session
      if (this.auth.billingLocked() && ov.access && !ov.access.locked) {
        await this.auth.refresh();
        if (!this.auth.billingLocked()) this.toast.ok('Your account is unlocked.');
      }
    } catch (e) { this.toast.err(e); } finally { this.loaded.set(true); }
  }

  /** The customer comes back from Paynow's page: find out what happened (Paynow may take a moment to confirm). */
  private async returnedFromPaynow(paymentId: string): Promise<void> {
    for (const wait of [0, 2500, 5000]) {
      if (wait) await new Promise(r => setTimeout(r, wait));
      try {
        const p = await this.api.get<PaymentRow>(`/api/v1/billing/payments/${paymentId}`);
        if (p.status === 'Paid') { this.toast.ok('Payment received. Thank you!'); await this.load(); return; }
        if (p.status !== 'Pending') { this.toast.err(new Error('The payment did not go through.')); return; }
      } catch (e) { this.toast.err(e); return; }
    }
    this.toast.ok('Your payment is still being confirmed. This page updates by itself.');
    await this.load();
  }

  startPay(i: BillingInvoice): void {
    this.tab.set(this.methods()[0]?.id ?? '');
    this.phone = ''; this.bankRef = ''; this.bankNote = ''; this.proof = null; this.result.set(null);
    this.plansOpen.set(false); this.paying.set(i);
  }

  closePay(): void { this.stopPolling(); this.paying.set(null); this.result.set(null); void this.load(); }

  /** A gateway page the customer leaves for (Paynow checkout, Stripe) and comes back from. */
  async payRedirect(m: PayMethod): Promise<void> {
    const inv = this.paying(); if (!inv) return;
    this.busy.set(true);
    try {
      const p = m.id === 'stripe'
        ? await this.api.post<PaymentRow>(`/api/v1/billing/invoices/${inv.id}/pay/stripe`, {})
        : await this.api.post<PaymentRow>(`/api/v1/billing/invoices/${inv.id}/pay/paynow`, { method: 'web' });
      if (p.redirectUrl) { window.location.href = p.redirectUrl; return; }
      this.result.set(p); this.poll(p);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  /** A prompt on the customer's phone (EcoCash direct, or EcoCash / OneMoney through Paynow). */
  async payMobile(m: PayMethod): Promise<void> {
    const inv = this.paying(); if (!inv) return;
    this.busy.set(true);
    try {
      const p = m.id === 'ecocash'
        ? await this.api.post<PaymentRow>(`/api/v1/billing/invoices/${inv.id}/pay/ecocash`, { phone: this.phone.trim() })
        : await this.api.post<PaymentRow>(`/api/v1/billing/invoices/${inv.id}/pay/paynow`, { method: m.id === 'paynow-onemoney' ? 'onemoney' : 'ecocash', phone: this.phone.trim() });
      this.result.set(p); this.poll(p);
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  /** The EcoCash "Send Money" dial string for the invoice amount. */
  ussd(m: PayMethod, inv: BillingInvoice): string { return `*153*1*1*${(m.details ?? '').replace(/\D/g, '')}*${inv.total.toFixed(2)}#`; }

  async copy(text: string): Promise<void> { try { await navigator.clipboard.writeText(text); this.toast.ok('Copied.'); } catch { this.toast.err(new Error('Copy failed - select the text and copy it manually.')); } }

  /** after a phone prompt: ask every few seconds (about three minutes) until the payment is decided */
  private poll(p: PaymentRow): void {
    this.stopPolling(); let n = 0;
    this.timer = setInterval(async () => { if (++n > 36) { this.stopPolling(); return; } await this.check(p, true); }, 5000);
  }
  private stopPolling(): void { if (this.timer) { clearInterval(this.timer); this.timer = undefined; } }

  async check(p: PaymentRow, quiet = false): Promise<void> {
    try {
      const fresh = await this.api.get<PaymentRow>(`/api/v1/billing/payments/${p.id}`);
      this.result.set(fresh);
      if (fresh.status !== 'Pending') { this.stopPolling(); await this.load(); }
    } catch (e) { if (!quiet) this.toast.err(e); }
  }

  /** The one-time code the operator gave after a cash or bank payment. */
  async redeem(): Promise<void> {
    this.busy.set(true);
    try {
      this.o.set(await this.api.post<BillingOverview>('/api/v1/billing/redeem', { code: this.activation.trim() }));
      this.activation = '';
      await this.auth.refresh();   // a fresh session carries the full menu
      this.toast.ok('Activated. Your account is unlocked.');
      await this.router.navigateByUrl(this.auth.homeUrl());
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  /** An expired subscription or trial: create the invoice that renews it, then pay it. */
  async renew(): Promise<void> {
    this.busy.set(true);
    try { const inv = await this.api.post<BillingInvoice>('/api/v1/billing/renew', {}); await this.load(); this.startPay(inv); }
    catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async submitGoLive(): Promise<void> {
    this.busy.set(true);
    try {
      await this.api.post('/api/v1/billing/go-live', { contactName: this.glName.trim(), contactPhone: this.glPhone.trim(), zimraTin: this.glTin.trim() || null, notes: this.glNotes.trim() || null });
      this.goLiveOpen.set(false); this.toast.ok('Sent. We forward your test documents to ZIMRA and let you know when your live device is issued.'); await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  pick(e: Event): void {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    if (file && !/.(pdf|png|jpe?g|webp)$/i.test(file.name)) { this.toast.err(new Error('Only a PDF or a picture (PNG, JPEG, WebP) can be uploaded.')); input.value = ''; this.proof = null; return; }
    if (file && file.size > 5_000_000) { this.toast.err(new Error('The proof of payment may be at most 5 MB.')); input.value = ''; this.proof = null; return; }
    this.proof = file;
  }

  async payManual(m: PayMethod): Promise<void> {
    const inv = this.paying(); if (!inv) return;
    if (!this.bankRef.trim()) { this.toast.err(new Error('Enter the payment reference.')); return; }
    this.busy.set(true);
    try {
      const form = new FormData();
      form.append('Reference', this.bankRef.trim()); if (this.bankNote.trim()) form.append('Notes', this.bankNote.trim()); if (this.proof) form.append('Proof', this.proof);
      this.result.set(await this.api.post<PaymentRow>(`/api/v1/billing/invoices/${inv.id}/pay/manual/${m.id}`, form));
      await this.load();
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async changePlan(p: Plan): Promise<void> {
    if (!(await this.dialog.confirm(`Switch to ${p.name} (${this.price(p, this.cycle())} per ${this.cycle() === 'yearly' ? 'year' : 'month'})? An invoice for the full price is created; the new plan starts when you pay it.`, 'Switch plan'))) return;
    this.busy.set(true);
    try {
      const inv = await this.api.post<BillingInvoice>('/api/v1/billing/change-plan', { planId: p.id, billingCycle: this.cycle() });
      await this.load(); this.plansOpen.set(false);
      if (inv.status === 'Open') this.startPay(inv); else this.toast.ok('Your plan was changed.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }

  async pdf(i: BillingInvoice): Promise<void> { try { await this.api.open(`/api/v1/billing/invoices/${i.id}/pdf`); } catch (e) { this.toast.err(e); } }
}
