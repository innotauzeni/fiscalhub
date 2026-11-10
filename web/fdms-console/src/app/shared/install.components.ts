import { ChangeDetectionStrategy, Component, Directive, ElementRef, inject, input, signal } from '@angular/core';
import { PwaService } from '../core/pwa.service';
import { ModalComponent } from './modal.components';
import { IconComponent } from './ui.components';

/** Fades and lifts an element in the first time it scrolls into view. Respects "reduce motion" (see styles.css). */
@Directive({ selector: '[appReveal]', host: { class: 'reveal' } })
export class RevealDirective {
  constructor() {
    const el = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    if (typeof IntersectionObserver === 'undefined') { el.classList.add('reveal-in'); return; }
    const io = new IntersectionObserver(entries => { if (entries.some(e => e.isIntersecting)) { el.classList.add('reveal-in'); io.disconnect(); } }, { threshold: 0.1 });
    io.observe(el);
  }
}

/**
 * "Install app": opens the browser's own dialog on Android and desktop, or the Add to Home Screen steps on iPhone and iPad. Hidden once the app is installed
 * (or when the browser offers no way to install).
 */
@Component({
  selector: 'app-install-button',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [IconComponent, ModalComponent],
  template: `
    @if (pwa.installable()) {
      <button type="button" [class]="style()" (click)="go()" aria-label="Install the app"><app-icon name="download" size="h-4 w-4" /><span [class]="labelClass()">Install app</span></button>
    }
    @if (steps()) {
      <app-modal title="Install FDMS Gateway" subtitle="Add it to your home screen: it opens like a normal app, full screen." width="max-w-md" (closed)="steps.set(false)">
        <ol class="space-y-4 text-sm">
          <li class="flex gap-3"><span class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-brand-600 text-xs font-bold text-white">1</span>
            <span>Tap the <strong>Share</strong> button <svg class="inline h-5 w-5 align-text-bottom text-brand-600" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 15V3M8 7l4-4 4 4M5 12v7a2 2 0 002 2h10a2 2 0 002-2v-7" /></svg> in Safari's toolbar (at the bottom of the screen, or the top on iPad).</span></li>
          <li class="flex gap-3"><span class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-brand-600 text-xs font-bold text-white">2</span><span>Scroll down and tap <strong>Add to Home Screen</strong>.</span></li>
          <li class="flex gap-3"><span class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-brand-600 text-xs font-bold text-white">3</span><span>Tap <strong>Add</strong>. The FDMS icon appears on your home screen.</span></li>
        </ol>
        <p class="hint mt-4">Not seeing it? Open this page in Safari first: other browsers on iPhone may not offer Add to Home Screen on older iOS versions.</p>
        <div class="mt-5 flex justify-end"><button class="btn" (click)="steps.set(false)">Got it</button></div>
      </app-modal>
    }
  `,
})
export class InstallButtonComponent {
  readonly pwa = inject(PwaService);
  /** header (compact), hero (large) */
  readonly variant = input<'compact' | 'large' | 'ghost'>('compact');
  readonly steps = signal(false);

  style(): string {
    return this.variant() === 'large' ? 'btn-ghost px-5 py-3 text-base' : this.variant() === 'ghost' ? 'icon-btn' : 'btn-ghost btn-sm';
  }
  labelClass(): string { return this.variant() === 'ghost' ? 'sr-only' : ''; }

  async go(): Promise<void> {
    if (this.pwa.canInstall()) await this.pwa.install();
    else this.steps.set(true);
  }
}
