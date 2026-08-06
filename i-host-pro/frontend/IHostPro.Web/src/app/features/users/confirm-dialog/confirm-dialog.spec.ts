import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { vi } from 'vitest';

import { ConfirmDialog, ConfirmDialogData } from './confirm-dialog';

// Constructed directly via runInInjectionContext (never TestBed.createComponent/detectChanges): the
// template pulls in TranslocoPipe, which needs a full Transloco provider tree unrelated to what this
// suite verifies — confirm()/cancel() only need MAT_DIALOG_DATA and MatDialogRef.
function configure(data: ConfirmDialogData): { component: ConfirmDialog; dialogRef: { close: ReturnType<typeof vi.fn> } } {
  const dialogRef = { close: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new ConfirmDialog());
  return { component, dialogRef };
}

describe('ConfirmDialog', () => {
  it('confirm() closes the dialog with true', () => {
    const { component, dialogRef } = configure({ titleKey: 't', messageKey: 'm' });

    component['confirm']();

    expect(dialogRef.close).toHaveBeenCalledWith(true);
  });

  it('cancel() closes the dialog with false', () => {
    const { component, dialogRef } = configure({ titleKey: 't', messageKey: 'm' });

    component['cancel']();

    expect(dialogRef.close).toHaveBeenCalledWith(false);
  });

  it('exposes the injected dialog data unchanged for the template to render', () => {
    const data: ConfirmDialogData = { titleKey: 'users.list.blockConfirmTitle', messageKey: 'users.list.blockConfirmMessage', messageParams: { name: 'Ada' }, confirmKey: 'users.list.block' };
    const { component } = configure(data);

    expect(component['data']).toBe(data);
  });
});
