import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';

import { AuthStateService } from './auth-state.service';
import { permissionGuard } from './permission.guard';
import { UserProfileService } from './user-profile.service';

function configure(roles: string[] = []): AuthStateService {
  sessionStorage.clear();
  TestBed.configureTestingModule({
    providers: [provideRouter([]), { provide: UserProfileService, useValue: { roles: () => roles } }],
  });
  return TestBed.inject(AuthStateService);
}

function run(routeData: Record<string, unknown>, url = '/protected') {
  const route = { data: routeData } as unknown as ActivatedRouteSnapshot;
  const state = { url } as RouterStateSnapshot;
  return TestBed.runInInjectionContext(() => permissionGuard(route, state));
}

describe('permissionGuard', () => {
  it('redirects to /login with the attempted URL preserved when not authenticated', () => {
    configure(['ADMIN']);

    const result = run({ roles: ['ADMIN'] }) as UrlTree;

    expect(result).toBeInstanceOf(UrlTree);
    expect(result.queryParams['redirectTo']).toBe('/protected');
  });

  it('allows navigation when authenticated and the route requires no specific role', () => {
    const authState = configure();
    authState.setTokens({ accessToken: 'a', refreshToken: 'r' });

    expect(run({})).toBe(true);
  });

  it('allows navigation when authenticated and the user has one of the required roles', () => {
    const authState = configure(['OPERATOR', 'ADMIN']);
    authState.setTokens({ accessToken: 'a', refreshToken: 'r' });

    expect(run({ roles: ['ADMIN'] })).toBe(true);
  });

  it('redirects to /forbidden when authenticated but lacking every required role', () => {
    const authState = configure(['OPERATOR']);
    authState.setTokens({ accessToken: 'a', refreshToken: 'r' });

    const result = run({ roles: ['ADMIN'] }) as UrlTree;

    expect(result.toString()).toBe('/forbidden');
  });
});
