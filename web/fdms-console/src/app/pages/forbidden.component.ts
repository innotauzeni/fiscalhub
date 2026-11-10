import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'app-forbidden',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="mx-auto mt-16 max-w-md text-center">
      <div class="text-5xl font-bold text-slate-300 dark:text-slate-700">403</div>
      <h1 class="mt-2 text-lg font-semibold">You don't have access to that page</h1>
      <p class="mt-1 text-sm text-slate-500">Your role does not include it. If you need it, ask your administrator to add it to your role.</p>
      @if (auth.menus().length) { <a class="btn mt-5" [routerLink]="auth.homeUrl()">Go to my home page</a> }
    </div>
  `,
})
export class ForbiddenComponent { readonly auth = inject(AuthService); }
