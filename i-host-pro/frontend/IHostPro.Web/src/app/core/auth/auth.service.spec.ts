import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { AuthTokensResponse, Client, OwnProfileResponse, SignupResponse } from '../api/generated/api-client';
import { AuthService } from './auth.service';
import { AuthStateService } from './auth-state.service';
import { UserProfileService } from './user-profile.service';

function tokens(overrides: Partial<AuthTokensResponse> = {}): AuthTokensResponse {
  return { accessToken: 'access-1', refreshToken: 'refresh-1', ...overrides };
}

function profile(): OwnProfileResponse {
  return { id: 'user-1', permissions: ['USERS:MANAGE'] };
}

function signupResponse(overrides: Partial<SignupResponse> = {}): SignupResponse {
  return { tenantSlug: 'acme', tokens: tokens(), ...overrides };
}

describe('AuthService', () => {
  let service: AuthService;
  let client: {
    login: ReturnType<typeof vi.fn>;
    logout: ReturnType<typeof vi.fn>;
    refresh: ReturnType<typeof vi.fn>;
    signup: ReturnType<typeof vi.fn>;
    start2: ReturnType<typeof vi.fn>;
    complete: ReturnType<typeof vi.fn>;
  };
  let authState: AuthStateService;
  let userProfile: { load: ReturnType<typeof vi.fn>; clear: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    sessionStorage.clear();
    client = { login: vi.fn(), logout: vi.fn(), refresh: vi.fn(), signup: vi.fn(), start2: vi.fn(), complete: vi.fn() };
    userProfile = { load: vi.fn(), clear: vi.fn() };

    TestBed.configureTestingModule({
      providers: [
        { provide: Client, useValue: client },
        { provide: UserProfileService, useValue: userProfile },
      ],
    });

    service = TestBed.inject(AuthService);
    authState = TestBed.inject(AuthStateService);
  });

  it('login stores the tokens and then loads the real permissions profile (never decodes the JWT)', () => {
    client.login.mockReturnValue(of(tokens()));
    userProfile.load.mockReturnValue(of(profile()));

    service.login('tenant', 'a@b.com', 'password').subscribe();

    expect(authState.isAuthenticated()).toBe(true);
    expect(userProfile.load).toHaveBeenCalledTimes(1);
  });

  it('signup stores the returned tokens and then loads the real permissions profile', () => {
    client.signup.mockReturnValue(of(signupResponse()));
    userProfile.load.mockReturnValue(of(profile()));

    let result: SignupResponse | undefined;
    service.signup('Acme Inc', 'Jane Admin', 'jane@acme.com', 'Sup3r$ecret').subscribe((value) => (result = value));

    expect(authState.isAuthenticated()).toBe(true);
    expect(userProfile.load).toHaveBeenCalledTimes(1);
    expect(result?.tenantSlug).toBe('acme');
  });

  it('requestPasswordReset delegates to Client.start2 without touching local session state', () => {
    client.start2.mockReturnValue(of(undefined));

    service.requestPasswordReset('acme', 'jane@acme.com').subscribe();

    expect(client.start2).toHaveBeenCalledWith({ tenantSlug: 'acme', email: 'jane@acme.com' });
    expect(authState.isAuthenticated()).toBe(false);
  });

  it('completePasswordReset delegates to Client.complete without auto-login', () => {
    client.complete.mockReturnValue(of(undefined));

    service.completePasswordReset('raw-token', 'N3wPassw0rd!').subscribe();

    expect(client.complete).toHaveBeenCalledWith({ token: 'raw-token', newPassword: 'N3wPassw0rd!' });
    expect(authState.isAuthenticated()).toBe(false);
  });

  it('logout clears the local session, including the cached permissions, even when the backend call succeeds', () => {
    authState.setTokens(tokens());
    client.logout.mockReturnValue(of(undefined));

    service.logout().subscribe();

    expect(authState.isAuthenticated()).toBe(false);
    expect(userProfile.clear).toHaveBeenCalledTimes(1);
  });

  it('logout clears the local session and cached permissions even when the backend call fails', () => {
    authState.setTokens(tokens());
    client.logout.mockReturnValue(throwError(() => new Error('network error')));

    service.logout().subscribe();

    expect(authState.isAuthenticated()).toBe(false);
    expect(userProfile.clear).toHaveBeenCalledTimes(1);
  });

  it('restoreSession with no stored refresh token resolves false and never loads a profile', () => {
    let result: boolean | undefined;
    service.restoreSession().subscribe((value) => (result = value));

    expect(result).toBe(false);
    expect(userProfile.load).not.toHaveBeenCalled();
  });

  it('restoreSession with a stored refresh token refreshes the access token and reloads the real permissions profile', () => {
    authState.setTokens(tokens());
    client.refresh.mockReturnValue(of(tokens({ accessToken: 'access-2', refreshToken: 'refresh-2' })));
    userProfile.load.mockReturnValue(of(profile()));

    let result: boolean | undefined;
    service.restoreSession().subscribe((value) => (result = value));

    expect(result).toBe(true);
    expect(userProfile.load).toHaveBeenCalledTimes(1);
    expect(authState.accessToken()).toBe('access-2');
  });

  it('restoreSession clears the local session and cached permissions when the refresh call fails', () => {
    authState.setTokens(tokens());
    client.refresh.mockReturnValue(throwError(() => new Error('refresh rejected')));

    let result: boolean | undefined;
    service.restoreSession().subscribe((value) => (result = value));

    expect(result).toBe(false);
    expect(authState.isAuthenticated()).toBe(false);
    expect(userProfile.clear).toHaveBeenCalledTimes(1);
  });
});
