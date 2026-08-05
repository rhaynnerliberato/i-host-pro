import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { Injector, inject, runInInjectionContext } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';

import { AuthService } from './auth.service';
import { AuthStateService } from './auth-state.service';

/** Set on a request once it has already been retried once after a refresh — prevents a second retry. */
const RETRIED = new HttpContextToken<boolean>(() => false);

const AUTH_ENDPOINT_PATTERN = /\/api\/v1\/auth\/(login|refresh|logout)(\?|$)/;

function withAccessToken(req: HttpRequest<unknown>, accessToken: string | null): HttpRequest<unknown> {
  return accessToken ? req.clone({ setHeaders: { Authorization: `Bearer ${accessToken}` } }) : req;
}

/**
 * Attaches the access token to every request and, on a 401, refreshes it
 * once (single-flight, shared across concurrent 401s) and retries the
 * original request exactly once. Never attempts a refresh for login/refresh/
 * logout themselves, for an already-retried request, or for a 403 — per the
 * approved token strategy.
 *
 * AuthService (and, through it, the NSwag-generated Client) is only injected
 * lazily inside the 401 branch, never unconditionally at the top — Client's
 * baseUrl comes from RuntimeConfigService, and RuntimeConfigService's own
 * config.json fetch is itself the very first request this interceptor sees.
 * Injecting AuthService eagerly for every request would construct Client
 * (and read RuntimeConfigService.config) while that first load is still in
 * flight.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const authState = inject(AuthStateService);
  const injector = inject(Injector);

  const authorizedReq = withAccessToken(req, authState.accessToken());

  return next(authorizedReq).pipe(
    catchError((error: unknown) => {
      const shouldRefreshAndRetry =
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        !AUTH_ENDPOINT_PATTERN.test(req.url) &&
        !req.context.get(RETRIED);

      if (!shouldRefreshAndRetry) {
        return throwError(() => error);
      }

      const authService = runInInjectionContext(injector, () => inject(AuthService));

      return authService.refreshAccessToken().pipe(
        switchMap((tokens) => {
          const retryReq = withAccessToken(req, tokens.accessToken ?? null).clone({
            context: req.context.set(RETRIED, true),
          });
          return next(retryReq);
        }),
        catchError((refreshError: unknown) => {
          authService.clearLocalSession();
          return throwError(() => refreshError);
        }),
      );
    }),
  );
};
