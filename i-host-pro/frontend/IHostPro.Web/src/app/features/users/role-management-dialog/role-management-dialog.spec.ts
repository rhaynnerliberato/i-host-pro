import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { RoleResponse, UserResponse } from '../../../core/api/generated/api-client';
import { RoleManagementDialog, RoleManagementDialogData } from './role-management-dialog';
import { UsersService } from '../users.service';

const roles: RoleResponse[] = [
  { code: 'ADMIN', name: 'Administrador' },
  { code: 'OPERATOR', name: 'Operador' },
];

function configure(user: UserResponse, confirmResult: boolean | undefined = true) {
  const dialogRef = { close: vi.fn() };
  const usersService = { assignRole: vi.fn(), removeRole: vi.fn() };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };
  const nestedDialogRef = { afterClosed: () => of(confirmResult) };
  const dialog = { open: vi.fn().mockReturnValue(nestedDialogRef) };

  const data: RoleManagementDialogData = { user, roles };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: MatDialog, useValue: dialog },
      { provide: UsersService, useValue: usersService },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new RoleManagementDialog());
  return { component, dialogRef, usersService, snackBar, dialog };
}

describe('RoleManagementDialog', () => {
  it('assignableRoles excludes roles the user already holds', () => {
    const { component } = configure({ id: 'u1', roles: ['ADMIN'] } as UserResponse);

    expect(component['assignableRoles']().map((r) => r.code)).toEqual(['OPERATOR']);
  });

  it('assignSelectedRole does nothing when no role is selected', () => {
    const { component, usersService } = configure({ id: 'u1', roles: [] } as unknown as UserResponse);

    component['assignSelectedRole']();

    expect(usersService.assignRole).not.toHaveBeenCalled();
  });

  it('assignSelectedRole calls UsersService.assignRole, adds the role locally, and clears the selection', () => {
    const { component, usersService } = configure({ id: 'u1', roles: [] } as unknown as UserResponse);
    usersService.assignRole.mockReturnValue(of(undefined));
    component['selectedRoleToAdd'].set('OPERATOR');

    component['assignSelectedRole']();

    expect(usersService.assignRole).toHaveBeenCalledWith('u1', { roleCode: 'OPERATOR' });
    expect(component['currentRoles']()).toEqual(['OPERATOR']);
    expect(component['selectedRoleToAdd']()).toBe('');
    expect(component['busy']()).toBe(false);
  });

  it('assignSelectedRole is ignored while a previous call is still in flight (duplicate-submission prevention)', () => {
    const { component, usersService } = configure({ id: 'u1', roles: [] } as unknown as UserResponse);
    component['selectedRoleToAdd'].set('OPERATOR');
    component['busy'].set(true);

    component['assignSelectedRole']();

    expect(usersService.assignRole).not.toHaveBeenCalled();
  });

  it('assignSelectedRole on error clears busy and shows a translated error snackbar', () => {
    const { component, usersService, snackBar } = configure({ id: 'u1', roles: [] } as unknown as UserResponse);
    usersService.assignRole.mockReturnValue(throwError(() => ({ status: 409 })));
    component['selectedRoleToAdd'].set('OPERATOR');

    component['assignSelectedRole']();

    expect(component['busy']()).toBe(false);
    expect(snackBar.open).toHaveBeenCalledWith('users.roles.errors.conflict', undefined, { duration: 4000 });
  });

  it('confirmRemoveRole opens a confirmation dialog and removes the role only when confirmed', () => {
    const { component, usersService, dialog } = configure({ id: 'u1', roles: ['OPERATOR'] } as UserResponse, true);
    usersService.removeRole.mockReturnValue(of(undefined));

    component['confirmRemoveRole']('OPERATOR');

    expect(dialog.open).toHaveBeenCalled();
    expect(usersService.removeRole).toHaveBeenCalledWith('u1', 'OPERATOR');
    expect(component['currentRoles']()).toEqual([]);
  });

  it('confirmRemoveRole does not remove the role when the confirmation is declined', () => {
    const { component, usersService } = configure({ id: 'u1', roles: ['OPERATOR'] } as UserResponse, false);

    component['confirmRemoveRole']('OPERATOR');

    expect(usersService.removeRole).not.toHaveBeenCalled();
    expect(component['currentRoles']()).toEqual(['OPERATOR']);
  });

  it('close() closes the dialog with the current (possibly updated) roles', () => {
    const { component, dialogRef } = configure({ id: 'u1', roles: ['ADMIN'] } as UserResponse);

    component['close']();

    expect(dialogRef.close).toHaveBeenCalledWith(['ADMIN']);
  });
});
