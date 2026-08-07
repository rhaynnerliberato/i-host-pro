import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { ReservationDetailResponse, ReservationSummaryResponse } from '../../../core/api/generated/api-client';
import { ConfirmDialog, ConfirmDialogData } from '../../users/confirm-dialog/confirm-dialog';
import { classifyReservationError } from '../reservation-error';
import { ReservationFormDialog, ReservationFormDialogData } from '../reservation-form-dialog/reservation-form-dialog';
import { ReservationsService } from '../reservations.service';

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

@Component({
  selector: 'app-reservations-list',
  imports: [
    DatePipe,
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatChipsModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatMenuModule,
    MatPaginatorModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTableModule,
  ],
  templateUrl: './reservations-list.html',
  styleUrl: './reservations-list.scss',
})
export class ReservationsList {
  private readonly reservationsService = inject(ReservationsService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);
  private readonly formBuilder = inject(FormBuilder);

  // ReservationSummaryResponse never exposes guestPhone (only ReservationDetailResponse does) — the backend's own listing contract.
  protected readonly displayedColumns = ['guestName', 'propertyId', 'checkInAt', 'checkOutAt', 'guestCount', 'status', 'actions'];

  protected readonly state = signal<LoadState>('loading');
  protected readonly reservations = signal<ReservationSummaryResponse[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly pageIndex = signal(0);
  protected readonly pageSize = signal(10);

  /** propertyId is a raw GUID field, not a selector — see Fase 4 doc Section 13.1 for why (GET /api/v1/properties requires PROPERTIES:MANAGE, which OPERATOR does not hold, but OPERATOR does hold RESERVATIONS:MANAGE and must be able to filter/create). from/to filter by day; period-intersection semantics are the backend's (§13.4). */
  protected readonly filterForm = this.formBuilder.nonNullable.group({
    propertyId: [''],
    status: [''],
    from: [''],
    to: [''],
  });

  constructor() {
    this.loadReservations();
  }

  protected loadReservations(): void {
    this.state.set('loading');
    const { propertyId, status, from, to } = this.filterForm.getRawValue();
    this.reservationsService
      .list(
        this.pageIndex() + 1,
        this.pageSize(),
        propertyId || undefined,
        status || undefined,
        from ? new Date(from) : undefined,
        to ? new Date(to) : undefined,
      )
      .subscribe({
        next: (page) => {
          const items = page.items ?? [];
          this.reservations.set(items);
          this.totalCount.set(page.totalCount ?? 0);
          this.state.set(items.length === 0 ? 'empty' : 'loaded');
        },
        error: () => this.state.set('error'),
      });
  }

  protected applyFilters(): void {
    this.pageIndex.set(0);
    this.loadReservations();
  }

  protected onPageChange(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.loadReservations();
  }

  protected statusLabelKey(status: string | undefined): string {
    return status === 'cancelled' ? 'reservations.list.statusCancelled' : 'reservations.list.statusConfirmed';
  }

  /** Mirrors the backend's own guard (UpdateReservationCommandHandler rejects any PATCH on a Cancelled reservation with CancelledReservationCannotBeModified; Cancel itself rejects a second cancellation with ReservationAlreadyCancelled) — presentation only, the backend remains the sole authority. */
  protected canEdit(status: string | undefined): boolean {
    return status === 'confirmed';
  }

  protected canCancel(status: string | undefined): boolean {
    return status === 'confirmed';
  }

  protected openCreateDialog(): void {
    const ref = this.dialog.open<ReservationFormDialog, ReservationFormDialogData, ReservationDetailResponse>(ReservationFormDialog, {
      data: {},
      width: '640px',
    });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.snackBar.open(this.transloco.translate('reservations.list.createdSuccess'), undefined, { duration: 3000 });
        this.loadReservations();
      }
    });
  }

  protected openEditDialog(reservationId: string): void {
    this.reservationsService.getById(reservationId).subscribe({
      next: (reservation) => {
        const ref = this.dialog.open<ReservationFormDialog, ReservationFormDialogData, ReservationDetailResponse>(ReservationFormDialog, {
          data: { reservation },
          width: '640px',
        });
        ref.afterClosed().subscribe((updated) => {
          if (updated) {
            this.snackBar.open(this.transloco.translate('reservations.list.updatedSuccess'), undefined, { duration: 3000 });
            this.loadReservations();
          }
        });
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  protected confirmCancel(reservation: ReservationSummaryResponse): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        titleKey: 'reservations.list.cancelConfirmTitle',
        messageKey: 'reservations.list.cancelConfirmMessage',
        messageParams: { name: reservation.guestName ?? '' },
        confirmKey: 'reservations.list.cancel',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.reservationsService.cancel(reservation.id!).subscribe({
        next: () => {
          this.snackBar.open(this.transloco.translate('reservations.list.cancelledSuccess'), undefined, { duration: 3000 });
          this.loadReservations();
        },
        error: (error: unknown) => this.showActionError(error),
      });
    });
  }

  private showActionError(error: unknown): void {
    const { status } = classifyReservationError(error);
    const key =
      status === 409
        ? 'reservations.list.errors.conflict'
        : status === 404
          ? 'reservations.list.errors.notFound'
          : 'reservations.list.errors.generic';
    this.snackBar.open(this.transloco.translate(key), undefined, { duration: 4000 });
  }
}
