import { Injectable, computed, signal } from '@angular/core';

import type { AuthTokensResponse } from '../api/generated/api-client';

const REFRESH_TOKEN_STORAGE_KEY = 'ihostpro.refreshToken';

/**
 * Holds the access token in memory only (never persisted) and the refresh
 * token in sessionStorage (never localStorage) — per the approved token
 * strategy. Nothing outside this service reads/writes either storage
 * location directly.
 */
@Injectable({ providedIn: 'root' })
export class AuthStateService {
  private readonly accessTokenSignal = signal<string | null>(null);

  readonly accessToken = this.accessTokenSignal.asReadonly();
  readonly isAuthenticated = computed(() => this.accessTokenSignal() !== null);

  get refreshToken(): string | null {
    return sessionStorage.getItem(REFRESH_TOKEN_STORAGE_KEY);
  }

  /** Called after every successful login/refresh — always overwrites the refresh token with the newly rotated one. */
  setTokens(tokens: AuthTokensResponse): void {
    this.accessTokenSignal.set(tokens.accessToken ?? null);

    if (tokens.refreshToken) {
      sessionStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, tokens.refreshToken);
    }
  }

  clear(): void {
    this.accessTokenSignal.set(null);
    sessionStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY);
  }
}
