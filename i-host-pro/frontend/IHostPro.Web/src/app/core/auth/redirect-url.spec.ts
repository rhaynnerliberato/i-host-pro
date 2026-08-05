import { isSafeRedirectPath } from './redirect-url';

describe('isSafeRedirectPath', () => {
  it('accepts an in-app absolute path', () => {
    expect(isSafeRedirectPath('/reservations/123')).toBe(true);
  });

  it('rejects null/undefined/empty', () => {
    expect(isSafeRedirectPath(null)).toBe(false);
    expect(isSafeRedirectPath(undefined)).toBe(false);
    expect(isSafeRedirectPath('')).toBe(false);
  });

  it('rejects an absolute external URL', () => {
    expect(isSafeRedirectPath('https://evil.example')).toBe(false);
  });

  it('rejects a protocol-relative URL (open-redirect trick)', () => {
    expect(isSafeRedirectPath('//evil.example')).toBe(false);
  });

  it('rejects a backslash-based open-redirect trick', () => {
    expect(isSafeRedirectPath('/\\evil.example')).toBe(false);
  });

  it('rejects a path that does not start with a slash', () => {
    expect(isSafeRedirectPath('reservations/123')).toBe(false);
  });
});
