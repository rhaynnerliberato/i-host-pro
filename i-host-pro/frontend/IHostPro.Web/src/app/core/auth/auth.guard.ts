import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthStateService } from './auth-state.service';

export const authGuard: CanActivateFn = (_route, state) => {
  const authState = inject(AuthStateService);
  const router = inject(Router);

  return authState.isAuthenticated() ? true : router.createUrlTree(['/login'], { queryParams: { redirectTo: state.url } });
};
