import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { Client, OwnProfileResponse } from '../api/generated/api-client';
import { UserProfileService } from './user-profile.service';

function profile(overrides: Partial<OwnProfileResponse> = {}): OwnProfileResponse {
  return {
    id: 'user-1',
    fullName: 'Ada Lovelace',
    email: 'ada@example.com',
    status: 'Active',
    roles: ['ADMIN'],
    permissions: ['USERS:MANAGE'],
    createdAt: new Date('2026-01-01T00:00:00Z'),
    ...overrides,
  };
}

describe('UserProfileService', () => {
  let service: UserProfileService;
  let client: { me: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    client = { me: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(UserProfileService);
  });

  it('starts with no profile and an empty permissions list', () => {
    expect(service.profile()).toBeNull();
    expect(service.permissions()).toEqual([]);
  });

  it('hasPermission is false for any code before a profile has ever loaded (fails closed)', () => {
    expect(service.hasPermission('USERS:MANAGE')).toBe(false);
  });

  it('load() calls GET /api/v1/users/me and populates the profile and permissions signals', () => {
    client.me.mockReturnValue(of(profile()));

    service.load().subscribe();

    expect(client.me).toHaveBeenCalledTimes(1);
    expect(service.profile()?.id).toBe('user-1');
    expect(service.permissions()).toEqual(['USERS:MANAGE']);
  });

  it('hasPermission reflects the real permission codes returned by the backend, never a role name', () => {
    client.me.mockReturnValue(of(profile({ roles: ['ADMIN'], permissions: ['USERS:MANAGE'] })));

    service.load().subscribe();

    expect(service.hasPermission('USERS:MANAGE')).toBe(true);
    expect(service.hasPermission('ADMIN')).toBe(false);
  });

  it('permissions is the union across every role the user holds, with no duplicates assumed by the frontend', () => {
    client.me.mockReturnValue(of(profile({ permissions: ['USERS:MANAGE', 'RESERVATIONS:MANAGE'] })));

    service.load().subscribe();

    expect(service.permissions()).toEqual(['USERS:MANAGE', 'RESERVATIONS:MANAGE']);
  });

  it('falls back to an empty permissions array when the backend omits the field', () => {
    client.me.mockReturnValue(of(profile({ permissions: undefined })));

    service.load().subscribe();

    expect(service.permissions()).toEqual([]);
    expect(service.hasPermission('USERS:MANAGE')).toBe(false);
  });

  it('clear() resets the profile to null and permissions back to empty (used on logout)', () => {
    client.me.mockReturnValue(of(profile()));
    service.load().subscribe();
    expect(service.permissions()).toEqual(['USERS:MANAGE']);

    service.clear();

    expect(service.profile()).toBeNull();
    expect(service.permissions()).toEqual([]);
    expect(service.hasPermission('USERS:MANAGE')).toBe(false);
  });

  it(
    'hasPermission never prefix-matches — CLEANINGS:MANAGE does not grant CLEANINGS:MANAGE:OWN_CLEANING and vice versa ' +
      '(Fase 6, Incremento 2A approval §22 — the admin and self-service Housekeeping permissions must stay exact-match distinct)',
    () => {
      client.me.mockReturnValue(of(profile({ roles: ['ADMIN'], permissions: ['CLEANINGS:MANAGE'] })));
      service.load().subscribe();

      expect(service.hasPermission('CLEANINGS:MANAGE')).toBe(true);
      expect(service.hasPermission('CLEANINGS:MANAGE:OWN_CLEANING')).toBe(false);

      client.me.mockReturnValue(of(profile({ roles: ['HOUSEKEEPER'], permissions: ['CLEANINGS:MANAGE:OWN_CLEANING'] })));
      service.load().subscribe();

      expect(service.hasPermission('CLEANINGS:MANAGE:OWN_CLEANING')).toBe(true);
      expect(service.hasPermission('CLEANINGS:MANAGE')).toBe(false);
    },
  );

  it('leaves the profile and permissions untouched when load() fails, so callers must explicitly clear() on a refresh failure', () => {
    client.me.mockReturnValue(of(profile()));
    service.load().subscribe();

    client.me.mockReturnValue(throwError(() => new Error('network error')));
    service.load().subscribe({ error: () => undefined });

    expect(service.profile()?.id).toBe('user-1');
    expect(service.permissions()).toEqual(['USERS:MANAGE']);
  });
});
