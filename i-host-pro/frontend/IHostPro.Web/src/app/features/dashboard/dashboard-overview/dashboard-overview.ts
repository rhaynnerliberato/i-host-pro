import { DatePipe } from '@angular/common';
import { DestroyRef, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Subject, interval, merge } from 'rxjs';
import { catchError, of, startWith, switchMap } from 'rxjs';

import { DashboardOverviewResponse, DashboardOccurrenceTypeCountResponse, DashboardStatusCountResponse } from '../../../core/api/generated/api-client';
import { DashboardService } from '../dashboard.service';
import {
  DashboardPeriod,
  DashboardPeriodError,
  DashboardPeriodPreset,
  isDashboardPeriodError,
  last30DaysPeriod,
  last7DaysPeriod,
  parseLocalDateInputValue,
  customPeriod,
  todayPeriod,
} from '../dashboard-period';

const POLL_INTERVAL_MS = 60_000;

type LoadPhase = 'loading' | 'loaded' | 'error';

interface DashboardCard {
  readonly labelKey: string;
  readonly value: number;
}

/**
 * Operational Dashboard (Fase 7, Incremento 2 — Dashboard &amp; Reporting
 * Foundation, Checkpoint 3) — the single administrative page consuming
 * GET /api/v1/dashboard/overview (Checkpoint 2, already homologated). No
 * chart library: every indicator is a card, a compact table, or a chip —
 * Angular Material only (mandate §3).
 *
 * Fetch orchestration (mandate §26-28): the initial load, every `period`
 * change, the 60s poll tick, and a manual refresh click all funnel into ONE
 * `switchMap` pipeline via a plain `Subject` (`triggerFetch$`, `next()`-ed
 * explicitly by every mutator below — deliberately not RxJS interop's
 * `toObservable(period)`, whose signal-to-observable bridge runs through
 * Angular's effect scheduler and would make the very first fetch and every
 * period-driven refetch depend on a change-detection flush instead of firing
 * synchronously). `switchMap` itself cancels a still-in-flight request
 * superseded by a newer trigger, and `takeUntilDestroyed` stops the interval
 * when this component is destroyed (no orphaned polling subscription).
 *
 * State model (mandate §29/§32): `phase` only ever shows the page-level
 * spinner/error view for the FIRST load (no data yet). Every later trigger
 * (poll tick, period change, manual refresh) keeps the previously loaded
 * cards on screen and toggles `refreshing`/`refreshFailed` instead — a
 * background refresh failure is reported inline without discarding the data
 * still shown.
 */
@Component({
  selector: 'app-dashboard-overview',
  imports: [
    DatePipe,
    ReactiveFormsModule,
    TranslocoPipe,
    MatButtonModule,
    MatButtonToggleModule,
    MatCardModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './dashboard-overview.html',
  styleUrl: './dashboard-overview.scss',
})
export class DashboardOverview {
  private readonly dashboardService = inject(DashboardService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly transloco = inject(TranslocoService);

  private readonly triggerFetch$ = new Subject<void>();

  protected readonly preset = signal<DashboardPeriodPreset>('today');
  protected readonly period = signal<DashboardPeriod>(todayPeriod(new Date()));
  protected readonly customRangeError = signal<DashboardPeriodError | null>(null);
  protected readonly customRangeForm = this.formBuilder.nonNullable.group({ from: [''], to: [''] });

  protected readonly phase = signal<LoadPhase>('loading');
  protected readonly refreshing = signal(false);
  protected readonly refreshFailed = signal(false);
  protected readonly overview = signal<DashboardOverviewResponse | null>(null);

  protected readonly periodMetricsCards = computed<DashboardCard[]>(() => {
    const data = this.overview();
    return [
      { labelKey: 'dashboard.reservations.checkInsInPeriod', value: data?.reservations?.checkInsInPeriod ?? 0 },
      { labelKey: 'dashboard.reservations.checkOutsInPeriod', value: data?.reservations?.checkOutsInPeriod ?? 0 },
      { labelKey: 'dashboard.reservations.cancelledInPeriod', value: data?.reservations?.cancelledInPeriod ?? 0 },
      { labelKey: 'dashboard.housekeeping.completedInPeriod', value: data?.housekeeping?.completedInPeriod ?? 0 },
      { labelKey: 'dashboard.housekeeping.cancelledInPeriod', value: data?.housekeeping?.cancelledInPeriod ?? 0 },
      { labelKey: 'dashboard.occurrences.totalInPeriod', value: data?.occurrences?.totalInPeriod ?? 0 },
    ];
  });

  protected readonly operationalCards = computed<DashboardCard[]>(() => {
    const data = this.overview();
    return [
      { labelKey: 'dashboard.reservations.futureReservations', value: data?.reservations?.futureReservations ?? 0 },
      { labelKey: 'dashboard.housekeeping.pending', value: data?.housekeeping?.pending ?? 0 },
      { labelKey: 'dashboard.housekeeping.inProgress', value: data?.housekeeping?.inProgress ?? 0 },
      { labelKey: 'dashboard.housekeeping.interrupted', value: data?.housekeeping?.interrupted ?? 0 },
      { labelKey: 'dashboard.housekeeping.delayed', value: data?.housekeeping?.delayed ?? 0 },
      { labelKey: 'dashboard.housekeeping.waitingHelp', value: data?.housekeeping?.waitingHelp ?? 0 },
      { labelKey: 'dashboard.housekeeping.waitingMaterials', value: data?.housekeeping?.waitingMaterials ?? 0 },
    ];
  });

  protected readonly propertiesCards = computed<DashboardCard[]>(() => {
    const data = this.overview();
    return [
      { labelKey: 'dashboard.properties.active', value: data?.properties?.active ?? 0 },
      { labelKey: 'dashboard.properties.inactive', value: data?.properties?.inactive ?? 0 },
      { labelKey: 'dashboard.properties.archived', value: data?.properties?.archived ?? 0 },
    ];
  });

  protected readonly statusCounts = computed<DashboardStatusCountResponse[]>(() => this.overview()?.reservations?.statusCounts ?? []);
  protected readonly occurrenceTypes = computed<DashboardOccurrenceTypeCountResponse[]>(() => this.overview()?.occurrences?.byType ?? []);

  protected readonly generatedAtUtc = computed<Date | undefined>(() => this.overview()?.generatedAtUtc);

  /** A single date when the period is exactly one calendar day (e.g. "Hoje"); a from–to range otherwise (mandate §12). */
  protected readonly periodLabelKind = computed<'single' | 'range'>(() => {
    const { from, to } = this.period();
    return to.getTime() - from.getTime() <= 24 * 60 * 60 * 1000 ? 'single' : 'range';
  });

  /** The last INCLUDED day for display — `to` itself is the exclusive boundary, one day past it. */
  protected readonly periodLastIncludedDay = computed(() => new Date(this.period().to.getTime() - 24 * 60 * 60 * 1000));

  constructor() {
    merge(this.triggerFetch$, interval(POLL_INTERVAL_MS))
      .pipe(
        startWith(undefined),
        switchMap(() => {
          const isFirstLoad = this.overview() === null;
          if (isFirstLoad) this.phase.set('loading');
          else this.refreshing.set(true);

          const { from, to } = this.period();
          return this.dashboardService.overview(from, to).pipe(catchError(() => of(null)));
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((response) => {
        this.refreshing.set(false);

        if (response === null) {
          if (this.overview() === null) this.phase.set('error');
          else this.refreshFailed.set(true);
          return;
        }

        this.overview.set(response);
        this.phase.set('loaded');
        this.refreshFailed.set(false);
      });
  }

  protected selectPreset(preset: Exclude<DashboardPeriodPreset, 'custom'>): void {
    this.preset.set(preset);
    this.customRangeError.set(null);
    const now = new Date();
    this.period.set(preset === 'today' ? todayPeriod(now) : preset === 'last7' ? last7DaysPeriod(now) : last30DaysPeriod(now));
    this.triggerFetch$.next();
  }

  /**
   * Only reveals the custom-range form (mandate §10) — never fetches on its
   * own. The `period` signal (and therefore the fetch pipeline) is only
   * updated once the user submits a valid range via `applyCustomRange()`.
   */
  protected selectCustomPreset(): void {
    this.preset.set('custom');
    this.customRangeError.set(null);
  }

  protected applyCustomRange(): void {
    const { from, to } = this.customRangeForm.getRawValue();
    const fromDate = parseLocalDateInputValue(from);
    const toDate = parseLocalDateInputValue(to);

    if (!fromDate || !toDate) {
      this.customRangeError.set('invalidRange');
      return;
    }

    const result = customPeriod(fromDate, toDate);
    if (isDashboardPeriodError(result)) {
      this.customRangeError.set(result);
      return;
    }

    this.customRangeError.set(null);
    this.preset.set('custom');
    this.period.set(result);
    this.triggerFetch$.next();
  }

  protected refresh(): void {
    this.triggerFetch$.next();
  }

  protected retry(): void {
    this.triggerFetch$.next();
  }

  /**
   * Reservations.StatusCounts is an extensible list-of-{Status,Count} shape
   * (Checkpoint 2 mandate) — a future status code this catalog does not yet
   * know about must never render a broken/literal i18n key path. Transloco
   * returns the key itself when no translation exists, so comparing against
   * the key is a reliable "was this a real translation" check (mandate §16).
   */
  protected reservationStatusLabel(status: string | undefined): string {
    return this.translateOrFallback(`dashboard.reservations.statusCounts.status.${status}`, status);
  }

  protected occurrenceTypeLabel(type: string | undefined): string {
    return this.translateOrFallback(`dashboard.occurrences.byType.types.${type}`, type);
  }

  private translateOrFallback(key: string, fallback: string | undefined): string {
    if (!fallback) return '—';
    const translated = this.transloco.translate(key);
    return translated === key ? fallback : translated;
  }
}
