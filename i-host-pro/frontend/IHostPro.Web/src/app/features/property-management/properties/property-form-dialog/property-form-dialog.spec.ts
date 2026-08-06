import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CondominiumSummaryResponse, PropertyDetailResponse } from '../../../../core/api/generated/api-client';
import { PropertiesService } from '../../properties.service';
import { PropertyFormDialog, PropertyFormDialogData } from './property-form-dialog';

const condominiums: CondominiumSummaryResponse[] = [{ id: 'c1', name: 'Edificio Sol' }];

function configure(data: PropertyFormDialogData) {
  const dialogRef = { close: vi.fn() };
  const propertiesService = { create: vi.fn(), update: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: PropertiesService, useValue: propertiesService },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new PropertyFormDialog());
  return { component, dialogRef, propertiesService };
}

describe('PropertyFormDialog', () => {
  it('create mode requires code, name and a capacity of at least 1, and does not require an address', () => {
    const { component } = configure({ condominiums });

    expect(component['isEditMode']).toBe(false);
    expect(component['form'].valid).toBe(false);
    component['form'].patchValue({ code: 'APT-1', name: 'Apto 1', capacity: 2 });
    expect(component['form'].valid).toBe(true);
  });

  it('edit mode pre-fills code/name/capacity/condominiumId and turns hasOwnAddress on only when the property already has its own address', () => {
    const property = { id: 'p1', code: 'APT-1', name: 'Apto 1', capacity: 4, condominiumId: 'c1', address: { zipCode: '01000-000', street: 'Rua A', number: '10', neighborhood: 'Centro', city: 'SP', state: 'SP', country: 'BR' } } as PropertyDetailResponse;
    const { component } = configure({ property, condominiums });

    expect(component['isEditMode']).toBe(true);
    expect(component['hasOwnAddress']()).toBe(true);
    expect(component['form'].getRawValue()).toMatchObject({ code: 'APT-1', name: 'Apto 1', capacity: 4, condominiumId: 'c1' });
  });

  it('edit mode leaves hasOwnAddress off when the property has no address of its own', () => {
    const property = { id: 'p1', code: 'APT-1', name: 'Apto 1', capacity: 4, condominiumId: 'c1' } as PropertyDetailResponse;
    const { component } = configure({ property, condominiums });

    expect(component['hasOwnAddress']()).toBe(false);
  });

  it('toggleOwnAddress updates the hasOwnAddress signal', () => {
    const { component } = configure({ condominiums });

    component['toggleOwnAddress'](true);
    expect(component['hasOwnAddress']()).toBe(true);

    component['toggleOwnAddress'](false);
    expect(component['hasOwnAddress']()).toBe(false);
  });

  it('submit() blocks and touches every field when hasOwnAddress is on but the address fields are incomplete', () => {
    const { component, propertiesService } = configure({ condominiums });
    component['form'].patchValue({ code: 'APT-1', name: 'Apto 1', capacity: 2 });
    component['toggleOwnAddress'](true);

    component['submit']();

    expect(propertiesService.create).not.toHaveBeenCalled();
    expect(component['form'].get('zipCode')?.touched).toBe(true);
  });

  it('submit() in create mode without an own address sends address: undefined and condominiumId only when selected', () => {
    const created = { id: 'p1' } as PropertyDetailResponse;
    const { component, dialogRef, propertiesService } = configure({ condominiums });
    propertiesService.create.mockReturnValue(of(created));
    component['form'].patchValue({ code: 'APT-1', name: 'Apto 1', capacity: 2, condominiumId: 'c1' });

    component['submit']();

    expect(propertiesService.create).toHaveBeenCalledWith({ code: 'APT-1', name: 'Apto 1', capacity: 2, condominiumId: 'c1', address: undefined });
    expect(dialogRef.close).toHaveBeenCalledWith(created);
    expect(component['submitting']()).toBe(false);
  });

  it('submit() in create mode with an own address sends the full address and omits an empty complement', () => {
    const created = { id: 'p1' } as PropertyDetailResponse;
    const { component, propertiesService } = configure({ condominiums });
    propertiesService.create.mockReturnValue(of(created));
    component['form'].patchValue({
      code: 'APT-1',
      name: 'Apto 1',
      capacity: 2,
      zipCode: '01000-000',
      street: 'Rua A',
      number: '10',
      neighborhood: 'Centro',
      city: 'SP',
      state: 'SP',
    });
    component['toggleOwnAddress'](true);

    component['submit']();

    expect(propertiesService.create).toHaveBeenCalledWith(
      expect.objectContaining({
        address: { zipCode: '01000-000', street: 'Rua A', number: '10', complement: undefined, neighborhood: 'Centro', city: 'SP', state: 'SP', country: 'BR' },
      }),
    );
  });

  it('submit() in edit mode calls PropertiesService.update with the property id, sending null for an unselected condominium/address', () => {
    const property = { id: 'p1', code: 'APT-1', name: 'Apto 1', capacity: 2 } as PropertyDetailResponse;
    const updated = { id: 'p1', name: 'Apto 1 Renovado' } as PropertyDetailResponse;
    const { component, dialogRef, propertiesService } = configure({ property, condominiums });
    propertiesService.update.mockReturnValue(of(updated));
    component['form'].patchValue({ name: 'Apto 1 Renovado' });

    component['submit']();

    expect(propertiesService.update).toHaveBeenCalledWith('p1', { code: 'APT-1', name: 'Apto 1 Renovado', capacity: 2, condominiumId: null, address: null });
    expect(dialogRef.close).toHaveBeenCalledWith(updated);
  });

  it('submit() sets submitting=true synchronously so a duplicate click before the response is ignored', () => {
    const { component, propertiesService } = configure({ condominiums });
    propertiesService.create.mockReturnValue(of({} as PropertyDetailResponse));
    component['form'].patchValue({ code: 'APT-1', name: 'Apto 1', capacity: 2 });
    component['submitting'].set(true);

    component['submit']();

    expect(propertiesService.create).not.toHaveBeenCalled();
  });

  it('submit() on a 409 conflict sets the conflict error key and stops submitting', () => {
    const { component, propertiesService } = configure({ condominiums });
    propertiesService.create.mockReturnValue(throwError(() => ({ status: 409 })));
    component['form'].patchValue({ code: 'APT-1', name: 'Apto 1', capacity: 2 });

    component['submit']();

    expect(component['errorKey']()).toBe('propertyManagement.properties.form.errors.conflict');
    expect(component['submitting']()).toBe(false);
  });

  it('submit() on a 400 validation error sets the validation error key', () => {
    const { component, propertiesService } = configure({ condominiums });
    propertiesService.create.mockReturnValue(throwError(() => ({ status: 400, codes: ['code_required'] })));
    component['form'].patchValue({ code: 'APT-1', name: 'Apto 1', capacity: 2 });

    component['submit']();

    expect(component['errorKey']()).toBe('propertyManagement.properties.form.errors.validation');
  });

  it('submit() on any other error sets the generic error key', () => {
    const { component, propertiesService } = configure({ condominiums });
    propertiesService.create.mockReturnValue(throwError(() => ({ status: 500 })));
    component['form'].patchValue({ code: 'APT-1', name: 'Apto 1', capacity: 2 });

    component['submit']();

    expect(component['errorKey']()).toBe('propertyManagement.properties.form.errors.generic');
  });

  it('cancel() closes the dialog with no result', () => {
    const { component, dialogRef } = configure({ condominiums });

    component['cancel']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
