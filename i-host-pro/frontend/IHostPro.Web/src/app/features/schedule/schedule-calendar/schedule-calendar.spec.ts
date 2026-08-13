import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import type { CalendarOptions, EventInput } from '@fullcalendar/core';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { ScheduleItemResponse } from '../../../core/api/generated/api-client';
import { ScheduleService } from '../schedule.service';
import { ScheduleCalendar } from './schedule-calendar';

function cleaningItem(): ScheduleItemResponse {
  return {
    id: 'cleaning-1',
    type: 'Cleaning',
    propertyId: 'prop-1',
    startAtUtc: new Date('2026-08-16T14:00:00Z'),
    endAtUtc: undefined,
    status: 'Assigned',
    housekeeperUserId: 'housekeeper-1',
    sourceReferenceId: 'cleaning-1',
  };
}

function configure(listResult: ScheduleItemResponse[] = []) {
  const scheduleService = { list: vi.fn().mockReturnValue(of(listResult)) };
  const transloco = { translate: (key: string) => key, selectTranslate: (keys: string[]) => of(keys) };
  const breakpointObserver = { isMatched: () => false, observe: () => of({ matches: false }) };

  TestBed.configureTestingModule({
    providers: [
      { provide: ScheduleService, useValue: scheduleService },
      { provide: TranslocoService, useValue: transloco },
      { provide: BreakpointObserver, useValue: breakpointObserver },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new ScheduleCalendar());
  return { component, scheduleService };
}

function calendarOptions(component: ScheduleCalendar): CalendarOptions {
  return (component as unknown as { calendarOptions: CalendarOptions }).calendarOptions;
}

function invokeEventsSource(
  component: ScheduleCalendar,
  range: { start: Date; end: Date },
  successCallback: (events: EventInput[]) => void,
  failureCallback: (error: unknown) => void,
): void {
  (calendarOptions(component).events as (info: unknown, s: (e: EventInput[]) => void, f: (e: unknown) => void) => void)(
    range,
    successCallback,
    failureCallback,
  );
}

describe('ScheduleCalendar', () => {
  it('requests the schedule with the exact visible date range FullCalendar provides', () => {
    const { component, scheduleService } = configure();
    const from = new Date('2026-08-10T00:00:00Z');
    const to = new Date('2026-08-17T00:00:00Z');

    invokeEventsSource(component, { start: from, end: to }, vi.fn(), vi.fn());

    expect(scheduleService.list).toHaveBeenCalledWith(from, to, undefined, undefined, undefined);
  });

  it('forwards the selected event type filter to the schedule request', () => {
    const { component, scheduleService } = configure();
    component['filterForm'].controls.eventType.setValue('Cleaning');
    const from = new Date('2026-08-10T00:00:00Z');
    const to = new Date('2026-08-17T00:00:00Z');

    invokeEventsSource(component, { start: from, end: to }, vi.fn(), vi.fn());

    expect(scheduleService.list).toHaveBeenCalledWith(from, to, undefined, undefined, 'Cleaning');
  });

  it('sets state to loaded and passes mapped events to the success callback on a non-empty result', () => {
    const { component } = configure([cleaningItem()]);
    const successCallback = vi.fn();

    invokeEventsSource(component, { start: new Date(), end: new Date() }, successCallback, vi.fn());

    expect(component['state']()).toBe('loaded');
    expect(successCallback).toHaveBeenCalledWith([expect.objectContaining({ id: 'cleaning-1' })]);
  });

  it('sets state to empty when the API returns zero items', () => {
    const { component } = configure([]);
    const successCallback = vi.fn();

    invokeEventsSource(component, { start: new Date(), end: new Date() }, successCallback, vi.fn());

    expect(component['state']()).toBe('empty');
    expect(successCallback).toHaveBeenCalledWith([]);
  });

  it('sets state to error and calls the failure callback when the request fails', () => {
    const { component, scheduleService } = configure();
    scheduleService.list.mockReturnValue(throwError(() => new Error('boom')));
    const failureCallback = vi.fn();

    invokeEventsSource(component, { start: new Date(), end: new Date() }, vi.fn(), failureCallback);

    expect(component['state']()).toBe('error');
    expect(failureCallback).toHaveBeenCalled();
  });

  it('sets state to loading when FullCalendar reports a fetch in progress', () => {
    const { component } = configure();
    component['state'].set('loaded');

    (calendarOptions(component).loading as (isLoading: boolean) => void)(true);

    expect(component['state']()).toBe('loading');
  });

  it('does not change state when FullCalendar reports loading has finished', () => {
    const { component } = configure();
    component['state'].set('loaded');

    (calendarOptions(component).loading as (isLoading: boolean) => void)(false);

    expect(component['state']()).toBe('loaded');
  });

  it('configures day, week and month grid views with no interaction/premium plugins', () => {
    const { component } = configure();
    const options = calendarOptions(component);

    expect(options.headerToolbar).toEqual({ left: 'prev,next today', center: 'title', right: 'dayGridMonth,timeGridWeek,timeGridDay' });
    expect(options.plugins).toHaveLength(2);
  });

  it('defaults to the week view on desktop (BreakpointObserver.isMatched(Handset) === false)', () => {
    const { component } = configure();
    expect(calendarOptions(component).initialView).toBe('timeGridWeek');
  });

  it('resolves toolbar button labels via selectTranslate rather than a one-shot translate() snapshot', () => {
    // Regression test: buttonText used to be built once from
    // TranslocoService.translate() at field-initializer time, before the
    // translation JSON had loaded — freezing the raw i18n keys into the
    // toolbar forever. selectTranslate(...) must be used instead, so the
    // labels resolve once loading completes (real bug found and fixed via
    // browser verification — Fase 7 homologation document, Checkpoint 2).
    const scheduleService = { list: vi.fn().mockReturnValue(of([])) };
    const selectTranslate = vi.fn((keys: string[]) => of(keys.map((k) => `translated:${k}`)));
    const transloco = { translate: (key: string) => key, selectTranslate };
    const breakpointObserver = { isMatched: () => false, observe: () => of({ matches: false }) };

    TestBed.configureTestingModule({
      providers: [
        { provide: ScheduleService, useValue: scheduleService },
        { provide: TranslocoService, useValue: transloco },
        { provide: BreakpointObserver, useValue: breakpointObserver },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new ScheduleCalendar());

    expect(selectTranslate).toHaveBeenCalledWith(['schedule.today', 'schedule.month', 'schedule.week', 'schedule.day']);
    expect(component['buttonLabels']()).toEqual({
      today: 'translated:schedule.today',
      month: 'translated:schedule.month',
      week: 'translated:schedule.week',
      day: 'translated:schedule.day',
    });
  });
});

describe('ScheduleCalendar on a handset breakpoint', () => {
  it('defaults to the day view', () => {
    TestBed.configureTestingModule({
      providers: [
        { provide: ScheduleService, useValue: { list: vi.fn().mockReturnValue(of([])) } },
        { provide: TranslocoService, useValue: { translate: (key: string) => key, selectTranslate: (keys: string[]) => of(keys) } },
        { provide: BreakpointObserver, useValue: { isMatched: () => true, observe: () => of({ matches: true }) } },
      ],
    });
    const component = TestBed.runInInjectionContext(() => new ScheduleCalendar());

    expect(calendarOptions(component).initialView).toBe('timeGridDay');
  });
});
