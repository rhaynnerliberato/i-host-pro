import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  Client,
  CleaningChecklistItemResponse,
  CleaningDetailResponse,
  CleaningOccurrenceResponse,
  PagedCleaningResponse,
} from '../../core/api/generated/api-client';

/**
 * Thin wrapper over the generated Client's self-service ("own cleaning")
 * methods — the Portal da Faxineira's only representation of these HTTP
 * contracts (Fase 6, Incremento 2A). Mirrors `HousekeepingService`'s own
 * shape, deliberately kept a separate service/injection token: this feature
 * never reuses the administrative `HousekeepingService` (approval §5-6 —
 * the Portal is a self-contained self-service surface, never routed through
 * admin-only endpoints).
 */
@Injectable({ providedIn: 'root' })
export class PortalService {
  private readonly client = inject(Client);

  listMyCleanings(status?: string, page?: number, pageSize?: number): Observable<PagedCleaningResponse> {
    return this.client.myCleanings(status, page, pageSize);
  }

  getMyCleaningById(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.myCleanings2(cleaningId);
  }

  markInTransit(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.inTransit(cleaningId);
  }

  start(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.startOwnCleaning(cleaningId);
  }

  startInspection(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.startOwnCleaningInspection(cleaningId);
  }

  complete(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.completeOwnCleaning(cleaningId);
  }

  markWaitingMaterials(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.markOwnCleaningWaitingMaterials(cleaningId);
  }

  markWaitingHelp(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.markOwnCleaningWaitingHelp(cleaningId);
  }

  reportDelay(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.delay(cleaningId);
  }

  registerOccurrence(cleaningId: string, type: string, description: string | undefined): Observable<CleaningOccurrenceResponse> {
    return this.client.occurrences(cleaningId, { type, description });
  }

  listOccurrences(cleaningId: string): Observable<CleaningOccurrenceResponse[]> {
    return this.client.occurrencesAll(cleaningId);
  }

  getChecklist(cleaningId: string): Observable<CleaningChecklistItemResponse[]> {
    return this.client.checklistAll(cleaningId);
  }

  setChecklistItem(cleaningId: string, itemType: string, isChecked: boolean): Observable<CleaningChecklistItemResponse> {
    return this.client.checklist(cleaningId, itemType, { isChecked });
  }
}
