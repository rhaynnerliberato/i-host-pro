import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client } from '../../core/api/generated/api-client';
import { PoliciesService } from './policies.service';

describe('PoliciesService', () => {
  let service: PoliciesService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = {
      policies: vi.fn().mockReturnValue(of([])),
      valuesGET: vi.fn().mockReturnValue(of({})),
      valuesPOST: vi.fn().mockReturnValue(of({})),
      effective: vi.fn().mockReturnValue(of({})),
      history: vi.fn().mockReturnValue(of([])),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(PoliciesService);
  });

  it('list delegates to Client.policies with no arguments', () => {
    service.list().subscribe();
    expect(client['policies']).toHaveBeenCalledWith();
  });

  it('getValueAtScope delegates to Client.valuesGET with policyCode, scopeType and propertyId', () => {
    service.getValueAtScope('EARLY_CHECKIN', 'Property', 'prop-1').subscribe();
    expect(client['valuesGET']).toHaveBeenCalledWith('EARLY_CHECKIN', 'Property', 'prop-1');
  });

  it('createVersion delegates to Client.valuesPOST with policyCode and request body', () => {
    const request = { scopeType: 'Tenant', value: { allowed: true }, reason: 'initial setup' };
    service.createVersion('EARLY_CHECKIN', request).subscribe();
    expect(client['valuesPOST']).toHaveBeenCalledWith('EARLY_CHECKIN', request);
  });

  it('getEffective delegates to Client.effective with policyCode and propertyId', () => {
    service.getEffective('LATE_CHECKOUT', 'prop-1').subscribe();
    expect(client['effective']).toHaveBeenCalledWith('LATE_CHECKOUT', 'prop-1');
  });

  it('getHistory delegates to Client.history with policyCode, scopeType and propertyId', () => {
    service.getHistory('LATE_CHECKOUT', 'Tenant', undefined).subscribe();
    expect(client['history']).toHaveBeenCalledWith('LATE_CHECKOUT', 'Tenant', undefined);
  });
});
