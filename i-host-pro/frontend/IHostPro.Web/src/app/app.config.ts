import {
  ApplicationConfig,
  Injector,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  inject,
  isDevMode,
  runInInjectionContext,
} from '@angular/core';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TranslocoHttpLoader } from './transloco-loader';
import { provideTransloco } from '@jsverse/transloco';
import { RuntimeConfigService } from './core/config/runtime-config.service';
import { API_BASE_URL } from './core/api/generated/api-client';
import { authInterceptor } from './core/auth/auth.interceptor';
import { AuthService } from './core/auth/auth.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    // Loads and validates public/config.json, then tries to restore a
    // session from the refresh token in sessionStorage — both before the
    // rest of the app bootstraps. provideAppInitializer is the current,
    // non-deprecated functional replacement for the old class-based
    // APP_INITIALIZER token. Sequenced explicitly (not two separate
    // provideAppInitializer calls) because restoreSession's Client depends
    // on API_BASE_URL, which depends on RuntimeConfigService already having
    // loaded — multiple app initializers otherwise run in parallel.
    //
    // inject() only works synchronously, so RuntimeConfigService and the
    // Injector are captured up front; AuthService is deliberately injected
    // AFTER the await (via runInInjectionContext) rather than alongside them
    // — injecting it eagerly would construct the NSwag-generated Client,
    // which reads RuntimeConfigService.config, before load() has resolved.
    provideAppInitializer(() => {
      const runtimeConfig = inject(RuntimeConfigService);
      const injector = inject(Injector);

      return (async () => {
        await runtimeConfig.load();
        const authService = runInInjectionContext(injector, () => inject(AuthService));
        await firstValueFrom(authService.restoreSession());
      })();
    }),
    // The NSwag-generated Client resolves its baseUrl from this token. Safe
    // to read RuntimeConfigService.config eagerly here because this factory
    // only runs the first time something injects the Client, which never
    // happens before the app initializer above has completed.
    { provide: API_BASE_URL, useFactory: () => inject(RuntimeConfigService).config.apiBaseUrl },
    provideTransloco({
      config: {
        availableLangs: ['pt-BR', 'en'],
        defaultLang: 'pt-BR',
        // Remove this option if your application doesn't support changing language in runtime.
        reRenderOnLangChange: true,
        prodMode: !isDevMode(),
      },
      loader: TranslocoHttpLoader,
    }),
  ],
};
