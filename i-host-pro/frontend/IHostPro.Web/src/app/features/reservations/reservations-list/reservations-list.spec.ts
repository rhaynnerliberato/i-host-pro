import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { PagedReservationResponse, ReservationDetailResponse, ReservationSummaryResponse } from '../../../core/api/generated/api-client';
import { ReservationsService } from '../reservations.service';
import { ReservationsList } from './reservations-list';

function configure(listResult: PagedReservationResponse = { items: [], totalCount: 0 }) {
  const reservationsService = {
    list: vi.fn().mockReturnValue(of(listResult)),
    getById: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    cancel: vi.fn(),
  };
  const nestedDialogRef = { afterClosed: () => of(undefined) };
  const dialog = { open: vi.fn().mockReturnValue(nestedDialogRef) };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };

  TestBed.configureTestingModule({
    providers: [
      { provide: ReservationsService, useValue: reservationsService },
      { provide: MatDialog, useValue: dialog },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new ReservationsList());
  return { component, reservationsService, dialog, snackBar };
}

describe('ReservationsList', () => {
  const reservation: ReservationSummaryResponse = {
    id: 'r1',
    propertyId: 'p1',
    guestName: 'Guest',
    guestCount: 2,
    status: 'confirmed',
    checkInAt: new Date('2026-08-01T14:00:00Z'),
    checkOutAt: new Date('2026-08-05T11:00:00Z'),
  } as ReservationSummaryResponse;

  it('loads reservations on construction with no filters, ending in the loaded state when items exist', () => {
    const { component, reservationsService } = configure({ items: [reservation], totalCount: 1 });

    expect(reservationsService.list).toHaveBeenCalledWith(1, 10, undefined, undefined, undefined, undefined);
    expect(component['state']()).toBe('loaded');
    expect(component['reservations']()).toEqual([reservation]);
    expect(component['totalCount']()).toBe(1);
  });

  it('ends in the empty state when the page has zero items', () => {
    const { component } = configure({ items: [], totalCount: 0 });
    expect(component['state']()).toBe('empty');
  });

  it('ends in the error state when the list request fails', () => {
    const reservationsService = { list: vi.fn().mockReturnValue(throwError(() => new Error('boom'))), getById: vi.fn(), create: vi.fn(), update: vi.fn(), cancel: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: ReservationsService, useValue: reservationsService },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (k: string) => k } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new ReservationsList());

    expect(component['state']()).toBe('error');
  });

  it('applyFilters resets the page index to 0 and reloads with the current form values', () => {
    const { component, reservationsService } = configure();
    reservationsService.list.mockClear();
    component['pageIndex'].set(3);
    component['filterForm'].patchValue({ propertyId: 'p1', status: 'confirmed', from: '2026-08-01', to: '2026-08-10' });

    component['applyFilters']();

    expect(component['pageIndex']()).toBe(0);
    expect(reservationsService.list).toHaveBeenCalledWith(1, 10, 'p1', 'confirmed', new Date('2026-08-01'), new Date('2026-08-10'));
  });

  it('onPageChange updates the page index/size and reloads', () => {
    const { component, reservationsService } = configure();
    reservationsService.list.mockClear();

    component['onPageChange']({ pageIndex: 2, pageSize: 25, length: 100 });

    expect(component['pageIndex']()).toBe(2);
    expect(component['pageSize']()).toBe(25);
    expect(reservationsService.list).toHaveBeenCalledWith(3, 25, undefined, undefined, undefined, undefined);
  });

  it('statusLabelKey maps confirmed/cancelled to their translation keys', () => {
    const { component } = configure();
    expect(component['statusLabelKey']('confirmed')).toBe('reservations.list.statusConfirmed');
    expect(component['statusLabelKey']('cancelled')).toBe('reservations.list.statusCancelled');
  });

  it('canEdit/canCancel only allow the confirmed state — a cancelled reservation is terminal (mirrors the backend guard)', () => {
    const { component } = configure();

    expect(component['canEdit']('confirmed')).toBe(true);
    expect(component['canEdit']('cancelled')).toBe(false);
    expect(component['canEdit'](undefined)).toBe(false);

    expect(component['canCancel']('confirmed')).toBe(true);
    expect(component['canCancel']('cancelled')).toBe(false);
    expect(component['canCancel'](undefined)).toBe(false);
  });

  it('openCreateDialog shows a success snackbar and reloads only when a reservation was created', () => {
    const created = { id: 'r2' } as ReservationDetailResponse;
    const { component, dialog, reservationsService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(created) });
    reservationsService.list.mockClear();

    component['openCreateDialog']();

    expect(snackBar.open).toHaveBeenCalledWith('reservations.list.createdSuccess', undefined, { duration: 3000 });
    expect(reservationsService.list).toHaveBeenCalledTimes(1);
  });

  it('openCreateDialog does not reload or notify when the dialog is dismissed without creating a reservation', () => {
    const { component, dialog, reservationsService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    reservationsService.list.mockClear();

    component['openCreateDialog']();

    expect(snackBar.open).not.toHaveBeenCalled();
    expect(reservationsService.list).not.toHaveBeenCalled();
  });

  it('openEditDialog fetches the reservation, opens the dialog pre-filled, and reloads on update', () => {
    const { component, dialog, reservationsService, snackBar } = configure();
    reservationsService.getById.mockReturnValue(of(reservation));
    dialog.open.mockReturnValue({ afterClosed: () => of({ ...reservation, guestName: 'Guest Edited' }) });
    reservationsService.list.mockClear();

    component['openEditDialog']('r1');

    expect(reservationsService.getById).toHaveBeenCalledWith('r1');
    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { reservation } }));
    expect(snackBar.open).toHaveBeenCalledWith('reservations.list.updatedSuccess', undefined, { duration: 3000 });
    expect(reservationsService.list).toHaveBeenCalledTimes(1);
  });

  it('openEditDialog shows a classified error and never opens the dialog when fetching the reservation fails', () => {
    const { component, dialog, reservationsService, snackBar } = configure();
    reservationsService.getById.mockReturnValue(throwError(() => ({ status: 404 })));

    component['openEditDialog']('r1');

    expect(dialog.open).not.toHaveBeenCalled();
    expect(snackBar.open).toHaveBeenCalledWith('reservations.list.errors.notFound', undefined, { duration: 4000 });
  });

  it('confirmCancel opens a confirmation dialog and cancels the reservation only when confirmed', () => {
    const { component, dialog, reservationsService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    reservationsService.cancel.mockReturnValue(of({ ...reservation, status: 'cancelled' }));
    reservationsService.list.mockClear();

    component['confirmCancel'](reservation);

    expect(reservationsService.cancel).toHaveBeenCalledWith('r1');
    expect(snackBar.open).toHaveBeenCalledWith('reservations.list.cancelledSuccess', undefined, { duration: 3000 });
    expect(reservationsService.list).toHaveBeenCalledTimes(1);
  });

  it('confirmCancel does not cancel the reservation when the confirmation is declined', () => {
    const { component, dialog, reservationsService } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(false) });

    component['confirmCancel'](reservation);

    expect(reservationsService.cancel).not.toHaveBeenCalled();
  });

  it('confirmCancel on a repeated cancellation (409, backend rejects cancelling an already-cancelled reservation) shows a classified conflict error and does not reload', () => {
    const { component, dialog, reservationsService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    reservationsService.cancel.mockReturnValue(throwError(() => ({ status: 409 })));
    reservationsService.list.mockClear();

    component['confirmCancel'](reservation);

    expect(snackBar.open).toHaveBeenCalledWith('reservations.list.errors.conflict', undefined, { duration: 4000 });
    expect(reservationsService.list).not.toHaveBeenCalled();
  });
});
