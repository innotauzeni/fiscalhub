import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { errorMessage } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { ToastService } from '../core/ui.services';

@Component({
  selector: 'app-change-password',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  template: `
    <div class="flex min-h-screen items-center justify-center bg-slate-50 p-4 dark:bg-slate-950">
      <form class="card w-full max-w-sm p-7" (ngSubmit)="submit()">
        <h1 class="text-lg font-semibold">{{ forced() ? 'Choose a new password' : 'Change password' }}</h1>
        @if (forced()) { <p class="mt-1 text-sm text-slate-500">Your administrator set a temporary password. Pick your own to continue.</p> }

        <label class="label mt-5" for="cur">Current password</label>
        <input id="cur" name="cur" type="password" class="input" [(ngModel)]="current" autocomplete="current-password" required />
        <label class="label mt-4" for="new">New password</label>
        <input id="new" name="new" type="password" class="input" [(ngModel)]="next" autocomplete="new-password" required />
        <p class="hint">At least 10 characters with an upper-case letter, a lower-case letter and a digit.</p>
        <label class="label mt-4" for="rep">Repeat new password</label>
        <input id="rep" name="rep" type="password" class="input" [(ngModel)]="repeat" autocomplete="new-password" required />

        @if (error()) { <p class="mt-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-900/30 dark:text-red-300" role="alert">{{ error() }}</p> }
        <div class="mt-5 flex gap-2">
          <button class="btn flex-1" type="submit" [disabled]="busy() || !current || !next || next !== repeat">Save password</button>
          @if (!forced()) { <button type="button" class="btn-ghost" (click)="back()">Cancel</button> }
        </div>
        @if (repeat && next !== repeat) { <p class="hint text-red-600">The two new passwords do not match.</p> }
      </form>
    </div>
  `,
})
export class ChangePasswordComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly forced = this.auth.mustChangePassword;
  current = ''; next = ''; repeat = '';
  readonly busy = signal(false);
  readonly error = signal('');

  async submit(): Promise<void> {
    this.busy.set(true); this.error.set('');
    try {
      await this.auth.changePassword(this.current, this.next);
      this.toast.ok('Password changed.');
      await this.router.navigateByUrl(this.auth.homeUrl());
    } catch (e) { this.error.set(errorMessage(e).replace(/^\[[A-Z_]+\]\s*/, '')); }
    finally { this.busy.set(false); }
  }

  back(): void { void this.router.navigateByUrl(this.auth.homeUrl()); }
}
