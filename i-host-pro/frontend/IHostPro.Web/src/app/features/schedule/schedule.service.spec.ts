import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client } from '../../core/api/generated/api-client';
import { ScheduleService } from './schedule.service';

describe('ScheduleService', () => {
  let service: ScheduleService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = { schedule: vi.fn().mockReturnValue(of([])) };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(ScheduleService);
  });

  it('list delegates to Client.schedule with from, to, propertyId, housekeeperUserId and eventType', () => {
    const from = new Date('2026-08-01T00:00:00.000Z');
    const to = new Date('2026-08-08T00:00:00.000Z');
    service.list(from, to, 'prop-1', 'user-1', 'Cleaning').subscribe();
    expect(client['schedule']).toHaveBeenCalledWith(from, to, 'prop-1', 'user-1', 'Cleaning');
  });

  it('list forwards undefined for the optional filters when none are provided', () => {
    const from = new Date('2026-08-01T00:00:00.000Z');
    const to = new Date('2026-08-08T00:00:00.000Z');
    service.list(from, to).subscribe();
    expect(client['schedule']).toHaveBeenCalledWith(from, to, undefined, undefined, undefined);
  });
});
