import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { take } from 'rxjs';

import { AirbnbListingTitleMappingResponse } from '../../../../core/api/generated/api-client';
import { ConfirmDialog, ConfirmDialogData } from '../../../users/confirm-dialog/confirm-dialog';
import {
  AirbnbEmailMailboxStatus,
  AirbnbEmailProcessingSummary,
  AirbnbEmailService,
} from '../airbnb-email.service';
import { EnableAutoPublicationDialog } from '../enable-auto-publication-dialog/enable-auto-publication-dialog';
import { MappingFormDialog } from '../mapping-form-dialog/mapping-form-dialog';

type LoadState = 'loading' | 'loaded' | 'error';

interface AutoPublicationView {
  enabled: boolean;
  notBeforeUtc: Date | undefined;
}

/** Bounded result codes the oauth/callback redirect appends — never anything else (Web OAuth architecture gate, item 21). */
type ConnectResultCode = 'success' | 'denied' | 'expired' | 'error';

/**
 * Airbnb Email Bridge Minimal Operations/UX gate — one compact page, four
 * sections (Connection / Automatic publication / Listing mappings /
 * Processing attention), each with its own independent load state so one
 * section's failure never blocks the others.
 *
 * Web OAuth architecture gate: the "Connect" action IS now exposed here —
 * self-service Authorization Code + PKCE, no developer/engineering setup
 * step. It only ever appears when there is no active connection
 * (NotConfigured/Disconnected/Error); a Connected mailbox never shows it
 * (see the template's own status guard). Clicking it does a full-page
 * navigation to the backend-issued Microsoft authorization URL — this
 * component never generates, sees, or stores any `state`/PKCE material
 * itself.
 */
@Component({
  selector: 'app-airbnb-email-page',
  imports: [DatePipe, TranslocoPipe, MatButtonModule, MatCardModule, MatChipsModule, MatIconModule, MatProgressSpinnerModule, MatTableModule],
  templateUrl: './airbnb-email-page.html',
  styleUrl: './airbnb-email-page.scss',
})
export class AirbnbEmailPage {
  private readonly airbnbEmailService = inject(AirbnbEmailService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly displayedMappingColumns = ['listingTitle', 'propertyId'];

  protected readonly connectionState = signal<LoadState>('loading');
  protected readonly connection = signal<AirbnbEmailMailboxStatus | null>(null);
  protected readonly connecting = signal(false);

  protected readonly autoPublicationState = signal<LoadState>('loading');
  protected readonly autoPublication = signal<AutoPublicationView | null>(null);

  protected readonly mappingsState = signal<LoadState>('loading');
  protected readonly mappings = signal<AirbnbListingTitleMappingResponse[]>([]);

  protected readonly summaryState = signal<LoadState>('loading');
  protected readonly summary = signal<AirbnbEmailProcessingSummary | null>(null);

  constructor() {
    this.handleConnectCallbackResult();
    this.loadConnection();
    this.loadAutoPublication();
    this.loadMappings();
    this.loadSummary();
  }

  /**
   * Reads the bounded `connect` result code the oauth/callback redirect may
   * have appended, shows a one-time toast for it, then strips it from the
   * URL — never re-shown on a later reload/refresh of this same page.
   *
   * Uses `selectTranslate` (never the synchronous `translate()`), mirroring
   * ScheduleCalendar's own precedent: this runs from the constructor, before
   * the i18n JSON's async HTTP load is guaranteed to have completed —
   * `translate()` would silently return the raw key in that window and the
   * snackbar, being a one-shot imperative call, would never self-correct
   * once the real translation arrived.
   */
  private handleConnectCallbackResult(): void {
    const resultCode = this.route.snapshot.queryParamMap.get('connect') as ConnectResultCode | null;
    if (!resultCode) return;

    const messageKeys: Record<ConnectResultCode, string> = {
      success: 'integrations.airbnbEmail.connection.connectSuccess',
      denied: 'integrations.airbnbEmail.connection.connectDenied',
      expired: 'integrations.airbnbEmail.connection.connectExpired',
      error: 'integrations.airbnbEmail.connection.connectError',
    };
    const messageKey = messageKeys[resultCode] ?? messageKeys.error;

    this.transloco
      .selectTranslate(messageKey)
      .pipe(take(1))
      .subscribe((message) => this.snackBar.open(message, undefined, { duration: 5000 }));

    this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }

  protected connect(): void {
    if (this.connecting()) return;

    this.connecting.set(true);
    this.airbnbEmailService.connect().subscribe({
      next: (authorizationUrl) => {
        if (!authorizationUrl) {
          this.connecting.set(false);
          this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.connection.errors.generic'), undefined, { duration: 4000 });
          return;
        }
        window.location.href = authorizationUrl;
      },
      error: () => {
        this.connecting.set(false);
        this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.connection.errors.generic'), undefined, { duration: 4000 });
      },
    });
  }

  protected loadConnection(): void {
    this.connectionState.set('loading');
    this.airbnbEmailService.getStatus().subscribe({
      next: (status) => {
        this.connection.set(status);
        this.connectionState.set('loaded');
      },
      error: () => this.connectionState.set('error'),
    });
  }

  protected loadAutoPublication(): void {
    this.autoPublicationState.set('loading');
    this.airbnbEmailService.getAutoPublicationStatus().subscribe({
      next: (status) => {
        this.autoPublication.set({ enabled: status.autoPublishEnabled ?? false, notBeforeUtc: status.autoPublishNotBeforeUtc });
        this.autoPublicationState.set('loaded');
      },
      error: () => this.autoPublicationState.set('error'),
    });
  }

  protected loadMappings(): void {
    this.mappingsState.set('loading');
    this.airbnbEmailService.listMappings().subscribe({
      next: (mappings) => {
        this.mappings.set(mappings);
        this.mappingsState.set('loaded');
      },
      error: () => this.mappingsState.set('error'),
    });
  }

  protected loadSummary(): void {
    this.summaryState.set('loading');
    this.airbnbEmailService.getProcessingSummary().subscribe({
      next: (summary) => {
        this.summary.set(summary);
        this.summaryState.set('loaded');
      },
      error: () => this.summaryState.set('error'),
    });
  }

  protected confirmDisconnect(): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        titleKey: 'integrations.airbnbEmail.connection.disconnectConfirmTitle',
        messageKey: 'integrations.airbnbEmail.connection.disconnectConfirmMessage',
        confirmKey: 'integrations.airbnbEmail.connection.disconnect',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.airbnbEmailService.disconnect().subscribe({
        next: () => {
          this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.connection.disconnectedSuccess'), undefined, { duration: 3000 });
          this.loadConnection();
        },
        error: () => this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.connection.errors.generic'), undefined, { duration: 4000 }),
      });
    });
  }

  protected openEnableAutoPublicationDialog(): void {
    const ref = this.dialog.open(EnableAutoPublicationDialog);
    ref.afterClosed().subscribe((enabled) => {
      if (!enabled) return;
      this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.autoPublication.enabledSuccess'), undefined, { duration: 3000 });
      this.loadAutoPublication();
    });
  }

  protected confirmDisableAutoPublication(): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        titleKey: 'integrations.airbnbEmail.autoPublication.disableConfirmTitle',
        messageKey: 'integrations.airbnbEmail.autoPublication.disableConfirmMessage',
        confirmKey: 'integrations.airbnbEmail.autoPublication.disable',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.airbnbEmailService.disableAutoPublication().subscribe({
        next: () => {
          this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.autoPublication.disabledSuccess'), undefined, { duration: 3000 });
          this.loadAutoPublication();
        },
        error: () =>
          this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.autoPublication.errors.generic'), undefined, { duration: 4000 }),
      });
    });
  }

  protected openCreateMappingDialog(): void {
    const ref = this.dialog.open(MappingFormDialog, { width: '480px' });
    ref.afterClosed().subscribe((created) => {
      if (!created) return;
      this.snackBar.open(this.transloco.translate('integrations.airbnbEmail.mappings.createdSuccess'), undefined, { duration: 3000 });
      this.loadMappings();
    });
  }
}
