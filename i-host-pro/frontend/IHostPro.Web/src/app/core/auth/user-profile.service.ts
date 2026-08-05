import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';

import { Client, OwnProfileResponse } from '../api/generated/api-client';

/**
 * Source of truth for "who is the current user and what can they do".
 * Roles come from a real GET /api/v1/users/me call — the JWT is never
 * decoded client-side to derive authorization decisions.
 */
@Injectable({ providedIn: 'root' })
export class UserProfileService {
  private readonly client = inject(Client);

  private readonly profileSignal = signal<OwnProfileResponse | null>(null);

  readonly profile = this.profileSignal.asReadonly();
  readonly roles = computed(() => this.profileSignal()?.roles ?? []);

  load(): Observable<OwnProfileResponse> {
    return this.client.me().pipe(tap((profile) => this.profileSignal.set(profile)));
  }

  clear(): void {
    this.profileSignal.set(null);
  }
}
