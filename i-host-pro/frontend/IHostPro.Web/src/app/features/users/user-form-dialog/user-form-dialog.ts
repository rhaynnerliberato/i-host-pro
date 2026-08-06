import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { TranslocoPipe } from '@jsverse/transloco';

import { RoleResponse, UserResponse } from '../../../core/api/generated/api-client';
import { classifyUserActionError } from '../user-error';
import { UsersService } from '../users.service';

export interface UserFormDialogData {
  /** Present only in edit mode. */
  user?: UserResponse;
  roles: RoleResponse[];
}

/**
 * Create and edit share one dialog: both collect full name + e-mail, but
 * only create additionally collects the initial password + role (the
 * backend's own contracts differ the same way — `UpdateUserRequest` has no
 * password/role field at all; role changes happen exclusively through the
 * separate assign/remove-role actions).
 */
@Component({
  selector: 'app-user-form-dialog',
  imports: [
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
  ],
  templateUrl: './user-form-dialog.html',
  styleUrl: './user-form-dialog.scss',
})
export class UserFormDialog {
  protected readonly data = inject<UserFormDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<UserFormDialog>);
  private readonly formBuilder = inject(FormBuilder);
  private readonly usersService = inject(UsersService);

  protected readonly isEditMode = !!this.data.user;
  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly form = this.formBuilder.nonNullable.group({
    fullName: [this.data.user?.fullName ?? '', [Validators.required]],
    email: [this.data.user?.email ?? '', [Validators.required, Validators.email]],
    initialPassword: ['', this.isEditMode ? [] : [Validators.required, Validators.minLength(10)]],
    roleCode: ['', this.isEditMode ? [] : [Validators.required]],
  });

  protected readonly roleOptions = computed(() => this.data.roles);

  protected submit(): void {
    if (this.submitting() || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);
    const { fullName, email, initialPassword, roleCode } = this.form.getRawValue();

    const request$ = this.isEditMode
      ? this.usersService.update(this.data.user!.id!, { fullName, email })
      : this.usersService.create({ fullName, email, initialPassword, roleCode });

    request$.subscribe({
      next: (user) => {
        this.submitting.set(false);
        this.dialogRef.close(user);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const { status } = classifyUserActionError(error);
        this.errorKey.set(
          status === 409 ? 'users.form.errors.conflict' : status === 400 ? 'users.form.errors.validation' : 'users.form.errors.generic',
        );
      },
    });
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}
