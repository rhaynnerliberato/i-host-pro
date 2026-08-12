import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client } from '../../core/api/generated/api-client';
import { HousekeepingService } from './housekeeping.service';

describe('HousekeepingService', () => {
  let service: HousekeepingService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = {
      cleaningsGET: vi.fn().mockReturnValue(of({})),
      cleaningsGET2: vi.fn().mockReturnValue(of({})),
      cleaningsPOST: vi.fn().mockReturnValue(of({})),
      assign: vi.fn().mockReturnValue(of({})),
      start: vi.fn().mockReturnValue(of({})),
      startInspection: vi.fn().mockReturnValue(of({})),
      complete: vi.fn().mockReturnValue(of({})),
      cancelCleaning: vi.fn().mockReturnValue(of({})),
      interrupt: vi.fn().mockReturnValue(of({})),
      waitingMaterials: vi.fn().mockReturnValue(of({})),
      waitingHelp: vi.fn().mockReturnValue(of({})),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(HousekeepingService);
  });

  it('list delegates to Client.cleaningsGET with status, propertyId, assignedHousekeeperUserId, page and pageSize', () => {
    service.list(2, 25, 'Pending', 'prop-1', 'user-1').subscribe();
    expect(client['cleaningsGET']).toHaveBeenCalledWith('Pending', 'prop-1', 'user-1', 2, 25);
  });

  it('list forwards undefined filters when none are provided', () => {
    service.list(1, 10).subscribe();
    expect(client['cleaningsGET']).toHaveBeenCalledWith(undefined, undefined, undefined, 1, 10);
  });

  it('getById delegates to Client.cleaningsGET2 with the cleaning id', () => {
    service.getById('cleaning-1').subscribe();
    expect(client['cleaningsGET2']).toHaveBeenCalledWith('cleaning-1');
  });

  it('create delegates to Client.cleaningsPOST with the request body', () => {
    const request = { propertyId: 'prop-1', reservationId: undefined };
    service.create(request).subscribe();
    expect(client['cleaningsPOST']).toHaveBeenCalledWith(request);
  });

  it('assign delegates to Client.assign with the cleaning id and a housekeeperUserId body', () => {
    service.assign('cleaning-1', 'user-1').subscribe();
    expect(client['assign']).toHaveBeenCalledWith('cleaning-1', { housekeeperUserId: 'user-1' });
  });

  it('start delegates to Client.start with the cleaning id', () => {
    service.start('cleaning-1').subscribe();
    expect(client['start']).toHaveBeenCalledWith('cleaning-1');
  });

  it('startInspection delegates to Client.startInspection with the cleaning id', () => {
    service.startInspection('cleaning-1').subscribe();
    expect(client['startInspection']).toHaveBeenCalledWith('cleaning-1');
  });

  it('complete delegates to Client.complete with the cleaning id', () => {
    service.complete('cleaning-1').subscribe();
    expect(client['complete']).toHaveBeenCalledWith('cleaning-1');
  });

  it('cancel delegates to Client.cancelCleaning with the cleaning id', () => {
    service.cancel('cleaning-1').subscribe();
    expect(client['cancelCleaning']).toHaveBeenCalledWith('cleaning-1');
  });

  it('markInterrupted delegates to Client.interrupt with the cleaning id', () => {
    service.markInterrupted('cleaning-1').subscribe();
    expect(client['interrupt']).toHaveBeenCalledWith('cleaning-1');
  });

  it('markWaitingMaterials delegates to Client.waitingMaterials with the cleaning id', () => {
    service.markWaitingMaterials('cleaning-1').subscribe();
    expect(client['waitingMaterials']).toHaveBeenCalledWith('cleaning-1');
  });

  it('markWaitingHelp delegates to Client.waitingHelp with the cleaning id', () => {
    service.markWaitingHelp('cleaning-1').subscribe();
    expect(client['waitingHelp']).toHaveBeenCalledWith('cleaning-1');
  });
});
