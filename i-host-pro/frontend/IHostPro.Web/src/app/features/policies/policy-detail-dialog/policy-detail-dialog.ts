import { DatePipe, JsonPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import { UserProfileService } from '../../../core/auth/user-profile.service';
import { EffectivePolicyResponse, PolicyDefinitionResponse, PolicyValueDetailResponse } from '../../../core/api/generated/api-client';
import { PoliciesService } from '../policies.service';
import { classifyPolicyActionError } from '../policy-error';

export interface PolicyDetailDialogData {
  policy: PolicyDefinitionResponse;
}

type ScopeType = 'Tenant' | 'Property';
type CurrentValueState = 'configured' | 'notConfigured' | 'error';
type ChargeType = 'none' | 'fixedAmount' | 'percentage';

const KNOWN_ERROR_TITLES = [
  'policy_not_found',
  'invalid_policy_value',
  'scope_not_supported',
  'policy_not_configured',
  'version_conflict',
  'forbidden',
  'validation_error',
];

/**
 * View the effective value, the exact value/history at a chosen scope
 * (Tenant or Property — GLOBAL is never directly readable here:
 * `GetPolicyValueByScopeQuery`/`GetPolicyHistoryQuery` both reject it with
 * `forbidden`, only reachable indirectly via the effective endpoint's
 * `resolvedScope`), and create a new version at that scope. Read-only value
 * display (effective + current) is a raw JSON dump — only the "new version"
 * form, which is the one place a value is actually authored, gets a typed
 * per-policy-code shape (§3: `EARLY_CHECKIN`/`LATE_CHECKOUT`).
 */
@Component({
  selector: 'app-policy-detail-dialog',
  imports: [
    DatePipe,
    JsonPipe,
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatCheckboxModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MatTableModule,
  ],
  templateUrl: './policy-detail-dialog.html',
  styleUrl: './policy-detail-dialog.scss',
})
export class PolicyDetailDialog {
  protected readonly data = inject<PolicyDetailDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<PolicyDetailDialog>);
  private readonly formBuilder = inject(FormBuilder);
  private readonly policiesService = inject(PoliciesService);
  private readonly userProfile = inject(UserProfileService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);

  protected readonly isEarlyCheckIn = this.data.policy.code === 'EARLY_CHECKIN';
  protected readonly isLateCheckout = this.data.policy.code === 'LATE_CHECKOUT';
  protected readonly canManage = computed(() => this.userProfile.hasPermission('POLICIES:MANAGE'));

  protected readonly historyColumns = ['version', 'createdAtUtc', 'reason', 'isCurrent'];

  protected readonly scopeType = signal<ScopeType>('Tenant');
  protected readonly propertyIdControl = this.formBuilder.nonNullable.control('');

  protected readonly loading = signal(false);
  protected readonly loadedScope = signal<{ scopeType: ScopeType; propertyId?: string } | null>(null);

  protected readonly effective = signal<EffectivePolicyResponse | null>(null);
  protected readonly effectiveError = signal(false);

  protected readonly currentValue = signal<PolicyValueDetailResponse | null>(null);
  protected readonly currentValueState = signal<CurrentValueState>('notConfigured');

  protected readonly history = signal<PolicyValueDetailResponse[]>([]);
  protected readonly historyError = signal(false);

  protected readonly submitting = signal(false);
  protected readonly formErrorKey = signal<string | null>(null);

  protected readonly inheritanceKey = computed(() => {
    const effective = this.effective();
    const scope = this.loadedScope();
    if (!effective || !scope) return null;
    if (effective.status !== 'Resolved') return 'policies.detail.effective.notConfigured';
    if (effective.resolvedScope === scope.scopeType) return 'policies.detail.effective.definedHere';
    if (effective.resolvedScope === 'Tenant') return 'policies.detail.effective.inheritedFromTenant';
    return 'policies.detail.effective.inheritedFromGlobal';
  });

  protected readonly newVersionForm = this.formBuilder.nonNullable.group({
    reason: ['', [Validators.required]],
    allowed: [false],
    earliestTime: [''],
    requiresCleaningCompleted: [false],
    requiresForm: [false],
    notifyFrontDesk: [false],
    latestTime: [''],
    chargeType: ['none' as ChargeType],
    chargeValue: [null as number | null],
    requiresPix: [false],
    blocksCalendar: [false],
    updatesCleaning: [false],
  });

  protected load(): void {
    const scopeType = this.scopeType();
    if (scopeType === 'Property' && !this.propertyIdControl.value.trim()) {
      this.propertyIdControl.markAsTouched();
      return;
    }

    const propertyId = scopeType === 'Property' ? this.propertyIdControl.value.trim() : undefined;
    const policyCode = this.data.policy.code!;

    this.loading.set(true);

    forkJoin({
      effective: this.policiesService.getEffective(policyCode, propertyId).pipe(catchError(() => of(null))),
      currentValue: this.policiesService.getValueAtScope(policyCode, scopeType, propertyId).pipe(
        map((value) => ({ state: 'configured' as CurrentValueState, value })),
        catchError((error: unknown) => {
          const { title } = classifyPolicyActionError(error);
          const state: CurrentValueState = title === 'policy_not_configured' ? 'notConfigured' : 'error';
          return of({ state, value: null as PolicyValueDetailResponse | null });
        }),
      ),
      history: this.policiesService.getHistory(policyCode, scopeType, propertyId).pipe(catchError(() => of(null))),
    }).subscribe((result) => {
      this.loading.set(false);
      this.loadedScope.set({ scopeType, propertyId });

      this.effective.set(result.effective);
      this.effectiveError.set(result.effective === null);

      this.currentValue.set(result.currentValue.value);
      this.currentValueState.set(result.currentValue.state);

      this.history.set(result.history ?? []);
      this.historyError.set(result.history === null);

      this.populateFormFromCurrentValue();
    });
  }

  protected submitNewVersion(): void {
    if (this.submitting() || this.newVersionForm.invalid) {
      this.newVersionForm.markAllAsTouched();
      return;
    }

    const scope = this.loadedScope();
    if (!scope) return;

    const raw = this.newVersionForm.getRawValue();

    if (this.isLateCheckout && !this.isLateCheckoutChargeValid(raw.chargeType, raw.chargeValue)) {
      this.formErrorKey.set('policies.detail.form.errors.invalid_policy_value');
      return;
    }

    this.submitting.set(true);
    this.formErrorKey.set(null);

    this.policiesService
      .createVersion(this.data.policy.code!, {
        scopeType: scope.scopeType,
        propertyId: scope.propertyId,
        value: this.buildRawValue(raw),
        reason: raw.reason,
        expectedVersion: this.currentValueState() === 'configured' ? this.currentValue()?.version : undefined,
      })
      .subscribe({
        next: () => {
          this.submitting.set(false);
          this.snackBar.open(this.transloco.translate('policies.detail.form.createdSuccess'), undefined, { duration: 3000 });
          this.load();
        },
        error: (error: unknown) => {
          this.submitting.set(false);
          const { title } = classifyPolicyActionError(error);
          const key = title && KNOWN_ERROR_TITLES.includes(title) ? title : 'generic';
          this.formErrorKey.set(`policies.detail.form.errors.${key}`);
        },
      });
  }

  protected close(): void {
    this.dialogRef.close();
  }

  private isLateCheckoutChargeValid(chargeType: ChargeType, chargeValue: number | null): boolean {
    if (chargeType === 'percentage') return chargeValue !== null && chargeValue >= 0 && chargeValue <= 100;
    if (chargeType === 'fixedAmount') return chargeValue !== null && chargeValue >= 0;
    return true;
  }

  private buildRawValue(raw: ReturnType<PolicyDetailDialog['newVersionForm']['getRawValue']>): unknown {
    if (this.isEarlyCheckIn) {
      return {
        allowed: raw.allowed,
        earliestTime: toTimeOnlyValue(raw.earliestTime),
        requiresCleaningCompleted: raw.requiresCleaningCompleted,
        requiresForm: raw.requiresForm,
        notifyFrontDesk: raw.notifyFrontDesk,
      };
    }

    return {
      allowed: raw.allowed,
      latestTime: toTimeOnlyValue(raw.latestTime),
      chargeType: raw.chargeType,
      chargeValue: raw.chargeType === 'none' ? null : raw.chargeValue,
      requiresPix: raw.requiresPix,
      blocksCalendar: raw.blocksCalendar,
      updatesCleaning: raw.updatesCleaning,
    };
  }

  private populateFormFromCurrentValue(): void {
    const value = (this.currentValue()?.value ?? {}) as Record<string, unknown>;

    this.newVersionForm.reset({
      reason: '',
      allowed: (value['allowed'] as boolean) ?? false,
      earliestTime: this.isEarlyCheckIn ? toTimeInputValue(value['earliestTime'] as string | null | undefined) : '',
      requiresCleaningCompleted: (value['requiresCleaningCompleted'] as boolean) ?? false,
      requiresForm: (value['requiresForm'] as boolean) ?? false,
      notifyFrontDesk: (value['notifyFrontDesk'] as boolean) ?? false,
      latestTime: this.isLateCheckout ? toTimeInputValue(value['latestTime'] as string | null | undefined) : '',
      chargeType: ((value['chargeType'] as ChargeType) ?? 'none'),
      chargeValue: (value['chargeValue'] as number | null) ?? null,
      requiresPix: (value['requiresPix'] as boolean) ?? false,
      blocksCalendar: (value['blocksCalendar'] as boolean) ?? false,
      updatesCleaning: (value['updatesCleaning'] as boolean) ?? false,
    });
    this.formErrorKey.set(null);
  }
}

/** "HH:mm:ss" (the shape `TimeOnly` serializes to) -> "HH:mm" (the shape `<input type="time">` expects). */
function toTimeInputValue(value: string | null | undefined): string {
  return value ? value.slice(0, 5) : '';
}

/** "HH:mm" (from `<input type="time">`) -> "HH:mm:ss" (the shape `TimeOnly` deserializes from), or null when left blank. */
function toTimeOnlyValue(value: string): string | null {
  return value ? `${value}:00` : null;
}
