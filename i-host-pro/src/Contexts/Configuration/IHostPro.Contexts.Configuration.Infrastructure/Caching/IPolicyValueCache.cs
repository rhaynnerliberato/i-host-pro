using IHostPro.Contexts.Configuration.Infrastructure.Resolution;

namespace IHostPro.Contexts.Configuration.Infrastructure.Caching;

/// <summary>
/// Caches the resolved outcome of <see cref="IPolicyValueResolver.ResolveAsync"/>
/// (Fase 5, Incremento 1, Checkpoint 6, §6) — internal to
/// <c>Configuration.Infrastructure</c>, exactly like <see cref="IPolicyValueResolver"/>
/// itself: its only consumer, <see cref="CachedPolicyValueResolver"/>, lives
/// in this same assembly. Invalidation is deliberately a SEPARATE, public
/// interface (<see cref="IPolicyCacheInvalidator"/>) rather than a member
/// here — the <c>PolicyUpdated</c> consumer that calls it is constructed by
/// <c>IHostPro.Worker</c>'s DI container (a different assembly) and must be a
/// public class, so its constructor cannot take a parameter type (like
/// <see cref="PolicyValueResolution"/>, used by
/// <see cref="TryGetAsync"/>/<see cref="SetAsync"/>) that is less accessible
/// than itself.
///
/// Implementations must never throw for a genuine cache failure — see
/// <see cref="RedisPolicyValueCache"/>'s own doc comment for the fail-closed
/// rationale (PostgreSQL remains authoritative; a Redis outage degrades to
/// "nothing cached", never to a fabricated or stale-but-trusted answer).
/// </summary>
internal interface IPolicyValueCache
{
    /// <summary>Returns the cached resolution, or <c>null</c> on a genuine cache miss (including any cache-layer failure, treated identically to a miss).</summary>
    Task<PolicyValueResolution?> TryGetAsync(Guid tenantId, string policyCode, Guid? propertyId, CancellationToken cancellationToken);

    Task SetAsync(Guid tenantId, string policyCode, Guid? propertyId, PolicyValueResolution resolution, CancellationToken cancellationToken);
}

/// <summary>
/// Invalidates every cached resolution for a given (tenantId, policyCode) —
/// deliberately coarser than a single scope: a Tenant-level change can affect
/// the effective resolution of every Property that has no Property-level
/// override of its own, and those Property-keyed cache entries cannot be
/// enumerated to invalidate individually. Called only by the <c>PolicyUpdated</c>
/// consumer, only after that event's own commit (§6: "invalidação imediata
/// depois de commit bem-sucedido... nenhuma invalidação antes do commit").
/// Public (unlike <see cref="IPolicyValueCache"/>) because its one real
/// consumer, <c>PolicyUpdatedCacheInvalidation</c>, is DI-constructed
/// from <c>IHostPro.Worker</c>, a different assembly.
/// </summary>
public interface IPolicyCacheInvalidator
{
    Task InvalidateAsync(Guid tenantId, string policyCode, CancellationToken cancellationToken);
}
