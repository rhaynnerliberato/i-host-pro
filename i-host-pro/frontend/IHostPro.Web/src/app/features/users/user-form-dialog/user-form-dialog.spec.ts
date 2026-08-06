import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { RoleResponse, UserResponse } from '../../../core/api/generated/api-client';
import { UserFormDialog, UserFormDialogData } from './user-form-dialog';
import { UsersService } from '../users.service';

const roles: RoleResponse[] = [{ code: 'ADMIN', name: 'Administrador' }];

function configure(data: UserFormDialogData) {
  const dialogRef = { close: vi.fn() };
  const usersService = { create: vi.fn(), update: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: UsersService, useValue: usersService },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new UserFormDialog());
  return { component, dialogRef, usersService };
}

describe('UserFormDialog', () => {
  it('create mode requires fullName, email, initialPassword and roleCode', () => {
    const { component } = configure({ roles });

    expect(component['isEditMode']).toBe(false);
    expect(component['form'].valid).toBe(false);
    component['form'].setValue({ fullName: 'Ada', email: 'ada@example.com', initialPassword: 'password123', roleCode: 'ADMIN' });
    expect(component['form'].valid).toBe(true);
  });

  it('edit mode pre-fills fullName/email and does not require password or role', () => {
    const user = { id: 'u1', fullName: 'Ada', email: 'ada@example.com' } as UserResponse;
    const { component } = configure({ user, roles });

    expect(component['isEditMode']).toBe(true);
    expect(component['form'].getRawValue()).toEqual({ fullName: 'Ada', email: 'ada@example.com', initialPassword: '', roleCode: '' });
    expect(component['form'].valid).toBe(true);
  });

  it('submit() does nothing and marks all fields touched when the form is invalid', () => {
    const { component, usersService } = configure({ roles });
    component['form'].patchValue({ fullName: '' });

    component['submit']();

    expect(usersService.create).not.toHaveBeenCalled();
    expect(component['form'].get('fullName')?.touched).toBe(true);
  });

  it('submit() in create mode calls UsersService.create and closes the dialog with the created user', () => {
    const created = { id: 'u1' } as UserResponse;
    const { component, dialogRef, usersService } = configure({ roles });
    usersService.create.mockReturnValue(of(created));
    component['form'].setValue({ fullName: 'Ada', email: 'ada@example.com', initialPassword: 'password123', roleCode: 'ADMIN' });

    component['submit']();

    expect(usersService.create).toHaveBeenCalledWith({ fullName: 'Ada', email: 'ada@example.com', initialPassword: 'password123', roleCode: 'ADMIN' });
    expect(dialogRef.close).toHaveBeenCalledWith(created);
    expect(component['submitting']()).toBe(false);
  });

  it('submit() in edit mode calls UsersService.update with only fullName/email, never password or role', () => {
    const user = { id: 'u1', fullName: 'Ada', email: 'ada@example.com' } as UserResponse;
    const updated = { id: 'u1', fullName: 'Ada Lovelace' } as UserResponse;
    const { component, dialogRef, usersService } = configure({ user, roles });
    usersService.update.mockReturnValue(of(updated));
    component['form'].patchValue({ fullName: 'Ada Lovelace' });

    component['submit']();

    expect(usersService.update).toHaveBeenCalledWith('u1', { fullName: 'Ada Lovelace', email: 'ada@example.com' });
    expect(dialogRef.close).toHaveBeenCalledWith(updated);
  });

  it('submit() sets submitting=true synchronously so a duplicate click before the response is ignored', () => {
    const { component, usersService } = configure({ roles });
    usersService.create.mockReturnValue(of({} as UserResponse));
    component['form'].setValue({ fullName: 'Ada', email: 'ada@example.com', initialPassword: 'password123', roleCode: 'ADMIN' });
    component['submitting'].set(true);

    component['submit']();

    expect(usersService.create).not.toHaveBeenCalled();
  });

  it('submit() on a 409 conflict sets the conflict error key and stops submitting', () => {
    const { component, usersService } = configure({ roles });
    usersService.create.mockReturnValue(throwError(() => ({ status: 409 })));
    component['form'].setValue({ fullName: 'Ada', email: 'ada@example.com', initialPassword: 'password123', roleCode: 'ADMIN' });

    component['submit']();

    expect(component['errorKey']()).toBe('users.form.errors.conflict');
    expect(component['submitting']()).toBe(false);
  });

  it('submit() on a 400 validation error sets the validation error key', () => {
    const { component, usersService } = configure({ roles });
    usersService.create.mockReturnValue(throwError(() => ({ status: 400, codes: ['EMAIL_INVALID'] })));
    component['form'].setValue({ fullName: 'Ada', email: 'ada@example.com', initialPassword: 'password123', roleCode: 'ADMIN' });

    component['submit']();

    expect(component['errorKey']()).toBe('users.form.errors.validation');
  });

  it('submit() on any other error sets the generic error key', () => {
    const { component, usersService } = configure({ roles });
    usersService.create.mockReturnValue(throwError(() => ({ status: 500 })));
    component['form'].setValue({ fullName: 'Ada', email: 'ada@example.com', initialPassword: 'password123', roleCode: 'ADMIN' });

    component['submit']();

    expect(component['errorKey']()).toBe('users.form.errors.generic');
  });

  it('cancel() closes the dialog with no result', () => {
    const { component, dialogRef } = configure({ roles });

    component['cancel']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
