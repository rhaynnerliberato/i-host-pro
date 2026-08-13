import type { EventInput } from '@fullcalendar/core';

import { ScheduleItemResponse } from '../../core/api/generated/api-client';

/** Extra, type-safe data FullCalendar carries alongside each event without contaminating its own EventInput shape — read back via event.extendedProps in templates/click handlers. */
export interface ScheduleEventExtendedProps {
  type: string;
  status: string;
  propertyId: string;
  housekeeperUserId: string | undefined;
  sourceReferenceId: string;
}

/**
 * Isolates FullCalendar's `EventInput` shape from `ScheduleItemResponse` —
 * the API contract must never depend on this UI library. Pure and
 * unit-testable without rendering FullCalendar itself.
 *
 * `startAtUtc`/`endAtUtc` are passed through as the native `Date` objects
 * the generated NSwag client already produces (`dateTimeType: "Date"`) —
 * FullCalendar's default `timeZone: 'local'` then renders them in the
 * browser's own timezone, the same implicit convention `DatePipe` already
 * uses everywhere else in this app (e.g. `createdAtUtc | date: 'short'` in
 * CleaningsList) — never re-parsed, never shifted.
 *
 * Backend contract note (`ScheduleReader`, Checkpoint 1): a Cleaning with
 * `ScheduledAtUtc == null` is already excluded server-side (it "does not
 * participate in schedule-based ordering"), so `startAtUtc` is expected to
 * always be present — this still checks defensively rather than trusting
 * that invariant blindly, since `ScheduleItemResponse.startAtUtc` is typed
 * optional on the wire. An item that fails the check is filtered out by the
 * caller (`mapScheduleItemsToEvents`), never rendered with an invented
 * date.
 */
export function mapScheduleItemToEvent(item: ScheduleItemResponse, translate: (key: string) => string): EventInput | null {
  if (!item.id || !item.startAtUtc || !item.type || !item.status || !item.propertyId || !item.sourceReferenceId) return null;

  const typeLabel = translate(`schedule.types.${item.type}`);
  const statusLabel = translate(`schedule.status.${item.status}`);

  const extendedProps: ScheduleEventExtendedProps = {
    type: item.type,
    status: item.status,
    propertyId: item.propertyId,
    housekeeperUserId: item.housekeeperUserId,
    sourceReferenceId: item.sourceReferenceId,
  };

  return {
    id: item.id,
    title: `${typeLabel} — ${statusLabel}`,
    start: item.startAtUtc,
    end: item.endAtUtc,
    classNames: [item.type === 'Reservation' ? 'schedule-event-reservation' : 'schedule-event-cleaning'],
    extendedProps,
  };
}

/** Filters out any item the mapper rejects (see its own doc comment) rather than ever rendering an invented value. */
export function mapScheduleItemsToEvents(items: readonly ScheduleItemResponse[], translate: (key: string) => string): EventInput[] {
  return items.map((item) => mapScheduleItemToEvent(item, translate)).filter((event): event is EventInput => event !== null);
}
