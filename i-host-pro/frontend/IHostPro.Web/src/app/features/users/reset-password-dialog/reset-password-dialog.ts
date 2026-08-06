import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe } from '@jsverse/transloco';

import { UserResponse } from '../../../core/api/generated/api-client';
import { classifyUserActionError } from '../user-error';
import { UsersService } from '../users.service';

export interface ResetPasswordDialogData {
  user: UserResponse;
}

/** Administrative password reset — a sensitive action, always reached through a confirmation step in UsersList before this dialog opens. */
@Component({
  selector: 'app-reset-password-dialog',
  imports: [ReactiveFormsModule, TranslocoPipe, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressSpinnerModule],
  templateUrl: './reset-password-dialog.html',
  styleUrl: './reset-password-dialog.scss',
})
export class ResetPasswordDialog {
  protected readonly data = inject<ResetPasswordDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ResetPasswordDialog>);
  private readonly formBuilder = inject(FormBuilder);
  private readonly usersService = inject(UsersService);

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly form = this.formBuilder.nonNullable.group({
    newPassword: ['', [Validators.required, Validators.minLength(10)]],
  });

  protected submit(): void {
    if (this.submitting() || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);
    const { newPassword } = this.form.getRawValue();

    this.usersService.resetPassword(this.data.user.id!, { newPassword }).subscribe({
      next: () => {
        this.submitting.set(false);
        this.dialogRef.close(true);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const { status } = classifyUserActionError(error);
        this.errorKey.set(
          status === 409
            ? 'users.resetPassword.errors.conflict'
            : status === 400
              ? 'users.resetPassword.errors.validation'
              : 'users.resetPassword.errors.generic',
        );
      },
    });
  }

  protected cancel(): void {
    this.dialogRef.close(false);
  }
}
