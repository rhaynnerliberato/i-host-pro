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
  });

  it('shows the "Condomínios" and "Imóveis" nav items to a user holding PROPERTIES:MANAGE', () => {
    const component = configure(['PROPERTIES:MANAGE']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/condominiums');
    expect(paths).toContain('/properties');
  });

  it('hides the "Condomínios" and "Imóveis" nav items from a user who does not hold PROPERTIES:MANAGE', () => {
    const component = configure(['SOME:OTHER']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).not.toContain('/condominiums');
    expect(paths).not.toContain('/properties');
  });

  it('shows the "Reservas" nav item to a user holding RESERVATIONS:MANAGE', () => {
    const component = configure(['RESERVATIONS:MANAGE']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/reservations');
  });

  it('hides the "Reservas" nav item from a user who does not hold RESERVATIONS:MANAGE', () => {
    const component = configure(['SOME:OTHER']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).not.toContain('/reservations');
  });

  it('shows the "Políticas" nav item to a user holding only POLICIES:READ (OR semantics across an array)', () => {
    const component = configure(['POLICIES:READ']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/policies');
  });

  it('shows the "Políticas" nav item to a user holding only POLICIES:MANAGE (OR semantics across an array)', () => {
    const component = configure(['POLICIES:MANAGE']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/policies');
  });

  it('hides the "Políticas" nav item from a user who holds neither POLICIES:READ nor POLICIES:MANAGE', () => {
    const component = configure(['SOME:OTHER']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).not.toContain('/policies');
  });

  it('shows the "Limpezas" nav item to a user holding CLEANINGS:MANAGE', () => {
    const component = configure(['CLEANINGS:MANAGE']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/housekeeping');
  });

  it('hides the "Limpezas" nav item from a user who does not hold CLEANINGS:MANAGE', () => {
    const component = configure(['SOME:OTHER']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).not.toContain('/housekeeping');
  });

  it('shows the "Dashboard" nav item to a user holding only DASHBOARD:MANAGE (OR semantics across an array)', () => {
    const component = configure(['DASHBOARD:MANAGE']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/dashboard');
  });

  it('shows the "Dashboard" nav item to a user holding only DASHBOARD:READ (OR semantics across an array)', () => {
    const component = configure(['DASHBOARD:READ']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).toContain('/dashboard');
  });

  it('hides the "Dashboard" nav item from a user holding only DASHBOARD:READ:OWN_OWNER (no prefix matching)', () => {
    const component = configure(['DASHBOARD:READ:OWN_OWNER']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).not.toContain('/dashboard');
  });

  it('hides the "Dashboard" nav item from a user holding only DASHBOARD:USE', () => {
    const component = configure(['DASHBOARD:USE']);

    const paths = component['navItems']().map((item) => item.path);

    expect(paths).not.toContain('/dashboard');
  });
});
