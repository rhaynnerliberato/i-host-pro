import { BreakpointObserver, Breakpoints } from '@angular/cdk/layout';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { map } from 'rxjs';

import { toSignal } from '@angular/core/rxjs-interop';
import { AuthService } from '../../core/auth/auth.service';
import { UserProfileService } from '../../core/auth/user-profile.service';

interface NavItem {
  labelKey: string;
  path: string;
  icon: string;
  /**
   * Real permission code(s) (never a role name) required to show this item —
   * omitted means always visible to an authenticated user. An array is OR
   * semantics (mirrors `permissionGuard`'s own `.some(...)`): needed for
   * Policies, whose seeded catalog deliberately gives no single role both
   * `POLICIES:READ` and `POLICIES:MANAGE` (only ADMIN has MANAGE, only
   * AI_AGENT has READ) — either one must be able to reach the nav entry.
   */
  requiredPermission?: string | string[];
}

const NAV_ITEMS: NavItem[] = [
  { labelKey: 'layout.nav.home', path: '/', icon: 'home' },
  { labelKey: 'layout.nav.users', path: '/users', icon: 'group', requiredPermission: 'USERS:MANAGE' },
  { labelKey: 'layout.nav.condominiums', path: '/condominiums', icon: 'apartment', requiredPermission: 'PROPERTIES:MANAGE' },
  { labelKey: 'layout.nav.properties', path: '/properties', icon: 'villa', requiredPermission: 'PROPERTIES:MANAGE' },
  { labelKey: 'layout.nav.reservations', path: '/reservations', icon: 'event', requiredPermission: 'RESERVATIONS:MANAGE' },
  { labelKey: 'layout.nav.policies', path: '/policies', icon: 'rule', requiredPermission: ['POLICIES:READ', 'POLICIES:MANAGE'] },
  { labelKey: 'layout.nav.housekeeping', path: '/housekeeping', icon: 'cleaning_services', requiredPermission: 'CLEANINGS:MANAGE' },
  { labelKey: 'layout.nav.schedule', path: '/schedule', icon: 'calendar_month', requiredPermission: ['SCHEDULE:MANAGE', 'SCHEDULE:READ'] },
  { labelKey: 'layout.nav.dashboard', path: '/dashboard', icon: 'dashboard', requiredPermission: ['DASHBOARD:MANAGE', 'DASHBOARD:READ'] },
];

@Component({
  selector: 'app-admin-layout',
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    TranslocoPipe,
    MatSidenavModule,
    MatToolbarModule,
    MatButtonModule,
    MatIconModule,
    MatListModule,
  ],
  templateUrl: './admin-layout.html',
  styleUrl: './admin-layout.scss',
})
export class AdminLayout {
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);
  private readonly breakpointObserver = inject(BreakpointObserver);

  protected readonly userProfile = inject(UserProfileService);
  protected readonly navItems = computed(() =>
    NAV_ITEMS.filter((item) => {
      if (!item.requiredPermission) return true;
      const codes = Array.isArray(item.requiredPermission) ? item.requiredPermission : [item.requiredPermission];
      return codes.some((code) => this.userProfile.hasPermission(code));
    }),
  );

  protected readonly isHandset = toSignal(
    this.breakpointObserver.observe(Breakpoints.Handset).pipe(map((result) => result.matches)),
    { initialValue: false },
  );

  protected readonly sidenavOpened = signal(true);

  protected toggleSidenav(): void {
    this.sidenavOpened.update((opened) => !opened);
  }

  protected logout(): void {
    this.authService.logout().subscribe(() => this.router.navigateByUrl('/login'));
  }
}
