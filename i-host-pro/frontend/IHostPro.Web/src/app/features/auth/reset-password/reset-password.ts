import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../../core/auth/auth.service';
import { classifyAuthActionError } from '../auth-action-error';

/** Mirrors the backend's IdentityErrorCodes.PasswordResetTokenInvalid — the single undifferentiated code for an invalid, expired, or already-consumed token. */
const PASSWORD_RESET_TOKEN_INVALID_CODE = 'Identity.PasswordResetTokenInvalid';

function passwordsMatchValidator(control: AbstractControl): ValidationErrors | null {
  const password = control.get('newPassword')?.value;
  const confirmPassword = control.get('confirmPassword')?.value;
  return password && confirmPassword && password !== confirmPassword ? { passwordMismatch: true } : null;
}

/**
 * Reached only via the link emailed by StartPasswordResetProcessor
 * (`{FrontendResetUrlBase}?token={rawToken}&tenant={slug}`) — the `tenant`
 * query param is informational only; ForgotPasswordCompleteRequest never
 * needs it, since CompletePasswordResetProcessor recovers the tenant
 * exclusively from the consumed token row itself.
 */
@Component({
  selector: 'app-reset-password',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    TranslocoPipe,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatCardModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './reset-password.html',
  styleUrl: './reset-password.scss',
})
export class ResetPassword {
  private readonly formBuilder = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  private readonly token = this.route.snapshot.queryParamMap.get('token');

  protected readonly tokenMissing = signal(this.token === null);
  protected readonly submitting = signal(false);
  protected readonly submitted = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly form = this.formBuilder.nonNullable.group(
    {
      newPassword: ['', [Validators.required, Validators.minLength(10)]],
      confirmPassword: ['', [Validators.required]],
    },
    { validators: passwordsMatchValidator },
  );

  protected submit(): void {
    if (this.submitting() || this.form.invalid || this.token === null) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);

    const { newPassword } = this.form.getRawValue();

    this.authService.completePasswordReset(this.token, newPassword).subscribe({
      next: () => {
        this.submitting.set(false);
        this.submitted.set(true);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const { status, codes } = classifyAuthActionError(error);
        this.errorKey.set(
          codes.includes(PASSWORD_RESET_TOKEN_INVALID_CODE)
            ? 'auth.resetPassword.errors.tokenInvalid'
            : status === 400
              ? 'auth.resetPassword.errors.validation'
              : 'auth.resetPassword.errors.generic',
        );
      },
    });
  }
}
