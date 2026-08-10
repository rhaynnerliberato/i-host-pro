import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { PolicyDefinitionResponse } from '../../../core/api/generated/api-client';
import { PoliciesService } from '../policies.service';
import { PoliciesList } from './policies-list';

const earlyCheckIn: PolicyDefinitionResponse = {
  code: 'EARLY_CHECKIN',
  name: 'Early check-in',
  description: 'Allows early check-in',
  category: 'CheckIn',
  valueType: 'Object',
  schemaVersion: 1,
  isActive: true,
};

function configure(listResult: PolicyDefinitionResponse[] = []) {
  const policiesService = { list: vi.fn().mockReturnValue(of(listResult)) };
  const dialog = { open: vi.fn() };

  TestBed.configureTestingModule({
    providers: [
      { provide: PoliciesService, useValue: policiesService },
      { provide: MatDialog, useValue: dialog },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new PoliciesList());
  return { component, policiesService, dialog };
}

describe('PoliciesList', () => {
  it('loads the catalog on construction, ending in the loaded state when items exist', () => {
    const { component, policiesService } = configure([earlyCheckIn]);

    expect(policiesService.list).toHaveBeenCalledTimes(1);
    expect(component['state']()).toBe('loaded');
    expect(component['policies']()).toEqual([earlyCheckIn]);
  });

  it('ends in the empty state when the catalog has zero items', () => {
    const { component } = configure([]);

    expect(component['state']()).toBe('empty');
  });

  it('ends in the error state when the list request fails', () => {
    const policiesService = { list: vi.fn().mockReturnValue(throwError(() => new Error('boom'))) };
    TestBed.configureTestingModule({
      providers: [
        { provide: PoliciesService, useValue: policiesService },
        { provide: MatDialog, useValue: { open: vi.fn() } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new PoliciesList());

    expect(component['state']()).toBe('error');
  });

  it('openDetailDialog opens PolicyDetailDialog with the selected policy', () => {
    const { component, dialog } = configure([earlyCheckIn]);

    component['openDetailDialog'](earlyCheckIn);

    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { policy: earlyCheckIn } }));
  });
});
