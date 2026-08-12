import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CleaningDetailResponse, CleaningSummaryResponse, PagedCleaningResponse } from '../../../core/api/generated/api-client';
import { HousekeepingService } from '../housekeeping.service';
import { CleaningsList } from './cleanings-list';

function configure(listResult: PagedCleaningResponse = { items: [], totalCount: 0 }) {
  const housekeepingService = {
    list: vi.fn().mockReturnValue(of(listResult)),
    getById: vi.fn(),
    create: vi.fn(),
    assign: vi.fn(),
    start: vi.fn(),
    startInspection: vi.fn(),
    complete: vi.fn(),
    cancel: vi.fn(),
    markInterrupted: vi.fn(),
    markWaitingMaterials: vi.fn(),
    markWaitingHelp: vi.fn(),
  };
  const nestedDialogRef = { afterClosed: () => of(undefined) };
  const dialog = { open: vi.fn().mockReturnValue(nestedDialogRef) };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };

  TestBed.configureTestingModule({
    providers: [
      { provide: HousekeepingService, useValue: housekeepingService },
      { provide: MatDialog, useValue: dialog },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new CleaningsList());
  return { component, housekeepingService, dialog, snackBar };
}

describe('CleaningsList', () => {
  const cleaning: CleaningSummaryResponse = {
    id: 'c1',
    propertyId: 'p1',
    reservationId: undefined,
    assignedHousekeeperUserId: undefined,
    status: 'Pending',
    createdAtUtc: new Date('2026-08-01T10:00:00Z'),
  } as CleaningSummaryResponse;

  it('loads cleanings on construction with no filters, ending in the loaded state when items exist', () => {
    const { component, housekeepingService } = configure({ items: [cleaning], totalCount: 1 });

    expect(housekeepingService.list).toHaveBeenCalledWith(1, 10, undefined, undefined, undefined);
    expect(component['state']()).toBe('loaded');
    expect(component['cleanings']()).toEqual([cleaning]);
    expect(component['totalCount']()).toBe(1);
  });

  it('ends in the empty state when the page has zero items', () => {
    const { component } = configure({ items: [], totalCount: 0 });
    expect(component['state']()).toBe('empty');
  });

  it('ends in the error state when the list request fails', () => {
    const housekeepingService = { list: vi.fn().mockReturnValue(throwError(() => new Error('boom'))) };
    TestBed.configureTestingModule({
      providers: [
        { provide: HousekeepingService, useValue: housekeepingService },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (k: string) => k } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new CleaningsList());

    expect(component['state']()).toBe('error');
  });

  it('applyFilters resets the page index to 0 and reloads with the current form values', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.list.mockClear();
    component['pageIndex'].set(3);
    component['filterForm'].patchValue({ status: 'Assigned', propertyId: 'p1', assignedHousekeeperUserId: 'u1' });

    component['applyFilters']();

    expect(component['pageIndex']()).toBe(0);
    expect(housekeepingService.list).toHaveBeenCalledWith(1, 10, 'Assigned', 'p1', 'u1');
  });

  it('onPageChange updates the page index/size and reloads', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.list.mockClear();

    component['onPageChange']({ pageIndex: 2, pageSize: 25, length: 100 });

    expect(component['pageIndex']()).toBe(2);
    expect(component['pageSize']()).toBe(25);
    expect(housekeepingService.list).toHaveBeenCalledWith(3, 25, undefined, undefined, undefined);
  });

  it('statusLabelKey maps a status code to its translation key', () => {
    const { component } = configure();
    expect(component['statusLabelKey']('InInspection')).toBe('housekeeping.list.status.InInspection');
    expect(component['statusLabelKey'](undefined)).toBe('housekeeping.list.status.Pending');
  });

  it('guards mirror the domain state machine exactly (Domain/Cleaning.cs)', () => {
    const { component } = configure();

    expect(component['canAssign']('Pending')).toBe(true);
    expect(component['canAssign']('Assigned')).toBe(false);

    expect(component['canStart']('Assigned')).toBe(true);
    expect(component['canStart']('Pending')).toBe(false);

    expect(component['canStartInspection']('Started')).toBe(true);
    expect(component['canStartInspection']('Assigned')).toBe(false);

    expect(component['canComplete']('InInspection')).toBe(true);
    expect(component['canComplete']('Started')).toBe(false);

    expect(component['canCancel']('Pending')).toBe(true);
    expect(component['canCancel']('Assigned')).toBe(true);
    expect(component['canCancel']('Started')).toBe(false);

    expect(component['canMarkInterrupted']('Started')).toBe(true);
    expect(component['canMarkInterrupted']('InInspection')).toBe(false);

    expect(component['canMarkWaitingMaterials']('Started')).toBe(true);
    expect(component['canMarkWaitingHelp']('Started')).toBe(true);
  });

  it('hasAnyAction is false for every terminal/side-tracked status with no implemented return transition', () => {
    const { component } = configure();

    expect(component['hasAnyAction']('Completed')).toBe(false);
    expect(component['hasAnyAction']('Cancelled')).toBe(false);
    expect(component['hasAnyAction']('Interrupted')).toBe(false);
    expect(component['hasAnyAction']('WaitingMaterials')).toBe(false);
    expect(component['hasAnyAction']('WaitingHelp')).toBe(false);
    expect(component['hasAnyAction']('InTransit')).toBe(false);
    expect(component['hasAnyAction']('Pending')).toBe(true);
    expect(component['hasAnyAction']('Started')).toBe(true);
  });

  it('openCreateDialog shows a success snackbar and reloads only when a cleaning was created', () => {
    const created = { id: 'c2' } as CleaningDetailResponse;
    const { component, dialog, housekeepingService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(created) });
    housekeepingService.list.mockClear();

    component['openCreateDialog']();

    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.createdSuccess', undefined, { duration: 3000 });
    expect(housekeepingService.list).toHaveBeenCalledTimes(1);
  });

  it('openCreateDialog does not reload or notify when the dialog is dismissed without creating a cleaning', () => {
    const { component, dialog, housekeepingService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    housekeepingService.list.mockClear();

    component['openCreateDialog']();

    expect(snackBar.open).not.toHaveBeenCalled();
    expect(housekeepingService.list).not.toHaveBeenCalled();
  });

  it('openDetailDialog fetches the cleaning detail and opens the read-only dialog', () => {
    const detail = { id: 'c1', propertyId: 'p1', status: 'Pending' } as CleaningDetailResponse;
    const { component, dialog, housekeepingService } = configure();
    housekeepingService.getById.mockReturnValue(of(detail));

    component['openDetailDialog']('c1');

    expect(housekeepingService.getById).toHaveBeenCalledWith('c1');
    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { cleaning: detail } }));
  });

  it('openDetailDialog shows a classified error and never opens the dialog when the fetch fails', () => {
    const { component, dialog, housekeepingService, snackBar } = configure();
    housekeepingService.getById.mockReturnValue(throwError(() => ({ status: 404 })));

    component['openDetailDialog']('c1');

    expect(dialog.open).not.toHaveBeenCalled();
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.errors.notFound', undefined, { duration: 4000 });
  });

  it('openAssignDialog reloads and notifies only when the cleaning was actually assigned', () => {
    const assigned = { id: 'c1', status: 'Assigned' } as CleaningDetailResponse;
    const { component, dialog, housekeepingService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(assigned) });
    housekeepingService.list.mockClear();

    component['openAssignDialog'](cleaning);

    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { cleaningId: 'c1' } }));
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.assignedSuccess', undefined, { duration: 3000 });
    expect(housekeepingService.list).toHaveBeenCalledTimes(1);
  });

  it('start/startInspection/complete/markInterrupted/markWaitingMaterials/markWaitingHelp each call their service method, notify, and reload', () => {
    const { component, housekeepingService, snackBar } = configure();
    const response = { id: 'c1' } as CleaningDetailResponse;
    housekeepingService.start.mockReturnValue(of(response));
    housekeepingService.startInspection.mockReturnValue(of(response));
    housekeepingService.complete.mockReturnValue(of(response));
    housekeepingService.markInterrupted.mockReturnValue(of(response));
    housekeepingService.markWaitingMaterials.mockReturnValue(of(response));
    housekeepingService.markWaitingHelp.mockReturnValue(of(response));
    housekeepingService.list.mockClear();

    component['start'](cleaning);
    component['startInspection'](cleaning);
    component['complete'](cleaning);
    component['markInterrupted'](cleaning);
    component['markWaitingMaterials'](cleaning);
    component['markWaitingHelp'](cleaning);

    expect(housekeepingService.start).toHaveBeenCalledWith('c1');
    expect(housekeepingService.startInspection).toHaveBeenCalledWith('c1');
    expect(housekeepingService.complete).toHaveBeenCalledWith('c1');
    expect(housekeepingService.markInterrupted).toHaveBeenCalledWith('c1');
    expect(housekeepingService.markWaitingMaterials).toHaveBeenCalledWith('c1');
    expect(housekeepingService.markWaitingHelp).toHaveBeenCalledWith('c1');
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.startedSuccess', undefined, { duration: 3000 });
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.inspectionStartedSuccess', undefined, { duration: 3000 });
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.completedSuccess', undefined, { duration: 3000 });
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.interruptedSuccess', undefined, { duration: 3000 });
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.waitingMaterialsSuccess', undefined, { duration: 3000 });
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.waitingHelpSuccess', undefined, { duration: 3000 });
    expect(housekeepingService.list).toHaveBeenCalledTimes(6);
  });

  it('a lifecycle action on an invalid transition (409) shows a classified conflict error and does not reload', () => {
    const { component, housekeepingService, snackBar } = configure();
    housekeepingService.start.mockReturnValue(throwError(() => ({ status: 409, code: 'invalid_cleaning_transition' })));
    housekeepingService.list.mockClear();

    component['start'](cleaning);

    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.errors.conflict', undefined, { duration: 4000 });
    expect(housekeepingService.list).not.toHaveBeenCalled();
  });

  it('a lifecycle action on a lost optimistic concurrency race (409, cleaning_concurrency_conflict) shows the concurrency-specific error', () => {
    const { component, housekeepingService, snackBar } = configure();
    housekeepingService.start.mockReturnValue(throwError(() => ({ status: 409, code: 'cleaning_concurrency_conflict' })));

    component['start'](cleaning);

    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.errors.concurrency', undefined, { duration: 4000 });
  });

  it('a lifecycle action rejected for an ineligible housekeeper (403) shows the forbidden error', () => {
    const { component, housekeepingService, snackBar } = configure();
    housekeepingService.start.mockReturnValue(throwError(() => ({ status: 403, code: 'housekeeper_not_eligible' })));

    component['start'](cleaning);

    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.errors.forbidden', undefined, { duration: 4000 });
  });

  it('confirmCancel opens a confirmation dialog and cancels the cleaning only when confirmed', () => {
    const { component, dialog, housekeepingService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    housekeepingService.cancel.mockReturnValue(of({ ...cleaning, status: 'Cancelled' } as CleaningDetailResponse));
    housekeepingService.list.mockClear();

    component['confirmCancel'](cleaning);

    expect(housekeepingService.cancel).toHaveBeenCalledWith('c1');
    expect(snackBar.open).toHaveBeenCalledWith('housekeeping.list.cancelledSuccess', undefined, { duration: 3000 });
    expect(housekeepingService.list).toHaveBeenCalledTimes(1);
  });

  it('confirmCancel does not cancel the cleaning when the confirmation is declined', () => {
    const { component, dialog, housekeepingService } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(false) });

    component['confirmCancel'](cleaning);

    expect(housekeepingService.cancel).not.toHaveBeenCalled();
  });
});
