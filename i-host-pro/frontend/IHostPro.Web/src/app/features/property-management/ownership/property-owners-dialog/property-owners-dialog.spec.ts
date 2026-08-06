import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { PagedPropertyOwnerResponse, PropertyOwnerResponse } from '../../../../core/api/generated/api-client';
import { PropertiesService } from '../../properties.service';
import { PropertyOwnersDialog, PropertyOwnersDialogData } from './property-owners-dialog';

const data: PropertyOwnersDialogData = { propertyId: 'p1', propertyName: 'Apto 1' };

function configure(listResult: PagedPropertyOwnerResponse = { items: [], totalCount: 0 }) {
  const propertiesService = {
    listOwners: vi.fn().mockReturnValue(of(listResult)),
    linkOwner: vi.fn(),
    unlinkOwner: vi.fn(),
  };
  const dialogRef = { close: vi.fn() };
  const nestedDialogRef = { afterClosed: () => of(undefined) };
  const dialog = { open: vi.fn().mockReturnValue(nestedDialogRef) };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };

  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: MatDialog, useValue: dialog },
      { provide: PropertiesService, useValue: propertiesService },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new PropertyOwnersDialog());
  return { component, dialogRef, dialog, propertiesService, snackBar };
}

describe('PropertyOwnersDialog', () => {
  const owner: PropertyOwnerResponse = { propertyId: 'p1', ownerUserId: 'u1', createdAt: new Date('2026-01-01T00:00:00Z') };

  it('loads owners on construction, ending in the loaded state when items exist', () => {
    const { component, propertiesService } = configure({ items: [owner], totalCount: 1 });

    expect(propertiesService.listOwners).toHaveBeenCalledWith('p1', 1, 100);
    expect(component['state']()).toBe('loaded');
    expect(component['owners']()).toEqual([owner]);
  });

  it('ends in the empty state when no owner is linked', () => {
    const { component } = configure({ items: [], totalCount: 0 });

    expect(component['state']()).toBe('empty');
  });

  it('ends in the error state when loading owners fails', () => {
    const propertiesService = { listOwners: vi.fn().mockReturnValue(throwError(() => new Error('boom'))), linkOwner: vi.fn(), unlinkOwner: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: data },
        { provide: MatDialogRef, useValue: { close: vi.fn() } },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: PropertiesService, useValue: propertiesService },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (k: string) => k } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new PropertyOwnersDialog());

    expect(component['state']()).toBe('error');
  });

  it('linkOwner does nothing and marks the field touched when the owner user id is blank', () => {
    const { component, propertiesService } = configure();

    component['linkOwner']();

    expect(propertiesService.linkOwner).not.toHaveBeenCalled();
    expect(component['ownerUserIdControl'].touched).toBe(true);
  });

  it('linkOwner calls PropertiesService.linkOwner, resets the field, shows a success snackbar and reloads', () => {
    const { component, propertiesService, snackBar } = configure();
    propertiesService.linkOwner.mockReturnValue(of(owner));
    propertiesService.listOwners.mockClear();
    component['ownerUserIdControl'].setValue('u1');

    component['linkOwner']();

    expect(propertiesService.linkOwner).toHaveBeenCalledWith('p1', { ownerUserId: 'u1' });
    expect(component['ownerUserIdControl'].value).toBe('');
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.ownership.linkedSuccess', undefined, { duration: 3000 });
    expect(propertiesService.listOwners).toHaveBeenCalledTimes(1);
  });

  it('linkOwner ignores a duplicate submit while a request is already in flight', () => {
    const { component, propertiesService } = configure();
    component['ownerUserIdControl'].setValue('u1');
    component['busy'].set(true);

    component['linkOwner']();

    expect(propertiesService.linkOwner).not.toHaveBeenCalled();
  });

  it('linkOwner shows the not-found message on a 404 (unknown or ineligible user)', () => {
    const { component, propertiesService, snackBar } = configure();
    propertiesService.linkOwner.mockReturnValue(throwError(() => ({ status: 404 })));
    component['ownerUserIdControl'].setValue('11111111-1111-1111-1111-111111111111');

    component['linkOwner']();

    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.ownership.errors.notFound', undefined, { duration: 4000 });
    expect(component['busy']()).toBe(false);
  });

  it('linkOwner shows the conflict message on a 409 (already linked)', () => {
    const { component, propertiesService, snackBar } = configure();
    propertiesService.linkOwner.mockReturnValue(throwError(() => ({ status: 409 })));
    component['ownerUserIdControl'].setValue('u1');

    component['linkOwner']();

    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.ownership.errors.conflict', undefined, { duration: 4000 });
  });

  it('linkOwner shows the generic message for any other error status (e.g. a 400 owner_user_id_required)', () => {
    const { component, propertiesService, snackBar } = configure();
    propertiesService.linkOwner.mockReturnValue(throwError(() => ({ status: 400, codes: ['owner_user_id_required'] })));
    component['ownerUserIdControl'].setValue('u1');

    component['linkOwner']();

    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.ownership.errors.generic', undefined, { duration: 4000 });
  });

  it('confirmRemove opens a confirmation dialog and removes the owner only when confirmed', () => {
    const { component, dialog, propertiesService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(true) });
    propertiesService.unlinkOwner.mockReturnValue(of(undefined));
    propertiesService.listOwners.mockClear();

    component['confirmRemove'](owner);

    expect(propertiesService.unlinkOwner).toHaveBeenCalledWith('p1', 'u1');
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.ownership.removedSuccess', undefined, { duration: 3000 });
    expect(propertiesService.listOwners).toHaveBeenCalledTimes(1);
  });

  it('confirmRemove does not remove the owner when the confirmation is declined', () => {
    const { component, dialog, propertiesService } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(false) });

    component['confirmRemove'](owner);

    expect(propertiesService.unlinkOwner).not.toHaveBeenCalled();
  });

  it('close() closes the dialog', () => {
    const { component, dialogRef } = configure();

    component['close']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
