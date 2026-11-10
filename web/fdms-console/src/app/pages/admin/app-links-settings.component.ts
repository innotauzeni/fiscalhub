import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../../core/api.service';
import { ToastService } from '../../core/ui.services';
import { AppLinks, AppLinksStore, StoreBadgesComponent } from '../../shared/store-badges.component';
import { IconComponent } from '../../shared/ui.components';

/** Where people download the mobile app. Each link that is filled in becomes a button on the public website; empty ones stay hidden until the app is in that store. */
@Component({
  selector: 'app-app-links-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, IconComponent, StoreBadgesComponent],
  template: `
    <form class="card mt-6" (ngSubmit)="save()">
      <div class="card-title flex items-center gap-2"><app-icon name="download" size="h-4 w-4" />Mobile app downloads</div>
      <p class="mb-4 text-sm text-slate-600 dark:text-slate-300">Paste the address of the app's page in each store once it is approved. A button appears on the public website for every link you fill in; leave a store empty until the app is there.</p>
      <div class="grid gap-4 sm:grid-cols-2">
        <label class="block sm:col-span-2"><span class="label">APKPure page</span>
          <input class="input font-mono" name="apk" [(ngModel)]="f.apkPure" placeholder="https://apkpure.com/fiscalhub/zw.co.daraerp.fdms_mobile" autocomplete="off" /></label>
        <label class="block sm:col-span-2"><span class="label">Google Play page</span>
          <input class="input font-mono" name="play" [(ngModel)]="f.playStore" placeholder="https://play.google.com/store/apps/details?id=zw.co.daraerp.fdms_mobile" autocomplete="off" /></label>
        <label class="block sm:col-span-2"><span class="label">App Store page</span>
          <input class="input font-mono" name="apple" [(ngModel)]="f.appStore" placeholder="https://apps.apple.com/app/fiscalhub/id..." autocomplete="off" /></label>
        <label class="block"><span class="label">Current version (shown under the buttons)</span><input class="input" name="ver" [(ngModel)]="f.version" placeholder="1.0.0" /></label>
      </div>
      <p class="hint mt-3">Only the stores' own https addresses are accepted (apkpure.com, play.google.com, apps.apple.com).</p>
      <div class="mt-4 rounded-xl border border-dashed border-slate-200 p-4 dark:border-slate-700">
        <div class="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-500">What visitors see</div>
        <app-store-badges [showVersion]="true" />
        @if (!links.links()?.apkPure && !links.links()?.playStore && !links.links()?.appStore) { <p class="text-sm text-slate-500">Nothing yet: no link is set.</p> }
      </div>
      <div class="mt-4 flex justify-end"><button class="btn" type="submit" [disabled]="busy()">Save download links</button></div>
    </form>
  `,
})
export class AppLinksSettingsComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  readonly links = inject(AppLinksStore);
  readonly busy = signal(false);
  f = { apkPure: '', playStore: '', appStore: '', version: '' };

  async ngOnInit(): Promise<void> {
    await this.links.load(true);
    const l = this.links.links();
    if (l) this.f = { apkPure: l.apkPure ?? '', playStore: l.playStore ?? '', appStore: l.appStore ?? '', version: l.version ?? '' };
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const l = await this.api.put<AppLinks>('/api/v1/settings/app-links', { apkPure: this.f.apkPure || null, playStore: this.f.playStore || null, appStore: this.f.appStore || null, version: this.f.version || null });
      this.links.links.set(l);
      this.f = { apkPure: l.apkPure ?? '', playStore: l.playStore ?? '', appStore: l.appStore ?? '', version: l.version ?? '' };
      this.toast.ok('Download links saved. They show on the website now.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
}
