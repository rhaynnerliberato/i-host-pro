import { Component, inject, signal } from '@angular/core';
import { FormControl, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { PropertyOwnerResponse } from '../../../../core/api/generated/api-client';
import { ConfirmDialog, ConfirmDialogData } from '../../../users/confirm-dialog/confirm-dialog';
import { classifyPropertyManagementError } from '../../property-management-error';
import { PropertiesService } from '../../properties.service';

export interface PropertyOwnersDialogData {
  propertyId: string;
  propertyName: string;
}

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

/**
 * Views/links/removes owner links for one property — PropertyOwnerResponse
 * carries only OwnerUserId (no name/e-mail, the endpoint doesn't resolve
 * it), so this dialog displays the raw id, per the scope registered in the
 * Fase 4 document (Section 12.2).
 */
@Component({
  selector: 'app-property-owners-dialog',
  imports: [
    FormsModule,
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatListModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './property-owners-dialog.html',
  styleUrl: './property-owners-dialog.scss',
})
export class PropertyOwnersDialog {
  protected readonly data = inject<PropertyOwnersDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<PropertyOwnersDialog>);
  private readonly dialog = inject(MatDialog);
  private readonly propertiesService = inject(PropertiesService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);

  protected readonly state = signal<LoadState>('loading');
  protected readonly owners = signal<PropertyOwnerResponse[]>([]);
  protected readonly busy = signal(false);
  protected readonly ownerUserIdControl = new FormControl('', { nonNullable: true, validators: [Validators.required] });

  constructor() {
    this.loadOwners();
  }

  protected loadOwners(): void {
    this.state.set('loading');
    this.propertiesService.listOwners(this.data.propertyId, 1, 100).subscribe({
      next: (page) => {
        const items = page.items ?? [];
        this.owners.set(items);
        this.state.set(items.length === 0 ? 'empty' : 'loaded');
      },
      error: () => this.state.set('error'),
    });
  }

  protected linkOwner(): void {
    if (this.busy() || this.ownerUserIdControl.invalid) {
      this.ownerUserIdControl.markAsTouched();
      return;
    }

    this.busy.set(true);
    this.propertiesService.linkOwner(this.data.propertyId, { ownerUserId: this.ownerUserIdControl.value }).subscribe({
      next: () => {
        this.busy.set(false);
        this.ownerUserIdControl.reset('');
        this.snackBar.open(this.transloco.translate('propertyManagement.ownership.linkedSuccess'), undefined, { duration: 3000 });
        this.loadOwners();
      },
      error: (error: unknown) => this.handleError(error),
    });
  }

  protected confirmRemove(owner: PropertyOwnerResponse): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        titleKey: 'propertyManagement.ownership.removeConfirmTitle',
        messageKey: 'propertyManagement.ownership.removeConfirmMessage',
        messageParams: { ownerUserId: owner.ownerUserId ?? '' },
        confirmKey: 'common.remove',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (confirmed) this.removeOwner(owner.ownerUserId!);
    });
  }

  private removeOwner(ownerUserId: string): void {
    this.busy.set(true);
    this.propertiesService.unlinkOwner(this.data.propertyId, ownerUserId).subscribe({
      next: () => {
        this.busy.set(false);
        this.snackBar.open(this.transloco.translate('propertyManagement.ownership.removedSuccess'), undefined, { duration: 3000 });
        this.loadOwners();
      },
      error: (error: unknown) => this.handleError(error),
    });
  }

  private handleError(error: unknown): void {
    this.busy.set(false);
    const { status } = classifyPropertyManagementError(error);
    const key =
      status === 409
        ? 'propertyManagement.ownership.errors.conflict'
        : status === 404
          ? 'propertyManagement.ownership.errors.notFound'
          : 'propertyManagement.ownership.errors.generic';
    this.snackBar.open(this.transloco.translate(key), undefined, { duration: 4000 });
  }

  protected close(): void {
    this.dialogRef.close();
  }
}
