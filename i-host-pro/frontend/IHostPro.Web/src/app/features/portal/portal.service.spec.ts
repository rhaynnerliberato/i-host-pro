import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client } from '../../core/api/generated/api-client';
import { PortalService } from './portal.service';

describe('PortalService', () => {
  let service: PortalService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = {
      myCleanings: vi.fn().mockReturnValue(of({})),
      myCleanings2: vi.fn().mockReturnValue(of({})),
      inTransit: vi.fn().mockReturnValue(of({})),
      startOwnCleaning: vi.fn().mockReturnValue(of({})),
      startOwnCleaningInspection: vi.fn().mockReturnValue(of({})),
      completeOwnCleaning: vi.fn().mockReturnValue(of({})),
      markOwnCleaningWaitingMaterials: vi.fn().mockReturnValue(of({})),
      markOwnCleaningWaitingHelp: vi.fn().mockReturnValue(of({})),
      delay: vi.fn().mockReturnValue(of({})),
      occurrences: vi.fn().mockReturnValue(of({})),
      occurrencesAll: vi.fn().mockReturnValue(of([])),
      checklistAll: vi.fn().mockReturnValue(of([])),
      checklist: vi.fn().mockReturnValue(of({})),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(PortalService);
  });

  it('listMyCleanings delegates to Client.myCleanings with status, page and pageSize', () => {
    service.listMyCleanings('Started', 2, 25).subscribe();
    expect(client['myCleanings']).toHaveBeenCalledWith('Started', 2, 25);
  });

  it('getMyCleaningById delegates to Client.myCleanings2 with the cleaning id', () => {
    service.getMyCleaningById('cleaning-1').subscribe();
    expect(client['myCleanings2']).toHaveBeenCalledWith('cleaning-1');
  });

  it('markInTransit delegates to Client.inTransit with the cleaning id', () => {
    service.markInTransit('cleaning-1').subscribe();
    expect(client['inTransit']).toHaveBeenCalledWith('cleaning-1');
  });

  it('start delegates to Client.startOwnCleaning, never the administrative Client.start', () => {
    service.start('cleaning-1').subscribe();
    expect(client['startOwnCleaning']).toHaveBeenCalledWith('cleaning-1');
  });

  it('startInspection delegates to Client.startOwnCleaningInspection', () => {
    service.startInspection('cleaning-1').subscribe();
    expect(client['startOwnCleaningInspection']).toHaveBeenCalledWith('cleaning-1');
  });

  it('complete delegates to Client.completeOwnCleaning', () => {
    service.complete('cleaning-1').subscribe();
    expect(client['completeOwnCleaning']).toHaveBeenCalledWith('cleaning-1');
  });

  it('markWaitingMaterials delegates to Client.markOwnCleaningWaitingMaterials', () => {
    service.markWaitingMaterials('cleaning-1').subscribe();
    expect(client['markOwnCleaningWaitingMaterials']).toHaveBeenCalledWith('cleaning-1');
  });

  it('markWaitingHelp delegates to Client.markOwnCleaningWaitingHelp', () => {
    service.markWaitingHelp('cleaning-1').subscribe();
    expect(client['markOwnCleaningWaitingHelp']).toHaveBeenCalledWith('cleaning-1');
  });

  it('reportDelay delegates to Client.delay with the cleaning id', () => {
    service.reportDelay('cleaning-1').subscribe();
    expect(client['delay']).toHaveBeenCalledWith('cleaning-1');
  });

  it('registerOccurrence delegates to Client.occurrences with a type/description body', () => {
    service.registerOccurrence('cleaning-1', 'Damage', 'Broken lamp').subscribe();
    expect(client['occurrences']).toHaveBeenCalledWith('cleaning-1', { type: 'Damage', description: 'Broken lamp' });
  });

  it('listOccurrences delegates to Client.occurrencesAll with the cleaning id', () => {
    service.listOccurrences('cleaning-1').subscribe();
    expect(client['occurrencesAll']).toHaveBeenCalledWith('cleaning-1');
  });

  it('getChecklist delegates to Client.checklistAll with the cleaning id', () => {
    service.getChecklist('cleaning-1').subscribe();
    expect(client['checklistAll']).toHaveBeenCalledWith('cleaning-1');
  });

  it('setChecklistItem delegates to Client.checklist with the cleaning id, item type, and an isChecked body', () => {
    service.setChecklistItem('cleaning-1', 'Stove', true).subscribe();
    expect(client['checklist']).toHaveBeenCalledWith('cleaning-1', 'Stove', { isChecked: true });
  });
});
