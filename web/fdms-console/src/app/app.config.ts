import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './core/auth.interceptor';
import { apiBaseInterceptor } from './core/env';
import { AuthService } from './core/auth.service';
import { PwaService } from './core/pwa.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding(), withInMemoryScrolling({ anchorScrolling: 'enabled', scrollPositionRestoration: 'enabled' })),
    provideHttpClient(withInterceptors([authInterceptor, apiBaseInterceptor])),
    // turn a stored refresh token back into a session before the first route is resolved
    provideAppInitializer(() => inject(AuthService).restore()),
    // installable app: service worker, install prompt, update notice (does nothing while developing)
    provideAppInitializer(() => { inject(PwaService).register(); }),
  ],
};
