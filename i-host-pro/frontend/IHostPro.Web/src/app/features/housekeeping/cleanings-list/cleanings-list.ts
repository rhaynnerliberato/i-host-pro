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
import { Observable } from 'rxjs';

import { CleaningDetailResponse, CleaningSummaryResponse } from '../../../core/api/generated/api-client';
import { ConfirmDialog, ConfirmDialogData } from '../../users/confirm-dialog/confirm-dialog';
import { AssignCleaningDialog, AssignCleaningDialogData } from '../assign-cleaning-dialog/assign-cleaning-dialog';
import { CleaningDetailDialog, CleaningDetailDialogData } from '../cleaning-detail-dialog/cleaning-detail-dialog';
import { CleaningFormDialog } from '../cleaning-form-dialog/cleaning-form-dialog';
import { classifyHousekeepingError } from '../housekeeping-error';
import { HousekeepingService } from '../housekeeping.service';

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

/** Mirrors `CleaningStatus` (Domain, Documento 06 §5) — the exact stable codes `CleaningStatusCodeMapper` exposes on the wire. */
const STATUS_CODES = [
  'Pending',
  'Assigned',
  'InTransit',
  'Started',
  'InInspection',
  'Completed',
  'Cancelled',
  'Interrupted',
  'WaitingMaterials',
  'WaitingHelp',
] as const;

@Component({
  selector: 'app-cleanings-list',
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
  templateUrl: './cleanings-list.html',
  styleUrl: './cleanings-list.scss',
})
export class CleaningsList {
  private readonly housekeepingService = inject(HousekeepingService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly statusCodes = STATUS_CODES;
  protected readonly displayedColumns = ['propertyId', 'reservationId', 'assignedHousekeeperUserId', 'status', 'createdAtUtc', 'actions'];

  protected readonly state = signal<LoadState>('loading');
  protected readonly cleanings = signal<CleaningSummaryResponse[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly pageIndex = signal(0);
  protected readonly pageSize = signal(10);

  /** propertyId/assignedHousekeeperUserId are raw GUID fields, not selectors — same rationale as ReservationsList's own propertyId filter. */
  protected readonly filterForm = this.formBuilder.nonNullable.group({
    status: [''],
    propertyId: [''],
    assignedHousekeeperUserId: [''],
  });

  constructor() {
    this.loadCleanings();
  }

  protected loadCleanings(): void {
    this.state.set('loading');
    const { status, propertyId, assignedHousekeeperUserId } = this.filterForm.getRawValue();
    this.housekeepingService
      .list(this.pageIndex() + 1, this.pageSize(), status || undefined, propertyId || undefined, assignedHousekeeperUserId || undefined)
      .subscribe({
        next: (page) => {
          const items = page.items ?? [];
          this.cleanings.set(items);
          this.totalCount.set(page.totalCount ?? 0);
          this.state.set(items.length === 0 ? 'empty' : 'loaded');
        },
        error: () => this.state.set('error'),
      });
  }

  protected applyFilters(): void {
    this.pageIndex.set(0);
    this.loadCleanings();
  }

  protected onPageChange(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.loadCleanings();
  }

  protected statusLabelKey(status: string | undefined): string {
    return `housekeeping.list.status.${status ?? 'Pending'}`;
  }

  // Every guard below mirrors Cleaning's own domain guard (Domain/Cleaning.cs)
  // exactly — presentation only, the backend remains the sole authority.
  protected canAssign(status: string | undefined): boolean {
    return status === 'Pending';
  }

  protected canStart(status: string | undefined): boolean {
    return status === 'Assigned';
  }

  protected canStartInspection(status: string | undefined): boolean {
    return status === 'Started';
  }

  protected canComplete(status: string | undefined): boolean {
    return status === 'InInspection';
  }

  protected canCancel(status: string | undefined): boolean {
    return status === 'Pending' || status === 'Assigned';
  }

  protected canMarkInterrupted(status: string | undefined): boolean {
    return status === 'Started';
  }

  protected canMarkWaitingMaterials(status: string | undefined): boolean {
    return status === 'Started';
  }

  protected canMarkWaitingHelp(status: string | undefined): boolean {
    return status === 'Started';
  }

  /** Whether the row's menu holds any enabled action at all — controls whether the trigger button itself is shown. */
  protected hasAnyAction(status: string | undefined): boolean {
    return (
      this.canAssign(status) ||
      this.canStart(status) ||
      this.canStartInspection(status) ||
      this.canComplete(status) ||
      this.canCancel(status) ||
      this.canMarkInterrupted(status) ||
      this.canMarkWaitingMaterials(status) ||
      this.canMarkWaitingHelp(status)
    );
  }

  protected openCreateDialog(): void {
    const ref = this.dialog.open<CleaningFormDialog, void, CleaningDetailResponse>(CleaningFormDialog, { width: '640px' });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.snackBar.open(this.transloco.translate('housekeeping.list.createdSuccess'), undefined, { duration: 3000 });
        this.loadCleanings();
      }
    });
  }

  protected openDetailDialog(cleaningId: string): void {
    this.housekeepingService.getById(cleaningId).subscribe({
      next: (cleaning) => {
        this.dialog.open<CleaningDetailDialog, CleaningDetailDialogData>(CleaningDetailDialog, { data: { cleaning }, width: '520px' });
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  protected openAssignDialog(cleaning: CleaningSummaryResponse): void {
    const ref = this.dialog.open<AssignCleaningDialog, AssignCleaningDialogData, CleaningDetailResponse>(AssignCleaningDialog, {
      data: { cleaningId: cleaning.id! },
      width: '480px',
    });
    ref.afterClosed().subscribe((assigned) => {
      if (assigned) {
        this.snackBar.open(this.transloco.translate('housekeeping.list.assignedSuccess'), undefined, { duration: 3000 });
        this.loadCleanings();
      }
    });
  }

  protected start(cleaning: CleaningSummaryResponse): void {
    this.runTransition(this.housekeepingService.start(cleaning.id!), 'housekeeping.list.startedSuccess');
  }

  protected startInspection(cleaning: CleaningSummaryResponse): void {
    this.runTransition(this.housekeepingService.startInspection(cleaning.id!), 'housekeeping.list.inspectionStartedSuccess');
  }

  protected complete(cleaning: CleaningSummaryResponse): void {
    this.runTransition(this.housekeepingService.complete(cleaning.id!), 'housekeeping.list.completedSuccess');
  }

  protected markInterrupted(cleaning: CleaningSummaryResponse): void {
    this.runTransition(this.housekeepingService.markInterrupted(cleaning.id!), 'housekeeping.list.interruptedSuccess');
  }

  protected markWaitingMaterials(cleaning: CleaningSummaryResponse): void {
    this.runTransition(this.housekeepingService.markWaitingMaterials(cleaning.id!), 'housekeeping.list.waitingMaterialsSuccess');
  }

  protected markWaitingHelp(cleaning: CleaningSummaryResponse): void {
    this.runTransition(this.housekeepingService.markWaitingHelp(cleaning.id!), 'housekeeping.list.waitingHelpSuccess');
  }

  protected confirmCancel(cleaning: CleaningSummaryResponse): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        titleKey: 'housekeeping.list.cancelConfirmTitle',
        messageKey: 'housekeeping.list.cancelConfirmMessage',
        messageParams: { propertyId: cleaning.propertyId ?? '' },
        confirmKey: 'housekeeping.list.cancel',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (confirmed) this.runTransition(this.housekeepingService.cancel(cleaning.id!), 'housekeeping.list.cancelledSuccess');
    });
  }

  private runTransition(request$: Observable<CleaningDetailResponse>, successKey: string): void {
    request$.subscribe({
      next: () => {
        this.snackBar.open(this.transloco.translate(successKey), undefined, { duration: 3000 });
        this.loadCleanings();
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  private showActionError(error: unknown): void {
    const { status, code } = classifyHousekeepingError(error);
    const key =
      status === 403
        ? 'housekeeping.list.errors.forbidden'
        : status === 409 && code === 'cleaning_concurrency_conflict'
          ? 'housekeeping.list.errors.concurrency'
          : status === 409
            ? 'housekeeping.list.errors.conflict'
            : status === 404
              ? 'housekeeping.list.errors.notFound'
              : 'housekeeping.list.errors.generic';
    this.snackBar.open(this.transloco.translate(key), undefined, { duration: 4000 });
  }
}
