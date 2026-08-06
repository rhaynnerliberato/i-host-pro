import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { TranslocoPipe } from '@jsverse/transloco';

import { CondominiumSummaryResponse, PropertyDetailResponse } from '../../../../core/api/generated/api-client';
import { classifyPropertyManagementError } from '../../property-management-error';
import { PropertiesService } from '../../properties.service';

export interface PropertyFormDialogData {
  /** Present only in edit mode. */
  property?: PropertyDetailResponse;
  condominiums: CondominiumSummaryResponse[];
}

/**
 * Create and edit share one dialog. Address is optional at the backend
 * validator level (CreatePropertyCommandValidator/UpdatePropertyCommandValidator)
 * — a property may rely entirely on its condominium's address instead — but
 * once any address field is provided, every sub-field becomes required
 * (mirrors the backend's own conditional "When Address is not null" rule,
 * not an invented one). The "own address" toggle reflects that directly.
 */
@Component({
  selector: 'app-property-form-dialog',
  imports: [
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
  ],
  templateUrl: './property-form-dialog.html',
  styleUrl: './property-form-dialog.scss',
})
export class PropertyFormDialog {
  protected readonly data = inject<PropertyFormDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<PropertyFormDialog>);
  private readonly formBuilder = inject(FormBuilder);
  private readonly propertiesService = inject(PropertiesService);

  protected readonly isEditMode = !!this.data.property;
  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly hasOwnAddress = signal(!!this.data.property?.address);

  protected readonly form = this.formBuilder.nonNullable.group({
    code: [this.data.property?.code ?? '', [Validators.required]],
    name: [this.data.property?.name ?? '', [Validators.required]],
    capacity: [this.data.property?.capacity ?? 1, [Validators.required, Validators.min(1)]],
    condominiumId: [this.data.property?.condominiumId ?? ''],
    zipCode: [this.data.property?.address?.zipCode ?? ''],
    street: [this.data.property?.address?.street ?? ''],
    number: [this.data.property?.address?.number ?? ''],
    complement: [this.data.property?.address?.complement ?? ''],
    neighborhood: [this.data.property?.address?.neighborhood ?? ''],
    city: [this.data.property?.address?.city ?? ''],
    state: [this.data.property?.address?.state ?? ''],
    country: [this.data.property?.address?.country ?? 'BR'],
  });

  protected readonly condominiumOptions = computed(() => this.data.condominiums);

  protected toggleOwnAddress(checked: boolean): void {
    this.hasOwnAddress.set(checked);
  }

  protected submit(): void {
    if (this.hasOwnAddress() && !this.addressFieldsValid()) {
      this.form.markAllAsTouched();
      return;
    }
    if (this.submitting() || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);
    const values = this.form.getRawValue();
    const condominiumId = values.condominiumId || undefined;
    const address = this.hasOwnAddress()
      ? {
          zipCode: values.zipCode,
          street: values.street,
          number: values.number,
          complement: values.complement || undefined,
          neighborhood: values.neighborhood,
          city: values.city,
          state: values.state,
          country: values.country,
        }
      : undefined;

    const request$ = this.isEditMode
      ? this.propertiesService.update(this.data.property!.id!, {
          code: values.code,
          name: values.name,
          capacity: values.capacity,
          condominiumId: condominiumId ?? null,
          address: address ?? null,
        })
      : this.propertiesService.create({ code: values.code, name: values.name, capacity: values.capacity, condominiumId, address });

    request$.subscribe({
      next: (property) => {
        this.submitting.set(false);
        this.dialogRef.close(property);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const { status } = classifyPropertyManagementError(error);
        this.errorKey.set(
          status === 409
            ? 'propertyManagement.properties.form.errors.conflict'
            : status === 400
              ? 'propertyManagement.properties.form.errors.validation'
              : 'propertyManagement.properties.form.errors.generic',
        );
      },
    });
  }

  private addressFieldsValid(): boolean {
    const { zipCode, street, number, neighborhood, city, state } = this.form.getRawValue();
    return !!(zipCode && street && number && neighborhood && city && state);
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}
