import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { AuthService } from '../../core/auth/auth.service';
import { PortalShell } from './portal-shell';

function configure(logout: () => ReturnType<AuthService['logout']>) {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), { provide: AuthService, useValue: { logout } }],
  });
  return TestBed.runInInjectionContext(() => new PortalShell());
}

describe('PortalShell', () => {
  it('logs out and navigates to /login', () => {
    const logout = vi.fn().mockReturnValue(of(undefined));
    const component = configure(logout);
    const router = TestBed.inject(Router);
    const navigateSpy = vi.spyOn(router, 'navigateByUrl');

    component['logout']();

    expect(logout).toHaveBeenCalled();
    expect(navigateSpy).toHaveBeenCalledWith('/login');
  });
});
