import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { PagedUserResponse, RoleResponse, UserResponse } from '../../../core/api/generated/api-client';
import { UsersService } from '../users.service';
import { UsersList } from './users-list';

function configure(rolesResult: RoleResponse[] = [], listResult: PagedUserResponse = { items: [], totalCount: 0 }) {
  const usersService = {
    listRoles: vi.fn().mockReturnValue(of(rolesResult)),
    list: vi.fn().mockReturnValue(of(listResult)),
    block: vi.fn(),
    unblock: vi.fn(),
  };
  const nestedDialogRef = { afterClosed: () => of(undefined) };
  const dialog = { open: vi.fn().mockReturnValue(nestedDialogRef) };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };

  TestBed.configureTestingModule({
    providers: [
      { provide: UsersService, useValue: usersService },
      { provide: MatDialog, useValue: dialog },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new UsersList());
  return { component, usersService, dialog, snackBar };
}

describe('UsersList', () => {
  const user: UserResponse = { id: 'u1', fullName: 'Ada Lovelace', email: 'ada@example.com', status: 'Active', roles: ['ADMIN'] };

  it('loads roles and users on construction, ending in the loaded state when items exist', () => {
    const { component, usersService } = configure([], { items: [user], totalCount: 1 });

    expect(usersService.listRoles).toHaveBeenCalledTimes(1);
    expect(usersService.list).toHaveBeenCalledTimes(1);
    expect(component['state']()).toBe('loaded');
    expect(component['users']()).toEqual([user]);
    expect(component['totalCount']()).toBe(1);
  });

  it('ends in the empty state when the page has zero items', () => {
    const { component } = configure([], { items: [], totalCount: 0 });

    expect(component['state']()).toBe('empty');
  });

  it('ends in the error state when the list request fails', () => {
    const usersService = { listRoles: vi.fn().mockReturnValue(of([])), list: vi.fn().mockReturnValue(throwError(() => new Error('boom'))) };
    TestBed.configureTestingModule({
      providers: [
        { provide: UsersService, useValue: usersService },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (k: string) => k } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new UsersList());

    expect(component['state']()).toBe('error');
  });

  it('a failed role-catalog load never blocks the user list itself', () => {
    const usersService = {
      listRoles: vi.fn().mockReturnValue(throwError(() => new Error('roles down'))),
      list: vi.fn().mockReturnValue(of({ items: [user], totalCount: 1 })),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: UsersService, useValue: usersService },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (k: string) => k } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new UsersList());

    expect(component['state']()).toBe('loaded');
    expect(component['roles']()).toEqual([]);
  });

  it('statusLabelKey maps the Blocked status and defaults everything else to Active', () => {
    const { component } = configure();

    expect(component['statusLabelKey']('Blocked')).toBe('users.list.statusBlocked');
    expect(component['statusLabelKey']('Active')).toBe('users.list.statusActive');
    expect(component['statusLabelKey'](undefined)).toBe('users.list.statusActive');
  });

  it('roleName resolves a code to its catalog display name, falling back to the code itself', () => {
    const { component } = configure([{ code: 'ADMIN', name: 'Administrador' }]);

    expect(component['roleName']('ADMIN')).toBe('Administrador');
    expect(component['roleName']('UNKNOWN')).toBe('UNKNOWN');
  });

  it('onPageChange updates the page index/size and reloads', () => {
    const { component, usersService } = configure();
    usersService.list.mockClear();

    component['onPageChange']({ pageIndex: 2, pageSize: 25, length: 100 });

    expect(component['pageIndex']()).toBe(2);
    expect(component['pageSize']()).toBe(25);
    expect(usersService.list).toHaveBeenCalledWith(3, 25, undefined, undefined);
  });

  it('the status filter reloads immediately (no debounce) and resets to the first page', () => {
    const { component, usersService } = configure();
    component['pageIndex'].set(3);
    usersService.list.mockClear();

    component['statusControl'].setValue(2 as never);

    expect(component['pageIndex']()).toBe(0);
    expect(usersService.list).toHaveBeenCalled();
  });

  it('openCreateDialog shows a success snackbar and reloads only when a user was actually created', () => {
    const created = { id: 'u2' } as UserResponse;
    const { component, dialog, usersService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(created) });
    usersService.list.mockClear();

    component['openCreateDialog']();

    expect(dialog.open).toHaveBeenCalled();
    expect(snackBar.open).toHaveBeenCalledWith('users.list.createdSuccess', undefined, { duration: 3000 });
    expect(usersService.list).toHaveBeenCalledTimes(1);
  });

  it('openCreateDialog does not reload or notify when the dialog is dismissed without creating a user', () => {
    const { component, dialog, usersService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    usersService.list.mockClear();

    component['openCreateDialog']();

    expect(snackBar.open).not.toHaveBeenCalled();
    expect(usersService.list).not.toHaveBeenCalled();
  });

  it('confirmBlock opens a confirmation dialog and blocks the user only when confirmed', () => {
    const { component, dialog, usersService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    usersService.block.mockReturnValue(of(undefined));

    component['confirmBlock'](user);

    expect(usersService.block).toHaveBeenCalledWith('u1');
    expect(snackBar.open).toHaveBeenCalledWith('users.list.blockedSuccess', undefined, { duration: 3000 });
  });

  it('confirmBlock does not block the user when the confirmation is declined', () => {
    const { component, dialog, usersService } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(false) });

    component['confirmBlock'](user);

    expect(usersService.block).not.toHaveBeenCalled();
  });

  it('confirmBlock shows a classified error message when the block request fails', () => {
    const { component, dialog, usersService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    usersService.block.mockReturnValue(throwError(() => ({ status: 409 })));

    component['confirmBlock'](user);

    expect(snackBar.open).toHaveBeenCalledWith('users.list.errors.conflict', undefined, { duration: 4000 });
  });

  it('unblock calls the service directly (no confirmation step) and shows a success snackbar', () => {
    const { component, usersService, snackBar } = configure();
    usersService.unblock.mockReturnValue(of(undefined));

    component['unblock'](user);

    expect(usersService.unblock).toHaveBeenCalledWith('u1');
    expect(snackBar.open).toHaveBeenCalledWith('users.list.unblockedSuccess', undefined, { duration: 3000 });
  });
});
