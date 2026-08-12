import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CleaningDetailResponse } from '../../../core/api/generated/api-client';
import { HousekeepingService } from '../housekeeping.service';
import { AssignCleaningDialog, AssignCleaningDialogData } from './assign-cleaning-dialog';

function configure(data: AssignCleaningDialogData) {
  const dialogRef = { close: vi.fn() };
  const housekeepingService = { assign: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: HousekeepingService, useValue: housekeepingService },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new AssignCleaningDialog());
  return { component, dialogRef, housekeepingService };
}

describe('AssignCleaningDialog', () => {
  it('submit() blocks and touches the field when housekeeperUserId is blank', () => {
    const { component, housekeepingService } = configure({ cleaningId: 'c1' });

    component['submit']();

    expect(housekeepingService.assign).not.toHaveBeenCalled();
    expect(component['form'].controls.housekeeperUserId.touched).toBe(true);
  });

  it('submit() delegates to HousekeepingService.assign with the cleaning id and typed housekeeperUserId', () => {
    const assigned = { id: 'c1', status: 'Assigned' } as CleaningDetailResponse;
    const { component, dialogRef, housekeepingService } = configure({ cleaningId: 'c1' });
    housekeepingService.assign.mockReturnValue(of(assigned));
    component['form'].controls.housekeeperUserId.setValue('user-1');

    component['submit']();

    expect(housekeepingService.assign).toHaveBeenCalledWith('c1', 'user-1');
    expect(dialogRef.close).toHaveBeenCalledWith(assigned);
    expect(component['submitting']()).toBe(false);
  });

  it('submit() sets submitting=true synchronously so a duplicate click before the response is ignored', () => {
    const { component, housekeepingService } = configure({ cleaningId: 'c1' });
    housekeepingService.assign.mockReturnValue(of({} as CleaningDetailResponse));
    component['form'].controls.housekeeperUserId.setValue('user-1');
    component['submitting'].set(true);

    component['submit']();

    expect(housekeepingService.assign).not.toHaveBeenCalled();
  });

  it('submit() on a 403 sets the notEligible error key ("não é HOUSEKEEPER, inativo ou de outro tenant")', () => {
    const { component, housekeepingService } = configure({ cleaningId: 'c1' });
    housekeepingService.assign.mockReturnValue(throwError(() => ({ status: 403, code: 'housekeeper_not_eligible' })));
    component['form'].controls.housekeeperUserId.setValue('user-1');

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.assign.errors.notEligible');
    expect(component['submitting']()).toBe(false);
  });

  it('submit() on a 404 sets the notFound error key', () => {
    const { component, housekeepingService } = configure({ cleaningId: 'c1' });
    housekeepingService.assign.mockReturnValue(throwError(() => ({ status: 404, code: 'cleaning_not_found' })));
    component['form'].controls.housekeeperUserId.setValue('user-1');

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.assign.errors.notFound');
  });

  it('submit() on a 409 cleaning_concurrency_conflict sets the concurrency error key', () => {
    const { component, housekeepingService } = configure({ cleaningId: 'c1' });
    housekeepingService.assign.mockReturnValue(
      throwError(() => ({ status: 409, code: 'cleaning_concurrency_conflict' })),
    );
    component['form'].controls.housekeeperUserId.setValue('user-1');

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.assign.errors.concurrency');
  });

  it('submit() on a 409 invalid_cleaning_transition sets the generic conflict error key', () => {
    const { component, housekeepingService } = configure({ cleaningId: 'c1' });
    housekeepingService.assign.mockReturnValue(
      throwError(() => ({ status: 409, code: 'invalid_cleaning_transition' })),
    );
    component['form'].controls.housekeeperUserId.setValue('user-1');

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.assign.errors.conflict');
  });

  it('submit() on a 400 sets the validation error key', () => {
    const { component, housekeepingService } = configure({ cleaningId: 'c1' });
    housekeepingService.assign.mockReturnValue(
      throwError(() => ({ status: 400, codes: ['housekeeper_user_id_required'] })),
    );
    component['form'].controls.housekeeperUserId.setValue('user-1');

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.assign.errors.validation');
  });

  it('submit() on any other error sets the generic error key', () => {
    const { component, housekeepingService } = configure({ cleaningId: 'c1' });
    housekeepingService.assign.mockReturnValue(throwError(() => ({ status: 500 })));
    component['form'].controls.housekeeperUserId.setValue('user-1');

    component['submit']();

    expect(component['errorKey']()).toBe('housekeeping.assign.errors.generic');
  });

  it('cancel() closes the dialog with no result', () => {
    const { component, dialogRef } = configure({ cleaningId: 'c1' });

    component['cancel']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
