import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthStateService } from './auth-state.service';
import { UserProfileService } from './user-profile.service';

/**
 * Requires authentication and, if the route declares
 * `data: { permissions: [...] }`, that the current user holds at least one
 * of those real permission codes — per `OwnProfileResponse.permissions`
 * (GET /api/v1/users/me, resolved server-side), never a role name, never a
 * decoded JWT (Fase 4, Incremento 2 — role-based route data was replaced by
 * permission-based data specifically so authorization decisions here can
 * never drift from what the backend's own policies enforce). Routes with no
 * `permissions` data behave like authGuard.
 *
 * Fail-closed: if the profile has not loaded yet (e.g. this guard runs
 * before `restoreSession()`/`load()` resolves) `userProfile.permissions()`
 * is empty, so a permission-gated route denies access rather than granting
 * it — never "assume authorized while the profile is still loading".
 */
export const permissionGuard: CanActivateFn = (route, state) => {
  const authState = inject(AuthStateService);
  const userProfile = inject(UserProfileService);
  const router = inject(Router);

  if (!authState.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { redirectTo: state.url } });
  }

  const requiredPermissions = (route.data['permissions'] as string[] | undefined) ?? [];
  const hasAccess = requiredPermissions.length === 0 || requiredPermissions.some((code) => userProfile.hasPermission(code));

  return hasAccess ? true : router.createUrlTree(['/forbidden']);
};
