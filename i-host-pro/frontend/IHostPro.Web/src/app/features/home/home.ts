import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { UserProfileService } from '../../core/auth/user-profile.service';

/**
 * Minimal authenticated landing page for Checkpoint 3 (auth flow needs a
 * real destination to redirect to and to assert against). Checkpoint 4 wraps
 * this same route in the full admin layout shell (header/nav) — the content
 * here is not throwaway.
 */
@Component({
  selector: 'app-home',
  imports: [TranslocoPipe],
  templateUrl: './home.html',
  styleUrl: './home.scss',
})
export class Home {
  protected readonly userProfile = inject(UserProfileService);
}
