import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { UserProfileService } from '../../core/auth/user-profile.service';
import { AdminLayout } from './admin-layout';

// Constructed directly via runInInjectionContext (never TestBed.createComponent/detectChanges): the
// template pulls in TranslocoPipe, which needs a full Transloco provider tree unrelated to what this
// suite verifies. navItems is plain component logic driven by inject()-based fields, so building the
// class instance in an injection context exercises it without ever rendering the template.
function configure(permissions: string[]): AdminLayout {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: BreakpointObserver, useValue: { observe: () => of({ matches: false }) } },
      { provide: UserProfileService, useValue: { hasPermission: (code: string) => permissions.includes(code) } },
      { provide: AuthService, useValue: { logout: () => of(undefined) } },
    ],
  });
  return TestBed.runInInjectionContext(() => new AdminLayout());
}

describe('AdminLayout nav item visibility', () => {
  it('shows the "Usuários" nav item to a user holding USERS:MANAGE', () => {
    const component = configure(['USERS:MANAGE']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/users');
  });

  it('hides the "Usuários" nav item from a user who does not hold USERS:MANAGE', () => {
    const component = configure(['SOME:OTHER']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).not.toContain('/users');
  });

  it('hides every permission-gated item (fails closed) when the profile has no permissions at all', () => {
    const component = configure([]);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).not.toContain('/users');
    expect(paths).toContain('/');
  });

  it('always shows nav items that declare no required permission, regardless of the user\'s permissions', () => {
    const component = configure([]);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/');
    expect(paths).toContain('/condominiums');
  });
});
