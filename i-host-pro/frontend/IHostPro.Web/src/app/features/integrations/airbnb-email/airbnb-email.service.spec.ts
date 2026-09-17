import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { AirbnbEmailConnectionStatus, Client } from '../../../core/api/generated/api-client';
import { AirbnbEmailService } from './airbnb-email.service';

describe('AirbnbEmailService', () => {
  let service: AirbnbEmailService;
  let client: Record<string, ReturnType<typeof vi.fn>>;

  beforeEach(() => {
    client = {
      airbnbEmail: vi.fn().mockReturnValue(of({})),
      disconnect: vi.fn().mockReturnValue(of({})),
      start: vi.fn().mockReturnValue(of({ authorizationUrl: 'https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=abc' })),
      autoPublication: vi.fn().mockReturnValue(of({})),
      enableAutoPublication: vi.fn().mockReturnValue(of({})),
      disableAutoPublication: vi.fn().mockReturnValue(of({})),
      processingSummary: vi.fn().mockReturnValue(of({})),
      listingTitleMappingsAll: vi.fn().mockReturnValue(of([])),
      listingTitleMappings: vi.fn().mockReturnValue(of({})),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Client, useValue: client }] });
    service = TestBed.inject(AirbnbEmailService);
  });

  describe('getStatus', () => {
    // The backend has no global JsonStringEnumConverter, so this enum
    // serializes as a plain integer over the wire and NSwag can only emit
    // unnamed _0/_1/_2/_3 members - these numeric values must stay in sync
    // with AirbnbEmailConnectionStatus.cs (NotConfigured=0, Disconnected=1,
    // Connected=2, Error=3).
    it.each([
      [AirbnbEmailConnectionStatus._0, 'notConfigured'],
      [AirbnbEmailConnectionStatus._1, 'disconnected'],
      [AirbnbEmailConnectionStatus._2, 'connected'],
      [AirbnbEmailConnectionStatus._3, 'error'],
      [undefined, 'notConfigured'],
    ])('maps raw status %s to label %s', (rawStatus, expectedLabel) => {
      client['airbnbEmail'].mockReturnValue(of({ status: rawStatus, isEnabled: true, mailboxAddress: 'guest@hotmail.com' }));

      let result: { status: string } | undefined;
      service.getStatus().subscribe((value) => (result = value));

      expect(result?.status).toBe(expectedLabel);
    });

    it('defaults isEnabled to false and preserves undefined lastAuthenticatedAtUtc/mailboxAddress when omitted', () => {
      client['airbnbEmail'].mockReturnValue(of({ status: AirbnbEmailConnectionStatus._0 }));

      let result: { isEnabled: boolean; lastAuthenticatedAtUtc: Date | undefined; mailboxAddress: string | undefined } | undefined;
      service.getStatus().subscribe((value) => (result = value));

      expect(result?.isEnabled).toBe(false);
      expect(result?.lastAuthenticatedAtUtc).toBeUndefined();
      expect(result?.mailboxAddress).toBeUndefined();
    });
  });

  it('disconnect delegates to Client.disconnect', () => {
    service.disconnect().subscribe();
    expect(client['disconnect']).toHaveBeenCalledWith();
  });

  it('connect delegates to Client.start and returns the authorization URL', () => {
    let result: string | undefined;
    service.connect().subscribe((value) => (result = value));

    expect(client['start']).toHaveBeenCalledWith();
    expect(result).toBe('https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=abc');
  });

  it('connect returns an empty string when the backend omits authorizationUrl', () => {
    client['start'].mockReturnValue(of({}));

    let result: string | undefined;
    service.connect().subscribe((value) => (result = value));

    expect(result).toBe('');
  });

  it('getAutoPublicationStatus delegates to Client.autoPublication', () => {
    service.getAutoPublicationStatus().subscribe();
    expect(client['autoPublication']).toHaveBeenCalledWith();
  });

  it('enableAutoPublication sends the given cutoff as notBeforeUtc', () => {
    const cutoff = new Date('2026-09-16T12:00:00Z');
    service.enableAutoPublication(cutoff).subscribe();
    expect(client['enableAutoPublication']).toHaveBeenCalledWith({ notBeforeUtc: cutoff });
  });

  it('disableAutoPublication delegates to Client.disableAutoPublication', () => {
    service.disableAutoPublication().subscribe();
    expect(client['disableAutoPublication']).toHaveBeenCalledWith();
  });

  it('getProcessingSummary defaults every count to 0 when omitted', () => {
    client['processingSummary'].mockReturnValue(of({}));

    let result: { pending: number; processed: number; needsReview: number; failed: number; ignored: number } | undefined;
    service.getProcessingSummary().subscribe((value) => (result = value));

    expect(result).toEqual({ pending: 0, processed: 0, needsReview: 0, failed: 0, ignored: 0 });
  });

  it('getProcessingSummary forwards real counts unchanged', () => {
    client['processingSummary'].mockReturnValue(of({ pending: 60, processed: 3, needsReview: 1, failed: 2, ignored: 4 }));

    let result: { pending: number } | undefined;
    service.getProcessingSummary().subscribe((value) => (result = value));

    expect(result).toEqual({ pending: 60, processed: 3, needsReview: 1, failed: 2, ignored: 4 });
  });

  it('listMappings delegates to Client.listingTitleMappingsAll', () => {
    service.listMappings().subscribe();
    expect(client['listingTitleMappingsAll']).toHaveBeenCalledWith();
  });

  it('createMapping delegates to Client.listingTitleMappings with the request', () => {
    const request = { listingTitle: 'Casa na Praia', propertyId: 'prop-1' };
    service.createMapping(request).subscribe();
    expect(client['listingTitleMappings']).toHaveBeenCalledWith(request);
  });
});
