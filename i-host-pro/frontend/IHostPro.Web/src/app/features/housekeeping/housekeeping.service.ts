import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AssignCleaningRequest,
  CleaningDetailResponse,
  Client,
  CreateCleaningRequest,
  PagedCleaningResponse,
} from '../../core/api/generated/api-client';

/** Thin wrapper over the generated Client's cleaning methods — the only representation of these HTTP contracts this feature uses. */
@Injectable({ providedIn: 'root' })
export class HousekeepingService {
  private readonly client = inject(Client);

  list(
    page: number,
    pageSize: number,
    status?: string,
    propertyId?: string,
    assignedHousekeeperUserId?: string,
  ): Observable<PagedCleaningResponse> {
    return this.client.cleaningsGET(status, propertyId, assignedHousekeeperUserId, page, pageSize);
  }

  getById(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.cleaningsGET2(cleaningId);
  }

  create(request: CreateCleaningRequest): Observable<CleaningDetailResponse> {
    return this.client.cleaningsPOST(request);
  }

  assign(cleaningId: string, housekeeperUserId: string): Observable<CleaningDetailResponse> {
    return this.client.assign(cleaningId, { housekeeperUserId } satisfies AssignCleaningRequest);
  }

  // NSwag's flattened Client class renamed this to start3 — the Self-Service
  // Identity & Onboarding Foundation gate's new forgot-password/start
  // endpoint now occupies "start2" (itself already renamed once before, by
  // the Airbnb Email Bridge Web OAuth gate's oauth/start endpoint).
  start(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.start3(cleaningId);
  }

  startInspection(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.startInspection(cleaningId);
  }

  // NSwag's flattened Client class renamed this to complete2 — the Self-Service
  // Identity & Onboarding Foundation gate's new forgot-password/complete
  // endpoint took the plain "complete" operation id.
  complete(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.complete2(cleaningId);
  }

  cancel(cleaningId: string): Observable<CleaningDetailResponse> {
    // Explicit, stable OperationId ("CancelCleaning") assigned server-side
    // (Program.cs's AddSwaggerGen CustomOperationIds) — see the Fase 6
    // homologation document (OpenAPI operationId stability gate). The actual
    // HTTP route (/api/v1/cleanings/{cleaningId}/cancel) is unchanged.
    return this.client.cancelCleaning(cleaningId);
  }

  markInterrupted(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.interrupt(cleaningId);
  }

  markWaitingMaterials(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.waitingMaterials(cleaningId);
  }

  markWaitingHelp(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.waitingHelp(cleaningId);
  }
}
