/**
 * Unlike `classifyUserActionError`/`classifyReservationError`, `PolicyResultHttpMapper`
 * (backend) distinguishes its seven outcomes by `ProblemDetails.Title` itself
 * (`policy_not_found`, `invalid_policy_value`, `scope_not_supported`,
 * `policy_not_configured`, `version_conflict`, `forbidden`, `validation_error`)
 * — never by a `codes` array, which is populated only for the generic
 * `validation_error` fallback (FluentValidation's comma-joined error codes).
 * The generated `Client` still throws the raw deserialized `ProblemDetails`
 * object (or an `ApiException`) for a non-2xx response, so the same
 * duck-typing rationale as `user-error.ts` applies.
 */
export interface PolicyActionError {
  status: number | undefined;
  title: string | undefined;
  codes: string[];
}

export function classifyPolicyActionError(error: unknown): PolicyActionError {
  if (typeof error !== 'object' || error === null) {
    return { status: undefined, title: undefined, codes: [] };
  }

  const status = (error as { status?: unknown }).status;
  const title = (error as { title?: unknown }).title;
  const codesRaw = (error as { codes?: unknown }).codes;
  const codes = Array.isArray(codesRaw) ? codesRaw.filter((code): code is string => typeof code === 'string') : [];

  return {
    status: typeof status === 'number' ? status : undefined,
    title: typeof title === 'string' ? title : undefined,
    codes,
  };
}
