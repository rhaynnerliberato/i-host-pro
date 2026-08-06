import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';

import { AuthStateService } from './auth-state.service';
import { permissionGuard } from './permission.guard';
import { UserProfileService } from './user-profile.service';

function configure(permissions: string[] = []): AuthStateService {
  sessionStorage.clear();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: UserProfileService, useValue: { hasPermission: (code: string) => permissions.includes(code) } },
    ],
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
    configure(['USERS:MANAGE']);

    const result = run({ permissions: ['USERS:MANAGE'] }) as UrlTree;

    expect(result).toBeInstanceOf(UrlTree);
    expect(result.queryParams['redirectTo']).toBe('/protected');
  });

  it('allows navigation when authenticated and the route requires no specific permission', () => {
    const authState = configure();
    authState.setTokens({ accessToken: 'a', refreshToken: 'r' });

    expect(run({})).toBe(true);
  });

  it('allows navigation when authenticated and the user holds one of the required permissions', () => {
    const authState = configure(['SOME:OTHER', 'USERS:MANAGE']);
    authState.setTokens({ accessToken: 'a', refreshToken: 'r' });

    expect(run({ permissions: ['USERS:MANAGE'] })).toBe(true);
  });

  it('redirects to /forbidden when authenticated but lacking every required permission', () => {
    const authState = configure(['SOME:OTHER']);
    authState.setTokens({ accessToken: 'a', refreshToken: 'r' });

    const result = run({ permissions: ['USERS:MANAGE'] }) as UrlTree;

    expect(result.toString()).toBe('/forbidden');
  });

  it('denies access (fails closed) when the user has no permissions at all, even for a permission-gated route', () => {
    const authState = configure([]);
    authState.setTokens({ accessToken: 'a', refreshToken: 'r' });

    const result = run({ permissions: ['USERS:MANAGE'] }) as UrlTree;

    expect(result.toString()).toBe('/forbidden');
  });
});
