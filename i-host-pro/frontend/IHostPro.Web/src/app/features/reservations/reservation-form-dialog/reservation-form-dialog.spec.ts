import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { ReservationDetailResponse } from '../../../core/api/generated/api-client';
import { ReservationsService } from '../reservations.service';
import { ReservationFormDialog, ReservationFormDialogData } from './reservation-form-dialog';

function configure(data: ReservationFormDialogData) {
  const dialogRef = { close: vi.fn() };
  const reservationsService = { create: vi.fn(), update: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: ReservationsService, useValue: reservationsService },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new ReservationFormDialog());
  return { component, dialogRef, reservationsService };
}

describe('ReservationFormDialog', () => {
  it('create mode requires propertyId, guestName, checkInAt, checkOutAt and a guestCount of at least 1', () => {
    const { component } = configure({});

    expect(component['isEditMode']).toBe(false);
    expect(component['form'].valid).toBe(false);
    component['form'].patchValue({
      propertyId: 'p1',
      guestName: 'Guest',
      checkInAt: '2026-08-01T14:00',
      checkOutAt: '2026-08-05T11:00',
      guestCount: 2,
    });
    expect(component['form'].valid).toBe(true);
  });

  it('edit mode pre-fills every field from a real Date instance into the same local datetime-local string ("exibição consistente")', () => {
    const property = {
      id: 'r1',
      propertyId: 'p1',
      guestName: 'Guest',
      guestPhone: '+55 11 90000-0000',
      checkInAt: new Date(2026, 7, 1, 14, 0),
      checkOutAt: new Date(2026, 7, 5, 11, 0),
      guestCount: 3,
    } as ReservationDetailResponse;
    const { component } = configure({ reservation: property });

    expect(component['isEditMode']).toBe(true);
    expect(component['form'].getRawValue()).toMatchObject({
      propertyId: 'p1',
      guestName: 'Guest',
      guestPhone: '+55 11 90000-0000',
      checkInAt: '2026-08-01T14:00',
      checkOutAt: '2026-08-05T11:00',
      guestCount: 3,
    });
  });

  it('edit mode pre-fills identically from a plain ISO string without an explicit offset (the generated Client leaves jsonParseReviver unset, so dates arrive as strings at runtime — same display either way)', () => {
    const property = {
      id: 'r1',
      propertyId: 'p1',
      guestName: 'Guest',
      checkInAt: '2026-08-01T14:00:00' as unknown as Date,
      checkOutAt: '2026-08-05T11:00:00' as unknown as Date,
      guestCount: 1,
    } as ReservationDetailResponse;
    const { component } = configure({ reservation: property });

    expect(component['form'].getRawValue().checkInAt).toBe('2026-08-01T14:00');
    expect(component['form'].getRawValue().checkOutAt).toBe('2026-08-05T11:00');
  });

  it('submit() blocks and touches every field when the form is invalid', () => {
    const { component, reservationsService } = configure({});

    component['submit']();

    expect(reservationsService.create).not.toHaveBeenCalled();
    expect(component['form'].get('propertyId')?.touched).toBe(true);
  });

  it('submit() blocks a check-out on/before check-in without calling the service ("intervalo inválido") — presentation only, mirrors the backend rule', () => {
    const { component, reservationsService } = configure({});
    component['form'].patchValue({
      propertyId: 'p1',
      guestName: 'Guest',
      checkInAt: '2026-08-05T14:00',
      checkOutAt: '2026-08-05T14:00',
      guestCount: 2,
    });

    component['submit']();

    expect(component['errorKey']()).toBe('reservations.form.errors.checkOutBeforeCheckIn');
    expect(reservationsService.create).not.toHaveBeenCalled();
  });

  it('submit() in create mode sends real Date instances for checkInAt/checkOutAt built from the local datetime-local value, and omits guestPhone when blank ("datas locais válidas", "serialização com offset", "ausência de offset nunca enviada" — a genuine Date always serializes with an explicit Z offset downstream, never a bare string)', () => {
    const created = { id: 'r1' } as ReservationDetailResponse;
    const { component, dialogRef, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(of(created));
    component['form'].patchValue({
      propertyId: 'p1',
      guestName: 'Guest',
      checkInAt: '2026-08-01T14:00',
      checkOutAt: '2026-08-05T11:00',
      guestCount: 2,
    });

    component['submit']();

    expect(reservationsService.create).toHaveBeenCalledTimes(1);
    const request = reservationsService.create.mock.calls[0][0];
    expect(request.propertyId).toBe('p1');
    expect(request.guestName).toBe('Guest');
    expect(request.guestPhone).toBeUndefined();
    expect(request.checkInAt).toBeInstanceOf(Date);
    expect(request.checkOutAt).toBeInstanceOf(Date);
    expect(request.checkInAt.getTime()).toBe(new Date(2026, 7, 1, 14, 0).getTime());
    expect(request.checkOutAt.getTime()).toBe(new Date(2026, 7, 5, 11, 0).getTime());
    expect(dialogRef.close).toHaveBeenCalledWith(created);
    expect(component['submitting']()).toBe(false);
  });

  it('submit() in create mode forwards a real guestPhone unchanged', () => {
    const { component, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(of({} as ReservationDetailResponse));
    component['form'].patchValue({
      propertyId: 'p1',
      guestName: 'Guest',
      guestPhone: '+55 11 90000-0000',
      checkInAt: '2026-08-01T14:00',
      checkOutAt: '2026-08-05T11:00',
      guestCount: 2,
    });

    component['submit']();

    expect(reservationsService.create).toHaveBeenCalledWith(expect.objectContaining({ guestPhone: '+55 11 90000-0000' }));
  });

  it('submit() in edit mode calls ReservationsService.update with the reservation id, sending explicit null for a cleared guestPhone', () => {
    const reservation = {
      id: 'r1',
      propertyId: 'p1',
      guestName: 'Guest',
      guestPhone: '+55 11 90000-0000',
      checkInAt: new Date(2026, 7, 1, 14, 0),
      checkOutAt: new Date(2026, 7, 5, 11, 0),
      guestCount: 2,
    } as ReservationDetailResponse;
    const updated = { id: 'r1', guestName: 'Guest Edited' } as ReservationDetailResponse;
    const { component, dialogRef, reservationsService } = configure({ reservation });
    reservationsService.update.mockReturnValue(of(updated));
    component['form'].patchValue({ guestName: 'Guest Edited', guestPhone: '' });

    component['submit']();

    expect(reservationsService.update).toHaveBeenCalledTimes(1);
    const [reservationId, values] = reservationsService.update.mock.calls[0];
    expect(reservationId).toBe('r1');
    expect(values.guestName).toBe('Guest Edited');
    expect(values.guestPhone).toBeNull();
    expect(dialogRef.close).toHaveBeenCalledWith(updated);
  });

  it('submit() sets submitting=true synchronously so a duplicate click before the response is ignored', () => {
    const { component, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(of({} as ReservationDetailResponse));
    component['form'].patchValue({
      propertyId: 'p1',
      guestName: 'Guest',
      checkInAt: '2026-08-01T14:00',
      checkOutAt: '2026-08-05T11:00',
      guestCount: 2,
    });
    component['submitting'].set(true);

    component['submit']();

    expect(reservationsService.create).not.toHaveBeenCalled();
  });

  it('submit() on a 404 (property or reservation not found) sets the notFound error key', () => {
    const { component, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(throwError(() => ({ status: 404 })));
    component['form'].patchValue({ propertyId: 'p1', guestName: 'Guest', checkInAt: '2026-08-01T14:00', checkOutAt: '2026-08-05T11:00', guestCount: 2 });

    component['submit']();

    expect(component['errorKey']()).toBe('reservations.form.errors.notFound');
    expect(component['submitting']()).toBe(false);
  });

  it('submit() on a 409 (schedule conflict, already cancelled, or concurrent edit — never further distinguished) sets the conflict error key', () => {
    const { component, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(throwError(() => ({ status: 409 })));
    component['form'].patchValue({ propertyId: 'p1', guestName: 'Guest', checkInAt: '2026-08-01T14:00', checkOutAt: '2026-08-05T11:00', guestCount: 2 });

    component['submit']();

    expect(component['errorKey']()).toBe('reservations.form.errors.conflict');
  });

  it('submit() on a 400 with code Reservations.PropertyNotActive sets the propertyNotActive error key', () => {
    const { component, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(throwError(() => ({ status: 400, codes: ['Reservations.PropertyNotActive'] })));
    component['form'].patchValue({ propertyId: 'p1', guestName: 'Guest', checkInAt: '2026-08-01T14:00', checkOutAt: '2026-08-05T11:00', guestCount: 2 });

    component['submit']();

    expect(component['errorKey']()).toBe('reservations.form.errors.propertyNotActive');
  });

  it('submit() on a 400 with code Reservations.PropertyCapacityExceeded sets the capacityExceeded error key ("capacidade")', () => {
    const { component, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(throwError(() => ({ status: 400, codes: ['Reservations.PropertyCapacityExceeded'] })));
    component['form'].patchValue({ propertyId: 'p1', guestName: 'Guest', checkInAt: '2026-08-01T14:00', checkOutAt: '2026-08-05T11:00', guestCount: 5 });

    component['submit']();

    expect(component['errorKey']()).toBe('reservations.form.errors.capacityExceeded');
  });

  it('submit() on any other 400 sets the generic validation error key', () => {
    const { component, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(throwError(() => ({ status: 400, codes: ['SomeOtherCode'] })));
    component['form'].patchValue({ propertyId: 'p1', guestName: 'Guest', checkInAt: '2026-08-01T14:00', checkOutAt: '2026-08-05T11:00', guestCount: 2 });

    component['submit']();

    expect(component['errorKey']()).toBe('reservations.form.errors.validation');
  });

  it('submit() on any other error sets the generic error key', () => {
    const { component, reservationsService } = configure({});
    reservationsService.create.mockReturnValue(throwError(() => ({ status: 500 })));
    component['form'].patchValue({ propertyId: 'p1', guestName: 'Guest', checkInAt: '2026-08-01T14:00', checkOutAt: '2026-08-05T11:00', guestCount: 2 });

    component['submit']();

    expect(component['errorKey']()).toBe('reservations.form.errors.generic');
  });

  it('cancel() closes the dialog with no result', () => {
    const { component, dialogRef } = configure({});

    component['cancel']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
