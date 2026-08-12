import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CleaningSummaryResponse, PagedCleaningResponse } from '../../../core/api/generated/api-client';
import { PortalService } from '../portal.service';
import { MyCleaningsList } from './my-cleanings-list';

function configure(listResult: PagedCleaningResponse = { items: [], totalCount: 0 }) {
  const portalService = {
    listMyCleanings: vi.fn().mockReturnValue(of(listResult)),
    start: vi.fn().mockReturnValue(of({})),
    startInspection: vi.fn().mockReturnValue(of({})),
    complete: vi.fn().mockReturnValue(of({})),
  };
  const router = { navigate: vi.fn() };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };

  TestBed.configureTestingModule({
    providers: [
      { provide: PortalService, useValue: portalService },
      { provide: Router, useValue: router },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new MyCleaningsList());
  return { component, portalService, router, snackBar };
}

describe('MyCleaningsList', () => {
  const cleaning: CleaningSummaryResponse = {
    id: 'c1',
    propertyId: 'p1',
    status: 'Assigned',
    createdAtUtc: new Date('2026-08-01T10:00:00Z'),
    scheduledAtUtc: new Date('2026-08-02T14:00:00Z'),
  } as CleaningSummaryResponse;

  it('loads own cleanings on construction, ending in the loaded state when items exist', () => {
    const { component, portalService } = configure({ items: [cleaning], totalCount: 1 });

    expect(portalService.listMyCleanings).toHaveBeenCalledWith(undefined, 1, 50);
    expect(component['state']()).toBe('loaded');
    expect(component['cleanings']()).toEqual([cleaning]);
  });

  it('ends in the empty state when the server returns no items', () => {
    const { component } = configure({ items: [], totalCount: 0 });

    expect(component['state']()).toBe('empty');
  });

  it('ends in the error state when the request fails', () => {
    const portalService = { listMyCleanings: vi.fn().mockReturnValue(throwError(() => new Error('boom'))) };
    TestBed.configureTestingModule({
      providers: [
        { provide: PortalService, useValue: portalService },
        { provide: Router, useValue: { navigate: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (key: string) => key } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new MyCleaningsList());

    expect(component['state']()).toBe('error');
  });

  it('navigates to the detail route when a card is opened', () => {
    const { component, router } = configure({ items: [cleaning], totalCount: 1 });

    component['openDetail'](cleaning);

    expect(router.navigate).toHaveBeenCalledWith(['/my-cleanings', 'c1']);
  });

  it('exposes Start as the primary action for Assigned and InTransit, never skipping StartInspection', () => {
    const { component } = configure();

    expect(component['primaryActionKey']('Assigned')).toBe('portal.list.start');
    expect(component['primaryActionKey']('InTransit')).toBe('portal.list.start');
    expect(component['primaryActionKey']('Started')).toBe('portal.list.startInspection');
    expect(component['primaryActionKey']('InInspection')).toBe('portal.list.complete');
  });

  it('exposes no primary action for statuses with no forward self-service transition', () => {
    const { component } = configure();

    expect(component['primaryActionKey']('Completed')).toBeNull();
    expect(component['primaryActionKey']('Cancelled')).toBeNull();
    expect(component['primaryActionKey']('Interrupted')).toBeNull();
    expect(component['primaryActionKey']('WaitingMaterials')).toBeNull();
    expect(component['primaryActionKey']('WaitingHelp')).toBeNull();
  });

  it('runs Start and reloads on success when the primary action is triggered from Assigned', () => {
    const { component, portalService } = configure({ items: [cleaning], totalCount: 1 });
    const event = { stopPropagation: vi.fn() } as unknown as Event;

    component['runPrimaryAction'](cleaning, event);

    expect(event.stopPropagation).toHaveBeenCalled();
    expect(portalService.start).toHaveBeenCalledWith('c1');
  });
});
