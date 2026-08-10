import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  Client,
  CreatePolicyValueVersionRequest,
  EffectivePolicyResponse,
  PolicyDefinitionResponse,
  PolicyValueDetailResponse,
} from '../../core/api/generated/api-client';

/** Thin wrapper over the generated Client's policy catalog/value/effective/history methods — the only representation of these HTTP contracts this feature uses. */
@Injectable({ providedIn: 'root' })
export class PoliciesService {
  private readonly client = inject(Client);

  list(): Observable<PolicyDefinitionResponse[]> {
    return this.client.policies();
  }

  getValueAtScope(policyCode: string, scopeType: string, propertyId: string | undefined): Observable<PolicyValueDetailResponse> {
    return this.client.valuesGET(policyCode, scopeType, propertyId);
  }

  createVersion(policyCode: string, request: CreatePolicyValueVersionRequest): Observable<PolicyValueDetailResponse> {
    return this.client.valuesPOST(policyCode, request);
  }

  getEffective(policyCode: string, propertyId: string | undefined): Observable<EffectivePolicyResponse> {
    return this.client.effective(policyCode, propertyId);
  }

  getHistory(policyCode: string, scopeType: string, propertyId: string | undefined): Observable<PolicyValueDetailResponse[]> {
    return this.client.history(policyCode, scopeType, propertyId);
  }
}
