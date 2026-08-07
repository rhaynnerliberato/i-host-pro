import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe } from '@jsverse/transloco';

import { CreateReservationRequest, ReservationDetailResponse } from '../../../core/api/generated/api-client';
import { classifyReservationError } from '../reservation-error';
import { ReservationsService } from '../reservations.service';

export interface ReservationFormDialogData {
  /** Present only in edit mode. */
  reservation?: ReservationDetailResponse;
}

/**
 * Create and edit share one dialog — both collect the same six fields. Edit
 * always submits the full current form state (never a true partial patch
 * from the UI's perspective), matching the convention already established
 * for Property Management's edit dialogs in Increment 3.
 *
 * checkInAt/checkOutAt use native `datetime-local` inputs (no date/time
 * picker library is used elsewhere in this project, and none is added here
 * without a proven need). Their string value is interpreted by the browser
 * as local time with no explicit offset; converting through `new Date(...)`
 * yields a real `Date` instance, and NSwag's generated client serializes a
 * `Date` via `JSON.stringify`, which calls `Date.prototype.toJSON()` —
 * always an ISO-8601 string with an explicit "Z" (UTC) offset, satisfying
 * the backend's `RequireExplicitOffsetDateTimeOffsetConverter` (Fase 4 doc,
 * Section 13.4) without any manual string formatting.
 */
@Component({
  selector: 'app-reservation-form-dialog',
  imports: [ReactiveFormsModule, TranslocoPipe, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatProgressSpinnerModule],
  templateUrl: './reservation-form-dialog.html',
  styleUrl: './reservation-form-dialog.scss',
})
export class ReservationFormDialog {
  protected readonly data = inject<ReservationFormDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ReservationFormDialog>);
  private readonly formBuilder = inject(FormBuilder);
  private readonly reservationsService = inject(ReservationsService);

  protected readonly isEditMode = !!this.data.reservation;
  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly form = this.formBuilder.nonNullable.group({
    propertyId: [this.data.reservation?.propertyId ?? '', [Validators.required]],
    guestName: [this.data.reservation?.guestName ?? '', [Validators.required, Validators.maxLength(150)]],
    guestPhone: [this.data.reservation?.guestPhone ?? '', [Validators.maxLength(30)]],
    checkInAt: [this.data.reservation?.checkInAt ? toLocalInputValue(this.data.reservation.checkInAt) : '', [Validators.required]],
    checkOutAt: [this.data.reservation?.checkOutAt ? toLocalInputValue(this.data.reservation.checkOutAt) : '', [Validators.required]],
    guestCount: [this.data.reservation?.guestCount ?? 1, [Validators.required, Validators.min(1)]],
  });

  protected submit(): void {
    if (this.submitting() || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { propertyId, guestName, guestPhone, checkInAt, checkOutAt, guestCount } = this.form.getRawValue();
    const checkIn = new Date(checkInAt);
    const checkOut = new Date(checkOutAt);

    // Mirrors the backend's own domain rule (Reservation.Create/Reschedule reject
    // checkOut <= checkIn) — presentation only, the backend remains the sole authority.
    if (checkOut <= checkIn) {
      this.errorKey.set('reservations.form.errors.checkOutBeforeCheckIn');
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);

    const request$ = this.isEditMode
      ? this.reservationsService.update(this.data.reservation!.id!, {
          propertyId,
          guestName,
          guestPhone: guestPhone || null,
          checkInAt: checkIn,
          checkOutAt: checkOut,
          guestCount,
        })
      : this.reservationsService.create({
          propertyId,
          guestName,
          guestPhone: guestPhone || undefined,
          checkInAt: checkIn,
          checkOutAt: checkOut,
          guestCount,
        } satisfies CreateReservationRequest);

    request$.subscribe({
      next: (reservation) => {
        this.submitting.set(false);
        this.dialogRef.close(reservation);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.errorKey.set(this.resolveErrorKey(error));
      },
    });
  }

  /** Distinguishes the real, documented outcomes (Fase 4 doc, Section 13.4) — 404 vs the two 400 codes the backend actually returns for a property-eligibility failure vs any other 400 vs 409 (never further distinguishable — the backend's own ProblemDetails carries no code for 404/409, only 400). */
  private resolveErrorKey(error: unknown): string {
    const { status, codes } = classifyReservationError(error);
    if (status === 404) return 'reservations.form.errors.notFound';
    if (status === 409) return 'reservations.form.errors.conflict';
    if (status === 400) {
      if (codes.includes('Reservations.PropertyNotActive')) return 'reservations.form.errors.propertyNotActive';
      if (codes.includes('Reservations.PropertyCapacityExceeded')) return 'reservations.form.errors.capacityExceeded';
      return 'reservations.form.errors.validation';
    }
    return 'reservations.form.errors.generic';
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}

/**
 * The generated `Client` leaves `jsonParseReviver` unset, so every response
 * date field is actually still a plain ISO string at runtime despite the
 * generated TypeScript interface declaring `Date` — every other usage in
 * this codebase only ever feeds these values into `DatePipe`, which accepts
 * a string transparently, so this never surfaced before. `new Date(...)` on
 * an already-real `Date` returns an equivalent `Date`, so this is safe for
 * either runtime shape.
 */
function toLocalInputValue(value: Date | string): string {
  const date = value instanceof Date ? value : new Date(value);
  const pad = (n: number) => n.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}
