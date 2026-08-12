import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CleaningDetailResponse } from '../../../core/api/generated/api-client';
import { PortalService } from '../portal.service';
import { MyCleaningDetail } from './my-cleaning-detail';

function configure(cleaning: CleaningDetailResponse = { id: 'c1', propertyId: 'p1', status: 'Assigned' } as CleaningDetailResponse) {
  const portalService = {
    getMyCleaningById: vi.fn().mockReturnValue(of(cleaning)),
    listOccurrences: vi.fn().mockReturnValue(of([])),
    getChecklist: vi.fn().mockReturnValue(of([])),
    markInTransit: vi.fn().mockReturnValue(of({})),
    start: vi.fn().mockReturnValue(of({})),
    startInspection: vi.fn().mockReturnValue(of({})),
    complete: vi.fn().mockReturnValue(of({})),
    markWaitingMaterials: vi.fn().mockReturnValue(of({})),
    markWaitingHelp: vi.fn().mockReturnValue(of({})),
    reportDelay: vi.fn().mockReturnValue(of({})),
    registerOccurrence: vi.fn().mockReturnValue(of({})),
    setChecklistItem: vi.fn().mockReturnValue(of({})),
  };
  const router = { navigate: vi.fn() };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };
  const activatedRoute = { snapshot: { paramMap: { get: () => 'c1' } } };

  TestBed.configureTestingModule({
    providers: [
      { provide: PortalService, useValue: portalService },
      { provide: Router, useValue: router },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
      { provide: ActivatedRoute, useValue: activatedRoute },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new MyCleaningDetail());
  return { component, portalService, router, snackBar };
}

describe('MyCleaningDetail', () => {
  it('loads the cleaning, occurrences, and checklist on construction', () => {
    const { component, portalService } = configure();

    expect(portalService.getMyCleaningById).toHaveBeenCalledWith('c1');
    expect(portalService.listOccurrences).toHaveBeenCalledWith('c1');
    expect(portalService.getChecklist).toHaveBeenCalledWith('c1');
    expect(component['state']()).toBe('loaded');
  });

  it('navigates back to the list', () => {
    const { component, router } = configure();

    component['goBack']();

    expect(router.navigate).toHaveBeenCalledWith(['/my-cleanings']);
  });

  describe('lifecycle action visibility mirrors the real domain guards', () => {
    it('Assigned: can mark in transit and start, cannot start inspection/complete', () => {
      const { component } = configure({ id: 'c1', propertyId: 'p1', status: 'Assigned' } as CleaningDetailResponse);

      expect(component['canMarkInTransit']()).toBe(true);
      expect(component['canStart']()).toBe(true);
      expect(component['canStartInspection']()).toBe(false);
      expect(component['canComplete']()).toBe(false);
    });

    it('InTransit: can start but not mark in transit again', () => {
      const { component } = configure({ id: 'c1', propertyId: 'p1', status: 'InTransit' } as CleaningDetailResponse);

      expect(component['canMarkInTransit']()).toBe(false);
      expect(component['canStart']()).toBe(true);
    });

    it('Started: can start inspection and mark waiting materials/help, cannot complete', () => {
      const { component } = configure({ id: 'c1', propertyId: 'p1', status: 'Started' } as CleaningDetailResponse);

      expect(component['canStartInspection']()).toBe(true);
      expect(component['canMarkWaitingMaterials']()).toBe(true);
      expect(component['canMarkWaitingHelp']()).toBe(true);
      expect(component['canComplete']()).toBe(false);
    });

    it('InInspection: can complete only', () => {
      const { component } = configure({ id: 'c1', propertyId: 'p1', status: 'InInspection' } as CleaningDetailResponse);

      expect(component['canComplete']()).toBe(true);
      expect(component['canStart']()).toBe(false);
      expect(component['canStartInspection']()).toBe(false);
    });

    it('Completed is terminal — no delay, no new occurrences', () => {
      const { component } = configure({ id: 'c1', propertyId: 'p1', status: 'Completed' } as CleaningDetailResponse);

      expect(component['isTerminal']()).toBe(true);
    });

    it('Cancelled is terminal — no delay, no new occurrences', () => {
      const { component } = configure({ id: 'c2', propertyId: 'p1', status: 'Cancelled' } as CleaningDetailResponse);

      expect(component['isTerminal']()).toBe(true);
    });

    it('non-terminal statuses allow reporting a delay', () => {
      const { component } = configure({ id: 'c1', propertyId: 'p1', status: 'Started' } as CleaningDetailResponse);

      expect(component['isTerminal']()).toBe(false);
    });
  });

  it('submits a valid occurrence and reloads the occurrence list', () => {
    const { component, portalService, snackBar } = configure();
    component['occurrenceForm'].setValue({ type: 'Damage', description: 'Broken lamp' });

    component['submitOccurrence']();

    expect(portalService.registerOccurrence).toHaveBeenCalledWith('c1', 'Damage', 'Broken lamp');
    expect(portalService.listOccurrences).toHaveBeenCalledTimes(2);
    expect(snackBar.open).toHaveBeenCalledWith('portal.detail.occurrences.occurrenceRegistered', undefined, { duration: 3000 });
  });

  it('does not submit an occurrence with no type selected', () => {
    const { component, portalService } = configure();
    component['occurrenceForm'].setValue({ type: '', description: '' });

    component['submitOccurrence']();

    expect(portalService.registerOccurrence).not.toHaveBeenCalled();
    expect(component['occurrenceForm'].touched).toBe(true);
  });

  it('toggles a checklist item and reloads the checklist', () => {
    const { component, portalService } = configure();

    component['toggleChecklistItem']('Stove', true);

    expect(portalService.setChecklistItem).toHaveBeenCalledWith('c1', 'Stove', true);
    expect(portalService.getChecklist).toHaveBeenCalledTimes(2);
  });

  it('shows a generic error and does not reload when a lifecycle action fails', () => {
    const portalService = {
      getMyCleaningById: vi.fn().mockReturnValue(of({ id: 'c1', propertyId: 'p1', status: 'Assigned' } as CleaningDetailResponse)),
      listOccurrences: vi.fn().mockReturnValue(of([])),
      getChecklist: vi.fn().mockReturnValue(of([])),
      start: vi.fn().mockReturnValue(throwError(() => ({ status: 409 }))),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: PortalService, useValue: portalService },
        { provide: Router, useValue: { navigate: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (key: string) => key } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 'c1' } } } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new MyCleaningDetail());
    const snackBar = TestBed.inject(MatSnackBar) as unknown as { open: ReturnType<typeof vi.fn> };

    component['start']();

    expect(snackBar.open).toHaveBeenCalledWith('portal.detail.errors.conflict', undefined, { duration: 4000 });
    expect(portalService.getMyCleaningById).toHaveBeenCalledTimes(1);
  });
});
