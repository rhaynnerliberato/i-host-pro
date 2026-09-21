import { Component, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { TranslocoPipe } from '@jsverse/transloco';

import { BrandMark } from '../brand-mark/brand-mark';

/**
 * UI/UX Visual Foundation gate — the one shared layout for every public auth
 * page (login/signup/forgot-password/reset-password): a split-panel on
 * desktop (brand/context panel + form card) collapsing to a single-column
 * form-only card on mobile. Owns the card shell itself (not just the page
 * wrapper) so individual auth pages no longer each declare their own card
 * width/padding — they only project their form (and any success/error
 * state) as content.
 */
@Component({
  selector: 'app-auth-shell',
  imports: [MatCardModule, BrandMark, TranslocoPipe],
  templateUrl: './auth-shell.html',
  styleUrl: './auth-shell.scss',
})
export class AuthShell {
  readonly titleKey = input.required<string>();
}
