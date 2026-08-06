import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { CondominiumSummaryResponse, PropertyDetailResponse, PropertySummaryResponse } from '../../../../core/api/generated/api-client';
import { ConfirmDialog, ConfirmDialogData } from '../../../users/confirm-dialog/confirm-dialog';
import { classifyPropertyManagementError } from '../../property-management-error';
import { CondominiumsService } from '../../condominiums.service';
import { PropertiesService } from '../../properties.service';
import { PropertyOwnersDialog, PropertyOwnersDialogData } from '../../ownership/property-owners-dialog/property-owners-dialog';
import { PropertyFormDialog, PropertyFormDialogData } from '../property-form-dialog/property-form-dialog';

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

@Component({
  selector: 'app-properties-list',
  imports: [
    TranslocoPipe,
    MatButtonModule,
    MatChipsModule,
    MatIconModule,
    MatMenuModule,
    MatPaginatorModule,
    MatProgressSpinnerModule,
    MatTableModule,
  ],
  templateUrl: './properties-list.html',
  styleUrl: './properties-list.scss',
})
export class PropertiesList {
  private readonly propertiesService = inject(PropertiesService);
  private readonly condominiumsService = inject(CondominiumsService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);

  protected readonly displayedColumns = ['code', 'name', 'capacity', 'status', 'actions'];

  protected readonly state = signal<LoadState>('loading');
  protected readonly properties = signal<PropertySummaryResponse[]>([]);
  protected readonly totalCount = signal(0);
  protected readonly pageIndex = signal(0);
  protected readonly pageSize = signal(10);
  protected readonly condominiums = signal<CondominiumSummaryResponse[]>([]);

  constructor() {
    this.condominiumsService.list(1, 100).subscribe({
      next: (page) => this.condominiums.set(page.items ?? []),
      // The condominium catalog is only needed for the create/edit selector — a failure here must never block the list itself.
      error: () => this.condominiums.set([]),
    });

    this.loadProperties();
  }

  protected loadProperties(): void {
    this.state.set('loading');
    this.propertiesService.list(this.pageIndex() + 1, this.pageSize()).subscribe({
      next: (page) => {
        const items = page.items ?? [];
        this.properties.set(items);
        this.totalCount.set(page.totalCount ?? 0);
        this.state.set(items.length === 0 ? 'empty' : 'loaded');
      },
      error: () => this.state.set('error'),
    });
  }

  protected onPageChange(event: PageEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.loadProperties();
  }

  protected statusLabelKey(status: string | undefined): string {
    switch (status) {
      case 'active':
        return 'propertyManagement.properties.list.statusActive';
      case 'inactive':
        return 'propertyManagement.properties.list.statusInactive';
      case 'archived':
        return 'propertyManagement.properties.list.statusArchived';
      default:
        return 'propertyManagement.properties.list.statusDraft';
    }
  }

  /** Mirrors Property.Activate/Deactivate/Archive's own guards (Domain) — presentation only, the backend remains the sole authority. */
  protected canActivate(status: string | undefined): boolean {
    return status === 'draft' || status === 'inactive';
  }

  protected canDeactivate(status: string | undefined): boolean {
    return status === 'active';
  }

  protected canArchive(status: string | undefined): boolean {
    return status === 'draft' || status === 'inactive';
  }

  protected canEdit(status: string | undefined): boolean {
    return status !== 'archived';
  }

  protected openCreateDialog(): void {
    const ref = this.dialog.open<PropertyFormDialog, PropertyFormDialogData, PropertyDetailResponse>(PropertyFormDialog, {
      data: { condominiums: this.condominiums() },
      width: '640px',
    });
    ref.afterClosed().subscribe((created) => {
      if (created) {
        this.snackBar.open(this.transloco.translate('propertyManagement.properties.list.createdSuccess'), undefined, { duration: 3000 });
        this.loadProperties();
      }
    });
  }

  protected openEditDialog(propertyId: string): void {
    this.propertiesService.getById(propertyId).subscribe({
      next: (property) => {
        const ref = this.dialog.open<PropertyFormDialog, PropertyFormDialogData, PropertyDetailResponse>(PropertyFormDialog, {
          data: { property, condominiums: this.condominiums() },
          width: '640px',
        });
        ref.afterClosed().subscribe((updated) => {
          if (updated) {
            this.snackBar.open(this.transloco.translate('propertyManagement.properties.list.updatedSuccess'), undefined, { duration: 3000 });
            this.loadProperties();
          }
        });
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  protected openOwnersDialog(property: PropertySummaryResponse): void {
    const ref = this.dialog.open<PropertyOwnersDialog, PropertyOwnersDialogData, void>(PropertyOwnersDialog, {
      data: { propertyId: property.id!, propertyName: property.name ?? '' },
      width: '560px',
    });
    ref.afterClosed().subscribe(() => this.loadProperties());
  }

  protected activate(propertyId: string): void {
    this.propertiesService.activate(propertyId).subscribe({
      next: () => {
        this.snackBar.open(this.transloco.translate('propertyManagement.properties.list.activatedSuccess'), undefined, { duration: 3000 });
        this.loadProperties();
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  protected deactivate(propertyId: string): void {
    this.propertiesService.deactivate(propertyId).subscribe({
      next: () => {
        this.snackBar.open(this.transloco.translate('propertyManagement.properties.list.deactivatedSuccess'), undefined, { duration: 3000 });
        this.loadProperties();
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  protected confirmArchive(property: PropertySummaryResponse): void {
    const ref = this.dialog.open<ConfirmDialog, ConfirmDialogData, boolean>(ConfirmDialog, {
      data: {
        titleKey: 'propertyManagement.properties.list.archiveConfirmTitle',
        messageKey: 'propertyManagement.properties.list.archiveConfirmMessage',
        messageParams: { name: property.name ?? '' },
        confirmKey: 'propertyManagement.properties.list.archive',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.propertiesService.archive(property.id!).subscribe({
        next: () => {
          this.snackBar.open(this.transloco.translate('propertyManagement.properties.list.archivedSuccess'), undefined, { duration: 3000 });
          this.loadProperties();
        },
        error: (error: unknown) => this.showActionError(error),
      });
    });
  }

  private showActionError(error: unknown): void {
    const { status } = classifyPropertyManagementError(error);
    const key =
      status === 409
        ? 'propertyManagement.properties.list.errors.conflict'
        : status === 404
          ? 'propertyManagement.properties.list.errors.notFound'
          : 'propertyManagement.properties.list.errors.generic';
    this.snackBar.open(this.transloco.translate(key), undefined, { duration: 4000 });
  }
}
