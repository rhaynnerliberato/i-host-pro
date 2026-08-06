import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { CondominiumDetailResponse, CondominiumSummaryResponse } from '../../../../core/api/generated/api-client';
import { CondominiumsService } from '../../condominiums.service';
import { CondominiumFormDialog, CondominiumFormDialogData } from '../condominium-form-dialog/condominium-form-dialog';

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

@Component({
  selector: 'app-condominiums-list',
  imports: [
    DatePipe,
    TranslocoPipe,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
    MatProgressSpinnerModule,
    MatTableModule,
  ],
  templateUrl: './condominiums-list.html',
  styleUrl: './condominiums-list.scss',
})
export class CondominiumsList {
  private readonly condominiumsService = inject(CondominiumsService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);

  // CondominiumSummaryResponse carries no address — only detail view/edit resolves it.
  protected readonly displayedColumns = ['name', 'createdAt', 'actions'];

  protected readonly state = signal<LoadState>('loading');
  protected readonly condominiums = signal<CondominiumSummaryResponse[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly pageIndex = signal(0);
  protected readonly pageSize = signal(10);

  constructor() {
    this.loadCondominiums();
  }

  protected loadCondominiums(): void {
    this.state.set('loading');
    this.condominiumsService.list(this.pageIndex() + 1, this.pageSize()).subscribe({
      next: (page) => {
        const items = page.items ?? [];
        this.condominiums.set(items);
        this.totalCount.set(page.totalCount ?? 0);
        this.state.set(items.length === 0 ? 'empty' : 'loaded');
      },
      error: () => this.state.set('error'),
    });
  }

  protected onPageChange(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.loadCondominiums();
  }

  protected openCreateDialog(): void {
    const ref = this.dialog.open<CondominiumFormDialog, CondominiumFormDialogData, CondominiumDetailResponse>(CondominiumFormDialog, {
      data: {},
      width: '560px',
    });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.snackBar.open(this.transloco.translate('propertyManagement.condominiums.list.createdSuccess'), undefined, { duration: 3000 });
        this.loadCondominiums();
      }
    });
  }

  protected openEditDialog(condominiumId: string): void {
    this.condominiumsService.getById(condominiumId).subscribe({
      next: (condominium) => {
        const ref = this.dialog.open<CondominiumFormDialog, CondominiumFormDialogData, CondominiumDetailResponse>(CondominiumFormDialog, {
          data: { condominium },
          width: '560px',
        });
        ref.afterClosed().subscribe((updated) => {
          if (updated) {
            this.snackBar.open(this.transloco.translate('propertyManagement.condominiums.list.updatedSuccess'), undefined, { duration: 3000 });
            this.loadCondominiums();
          }
        });
      },
      error: () => this.snackBar.open(this.transloco.translate('propertyManagement.condominiums.list.errors.generic'), undefined, { duration: 4000 }),
    });
  }
}
