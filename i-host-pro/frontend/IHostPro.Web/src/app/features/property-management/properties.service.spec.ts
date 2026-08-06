import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client } from '../../core/api/generated/api-client';
import { PropertiesService } from './properties.service';

describe('PropertiesService', () => {
  let service: PropertiesService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = {
      propertiesGET: vi.fn().mockReturnValue(of({})),
      propertiesGET2: vi.fn().mockReturnValue(of({})),
      propertiesPOST: vi.fn().mockReturnValue(of({})),
      propertiesPATCH: vi.fn().mockReturnValue(of({})),
      activate: vi.fn().mockReturnValue(of({})),
      deactivate: vi.fn().mockReturnValue(of({})),
      archive: vi.fn().mockReturnValue(of({})),
      ownersGET: vi.fn().mockReturnValue(of({})),
      ownersPOST: vi.fn().mockReturnValue(of({})),
      ownersDELETE: vi.fn().mockReturnValue(of(undefined)),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(PropertiesService);
  });

  it('list delegates to Client.propertiesGET with page and pageSize', () => {
    service.list(2, 25).subscribe();
    expect(client['propertiesGET']).toHaveBeenCalledWith(2, 25);
  });

  it('getById delegates to Client.propertiesGET2 with the property id', () => {
    service.getById('prop-1').subscribe();
    expect(client['propertiesGET2']).toHaveBeenCalledWith('prop-1');
  });

  it('create delegates to Client.propertiesPOST with the request body', () => {
    const request = { code: 'APT-1', name: 'Apto 1', capacity: 2 };
    service.create(request).subscribe();
    expect(client['propertiesPOST']).toHaveBeenCalledWith(request);
  });

  it('update sends every field as a bare value, preserving explicit null for condominiumId/address (clears the link/own address)', () => {
    service
      .update('prop-1', { code: 'APT-1', name: 'Apto 1', capacity: 4, condominiumId: null, address: null })
      .subscribe();

    expect(client['propertiesPATCH']).toHaveBeenCalledWith('prop-1', {
      code: 'APT-1',
      name: 'Apto 1',
      capacity: 4,
      condominiumId: null,
      address: null,
    });
  });

  it('update forwards a real condominiumId/address without conversion', () => {
    const address = { zipCode: '01000-000', street: 'Rua A', number: '10', neighborhood: 'Centro', city: 'SP', state: 'SP', country: 'BR' };

    service.update('prop-1', { code: 'APT-1', name: 'Apto 1', capacity: 4, condominiumId: 'condo-1', address }).subscribe();

    expect(client['propertiesPATCH']).toHaveBeenCalledWith('prop-1', {
      code: 'APT-1',
      name: 'Apto 1',
      capacity: 4,
      condominiumId: 'condo-1',
      address,
    });
  });

  it('activate delegates to Client.activate with the property id', () => {
    service.activate('prop-1').subscribe();
    expect(client['activate']).toHaveBeenCalledWith('prop-1');
  });

  it('deactivate delegates to Client.deactivate with the property id', () => {
    service.deactivate('prop-1').subscribe();
    expect(client['deactivate']).toHaveBeenCalledWith('prop-1');
  });

  it('archive delegates to Client.archive with the property id', () => {
    service.archive('prop-1').subscribe();
    expect(client['archive']).toHaveBeenCalledWith('prop-1');
  });

  it('listOwners delegates to Client.ownersGET with the property id, page and pageSize', () => {
    service.listOwners('prop-1', 1, 100).subscribe();
    expect(client['ownersGET']).toHaveBeenCalledWith('prop-1', 1, 100);
  });

  it('linkOwner delegates to Client.ownersPOST with the property id and request body', () => {
    const request = { ownerUserId: 'user-1' };
    service.linkOwner('prop-1', request).subscribe();
    expect(client['ownersPOST']).toHaveBeenCalledWith('prop-1', request);
  });

  it('unlinkOwner delegates to Client.ownersDELETE with the property id and owner user id', () => {
    service.unlinkOwner('prop-1', 'user-1').subscribe();
    expect(client['ownersDELETE']).toHaveBeenCalledWith('prop-1', 'user-1');
  });
});
