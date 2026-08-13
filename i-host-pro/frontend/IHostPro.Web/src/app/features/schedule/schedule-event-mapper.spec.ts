import { ScheduleItemResponse } from '../../core/api/generated/api-client';
import { mapScheduleItemToEvent, mapScheduleItemsToEvents, ScheduleEventExtendedProps } from './schedule-event-mapper';

const translate = (key: string): string => key;

function reservationItem(overrides: Partial<ScheduleItemResponse> = {}): ScheduleItemResponse {
  return {
    id: 'res-1',
    type: 'Reservation',
    propertyId: 'prop-1',
    startAtUtc: new Date('2026-08-15T09:30:00-03:00'),
    endAtUtc: new Date('2026-08-18T12:00:00-03:00'),
    status: 'confirmed',
    housekeeperUserId: undefined,
    sourceReferenceId: 'res-1',
    ...overrides,
  };
}

function cleaningItem(overrides: Partial<ScheduleItemResponse> = {}): ScheduleItemResponse {
  return {
    id: 'cleaning-1',
    type: 'Cleaning',
    propertyId: 'prop-1',
    startAtUtc: new Date('2026-08-16T14:00:00Z'),
    endAtUtc: undefined,
    status: 'Assigned',
    housekeeperUserId: 'housekeeper-1',
    sourceReferenceId: 'cleaning-1',
    ...overrides,
  };
}

describe('mapScheduleItemToEvent', () => {
  it('maps a Reservation with start/end, type label and id', () => {
    const item = reservationItem();
    const event = mapScheduleItemToEvent(item, translate);

    expect(event).not.toBeNull();
    expect(event!.id).toBe('res-1');
    expect(event!.title).toBe('schedule.types.Reservation — schedule.status.confirmed');
    expect(event!.start).toBe(item.startAtUtc);
    expect(event!.end).toBe(item.endAtUtc);
    expect(event!.classNames).toEqual(['schedule-event-reservation']);
  });

  it('maps a Cleaning with start ScheduledAtUtc and no end', () => {
    const item = cleaningItem();
    const event = mapScheduleItemToEvent(item, translate);

    expect(event).not.toBeNull();
    expect(event!.start).toBe(item.startAtUtc);
    expect(event!.end).toBeUndefined();
    expect(event!.classNames).toEqual(['schedule-event-cleaning']);
  });

  it('carries status and housekeeperUserId in extendedProps for a Cleaning', () => {
    const item = cleaningItem();
    const event = mapScheduleItemToEvent(item, translate);

    const extendedProps = event!.extendedProps as ScheduleEventExtendedProps;
    expect(extendedProps.status).toBe('Assigned');
    expect(extendedProps.housekeeperUserId).toBe('housekeeper-1');
    expect(extendedProps.type).toBe('Cleaning');
    expect(extendedProps.sourceReferenceId).toBe('cleaning-1');
  });

  it('preserves the exact instant of a real DateTimeOffset (non-UTC offset) without any shift', () => {
    // 2026-08-15T09:30:00-03:00 is 2026-08-15T12:30:00Z — the mapper must
    // never re-parse/re-derive this value (e.g. via toDateString()), only
    // pass through the Date instance the NSwag client already produced.
    const item = reservationItem({ startAtUtc: new Date('2026-08-15T09:30:00-03:00') });
    const event = mapScheduleItemToEvent(item, translate);

    expect((event!.start as Date).toISOString()).toBe('2026-08-15T12:30:00.000Z');
    expect((event!.start as Date).getTime()).toBe(item.startAtUtc!.getTime());
  });

  it('returns null for an item with no startAtUtc (defensive — the backend already excludes these, per ScheduleReader)', () => {
    const item = cleaningItem({ startAtUtc: undefined });
    expect(mapScheduleItemToEvent(item, translate)).toBeNull();
  });

  it('returns null for an item missing id, type, status, propertyId or sourceReferenceId', () => {
    expect(mapScheduleItemToEvent(cleaningItem({ id: undefined }), translate)).toBeNull();
    expect(mapScheduleItemToEvent(cleaningItem({ type: undefined }), translate)).toBeNull();
    expect(mapScheduleItemToEvent(cleaningItem({ status: undefined }), translate)).toBeNull();
    expect(mapScheduleItemToEvent(cleaningItem({ propertyId: undefined }), translate)).toBeNull();
    expect(mapScheduleItemToEvent(cleaningItem({ sourceReferenceId: undefined }), translate)).toBeNull();
  });
});

describe('mapScheduleItemsToEvents', () => {
  it('filters out rejected items rather than rendering an invented date', () => {
    const items = [reservationItem(), cleaningItem({ startAtUtc: undefined }), cleaningItem({ id: 'cleaning-2' })];
    const events = mapScheduleItemsToEvents(items, translate);

    expect(events).toHaveLength(2);
    expect(events.map((e) => e.id)).toEqual(['res-1', 'cleaning-2']);
  });

  it('maps an empty list to an empty array', () => {
    expect(mapScheduleItemsToEvents([], translate)).toEqual([]);
  });
});
