/**
 * Same duck-typing rationale as `features/reservations/reservation-error.ts`:
 * the generated `Client` throws the raw deserialized `ProblemDetails` object
 * for a non-2xx response, never Angular's `HttpErrorResponse`.
 * `HousekeepingResultHttpMapper` (backend) maps 404/409/403 to a single
 * `Extensions["code"]` string (unlike Reservations, which only ever exposes
 * status-level 404/409), and 400 validation failures to an
 * `Extensions["codes"]` array — both are read here.
 */
export interface HousekeepingActionError {
  status: number | undefined;
  code: string | undefined;
  codes: string[];
}

export function classifyHousekeepingError(error: unknown): HousekeepingActionError {
  if (typeof error !== 'object' || error === null) {
    return { status: undefined, code: undefined, codes: [] };
  }

  const status = (error as { status?: unknown }).status;
  const code = (error as { code?: unknown }).code;
  const codesRaw = (error as { codes?: unknown }).codes;
  const codes = Array.isArray(codesRaw) ? codesRaw.filter((c): c is string => typeof c === 'string') : [];

  return {
    status: typeof status === 'number' ? status : undefined,
    code: typeof code === 'string' ? code : undefined,
    codes,
  };
}
