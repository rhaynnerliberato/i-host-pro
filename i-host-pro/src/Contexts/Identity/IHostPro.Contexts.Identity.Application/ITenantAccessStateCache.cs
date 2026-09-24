namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Tracks whether a tenant's already-issued credentials (access tokens,
/// sessions) may still be used, independently of <see cref="Domain.Tenant.Status"/>
/// in PostgreSQL — which only gates NEW logins/refreshes (see
/// <c>ITenantBootstrapReader</c>), never an already-issued credential.
///
/// Tenant Suspension/Reactivation Enforcement workstream: suspending a tenant
/// must immediately block every credential already in a client's hands, not
/// merely new ones, and reactivating a tenant must NOT silently resurrect
/// those old credentials — a fresh login is required. Recording only
/// Suspended/Active would fail the second half: clearing back to Active on
/// reactivation would make a pre-suspension token valid again. Instead, a
/// reactivation records the instant access is restored FROM, and
/// <see cref="IsAccessAllowedAsync"/> rejects any credential issued before
/// that instant, regardless of tenant status.
///
/// Consulted on the hot authenticated-request path
/// (<c>ConfigureJwtBearerOptions.OnTokenValidatedAsync</c>) — implementations
/// must never fall back to PostgreSQL there. Unlike <see cref="ISessionRevocationCache"/>
/// (fail-open: a cache failure must never block an otherwise-valid request),
/// this cache fails CLOSED: since it is the only enforcement point for an
/// already-issued credential, a failure to determine a tenant's access state
/// must deny, never silently allow a possibly-suspended tenant through.
/// </summary>
public interface ITenantAccessStateCache
{
    /// <summary>
    /// Records that <paramref name="tenantId"/> is suspended — every
    /// credential for it, regardless of when issued, must be denied from now
    /// on until <see cref="MarkReactivatedAsync"/> is called.
    /// </summary>
    Task MarkSuspendedAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="tenantId"/> is active again as of
    /// <paramref name="reactivatedAtUtc"/> — a credential issued before this
    /// instant remains denied (it predates the reactivation and must not be
    /// silently resurrected); only a credential issued at or after this
    /// instant (i.e. from a fresh login) is allowed.
    /// </summary>
    Task MarkReactivatedAsync(Guid tenantId, DateTimeOffset reactivatedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// True when a credential for <paramref name="tenantId"/> issued at
    /// <paramref name="credentialIssuedAtUtc"/> (an access token's <c>iat</c>,
    /// or a session's creation time) may still be used. A tenant that was
    /// never suspended (no cache entry at all) is always allowed — this cache
    /// only ever needs to remember tenants that WERE suspended at least once.
    ///
    /// Comparison is whole-second granularity (a JWT <c>iat</c>/NumericDate
    /// has no sub-second precision), inclusive at the boundary — a credential
    /// issued in the exact same second as the reactivation cutover is
    /// allowed. This is a deliberate, accepted narrow limitation: it favors
    /// never falsely rejecting a genuine fresh login that happens to land in
    /// that same second, at the cost of a purely theoretical one-second
    /// window where a credential issued a moment before a suspend-then-
    /// reactivate cycle that itself completed within that same second would
    /// also pass. Not reachable in real operation — suspending and
    /// reactivating a tenant is a manual administrative action that takes far
    /// longer than one second end to end.
    /// </summary>
    Task<bool> IsAccessAllowedAsync(Guid tenantId, DateTimeOffset credentialIssuedAtUtc, CancellationToken cancellationToken);
}
