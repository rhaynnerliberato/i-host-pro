/**
 * Same duck-typing rationale as `features/auth/login/login-error.ts`: the
 * generated `Client` throws the raw deserialized `ProblemDetails` object (or
 * an `ApiException`) for a non-2xx response, never Angular's
 * `HttpErrorResponse`. `ResultHttpMapper` (backend) maps every user-
 * administration failure to exactly one of 400/404/409, with 400 alone
 * carrying a `codes` array (ASP.NET Core's `ProblemDetails.Extensions`
 * serializes flattened onto the root object, never nested) — 404/409 never
 * distinguish *which* not-found/conflict reason occurred beyond the status
 * code itself, so callers can only show a status-level generic message for
 * those two, never a code-specific one.
 */
export interface UserActionError {
  status: number | undefined;
  codes: string[];
}

export function classifyUserActionError(error: unknown): UserActionError {
  if (typeof error !== 'object' || error === null) {
    return { status: undefined, codes: [] };
  }

  const status = (error as { status?: unknown }).status;
  const codesRaw = (error as { codes?: unknown }).codes;
  const codes = Array.isArray(codesRaw) ? codesRaw.filter((code): code is string => typeof code === 'string') : [];

  return { status: typeof status === 'number' ? status : undefined, codes };
}
