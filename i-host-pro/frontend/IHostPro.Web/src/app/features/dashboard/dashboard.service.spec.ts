import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client } from '../../core/api/generated/api-client';
import { DashboardService } from './dashboard.service';

describe('DashboardService', () => {
  let service: DashboardService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = { overview: vi.fn().mockReturnValue(of({})) };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(DashboardService);
  });

  it('overview delegates to Client.overview with from and to', () => {
    const from = new Date('2026-08-17T00:00:00.000Z');
    const to = new Date('2026-08-18T00:00:00.000Z');

    service.overview(from, to).subscribe();

    expect(client['overview']).toHaveBeenCalledWith(from, to);
  });
});
