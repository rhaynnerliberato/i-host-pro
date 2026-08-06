import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { debounceTime, distinctUntilChanged } from 'rxjs';

import { RoleResponse, UserResponse, UserStatus } from '../../../core/api/generated/api-client';
import { ConfirmDialog, ConfirmDialogData } from '../confirm-dialog/confirm-dialog';
import { ResetPasswordDialog, ResetPasswordDialogData } from '../reset-password-dialog/reset-password-dialog';
import { RoleManagementDialog, RoleManagementDialogData } from '../role-management-dialog/role-management-dialog';
import { UserFormDialog, UserFormDialogData } from '../user-form-dialog/user-form-dialog';
import { classifyUserActionError } from '../user-error';
import { UsersService } from '../users.service';

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

@Component({
  selector: 'app-users-list',
  imports: [
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatMenuModule,
    MatPaginatorModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTableModule,
  ],
  templateUrl: './users-list.html',
  styleUrl: './users-list.scss',
})
export class UsersList {
  private readonly usersService = inject(UsersService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);

  protected readonly displayedColumns = ['fullName', 'email', 'status', 'roles', 'actions'];
  protected readonly UserStatus = UserStatus;

  protected readonly searchControl = new FormControl('', { nonNullable: true });
  protected readonly statusControl = new FormControl<UserStatus | null>(null);

  protected readonly state = signal<LoadState>('loading');
  protected readonly users = signal<UserResponse[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly pageIndex = signal(0);
  protected readonly pageSize = signal(10);
  protected readonly roles = signal<RoleResponse[]>([]);

  constructor() {
    this.usersService.listRoles().subscribe({
      next: (roles) => this.roles.set(roles),
      // The role catalog is only needed for the create/assign-role pickers — a failure here must never block the list itself.
      error: () => this.roles.set([]),
    });

    this.searchControl.valueChanges
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed())
      .subscribe(() => {
        this.pageIndex.set(0);
        this.loadUsers();
      });

    this.statusControl.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.pageIndex.set(0);
      this.loadUsers();
    });

    this.loadUsers();
  }

  protected loadUsers(): void {
    this.state.set('loading');
    const search = this.searchControl.value.trim();
    this.usersService.list(this.pageIndex() + 1, this.pageSize(), search === '' ? undefined : search, this.statusControl.value ?? undefined).subscribe({
      next: (page) => {
        const items = page.items ?? [];
        this.users.set(items);
        this.totalCount.set(page.totalCount ?? 0);
        this.state.set(items.length === 0 ? 'empty' : 'loaded');
      },
      error: () => this.state.set('error'),
    });
  }

  protected onPageChange(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.loadUsers();
  }

  protected statusLabelKey(status: string | undefined): string {
    return status === 'Blocked' ? 'users.list.statusBlocked' : 'users.list.statusActive';
  }

  protected roleName(code: string): string {
    return this.roles().find((role) => role.code === code)?.name ?? code;
  }

  protected openCreateDialog(): void {
    const ref = this.dialog.open<UserFormDialog, UserFormDialogData, UserResponse>(UserFormDialog, {
      data: { roles: this.roles() },
      width: '480px',
    });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.snackBar.open(this.transloco.translate('users.list.createdSuccess'), undefined, { duration: 3000 });
        this.loadUsers();
      }
    });
  }

  protected openEditDialog(user: UserResponse): void {
    const ref = this.dialog.open<UserFormDialog, UserFormDialogData, UserResponse>(UserFormDialog, {
      data: { user, roles: this.roles() },
      width: '480px',
    });
    ref.afterClosed().subscribe((updated) => {
      if (updated) {
        this.snackBar.open(this.transloco.translate('users.list.updatedSuccess'), undefined, { duration: 3000 });
        this.loadUsers();
      }
    });
  }

  protected openRoleManagementDialog(user: UserResponse): void {
    const ref = this.dialog.open<RoleManagementDialog, RoleManagementDialogData, string[]>(RoleManagementDialog, {
      data: { user, roles: this.roles() },
      width: '480px',
    });
    ref.afterClosed().subscribe(() => this.loadUsers());
  }

  protected confirmBlock(user: UserResponse): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        titleKey: 'users.list.blockConfirmTitle',
        messageKey: 'users.list.blockConfirmMessage',
        messageParams: { name: user.fullName ?? '' },
        confirmKey: 'users.list.block',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.usersService.block(user.id!).subscribe({
        next: () => {
          this.snackBar.open(this.transloco.translate('users.list.blockedSuccess'), undefined, { duration: 3000 });
          this.loadUsers();
        },
        error: (error: unknown) => this.showActionError(error),
      });
    });
  }

  protected unblock(user: UserResponse): void {
    this.usersService.unblock(user.id!).subscribe({
      next: () => {
        this.snackBar.open(this.transloco.translate('users.list.unblockedSuccess'), undefined, { duration: 3000 });
        this.loadUsers();
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  protected openResetPasswordDialog(user: UserResponse): void {
    const ref = this.dialog.open<ResetPasswordDialog, ResetPasswordDialogData, boolean>(ResetPasswordDialog, {
      data: { user },
      width: '420px',
    });
    ref.afterClosed().subscribe((succeeded) => {
      if (succeeded) {
        this.snackBar.open(this.transloco.translate('users.resetPassword.success'), undefined, { duration: 3000 });
      }
    });
  }

  private showActionError(error: unknown): void {
    const { status } = classifyUserActionError(error);
    const key = status === 409 ? 'users.list.errors.conflict' : status === 404 ? 'users.list.errors.notFound' : 'users.list.errors.generic';
    this.snackBar.open(this.transloco.translate(key), undefined, { duration: 4000 });
  }
}
