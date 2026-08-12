import { Component, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';

/**
 * Dedicated mobile-first shell for the Portal da Faxineira (Fase 6,
 * Incremento 2A) — deliberately never reuses `AdminLayout` (approval §5-6):
 * no side navigation, no `BreakpointObserver`-driven `mat-sidenav`, no PWA/
 * offline behavior. A single top toolbar (app name only) and a bottom
 * navigation bar with exactly two destinations — "Minhas Faxinas" and
 * "Sair" — matching Documento 14 §26's own "extremamente simples" mandate:
 * this portal has one real screen (own cleanings), so a multi-tab bottom
 * nav would be functionality this increment does not need.
 */
@Component({
  selector: 'app-portal-shell',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, TranslocoPipe, MatIconModule, MatToolbarModule],
  templateUrl: './portal-shell.html',
  styleUrl: './portal-shell.scss',
})
export class PortalShell {
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);

  protected logout(): void {
    this.authService.logout().subscribe(() => this.router.navigateByUrl('/login'));
  }
}
