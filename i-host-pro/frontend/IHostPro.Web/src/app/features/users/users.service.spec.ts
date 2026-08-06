import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client, UserStatus } from '../../core/api/generated/api-client';
import { UsersService } from './users.service';

describe('UsersService', () => {
  let service: UsersService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = {
      usersGET: vi.fn().mockReturnValue(of({})),
      usersGET2: vi.fn().mockReturnValue(of({})),
      usersPOST: vi.fn().mockReturnValue(of({})),
      usersPATCH: vi.fn().mockReturnValue(of({})),
      block: vi.fn().mockReturnValue(of(undefined)),
      unblock: vi.fn().mockReturnValue(of(undefined)),
      resetPassword: vi.fn().mockReturnValue(of(undefined)),
      rolesPOST: vi.fn().mockReturnValue(of(undefined)),
      rolesDELETE: vi.fn().mockReturnValue(of(undefined)),
      rolesAll: vi.fn().mockReturnValue(of([])),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(UsersService);
  });

  it('list delegates to Client.usersGET with page, pageSize, search and status', () => {
    service.list(2, 25, 'ada', UserStatus._1).subscribe();
    expect(client['usersGET']).toHaveBeenCalledWith(2, 25, 'ada', UserStatus._1);
  });

  it('getById delegates to Client.usersGET2 with the user id', () => {
    service.getById('user-1').subscribe();
    expect(client['usersGET2']).toHaveBeenCalledWith('user-1');
  });

  it('create delegates to Client.usersPOST with the request body', () => {
    const request = { fullName: 'Ada', email: 'ada@example.com', initialPassword: 'x', roleCode: 'ADMIN' };
    service.create(request).subscribe();
    expect(client['usersPOST']).toHaveBeenCalledWith(request);
  });

  it('update delegates to Client.usersPATCH with the user id and request body', () => {
    const request = { fullName: 'Ada', email: 'ada@example.com' };
    service.update('user-1', request).subscribe();
    expect(client['usersPATCH']).toHaveBeenCalledWith('user-1', request);
  });

  it('block delegates to Client.block with the user id', () => {
    service.block('user-1').subscribe();
    expect(client['block']).toHaveBeenCalledWith('user-1');
  });

  it('unblock delegates to Client.unblock with the user id', () => {
    service.unblock('user-1').subscribe();
    expect(client['unblock']).toHaveBeenCalledWith('user-1');
  });

  it('resetPassword delegates to Client.resetPassword with the user id and request body', () => {
    const request = { newPassword: 'new-password-123' };
    service.resetPassword('user-1', request).subscribe();
    expect(client['resetPassword']).toHaveBeenCalledWith('user-1', request);
  });

  it('assignRole delegates to Client.rolesPOST with the user id and request body', () => {
    const request = { roleCode: 'OPERATOR' };
    service.assignRole('user-1', request).subscribe();
    expect(client['rolesPOST']).toHaveBeenCalledWith('user-1', request);
  });

  it('removeRole delegates to Client.rolesDELETE with the user id and role code', () => {
    service.removeRole('user-1', 'OPERATOR').subscribe();
    expect(client['rolesDELETE']).toHaveBeenCalledWith('user-1', 'OPERATOR');
  });

  it('listRoles delegates to Client.rolesAll with no arguments', () => {
    service.listRoles().subscribe();
    expect(client['rolesAll']).toHaveBeenCalledWith();
  });
});
