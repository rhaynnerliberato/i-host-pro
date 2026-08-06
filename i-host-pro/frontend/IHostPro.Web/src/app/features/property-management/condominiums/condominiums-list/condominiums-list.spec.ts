import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CondominiumDetailResponse, CondominiumSummaryResponse, PagedCondominiumResponse } from '../../../../core/api/generated/api-client';
import { CondominiumsService } from '../../condominiums.service';
import { CondominiumsList } from './condominiums-list';

function configure(listResult: PagedCondominiumResponse = { items: [], totalCount: 0 }) {
  const condominiumsService = {
    list: vi.fn().mockReturnValue(of(listResult)),
    getById: vi.fn(),
  };
  const nestedDialogRef = { afterClosed: () => of(undefined) };
  const dialog = { open: vi.fn().mockReturnValue(nestedDialogRef) };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };

  TestBed.configureTestingModule({
    providers: [
      { provide: CondominiumsService, useValue: condominiumsService },
      { provide: MatDialog, useValue: dialog },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new CondominiumsList());
  return { component, condominiumsService, dialog, snackBar };
}

describe('CondominiumsList', () => {
  const condominium: CondominiumSummaryResponse = { id: 'c1', name: 'Edificio Sol', createdAt: new Date('2026-01-01T00:00:00Z') };

  it('loads condominiums on construction, ending in the loaded state when items exist', () => {
    const { component, condominiumsService } = configure({ items: [condominium], totalCount: 1 });

    expect(condominiumsService.list).toHaveBeenCalledTimes(1);
    expect(component['state']()).toBe('loaded');
    expect(component['condominiums']()).toEqual([condominium]);
    expect(component['totalCount']()).toBe(1);
  });

  it('ends in the empty state when the page has zero items', () => {
    const { component } = configure({ items: [], totalCount: 0 });

    expect(component['state']()).toBe('empty');
  });

  it('ends in the error state when the list request fails', () => {
    const condominiumsService = { list: vi.fn().mockReturnValue(throwError(() => new Error('boom'))), getById: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: CondominiumsService, useValue: condominiumsService },
        { provide: MatDialog, useValue: { open: vi.fn() } },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
        { provide: TranslocoService, useValue: { translate: (k: string) => k } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new CondominiumsList());

    expect(component['state']()).toBe('error');
  });

  it('onPageChange updates the page index/size and reloads', () => {
    const { component, condominiumsService } = configure();
    condominiumsService.list.mockClear();

    component['onPageChange']({ pageIndex: 2, pageSize: 25, length: 100 });

    expect(component['pageIndex']()).toBe(2);
    expect(component['pageSize']()).toBe(25);
    expect(condominiumsService.list).toHaveBeenCalledWith(3, 25);
  });

  it('openCreateDialog shows a success snackbar and reloads only when a condominium was actually created', () => {
    const created = { id: 'c2' } as CondominiumDetailResponse;
    const { component, dialog, condominiumsService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(created) });
    condominiumsService.list.mockClear();

    component['openCreateDialog']();

    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: {} }));
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.condominiums.list.createdSuccess', undefined, { duration: 3000 });
    expect(condominiumsService.list).toHaveBeenCalledTimes(1);
  });

  it('openCreateDialog does not reload or notify when the dialog is dismissed without creating a condominium', () => {
    const { component, dialog, condominiumsService, snackBar } = configure();
    dialog.open.mockReturnValue({ afterClosed: () => of(undefined) });
    condominiumsService.list.mockClear();

    component['openCreateDialog']();

    expect(snackBar.open).not.toHaveBeenCalled();
    expect(condominiumsService.list).not.toHaveBeenCalled();
  });

  it('openEditDialog fetches the condominium, opens the dialog pre-filled, and reloads on update', () => {
    const { component, dialog, condominiumsService, snackBar } = configure();
    condominiumsService.getById.mockReturnValue(of(condominium));
    dialog.open.mockReturnValue({ afterClosed: () => of({ ...condominium, name: 'Edificio Lua' }) });
    condominiumsService.list.mockClear();

    component['openEditDialog']('c1');

    expect(condominiumsService.getById).toHaveBeenCalledWith('c1');
    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { condominium } }));
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.condominiums.list.updatedSuccess', undefined, { duration: 3000 });
    expect(condominiumsService.list).toHaveBeenCalledTimes(1);
  });

  it('openEditDialog shows a generic error and never opens the dialog when fetching the condominium fails', () => {
    const { component, dialog, condominiumsService, snackBar } = configure();
    condominiumsService.getById.mockReturnValue(throwError(() => new Error('boom')));

    component['openEditDialog']('c1');

    expect(dialog.open).not.toHaveBeenCalled();
    expect(snackBar.open).toHaveBeenCalledWith('propertyManagement.condominiums.list.errors.generic', undefined, { duration: 4000 });
  });
});
