import { Injectable, inject } from '@angular/core';
import { Observable, catchError, finalize, map, of, shareReplay, switchMap, tap, throwError } from 'rxjs';

import { AuthTokensResponse, Client, SignupResponse } from '../api/generated/api-client';
import { AuthStateService } from './auth-state.service';
import { UserProfileService } from './user-profile.service';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly client = inject(Client);
  private readonly authState = inject(AuthStateService);
  private readonly userProfile = inject(UserProfileService);

  private refreshInFlight$: Observable<AuthTokensResponse> | null = null;

  /** Loads the real user profile (roles included) as part of login — authorization never relies on a decoded JWT. */
  login(tenantSlug: string, email: string, password: string): Observable<AuthTokensResponse> {
    return this.client.login({ tenantSlug, email, password }).pipe(
      tap((tokens) => this.authState.setTokens(tokens)),
      switchMap((tokens) => this.userProfile.load().pipe(map(() => tokens))),
    );
  }

  /** Self-service signup: creates a brand-new tenant/admin and logs them in immediately, same profile-loading rationale as login. */
  signup(companyName: string, adminFullName: string, adminEmail: string, password: string): Observable<SignupResponse> {
    return this.client.signup({ companyName, adminFullName, adminEmail, password }).pipe(
      tap((response) => {
        if (response.tokens) {
          this.authState.setTokens(response.tokens);
        }
      }),
      switchMap((response) => this.userProfile.load().pipe(map(() => response))),
    );
  }

  /** Always resolves the same way regardless of whether the tenant/email exists — the backend never signals that distinction to the caller. */
  requestPasswordReset(tenantSlug: string, email: string): Observable<void> {
    return this.client.start2({ tenantSlug, email });
  }

  /** No tokens are returned on success — the backend never auto-logs-in after a reset; the caller must sign in again with the new password. */
  completePasswordReset(token: string, newPassword: string): Observable<void> {
    return this.client.complete({ token, newPassword });
  }

  /** Always resolves and always clears local state, even when the backend call fails. */
  logout(): Observable<void> {
    return this.client.logout().pipe(
      catchError(() => of(undefined)),
      tap(() => this.clearLocalSession()),
    );
  }

  /** Called once at bootstrap to restore a session from the refresh token in sessionStorage, if any. */
  restoreSession(): Observable<boolean> {
    if (!this.authState.refreshToken) {
      return of(false);
    }

    return this.refreshAccessToken().pipe(
      switchMap(() => this.userProfile.load()),
      map(() => true),
      catchError(() => {
        this.clearLocalSession();
        return of(false);
      }),
    );
  }

  /** Single-flight: concurrent callers while a refresh is already in progress share the same in-flight request. */
  refreshAccessToken(): Observable<AuthTokensResponse> {
    if (this.refreshInFlight$) {
      return this.refreshInFlight$;
    }

    const refreshToken = this.authState.refreshToken;
    if (!refreshToken) {
      return throwError(() => new Error('No refresh token available.'));
    }

    const request$ = this.client.refresh({ refreshToken }).pipe(
      tap((tokens) => this.authState.setTokens(tokens)),
      finalize(() => (this.refreshInFlight$ = null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    this.refreshInFlight$ = request$;
    return request$;
  }

  clearLocalSession(): void {
    this.authState.clear();
    this.userProfile.clear();
  }
}
