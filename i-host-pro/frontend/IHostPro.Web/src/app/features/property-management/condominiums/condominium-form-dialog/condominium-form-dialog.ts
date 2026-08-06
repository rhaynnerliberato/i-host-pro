import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe } from '@jsverse/transloco';

import { CondominiumDetailResponse } from '../../../../core/api/generated/api-client';
import { classifyPropertyManagementError } from '../../property-management-error';
import { CondominiumsService } from '../../condominiums.service';

export interface CondominiumFormDialogData {
  /** Present only in edit mode. */
  condominium?: CondominiumDetailResponse;
}

/** Create and edit share one dialog — both collect name + full address (always required, per CreateCondominiumCommandValidator/UpdateCondominiumCommandValidator). */
@Component({
  selector: 'app-condominium-form-dialog',
  imports: [
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './condominium-form-dialog.html',
  styleUrl: './condominium-form-dialog.scss',
})
export class CondominiumFormDialog {
  protected readonly data = inject<CondominiumFormDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<CondominiumFormDialog>);
  private readonly formBuilder = inject(FormBuilder);
  private readonly condominiumsService = inject(CondominiumsService);

  protected readonly isEditMode = !!this.data.condominium;
  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly form = this.formBuilder.nonNullable.group({
    name: [this.data.condominium?.name ?? '', [Validators.required]],
    zipCode: [this.data.condominium?.address?.zipCode ?? '', [Validators.required]],
    street: [this.data.condominium?.address?.street ?? '', [Validators.required]],
    number: [this.data.condominium?.address?.number ?? '', [Validators.required]],
    complement: [this.data.condominium?.address?.complement ?? ''],
    neighborhood: [this.data.condominium?.address?.neighborhood ?? '', [Validators.required]],
    city: [this.data.condominium?.address?.city ?? '', [Validators.required]],
    state: [this.data.condominium?.address?.state ?? '', [Validators.required]],
    country: [this.data.condominium?.address?.country ?? 'BR', [Validators.required]],
  });

  protected submit(): void {
    if (this.submitting() || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);
    const { name, zipCode, street, number, complement, neighborhood, city, state, country } = this.form.getRawValue();
    const address = { zipCode, street, number, complement: complement || undefined, neighborhood, city, state, country };

    const request$ = this.isEditMode
      ? this.condominiumsService.update(this.data.condominium!.id!, { name, address })
      : this.condominiumsService.create({ name, address });

    request$.subscribe({
      next: (condominium) => {
        this.submitting.set(false);
        this.dialogRef.close(condominium);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const { status } = classifyPropertyManagementError(error);
        this.errorKey.set(
          status === 409
            ? 'propertyManagement.condominiums.form.errors.conflict'
            : status === 400
              ? 'propertyManagement.condominiums.form.errors.validation'
              : 'propertyManagement.condominiums.form.errors.generic',
        );
      },
    });
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}
