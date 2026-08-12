import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe } from '@jsverse/transloco';

import { CleaningDetailResponse } from '../../../core/api/generated/api-client';
import { classifyHousekeepingError } from '../housekeeping-error';
import { HousekeepingService } from '../housekeeping.service';

export interface AssignCleaningDialogData {
  cleaningId: string;
}

/**
 * Assigns a Housekeeper to a Pending cleaning — the assignee is a raw GUID
 * field, not a picker: there is no endpoint this increment to list users by
 * the HOUSEKEEPER role for the frontend to populate a selector from (the
 * backend itself only validates eligibility server-side, via
 * `IIdentityUserEligibilityReader`, when the command runs).
 */
@Component({
  selector: 'app-assign-cleaning-dialog',
  imports: [ReactiveFormsModule, TranslocoPipe, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressSpinnerModule],
  templateUrl: './assign-cleaning-dialog.html',
  styleUrl: './assign-cleaning-dialog.scss',
})
export class AssignCleaningDialog {
  protected readonly data = inject<AssignCleaningDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<AssignCleaningDialog>);
  private readonly housekeepingService = inject(HousekeepingService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly form = this.formBuilder.nonNullable.group({
    housekeeperUserId: ['', [Validators.required]],
  });
  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected submit(): void {
    if (this.submitting() || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { housekeeperUserId } = this.form.getRawValue();

    this.submitting.set(true);
    this.errorKey.set(null);

    this.housekeepingService.assign(this.data.cleaningId, housekeeperUserId).subscribe({
      next: (cleaning: CleaningDetailResponse) => {
        this.submitting.set(false);
        this.dialogRef.close(cleaning);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.errorKey.set(this.resolveErrorKey(error));
      },
    });
  }

  private resolveErrorKey(error: unknown): string {
    const { status, code } = classifyHousekeepingError(error);
    if (status === 403) return 'housekeeping.assign.errors.notEligible';
    if (status === 404) return 'housekeeping.assign.errors.notFound';
    if (status === 409 && code === 'cleaning_concurrency_conflict') return 'housekeeping.assign.errors.concurrency';
    if (status === 409) return 'housekeeping.assign.errors.conflict';
    if (status === 400) return 'housekeeping.assign.errors.validation';
    return 'housekeeping.assign.errors.generic';
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}
