import { Injectable, signal } from '@angular/core';
import { errorMessage } from './api.service';

export interface Toast { id: number; text: string; kind: 'ok' | 'err'; }

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<Toast[]>([]);
  private n = 0;

  private push(text: string, kind: Toast['kind'], ms: number): void {
    const id = ++this.n;
    this.toasts.update(t => [...t, { id, text, kind }]);
    setTimeout(() => this.dismiss(id), ms);
  }
  ok(text: string): void { this.push(text, 'ok', 3500); }
  err(e: unknown): void { this.push(errorMessage(e), 'err', 8000); }
  dismiss(id: number): void { this.toasts.update(t => t.filter(x => x.id !== id)); }
}

export interface DialogState { message: string; confirmLabel: string; danger: boolean; ask: boolean; placeholder?: string; resolve: (value: string | boolean | null) => void; }

/** Confirm / prompt dialogs rendered once by the shell. */
@Injectable({ providedIn: 'root' })
export class DialogService {
  readonly state = signal<DialogState | null>(null);

  confirm(message: string, confirmLabel = 'Confirm', danger = false): Promise<boolean> {
    return new Promise(resolve => this.state.set({ message, confirmLabel, danger, ask: false, resolve: v => resolve(v === true) }));
  }
  /** Returns the entered text (possibly empty) or null when cancelled. */
  prompt(message: string, placeholder = '', confirmLabel = 'OK'): Promise<string | null> {
    return new Promise(resolve => this.state.set({ message, confirmLabel, danger: false, ask: true, placeholder, resolve: v => resolve(typeof v === 'string' ? v : null) }));
  }
  close(): void { this.state.set(null); }
}

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly dark = signal(false);

  constructor() {
    let stored: string | null = null;
    try { stored = localStorage.getItem('fdms.theme'); } catch { /* ignore */ }
    this.set(stored ? stored === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches);
  }

  set(dark: boolean): void {
    this.dark.set(dark);
    document.documentElement.classList.toggle('dark', dark);
    try { localStorage.setItem('fdms.theme', dark ? 'dark' : 'light'); } catch { /* ignore */ }
  }
  toggle(): void { this.set(!this.dark()); }
}
