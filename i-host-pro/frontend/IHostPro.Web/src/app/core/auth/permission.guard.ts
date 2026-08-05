import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthStateService } from './auth-state.service';
import { UserProfileService } from './user-profile.service';

/**
 * Requires authentication and, if the route declares `data: { roles: [...] }`,
 * that the current user (per the real GET /api/v1/users/me roles — never a
 * decoded JWT) has at least one of them. Routes with no `roles` data behave
 * like authGuard.
 */
export const permissionGuard: CanActivateFn = (route, state) => {
  const authState = inject(AuthStateService);
  const userProfile = inject(UserProfileService);
  const router = inject(Router);

  if (!authState.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { redirectTo: state.url } });
  }

  const requiredRoles = (route.data['roles'] as string[] | undefined) ?? [];
  const hasAccess = requiredRoles.length === 0 || requiredRoles.some((role) => userProfile.roles().includes(role));

  return hasAccess ? true : router.createUrlTree(['/forbidden']);
};
