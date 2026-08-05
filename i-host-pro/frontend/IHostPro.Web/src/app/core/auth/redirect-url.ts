/**
 * A `redirectTo` query param is user-controllable (anyone can type it into the address bar),
 * so it must be validated before being used for navigation — otherwise it becomes an open
 * redirect (e.g. `?redirectTo=https://evil.example` or `?redirectTo=//evil.example`).
 * Only an in-app, absolute path is accepted.
 */
export function isSafeRedirectPath(path: string | null | undefined): path is string {
  if (!path) {
    return false;
  }
  if (!path.startsWith('/')) {
    return false;
  }
  if (path.startsWith('//') || path.startsWith('/\\')) {
    return false;
  }
  return true;
}
