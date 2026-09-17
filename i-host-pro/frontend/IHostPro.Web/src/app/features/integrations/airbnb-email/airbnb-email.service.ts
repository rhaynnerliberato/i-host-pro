import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import {
  AirbnbAutoPublicationStatusResponse,
  AirbnbEmailConnectionStatus,
  AirbnbListingTitleMappingResponse,
  Client,
  CreateAirbnbListingTitleMappingRequest,
} from '../../../core/api/generated/api-client';

/**
 * The backend has no global JsonStringEnumConverter, so AirbnbEmailConnectionStatus
 * serializes as a plain integer and NSwag emits an unnamed `_0`/`_1`/`_2`/`_3` enum
 * (no OpenAPI enum-name metadata to generate real member names from). This maps it
 * to the same friendly-string convention already used for reservation status
 * elsewhere in this app, so the numeric enum never leaks past this service.
 */
export type AirbnbEmailConnectionStatusLabel = 'notConfigured' | 'disconnected' | 'connected' | 'error';

export interface AirbnbEmailMailboxStatus {
  status: AirbnbEmailConnectionStatusLabel;
  isEnabled: boolean;
  lastAuthenticatedAtUtc: Date | undefined;
  mailboxAddress: string | undefined;
}

export interface AirbnbEmailProcessingSummary {
  pending: number;
  processed: number;
  needsReview: number;
  failed: number;
  ignored: number;
}

function toStatusLabel(status: AirbnbEmailConnectionStatus | undefined): AirbnbEmailConnectionStatusLabel {
  switch (status) {
    case AirbnbEmailConnectionStatus._2:
      return 'connected';
    case AirbnbEmailConnectionStatus._1:
      return 'disconnected';
    case AirbnbEmailConnectionStatus._3:
      return 'error';
    default:
      return 'notConfigured';
  }
}

/** Thin wrapper over the generated Client's Airbnb Email Bridge methods — the only representation of these HTTP contracts this feature uses. */
@Injectable({ providedIn: 'root' })
export class AirbnbEmailService {
  private readonly client = inject(Client);

  getStatus(): Observable<AirbnbEmailMailboxStatus> {
    return this.client.airbnbEmail().pipe(
      map((response) => ({
        status: toStatusLabel(response.status),
        isEnabled: response.isEnabled ?? false,
        lastAuthenticatedAtUtc: response.lastAuthenticatedAtUtc,
        mailboxAddress: response.mailboxAddress,
      })),
    );
  }

  disconnect(): Observable<void> {
    return this.client.disconnect().pipe(map(() => undefined));
  }

  /**
   * Web OAuth architecture gate: asks the backend for a fresh, single-use
   * Microsoft authorization URL. The caller navigates the browser there with
   * a full-page redirect — never opened in an iframe/popup, and never any
   * `state`/PKCE material handled on this side (both stay server-side).
   */
  connect(): Observable<string> {
    return this.client.start().pipe(map((response) => response.authorizationUrl ?? ''));
  }

  getAutoPublicationStatus(): Observable<AirbnbAutoPublicationStatusResponse> {
    return this.client.autoPublication();
  }

  enableAutoPublication(notBeforeUtc: Date): Observable<AirbnbAutoPublicationStatusResponse> {
    return this.client.enableAutoPublication({ notBeforeUtc });
  }

  disableAutoPublication(): Observable<AirbnbAutoPublicationStatusResponse> {
    return this.client.disableAutoPublication();
  }

  getProcessingSummary(): Observable<AirbnbEmailProcessingSummary> {
    return this.client.processingSummary().pipe(
      map((response) => ({
        pending: response.pending ?? 0,
        processed: response.processed ?? 0,
        needsReview: response.needsReview ?? 0,
        failed: response.failed ?? 0,
        ignored: response.ignored ?? 0,
      })),
    );
  }

  listMappings(): Observable<AirbnbListingTitleMappingResponse[]> {
    return this.client.listingTitleMappingsAll();
  }

  createMapping(request: CreateAirbnbListingTitleMappingRequest): Observable<AirbnbListingTitleMappingResponse> {
    return this.client.listingTitleMappings(request);
  }
}
