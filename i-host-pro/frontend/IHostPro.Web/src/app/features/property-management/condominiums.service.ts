import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { Client, CondominiumDetailResponse, CreateCondominiumRequest, PagedCondominiumResponse, UpdateCondominiumRequest } from '../../core/api/generated/api-client';

/** Thin wrapper over the generated Client's condominium methods — the only representation of these HTTP contracts this feature uses. */
@Injectable({ providedIn: 'root' })
export class CondominiumsService {
  private readonly client = inject(Client);

  list(page: number, pageSize: number): Observable<PagedCondominiumResponse> {
    return this.client.condominiumsGET(page, pageSize);
  }

  getById(condominiumId: string): Observable<CondominiumDetailResponse> {
    return this.client.condominiumsGET2(condominiumId);
  }

  create(request: CreateCondominiumRequest): Observable<CondominiumDetailResponse> {
    return this.client.condominiumsPOST(request);
  }

  update(condominiumId: string, request: UpdateCondominiumRequest): Observable<CondominiumDetailResponse> {
    return this.client.condominiumsPATCH(condominiumId, request);
  }
}
