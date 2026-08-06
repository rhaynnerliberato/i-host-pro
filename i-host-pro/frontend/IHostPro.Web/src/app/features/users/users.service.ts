import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AssignRoleRequest,
  Client,
  CreateUserRequest,
  PagedUserResponse,
  ResetPasswordRequest,
  RoleResponse,
  UpdateUserRequest,
  UserResponse,
  UserStatus,
} from '../../core/api/generated/api-client';

/** Thin wrapper over the generated Client's user-administration + role-catalog methods — the only representation of these HTTP contracts this feature uses. */
@Injectable({ providedIn: 'root' })
export class UsersService {
  private readonly client = inject(Client);

  list(page: number, pageSize: number, search: string | undefined, status: UserStatus | undefined): Observable<PagedUserResponse> {
    return this.client.usersGET(page, pageSize, search, status);
  }

  getById(userId: string): Observable<UserResponse> {
    return this.client.usersGET2(userId);
  }

  create(request: CreateUserRequest): Observable<UserResponse> {
    return this.client.usersPOST(request);
  }

  update(userId: string, request: UpdateUserRequest): Observable<UserResponse> {
    return this.client.usersPATCH(userId, request);
  }

  block(userId: string): Observable<void> {
    return this.client.block(userId);
  }

  unblock(userId: string): Observable<void> {
    return this.client.unblock(userId);
  }

  resetPassword(userId: string, request: ResetPasswordRequest): Observable<void> {
    return this.client.resetPassword(userId, request);
  }

  assignRole(userId: string, request: AssignRoleRequest): Observable<void> {
    return this.client.rolesPOST(userId, request);
  }

  removeRole(userId: string, roleCode: string): Observable<void> {
    return this.client.rolesDELETE(userId, roleCode);
  }

  listRoles(): Observable<RoleResponse[]> {
    return this.client.rolesAll();
  }
}
