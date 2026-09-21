/**
 * Same duck-typing rationale as `login/login-error.ts` and `features/users/user-error.ts`:
 * the generated `Client` throws the raw deserialized `ProblemDetails` object
 * (or an `ApiException`) for a non-2xx response, never Angular's
 * `HttpErrorResponse`. Signup and reset-password-complete both map every
 * failure through the same generic-400 branch of `ResultHttpMapper`
 * (backend), carrying a `codes` array of stable ASCII codes — either the
 * password-policy validator's codes (e.g. `PasswordTooShort`) or, for
 * reset-password-complete only, the single undifferentiated
 * `Identity.PasswordResetTokenInvalid` code.
 */
export interface AuthActionError {
  status: number | undefined;
  codes: string[];
}

export function classifyAuthActionError(error: unknown): AuthActionError {
  if (typeof error !== 'object' || error === null) {
    return { status: undefined, codes: [] };
  }

  const status = (error as { status?: unknown }).status;
  const codesRaw = (error as { codes?: unknown }).codes;
  const codes = Array.isArray(codesRaw) ? codesRaw.filter((code): code is string => typeof code === 'string') : [];

  return { status: typeof status === 'number' ? status : undefined, codes };
}
