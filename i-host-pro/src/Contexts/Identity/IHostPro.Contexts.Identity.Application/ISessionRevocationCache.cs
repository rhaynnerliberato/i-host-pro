namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Accelerates session-revocation checks (a future JWT Bearer validator's
/// fast path) with a best-effort cache — Incremento 2 plan, Etapa 12.
/// PostgreSQL (<c>Session.Status</c>) is always the source of truth; this
/// cache exists purely as an optimization. Implementations must never throw:
/// any failure (Redis unreachable, timeout, etc.) is swallowed internally
/// and reported only via structured telemetry, never propagated to the
/// caller — a cache-write failure must never fail Logout or undo the
/// PostgreSQL revocation that already committed.
/// </summary>
public interface ISessionRevocationCache
{
    /// <summary>
    /// Records that a session was revoked. Callers must invoke this only
    /// AFTER the revocation has already committed to PostgreSQL (see
    /// <see cref="ISessionRevocationSignal"/> — the mechanism that lets a
    /// handler stage this fact for its executor to act on post-commit,
    /// without the handler or the Unit of Work touching this cache from
    /// inside the transaction).
    /// </summary>
    Task MarkRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Fast, best-effort revocation check for a future JWT Bearer validator
    /// (Incremento 2 plan, Etapa 13). Fail-open: any cache-layer failure
    /// (Redis unreachable, timeout, etc.) is swallowed internally, reported
    /// only via structured telemetry, and reported back as <c>false</c>
    /// ("not known to be revoked") — PostgreSQL/the token's own cryptographic
    /// validity remain authoritative, so a cache outage must never itself
    /// reject an otherwise-valid request. This fail-open behavior applies
    /// only to genuine cache failures — an <see cref="OperationCanceledException"/>
    /// from the caller's own <paramref name="cancellationToken"/> (e.g. the
    /// HTTP request was aborted) must propagate normally, never be
    /// swallowed and reinterpreted as "cache unavailable".
    /// </summary>
    Task<bool> IsRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken);
}
