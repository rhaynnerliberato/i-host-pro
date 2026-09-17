import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { AirbnbEmailMailboxStatus, AirbnbEmailService } from '../airbnb-email.service';
import { AirbnbEmailPage } from './airbnb-email-page';

function configure(queryParam: string | null = null) {
  const airbnbEmailService = {
    getStatus: vi.fn().mockReturnValue(of({ status: 'notConfigured', isEnabled: false, lastAuthenticatedAtUtc: undefined, mailboxAddress: undefined } as AirbnbEmailMailboxStatus)),
    getAutoPublicationStatus: vi.fn().mockReturnValue(of({ autoPublishEnabled: false })),
    listMappings: vi.fn().mockReturnValue(of([])),
    getProcessingSummary: vi.fn().mockReturnValue(of({ pending: 0, processed: 0, needsReview: 0, failed: 0, ignored: 0 })),
    connect: vi.fn().mockReturnValue(of('https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=abc')),
    disconnect: vi.fn().mockReturnValue(of(undefined)),
  };
  const router = { navigate: vi.fn() };
  const snackBar = { open: vi.fn() };
  const transloco = { translate: (key: string) => key, selectTranslate: (key: string) => of(key) };
  const activatedRoute = { snapshot: { queryParamMap: { get: (name: string) => (name === 'connect' ? queryParam : null) } } };

  TestBed.configureTestingModule({
    providers: [
      { provide: AirbnbEmailService, useValue: airbnbEmailService },
      { provide: Router, useValue: router },
      { provide: MatSnackBar, useValue: snackBar },
      { provide: TranslocoService, useValue: transloco },
      { provide: ActivatedRoute, useValue: activatedRoute },
    ],
  });
  const component = TestBed.runInInjectionContext(() => new AirbnbEmailPage());
  return { component, airbnbEmailService, router, snackBar };
}

describe('AirbnbEmailPage', () => {
  describe('connect()', () => {
    it('navigates the browser to the backend-issued authorization URL on success', () => {
      const { component } = configure();
      const originalLocation = window.location;
      // window.location.href is not directly assignable in jsdom without this.
      Object.defineProperty(window, 'location', { value: { ...originalLocation, href: '' }, writable: true });

      component['connect']();

      expect(window.location.href).toBe('https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=abc');
      Object.defineProperty(window, 'location', { value: originalLocation, writable: true });
    });

    it('shows a generic error and does not navigate when the backend call fails', () => {
      const { component, airbnbEmailService, snackBar } = configure();
      airbnbEmailService.connect.mockReturnValue(throwError(() => new Error('network error')));

      component['connect']();

      expect(snackBar.open).toHaveBeenCalledWith('integrations.airbnbEmail.connection.errors.generic', undefined, { duration: 4000 });
      expect(component['connecting']()).toBe(false);
    });

    it('ignores a second click while a connect request is already in flight', () => {
      const { component, airbnbEmailService } = configure();
      component['connecting'].set(true);

      component['connect']();

      expect(airbnbEmailService.connect).not.toHaveBeenCalled();
    });
  });

  describe('oauth callback result handling', () => {
    it('shows a success toast and strips the connect query parameter when connect=success', () => {
      const { snackBar, router } = configure('success');

      expect(snackBar.open).toHaveBeenCalledWith('integrations.airbnbEmail.connection.connectSuccess', undefined, { duration: 5000 });
      expect(router.navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: {}, replaceUrl: true }));
    });

    it('shows a denied toast when connect=denied', () => {
      const { snackBar } = configure('denied');
      expect(snackBar.open).toHaveBeenCalledWith('integrations.airbnbEmail.connection.connectDenied', undefined, { duration: 5000 });
    });

    it('shows an expired toast when connect=expired', () => {
      const { snackBar } = configure('expired');
      expect(snackBar.open).toHaveBeenCalledWith('integrations.airbnbEmail.connection.connectExpired', undefined, { duration: 5000 });
    });

    it('shows an error toast for any unrecognized result code', () => {
      const { snackBar } = configure('something-unexpected');
      expect(snackBar.open).toHaveBeenCalledWith('integrations.airbnbEmail.connection.connectError', undefined, { duration: 5000 });
    });

    it('does nothing when there is no connect query parameter', () => {
      const { snackBar, router } = configure(null);

      expect(snackBar.open).not.toHaveBeenCalled();
      expect(router.navigate).not.toHaveBeenCalled();
    });
  });
});
