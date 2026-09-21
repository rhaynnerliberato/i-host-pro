import { Component, computed, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { catchError, of } from 'rxjs';

import { UserProfileService } from '../../core/auth/user-profile.service';
import { AirbnbEmailService } from '../integrations/airbnb-email/airbnb-email.service';
import { PropertiesService } from '../property-management/properties.service';

/**
 * Minimal authenticated landing page for Checkpoint 3 (auth flow needs a
 * real destination to redirect to and to assert against). Checkpoint 4 wraps
 * this same route in the full admin layout shell (header/nav) — the content
 * here is not throwaway.
 *
 * Self-Service Identity & Onboarding Foundation gate — the first-use
 * checklist below is DERIVED entirely from existing state via existing
 * endpoints (frontend composition, no new backend aggregator endpoint, no
 * new persistence): a brand-new tenant has no Property, no Airbnb Email
 * connection, and no listing mapping yet, so all three start incomplete and
 * the card disappears on its own once genuinely done. A failed check (e.g. a
 * user without permission to see Properties/Integrations) is treated as
 * "incomplete" rather than surfacing an error on the home page.
 */
@Component({
  selector: 'app-home',
  imports: [TranslocoPipe, RouterLink, MatCardModule, MatIconModule],
  templateUrl: './home.html',
  styleUrl: './home.scss',
})
export class Home {
  protected readonly userProfile = inject(UserProfileService);
  private readonly propertiesService = inject(PropertiesService);
  private readonly airbnbEmailService = inject(AirbnbEmailService);

  protected readonly hasProperty = signal<boolean | null>(null);
  protected readonly airbnbConnected = signal<boolean | null>(null);
  protected readonly hasListingMapping = signal<boolean | null>(null);

  protected readonly checklistLoaded = computed(
    () => this.hasProperty() !== null && this.airbnbConnected() !== null && this.hasListingMapping() !== null,
  );
  protected readonly checklistComplete = computed(
    () => this.hasProperty() === true && this.airbnbConnected() === true && this.hasListingMapping() === true,
  );

  constructor() {
    this.propertiesService
      .list(1, 1)
      .pipe(catchError(() => of(null)))
      .subscribe((result) => this.hasProperty.set(((result?.totalCount ?? 0) > 0)));

    this.airbnbEmailService
      .getStatus()
      .pipe(catchError(() => of(null)))
      .subscribe((status) => this.airbnbConnected.set(status?.status === 'connected'));

    this.airbnbEmailService
      .listMappings()
      .pipe(catchError(() => of(null)))
      .subscribe((mappings) => this.hasListingMapping.set((mappings?.length ?? 0) > 0));
  }
}
