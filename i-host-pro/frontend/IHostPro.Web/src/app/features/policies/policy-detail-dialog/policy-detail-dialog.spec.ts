import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { PolicyDefinitionResponse, PolicyValueDetailResponse } from '../../../core/api/generated/api-client';
import { UserProfileService } from '../../../core/auth/user-profile.service';
import { PoliciesService } from '../policies.service';
import { PolicyDetailDialog, PolicyDetailDialogData } from './policy-detail-dialog';

const earlyCheckIn: PolicyDefinitionResponse = {
  code: 'EARLY_CHECKIN',
  name: 'Early check-in',
  description: 'Allows early check-in',
  category: 'CheckIn',
  valueType: 'Object',
  schemaVersion: 1,
  isActive: true,
};

const lateCheckout: PolicyDefinitionResponse = {
  code: 'LATE_CHECKOUT',
  name: 'Late checkout',
  description: 'Allows late checkout',
  category: 'CheckOut',
  valueType: 'Object',
  schemaVersion: 1,
  isActive: true,
};

function configure(policy: PolicyDefinitionResponse, permissions: string[] = ['POLICIES:MANAGE']) {
  const dialogRef = { close: vi.fn() };
  const policiesService = {
    getEffective: vi.fn().mockReturnValue(of({ policyCode: policy.code, status: 'NotConfigured' })),
    getValueAtScope: vi.fn().mockReturnValue(throwError(() => ({ status: 404, title: 'policy_not_configured' }))),
    getHistory: vi.fn().mockReturnValue(of([])),
    createVersion: vi.fn(),
  };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key };
  const userProfile = { hasPermission: (code: string) => permissions.includes(code) };

  const data: PolicyDetailDialogData = { policy };
  TestBed.configureTestingModule({
    providers: [
      { provide: MAT_DIALOG_DATA, useValue: data },
      { provide: MatDialogRef, useValue: dialogRef },
      { provide: PoliciesService, useValue: policiesService },
      { provide: UserProfileService, useValue: userProfile },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new PolicyDetailDialog());
  return { component, dialogRef, policiesService, snackBar };
}

describe('PolicyDetailDialog', () => {
  it('canManage is true for a profile holding POLICIES:MANAGE', () => {
    expect(configure(earlyCheckIn, ['POLICIES:MANAGE']).component['canManage']()).toBe(true);
  });

  it('canManage is false for a profile holding only POLICIES:READ', () => {
    expect(configure(earlyCheckIn, ['POLICIES:READ']).component['canManage']()).toBe(false);
  });

  it('load with Tenant scope calls the three read endpoints with propertyId undefined', () => {
    const { component, policiesService } = configure(earlyCheckIn);

    component['load']();

    expect(policiesService.getEffective).toHaveBeenCalledWith('EARLY_CHECKIN', undefined);
    expect(policiesService.getValueAtScope).toHaveBeenCalledWith('EARLY_CHECKIN', 'Tenant', undefined);
    expect(policiesService.getHistory).toHaveBeenCalledWith('EARLY_CHECKIN', 'Tenant', undefined);
  });

  it('load with Property scope but a blank propertyId marks the control touched and calls nothing', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    component['scopeType'].set('Property');

    component['load']();

    expect(component['propertyIdControl'].touched).toBe(true);
    expect(policiesService.getEffective).not.toHaveBeenCalled();
  });

  it('load with Property scope and a propertyId calls the three read endpoints with it', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    component['scopeType'].set('Property');
    component['propertyIdControl'].setValue('prop-1');

    component['load']();

    expect(policiesService.getEffective).toHaveBeenCalledWith('EARLY_CHECKIN', 'prop-1');
    expect(policiesService.getValueAtScope).toHaveBeenCalledWith('EARLY_CHECKIN', 'Property', 'prop-1');
    expect(policiesService.getHistory).toHaveBeenCalledWith('EARLY_CHECKIN', 'Property', 'prop-1');
  });

  it('load sets currentValueState to notConfigured when the exact-scope value 404s with policy_not_configured', () => {
    const { component } = configure(earlyCheckIn);

    component['load']();

    expect(component['currentValueState']()).toBe('notConfigured');
    expect(component['currentValue']()).toBeNull();
  });

  it('load sets currentValueState to error for any other failure reading the exact-scope value', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    policiesService.getValueAtScope.mockReturnValue(throwError(() => ({ status: 500 })));

    component['load']();

    expect(component['currentValueState']()).toBe('error');
  });

  it('load sets currentValueState to configured and stores the value when one exists at the scope', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    const current: PolicyValueDetailResponse = {
      id: 'v1',
      policyCode: 'EARLY_CHECKIN',
      scopeType: 'Tenant',
      version: 3,
      value: { allowed: true, earliestTime: '13:30:00', requiresCleaningCompleted: true, requiresForm: false, notifyFrontDesk: true },
      createdAtUtc: new Date('2026-01-01T00:00:00Z'),
      createdByUserId: 'u1',
      reason: 'initial',
      isCurrent: true,
    };
    policiesService.getValueAtScope.mockReturnValue(of(current));

    component['load']();

    expect(component['currentValueState']()).toBe('configured');
    expect(component['currentValue']()).toEqual(current);
  });

  it('inheritanceKey is null before a load completes', () => {
    const { component } = configure(earlyCheckIn);

    expect(component['inheritanceKey']()).toBeNull();
  });

  it('inheritanceKey reports notConfigured when the effective status is NotConfigured', () => {
    const { component } = configure(earlyCheckIn);

    component['load']();

    expect(component['inheritanceKey']()).toBe('policies.detail.effective.notConfigured');
  });

  it('inheritanceKey reports definedHere when resolvedScope matches the selected scope', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    policiesService.getEffective.mockReturnValue(of({ policyCode: 'EARLY_CHECKIN', status: 'Resolved', resolvedScope: 'Tenant', version: 1 }));

    component['load']();

    expect(component['inheritanceKey']()).toBe('policies.detail.effective.definedHere');
  });

  it('inheritanceKey reports inheritedFromTenant when resolved at Tenant but the selected scope is Property', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    component['scopeType'].set('Property');
    component['propertyIdControl'].setValue('prop-1');
    policiesService.getEffective.mockReturnValue(of({ policyCode: 'EARLY_CHECKIN', status: 'Resolved', resolvedScope: 'Tenant', version: 1 }));

    component['load']();

    expect(component['inheritanceKey']()).toBe('policies.detail.effective.inheritedFromTenant');
  });

  it('inheritanceKey reports inheritedFromGlobal when resolved at Global', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    policiesService.getEffective.mockReturnValue(of({ policyCode: 'EARLY_CHECKIN', status: 'Resolved', resolvedScope: 'Global', version: 1 }));

    component['load']();

    expect(component['inheritanceKey']()).toBe('policies.detail.effective.inheritedFromGlobal');
  });

  it('submitNewVersion does nothing and marks the form touched when the reason is blank', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    component['load']();

    component['submitNewVersion']();

    expect(policiesService.createVersion).not.toHaveBeenCalled();
    expect(component['newVersionForm'].controls.reason.touched).toBe(true);
  });

  it('submitNewVersion for EARLY_CHECKIN builds the typed value with HH:mm:ss time formatting and sends no expectedVersion when nothing is configured', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    policiesService.createVersion.mockReturnValue(of({}));
    component['load']();
    component['newVersionForm'].patchValue({ reason: 'initial setup', allowed: true, earliestTime: '13:30', requiresCleaningCompleted: true });

    component['submitNewVersion']();

    expect(policiesService.createVersion).toHaveBeenCalledWith('EARLY_CHECKIN', {
      scopeType: 'Tenant',
      propertyId: undefined,
      value: {
        allowed: true,
        earliestTime: '13:30:00',
        requiresCleaningCompleted: true,
        requiresForm: false,
        notifyFrontDesk: false,
      },
      reason: 'initial setup',
      expectedVersion: undefined,
    });
  });

  it('submitNewVersion sends the loaded current version as expectedVersion when a value is already configured', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    const current: PolicyValueDetailResponse = {
      id: 'v1',
      policyCode: 'EARLY_CHECKIN',
      scopeType: 'Tenant',
      version: 5,
      value: { allowed: true, requiresCleaningCompleted: false, requiresForm: false, notifyFrontDesk: false },
      createdAtUtc: new Date('2026-01-01T00:00:00Z'),
      createdByUserId: 'u1',
      reason: 'initial',
      isCurrent: true,
    };
    policiesService.getValueAtScope.mockReturnValue(of(current));
    policiesService.createVersion.mockReturnValue(of({}));
    component['load']();
    component['newVersionForm'].patchValue({ reason: 'policy change' });

    component['submitNewVersion']();

    expect(policiesService.createVersion).toHaveBeenCalledWith(
      'EARLY_CHECKIN',
      expect.objectContaining({ expectedVersion: 5 }),
    );
  });

  it('submitNewVersion for LATE_CHECKOUT rejects percentage without a valid chargeValue client-side, mirroring the backend rule, without calling the service', () => {
    const { component, policiesService } = configure(lateCheckout);
    component['load']();
    component['newVersionForm'].patchValue({ reason: 'setup', chargeType: 'percentage', chargeValue: null });

    component['submitNewVersion']();

    expect(policiesService.createVersion).not.toHaveBeenCalled();
    expect(component['formErrorKey']()).toBe('policies.detail.form.errors.invalid_policy_value');
  });

  it('submitNewVersion for LATE_CHECKOUT sends chargeValue null when chargeType is none, even if one was typed', () => {
    const { component, policiesService } = configure(lateCheckout);
    policiesService.createVersion.mockReturnValue(of({}));
    component['load']();
    component['newVersionForm'].patchValue({ reason: 'setup', chargeType: 'none', chargeValue: 10 });

    component['submitNewVersion']();

    expect(policiesService.createVersion).toHaveBeenCalledWith(
      'LATE_CHECKOUT',
      expect.objectContaining({ value: expect.objectContaining({ chargeType: 'none', chargeValue: null }) }),
    );
  });

  it('submitNewVersion on success shows a translated snackbar and reloads', () => {
    const { component, policiesService, snackBar } = configure(earlyCheckIn);
    policiesService.createVersion.mockReturnValue(of({}));
    component['load']();
    policiesService.getEffective.mockClear();
    component['newVersionForm'].patchValue({ reason: 'setup' });

    component['submitNewVersion']();

    expect(snackBar.open).toHaveBeenCalledWith('policies.detail.form.createdSuccess', undefined, { duration: 3000 });
    expect(policiesService.getEffective).toHaveBeenCalledTimes(1);
    expect(component['submitting']()).toBe(false);
  });

  it('submitNewVersion on error sets formErrorKey from the ProblemDetails title', () => {
    const { component, policiesService } = configure(earlyCheckIn);
    policiesService.createVersion.mockReturnValue(throwError(() => ({ status: 409, title: 'version_conflict' })));
    component['load']();
    component['newVersionForm'].patchValue({ reason: 'setup' });

    component['submitNewVersion']();

    expect(component['formErrorKey']()).toBe('policies.detail.form.errors.version_conflict');
    expect(component['submitting']()).toBe(false);
  });

  it('close() closes the dialog with no value', () => {
    const { component, dialogRef } = configure(earlyCheckIn);

    component['close']();

    expect(dialogRef.close).toHaveBeenCalledWith();
  });
});
