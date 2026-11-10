import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { IconComponent } from './ui.components';

/** Plain-words help for working offline: what it is, the steps for the day, and what to do when something is not right. */
@Component({
  selector: 'app-offline-guide',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, IconComponent],
  template: `
    <details class="card mb-5 group" [attr.open]="open() ? '' : null">
      <summary class="flex cursor-pointer list-none items-center justify-between gap-3">
        <span class="flex items-center gap-3"><span class="flex h-9 w-9 items-center justify-center rounded-xl bg-sky-100 text-sky-700 dark:bg-sky-500/20 dark:text-sky-200"><app-icon name="cloud-upload" /></span>
          <span><span class="block font-semibold">How offline fiscalisation works</span><span class="block text-sm text-slate-500">For shops that trade without a steady internet connection. Read this once.</span></span></span>
        <span class="text-sm font-medium text-brand-600 group-open:hidden">Show the steps</span><span class="hidden text-sm font-medium text-brand-600 group-open:inline">Hide</span>
      </summary>

      <div class="mt-5 grid gap-6 lg:grid-cols-2">
        <div>
          <h3 class="mb-2 text-sm font-semibold uppercase tracking-wide text-slate-500">The idea</h3>
          <p class="text-sm leading-6 text-slate-700 dark:text-slate-300">
            An <strong>offline device</strong> does not talk to ZIMRA while you trade. The gateway numbers and signs every receipt on its own, so each invoice already has its ZIMRA number and QR code and you can hand it to the customer straight away.
            At the end of the day you close the fiscal day and the receipts are sent to ZIMRA together, in a file. Only then can ZIMRA's verification page show them.
          </p>
          <p class="mt-3 rounded-xl bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-900/20 dark:text-amber-200">
            <strong>Offline mode is set by ZIMRA on the device</strong>, not here. If your device is Online but the internet is down, you do not need offline mode: receipts are numbered, kept as <em>Queued</em> and sent automatically when the connection returns.
          </p>
        </div>

        <div>
          <h3 class="mb-2 text-sm font-semibold uppercase tracking-wide text-slate-500">Every trading day</h3>
          <ol class="space-y-3">
            @for (s of steps; track s.title; let i = $index) {
              <li class="flex gap-3">
                <span class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-brand-600 text-sm font-semibold text-white">{{ i + 1 }}</span>
                <div class="text-sm"><div class="font-medium">{{ s.title }}</div><div class="text-slate-600 dark:text-slate-400">{{ s.text }}</div></div>
              </li>
            }
          </ol>
        </div>
      </div>

      <div class="mt-6 border-t border-slate-100 pt-5 dark:border-slate-800">
        <h3 class="mb-2 text-sm font-semibold uppercase tracking-wide text-slate-500">If something looks wrong</h3>
        <dl class="grid gap-x-8 gap-y-3 text-sm md:grid-cols-2">
          @for (f of faq; track f.q) { <div><dt class="font-medium">{{ f.q }}</dt><dd class="text-slate-600 dark:text-slate-400">{{ f.a }}</dd></div> }
        </dl>
        <p class="mt-4 text-sm text-slate-500">To check an invoice yourself, open it under <a routerLink="/receipts" class="font-medium text-brand-600 underline">Receipts</a> and press <strong>View</strong>: it shows the QR code and a button that opens ZIMRA's verification page.</p>
      </div>
    </details>
  `,
})
export class OfflineGuideComponent {
  readonly open = input(false);

  readonly steps = [
    { title: 'Check the device says Offline', text: 'Devices page: the mode shows Offline. If ZIMRA has just changed it, press “Refresh config” on the device.' },
    { title: 'Issue invoices as usual', text: 'New invoice, import or API. Each one is issued instantly with its ZIMRA number and QR code (status “Issued offline”). Print or send it to the customer.' },
    { title: 'Close the fiscal day when trading ends', text: 'Fiscal days → Close day. This works without internet: it ends the day and packs the receipts and the Z report into a file (or several, each under 3 MB).' },
    { title: 'Upload the file when you are online', text: 'The gateway tries by itself after closing and again in the background. You can also press “Upload / check now” on this page. Files go to ZIMRA in order.' },
    { title: 'Check it was accepted', text: 'The file’s status moves to Processed and the receipts change to Submitted. Anything ZIMRA does not accept is listed under Problems: fix it, then upload again.' },
  ];

  readonly faq = [
    { q: 'The customer scans the QR and ZIMRA says it cannot find the invoice', a: 'Normal until the day’s file has been uploaded and processed. Upload it (step 4), wait a few minutes, then scan again.' },
    { q: 'The file stays “Pending” or shows an error', a: 'Press “Upload / check now”. If the Problems column names a receipt, correct that invoice under Receipts, then upload again.' },
    { q: 'I forgot to close the day', a: 'The gateway closes any open day at 23:59 (Zimbabwe time), builds the file and uploads it as soon as there is a connection.' },
    { q: 'I want to go back to Online', a: 'Ask ZIMRA to change the device mode. Upload every offline file first so nothing is left behind.' },
  ];
}
