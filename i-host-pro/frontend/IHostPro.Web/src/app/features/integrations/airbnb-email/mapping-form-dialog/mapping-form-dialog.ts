import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { TranslocoPipe } from '@jsverse/transloco';

import { PropertySummaryResponse } from '../../../../core/api/generated/api-client';
import { PropertiesService } from '../../../property-management/properties.service';
import { AirbnbEmailService } from '../airbnb-email.service';

/**
 * A single Property page is fetched (no search/autocomplete) — an
 * intentionally minimal selector for this first operational surface. If a
 * tenant's property list grows large enough that this is unusable, that is
 * evidence to design pagination/search here, not something to build ahead of it.
 * 100 is the backend's own hard cap (ListPropertiesQueryValidator.MaxPageSize).
 */
const PROPERTY_PAGE_SIZE = 100;

@Component({
  selector: 'app-mapping-form-dialog',
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
  templateUrl: './mapping-form-dialog.html',
})
export class MappingFormDialog implements OnInit {
  private readonly dialogRef = inject(MatDialogRef<MappingFormDialog>);
  private readonly formBuilder = inject(FormBuilder);
  private readonly airbnbEmailService = inject(AirbnbEmailService);
  private readonly propertiesService = inject(PropertiesService);

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly properties = signal<PropertySummaryResponse[]>([]);
  protected readonly loadingProperties = signal(true);

  protected readonly form = this.formBuilder.nonNullable.group({
    listingTitle: ['', [Validators.required, Validators.maxLength(200)]],
    propertyId: ['', [Validators.required]],
  });

  ngOnInit(): void {
    this.propertiesService.list(1, PROPERTY_PAGE_SIZE).subscribe({
      next: (page) => {
        this.properties.set(page.items ?? []);
        this.loadingProperties.set(false);
      },
      error: () => this.loadingProperties.set(false),
    });
  }

  protected submit(): void {
    if (this.submitting() || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { listingTitle, propertyId } = this.form.getRawValue();
    this.submitting.set(true);
    this.errorKey.set(null);

    this.airbnbEmailService.createMapping({ listingTitle, propertyId }).subscribe({
      next: (mapping) => {
        this.submitting.set(false);
        this.dialogRef.close(mapping);
      },
      error: () => {
        this.submitting.set(false);
        this.errorKey.set('integrations.airbnbEmail.mappings.form.errors.generic');
      },
    });
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}
