import { TestBed } from '@angular/core/testing';
import { FormBuilder } from '@angular/forms';
import { TranslocoService } from '@jsverse/transloco';
import { Observable, Subject, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DashboardOverviewResponse } from '../../../core/api/generated/api-client';
import { DashboardService } from '../dashboard.service';
import { DashboardOverview } from './dashboard-overview';

function fullOverview(overrides: Partial<DashboardOverviewResponse> = {}): DashboardOverviewResponse {
  return {
    period: { from: new Date('2026-08-17T00:00:00Z'), to: new Date('2026-08-18T00:00:00Z') },
    reservations: {
      checkInsInPeriod: 3,
      checkOutsInPeriod: 2,
      futureReservations: 5,
      cancelledInPeriod: 1,
      statusCounts: [
        { status: 'confirmed', count: 4 },
        { status: 'cancelled', count: 1 },
      ],
    },
    housekeeping: {
      pending: 2,
      inProgress: 1,
      interrupted: 1,
      completedInPeriod: 6,
      cancelledInPeriod: 0,
      delayed: 1,
      waitingHelp: 0,
      waitingMaterials: 1,
    },
    properties: { active: 10, inactive: 2, archived: 1 },
    occurrences: { totalInPeriod: 3, byType: [{ type: 'Damage', count: 2 }, { type: 'Noise', count: 1 }] },
    generatedAtUtc: new Date('2026-08-17T12:00:00Z'),
    ...overrides,
  };
}

function configure(overview$fn: (from: Date, to: Date) => Observable<DashboardOverviewResponse>) {
  const dashboardService = { overview: vi.fn(overview$fn) };
  const transloco = { translate: (key: string) => key };

  TestBed.configureTestingModule({
    providers: [
      FormBuilder,
      { provide: DashboardService, useValue: dashboardService },
      { provide: TranslocoService, useValue: transloco },
    ],
  });

  const component = TestBed.runInInjectionContext(() => new DashboardOverview());
  return { component, dashboardService };
}

describe('DashboardOverview', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  describe('initial load', () => {
    it('fetches immediately on construction using the default "today" period', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));

      expect(dashboardService.overview).toHaveBeenCalledTimes(1);
      const [from, to] = dashboardService.overview.mock.calls[0] as [Date, Date];
      expect(to.getTime() - from.getTime()).toBe(24 * 60 * 60 * 1000);
      expect(component['phase']()).toBe('loaded');
    });

    it('sets phase to error and never populates overview when the first request fails', () => {
      const { component } = configure(() => throwError(() => new Error('boom')));

      expect(component['phase']()).toBe('error');
      expect(component['overview']()).toBeNull();
    });
  });

  describe('preset selection', () => {
    it('requests todayPeriod bounds when "today" is selected', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();

      component['selectPreset']('today');

      expect(dashboardService.overview).toHaveBeenCalledTimes(1);
      const [from, to] = dashboardService.overview.mock.calls[0] as [Date, Date];
      expect(to.getTime() - from.getTime()).toBe(24 * 60 * 60 * 1000);
    });

    it('requests a 7-day window when "last7" is selected', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();

      component['selectPreset']('last7');

      const [from, to] = dashboardService.overview.mock.calls[0] as [Date, Date];
      expect((to.getTime() - from.getTime()) / 86_400_000).toBe(7);
    });

    it('requests a 30-day window when "last30" is selected', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();

      component['selectPreset']('last30');

      const [from, to] = dashboardService.overview.mock.calls[0] as [Date, Date];
      expect((to.getTime() - from.getTime()) / 86_400_000).toBe(30);
    });

    it('selecting "custom" reveals the form without fetching', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();

      component['selectCustomPreset']();

      expect(component['preset']()).toBe('custom');
      expect(dashboardService.overview).not.toHaveBeenCalled();
    });
  });

  describe('custom range', () => {
    it('applies a valid range and fetches with the correct [from,to) bounds', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();
      component['customRangeForm'].setValue({ from: '2026-08-01', to: '2026-08-05' });

      component['applyCustomRange']();

      expect(component['customRangeError']()).toBeNull();
      const [from, to] = dashboardService.overview.mock.calls[0] as [Date, Date];
      expect(from).toEqual(new Date(2026, 7, 1, 0, 0, 0, 0));
      expect(to).toEqual(new Date(2026, 7, 6, 0, 0, 0, 0));
    });

    it('rejects an invalid range (to before from) without fetching', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();
      component['customRangeForm'].setValue({ from: '2026-08-10', to: '2026-08-01' });

      component['applyCustomRange']();

      expect(component['customRangeError']()).toBe('invalidRange');
      expect(dashboardService.overview).not.toHaveBeenCalled();
    });

    it('rejects a window larger than 100 days without fetching', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();
      component['customRangeForm'].setValue({ from: '2026-01-01', to: '2026-08-17' });

      component['applyCustomRange']();

      expect(component['customRangeError']()).toBe('windowTooLarge');
      expect(dashboardService.overview).not.toHaveBeenCalled();
    });

    it('rejects an empty/malformed date without fetching', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();
      component['customRangeForm'].setValue({ from: '', to: '2026-08-17' });

      component['applyCustomRange']();

      expect(component['customRangeError']()).toBe('invalidRange');
      expect(dashboardService.overview).not.toHaveBeenCalled();
    });
  });

  describe('polling (mandate §27-28)', () => {
    it('re-fetches every 60 seconds', () => {
      const { dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();

      vi.advanceTimersByTime(60_000);
      expect(dashboardService.overview).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(60_000);
      expect(dashboardService.overview).toHaveBeenCalledTimes(2);
    });

    it('does not fetch again before 60 seconds have elapsed', () => {
      const { dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();

      vi.advanceTimersByTime(59_000);

      expect(dashboardService.overview).not.toHaveBeenCalled();
    });
  });

  describe('manual refresh', () => {
    it('refresh() triggers an immediate fetch', () => {
      const { component, dashboardService } = configure(() => of(fullOverview()));
      dashboardService.overview.mockClear();

      component['refresh']();

      expect(dashboardService.overview).toHaveBeenCalledTimes(1);
    });
  });

  describe('background refresh does not discard existing data (mandate §29/§32)', () => {
    it('keeps refreshing=false and phase=loaded after a background refresh error, and flags refreshFailed', () => {
      let callCount = 0;
      const { component } = configure(() => {
        callCount++;
        return callCount === 1 ? of(fullOverview()) : throwError(() => new Error('network down'));
      });

      expect(component['phase']()).toBe('loaded');
      expect(component['overview']()).not.toBeNull();

      component['refresh']();

      expect(component['phase']()).toBe('loaded');
      expect(component['overview']()).not.toBeNull();
      expect(component['refreshFailed']()).toBe(true);
      expect(component['refreshing']()).toBe(false);
    });

    it('clears refreshFailed once a subsequent refresh succeeds', () => {
      let callCount = 0;
      const { component } = configure(() => {
        callCount++;
        return callCount === 2 ? throwError(() => new Error('down')) : of(fullOverview());
      });

      component['refresh'](); // call #2 -> fails
      expect(component['refreshFailed']()).toBe(true);

      component['refresh'](); // call #3 -> succeeds
      expect(component['refreshFailed']()).toBe(false);
    });

    it('sets refreshing=true only while a non-first request is in flight, never wiping the current overview', () => {
      const inFlight$ = new Subject<DashboardOverviewResponse>();
      const dashboardService = { overview: vi.fn().mockReturnValueOnce(of(fullOverview())).mockReturnValueOnce(inFlight$) };
      TestBed.configureTestingModule({
        providers: [
          FormBuilder,
          { provide: DashboardService, useValue: dashboardService },
          { provide: TranslocoService, useValue: { translate: (k: string) => k } },
        ],
      });
      const component = TestBed.runInInjectionContext(() => new DashboardOverview());

      component['refresh']();

      expect(component['refreshing']()).toBe(true);
      expect(component['overview']()).not.toBeNull();

      inFlight$.next(fullOverview({ generatedAtUtc: new Date('2026-08-17T13:00:00Z') }));
      inFlight$.complete();

      expect(component['refreshing']()).toBe(false);
    });
  });

  describe('switchMap cancels superseded requests (mandate §27)', () => {
    it('only the latest triggered request updates state when two overlap', () => {
      const first$ = new Subject<DashboardOverviewResponse>();
      const second$ = new Subject<DashboardOverviewResponse>();
      const dashboardService = { overview: vi.fn().mockReturnValueOnce(first$).mockReturnValueOnce(second$) };
      TestBed.configureTestingModule({
        providers: [
          FormBuilder,
          { provide: DashboardService, useValue: dashboardService },
          { provide: TranslocoService, useValue: { translate: (k: string) => k } },
        ],
      });
      const component = TestBed.runInInjectionContext(() => new DashboardOverview());

      component['refresh'](); // second real trigger, supersedes the initial in-flight construction request

      first$.next(fullOverview({ generatedAtUtc: new Date('2026-08-17T10:00:00Z') }));
      second$.next(fullOverview({ generatedAtUtc: new Date('2026-08-17T11:00:00Z') }));

      expect(component['overview']()?.generatedAtUtc).toEqual(new Date('2026-08-17T11:00:00Z'));
    });
  });

  describe('view-model cards (mandate §36 — no KPI recalculation)', () => {
    it('periodMetricsCards reflects the raw period-filtered fields exactly', () => {
      const { component } = configure(() => of(fullOverview()));

      const values = Object.fromEntries(component['periodMetricsCards']().map((c) => [c.labelKey, c.value]));

      expect(values['dashboard.reservations.checkInsInPeriod']).toBe(3);
      expect(values['dashboard.reservations.checkOutsInPeriod']).toBe(2);
      expect(values['dashboard.reservations.cancelledInPeriod']).toBe(1);
      expect(values['dashboard.housekeeping.completedInPeriod']).toBe(6);
      expect(values['dashboard.housekeeping.cancelledInPeriod']).toBe(0);
      expect(values['dashboard.occurrences.totalInPeriod']).toBe(3);
    });

    it('operationalCards reflects current-state fields, including FutureReservations (never period-filtered, mandate §15)', () => {
      const { component } = configure(() => of(fullOverview()));

      const values = Object.fromEntries(component['operationalCards']().map((c) => [c.labelKey, c.value]));

      expect(values['dashboard.reservations.futureReservations']).toBe(5);
      expect(values['dashboard.housekeeping.pending']).toBe(2);
      expect(values['dashboard.housekeeping.inProgress']).toBe(1);
      expect(values['dashboard.housekeeping.interrupted']).toBe(1);
      expect(values['dashboard.housekeeping.delayed']).toBe(1);
      expect(values['dashboard.housekeeping.waitingHelp']).toBe(0);
      expect(values['dashboard.housekeeping.waitingMaterials']).toBe(1);
    });

    it('propertiesCards reflects active/inactive/archived exactly, with no Draft field', () => {
      const { component } = configure(() => of(fullOverview()));

      const cards = component['propertiesCards']();

      expect(cards).toHaveLength(3);
      expect(cards.find((c) => c.labelKey === 'dashboard.properties.active')?.value).toBe(10);
      expect(cards.find((c) => c.labelKey === 'dashboard.properties.inactive')?.value).toBe(2);
      expect(cards.find((c) => c.labelKey === 'dashboard.properties.archived')?.value).toBe(1);
    });

    it('all card values fall back to 0 before any data has loaded', () => {
      const { component } = configure(() => throwError(() => new Error('boom')));

      const values = component['periodMetricsCards']();

      expect(values.every((c) => c.value === 0)).toBe(true);
    });
  });

  describe('statusCounts / occurrenceTypes', () => {
    it('exposes the raw statusCounts array from the response, empty when absent', () => {
      const { component } = configure(() => of(fullOverview()));
      expect(component['statusCounts']()).toEqual([
        { status: 'confirmed', count: 4 },
        { status: 'cancelled', count: 1 },
      ]);
    });

    it('exposes an empty statusCounts array before any data has loaded', () => {
      const { component } = configure(() => throwError(() => new Error('boom')));
      expect(component['statusCounts']()).toEqual([]);
    });

    it('exposes the raw occurrence byType array from the response', () => {
      const { component } = configure(() => of(fullOverview()));
      expect(component['occurrenceTypes']()).toEqual([
        { type: 'Damage', count: 2 },
        { type: 'Noise', count: 1 },
      ]);
    });
  });

  describe('reservationStatusLabel / occurrenceTypeLabel — safe fallback (mandate §16)', () => {
    it('returns the translated label for a known reservation status', () => {
      TestBed.configureTestingModule({
        providers: [
          FormBuilder,
          { provide: DashboardService, useValue: { overview: () => of(fullOverview()) } },
          { provide: TranslocoService, useValue: { translate: (key: string) => (key.endsWith('confirmed') ? 'Confirmada' : key) } },
        ],
      });
      const component = TestBed.runInInjectionContext(() => new DashboardOverview());

      expect(component['reservationStatusLabel']('confirmed')).toBe('Confirmada');
    });

    it('falls back to the raw status code for an unknown/future status rather than rendering a broken i18n key', () => {
      const { component } = configure(() => of(fullOverview()));

      expect(component['reservationStatusLabel']('some_future_status')).toBe('some_future_status');
    });

    it('falls back to the raw type for an unknown occurrence type', () => {
      const { component } = configure(() => of(fullOverview()));

      expect(component['occurrenceTypeLabel']('SomeNewType')).toBe('SomeNewType');
    });

    it('returns an em dash for an undefined status', () => {
      const { component } = configure(() => of(fullOverview()));

      expect(component['reservationStatusLabel'](undefined)).toBe('—');
    });
  });

  describe('period label (mandate §12 — no raw UTC exposed)', () => {
    it('is "single" for the default today period', () => {
      const { component } = configure(() => of(fullOverview()));
      expect(component['periodLabelKind']()).toBe('single');
    });

    it('is "range" once a multi-day period is selected', () => {
      const { component } = configure(() => of(fullOverview()));
      component['selectPreset']('last7');
      expect(component['periodLabelKind']()).toBe('range');
    });

    it('periodLastIncludedDay is one day before the exclusive `to` boundary', () => {
      const { component } = configure(() => of(fullOverview()));
      component['selectPreset']('last7');
      const { to } = component['period']();
      expect(component['periodLastIncludedDay']().getTime()).toBe(to.getTime() - 24 * 60 * 60 * 1000);
    });
  });
});
