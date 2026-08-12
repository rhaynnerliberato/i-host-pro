import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client } from '../../core/api/generated/api-client';
import { ReservationsService } from './reservations.service';

describe('ReservationsService', () => {
  let service: ReservationsService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = {
      reservationsGET: vi.fn().mockReturnValue(of({})),
      reservationsGET2: vi.fn().mockReturnValue(of({})),
      reservationsPOST: vi.fn().mockReturnValue(of({})),
      reservationsPATCH: vi.fn().mockReturnValue(of({})),
      cancelReservation: vi.fn().mockReturnValue(of({})),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(ReservationsService);
  });

  it('list delegates to Client.reservationsGET with propertyId, status, from, to, page and pageSize', () => {
    const from = new Date('2026-08-01T00:00:00Z');
    const to = new Date('2026-08-10T00:00:00Z');

    service.list(2, 25, 'prop-1', 'confirmed', from, to).subscribe();

    expect(client['reservationsGET']).toHaveBeenCalledWith('prop-1', 'confirmed', from, to, 2, 25);
  });

  it('list forwards undefined filters when none are provided (no manual contract)', () => {
    service.list(1, 10).subscribe();
    expect(client['reservationsGET']).toHaveBeenCalledWith(undefined, undefined, undefined, undefined, 1, 10);
  });

  it('getById delegates to Client.reservationsGET2 with the reservation id', () => {
    service.getById('res-1').subscribe();
    expect(client['reservationsGET2']).toHaveBeenCalledWith('res-1');
  });

  it('create delegates to Client.reservationsPOST with the request body, omitting guestPhone when not provided', () => {
    const request = {
      propertyId: 'prop-1',
      guestName: 'Guest',
      guestPhone: undefined,
      checkInAt: new Date('2026-08-01T14:00:00Z'),
      checkOutAt: new Date('2026-08-05T11:00:00Z'),
      guestCount: 2,
    };

    service.create(request).subscribe();

    expect(client['reservationsPOST']).toHaveBeenCalledWith(request);
  });

  it('update sends every field as a bare value, preserving explicit null for guestPhone (clears the phone)', () => {
    const checkInAt = new Date('2026-08-01T14:00:00Z');
    const checkOutAt = new Date('2026-08-05T11:00:00Z');

    service
      .update('res-1', { propertyId: 'prop-1', guestName: 'Guest', guestPhone: null, checkInAt, checkOutAt, guestCount: 2 })
      .subscribe();

    expect(client['reservationsPATCH']).toHaveBeenCalledWith('res-1', {
      propertyId: 'prop-1',
      guestName: 'Guest',
      guestPhone: null,
      checkInAt,
      checkOutAt,
      guestCount: 2,
    });
  });

  it('update forwards a real guestPhone without conversion', () => {
    const checkInAt = new Date('2026-08-01T14:00:00Z');
    const checkOutAt = new Date('2026-08-05T11:00:00Z');

    service
      .update('res-1', { propertyId: 'prop-1', guestName: 'Guest', guestPhone: '+55 11 90000-0000', checkInAt, checkOutAt, guestCount: 2 })
      .subscribe();

    expect(client['reservationsPATCH']).toHaveBeenCalledWith('res-1', {
      propertyId: 'prop-1',
      guestName: 'Guest',
      guestPhone: '+55 11 90000-0000',
      checkInAt,
      checkOutAt,
      guestCount: 2,
    });
  });

  it('cancel delegates to Client.cancelReservation with the reservation id', () => {
    service.cancel('res-1').subscribe();
    expect(client['cancelReservation']).toHaveBeenCalledWith('res-1');
  });
});
