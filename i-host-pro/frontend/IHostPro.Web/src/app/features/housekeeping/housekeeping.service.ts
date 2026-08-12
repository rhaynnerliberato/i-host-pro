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

  start(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.start(cleaningId);
  }

  startInspection(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.startInspection(cleaningId);
  }

  complete(cleaningId: string): Observable<CleaningDetailResponse> {
    return this.client.complete(cleaningId);
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
