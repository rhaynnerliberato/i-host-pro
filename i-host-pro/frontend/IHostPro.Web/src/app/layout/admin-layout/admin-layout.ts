import { BreakpointObserver, Breakpoints } from '@angular/cdk/layout';
import { Component, inject, signal } from '@angular/core';
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
}

const NAV_ITEMS: NavItem[] = [
  { labelKey: 'layout.nav.home', path: '/', icon: 'home' },
  { labelKey: 'layout.nav.users', path: '/users', icon: 'group' },
  { labelKey: 'layout.nav.condominiums', path: '/condominiums', icon: 'apartment' },
  { labelKey: 'layout.nav.properties', path: '/properties', icon: 'villa' },
  { labelKey: 'layout.nav.reservations', path: '/reservations', icon: 'event' },
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
  protected readonly navItems = NAV_ITEMS;

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
