/**
 * NSwag's generated `Client` never surfaces Angular's `HttpErrorResponse` for
 * a non-2xx response — it parses the response body itself and throws either
 * the deserialized `ProblemDetails` object (when the backend returns one, as
 * `Login` does) or its own `ApiException`. Both carry a numeric `status`
 * field mirroring the HTTP status; neither is an `HttpErrorResponse`. Duck-
 * typing on `status` (rather than depending on `ApiException` specifically)
 * covers both shapes without importing the generated client's types here.
 */
export function isInvalidCredentialsError(error: unknown): boolean {
  const status = typeof error === 'object' && error !== null ? (error as { status?: unknown }).status : undefined;
  return status === 401;
}
