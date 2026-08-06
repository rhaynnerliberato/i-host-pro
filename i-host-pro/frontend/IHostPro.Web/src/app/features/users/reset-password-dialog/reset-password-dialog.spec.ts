import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { UserResponse } from '../../../core/api/generated/api-client';
import { ResetPasswordDialog, ResetPasswordDialogData } from './reset-password-dialog';
import { UsersService } from '../users.service';

function configure(data: ResetPasswordDialogData) {
  const dialogRef = { close: vi.fn() };
  const usersService = { resetPassword: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: UsersService, useValue: usersService },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new ResetPasswordDialog());
  return { component, dialogRef, usersService };
}

describe('ResetPasswordDialog', () => {
  const user = { id: 'u1' } as UserResponse;

  it('requires a new password of at least 10 characters', () => {
    const { component } = configure({ user });

    component['form'].setValue({ newPassword: 'short' });
    expect(component['form'].valid).toBe(false);

    component['form'].setValue({ newPassword: 'a-long-enough-password' });
    expect(component['form'].valid).toBe(true);
  });

  it('submit() does nothing and marks the field touched when the form is invalid', () => {
    const { component, usersService } = configure({ user });

    component['submit']();

    expect(usersService.resetPassword).not.toHaveBeenCalled();
    expect(component['form'].get('newPassword')?.touched).toBe(true);
  });

  it('submit() calls UsersService.resetPassword with the user id and closes the dialog with true', () => {
    const { component, dialogRef, usersService } = configure({ user });
    usersService.resetPassword.mockReturnValue(of(undefined));
    component['form'].setValue({ newPassword: 'a-long-enough-password' });

    component['submit']();

    expect(usersService.resetPassword).toHaveBeenCalledWith('u1', { newPassword: 'a-long-enough-password' });
    expect(dialogRef.close).toHaveBeenCalledWith(true);
    expect(component['submitting']()).toBe(false);
  });

  it('submit() is ignored while a previous submission is still in flight (duplicate-submission prevention)', () => {
    const { component, usersService } = configure({ user });
    component['form'].setValue({ newPassword: 'a-long-enough-password' });
    component['submitting'].set(true);

    component['submit']();

    expect(usersService.resetPassword).not.toHaveBeenCalled();
  });

  it('submit() on a 409 conflict sets the conflict error key', () => {
    const { component, usersService } = configure({ user });
    usersService.resetPassword.mockReturnValue(throwError(() => ({ status: 409 })));
    component['form'].setValue({ newPassword: 'a-long-enough-password' });

    component['submit']();

    expect(component['errorKey']()).toBe('users.resetPassword.errors.conflict');
    expect(component['submitting']()).toBe(false);
  });

  it('submit() on a 400 validation error sets the validation error key', () => {
    const { component, usersService } = configure({ user });
    usersService.resetPassword.mockReturnValue(throwError(() => ({ status: 400 })));
    component['form'].setValue({ newPassword: 'a-long-enough-password' });

    component['submit']();

    expect(component['errorKey']()).toBe('users.resetPassword.errors.validation');
  });

  it('submit() on any other error sets the generic error key', () => {
    const { component, usersService } = configure({ user });
    usersService.resetPassword.mockReturnValue(throwError(() => ({ status: 500 })));
    component['form'].setValue({ newPassword: 'a-long-enough-password' });

    component['submit']();

    expect(component['errorKey']()).toBe('users.resetPassword.errors.generic');
  });

  it('cancel() closes the dialog with false', () => {
    const { component, dialogRef } = configure({ user });

    component['cancel']();

    expect(dialogRef.close).toHaveBeenCalledWith(false);
  });
});
