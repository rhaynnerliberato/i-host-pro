import { TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CleaningDetailResponse } from '../../../core/api/generated/api-client';
import { HousekeepingService } from '../housekeeping.service';
import { CleaningFormDialog } from './cleaning-form-dialog';

function configure() {
  const dialogRef = { close: vi.fn() };
  const housekeepingService = { create: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: HousekeepingService, useValue: housekeepingService },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new CleaningFormDialog());
  return { component, dialogRef, housekeepingService };
}

describe('CleaningFormDialog', () => {
  it('requires propertyId, reservationId is optional', () => {
    const { component } = configure();

    expect(component['form'].valid).toBe(false);
    component['form'].patchValue({ propertyId: 'p1' });
    expect(component['form'].valid).toBe(true);
  });

  it('submit() blocks and touches every field when the form is invalid', () => {
    const { component, housekeepingService } = configure();

    component['submit']();

    expect(housekeepingService.create).not.toHaveBeenCalled();
    expect(component['form'].get('propertyId')?.touched).toBe(true);
  });

  it('submit() sends propertyId and omits reservationId when blank', () => {
    const created = { id: 'c1' } as CleaningDetailResponse;
    const { component, dialogRef, housekeepingService } = configure();
    housekeepingService.create.mockReturnValue(of(created));
    component['form'].patchValue({ propertyId: 'p1' });

    component['submit']();

    expect(housekeepingService.create).toHaveBeenCalledWith({ propertyId: 'p1', reservationId: undefined });
    expect(dialogRef.close).toHaveBeenCalledWith(created);
    expect(component['submitting']()).toBe(false);
  });

  it('submit() forwards a real reservationId unchanged', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.create.mockReturnValue(of({} as CleaningDetailResponse));
    component['form'].patchValue({ propertyId: 'p1', reservationId: 'r1' });

    component['submit']();

    expect(housekeepingService.create).toHaveBeenCalledWith({ propertyId: 'p1', reservationId: 'r1' });
  });

  it('submit() sets submitting=true synchronously so a duplicate click before the response is ignored', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.create.mockReturnValue(of({} as CleaningDetailResponse));
    component['form'].patchValue({ propertyId: 'p1' });
    component['submitting'].set(true);

    component['submit']();

    expect(housekeepingService.create).not.toHaveBeenCalled();
  });

  it('submit() on a 404 property_not_found sets the propertyNotFound error key', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.create.mockReturnValue(throwError(() => ({ status: 404, code: 'property_not_found' })));
    component['form'].patchValue({ propertyId: 'p1' });

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.form.errors.propertyNotFound');
    expect(component['submitting']()).toBe(false);
  });

  it('submit() on a 404 reservation_reference_not_available sets the reservationNotFound error key', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.create.mockReturnValue(
      throwError(() => ({ status: 404, code: 'reservation_reference_not_available' })),
    );
    component['form'].patchValue({ propertyId: 'p1', reservationId: 'r1' });

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.form.errors.reservationNotFound');
  });

  it('submit() on any other 404 sets the generic notFound error key', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.create.mockReturnValue(throwError(() => ({ status: 404 })));
    component['form'].patchValue({ propertyId: 'p1' });

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.form.errors.notFound');
  });

  it('submit() on a 400 sets the validation error key', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.create.mockReturnValue(throwError(() => ({ status: 400, codes: ['property_id_required'] })));
    component['form'].patchValue({ propertyId: 'p1' });

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.form.errors.validation');
  });

  it('submit() on any other error sets the generic error key', () => {
    const { component, housekeepingService } = configure();
    housekeepingService.create.mockReturnValue(throwError(() => ({ status: 500 })));
    component['form'].patchValue({ propertyId: 'p1' });

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.form.errors.generic');
  });

  it('cancel() closes the dialog with no result', () => {
    const { component, dialogRef } = configure();

    component['cancel']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
