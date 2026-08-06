import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { Client } from '../../core/api/generated/api-client';
import { CondominiumsService } from './condominiums.service';

describe('CondominiumsService', () => {
  let service: CondominiumsService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = {
      condominiumsGET: vi.fn().mockReturnValue(of({})),
      condominiumsGET2: vi.fn().mockReturnValue(of({})),
      condominiumsPOST: vi.fn().mockReturnValue(of({})),
      condominiumsPATCH: vi.fn().mockReturnValue(of({})),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(CondominiumsService);
  });

  it('list delegates to Client.condominiumsGET with page and pageSize', () => {
    service.list(2, 25).subscribe();
    expect(client['condominiumsGET']).toHaveBeenCalledWith(2, 25);
  });

  it('getById delegates to Client.condominiumsGET2 with the condominium id', () => {
    service.getById('condo-1').subscribe();
    expect(client['condominiumsGET2']).toHaveBeenCalledWith('condo-1');
  });

  it('create delegates to Client.condominiumsPOST with the request body', () => {
    const request = { name: 'Edificio Sol', address: { zipCode: '01000-000', street: 'Rua A', number: '10', neighborhood: 'Centro', city: 'SP', state: 'SP', country: 'BR' } };
    service.create(request).subscribe();
    expect(client['condominiumsPOST']).toHaveBeenCalledWith(request);
  });

  it('update delegates to Client.condominiumsPATCH with the condominium id and request body', () => {
    const request = { name: 'Edificio Lua' };
    service.update('condo-1', request).subscribe();
    expect(client['condominiumsPATCH']).toHaveBeenCalledWith('condo-1', request);
  });
});
