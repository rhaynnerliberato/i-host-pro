import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { Client, ScheduleItemResponse } from '../../core/api/generated/api-client';

/**
 * Thin wrapper over the generated Client's schedule method — the only
 * representation of this HTTP contract this feature uses. `from`/`to` are
 * always required by the backend (GET /api/v1/schedule never accepts an
 * unbounded scan — ListScheduleQueryValidator) and capped at a 100-day
 * window; the caller (ScheduleCalendar) always supplies FullCalendar's own
 * visible-range bounds.
 */
@Injectable({ providedIn: 'root' })
export class ScheduleService {
  private readonly client = inject(Client);

  list(from: Date, to: Date, propertyId?: string, housekeeperUserId?: string, eventType?: string): Observable<ScheduleItemResponse[]> {
    return this.client.schedule(from, to, propertyId, housekeeperUserId, eventType);
  }
}
