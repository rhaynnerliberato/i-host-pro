import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { RoleResponse, UserResponse } from '../../../core/api/generated/api-client';
import { ConfirmDialog, ConfirmDialogData } from '../confirm-dialog/confirm-dialog';
import { classifyUserActionError } from '../user-error';
import { UsersService } from '../users.service';

export interface RoleManagementDialogData {
  user: UserResponse;
  roles: RoleResponse[];
}

/** Assign/remove roles for one user — the catalog (name + code) comes exclusively from the real GET /api/v1/roles response. */
@Component({
  selector: 'app-role-management-dialog',
  imports: [
    FormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatChipsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatSelectModule,
  ],
  templateUrl: './role-management-dialog.html',
  styleUrl: './role-management-dialog.scss',
})
export class RoleManagementDialog {
  protected readonly data = inject<RoleManagementDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<RoleManagementDialog>);
  private readonly dialog = inject(MatDialog);
  private readonly usersService = inject(UsersService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);

  protected readonly currentRoles = signal<string[]>([...(this.data.user.roles ?? [])]);
  protected readonly selectedRoleToAdd = signal<string>('');
  protected readonly busy = signal(false);

  protected readonly assignableRoles = computed(() => this.data.roles.filter((role) => !this.currentRoles().includes(role.code ?? '')));

  protected roleName(code: string): string {
    return this.data.roles.find((role) => role.code === code)?.name ?? code;
  }

  protected assignSelectedRole(): void {
    const roleCode = this.selectedRoleToAdd();
    if (!roleCode || this.busy()) return;

    this.busy.set(true);
    this.usersService.assignRole(this.data.user.id!, { roleCode }).subscribe({
      next: () => {
        this.busy.set(false);
        this.currentRoles.update((roles: string[]) => [...roles, roleCode]);
        this.selectedRoleToAdd.set('');
        this.snackBar.open(this.transloco.translate('users.roles.assignedSuccess'), undefined, { duration: 3000 });
      },
      error: (error: unknown) => this.handleError(error),
    });
  }

  protected confirmRemoveRole(roleCode: string): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        titleKey: 'users.roles.removeConfirmTitle',
        messageKey: 'users.roles.removeConfirmMessage',
        messageParams: { role: this.roleName(roleCode) },
        confirmKey: 'common.remove',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (confirmed) this.removeRole(roleCode);
    });
  }

  private removeRole(roleCode: string): void {
    this.busy.set(true);
    this.usersService.removeRole(this.data.user.id!, roleCode).subscribe({
      next: () => {
        this.busy.set(false);
        this.currentRoles.update((roles: string[]) => roles.filter((code: string) => code !== roleCode));
        this.snackBar.open(this.transloco.translate('users.roles.removedSuccess'), undefined, { duration: 3000 });
      },
      error: (error: unknown) => this.handleError(error),
    });
  }

  private handleError(error: unknown): void {
    this.busy.set(false);
    const { status } = classifyUserActionError(error);
    const key = status === 409 ? 'users.roles.errors.conflict' : status === 404 ? 'users.roles.errors.notFound' : 'users.roles.errors.generic';
    this.snackBar.open(this.transloco.translate(key), undefined, { duration: 4000 });
  }

  protected close(): void {
    this.dialogRef.close(this.currentRoles());
  }
}
