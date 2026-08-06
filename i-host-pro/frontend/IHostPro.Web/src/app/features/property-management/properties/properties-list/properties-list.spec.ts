import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CondominiumSummaryResponse, PagedPropertyResponse, PropertyDetailResponse, PropertySummaryResponse } from '../../../../core/api/generated/api-client';
import { CondominiumsService } from '../../condominiums.service';
import { PropertiesService } from '../../properties.service';
import { PropertiesList } from './properties-list';

function configure(
  listResult: PagedPropertyResponse = { items: [], totalCount: 0 },
  condominiumsResult: CondominiumSummaryResponse[] = [],
) {
  const propertiesService = {
    list: vi.fn().mockReturnValue(of(listResult)),
    getById: vi.fn(),
    activate: vi.fn(),
    deactivate: vi.fn(),
    archive: vi.fn(),
  };
  const condominiumsService = { list: vi.fn().mockReturnValue(of({ items: condominiumsResult, totalCount: condominiumsResult.length })) };
  const nestedDialogRef = { afterClosed: () => of(undefined) };
  const dialog = { open: vi.fn().mockReturnValue(nestedDialogRef) };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };

  TestBed.configureTestingModule({
    providers: [
      { provide: PropertiesService, useValue: propertiesService },
      { provide: CondominiumsService, useValue: condominiumsService },
      { provide: MatDialog, useValue: dialog },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new PropertiesList());
  return { component, propertiesService, condominiumsService, dialog, snackBar };
}

describe('PropertiesList', () => {
  const property: PropertySummaryResponse = { id: 'p1', code: 'APT-1', name: 'Apto 1', capacity: 2, status: 'draft' };
  const condominium: CondominiumSummaryResponse = { id: 'c1', name: 'Edificio Sol' };

  it('loads properties and the condominium catalog on construction, ending in the loaded state when items exist', () => {
    const { component, propertiesService, condominiumsService } = configure({ items: [property], totalCount: 1 }, [condominium]);

    expect(propertiesService.list).toHaveBeenCalledTimes(1);
    expect(condominiumsService.list).toHaveBeenCalledWith(1, 100);
    expect(component['state']()).toBe('loaded');
    expect(component['properties']()).toEqual([property]);
    expect(component['condominiums']()).toEqual([condominium]);
  });

  it('ends in the empty state when the page has zero items', () => {
    const { component } = configure({ items: [], totalCount: 0 });

    expect(component['state']()).toBe('empty');
  });

  it('ends in the error state when the list request fails', () => {
    const propertiesService = { list: vi.fn().mockReturnValue(throwError(() => new Error('boom'))), getById: vi.fn(), activate: vi.fn(), deactivate: vi.fn(), archive: vi.fn() };
    const condominiumsService = { list: vi.fn().mockReturnValue(of({ items: [], totalCount: 0 })) };
    TestBed.configureTestingModule({
      providers: [
        { provide: PropertiesService, useValue: propertiesService },
        { provide: CondominiumsService, useValue: condominiumsService },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (k: string) => k } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new PropertiesList());

    expect(component['state']()).toBe('error');
  });

  it('a failed condominium-catalog load never blocks the property list itself', () => {
    const propertiesService = { list: vi.fn().mockReturnValue(of({ items: [property], totalCount: 1 })), getById: vi.fn(), activate: vi.fn(), deactivate: vi.fn(), archive: vi.fn() };
    const condominiumsService = { list: vi.fn().mockReturnValue(throwError(() => new Error('condos down'))) };
    TestBed.configureTestingModule({
      providers: [
        { provide: PropertiesService, useValue: propertiesService },
        { provide: CondominiumsService, useValue: condominiumsService },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (k: string) => k } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new PropertiesList());

    expect(component['state']()).toBe('loaded');
    expect(component['condominiums']()).toEqual([]);
  });

  it('onPageChange updates the page index/size and reloads', () => {
    const { component, propertiesService } = configure();
    propertiesService.list.mockClear();

    component['onPageChange']({ pageIndex: 2, pageSize: 25, length: 100 });

    expect(component['pageIndex']()).toBe(2);
    expect(component['pageSize']()).toBe(25);
    expect(propertiesService.list).toHaveBeenCalledWith(3, 25);
  });

  it('lifecycle guards only allow the transitions Property.Activate/Deactivate/Archive themselves allow', () => {
    const { component } = configure();

    expect(component['canActivate']('draft')).toBe(true);
    expect(component['canActivate']('inactive')).toBe(true);
    expect(component['canActivate']('active')).toBe(false);
    expect(component['canActivate']('archived')).toBe(false);

    expect(component['canDeactivate']('active')).toBe(true);
    expect(component['canDeactivate']('draft')).toBe(false);
    expect(component['canDeactivate']('archived')).toBe(false);

    expect(component['canArchive']('draft')).toBe(true);
    expect(component['canArchive']('inactive')).toBe(true);
    expect(component['canArchive']('active')).toBe(false);
    expect(component['canArchive']('archived')).toBe(false);

    expect(component['canEdit']('draft')).toBe(true);
    expect(component['canEdit']('active')).toBe(true);
    expect(component['canEdit']('archived')).toBe(false);
  });

  it('openCreateDialog passes the loaded condominium catalog, shows a success snackbar and reloads only on creation', () => {
    const created = { id: 'p2' } as PropertyDetailResponse;
    const { component, dialog, propertiesService, snackBar } = configure({ items: [], totalCount: 0 }, [condominium]);
    dialog.open.mockReturnValue({ afterClosed: () => of(created) });
    propertiesService.list.mockClear();

    component['openCreateDialog']();

    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { condominiums: [condominium] } }));
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.properties.list.createdSuccess', undefined, { duration: 3000 });
    expect(propertiesService.list).toHaveBeenCalledTimes(1);
  });

  it('openCreateDialog does not reload or notify when the dialog is dismissed without creating a property', () => {
    const { component, dialog, propertiesService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    propertiesService.list.mockClear();

    component['openCreateDialog']();

    expect(snackBar.open).not.toHaveBeenCalled();
    expect(propertiesService.list).not.toHaveBeenCalled();
  });

  it('openEditDialog fetches the property, opens the dialog pre-filled, and reloads on update', () => {
    const { component, dialog, propertiesService, snackBar } = configure();
    propertiesService.getById.mockReturnValue(of(property));
    dialog.open.mockReturnValue({ afterClosed: () => of({ ...property, name: 'Apto 1 Renovado' }) });
    propertiesService.list.mockClear();

    component['openEditDialog']('p1');

    expect(propertiesService.getById).toHaveBeenCalledWith('p1');
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.properties.list.updatedSuccess', undefined, { duration: 3000 });
    expect(propertiesService.list).toHaveBeenCalledTimes(1);
  });

  it('openEditDialog shows a classified error and never opens the dialog when fetching the property fails', () => {
    const { component, dialog, propertiesService, snackBar } = configure();
    propertiesService.getById.mockReturnValue(throwError(() => ({ status: 404 })));

    component['openEditDialog']('p1');

    expect(dialog.open).not.toHaveBeenCalled();
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.properties.list.errors.notFound', undefined, { duration: 4000 });
  });

  it('activate calls the service and reloads on success', () => {
    const { component, propertiesService, snackBar } = configure();
    propertiesService.activate.mockReturnValue(of(property));
    propertiesService.list.mockClear();

    component['activate']('p1');

    expect(propertiesService.activate).toHaveBeenCalledWith('p1');
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.properties.list.activatedSuccess', undefined, { duration: 3000 });
    expect(propertiesService.list).toHaveBeenCalledTimes(1);
  });

  it('activate shows a classified conflict error and does not reload on failure', () => {
    const { component, propertiesService, snackBar } = configure();
    propertiesService.activate.mockReturnValue(throwError(() => ({ status: 409 })));
    propertiesService.list.mockClear();

    component['activate']('p1');

    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.properties.list.errors.conflict', undefined, { duration: 4000 });
    expect(propertiesService.list).not.toHaveBeenCalled();
  });

  it('deactivate calls the service and reloads on success', () => {
    const { component, propertiesService, snackBar } = configure();
    propertiesService.deactivate.mockReturnValue(of(property));
    propertiesService.list.mockClear();

    component['deactivate']('p1');

    expect(propertiesService.deactivate).toHaveBeenCalledWith('p1');
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.properties.list.deactivatedSuccess', undefined, { duration: 3000 });
    expect(propertiesService.list).toHaveBeenCalledTimes(1);
  });

  it('confirmArchive opens a confirmation dialog and archives the property only when confirmed', () => {
    const { component, dialog, propertiesService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    propertiesService.archive.mockReturnValue(of(property));
    propertiesService.list.mockClear();

    component['confirmArchive'](property);

    expect(propertiesService.archive).toHaveBeenCalledWith('p1');
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.properties.list.archivedSuccess', undefined, { duration: 3000 });
    expect(propertiesService.list).toHaveBeenCalledTimes(1);
  });

  it('confirmArchive does not archive the property when the confirmation is declined', () => {
    const { component, dialog, propertiesService } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(false) });

    component['confirmArchive'](property);

    expect(propertiesService.archive).not.toHaveBeenCalled();
  });

  it('openOwnersDialog opens the ownership dialog for the property and reloads once it closes', () => {
    const { component, dialog, propertiesService } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    propertiesService.list.mockClear();

    component['openOwnersDialog'](property);

    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { propertyId: 'p1', propertyName: 'Apto 1' } }));
    expect(propertiesService.list).toHaveBeenCalledTimes(1);
  });
});
