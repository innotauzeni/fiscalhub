import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api.service';
import {
  CanvasElement, CanvasLayout, CanvasStyle, ElementType, FieldInfo, ITEM_COLUMNS, InvoiceDocument, PAGE_H, PAGE_W, PRESETS, Preset, REQUIRED, TOTAL_ROWS, TemplateAsset,
  fill, hasTitle, isBareHeading, makeElement, money, newId,
} from '../../core/canvas';
import { InvoiceTemplate } from '../../core/models';
import { DialogService, ToastService } from '../../core/ui.services';
import { IconComponent } from '../../shared/ui.components';

interface Guide { kind: 'v' | 'h'; at: number; }
type Handle = 'nw' | 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w';

const PALETTE: { type: ElementType; label: string; icon: string; hint: string }[] = [
  { type: 'text', label: 'Text', icon: 'edit', hint: 'Any words; {placeholders} fill in' },
  { type: 'field', label: 'Invoice value', icon: 'file-text', hint: 'One value: number, date, customer…' },
  { type: 'logo', label: 'Logo', icon: 'building', hint: 'The template\'s logo' },
  { type: 'image', label: 'Picture', icon: 'upload', hint: 'Background, stamp, signature…' },
  { type: 'shape', label: 'Box / circle', icon: 'squares', hint: 'Colours, gradients, rounded' },
  { type: 'line', label: 'Line', icon: 'minus', hint: 'Divider' },
  { type: 'items', label: 'Items table', icon: 'layers', hint: 'Required by ZIMRA' },
  { type: 'totals', label: 'Totals', icon: 'chart', hint: 'Required by ZIMRA' },
  { type: 'qr', label: 'ZIMRA QR', icon: 'shield', hint: 'Required by ZIMRA' },
  { type: 'fiscal', label: 'Fiscal details', icon: 'receipt', hint: 'Required by ZIMRA' },
];

/**
 * The invoice builder: place every part of the invoice anywhere on an A4 page, style it, and save it as the template. Drag to move, pull the handles to
 * resize; arrows nudge (Shift = 5 mm); Ctrl+Z / Ctrl+Y undo and redo; Ctrl+D duplicates; Ctrl+C / Ctrl+V copy; Delete removes; Ctrl+S saves.
 */
@Component({
  selector: 'app-invoice-builder',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, IconComponent, NgTemplateOutlet],
  styles: [`
    :host { display: block; }
    .el { position: absolute; box-sizing: border-box; overflow: hidden; cursor: move; user-select: none; }
    .el.locked { cursor: default; }
    .sel { outline: 1.5px solid #2563eb; outline-offset: 0; z-index: 2; }
    .hover:not(.sel):hover { outline: 1px dashed #93c5fd; }
    .handle { position: absolute; width: 9px; height: 9px; background: #fff; border: 1.5px solid #2563eb; border-radius: 2px; z-index: 5; }
    .guide { position: absolute; background: #ec4899; z-index: 6; pointer-events: none; }
    .grid-bg { background-image: linear-gradient(to right, rgba(148,163,184,.18) 1px, transparent 1px), linear-gradient(to bottom, rgba(148,163,184,.18) 1px, transparent 1px); }
    .tool { display: flex; align-items: center; gap: .6rem; width: 100%; padding: .45rem .55rem; border-radius: .6rem; text-align: left; font-size: .8rem; }
    .tool:hover { background: rgb(241 245 249); }
    :host-context(.dark) .tool:hover { background: rgb(30 41 59); }
    .lbl { font-size: .68rem; font-weight: 600; text-transform: uppercase; letter-spacing: .04em; color: rgb(100 116 139); margin-bottom: .2rem; display: block; }
    .mini { width: 100%; border: 1px solid rgb(203 213 225); border-radius: .45rem; padding: .25rem .4rem; font-size: .78rem; background: transparent; }
    :host-context(.dark) .mini { border-color: rgb(51 65 85); }
    .color { width: 2rem; height: 1.7rem; padding: 0; border: 1px solid rgb(203 213 225); border-radius: .4rem; background: none; }
    .seg button { padding: .2rem .45rem; font-size: .75rem; border-radius: .35rem; }
    .seg button.on { background: #2563eb; color: #fff; }
  `],
  template: `
    <!-- top bar -->
    <div class="sticky top-0 z-20 -mx-4 mb-3 flex flex-wrap items-center gap-2 border-b border-slate-200 bg-white/95 px-4 py-2 backdrop-blur dark:border-slate-800 dark:bg-slate-950/95 lg:-mx-6 lg:px-6">
      <a routerLink="/admin/templates" class="icon-btn" aria-label="Back to templates"><app-icon name="arrow-left" size="h-4 w-4" /></a>
      <div class="min-w-0">
        <div class="truncate text-sm font-semibold">{{ tpl()?.name ?? 'Invoice builder' }}</div>
        <div class="truncate text-xs text-slate-500">{{ tpl()?.branchId ? 'Branch: ' + (tpl()?.branchName ?? '') : 'Company default: every invoice uses it' }}@if (dirty()) { · <span class="text-amber-600">not saved</span> }</div>
      </div>
      <div class="ml-auto flex flex-wrap items-center gap-1.5">
        <button class="icon-btn" (click)="undo()" [disabled]="!past.length" title="Undo (Ctrl+Z)" aria-label="Undo"><app-icon name="arrow-left" size="h-4 w-4" /></button>
        <button class="icon-btn" (click)="redo()" [disabled]="!future.length" title="Redo (Ctrl+Y)" aria-label="Redo"><app-icon name="arrow-left" size="h-4 w-4" class="rotate-180" /></button>
        <span class="mx-1 h-6 w-px bg-slate-200 dark:bg-slate-700"></span>
        <button class="icon-btn" (click)="zoomBy(-0.15)" aria-label="Zoom out"><app-icon name="minus" size="h-4 w-4" /></button>
        <span class="w-11 text-center text-xs tabular-nums">{{ (zoom() * 100).toFixed(0) }}%</span>
        <button class="icon-btn" (click)="zoomBy(0.15)" aria-label="Zoom in"><app-icon name="plus" size="h-4 w-4" /></button>
        <select class="mini w-auto!" [ngModel]="grid()" (ngModelChange)="grid.set(+$event)" title="Snap to grid">
          <option [ngValue]="0">No grid</option><option [ngValue]="1">Grid 1 mm</option><option [ngValue]="2">Grid 2 mm</option><option [ngValue]="5">Grid 5 mm</option>
        </select>
        <span class="mx-1 h-6 w-px bg-slate-200 dark:bg-slate-700"></span>
        <button class="btn-ghost btn-sm" (click)="presetsOpen.set(true)"><app-icon name="layers" size="h-4 w-4" />Designs</button>
        <button class="btn-ghost btn-sm" (click)="preview()" [disabled]="busy()"><app-icon name="eye" size="h-4 w-4" />Preview</button>
        <div class="relative">
          <button class="btn-ghost btn-sm" (click)="exportOpen.set(!exportOpen())"><app-icon name="download" size="h-4 w-4" />Export</button>
          @if (exportOpen()) {
            <div class="fixed inset-0 z-30" (click)="exportOpen.set(false)"></div>
            <div class="absolute right-0 z-40 mt-1 w-64 rounded-xl border border-slate-200 bg-white p-1.5 text-sm shadow-lg dark:border-slate-700 dark:bg-slate-900">
              <button class="tool" (click)="render('pdf')"><app-icon name="file-text" size="h-4 w-4" />Sample invoice as PDF</button>
              <button class="tool" (click)="render('docx')"><app-icon name="file-text" size="h-4 w-4" />Sample invoice as Word (.docx)</button>
              <button class="tool" (click)="render('json')"><app-icon name="file-text" size="h-4 w-4" />Sample invoice as JSON data</button>
              <div class="my-1 border-t border-slate-100 dark:border-slate-800"></div>
              <button class="tool" (click)="exportTemplate()"><app-icon name="download" size="h-4 w-4" />Template file (.json)</button>
              <label class="tool cursor-pointer"><app-icon name="upload" size="h-4 w-4" />Import a template file<input type="file" accept="application/json,.json" class="sr-only" (change)="importTemplate($any($event.target))" /></label>
            </div>
          }
        </div>
        <button class="btn btn-sm" (click)="save()" [disabled]="busy() || !tpl()"><app-icon name="check" size="h-4 w-4" />{{ busy() ? 'Saving…' : 'Save' }}</button>
      </div>
    </div>

    @if (missing().length) {
      <div class="mb-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-2 text-sm text-amber-900 dark:border-amber-900/50 dark:bg-amber-900/20 dark:text-amber-100">
        ZIMRA requires these on every fiscal invoice: @for (m of missing(); track m) { <button class="ml-1 underline" (click)="addRequired(m)">add {{ typeLabel(m) }}</button> }
      </div>
    }

    @if (bareHeadings().length) {
      <div class="mb-3 rounded-xl border border-red-200 bg-red-50 px-4 py-2 text-sm text-red-800 dark:border-red-900/50 dark:bg-red-900/20 dark:text-red-200">
        A text box says just “INVOICE” as a heading. ZIMRA requires <b>FISCAL TAX INVOICE</b>: <button class="underline" (click)="select(bareHeadings()[0].id)">show it</button>, then delete it and add the Document title value (it also says CREDIT NOTE or DEBIT NOTE on those).
      </div>
    }

    <div class="grid gap-4 xl:grid-cols-[13rem_minmax(0,1fr)_17rem]">
      <!-- palette -->
      <aside class="card h-fit space-y-3 p-3 xl:sticky xl:top-20">
        <input #logoInput type="file" class="sr-only" accept="image/*" (change)="uploadLogo($any($event.target))" />
        <div>
          <span class="lbl">Add to the page</span>
          @for (p of palette; track p.type) {
            <button class="tool" (click)="p.type === 'image' ? imagePicker.set(true) : p.type === 'logo' && !logoUrl() ? logoInput.click() : add(p.type)" [title]="p.hint">
              <span class="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-brand-50 text-brand-600 dark:bg-brand-500/15"><app-icon [name]="p.icon" size="h-4 w-4" /></span>
              <span class="min-w-0"><span class="block font-medium">{{ p.label }}</span><span class="block truncate text-[11px] text-slate-500">{{ p.hint }}</span></span>
            </button>
          }
        </div>
        <div class="border-t border-slate-100 pt-3 dark:border-slate-800">
          <span class="lbl">Page colour</span>
          <div class="flex items-center gap-2"><input type="color" class="color" [ngModel]="layout().background" (ngModelChange)="setBackground($event)" /><span class="text-xs text-slate-500">{{ layout().background }}</span></div>
        </div>
        <div class="border-t border-slate-100 pt-3 dark:border-slate-800">
          <span class="lbl">Layers ({{ layout().elements.length }})</span>
          <ul class="max-h-64 space-y-0.5 overflow-y-auto text-xs">
            @for (e of reversed(); track e.id) {
              <li><button class="flex w-full items-center gap-1.5 rounded px-1.5 py-1 text-left" [class]="isSel(e.id) ? 'bg-brand-50 text-brand-700 dark:bg-brand-500/15' : 'hover:bg-slate-100 dark:hover:bg-slate-800'" (click)="select(e.id, $event.shiftKey)">
                <span class="w-14 shrink-0 text-slate-400">{{ typeLabel(e.type) }}</span><span class="truncate">{{ describe(e) }}</span>@if (e.locked) { <app-icon name="lock" size="h-3 w-3" /> }</button></li>
            }
          </ul>
        </div>
      </aside>

      <!-- page -->
      <div class="min-w-0 overflow-auto rounded-2xl bg-slate-200/70 p-6 dark:bg-slate-900" #scroller (pointerdown)="onBackground($event)">
        <div class="relative mx-auto shadow-xl" [style.width.px]="px(PAGE_W)" [style.height.px]="px(PAGE_H)" [style.background]="layout().background" #page
             (dragover)="onDragOver($event)" (dragleave)="dropping.set(false)" (drop)="onDrop($event)" [class.ring-4]="dropping()" [class.ring-brand-400]="dropping()">
          @if (dropping()) { <div class="pointer-events-none absolute inset-0 z-50 flex items-center justify-center bg-brand-500/10 text-sm font-semibold text-brand-700">Drop the picture to place it here</div> }
          @if (grid()) { <div class="grid-bg pointer-events-none absolute inset-0" [style.background-size]="px(grid()) + 'px ' + px(grid()) + 'px'"></div> }
          @for (e of layout().elements; track e.id) {
            <div class="el hover" [class.sel]="isSel(e.id)" [class.locked]="e.locked" [attr.data-id]="e.id"
                 [style.left.px]="px(e.x)" [style.top.px]="px(e.y)" [style.width.px]="px(e.w)" [style.height.px]="px(e.h)"
                 (pointerdown)="startMove($event, e)" (dblclick)="editText(e)">
              <ng-container [ngTemplateOutlet]="draw" [ngTemplateOutletContext]="{ $implicit: e }" />
            </div>
          }
          @if (single(); as e) {
            @for (h of handles; track h) {
              <span class="handle" [style.left.px]="handleX(e, h) - 4.5" [style.top.px]="handleY(e, h) - 4.5" [style.cursor]="h + '-resize'" (pointerdown)="startResize($event, e, h)"></span>
            }
          }
          @for (g of guides(); track $index) {
            @if (g.kind === 'v') { <div class="guide" [style.left.px]="px(g.at)" style="top:0;bottom:0;width:1px"></div> }
            @else { <div class="guide" [style.top.px]="px(g.at)" style="left:0;right:0;height:1px"></div> }
          }
        </div>
        <p class="mt-3 text-center text-xs text-slate-500">Sample invoice shown. Drag to move · handles resize · arrows nudge (Shift 5 mm) · Ctrl+Z undo · Ctrl+D duplicate · Delete removes · double-click text to edit</p>
      </div>

      <!-- inspector -->
      <aside class="card h-fit space-y-4 p-3 text-sm xl:sticky xl:top-20">
        @if (single(); as e) {
          <div class="flex items-center justify-between">
            <div class="font-semibold">{{ typeLabel(e.type) }}</div>
            <div class="flex gap-1">
              <button class="icon-btn h-7 w-7" (click)="toggleLock()" [title]="e.locked ? 'Unlock' : 'Lock in place'"><app-icon name="lock" size="h-3.5 w-3.5" /></button>
              <button class="icon-btn h-7 w-7" (click)="duplicate()" title="Duplicate (Ctrl+D)"><app-icon name="copy" size="h-3.5 w-3.5" /></button>
              <button class="icon-btn h-7 w-7 text-red-600" (click)="remove()" title="Delete"><app-icon name="trash" size="h-3.5 w-3.5" /></button>
            </div>
          </div>
          <div class="grid grid-cols-4 gap-1.5">
            @for (k of ['x','y','w','h']; track k) {
              <label><span class="lbl">{{ k === 'w' ? 'Width' : k === 'h' ? 'Height' : k.toUpperCase() }}</span><input class="mini" type="number" step="0.5" [ngModel]="round($any(e)[k])" (ngModelChange)="setGeom(k, $event)" /></label>
            }
          </div>
          <div>
            <span class="lbl">Arrange</span>
            <div class="flex flex-wrap gap-1">
              <button class="btn-ghost btn-sm px-2!" (click)="order('front')" title="Bring to front">Front</button>
              <button class="btn-ghost btn-sm px-2!" (click)="order('back')" title="Send to back">Back</button>
              <button class="btn-ghost btn-sm px-2!" (click)="alignPage('center')" title="Centre on the page">Centre</button>
              <button class="btn-ghost btn-sm px-2!" (click)="alignPage('full')" title="Full page width">Full width</button>
            </div>
          </div>

          <!-- what it shows -->
          @switch (e.type) {
            @case ('text') {
              <label class="block"><span class="lbl">Text</span><textarea class="mini" rows="4" [ngModel]="e.text" (ngModelChange)="patch({ text: $event })" #ta></textarea></label>
              <select class="mini" [ngModel]="''" (ngModelChange)="insertPlaceholder($event, ta)"><option value="">Insert an invoice value…</option>
                @for (g of fieldGroups(); track g.name) { <optgroup [label]="g.name">@for (f of g.fields; track f.key) { <option [value]="f.key">{{ f.label }}</option> }</optgroup> }</select>
            }
            @case ('field') {
              <label class="block"><span class="lbl">Shows</span><select class="mini" [ngModel]="e.field" (ngModelChange)="patch({ field: $event })">
                @for (g of fieldGroups(); track g.name) { <optgroup [label]="g.name">@for (f of g.fields; track f.key) { <option [value]="f.key">{{ f.label }}</option> }</optgroup> }</select></label>
              <label class="block"><span class="lbl">Words before it</span><input class="mini" [ngModel]="e.label" (ngModelChange)="patch({ label: $event })" placeholder="Invoice No:" /></label>
            }
            @case ('image') {
              <button class="btn-ghost btn-sm w-full" (click)="imagePicker.set(true)">Change picture</button>
              <div class="seg flex gap-1"><button [class.on]="e.style.fit !== 'stretch'" (click)="style({ fit: 'contain' })">Keep shape</button><button [class.on]="e.style.fit === 'stretch'" (click)="style({ fit: 'stretch' })">Stretch</button></div>
            }
            @case ('logo') {
              <p class="text-xs text-slate-500">Shows the template's logo, on every invoice made with this template.</p>
              <div class="flex flex-wrap gap-1.5">
                <button class="btn btn-sm" [disabled]="uploading()" (click)="logoInput.click()"><app-icon name="upload" size="h-3.5 w-3.5" />{{ uploading() ? 'Uploading…' : logoUrl() ? 'Replace logo' : 'Upload logo' }}</button>
                @if (logoUrl()) { <button class="btn-ghost btn-sm text-red-600" (click)="removeLogo()">Remove logo</button> }
              </div>
            }
            @case ('shape') {
              <div class="seg flex gap-1"><button [class.on]="e.shape !== 'ellipse'" (click)="patch({ shape: 'rect' })">Box</button><button [class.on]="e.shape === 'ellipse'" (click)="patch({ shape: 'ellipse' })">Circle</button></div>
            }
            @case ('qr') {
              <label class="block"><span class="lbl">Caption</span><input class="mini" [ngModel]="e.label" (ngModelChange)="patch({ label: $event })" placeholder="Scan to verify" /></label>
            }
            @case ('fiscal') {
              <label class="block"><span class="lbl">Heading (optional)</span><input class="mini" [ngModel]="e.label" (ngModelChange)="patch({ label: $event })" placeholder="Fiscal details" /></label>
              <p class="text-xs text-slate-500">Receipt and fiscal day numbers, device ID, ZIMRA receipt ID, verification address and device signature: filled in by ZIMRA's answer.</p>
            }
            @case ('items') {
              <div><span class="lbl">Columns, in order</span>
                @for (c of itemColumns; track c.key) {
                  <div class="flex items-center gap-1.5 py-0.5">
                    <input type="checkbox" [checked]="cols(e).includes(c.key)" (change)="toggleCol(c.key)" />
                    <input class="mini" [ngModel]="e.labels?.[c.key] ?? ''" (ngModelChange)="setLabel(c.key, $event)" [placeholder]="c.label" />
                    <button class="icon-btn h-6 w-6" (click)="moveCol(c.key, -1)" [disabled]="!cols(e).includes(c.key)" aria-label="Move left"><app-icon name="chevron" size="h-3 w-3" class="rotate-90" /></button>
                  </div>
                }
                <p class="hint">Headings may use {{ '{' }}currency{{ '}' }}.</p>
              </div>
            }
            @case ('totals') {
              <div><span class="lbl">Rows</span>
                @for (c of totalRows; track c.key) {
                  <div class="flex items-center gap-1.5 py-0.5">
                    <input type="checkbox" [checked]="cols(e).includes(c.key)" (change)="toggleCol(c.key)" />
                    <input class="mini" [ngModel]="e.labels?.[c.key] ?? ''" (ngModelChange)="setLabel(c.key, $event)" [placeholder]="c.label" />
                  </div>
                }
              </div>
            }
          }

          <!-- how it looks -->
          @if (e.type !== 'line' && e.type !== 'shape' && e.type !== 'image' && e.type !== 'logo') {
            <div class="space-y-2 border-t border-slate-100 pt-3 dark:border-slate-800">
              <span class="lbl">Text</span>
              <div class="grid grid-cols-[1fr_4rem] gap-1.5">
                <select class="mini" [ngModel]="e.style.font ?? ''" (ngModelChange)="style({ font: $event || null })"><option value="">Lato (default)</option>@for (f of fonts(); track f) { @if (f !== 'Lato') { <option [value]="f">{{ f }}</option> } }</select>
                <input class="mini" type="number" min="4" max="72" step="0.5" [ngModel]="e.style.fontSize" (ngModelChange)="style({ fontSize: +$event })" title="Size (pt)" />
              </div>
              <div class="flex flex-wrap items-center gap-1">
                <input type="color" class="color" [ngModel]="e.style.color" (ngModelChange)="style({ color: $event })" title="Text colour" />
                <div class="seg flex gap-0.5">
                  <button [class.on]="e.style.bold" (click)="style({ bold: !e.style.bold })"><b>B</b></button>
                  <button [class.on]="e.style.italic" (click)="style({ italic: !e.style.italic })"><i>I</i></button>
                  <button [class.on]="e.style.underline" (click)="style({ underline: !e.style.underline })"><u>U</u></button>
                  <button [class.on]="e.style.uppercase" (click)="style({ uppercase: !e.style.uppercase })">AA</button>
                </div>
              </div>
              <div class="flex flex-wrap gap-1">
                <div class="seg flex gap-0.5">@for (a of ['left','center','right']; track a) { <button [class.on]="(e.style.align ?? 'left') === a" (click)="style({ align: $any(a) })">{{ a === 'left' ? '⇤' : a === 'center' ? '↔' : '⇥' }}</button> }</div>
                <div class="seg flex gap-0.5">@for (a of ['top','middle','bottom']; track a) { <button [class.on]="(e.style.vAlign ?? 'top') === a" (click)="style({ vAlign: $any(a) })">{{ a === 'top' ? '⤒' : a === 'middle' ? '↕' : '⤓' }}</button> }</div>
              </div>
              <div class="grid grid-cols-2 gap-1.5">
                <label><span class="lbl">Letter spacing</span><input class="mini" type="number" step="0.02" min="-0.1" max="1" [ngModel]="e.style.letterSpacing ?? 0" (ngModelChange)="style({ letterSpacing: +$event })" /></label>
                <label><span class="lbl">Line height</span><input class="mini" type="number" step="0.1" min="0.8" max="3" [ngModel]="e.style.lineHeight ?? 1.2" (ngModelChange)="style({ lineHeight: +$event })" /></label>
              </div>
            </div>
          }
          @if (e.type === 'items' || e.type === 'totals') {
            <div class="space-y-2 border-t border-slate-100 pt-3 dark:border-slate-800">
              <span class="lbl">{{ e.type === 'items' ? 'Table' : 'Total row' }}</span>
              <div class="grid grid-cols-2 gap-1.5 text-xs">
                <label class="flex items-center gap-1.5"><input type="color" class="color" [ngModel]="e.style.headerFill ?? '#ffffff'" (ngModelChange)="style({ headerFill: $event })" />{{ e.type === 'items' ? 'Heading' : 'Total' }} fill</label>
                <label class="flex items-center gap-1.5"><input type="color" class="color" [ngModel]="e.style.headerColor ?? '#0f172a'" (ngModelChange)="style({ headerColor: $event })" />{{ e.type === 'items' ? 'Heading' : 'Total' }} text</label>
                @if (e.type === 'items') { <label class="flex items-center gap-1.5"><input type="color" class="color" [ngModel]="e.style.stripeFill ?? '#ffffff'" (ngModelChange)="style({ stripeFill: $event })" />Stripes</label> }
                <label class="flex items-center gap-1.5"><input type="color" class="color" [ngModel]="e.style.lineColor ?? '#e2e8f0'" (ngModelChange)="style({ lineColor: $event })" />Lines</label>
              </div>
              <div class="flex flex-wrap gap-2 text-xs">
                <button class="underline" (click)="style({ headerFill: null })">no fill</button>
                @if (e.type === 'items') { <button class="underline" (click)="style({ stripeFill: null })">no stripes</button> }
              </div>
              <label><span class="lbl">Row spacing (mm)</span><input class="mini" type="number" step="0.5" min="0" max="10" [ngModel]="e.style.rowPadding ?? 1.5" (ngModelChange)="style({ rowPadding: +$event })" /></label>
            </div>
          }
          @if (e.type === 'line') {
            <div class="grid grid-cols-2 gap-1.5 border-t border-slate-100 pt-3 dark:border-slate-800">
              <label><span class="lbl">Colour</span><input type="color" class="color" [ngModel]="e.style.border ?? '#94a3b8'" (ngModelChange)="style({ border: $event })" /></label>
              <label><span class="lbl">Thickness (pt)</span><input class="mini" type="number" step="0.25" min="0.25" max="20" [ngModel]="e.style.borderWidth ?? 1" (ngModelChange)="style({ borderWidth: +$event })" /></label>
            </div>
          }
          @if (e.type !== 'line') {
            <div class="space-y-2 border-t border-slate-100 pt-3 dark:border-slate-800">
              <span class="lbl">Box</span>
              <div class="flex flex-wrap items-center gap-2 text-xs">
                <label class="flex items-center gap-1"><input type="checkbox" [checked]="!!e.style.fill" (change)="style({ fill: e.style.fill ? null : '#f1f5f9', fill2: null })" />Fill</label>
                @if (e.style.fill) {
                  <input type="color" class="color" [ngModel]="e.style.fill" (ngModelChange)="style({ fill: $event })" />
                  <label class="flex items-center gap-1"><input type="checkbox" [checked]="!!e.style.fill2" (change)="style({ fill2: e.style.fill2 ? null : '#ffffff' })" />Gradient</label>
                  @if (e.style.fill2) {
                    <input type="color" class="color" [ngModel]="e.style.fill2" (ngModelChange)="style({ fill2: $event })" />
                    <select class="mini w-auto!" [ngModel]="e.style.gradientDir ?? 'horizontal'" (ngModelChange)="style({ gradientDir: $event })"><option value="horizontal">→</option><option value="vertical">↓</option><option value="diagonal">↘</option></select>
                  }
                }
              </div>
              <div class="flex flex-wrap items-center gap-2 text-xs">
                <label class="flex items-center gap-1"><input type="checkbox" [checked]="(e.style.borderWidth ?? 0) > 0" (change)="style((e.style.borderWidth ?? 0) > 0 ? { borderWidth: 0 } : { borderWidth: 1, border: e.style.border ?? '#cbd5e1' })" />Border</label>
                @if ((e.style.borderWidth ?? 0) > 0) {
                  <input type="color" class="color" [ngModel]="e.style.border ?? '#cbd5e1'" (ngModelChange)="style({ border: $event })" />
                  <input class="mini w-16!" type="number" step="0.25" min="0.25" max="20" [ngModel]="e.style.borderWidth" (ngModelChange)="style({ borderWidth: +$event })" title="pt" />
                }
              </div>
              <div class="grid grid-cols-3 gap-1.5">
                <label><span class="lbl">Corners</span><input class="mini" type="number" step="0.5" min="0" max="100" [ngModel]="e.style.radius ?? 0" (ngModelChange)="style({ radius: +$event })" /></label>
                <label><span class="lbl">Inside</span><input class="mini" type="number" step="0.5" min="0" max="20" [ngModel]="e.style.padding ?? 1" (ngModelChange)="style({ padding: +$event })" /></label>
                <label><span class="lbl">Opacity</span><input class="mini" type="number" step="0.05" min="0.05" max="1" [ngModel]="e.style.opacity ?? 1" (ngModelChange)="style({ opacity: +$event })" /></label>
              </div>
            </div>
          }
        } @else if (selected().size > 1) {
          <div class="font-semibold">{{ selected().size }} elements</div>
          <span class="lbl">Line them up</span>
          <div class="grid grid-cols-3 gap-1">
            @for (a of alignOptions; track a.key) { <button class="btn-ghost btn-sm px-1!" (click)="alignSel(a.key)">{{ a.label }}</button> }
          </div>
          <div class="flex gap-1"><button class="btn-ghost btn-sm" (click)="duplicate()">Duplicate</button><button class="btn-danger btn-sm" (click)="remove()">Delete</button></div>
        } @else {
          <div class="font-semibold">Nothing selected</div>
          <p class="text-xs text-slate-500">Click an element on the page to change it, or add one from the left. Shift+click selects several to line them up.</p>
          <p class="text-xs text-slate-500">Text can show invoice values with placeholders such as <code class="code">{{ '{' }}invoiceNo{{ '}' }}</code>, <code class="code">{{ '{' }}buyerName{{ '}' }}</code> or <code class="code">{{ '{' }}total{{ '}' }}</code>.</p>
          <p class="text-xs text-slate-500">Saving makes this the look of {{ tpl()?.branchId ? 'this branch\\'s' : 'all the company\\'s' }} A4 invoices. The 80 mm roll keeps the standard layout.</p>
          @if (layout().elements.length) { <button class="btn-ghost btn-sm text-red-600" (click)="useStandard()">Go back to the standard layout</button> }
        }
      </aside>
    </div>

    <!-- one element, drawn like the PDF -->
    <ng-template #draw let-e>
      <div class="pointer-events-none absolute inset-0" [style]="boxCss(e)"></div>
      @switch (e.type) {
        @case ('shape') {}
        @case ('line') { <div class="absolute" [style]="lineCss(e)"></div> }
        @case ('logo') { @if (logoUrl()) { <img [src]="logoUrl()" alt="" class="pointer-events-none absolute inset-0 h-full w-full" [style.object-fit]="'contain'" [style.object-position]="pos(e)" /> } @else { <div class="absolute inset-0 flex items-center justify-center border border-dashed border-slate-300 text-[10px] text-slate-400">LOGO</div> } }
        @case ('image') { @if (assetUrl(e.assetId)) { <img [src]="assetUrl(e.assetId)" alt="" class="pointer-events-none absolute inset-0 h-full w-full" [style.object-fit]="e.style.fit === 'stretch' ? 'fill' : 'contain'" [style.object-position]="pos(e)" /> } }
        @case ('qr') {
          <div class="absolute inset-0 flex flex-col items-center" [style.padding.px]="px(e.style.padding ?? 0)">
            <svg viewBox="0 0 29 29" class="min-h-0 w-full flex-1" preserveAspectRatio="xMidYMid meet"><rect width="29" height="29" fill="#fff"/>
              @for (q of qrCells; track $index) { <rect [attr.x]="q[0]" [attr.y]="q[1]" width="1" height="1" fill="#111"/> }</svg>
            @if (e.label) { <div class="shrink-0 leading-tight" [style.font-size.px]="pt(e.style.fontSize * 0.75)" [style.color]="e.style.color">{{ e.label }}</div> }
          </div>
        }
        @case ('items') {
          <div class="absolute inset-0 overflow-hidden" [style.padding.px]="px(e.style.padding ?? 1)">
            <table class="w-full border-collapse" [style]="textCss(e)">
              <thead><tr>@for (c of cols(e); track c) { <th [style]="cellCss(e, c, true)">{{ colLabel(e, c) }}</th> }</tr></thead>
              <tbody>@for (l of sample()?.lines ?? []; track l.no; let i = $index) { <tr [style.background]="i % 2 === 1 && e.style.stripeFill ? e.style.stripeFill : null">@for (c of cols(e); track c) { <td [style]="cellCss(e, c, false)">{{ cell(l, c) }}</td> }</tr> }</tbody>
            </table>
          </div>
        }
        @case ('totals') {
          <div class="absolute inset-0 overflow-hidden" [style.padding.px]="px(e.style.padding ?? 1)" [style]="textCss(e)">
            @for (r of totalLines(e); track $index) {
              <div class="flex justify-between" [style.padding]="px(e.style.rowPadding ?? 1.5) + 'px ' + 4 + 'px'" [style.background]="r.strong && e.style.headerFill ? e.style.headerFill : null"
                   [style.color]="r.strong && e.style.headerColor ? e.style.headerColor : null" [style.border-bottom]="r.strong && e.style.headerFill ? 'none' : '0.5px solid ' + (e.style.lineColor ?? '#e2e8f0')"
                   [style.font-weight]="r.strong ? 700 : null" [style.font-size.px]="r.strong ? pt(e.style.fontSize * 1.15) : null"><span>{{ r.label }}</span><span>{{ r.amount }}</span></div>
            }
          </div>
        }
        @default {
          <div class="absolute inset-0 flex overflow-hidden whitespace-pre-wrap" [style]="textCss(e)">
            <div class="w-full" [style.text-align]="e.style.align ?? 'left'">{{ content(e) }}</div>
          </div>
        }
      }
    </ng-template>

    <!-- designs to start from -->
    @if (presetsOpen()) {
      <div class="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/50 p-4" (click)="presetsOpen.set(false)">
        <div class="max-h-[90vh] w-full max-w-3xl overflow-y-auto rounded-2xl bg-white p-6 shadow-2xl dark:bg-slate-900" (click)="$event.stopPropagation()">
          <div class="mb-4 flex items-center justify-between"><h2 class="text-lg font-semibold">Start from a design</h2><button class="icon-btn" (click)="presetsOpen.set(false)" aria-label="Close"><app-icon name="x" size="h-4 w-4" /></button></div>
          <div class="grid gap-3 sm:grid-cols-2">
            @for (p of presets; track p.key) {
              <button class="flex gap-3 rounded-xl border border-slate-200 p-3 text-left transition hover:border-brand-400 hover:shadow dark:border-slate-700" (click)="usePreset(p)">
                <span class="h-16 w-12 shrink-0 rounded-md shadow-inner" [style.background]="p.swatch"></span>
                <span><span class="block font-semibold">{{ p.name }}</span><span class="text-xs text-slate-500">{{ p.hint }}</span></span>
              </button>
            }
          </div>
          <p class="mt-3 text-xs text-slate-500">Your colours, words and pictures can all be changed afterwards. Picking a design replaces what is on the page (Undo brings it back).</p>
        </div>
      </div>
    }

    <!-- pictures -->
    @if (imagePicker()) {
      <div class="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/50 p-4" (click)="imagePicker.set(false)">
        <div class="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-2xl bg-white p-6 shadow-2xl dark:bg-slate-900" (click)="$event.stopPropagation()">
          <div class="mb-4 flex items-center justify-between"><h2 class="text-lg font-semibold">Pictures</h2><button class="icon-btn" (click)="imagePicker.set(false)" aria-label="Close"><app-icon name="x" size="h-4 w-4" /></button></div>
          <label class="btn btn-sm mb-4 cursor-pointer"><app-icon name="upload" size="h-4 w-4" />{{ uploading() ? 'Uploading…' : 'Upload a picture (PNG, JPEG, WebP…)' }}<input type="file" class="sr-only" accept="image/*" [disabled]="uploading()" (change)="upload($any($event.target))" /></label>
          <p class="mb-3 text-xs text-slate-500">A full-page design as a background, a signature, a stamp, a decoration. Tip: for a designed background, upload it without the invoice text and put the invoice values on top.</p>
          <div class="grid grid-cols-3 gap-3 sm:grid-cols-4">
            @for (a of assets(); track a.id) {
              <div class="group relative">
                <button class="flex aspect-square w-full items-center justify-center overflow-hidden rounded-lg border border-slate-200 bg-slate-50 p-1 hover:border-brand-400 dark:border-slate-700 dark:bg-slate-800" (click)="pickAsset(a)">
                  @if (assetUrl(a.id)) { <img [src]="assetUrl(a.id)" alt="" class="max-h-full max-w-full object-contain" /> }
                </button>
                <button class="absolute right-1 top-1 hidden rounded bg-white/90 p-1 text-red-600 shadow group-hover:block" (click)="deleteAsset(a)" aria-label="Delete picture"><app-icon name="trash" size="h-3.5 w-3.5" /></button>
                <div class="mt-1 truncate text-[11px] text-slate-500">{{ a.name ?? 'Picture' }}</div>
              </div>
            } @empty { <p class="col-span-full py-6 text-center text-sm text-slate-500">No pictures yet.</p> }
          </div>
        </div>
      </div>
    }

    @if (previewUrl()) {
      <div class="fixed inset-0 z-50 flex flex-col bg-slate-900/80 p-4" (click)="closePreview()">
        <div class="mb-2 flex items-center justify-between text-white"><span class="text-sm">Preview with a sample invoice (exactly as it prints)</span><button class="rounded-lg p-2 hover:bg-white/10" (click)="closePreview()" aria-label="Close"><app-icon name="x" size="h-5 w-5" /></button></div>
        <div class="flex-1 overflow-auto text-center"><img [src]="previewUrl()" alt="Invoice preview" class="mx-auto max-w-full bg-white shadow-2xl" /></div>
      </div>
    }
  `,
})
export class InvoiceBuilderComponent implements OnInit {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly dialog = inject(DialogService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageRef = viewChild<ElementRef<HTMLDivElement>>('page');

  readonly PAGE_W = PAGE_W; readonly PAGE_H = PAGE_H;
  readonly palette = PALETTE; readonly presets = PRESETS; readonly itemColumns = ITEM_COLUMNS; readonly totalRows = TOTAL_ROWS;
  readonly handles: Handle[] = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'];
  readonly alignOptions = [{ key: 'left', label: 'Left' }, { key: 'hcenter', label: 'Centre' }, { key: 'right', label: 'Right' }, { key: 'top', label: 'Top' }, { key: 'vmiddle', label: 'Middle' }, { key: 'bottom', label: 'Bottom' }, { key: 'width', label: 'Same width' }, { key: 'height', label: 'Same height' }, { key: 'vspace', label: 'Space evenly' }];

  readonly tpl = signal<InvoiceTemplate | null>(null);
  readonly layout = signal<CanvasLayout>({ background: '#ffffff', elements: [] });
  readonly selected = signal<Set<string>>(new Set());
  readonly fields = signal<FieldInfo[]>([]);
  readonly fonts = signal<string[]>([]);
  readonly sample = signal<InvoiceDocument | null>(null);
  readonly assets = signal<TemplateAsset[]>([]);
  readonly assetUrls = signal<Record<string, string>>({});
  readonly logoUrl = signal<string | null>(null);
  readonly zoom = signal(1);
  readonly grid = signal(2);
  readonly guides = signal<Guide[]>([]);
  readonly busy = signal(false);
  readonly uploading = signal(false);
  readonly dropping = signal(false);
  readonly dirty = signal(false);
  readonly presetsOpen = signal(false);
  readonly imagePicker = signal(false);
  readonly exportOpen = signal(false);
  readonly previewUrl = signal<string | null>(null);
  past: string[] = []; future: string[] = [];
  private clipboard: CanvasElement[] = [];
  private known = new Set<string>();
  /** a fixed, QR-looking pattern for the page (the real code is drawn when the invoice prints) */
  readonly qrCells: [number, number][] = (() => {
    const cells: [number, number][] = []; let seed = 7;
    const finder = (x: number, y: number) => { for (let i = 0; i < 7; i++) for (let j = 0; j < 7; j++) if (i === 0 || i === 6 || j === 0 || j === 6 || (i >= 2 && i <= 4 && j >= 2 && j <= 4)) cells.push([x + i, y + j]); };
    finder(1, 1); finder(21, 1); finder(1, 21);
    for (let x = 1; x < 28; x++) for (let y = 1; y < 28; y++) { if ((x < 9 && y < 9) || (x > 19 && y < 9) || (x < 9 && y > 19)) continue; seed = (seed * 9301 + 49297) % 233280; if (seed / 233280 > 0.55) cells.push([x, y]); }
    return cells;
  })();

  readonly single = computed(() => { const sel = this.selected(); return sel.size === 1 ? this.layout().elements.find(e => sel.has(e.id)) ?? null : null; });
  readonly reversed = computed(() => [...this.layout().elements].reverse());
  /** what ZIMRA requires and the page lacks; 'title' = the FISCAL TAX INVOICE heading */
  readonly missing = computed(() => [...(hasTitle(this.layout()) ? [] : ['title']), ...REQUIRED.filter(t => !this.layout().elements.some(e => e.type === t))]);
  /** text boxes that say just "INVOICE" (or similar) as a heading */
  readonly bareHeadings = computed(() => this.layout().elements.filter(e => e.type === 'text' && (e.text ?? '').split(String.fromCharCode(10)).some(isBareHeading)));
  readonly fieldGroups = computed(() => {
    const groups: { name: string; fields: FieldInfo[] }[] = [];
    for (const f of this.fields()) { let g = groups.find(x => x.name === f.group); if (!g) groups.push(g = { name: f.group, fields: [] }); g.fields.push(f); }
    return groups;
  });

  constructor() {
    this.destroyRef.onDestroy(() => { for (const u of Object.values(this.assetUrls())) URL.revokeObjectURL(u); const l = this.logoUrl(); if (l) URL.revokeObjectURL(l); this.closePreview(); });
    window.addEventListener('beforeunload', this.beforeUnload);
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', this.beforeUnload));
  }
  private readonly beforeUnload = (ev: BeforeUnloadEvent) => { if (this.dirty()) { ev.preventDefault(); ev.returnValue = ''; } };

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id');
    try {
      const [list, meta, sample, assets] = await Promise.all([
        this.api.get<InvoiceTemplate[]>('/api/v1/templates'), this.api.get<{ fields: FieldInfo[]; fonts: string[] }>('/api/v1/templates/fields'),
        this.api.get<InvoiceDocument>('/api/v1/templates/sample'), this.api.get<TemplateAsset[]>('/api/v1/templates/assets'),
      ]);
      const t = list.find(x => x.id === id);
      if (!t) { this.toast.err(new Error('That template no longer exists.')); await this.router.navigateByUrl('/admin/templates'); return; }
      this.tpl.set(t); this.fields.set(meta.fields); this.fonts.set(meta.fonts); this.sample.set(sample); this.assets.set(assets);
      this.known = new Set(meta.fields.map(f => f.key));
      const canvas = (t.settings as unknown as { canvas?: CanvasLayout }).canvas;
      if (canvas?.elements?.length) this.layout.set(structuredClone(canvas));
      else this.presetsOpen.set(true);
      if (t.hasLogo) { try { this.logoUrl.set(URL.createObjectURL(await this.api.blob(`/api/v1/templates/${t.id}/logo`))); } catch { /* drawn as a placeholder */ } }
      for (const a of assets) void this.loadAsset(a.id);
      const width = this.pageRef()?.nativeElement.parentElement?.clientWidth ?? 900;
      this.zoom.set(Math.max(0.5, Math.min(1.6, (width - 48) / (PAGE_W * 3.78))));
    } catch (e) { this.toast.err(e); }
  }

  private async loadAsset(id: string): Promise<void> {
    if (this.assetUrls()[id]) return;
    try { const url = URL.createObjectURL(await this.api.blob(`/api/v1/templates/assets/${id}`)); this.assetUrls.update(m => ({ ...m, [id]: url })); } catch { /* shown empty */ }
  }

  // ---- geometry ----
  /** millimetres to screen pixels (96 dpi ≈ 3.78 px per mm, times the zoom) */
  px(mm: number): number { return mm * 3.7795 * this.zoom(); }
  pt(points: number): number { return points * 1.3333 * this.zoom(); }
  round(v: number): number { return Math.round(v * 10) / 10; }
  zoomBy(d: number): void { this.zoom.set(Math.max(0.35, Math.min(3, +(this.zoom() + d).toFixed(2)))); }
  private snap(v: number): number { const g = this.grid(); return g ? Math.round(v / g) * g : Math.round(v * 10) / 10; }
  handleX(e: CanvasElement, h: Handle): number { return this.px(h.includes('w') ? e.x : h.includes('e') ? e.x + e.w : e.x + e.w / 2); }
  handleY(e: CanvasElement, h: Handle): number { return this.px(h.includes('n') ? e.y : h.includes('s') ? e.y + e.h : e.y + e.h / 2); }

  // ---- history ----
  private commit(): void {
    this.past.push(JSON.stringify(this.layout()));
    if (this.past.length > 100) this.past.shift();
    this.future = [];
    this.dirty.set(true);
  }
  undo(): void { const prev = this.past.pop(); if (!prev) return; this.future.push(JSON.stringify(this.layout())); this.layout.set(JSON.parse(prev)); this.dirty.set(true); }
  redo(): void { const next = this.future.pop(); if (!next) return; this.past.push(JSON.stringify(this.layout())); this.layout.set(JSON.parse(next)); this.dirty.set(true); }

  private mutate(fn: (els: CanvasElement[]) => CanvasElement[], record = true): void {
    if (record) this.commit();
    this.layout.update(l => ({ ...l, elements: fn(l.elements) }));
  }
  private updateSelected(fn: (e: CanvasElement) => CanvasElement, record = true): void {
    const sel = this.selected();
    this.mutate(els => els.map(e => sel.has(e.id) ? fn(e) : e), record);
  }

  // ---- selection ----
  isSel(id: string): boolean { return this.selected().has(id); }
  select(id: string, add = false): void {
    const s = new Set(add ? this.selected() : []);
    if (add && s.has(id)) s.delete(id); else s.add(id);
    this.selected.set(s);
  }
  onBackground(ev: PointerEvent): void {
    const t = ev.target as HTMLElement;
    if (!t.closest('.el') && !t.classList.contains('handle')) this.selected.set(new Set());
  }

  // ---- adding ----
  /** fixes what the banner reports: the title comes in as the Document title value, centred near the top */
  addRequired(m: string): void {
    if (m === 'title') this.add('field', { field: 'title', label: null, x: 40, y: 40, w: 130, h: 12, style: { fontSize: 18, bold: true, align: 'center', vAlign: 'middle', letterSpacing: 0.1, color: '#0f172a' } as CanvasStyle });
    else this.add(m as ElementType);
  }

  add(type: ElementType, extra: Partial<CanvasElement> = {}): void {
    const e = makeElement(type, extra);
    this.mutate(els => [...els, e]);
    this.selected.set(new Set([e.id]));
  }
  setBackground(c: string): void { this.commit(); this.layout.update(l => ({ ...l, background: c })); }
  async usePreset(p: Preset): Promise<void> {
    this.presetsOpen.set(false);
    if (this.layout().elements.length && !(await this.dialog.confirm(`Replace the page with the “${p.name}” design?`, 'Replace'))) return;
    this.commit();
    this.layout.set(p.build());
    this.selected.set(new Set());
  }
  async useStandard(): Promise<void> {
    if (!(await this.dialog.confirm('Remove this design and print with the standard layout again? (Undo brings it back until you leave.)', 'Use standard layout', true))) return;
    this.commit();
    this.layout.set({ background: '#ffffff', elements: [] });
  }

  // ---- moving and resizing ----
  startMove(ev: PointerEvent, e: CanvasElement): void {
    if (ev.button !== 0) return;
    ev.stopPropagation();
    if (!this.isSel(e.id)) this.select(e.id, ev.shiftKey);
    else if (ev.shiftKey) { this.select(e.id, true); return; }
    const moving = this.layout().elements.filter(x => this.isSel(x.id) && !x.locked);
    if (!moving.length) return;
    const start = { x: ev.clientX, y: ev.clientY };
    const origin = new Map(moving.map(m => [m.id, { x: m.x, y: m.y }]));
    const lead = moving.find(m => m.id === e.id) ?? moving[0];
    let committed = false;
    const others = this.layout().elements.filter(x => !origin.has(x.id));
    const onMove = (m: PointerEvent) => {
      const dx = (m.clientX - start.x) / this.px(1), dy = (m.clientY - start.y) / this.px(1);
      if (!committed && Math.abs(dx) + Math.abs(dy) < 0.5) return;
      if (!committed) { this.commit(); committed = true; }
      const o = origin.get(lead.id)!;
      let nx = this.snap(o.x + dx), ny = this.snap(o.y + dy);
      // line up with the page and the other elements: edges and centres within 1.5 mm
      const guides: Guide[] = [];
      const xs = [0, PAGE_W / 2, PAGE_W, ...others.flatMap(x => [x.x, x.x + x.w / 2, x.x + x.w])];
      const ys = [0, PAGE_H / 2, PAGE_H, ...others.flatMap(x => [x.y, x.y + x.h / 2, x.y + x.h])];
      const near = (cands: number[], points: number[]) => { let best: { d: number; at: number; off: number } | null = null; for (const c of cands) for (const [i, p] of points.entries()) { const d = Math.abs(c - p); if (d < 1.5 && (!best || d < best.d)) best = { d, at: c, off: c - p + 0 * i }; } return best; };
      const bx = near(xs, [nx, nx + lead.w / 2, nx + lead.w]); if (bx) { nx += bx.off; guides.push({ kind: 'v', at: bx.at }); }
      const by = near(ys, [ny, ny + lead.h / 2, ny + lead.h]); if (by) { ny += by.off; guides.push({ kind: 'h', at: by.at }); }
      this.guides.set(guides);
      const ddx = nx - o.x, ddy = ny - o.y;
      this.mutate(els => els.map(x => { const p = origin.get(x.id); return p ? { ...x, x: clamp(p.x + ddx, 0, PAGE_W - x.w), y: clamp(p.y + ddy, 0, PAGE_H - x.h) } : x; }), false);
    };
    const onUp = () => { window.removeEventListener('pointermove', onMove); window.removeEventListener('pointerup', onUp); this.guides.set([]); };
    window.addEventListener('pointermove', onMove); window.addEventListener('pointerup', onUp);
  }

  startResize(ev: PointerEvent, e: CanvasElement, h: Handle): void {
    ev.stopPropagation(); ev.preventDefault();
    if (e.locked) return;
    this.commit();
    const start = { x: ev.clientX, y: ev.clientY, ox: e.x, oy: e.y, ow: e.w, oh: e.h };
    const onMove = (m: PointerEvent) => {
      const dx = (m.clientX - start.x) / this.px(1), dy = (m.clientY - start.y) / this.px(1);
      let { ox: x, oy: y, ow: w, oh: hh } = start;
      if (h.includes('e')) w = this.snap(start.ow + dx);
      if (h.includes('s')) hh = this.snap(start.oh + dy);
      if (h.includes('w')) { const nx = this.snap(start.ox + dx); w = start.ow + (start.ox - nx); x = nx; }
      if (h.includes('n')) { const ny = this.snap(start.oy + dy); hh = start.oh + (start.oy - ny); y = ny; }
      const min = e.type === 'line' ? 0.5 : 3;
      w = Math.max(min, w); hh = Math.max(min, hh);
      x = clamp(x, 0, PAGE_W - 1); y = clamp(y, 0, PAGE_H - 1); w = Math.min(w, PAGE_W - x); hh = Math.min(hh, PAGE_H - y);
      this.mutate(els => els.map(z => z.id === e.id ? { ...z, x, y, w, h: hh } : z), false);
    };
    const onUp = () => { window.removeEventListener('pointermove', onMove); window.removeEventListener('pointerup', onUp); };
    window.addEventListener('pointermove', onMove); window.addEventListener('pointerup', onUp);
  }

  setGeom(k: string, v: number): void {
    if (!Number.isFinite(+v)) return;
    this.updateSelected(e => {
      const n = { ...e, [k]: +v } as CanvasElement;
      n.x = clamp(n.x, 0, PAGE_W - 1); n.y = clamp(n.y, 0, PAGE_H - 1); n.w = clamp(n.w, 0.5, PAGE_W - n.x); n.h = clamp(n.h, 0.5, PAGE_H - n.y);
      return n;
    });
  }

  // ---- editing ----
  patch(p: Partial<CanvasElement>): void { this.updateSelected(e => ({ ...e, ...p })); }
  style(p: Partial<CanvasStyle>): void { this.updateSelected(e => ({ ...e, style: { ...e.style, ...p } })); }
  toggleLock(): void { this.updateSelected(e => ({ ...e, locked: !e.locked })); }
  remove(): void { const sel = this.selected(); this.mutate(els => els.filter(e => !sel.has(e.id))); this.selected.set(new Set()); }
  duplicate(): void {
    const sel = this.selected();
    const copies = this.layout().elements.filter(e => sel.has(e.id)).map(e => ({ ...structuredClone(e), id: newId(), x: clamp(e.x + 4, 0, PAGE_W - e.w), y: clamp(e.y + 4, 0, PAGE_H - e.h), locked: false }));
    this.mutate(els => [...els, ...copies]);
    this.selected.set(new Set(copies.map(c => c.id)));
  }
  order(where: 'front' | 'back'): void {
    const sel = this.selected();
    this.mutate(els => { const picked = els.filter(e => sel.has(e.id)), rest = els.filter(e => !sel.has(e.id)); return where === 'front' ? [...rest, ...picked] : [...picked, ...rest]; });
  }
  alignPage(how: 'center' | 'full'): void { this.updateSelected(e => how === 'center' ? { ...e, x: this.round((PAGE_W - e.w) / 2) } : { ...e, x: 0, w: PAGE_W }); }
  alignSel(key: string): void {
    const sel = this.layout().elements.filter(e => this.isSel(e.id));
    if (sel.length < 2) return;
    const minX = Math.min(...sel.map(e => e.x)), maxX = Math.max(...sel.map(e => e.x + e.w)), minY = Math.min(...sel.map(e => e.y)), maxY = Math.max(...sel.map(e => e.y + e.h));
    const first = sel[0];
    const ordered = [...sel].sort((a, b) => a.y - b.y);
    const gap = ordered.length > 2 ? (maxY - minY - ordered.reduce((s2, e) => s2 + e.h, 0)) / (ordered.length - 1) : 0;
    let cursor = minY;
    const pos = new Map<string, number>();
    for (const e of ordered) { pos.set(e.id, cursor); cursor += e.h + gap; }
    this.updateSelected(e => {
      switch (key) {
        case 'left': return { ...e, x: minX };
        case 'right': return { ...e, x: maxX - e.w };
        case 'hcenter': return { ...e, x: (minX + maxX) / 2 - e.w / 2 };
        case 'top': return { ...e, y: minY };
        case 'bottom': return { ...e, y: maxY - e.h };
        case 'vmiddle': return { ...e, y: (minY + maxY) / 2 - e.h / 2 };
        case 'width': return { ...e, w: first.w };
        case 'height': return { ...e, h: first.h };
        case 'vspace': return ordered.length > 2 ? { ...e, y: pos.get(e.id)! } : e;
        default: return e;
      }
    });
  }
  async editText(e: CanvasElement): Promise<void> {
    if (e.type !== 'text') return;
    const v = await this.dialog.prompt('Text (use {placeholders} such as {invoiceNo} for invoice values)', e.text ?? '', 'Done');
    if (v !== null) { this.selected.set(new Set([e.id])); this.patch({ text: v }); }
  }
  insertPlaceholder(key: string, ta: HTMLTextAreaElement): void {
    if (!key) return;
    const e = this.single(); if (!e) return;
    const text = e.text ?? '';
    const at = ta.selectionStart ?? text.length;
    this.patch({ text: text.slice(0, at) + '{' + key + '}' + text.slice(ta.selectionEnd ?? at) });
  }
  cols(e: CanvasElement): string[] {
    const all = e.type === 'items' ? ITEM_COLUMNS.map(c => c.key) : TOTAL_ROWS.map(c => c.key);
    const c = (e.columns ?? []).filter(k => all.includes(k));
    return c.length ? c : e.type === 'items' ? ['no', 'item', 'qty', 'price', 'total'] : ['subtotal', 'taxes', 'total'];
  }
  toggleCol(key: string): void {
    this.updateSelected(e => { const c = this.cols(e); const next = c.includes(key) ? c.filter(k => k !== key) : [...c, key]; return { ...e, columns: next.length ? next : c }; });
  }
  moveCol(key: string, d: number): void {
    this.updateSelected(e => { const c = [...this.cols(e)]; const i = c.indexOf(key); const j = i + d; if (i < 0 || j < 0 || j >= c.length) return e; [c[i], c[j]] = [c[j], c[i]]; return { ...e, columns: c }; });
  }
  setLabel(key: string, v: string): void { this.updateSelected(e => { const labels = { ...(e.labels ?? {}) }; if (v.trim()) labels[key] = v; else delete labels[key]; return { ...e, labels }; }); }

  // ---- drawing (approximates the PDF; Preview shows the exact print) ----
  typeLabel(t: string): string { return { title: 'the title (FISCAL TAX INVOICE)', text: 'Text', field: 'Value', image: 'Picture', logo: 'Logo', shape: 'Shape', line: 'Line', items: 'Items', totals: 'Totals', qr: 'QR code', fiscal: 'Fiscal' }[t] ?? t; }
  describe(e: CanvasElement): string {
    if (e.type === 'text') return (e.text ?? '').split('\n')[0];
    if (e.type === 'field') return this.fields().find(f => f.key === e.field)?.label ?? e.field ?? '';
    if (e.type === 'shape') return e.shape === 'ellipse' ? 'circle' : 'box';
    return '';
  }
  content(e: CanvasElement): string {
    const f = this.sample()?.fields ?? {};
    let t = '';
    if (e.type === 'text') t = fill(e.text, f, this.known);
    else if (e.type === 'field') { const v = e.field ? f[e.field] ?? '' : ''; t = e.label ? `${fill(e.label, f, this.known)} ${v}`.trim() : v; }
    else if (e.type === 'fiscal') {
      const s2 = this.sample()?.fiscal;
      t = (e.label ? e.label + '\n' : '') + (s2 ? `Receipt ${s2.receiptCounter} · Global ${s2.globalNo} · Fiscal day ${s2.fiscalDay} · Device ${s2.deviceId}\nZIMRA receipt ID: ${s2.zimraReceiptId ?? ''}\nVerify: ${s2.verificationUrl ?? ''}\nDevice signature: ${s2.signature ?? ''}` : '');
    }
    return e.style.uppercase ? t.toUpperCase() : t;
  }
  colLabel(e: CanvasElement, k: string): string {
    const dflt = { no: '#', item: 'Description', hs: 'HS Code', qty: 'Qty', price: 'Unit price', tax: 'VAT', total: 'Amount' }[k] ?? k;
    return fill(e.labels?.[k] || dflt, this.sample()?.fields ?? {}, this.known);
  }
  cell(l: InvoiceDocument['lines'][number], k: string): string {
    switch (k) {
      case 'no': return String(l.no);
      case 'item': return l.description;
      case 'hs': return l.hsCode ?? '';
      case 'qty': return String(l.quantity);
      case 'price': return money(l.unitPrice);
      case 'tax': return l.taxPercent != null ? `${l.taxPercent}%` : 'Exempt';
      default: return money(l.amount);
    }
  }
  totalLines(e: CanvasElement): { label: string; amount: string; strong: boolean }[] {
    const d = this.sample(); if (!d) return [];
    const f = d.fields;
    const lab = (k: string, dflt: string) => fill(e.labels?.[k] || dflt, f, this.known);
    const out: { label: string; amount: string; strong: boolean }[] = [];
    for (const k of this.cols(e)) {
      if (k === 'subtotal') out.push({ label: lab('subtotal', 'Subtotal ({currency})'), amount: money(d.subtotal), strong: false });
      if (k === 'taxes') for (const t of d.taxes) out.push({ label: t.percent != null ? lab('taxes', 'VAT ({rate})').replace('{rate}', `${t.percent}%`) : 'Exempt', amount: money(t.tax), strong: false });
      if (k === 'total') out.push({ label: lab('total', 'TOTAL ({currency})'), amount: money(d.total), strong: true });
      if (k === 'payments') for (const p of d.payments) out.push({ label: p.method, amount: money(p.amount), strong: false });
    }
    return out;
  }
  pos(e: CanvasElement): string { return `${e.style.align === 'left' ? 'left' : e.style.align === 'right' ? 'right' : 'center'} ${e.style.vAlign === 'top' ? 'top' : e.style.vAlign === 'bottom' ? 'bottom' : 'center'}`; }
  boxCss(e: CanvasElement): string {
    const st = e.style;
    if (e.type === 'line') return '';
    const parts: string[] = [];
    if (st.fill) parts.push(`background:${st.fill2 ? `linear-gradient(${st.gradientDir === 'vertical' ? '180deg' : st.gradientDir === 'diagonal' ? '135deg' : '90deg'},${st.fill},${st.fill2})` : st.fill}`);
    if ((st.borderWidth ?? 0) > 0 && st.border) parts.push(`border:${this.pt(st.borderWidth!)}px solid ${st.border}`);
    if (e.type === 'shape' && e.shape === 'ellipse') parts.push('border-radius:50%');
    else if (st.radius) parts.push(`border-radius:${this.px(st.radius)}px`);
    if ((st.opacity ?? 1) < 1) parts.push(`opacity:${st.opacity}`);
    return parts.join(';');
  }
  lineCss(e: CanvasElement): string {
    const st = e.style, w = Math.max(this.pt(st.borderWidth ?? 1), 1), c = st.border ?? st.color ?? '#0f172a';
    return e.w >= e.h ? `left:0;right:0;top:calc(50% - ${w / 2}px);border-top:${w}px solid ${c};opacity:${st.opacity ?? 1}` : `top:0;bottom:0;left:calc(50% - ${w / 2}px);border-left:${w}px solid ${c};opacity:${st.opacity ?? 1}`;
  }
  textCss(e: CanvasElement): string {
    const st = e.style;
    const font = st.font ? `'${st.font}',` : '';
    return [`font-family:${font}Lato,'Segoe UI',Arial,sans-serif`, `font-size:${this.pt(st.fontSize)}px`, `color:${st.color}`, `line-height:${st.lineHeight ?? 1.2}`,
      st.bold ? 'font-weight:700' : '', st.italic ? 'font-style:italic' : '', st.underline ? 'text-decoration:underline' : '', st.letterSpacing ? `letter-spacing:${st.letterSpacing}em` : '',
      `padding:${this.px(st.padding ?? 1)}px`, `align-items:${st.vAlign === 'middle' ? 'center' : st.vAlign === 'bottom' ? 'flex-end' : 'flex-start'}`].filter(Boolean).join(';');
  }
  cellCss(e: CanvasElement, k: string, head: boolean): string {
    const st = e.style;
    const align = k === 'item' ? 'left' : ['no', 'hs', 'qty', 'tax'].includes(k) ? 'center' : 'right';
    return [`text-align:${align}`, `padding:${this.px((st.rowPadding ?? 1.5) + (head ? 0.35 : 0))}px ${this.pt(4)}px`, 'white-space:nowrap',
      head ? `font-weight:700;color:${st.headerColor ?? st.color}${st.headerFill ? ';background:' + st.headerFill : ''}` : `border-bottom:0.5px solid ${st.lineColor ?? '#e2e8f0'}`].join(';');
  }
  assetUrl(id?: string | null): string | null { return id ? this.assetUrls()[id] ?? null : null; }

  // ---- pictures ----
  async upload(input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0]; input.value = '';
    if (file) await this.uploadPicture(file);
  }

  /**
   * Any picture the browser can open (PNG, JPEG, WebP, GIF, a phone photo…) becomes a PNG or JPEG small enough for the server:
   * kept as it is when it already is one and fits, otherwise redrawn at most maxSide pixels wide or high (PNG keeps transparency; JPEG when PNG is too big).
   */
  private async prepare(file: File, maxBytes: number, maxSide: number): Promise<Blob> {
    if (!file.type.startsWith('image/')) throw new Error('Choose a picture (PNG, JPEG, WebP…).');
    if ((file.type === 'image/png' || file.type === 'image/jpeg') && file.size <= maxBytes) return file;
    let bmp: ImageBitmap;
    try { bmp = await createImageBitmap(file); } catch { throw new Error('This picture cannot be opened. Save it as PNG or JPEG and try again.'); }
    for (const side of [maxSide, Math.round(maxSide * 0.75), Math.round(maxSide * 0.5)]) {
      const k = Math.min(1, side / Math.max(bmp.width, bmp.height));
      const c = document.createElement('canvas'); c.width = Math.max(1, Math.round(bmp.width * k)); c.height = Math.max(1, Math.round(bmp.height * k));
      c.getContext('2d')!.drawImage(bmp, 0, 0, c.width, c.height);
      const png = await new Promise<Blob | null>(r => c.toBlob(r, 'image/png'));
      if (png && png.size <= maxBytes) return png;
      for (const q of [0.9, 0.8, 0.7]) {
        const jpg = await new Promise<Blob | null>(r => c.toBlob(r, 'image/jpeg', q));
        if (jpg && jpg.size <= maxBytes) return jpg;
      }
    }
    throw new Error('This picture is too big even after shrinking it. Use a smaller one.');
  }

  private async uploadPicture(file: File, at?: { x: number; y: number }): Promise<void> {
    this.uploading.set(true);
    try {
      const blob = await this.prepare(file, 1_900_000, 2400);
      const name = file.name.replace(/\.[^.]+$/, '') + (blob.type === 'image/jpeg' ? '.jpg' : '.png');
      const fd = new FormData(); fd.append('file', blob, name);
      const a = await this.api.post<TemplateAsset>('/api/v1/templates/assets', fd);
      this.assets.update(x => [a, ...x]);
      await this.loadAsset(a.id);
      this.pickAsset(a, at);
    } catch (e) { this.toast.err(e); } finally { this.uploading.set(false); }
  }

  async uploadLogo(input: HTMLInputElement): Promise<void> {
    const file = input.files?.[0]; input.value = '';
    const t = this.tpl();
    if (!file || !t) return;
    this.uploading.set(true);
    try {
      const blob = await this.prepare(file, 290_000, 1200);
      const fd = new FormData(); fd.append('file', blob, blob.type === 'image/jpeg' ? 'logo.jpg' : 'logo.png');
      await this.api.post(`/api/v1/templates/${t.id}/logo`, fd);
      const old = this.logoUrl(); if (old) URL.revokeObjectURL(old);
      this.logoUrl.set(URL.createObjectURL(await this.api.blob(`/api/v1/templates/${t.id}/logo`)));
      this.tpl.set({ ...t, hasLogo: true });
      // a fresh logo goes on the page if it is not there yet
      if (!this.layout().elements.some(e => e.type === 'logo')) this.add('logo');
      this.toast.ok('Logo uploaded.');
    } catch (e) { this.toast.err(e); } finally { this.uploading.set(false); }
  }

  async removeLogo(): Promise<void> {
    const t = this.tpl();
    if (!t || !(await this.dialog.confirm('Remove the logo from this template? Invoices made with it will show no logo.', 'Remove', true))) return;
    try {
      await this.api.delete(`/api/v1/templates/${t.id}/logo`);
      const old = this.logoUrl(); if (old) URL.revokeObjectURL(old);
      this.logoUrl.set(null); this.tpl.set({ ...t, hasLogo: false });
      this.toast.ok('Logo removed.');
    } catch (e) { this.toast.err(e); }
  }

  onDragOver(ev: DragEvent): void {
    if (!ev.dataTransfer || ![...ev.dataTransfer.items].some(i => i.kind === 'file')) return;
    ev.preventDefault(); ev.dataTransfer.dropEffect = 'copy'; this.dropping.set(true);
  }

  /** A picture dropped on the page is uploaded and placed where it was dropped. */
  async onDrop(ev: DragEvent): Promise<void> {
    this.dropping.set(false);
    const file = ev.dataTransfer?.files?.[0];
    if (!file) return;
    ev.preventDefault();
    const box = this.pageRef()?.nativeElement.getBoundingClientRect();
    const at = box ? { x: (ev.clientX - box.left) / this.px(1), y: (ev.clientY - box.top) / this.px(1) } : undefined;
    await this.uploadPicture(file, at);
  }
  pickAsset(a: TemplateAsset, at?: { x: number; y: number }): void {
    this.imagePicker.set(false);
    const e = this.single();
    if (!at && e?.type === 'image') { this.patch({ assetId: a.id }); return; }
    // place it with its own proportions: where it was dropped, or in the middle
    const img = new Image();
    img.onload = () => {
      const w = Math.min(80, PAGE_W), h = Math.min(PAGE_H, w * (img.naturalHeight / Math.max(img.naturalWidth, 1)));
      const x = at ? Math.max(0, Math.min(PAGE_W - w, at.x - w / 2)) : (PAGE_W - w) / 2;
      const y = at ? Math.max(0, Math.min(PAGE_H - h, at.y - h / 2)) : 60;
      this.add('image', { assetId: a.id, w, h, x, y });
    };
    img.onerror = () => this.add('image', { assetId: a.id });
    img.src = this.assetUrl(a.id) ?? '';
  }
  async deleteAsset(a: TemplateAsset): Promise<void> {
    if (this.layout().elements.some(e => e.assetId === a.id)) { this.toast.err(new Error('This picture is on the page. Remove it from the page first.')); return; }
    if (!(await this.dialog.confirm('Delete this picture?', 'Delete', true))) return;
    try { await this.api.delete(`/api/v1/templates/assets/${a.id}`); this.assets.update(x => x.filter(y => y.id !== a.id)); } catch (e) { this.toast.err(e); }
  }

  // ---- saving, preview, files ----
  private settings(): Record<string, unknown> {
    const t = this.tpl()!;
    const canvas = this.layout().elements.length ? this.layout() : null;
    return { ...t.settings, canvas };
  }
  async save(): Promise<void> {
    const t = this.tpl(); if (!t) return;
    if (this.bareHeadings().length) { this.selected.set(new Set([this.bareHeadings()[0].id])); this.toast.err(new Error('A text box says just "INVOICE" as a heading. A fiscal invoice must say FISCAL TAX INVOICE: use the Document title value instead.')); return; }
    if (this.layout().elements.length && this.missing().length) { this.toast.err(new Error(`Add ${this.missing().map(m => this.typeLabel(m)).join(', ')} first: ZIMRA requires them.`)); return; }
    this.busy.set(true);
    try {
      const saved = await this.api.put<InvoiceTemplate>('/api/v1/templates', { id: t.id, branchId: t.branchId ?? null, name: t.name, settings: this.settings() });
      this.tpl.set(saved); this.dirty.set(false);
      this.toast.ok(this.layout().elements.length ? 'Saved. Invoices now print with this design.' : 'Saved. Invoices print with the standard layout.');
    } catch (e) { this.toast.err(e); } finally { this.busy.set(false); }
  }
  async preview(): Promise<void> {
    const t = this.tpl(); if (!t) return;
    this.busy.set(true);
    try {
      const blob = await this.api.postBlob('/api/v1/templates/preview', { settings: this.settings(), format: 'a4', templateId: t.id, large: true });
      this.closePreview(); this.previewUrl.set(URL.createObjectURL(blob));
    } catch (e) { this.toast.err(await this.blobError(e)); } finally { this.busy.set(false); }
  }
  closePreview(): void { const u = this.previewUrl(); if (u) URL.revokeObjectURL(u); this.previewUrl.set(null); }
  async render(format: 'pdf' | 'docx' | 'json'): Promise<void> {
    this.exportOpen.set(false);
    const t = this.tpl(); if (!t) return;
    try {
      const blob = await this.api.postBlob('/api/v1/templates/render', { settings: this.settings(), format, templateId: t.id });
      this.saveFile(blob, `sample-invoice.${format}`);
    } catch (e) { this.toast.err(await this.blobError(e)); }
  }
  async exportTemplate(): Promise<void> {
    this.exportOpen.set(false);
    const t = this.tpl(); if (!t) return;
    if (this.dirty()) { this.toast.err(new Error('Save first: the file holds the saved template.')); return; }
    try { this.saveFile(await this.api.blob(`/api/v1/templates/${t.id}/export`), `${t.name.replace(/[^\w-]+/g, '-').toLowerCase() || 'invoice-template'}.json`); } catch (e) { this.toast.err(e); }
  }
  async importTemplate(input: HTMLInputElement): Promise<void> {
    this.exportOpen.set(false);
    const file = input.files?.[0]; input.value = '';
    const t = this.tpl(); if (!file || !t) return;
    if (!(await this.dialog.confirm(`Load “${file.name}” into this template? Its layout, logo and pictures replace what is here.`, 'Load it'))) return;
    try {
      const body = JSON.parse(await file.text());
      const saved = await this.api.post<InvoiceTemplate>(`/api/v1/templates/${t.id}/import`, body);
      this.tpl.set(saved);
      const canvas = (saved.settings as unknown as { canvas?: CanvasLayout }).canvas;
      this.commit();
      this.layout.set(canvas?.elements?.length ? structuredClone(canvas) : { background: '#ffffff', elements: [] });
      this.dirty.set(false);
      this.assets.set(await this.api.get<TemplateAsset[]>('/api/v1/templates/assets'));
      for (const a of this.assets()) void this.loadAsset(a.id);
      if (saved.hasLogo) { try { this.logoUrl.set(URL.createObjectURL(await this.api.blob(`/api/v1/templates/${t.id}/logo`))); } catch { /* ignore */ } }
      this.toast.ok('Template loaded.');
    } catch (e) { this.toast.err(e instanceof SyntaxError ? new Error('That file is not a template file.') : e); }
  }
  private saveFile(blob: Blob, name: string): void {
    const href = URL.createObjectURL(blob);
    const a = document.createElement('a'); a.href = href; a.download = name; document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(href), 10_000);
  }
  /** an error answer to a file request arrives as a Blob: read the message out of it */
  private async blobError(e: unknown): Promise<unknown> {
    const err = (e as { error?: unknown })?.error;
    if (err instanceof Blob) { try { const j = JSON.parse(await err.text()); return new Error(j.title ?? errorMessage(e)); } catch { /* fall through */ } }
    return e;
  }

  // ---- keyboard ----
  @HostListener('document:keydown', ['$event'])
  onKey(ev: KeyboardEvent): void {
    const tag = (ev.target as HTMLElement)?.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
    const ctrl = ev.ctrlKey || ev.metaKey;
    if (ctrl && ev.key.toLowerCase() === 's') { ev.preventDefault(); void this.save(); return; }
    if (ctrl && ev.key.toLowerCase() === 'z') { ev.preventDefault(); ev.shiftKey ? this.redo() : this.undo(); return; }
    if (ctrl && ev.key.toLowerCase() === 'y') { ev.preventDefault(); this.redo(); return; }
    if (!this.selected().size) return;
    if (ctrl && ev.key.toLowerCase() === 'd') { ev.preventDefault(); this.duplicate(); return; }
    if (ctrl && ev.key.toLowerCase() === 'c') { this.clipboard = this.layout().elements.filter(e => this.isSel(e.id)).map(e => structuredClone(e)); return; }
    if (ctrl && ev.key.toLowerCase() === 'v' && this.clipboard.length) {
      const copies = this.clipboard.map(e => ({ ...structuredClone(e), id: newId(), x: clamp(e.x + 5, 0, PAGE_W - e.w), y: clamp(e.y + 5, 0, PAGE_H - e.h) }));
      this.mutate(els => [...els, ...copies]); this.selected.set(new Set(copies.map(c => c.id))); return;
    }
    if (ev.key === 'Delete' || ev.key === 'Backspace') { ev.preventDefault(); this.remove(); return; }
    if (ev.key === 'Escape') { this.selected.set(new Set()); return; }
    const step = ev.shiftKey ? 5 : 1;
    const d = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[ev.key];
    if (d) { ev.preventDefault(); this.updateSelected(e => e.locked ? e : { ...e, x: clamp(e.x + d[0], 0, PAGE_W - e.w), y: clamp(e.y + d[1], 0, PAGE_H - e.h) }); }
  }
}

function clamp(v: number, min: number, max: number): number { return Math.min(Math.max(v, min), Math.max(min, max)); }
