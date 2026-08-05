import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AuthStateService } from './auth-state.service';
import { authInterceptor } from './auth.interceptor';

// The NSwag-generated Client requests responseType: 'blob' and parses the body itself,
// so HttpTestingController must be flushed with an actual Blob for those requests —
// a plain object or string is not auto-converted.
function jsonBlob(value: unknown): Blob {
  return new Blob([JSON.stringify(value)], { type: 'application/json' });
}

function tick(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// AuthService (and, through it, the generated Client) is now constructed lazily, the
// first time a 401 is actually handled — and the Client's Blob-based response parsing
// (FileReader) resolves over a real, variable number of event-loop turns. A fixed tick
// count is flaky; poll instead until the expected follow-up request actually shows up.
async function waitForRequest(httpMock: HttpTestingController, url: string, maxTicks = 20): Promise<TestRequest> {
  for (let attempt = 0; attempt < maxTicks; attempt++) {
    const matches = httpMock.match(url);
    if (matches.length > 0) {
      return matches[0];
    }
    await tick();
  }
  throw new Error(`Timed out waiting for a request to ${url}`);
}

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let authState: AuthStateService;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    authState = TestBed.inject(AuthStateService);
  });

  afterEach(() => httpMock.verify());

  it('attaches the Authorization header when an access token is set', () => {
    authState.setTokens({ accessToken: 'access-1', refreshToken: 'refresh-1' });

    http.get('/api/v1/users/me').subscribe();

    const req = httpMock.expectOne('/api/v1/users/me');
    expect(req.request.headers.get('Authorization')).toBe('Bearer access-1');
    req.flush({});
  });

  it('on a 401, refreshes once and retries the original request with the new token', async () => {
    authState.setTokens({ accessToken: 'expired', refreshToken: 'refresh-1' });

    let result: unknown;
    http.get('/api/v1/users/me').subscribe((value) => (result = value));

    const firstAttempt = httpMock.expectOne('/api/v1/users/me');
    firstAttempt.flush(null, { status: 401, statusText: 'Unauthorized' });

    const refreshReq = await waitForRequest(httpMock, '/api/v1/auth/refresh');
    expect(JSON.parse(refreshReq.request.body as string)).toEqual({ refreshToken: 'refresh-1' });
    refreshReq.flush(jsonBlob({ accessToken: 'fresh', refreshToken: 'refresh-2' }));

    const retryAttempt = await waitForRequest(httpMock, '/api/v1/users/me');
    expect(retryAttempt.request.headers.get('Authorization')).toBe('Bearer fresh');
    retryAttempt.flush({ ok: true });

    expect(result).toEqual({ ok: true });
    expect(authState.accessToken()).toBe('fresh');
    expect(authState.refreshToken).toBe('refresh-2');
  });

  it('does not retry a request a second time if the retried request also gets a 401', async () => {
    authState.setTokens({ accessToken: 'expired', refreshToken: 'refresh-1' });

    let error: unknown;
    http.get('/api/v1/users/me').subscribe({ error: (err) => (error = err) });

    httpMock.expectOne('/api/v1/users/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    (await waitForRequest(httpMock, '/api/v1/auth/refresh')).flush(
      jsonBlob({ accessToken: 'fresh', refreshToken: 'refresh-2' }),
    );
    (await waitForRequest(httpMock, '/api/v1/users/me')).flush(null, { status: 401, statusText: 'Unauthorized' });

    expect((error as { status: number }).status).toBe(401);
    httpMock.expectNone('/api/v1/auth/refresh');
  });

  it('does not attempt a refresh on a 403', () => {
    authState.setTokens({ accessToken: 'access-1', refreshToken: 'refresh-1' });

    let error: unknown;
    http.get('/api/v1/users/me').subscribe({ error: (err) => (error = err) });

    httpMock.expectOne('/api/v1/users/me').flush(null, { status: 403, statusText: 'Forbidden' });

    expect((error as { status: number }).status).toBe(403);
    httpMock.expectNone('/api/v1/auth/refresh');
  });

  it('does not attempt a refresh for a 401 from the login endpoint itself', () => {
    let error: unknown;
    http.post('/api/v1/auth/login', {}).subscribe({ error: (err) => (error = err) });

    httpMock.expectOne('/api/v1/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect((error as { status: number }).status).toBe(401);
    httpMock.expectNone('/api/v1/auth/refresh');
  });

  it('shares a single refresh call across two concurrent 401s (single-flight)', async () => {
    authState.setTokens({ accessToken: 'expired', refreshToken: 'refresh-1' });

    http.get('/api/v1/users/me').subscribe();
    http.get('/api/v1/condominiums').subscribe();

    httpMock.expectOne('/api/v1/users/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne('/api/v1/condominiums').flush(null, { status: 401, statusText: 'Unauthorized' });

    (await waitForRequest(httpMock, '/api/v1/auth/refresh')).flush(
      jsonBlob({ accessToken: 'fresh', refreshToken: 'refresh-2' }),
    );

    (await waitForRequest(httpMock, '/api/v1/users/me')).flush({});
    (await waitForRequest(httpMock, '/api/v1/condominiums')).flush({});
  });
});
