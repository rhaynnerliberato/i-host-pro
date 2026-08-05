import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';

import { AuthStateService } from './auth-state.service';
import { authGuard } from './auth.guard';

function run(url: string) {
  const state = { url } as RouterStateSnapshot;
  return TestBed.runInInjectionContext(() => authGuard({} as ActivatedRouteSnapshot, state));
}

describe('authGuard', () => {
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
  });

  it('allows navigation when authenticated', () => {
    TestBed.inject(AuthStateService).setTokens({ accessToken: 'a', refreshToken: 'r' });

    expect(run('/reservations')).toBe(true);
  });

  it('redirects to /login with the attempted URL preserved when not authenticated', () => {
    const result = run('/reservations') as UrlTree;

    expect(result).toBeInstanceOf(UrlTree);
    expect(result.queryParams['redirectTo']).toBe('/reservations');
  });
});
