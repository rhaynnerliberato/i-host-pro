import { Injectable, inject } from '@angular/core';
import { Observable, catchError, finalize, map, of, shareReplay, switchMap, tap, throwError } from 'rxjs';

import { AuthTokensResponse, Client } from '../api/generated/api-client';
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
