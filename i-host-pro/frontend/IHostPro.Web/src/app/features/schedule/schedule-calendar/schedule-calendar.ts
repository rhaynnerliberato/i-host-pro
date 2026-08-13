import { BreakpointObserver, Breakpoints } from '@angular/cdk/layout';
import { Component, effect, inject, signal, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { FullCalendarComponent, FullCalendarModule } from '@fullcalendar/angular';
import dayGridPlugin from '@fullcalendar/daygrid';
import timeGridPlugin from '@fullcalendar/timegrid';
import type { CalendarOptions, EventInput } from '@fullcalendar/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { map } from 'rxjs';

import { mapScheduleItemsToEvents } from '../schedule-event-mapper';
import { ScheduleService } from '../schedule.service';

type LoadState = 'loading' | 'loaded' | 'empty' | 'error';

/**
 * Read-only Agenda (Fase 7, Incremento 1 — Agenda Foundation, Checkpoint 2).
 * FullCalendar 6.1.21 (Standard, MIT — see the Fase 7 homologation document
 * for the version/license rationale) is the single wrapper around the
 * calendar library — the API contract (`ScheduleItemResponse`) never
 * depends on it, and no other component in this feature renders it
 * directly.
 *
 * The `events` option is FullCalendar's own function form: it is called
 * automatically with the currently visible date range (`fetchInfo.start`/
 * `.end`, already accounting for the leading/trailing days a month grid
 * shows) whenever the view or navigated date changes — this is what keeps
 * every request bounded to the visible range (never a full-history fetch)
 * and lets FullCalendar itself discard a stale in-flight fetch when a newer
 * one supersedes it, rather than this component hand-rolling cancellation.
 * Changing the event-type filter calls `getApi().refetchEvents()`
 * explicitly, since a filter change is not a "dates changed" event
 * FullCalendar would otherwise notice on its own.
 *
 * Property and housekeeper filters are deliberately NOT implemented this
 * checkpoint: `GET /api/v1/properties` and `GET /api/v1/users` are gated by
 * `PROPERTIES:MANAGE`/`USERS:MANAGE` respectively (PropertiesController/
 * UsersController's own `[Authorize(Policy = ...)]`), which OPERATOR does
 * not hold (`IdentityCatalogSeed`: OPERATOR only has `PROPERTIES:READ`, no
 * `USERS:MANAGE` at all) — OPERATOR does hold `SCHEDULE:MANAGE` and is a
 * real persona for this same Agenda page, so a filter that 403s for
 * OPERATOR cannot be built without breaking the page for that persona.
 * Registered as a gap in the Fase 7 homologation document, not solved here.
 *
 * Event click navigation is also deliberately NOT implemented: neither
 * Reservations nor Housekeeping expose an administrative DETAIL ROUTE
 * (`app.routes.ts` sends both `/reservations` and `/housekeeping` to a
 * list component that opens its detail as a dialog, never a routed page) —
 * per the approval's own instruction, no new route is created only for
 * this. Events remain informational.
 */
@Component({
  selector: 'app-schedule-calendar',
  imports: [
    ReactiveFormsModule,
    TranslocoPipe,
    FullCalendarModule,
    MatButtonModule,
    MatFormFieldModule,
    MatProgressSpinnerModule,
    MatSelectModule,
  ],
  templateUrl: './schedule-calendar.html',
  styleUrl: './schedule-calendar.scss',
})
export class ScheduleCalendar {
  private readonly scheduleService = inject(ScheduleService);
  private readonly transloco = inject(TranslocoService);
  private readonly formBuilder = inject(FormBuilder);
  private readonly breakpointObserver = inject(BreakpointObserver);

  // Plain (non-required) viewChild deliberately: the buttonLabels effect
  // below can run before the view — and therefore this child — exists.
  // applyEventTypeFilter/retry are only ever invoked from user-triggered UI
  // handlers, always well after the view has mounted.
  private readonly fullCalendar = viewChild(FullCalendarComponent);

  protected readonly state = signal<LoadState>('loading');

  protected readonly filterForm = this.formBuilder.nonNullable.group({ eventType: [''] });

  // TranslocoService.translate() only returns a real translation once its
  // JSON has finished loading (an async HTTP fetch, not yet complete when
  // this class's fields are first initialized) — reading it once here as a
  // plain value would freeze the raw i18n keys into buttonText forever.
  // selectTranslate(...) re-emits once the load completes (and again on any
  // later language change), and the effect below pushes each update into
  // FullCalendar's own live API — the only way to change an already-mounted
  // calendar's config, since `[options]` is read once by FullCalendarComponent.
  private readonly buttonLabels = toSignal(
    this.transloco.selectTranslate(['schedule.today', 'schedule.month', 'schedule.week', 'schedule.day']).pipe(
      map(([today, month, week, day]) => ({ today, month, week, day })),
    ),
    { initialValue: { today: '', month: '', week: '', day: '' } },
  );

  protected readonly calendarOptions: CalendarOptions = {
    plugins: [dayGridPlugin, timeGridPlugin],
    initialView: this.breakpointObserver.isMatched(Breakpoints.Handset) ? 'timeGridDay' : 'timeGridWeek',
    headerToolbar: { left: 'prev,next today', center: 'title', right: 'dayGridMonth,timeGridWeek,timeGridDay' },
    height: 'auto',
    events: (fetchInfo, successCallback, failureCallback) => this.fetchEvents(fetchInfo, successCallback, failureCallback),
    loading: (isLoading) => {
      if (isLoading) this.state.set('loading');
    },
  };

  constructor() {
    effect(() => {
      const labels = this.buttonLabels();
      this.fullCalendar()?.getApi()?.setOption('buttonText', labels);
    });
  }

  protected applyEventTypeFilter(): void {
    this.fullCalendar()?.getApi().refetchEvents();
  }

  private fetchEvents(
    fetchInfo: { start: Date; end: Date },
    successCallback: (events: EventInput[]) => void,
    failureCallback: (error: Error) => void,
  ): void {
    const eventType = this.filterForm.getRawValue().eventType || undefined;
    this.scheduleService.list(fetchInfo.start, fetchInfo.end, undefined, undefined, eventType).subscribe({
      next: (items) => {
        const events = mapScheduleItemsToEvents(items, (key) => this.transloco.translate(key));
        this.state.set(events.length === 0 ? 'empty' : 'loaded');
        successCallback(events);
      },
      error: (error: unknown) => {
        this.state.set('error');
        failureCallback(error instanceof Error ? error : new Error(String(error)));
      },
    });
  }

  protected retry(): void {
    this.fullCalendar()?.getApi().refetchEvents();
  }
}
