import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Observable } from 'rxjs';

import { CleaningDetailResponse, CleaningSummaryResponse } from '../../../core/api/generated/api-client';
import { classifyHousekeepingError } from '../../housekeeping/housekeeping-error';
import { PortalService } from '../portal.service';

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

/**
 * The Portal's single primary screen (Fase 6, Incremento 2A — Documento 14
 * §26): "próximas faxinas; horário; imóvel; botão Iniciar; botão Concluir."
 * Already ordered by `ScheduledAtUtc` server-side (nulls last) — this
 * component never re-sorts. The one action button shown per card is always
 * the single NEXT real lifecycle step for that cleaning's current status
 * (Start/StartInspection/Complete) — never a shortcut that skips
 * `InInspection`, and never shown at all for a status with no forward
 * self-service transition (Completed/Cancelled/Interrupted/WaitingMaterials/
 * WaitingHelp — the last two have no documented "resume" transition, same
 * gap already registered for the administrative side in Incremento 1).
 * Every other action (in-transit, delay, waiting-materials/help, occurrence,
 * checklist) lives on the detail screen, reached by tapping the card.
 */
@Component({
  selector: 'app-my-cleanings-list',
  imports: [DatePipe, TranslocoPipe, MatButtonModule, MatCardModule, MatChipsModule, MatProgressSpinnerModule],
  templateUrl: './my-cleanings-list.html',
  styleUrl: './my-cleanings-list.scss',
})
export class MyCleaningsList {
  private readonly portalService = inject(PortalService);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  private readonly transloco = inject(TranslocoService);

  protected readonly state = signal<LoadState>('loading');
  protected readonly cleanings = signal<CleaningSummaryResponse[]>([]);

  constructor() {
    this.load();
  }

  protected load(): void {
    this.state.set('loading');
    this.portalService.listMyCleanings(undefined, 1, 50).subscribe({
      next: (page) => {
        const items = page.items ?? [];
        this.cleanings.set(items);
        this.state.set(items.length === 0 ? 'empty' : 'loaded');
      },
      error: () => this.state.set('error'),
    });
  }

  protected statusLabelKey(status: string | undefined): string {
    return `portal.list.status.${status ?? 'Assigned'}`;
  }

  protected openDetail(cleaning: CleaningSummaryResponse): void {
    this.router.navigate(['/my-cleanings', cleaning.id]);
  }

  /** The single stable-key label for this cleaning's next self-service step — `null` when no forward transition exists for its status. */
  protected primaryActionKey(status: string | undefined): string | null {
    switch (status) {
      case 'Assigned':
      case 'InTransit':
        return 'portal.list.start';
      case 'Started':
        return 'portal.list.startInspection';
      case 'InInspection':
        return 'portal.list.complete';
      default:
        return null;
    }
  }

  protected runPrimaryAction(cleaning: CleaningSummaryResponse, event: Event): void {
    event.stopPropagation();
    const cleaningId = cleaning.id!;

    switch (cleaning.status) {
      case 'Assigned':
      case 'InTransit':
        this.runTransition(this.portalService.start(cleaningId), 'portal.list.startedSuccess');
        break;
      case 'Started':
        this.runTransition(this.portalService.startInspection(cleaningId), 'portal.list.inspectionStartedSuccess');
        break;
      case 'InInspection':
        this.runTransition(this.portalService.complete(cleaningId), 'portal.list.completedSuccess');
        break;
    }
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
        ? 'portal.list.errors.conflict'
        : status === 404
          ? 'portal.list.errors.notFound'
          : 'portal.list.errors.generic';
    this.snackBar.open(this.transloco.translate(key), undefined, { duration: 4000 });
  }
}
