import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { TranslocoPipe } from '@jsverse/transloco';

import { CleaningDetailResponse } from '../../../core/api/generated/api-client';

export interface CleaningDetailDialogData {
  cleaning: CleaningDetailResponse;
}

/** Read-only view of a Cleaning's full administrative detail — every timestamp `CleaningSummaryResponse` (the list row's own shape) omits. */
@Component({
  selector: 'app-cleaning-detail-dialog',
  imports: [DatePipe, TranslocoPipe, MatButtonModule, MatDialogModule],
  templateUrl: './cleaning-detail-dialog.html',
  styleUrl: './cleaning-detail-dialog.scss',
})
export class CleaningDetailDialog {
  protected readonly data = inject<CleaningDetailDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<CleaningDetailDialog>);

  protected close(): void {
    this.dialogRef.close();
  }
}
