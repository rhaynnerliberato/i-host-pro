import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AirbnbEmailReceipt, AirbnbEmailService } from '../airbnb-email.service';
import { MappingFormDialog, MappingFormDialogData } from '../mapping-form-dialog/mapping-form-dialog';

export interface ReceiptDetailDialogData {
  receipt: AirbnbEmailReceipt;
}

/**
 * Airbnb Email Operational Exception Resolution gate — safe, read-only
 * receipt detail plus the two approved conditional actions. Never shows the
 * raw Graph message id, internet message id, email body, or guest PII — none
 * of those are ever sent by the backend in the first place (Section 7/22 of
 * the approved design), so there is nothing to filter here beyond simply not
 * inventing fields the API never returns.
 *
 * Closing with `true` tells the caller (the exception list) to reload — used
 * whenever this dialog's own state changed the receipt (a retry) or a
 * mapping was created that a retry from here might resolve.
 */
@Component({
  selector: 'app-receipt-detail-dialog',
  imports: [DatePipe, TranslocoPipe, MatButtonModule, MatDialogModule, MatProgressSpinnerModule],
  templateUrl: './receipt-detail-dialog.html',
  styleUrl: './receipt-detail-dialog.scss',
})
export class ReceiptDetailDialog {
  protected readonly data = inject<ReceiptDetailDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ReceiptDetailDialog, boolean>);
  private readonly dialog = inject(MatDialog);
  private readonly airbnbEmailService = inject(AirbnbEmailService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);

  protected readonly receipt = signal(this.data.receipt);
  protected readonly retrying = signal(false);
  private changed = false;

  protected canCreateMapping(): boolean {
    return this.receipt().processingStatus === 'NeedsReview';
  }

  protected canRetry(): boolean {
    const r = this.receipt();
    return r.processingStatus === 'NeedsReview' || (r.processingStatus === 'Failed' && r.failureReason === 'PublisherFailure');
  }

  protected openCreateMappingDialog(): void {
    const ref = this.dialog.open<MappingFormDialog, MappingFormDialogData>(MappingFormDialog, {
      width: '480px',
      data: { listingTitle: this.receipt().unmatchedListingTitle },
    });
    ref.afterClosed().subscribe((created) => {
      if (!created) return;
      this.changed = true;
      this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.mappings.createdSuccess'), undefined, { duration: 3000 });
    });
  }

  protected retry(): void {
    if (this.retrying()) return;
    this.retrying.set(true);

    this.airbnbEmailService.retryReceipt(this.receipt().id).subscribe({
      next: (updated) => {
        this.retrying.set(false);
        this.changed = true;
        this.receipt.set(updated);

        const successKey =
          updated.processingStatus === 'NeedsReview'
            ? 'integrations.airbnbEmail.exceptions.retry.stillNeedsReview'
            : 'integrations.airbnbEmail.exceptions.retry.success';
        this.snackBar.open(this.transloco.translate(successKey), undefined, { duration: 4000 });
      },
      error: (error: unknown) => {
        this.retrying.set(false);
        this.snackBar.open(this.transloco.translate(this.retryErrorKey(error)), undefined, { duration: 4000 });
      },
    });
  }

  private retryErrorKey(error: unknown): string {
    const status = error instanceof HttpErrorResponse ? error.status : 0;
    const code = error instanceof HttpErrorResponse ? (error.error?.title as string | undefined) : undefined;

    if (status === 409 && code === 'airbnb_email_receipt_mailbox_not_connected')
      return 'integrations.airbnbEmail.exceptions.retry.errors.mailboxNotConnected';
    if (status === 409 && code === 'airbnb_email_receipt_source_message_unavailable')
      return 'integrations.airbnbEmail.exceptions.retry.errors.sourceUnavailable';
    if (status === 409 && code === 'airbnb_email_receipt_retry_conflict')
      return 'integrations.airbnbEmail.exceptions.retry.errors.conflict';
    if (status === 409 && code === 'airbnb_email_receipt_not_retryable')
      return 'integrations.airbnbEmail.exceptions.retry.errors.notRetryable';
    if (status === 404) return 'integrations.airbnbEmail.exceptions.retry.errors.notFound';
    return 'integrations.airbnbEmail.exceptions.retry.errors.generic';
  }

  protected close(): void {
    this.dialogRef.close(this.changed);
  }
}
