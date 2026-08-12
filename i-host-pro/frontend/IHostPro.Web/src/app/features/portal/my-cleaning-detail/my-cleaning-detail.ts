import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import { CleaningChecklistItemResponse, CleaningDetailResponse, CleaningOccurrenceResponse } from '../../../core/api/generated/api-client';
import { classifyHousekeepingError } from '../../housekeeping/housekeeping-error';
import { PortalService } from '../portal.service';

type LoadState = 'loading' | 'loaded' | 'error';

/** Mirrors `OccurrenceType` (Domain, Documento 12 §8) — the exact stable codes `OccurrenceTypeCodeMapper` exposes on the wire. */
const OCCURRENCE_TYPE_CODES = ['Theft', 'Breakage', 'ForgottenObject', 'Damage', 'Animal', 'Smoking', 'Noise', 'MaterialShortage'] as const;

/** Mirrors `ChecklistItemType` (Domain, Documento 12 §8) — the exact stable codes `ChecklistItemTypeCodeMapper` exposes on the wire. */
const CHECKLIST_ITEM_CODES = ['Stove', 'Refrigerator', 'Tv', 'AirConditioning', 'Bathroom', 'Linens', 'Trash', 'Window'] as const;

/**
 * Every self-service lifecycle action, occurrence registration, and
 * checklist toggle for a single own Cleaning (Fase 6, Incremento 2A) —
 * everything Documento 14 §26's minimal main screen deliberately leaves off
 * lives here instead, reached only by tapping a card on the list screen.
 */
@Component({
  selector: 'app-my-cleaning-detail',
  imports: [
    DatePipe,
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatCardModule,
    MatCheckboxModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
  ],
  templateUrl: './my-cleaning-detail.html',
  styleUrl: './my-cleaning-detail.scss',
})
export class MyCleaningDetail {
  private readonly portalService = inject(PortalService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly occurrenceTypeCodes = OCCURRENCE_TYPE_CODES;
  protected readonly checklistItemCodes = CHECKLIST_ITEM_CODES;

  protected readonly state = signal<LoadState>('loading');
  protected readonly cleaning = signal<CleaningDetailResponse | null>(null);
  protected readonly occurrences = signal<CleaningOccurrenceResponse[]>([]);
  protected readonly checklist = signal<CleaningChecklistItemResponse[]>([]);
  protected readonly occurrenceFormOpen = signal(false);

  protected readonly occurrenceForm = this.formBuilder.nonNullable.group({
    type: ['', [Validators.required]],
    description: [''],
  });

  private readonly cleaningId = this.route.snapshot.paramMap.get('cleaningId')!;

  constructor() {
    this.load();
  }

  protected goBack(): void {
    this.router.navigate(['/my-cleanings']);
  }

  protected load(): void {
    this.state.set('loading');
    this.portalService.getMyCleaningById(this.cleaningId).subscribe({
      next: (cleaning) => {
        this.cleaning.set(cleaning);
        this.state.set('loaded');
        this.loadOccurrences();
        this.loadChecklist();
      },
      error: () => this.state.set('error'),
    });
  }

  private loadOccurrences(): void {
    this.portalService.listOccurrences(this.cleaningId).subscribe((occurrences) => this.occurrences.set(occurrences));
  }

  private loadChecklist(): void {
    this.portalService.getChecklist(this.cleaningId).subscribe((checklist) => this.checklist.set(checklist));
  }

  protected statusLabelKey(status: string | undefined): string {
    return `portal.list.status.${status ?? 'Assigned'}`;
  }

  // Every guard below mirrors Cleaning's own domain guard (Domain/Cleaning.cs)
  // exactly — presentation only, the backend remains the sole authority.
  protected canMarkInTransit(): boolean {
    return this.cleaning()?.status === 'Assigned';
  }

  protected canStart(): boolean {
    const status = this.cleaning()?.status;
    return status === 'Assigned' || status === 'InTransit';
  }

  protected canStartInspection(): boolean {
    return this.cleaning()?.status === 'Started';
  }

  protected canComplete(): boolean {
    return this.cleaning()?.status === 'InInspection';
  }

  protected canMarkWaitingMaterials(): boolean {
    return this.cleaning()?.status === 'Started';
  }

  protected canMarkWaitingHelp(): boolean {
    return this.cleaning()?.status === 'Started';
  }

  /** Delay/occurrence/checklist are all rejected server-side only once the cleaning is terminal — same guard here. */
  protected isTerminal(): boolean {
    const status = this.cleaning()?.status;
    return status === 'Completed' || status === 'Cancelled';
  }

  protected markInTransit(): void {
    this.runTransition(this.portalService.markInTransit(this.cleaningId), 'portal.detail.inTransitSuccess');
  }

  protected start(): void {
    this.runTransition(this.portalService.start(this.cleaningId), 'portal.detail.startedSuccess');
  }

  protected startInspection(): void {
    this.runTransition(this.portalService.startInspection(this.cleaningId), 'portal.detail.inspectionStartedSuccess');
  }

  protected complete(): void {
    this.runTransition(this.portalService.complete(this.cleaningId), 'portal.detail.completedSuccess');
  }

  protected markWaitingMaterials(): void {
    this.runTransition(this.portalService.markWaitingMaterials(this.cleaningId), 'portal.detail.waitingMaterialsSuccess');
  }

  protected markWaitingHelp(): void {
    this.runTransition(this.portalService.markWaitingHelp(this.cleaningId), 'portal.detail.waitingHelpSuccess');
  }

  protected reportDelay(): void {
    this.runTransition(this.portalService.reportDelay(this.cleaningId), 'portal.detail.delaySuccess');
  }

  protected toggleOccurrenceForm(): void {
    this.occurrenceFormOpen.update((open) => !open);
  }

  protected submitOccurrence(): void {
    if (this.occurrenceForm.invalid) {
      this.occurrenceForm.markAllAsTouched();
      return;
    }

    const { type, description } = this.occurrenceForm.getRawValue();
    this.portalService.registerOccurrence(this.cleaningId, type, description || undefined).subscribe({
      next: () => {
        this.snackBar.open(this.transloco.translate('portal.detail.occurrences.occurrenceRegistered'), undefined, { duration: 3000 });
        this.occurrenceForm.reset({ type: '', description: '' });
        this.occurrenceFormOpen.set(false);
        this.loadOccurrences();
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  protected isChecked(itemType: string): boolean {
    return this.checklist().find((item) => item.itemType === itemType)?.isChecked ?? false;
  }

  protected toggleChecklistItem(itemType: string, isChecked: boolean): void {
    this.portalService.setChecklistItem(this.cleaningId, itemType, isChecked).subscribe({
      next: () => this.loadChecklist(),
      error: (error: unknown) => this.showActionError(error),
    });
  }

  private runTransition(request$: Observable<CleaningDetailResponse>, successKey: string): void {
    request$.subscribe({
      next: () => {
        this.snackBar.open(this.transloco.translate(successKey), undefined, { duration: 3000 });
        this.load();
      },
      error: (error: unknown) => this.showActionError(error),
    });
  }

  private showActionError(error: unknown): void {
    const { status } = classifyHousekeepingError(error);
    const key =
      status === 409
        ? 'portal.detail.errors.conflict'
        : status === 400
          ? 'portal.detail.errors.validation'
          : status === 404
            ? 'portal.detail.errors.notFound'
            : 'portal.detail.errors.generic';
    this.snackBar.open(this.transloco.translate(key), undefined, { duration: 4000 });
  }
}
