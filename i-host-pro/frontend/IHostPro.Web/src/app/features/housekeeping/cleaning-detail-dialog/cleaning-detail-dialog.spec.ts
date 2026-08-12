import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { vi } from 'vitest';

import { CleaningDetailResponse } from '../../../core/api/generated/api-client';
import { CleaningDetailDialog, CleaningDetailDialogData } from './cleaning-detail-dialog';

function configure(data: CleaningDetailDialogData) {
  const dialogRef = { close: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new CleaningDetailDialog());
  return { component, dialogRef };
}

describe('CleaningDetailDialog', () => {
  it('exposes the injected cleaning detail data as-is', () => {
    const cleaning = { id: 'c1', propertyId: 'p1', status: 'Pending' } as CleaningDetailResponse;
    const { component } = configure({ cleaning });

    expect(component['data'].cleaning).toBe(cleaning);
  });

  it('close() closes the dialog with no result', () => {
    const { component, dialogRef } = configure({ cleaning: {} as CleaningDetailResponse });

    component['close']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
