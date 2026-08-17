import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { Client, DashboardOverviewResponse } from '../../core/api/generated/api-client';

/**
 * Thin wrapper over the generated Client's overview method — the only
 * representation of this HTTP contract this feature uses (mirrors
 * ScheduleService's own precedent). `from`/`to` are always required by the
 * backend (GET /api/v1/dashboard/overview — GetDashboardOverviewQueryValidator)
 * and capped at a 100-day window; the caller (DashboardOverview) always
 * supplies the currently selected period's bounds.
 */
@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly client = inject(Client);

  overview(from: Date, to: Date): Observable<DashboardOverviewResponse> {
    return this.client.overview(from, to);
  }
}
