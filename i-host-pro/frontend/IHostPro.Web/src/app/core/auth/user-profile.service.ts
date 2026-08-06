import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';

import { Client, OwnProfileResponse } from '../api/generated/api-client';

/**
 * Source of truth for "who is the current user and what can they do".
 * Roles AND effective permissions both come from a real GET /api/v1/users/me
 * call — the JWT is never decoded client-side to derive authorization
 * decisions, and roles are never used as an authorization decision on their
 * own (Fase 4, Incremento 2 — OwnProfileResponse.permissions is the sole
 * frontend source of truth for "what can I do"; `roles` remains available
 * for display purposes only).
 *
 * Fail-closed by construction: `permissions` falls back to an empty array
 * whenever there is no profile (never loaded, cleared on logout, or a
 * refresh failure clearing the session) — `hasPermission` on an empty array
 * is always `false`, never a silent "assume granted".
 */
@Injectable({ providedIn: 'root' })
export class UserProfileService {
  private readonly client = inject(Client);

  private readonly profileSignal = signal<OwnProfileResponse | null>(null);

  readonly profile = this.profileSignal.asReadonly();
  readonly roles = computed(() => this.profileSignal()?.roles ?? []);
  readonly permissions = computed(() => this.profileSignal()?.permissions ?? []);

  hasPermission(permissionCode: string): boolean {
    return this.permissions().includes(permissionCode);
  }

  load(): Observable<OwnProfileResponse> {
    return this.client.me().pipe(tap((profile) => this.profileSignal.set(profile)));
  }

  clear(): void {
    this.profileSignal.set(null);
  }
}
