import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AddressRequest,
  Client,
  CreatePropertyRequest,
  LinkPropertyOwnerRequest,
  PagedPropertyOwnerResponse,
  PagedPropertyResponse,
  PropertyDetailResponse,
  PropertyOwnerResponse,
  UpdatePropertyRequest,
} from '../../core/api/generated/api-client';

/** Plain values for an edit submission. This dialog always submits the full current form state (never a partial patch), so every field is always sent explicitly — including `null` for condominiumId/address to clear them, matching OptionalJsonConverter&lt;T&gt;'s omitted/null/value trichotomy on the wire. */
export interface UpdatePropertyValues {
  code: string;
  name: string;
  capacity: number;
  condominiumId: string | null;
  address: AddressRequest | null;
}

/** Thin wrapper over the generated Client's property + ownership methods — the only representation of these HTTP contracts this feature uses. */
@Injectable({ providedIn: 'root' })
export class PropertiesService {
  private readonly client = inject(Client);

  list(page: number, pageSize: number): Observable<PagedPropertyResponse> {
    return this.client.propertiesGET(page, pageSize);
  }

  getById(propertyId: string): Observable<PropertyDetailResponse> {
    return this.client.propertiesGET2(propertyId);
  }

  create(request: CreatePropertyRequest): Observable<PropertyDetailResponse> {
    return this.client.propertiesPOST(request);
  }

  update(propertyId: string, values: UpdatePropertyValues): Observable<PropertyDetailResponse> {
    // NSwag's "nullValue": "Undefined" setting (nswag.json) types every nullable field as
    // `T | undefined`, never `T | null` — but the wire contract (OptionalJsonConverter<T>,
    // HandleNull) genuinely accepts an explicit JSON null to clear condominiumId/address,
    // distinct from omitting the key. The cast below preserves that runtime null through a
    // generated type that cannot express it, without hand-declaring a competing contract.
    const request = {
      code: values.code,
      name: values.name,
      capacity: values.capacity,
      condominiumId: values.condominiumId,
      address: values.address,
    } as unknown as UpdatePropertyRequest;
    return this.client.propertiesPATCH(propertyId, request);
  }

  activate(propertyId: string): Observable<PropertyDetailResponse> {
    return this.client.activate(propertyId);
  }

  deactivate(propertyId: string): Observable<PropertyDetailResponse> {
    return this.client.deactivate(propertyId);
  }

  archive(propertyId: string): Observable<PropertyDetailResponse> {
    return this.client.archive(propertyId);
  }

  listOwners(propertyId: string, page: number, pageSize: number): Observable<PagedPropertyOwnerResponse> {
    return this.client.ownersGET(propertyId, page, pageSize);
  }

  linkOwner(propertyId: string, request: LinkPropertyOwnerRequest): Observable<PropertyOwnerResponse> {
    return this.client.ownersPOST(propertyId, request);
  }

  unlinkOwner(propertyId: string, ownerUserId: string): Observable<void> {
    return this.client.ownersDELETE(propertyId, ownerUserId);
  }
}
