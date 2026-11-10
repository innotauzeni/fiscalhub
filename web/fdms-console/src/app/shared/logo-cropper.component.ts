import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, input, output, signal, viewChild } from '@angular/core';
import { ModalComponent } from './modal.components';
import { IconComponent } from './ui.components';

type Shape = 'wide' | 'square' | 'free';

/**
 * Pick any picture (PNG, JPEG, WebP, GIF or SVG, any size), then drag it into place, zoom, and choose the shape. The result is always a PNG (or a JPEG when a
 * photo is too heavy), scaled down until it fits the size limit, so the invoice and the server never refuse it. Nothing leaves the browser until Use this logo.
 */
@Component({
  selector: 'app-logo-cropper',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ModalComponent, IconComponent],
  template: `
    <app-modal [title]="title()" subtitle="Drag to position, scroll or use the slider to zoom. What you see inside the frame is what is saved." width="max-w-2xl" (closed)="closed.emit()">
      @if (!img()) {
        <label class="flex cursor-pointer flex-col items-center justify-center gap-3 rounded-2xl border-2 border-dashed px-6 py-14 text-center transition"
          [class]="over() ? 'border-brand-500 bg-brand-50 dark:bg-brand-500/10' : 'border-slate-300 hover:border-brand-400 dark:border-slate-700'"
          (dragover)="$event.preventDefault(); over.set(true)" (dragleave)="over.set(false)" (drop)="$event.preventDefault(); over.set(false); pick($any($event).dataTransfer?.files)">
          <span class="flex h-12 w-12 items-center justify-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800"><app-icon name="upload" size="h-6 w-6" /></span>
          <span class="text-sm font-medium">Choose a picture or drop it here</span>
          <span class="text-xs text-slate-500">PNG, JPG, WebP, GIF or SVG. Any size: you crop and resize it next.</span>
          <input type="file" accept="image/*" class="sr-only" (change)="pick($any($event.target).files)" />
        </label>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
      } @else {
        <div class="flex flex-wrap items-center gap-2" role="radiogroup" aria-label="Shape" [class.hidden]="square()">
          @for (s of shapes; track s.id) {
            <button type="button" role="radio" [attr.aria-checked]="shape() === s.id" class="rounded-lg border px-3 py-1.5 text-sm transition"
              [class]="shape() === s.id ? 'border-brand-600 bg-brand-50 font-medium text-brand-700 dark:bg-brand-500/10 dark:text-brand-100' : 'border-slate-200 text-slate-600 hover:border-slate-300 dark:border-slate-700 dark:text-slate-300'"
              (click)="setShape(s.id)">{{ s.label }}<span class="ml-1 text-xs opacity-60">{{ s.hint }}</span></button>
          }
        </div>

        <div class="mt-4 flex justify-center">
          <div class="checker overflow-hidden rounded-xl border border-slate-300 shadow-inner dark:border-slate-600" [style.width.px]="frame().w" [style.height.px]="frame().h">
            <canvas #view class="block cursor-grab touch-none active:cursor-grabbing" tabindex="0" aria-label="Logo position. Drag, or use the arrow keys to move and plus and minus to zoom."
              [style.width.px]="frame().w" [style.height.px]="frame().h"
              (pointerdown)="down($event)" (pointermove)="move($event)" (pointerup)="up($event)" (pointercancel)="up($event)" (wheel)="wheel($event)" (keydown)="key($event)"></canvas>
          </div>
        </div>

        <div class="mx-auto mt-4 flex max-w-md items-center gap-3">
          <button type="button" class="icon-btn" (click)="zoomBy(-0.1)" aria-label="Zoom out"><span class="text-lg leading-none">−</span></button>
          <input type="range" class="h-2 flex-1 accent-brand-600" min="0" max="1000" [value]="sliderValue()" (input)="slide($any($event.target).valueAsNumber)" aria-label="Zoom" />
          <button type="button" class="icon-btn" (click)="zoomBy(0.1)" aria-label="Zoom in"><span class="text-lg leading-none">+</span></button>
        </div>
        <div class="mt-3 flex flex-wrap items-center justify-center gap-2 text-sm">
          <button type="button" class="btn-ghost btn-sm" (click)="fit('contain')">Fit whole picture</button>
          <button type="button" class="btn-ghost btn-sm" (click)="fit('cover')">Fill the frame</button>
          <div class="inline-flex rounded-lg border border-slate-200 p-0.5 dark:border-slate-700" role="group" aria-label="Background">
            <button type="button" class="rounded-md px-2.5 py-1 text-xs" [class]="!white() ? 'bg-slate-200 font-medium dark:bg-slate-700' : 'text-slate-500'" (click)="setWhite(false)">Transparent</button>
            <button type="button" class="rounded-md px-2.5 py-1 text-xs" [class]="white() ? 'bg-slate-200 font-medium dark:bg-slate-700' : 'text-slate-500'" (click)="setWhite(true)">White</button>
          </div>
          <label class="btn-ghost btn-sm cursor-pointer">Choose another<input type="file" accept="image/*" class="sr-only" (change)="pick($any($event.target).files)" /></label>
        </div>
        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }

        <div class="mt-6 flex items-center justify-between gap-3 border-t border-slate-100 pt-4 dark:border-slate-800">
          <span class="text-xs text-slate-500">Saved as {{ outputLabel() }}, kept under {{ kb() }} KB.</span>
          <div class="flex gap-2"><button type="button" class="btn-ghost" (click)="closed.emit()">Cancel</button><button type="button" class="btn" [disabled]="busy()" (click)="apply()">{{ busy() ? 'Preparing…' : useLabel() }}</button></div>
        </div>
      }
    </app-modal>
  `,
  styles: [`.checker { background-color: #fff; background-image: linear-gradient(45deg,#e5e7eb 25%,transparent 25%),linear-gradient(-45deg,#e5e7eb 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#e5e7eb 75%),linear-gradient(-45deg,transparent 75%,#e5e7eb 75%); background-size: 16px 16px; background-position: 0 0,0 8px,8px -8px,-8px 0; }`],
})
export class LogoCropperComponent {
  readonly title = input('Company logo');
  /** The picture to start with (for instance one just chosen elsewhere); otherwise the dialog asks for one. */
  readonly file = input<File | null>(null);
  /** A profile picture: always square, so the shape choice is hidden. */
  readonly square = input(false);
  /** Wording of the final button. */
  readonly useLabel = input("Use this logo");
  readonly maxBytes = input(250_000);
  /** Widest picture produced, in pixels. */
  readonly maxWidth = input(720);
  readonly done = output<Blob>();
  readonly closed = output<void>();

  readonly shapes: { id: Shape; label: string; hint: string }[] = [
    { id: 'wide', label: 'Wide', hint: '3:1, best for invoices' },
    { id: 'square', label: 'Square', hint: '1:1' },
    { id: 'free', label: 'Keep shape', hint: 'no crop' },
  ];

  readonly view = viewChild<ElementRef<HTMLCanvasElement>>('view');
  readonly img = signal<HTMLImageElement | null>(null);
  readonly shape = signal<Shape>('wide');
  readonly white = signal(false);
  readonly over = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly kb = computed(() => Math.round(this.maxBytes() / 1000));
  readonly outputLabel = computed(() => (this.white() ? 'PNG on white (JPEG if needed)' : 'PNG with a transparent background'));

  /** Where the picture's centre sits in the frame, and how many frame pixels one picture pixel takes. */
  private scale = signal(1);
  private cx = signal(0);
  private cy = signal(0);
  private drag: { x: number; y: number; cx: number; cy: number; id: number } | null = null;

  readonly frame = computed(() => {
    const i = this.img();
    const aspect = this.shape() === 'wide' ? 3 : this.shape() === 'square' ? 1 : i ? i.naturalWidth / i.naturalHeight : 1;
    const maxW = 480, maxH = 300;
    let w = maxW, h = w / aspect;
    if (h > maxH) { h = maxH; w = h * aspect; }
    return { w: Math.round(w), h: Math.round(h), aspect };
  });

  private contain = computed(() => { const i = this.img(); const f = this.frame(); return i ? Math.min(f.w / i.naturalWidth, f.h / i.naturalHeight) : 1; });
  private cover = computed(() => { const i = this.img(); const f = this.frame(); return i ? Math.max(f.w / i.naturalWidth, f.h / i.naturalHeight) : 1; });
  private minScale = computed(() => this.contain() * 0.5);
  private maxScale = computed(() => Math.max(this.cover() * 6, this.contain() * 2));
  readonly sliderValue = computed(() => Math.round(1000 * (Math.log(this.scale() / this.minScale()) / Math.log(this.maxScale() / this.minScale()))));

  constructor() {
    effect(() => { if (this.square()) this.shape.set('square'); });
    effect(() => { const f = this.file(); if (f) void this.load(f); });
    // repaint whenever anything that shows changes
    effect(() => { this.img(); this.scale(); this.cx(); this.cy(); this.frame(); this.view(); this.draw(); });
  }

  pick(files: FileList | null | undefined): void { const f = files?.[0]; if (f) void this.load(f); }

  private async load(file: File): Promise<void> {
    this.error.set('');
    if (!file.type.startsWith('image/') && !/\.(png|jpe?g|webp|gif|svg|bmp)$/i.test(file.name)) { this.error.set('That is not a picture file. Choose a PNG, JPG, WebP, GIF or SVG.'); return; }
    if (file.size > 15_000_000) { this.error.set('That picture is very large (over 15 MB). Choose a smaller one.'); return; }
    const url = URL.createObjectURL(file);
    try {
      const im = new Image();
      im.decoding = 'async';
      await new Promise<void>((ok, bad) => { im.onload = () => ok(); im.onerror = () => bad(new Error('unreadable')); im.src = url; });
      if (!im.naturalWidth || !im.naturalHeight) { im.width = im.width || 512; im.height = im.height || 512; }
      this.img.set(im);
      this.shape.set(this.square() ? 'square' : im.naturalWidth / im.naturalHeight >= 1.8 ? 'wide' : im.naturalWidth / im.naturalHeight <= 1.15 && im.naturalWidth / im.naturalHeight >= 0.87 ? 'square' : 'wide');
      this.fit('contain');
    } catch { this.error.set('This browser could not read that picture. Save it as PNG or JPG and try again (phone formats such as HEIC are not supported).'); }
    finally { setTimeout(() => URL.revokeObjectURL(url), 60_000); }
  }

  setShape(s: Shape): void { this.shape.set(s); queueMicrotask(() => this.fit('contain')); }
  setWhite(v: boolean): void { this.white.set(v); this.draw(); }

  fit(mode: 'contain' | 'cover'): void { this.scale.set(mode === 'contain' ? this.contain() : this.cover()); const f = this.frame(); this.cx.set(f.w / 2); this.cy.set(f.h / 2); }
  slide(v: number): void { this.scale.set(this.minScale() * Math.pow(this.maxScale() / this.minScale(), v / 1000)); }
  zoomBy(step: number): void { this.zoomAt(1 + step, this.frame().w / 2, this.frame().h / 2); }

  private zoomAt(factor: number, px: number, py: number): void {
    const old = this.scale(); const next = Math.min(this.maxScale(), Math.max(this.minScale(), old * factor));
    // keep the point under the pointer where it is
    this.cx.set(px - (px - this.cx()) * (next / old)); this.cy.set(py - (py - this.cy()) * (next / old));
    this.scale.set(next);
  }

  /** At least a sliver of the picture stays inside the frame, so it cannot be dragged out of reach. */
  private clampCentre(): void {
    const f = this.frame();
    this.cx.set(Math.min(f.w - 16, Math.max(16, this.cx())));
    this.cy.set(Math.min(f.h - 16, Math.max(16, this.cy())));
  }

  down(e: PointerEvent): void { const c = e.target as HTMLCanvasElement; c.setPointerCapture(e.pointerId); this.drag = { x: e.clientX, y: e.clientY, cx: this.cx(), cy: this.cy(), id: e.pointerId }; }
  move(e: PointerEvent): void { if (!this.drag || e.pointerId !== this.drag.id) return; this.cx.set(this.drag.cx + e.clientX - this.drag.x); this.cy.set(this.drag.cy + e.clientY - this.drag.y); this.clampCentre(); }
  up(e: PointerEvent): void { if (this.drag?.id === e.pointerId) this.drag = null; }
  wheel(e: WheelEvent): void { e.preventDefault(); const r = (e.target as HTMLCanvasElement).getBoundingClientRect(); this.zoomAt(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX - r.left, e.clientY - r.top); }
  key(e: KeyboardEvent): void {
    const step = e.shiftKey ? 20 : 5;
    const map: Record<string, () => void> = {
      ArrowLeft: () => this.cx.update(v => v - step), ArrowRight: () => this.cx.update(v => v + step), ArrowUp: () => this.cy.update(v => v - step), ArrowDown: () => this.cy.update(v => v + step),
      '+': () => this.zoomBy(0.1), '=': () => this.zoomBy(0.1), '-': () => this.zoomBy(-0.1),
    };
    if (map[e.key]) { e.preventDefault(); map[e.key](); this.clampCentre(); }
  }

  private paint(ctx: CanvasRenderingContext2D, w: number, h: number, k: number, im: HTMLImageElement, whiteBg: boolean): void {
    ctx.clearRect(0, 0, w, h);
    if (whiteBg) { ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, w, h); }
    const iw = im.naturalWidth * this.scale() * k, ih = im.naturalHeight * this.scale() * k;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(im, this.cx() * k - iw / 2, this.cy() * k - ih / 2, iw, ih);
  }

  private draw(): void {
    const c = this.view()?.nativeElement, im = this.img(); if (!c || !im) return;
    const f = this.frame(); const dpr = Math.min(2, window.devicePixelRatio || 1);
    c.width = Math.round(f.w * dpr); c.height = Math.round(f.h * dpr);
    const ctx = c.getContext('2d'); if (!ctx) return;
    this.paint(ctx, c.width, c.height, dpr, im, this.white());
  }

  /** Renders the frame at full quality and shrinks it until it is under the size limit (PNG first, then a JPEG for heavy photos). */
  async apply(): Promise<void> {
    const im = this.img(); if (!im) return;
    this.busy.set(true); this.error.set('');
    try {
      const f = this.frame();
      let outW = Math.min(this.maxWidth(), Math.round(f.w * 1.5));
      const toBlob = (c: HTMLCanvasElement, type: string, q?: number) => new Promise<Blob | null>(r => c.toBlob(r, type, q));
      for (let attempt = 0; attempt < 8; attempt++) {
        const k = outW / f.w; const c = document.createElement('canvas'); c.width = Math.round(f.w * k); c.height = Math.round(f.h * k);
        const ctx = c.getContext('2d'); if (!ctx) break;
        this.paint(ctx, c.width, c.height, k, im, this.white());
        const png = await toBlob(c, 'image/png');
        if (png && png.size <= this.maxBytes()) { this.done.emit(png); return; }
        // a heavy picture: a JPEG on white is far smaller than a PNG
        const j = document.createElement('canvas'); j.width = c.width; j.height = c.height; const jc = j.getContext('2d')!;
        this.paint(jc, j.width, j.height, k, im, true);
        const jpg = await toBlob(j, 'image/jpeg', 0.88);
        if (jpg && jpg.size <= this.maxBytes()) { this.done.emit(jpg); return; }
        outW = Math.round(outW * 0.75);
        if (outW < 120) break;
      }
      this.error.set('This picture is too detailed to shrink enough. Choose a simpler image or zoom out.');
    } finally { this.busy.set(false); }
  }
}
