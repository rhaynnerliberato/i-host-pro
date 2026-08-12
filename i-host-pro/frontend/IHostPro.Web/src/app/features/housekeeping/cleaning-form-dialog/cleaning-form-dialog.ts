import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe } from '@jsverse/transloco';

import { CleaningDetailResponse, CreateCleaningRequest } from '../../../core/api/generated/api-client';
import { classifyHousekeepingError } from '../housekeeping-error';
import { HousekeepingService } from '../housekeeping.service';

/**
 * Manual cleaning creation only — there is no update endpoint for a Cleaning
 * (Checkpoint 0 gate: no generic PATCH, no edit of an existing cleaning),
 * unlike `ReservationFormDialog`'s shared create/edit dialog. Takes no
 * `MAT_DIALOG_DATA` — creation needs no pre-existing state to seed the form.
 * `propertyId`/`reservationId` are raw GUID fields, not selectors — same
 * rationale as `ReservationsList`'s own `propertyId` filter (GET
 * /api/v1/properties requires PROPERTIES:MANAGE, which OPERATOR does not
 * necessarily hold together with CLEANINGS:MANAGE).
 */
@Component({
  selector: 'app-cleaning-form-dialog',
  imports: [ReactiveFormsModule, TranslocoPipe, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressSpinnerModule],
  templateUrl: './cleaning-form-dialog.html',
  styleUrl: './cleaning-form-dialog.scss',
})
export class CleaningFormDialog {
  private readonly dialogRef = inject(MatDialogRef<CleaningFormDialog>);
  private readonly formBuilder = inject(FormBuilder);
  private readonly housekeepingService = inject(HousekeepingService);

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly form = this.formBuilder.nonNullable.group({
    propertyId: ['', [Validators.required]],
    reservationId: [''],
  });

  protected submit(): void {
    if (this.submitting() || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { propertyId, reservationId } = this.form.getRawValue();

    this.submitting.set(true);
    this.errorKey.set(null);

    this.housekeepingService
      .create({ propertyId, reservationId: reservationId || undefined } satisfies CreateCleaningRequest)
      .subscribe({
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
    if (status === 404 && code === 'property_not_found') return 'housekeeping.form.errors.propertyNotFound';
    if (status === 404 && code === 'reservation_reference_not_available') return 'housekeeping.form.errors.reservationNotFound';
    if (status === 404) return 'housekeeping.form.errors.notFound';
    if (status === 400) return 'housekeeping.form.errors.validation';
    return 'housekeeping.form.errors.generic';
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}
