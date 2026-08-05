import { TestBed } from '@angular/core/testing';

import { AuthStateService } from './auth-state.service';

describe('AuthStateService', () => {
  let service: AuthStateService;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({});
    service = TestBed.inject(AuthStateService);
  });

  it('starts unauthenticated with no access token and no refresh token', () => {
    expect(service.isAuthenticated()).toBe(false);
    expect(service.accessToken()).toBeNull();
    expect(service.refreshToken).toBeNull();
  });

  it('setTokens stores the access token in memory and the refresh token in sessionStorage, never localStorage', () => {
    service.setTokens({ accessToken: 'access-1', refreshToken: 'refresh-1' });

    expect(service.isAuthenticated()).toBe(true);
    expect(service.accessToken()).toBe('access-1');
    expect(sessionStorage.getItem('ihostpro.refreshToken')).toBe('refresh-1');
    expect(localStorage.getItem('ihostpro.refreshToken')).toBeNull();
  });

  it('setTokens called again (rotation) immediately replaces the previous refresh token', () => {
    service.setTokens({ accessToken: 'access-1', refreshToken: 'refresh-1' });
    service.setTokens({ accessToken: 'access-2', refreshToken: 'refresh-2' });

    expect(service.accessToken()).toBe('access-2');
    expect(service.refreshToken).toBe('refresh-2');
  });

  it('clear removes both the in-memory access token and the sessionStorage refresh token', () => {
    service.setTokens({ accessToken: 'access-1', refreshToken: 'refresh-1' });

    service.clear();

    expect(service.isAuthenticated()).toBe(false);
    expect(service.accessToken()).toBeNull();
    expect(service.refreshToken).toBeNull();
  });
});
