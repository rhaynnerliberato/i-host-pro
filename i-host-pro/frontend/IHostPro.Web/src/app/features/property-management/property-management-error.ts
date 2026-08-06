/**
 * Same duck-typing rationale as `features/users/user-error.ts`: the
 * generated `Client` throws the raw deserialized `ProblemDetails` object (or
 * an `ApiException`) for a non-2xx response, never Angular's
 * `HttpErrorResponse`. `PropertyManagementResultHttpMapper` (backend, its
 * own mapper — not shared with Identity's) maps every failure to exactly one
 * of 400/404/409, with 400 alone carrying a `codes` array — 404/409 never
 * distinguish *which* not-found/conflict reason occurred beyond the status
 * code itself, so callers can only show a status-level generic message for
 * those two, never a code-specific one.
 */
export interface PropertyManagementActionError {
  status: number | undefined;
  codes: string[];
}

export function classifyPropertyManagementError(error: unknown): PropertyManagementActionError {
  if (typeof error !== 'object' || error === null) {
    return { status: undefined, codes: [] };
  }

  const status = (error as { status?: unknown }).status;
  const codesRaw = (error as { codes?: unknown }).codes;
  const codes = Array.isArray(codesRaw) ? codesRaw.filter((code): code is string => typeof code === 'string') : [];

  return { status: typeof status === 'number' ? status : undefined, codes };
}
