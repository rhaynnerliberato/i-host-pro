import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CondominiumDetailResponse } from '../../../../core/api/generated/api-client';
import { CondominiumsService } from '../../condominiums.service';
import { CondominiumFormDialog, CondominiumFormDialogData } from './condominium-form-dialog';

const validValues = {
  name: 'Edificio Sol',
  zipCode: '01000-000',
  street: 'Rua A',
  number: '10',
  complement: '',
  neighborhood: 'Centro',
  city: 'Sao Paulo',
  state: 'SP',
  country: 'BR',
};

function configure(data: CondominiumFormDialogData) {
  const dialogRef = { close: vi.fn() };
  const condominiumsService = { create: vi.fn(), update: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: CondominiumsService, useValue: condominiumsService },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new CondominiumFormDialog());
  return { component, dialogRef, condominiumsService };
}

describe('CondominiumFormDialog', () => {
  it('create mode requires name and every mandatory address field except complement', () => {
    const { component } = configure({});

    expect(component['isEditMode']).toBe(false);
    expect(component['form'].valid).toBe(false);
    component['form'].setValue(validValues);
    expect(component['form'].valid).toBe(true);
  });

  it('edit mode pre-fills name and address from the given condominium', () => {
    const condominium = {
      id: 'c1',
      name: 'Edificio Sol',
      address: { zipCode: '01000-000', street: 'Rua A', number: '10', neighborhood: 'Centro', city: 'Sao Paulo', state: 'SP', country: 'BR' },
    } as CondominiumDetailResponse;
    const { component } = configure({ condominium });

    expect(component['isEditMode']).toBe(true);
    expect(component['form'].getRawValue()).toEqual({ ...validValues, complement: '' });
  });

  it('submit() does nothing and marks all fields touched when the form is invalid', () => {
    const { component, condominiumsService } = configure({});

    component['submit']();

    expect(condominiumsService.create).not.toHaveBeenCalled();
    expect(component['form'].get('name')?.touched).toBe(true);
  });

  it('submit() in create mode calls CondominiumsService.create with name and address, omitting an empty complement', () => {
    const created = { id: 'c1' } as CondominiumDetailResponse;
    const { component, dialogRef, condominiumsService } = configure({});
    condominiumsService.create.mockReturnValue(of(created));
    component['form'].setValue(validValues);

    component['submit']();

    expect(condominiumsService.create).toHaveBeenCalledWith({
      name: 'Edificio Sol',
      address: { zipCode: '01000-000', street: 'Rua A', number: '10', complement: undefined, neighborhood: 'Centro', city: 'Sao Paulo', state: 'SP', country: 'BR' },
    });
    expect(dialogRef.close).toHaveBeenCalledWith(created);
    expect(component['submitting']()).toBe(false);
  });

  it('submit() in edit mode calls CondominiumsService.update with the condominium id', () => {
    const condominium = { id: 'c1', name: 'Edificio Sol', address: { zipCode: '01000-000', street: 'Rua A', number: '10', neighborhood: 'Centro', city: 'Sao Paulo', state: 'SP', country: 'BR' } } as CondominiumDetailResponse;
    const updated = { id: 'c1', name: 'Edificio Lua' } as CondominiumDetailResponse;
    const { component, dialogRef, condominiumsService } = configure({ condominium });
    condominiumsService.update.mockReturnValue(of(updated));
    component['form'].patchValue({ name: 'Edificio Lua' });

    component['submit']();

    expect(condominiumsService.update).toHaveBeenCalledWith('c1', expect.objectContaining({ name: 'Edificio Lua' }));
    expect(dialogRef.close).toHaveBeenCalledWith(updated);
  });

  it('submit() sets submitting=true synchronously so a duplicate click before the response is ignored', () => {
    const { component, condominiumsService } = configure({});
    condominiumsService.create.mockReturnValue(of({} as CondominiumDetailResponse));
    component['form'].setValue(validValues);
    component['submitting'].set(true);

    component['submit']();

    expect(condominiumsService.create).not.toHaveBeenCalled();
  });

  it('submit() on a 409 conflict sets the conflict error key and stops submitting', () => {
    const { component, condominiumsService } = configure({});
    condominiumsService.create.mockReturnValue(throwError(() => ({ status: 409 })));
    component['form'].setValue(validValues);

    component['submit']();

    expect(component['errorKey']()).toBe('propertyManagement.condominiums.form.errors.conflict');
    expect(component['submitting']()).toBe(false);
  });

  it('submit() on a 400 validation error sets the validation error key', () => {
    const { component, condominiumsService } = configure({});
    condominiumsService.create.mockReturnValue(throwError(() => ({ status: 400, codes: ['name_required'] })));
    component['form'].setValue(validValues);

    component['submit']();

    expect(component['errorKey']()).toBe('propertyManagement.condominiums.form.errors.validation');
  });

  it('submit() on any other error sets the generic error key', () => {
    const { component, condominiumsService } = configure({});
    condominiumsService.create.mockReturnValue(throwError(() => ({ status: 500 })));
    component['form'].setValue(validValues);

    component['submit']();

    expect(component['errorKey']()).toBe('propertyManagement.condominiums.form.errors.generic');
  });

  it('cancel() closes the dialog with no result', () => {
    const { component, dialogRef } = configure({});

    component['cancel']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
