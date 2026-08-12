import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { Client, CreateReservationRequest, PagedReservationResponse, ReservationDetailResponse, UpdateReservationRequest } from '../../core/api/generated/api-client';

/** Plain values for an edit submission. This dialog always submits the full current form state (never a partial patch), so every field is always sent explicitly — including `null` for guestPhone to clear it, matching the backend's Optional<T> omitted/null/value trichotomy on the wire (only the null case is reachable from this UI, since every field is always set). */
export interface UpdateReservationValues {
  propertyId: string;
  guestName: string;
  guestPhone: string | null;
  checkInAt: Date;
  checkOutAt: Date;
  guestCount: number;
}

/** Thin wrapper over the generated Client's reservation methods — the only representation of these HTTP contracts this feature uses. */
@Injectable({ providedIn: 'root' })
export class ReservationsService {
  private readonly client = inject(Client);

  list(
    page: number,
    pageSize: number,
    propertyId?: string,
    status?: string,
    from?: Date,
    to?: Date,
  ): Observable<PagedReservationResponse> {
    return this.client.reservationsGET(propertyId, status, from, to, page, pageSize);
  }

  getById(reservationId: string): Observable<ReservationDetailResponse> {
    return this.client.reservationsGET2(reservationId);
  }

  create(request: CreateReservationRequest): Observable<ReservationDetailResponse> {
    return this.client.reservationsPOST(request);
  }

  update(reservationId: string, values: UpdateReservationValues): Observable<ReservationDetailResponse> {
    // NSwag's "nullValue": "Undefined" setting (nswag.json) types every nullable field as
    // `T | undefined`, never `T | null` — but the wire contract (this context's own
    // OptionalJsonConverter<T>, HandleNull) genuinely accepts an explicit JSON null to clear
    // guestPhone, distinct from omitting the key. The cast below preserves that runtime null
    // through a generated type that cannot express it, without hand-declaring a competing
    // contract — same rationale as properties.service.ts's update().
    const request = {
      propertyId: values.propertyId,
      guestName: values.guestName,
      guestPhone: values.guestPhone,
      checkInAt: values.checkInAt,
      checkOutAt: values.checkOutAt,
      guestCount: values.guestCount,
    } as unknown as UpdateReservationRequest;
    return this.client.reservationsPATCH(reservationId, request);
  }

  cancel(reservationId: string): Observable<ReservationDetailResponse> {
    // Explicit, stable OperationId ("CancelReservation") assigned server-side
    // (Program.cs's AddSwaggerGen CustomOperationIds) — see the Fase 6
    // homologation document (OpenAPI operationId stability gate) for why a
    // numeric-suffix name (cancel2) is no longer produced here. The actual
    // HTTP route (/api/v1/reservations/{reservationId}/cancel) is unchanged.
    return this.client.cancelReservation(reservationId);
  }
}
