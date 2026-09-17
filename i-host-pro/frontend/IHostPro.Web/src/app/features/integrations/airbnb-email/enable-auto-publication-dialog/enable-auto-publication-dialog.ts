import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe } from '@jsverse/transloco';

import { AirbnbEmailService } from '../airbnb-email.service';

/**
 * The cutoff is always "now, at the moment of activation" — never a
 * historical date the operator could pick (ChatGPT decision item 11 for
 * this gate: "Do NOT default to historical date"). The operator only
 * confirms; there is no date field to fill in.
 */
@Component({
  selector: 'app-enable-auto-publication-dialog',
  imports: [TranslocoPipe, MatButtonModule, MatDialogModule, MatProgressSpinnerModule],
  templateUrl: './enable-auto-publication-dialog.html',
})
export class EnableAutoPublicationDialog {
  private readonly dialogRef = inject(MatDialogRef<EnableAutoPublicationDialog>);
  private readonly airbnbEmailService = inject(AirbnbEmailService);

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected confirm(): void {
    if (this.submitting()) return;

    this.submitting.set(true);
    this.errorKey.set(null);

    this.airbnbEmailService.enableAutoPublication(new Date()).subscribe({
      next: (status) => {
        this.submitting.set(false);
        this.dialogRef.close(status);
      },
      error: () => {
        this.submitting.set(false);
        this.errorKey.set('integrations.airbnbEmail.autoPublication.enableDialog.errors.generic');
      },
    });
  }

  protected cancel(): void {
    this.dialogRef.close();
  }
}
