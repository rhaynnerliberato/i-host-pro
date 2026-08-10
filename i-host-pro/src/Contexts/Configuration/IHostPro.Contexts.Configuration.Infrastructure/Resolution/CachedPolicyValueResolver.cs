using IHostPro.Contexts.Configuration.Infrastructure.Caching;

namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <summary>
/// Decorates the real, DB-only <see cref="PolicyValueResolver"/> with a
/// look-aside cache (Fase 5, Incremento 1, Checkpoint 6, §6) — registered as
/// the public <see cref="IPolicyValueResolver"/> in DI, so both typed readers
/// (<c>EarlyCheckInPolicyReader</c>/<c>LateCheckoutPolicyReader</c>) get
/// caching automatically without any change to either. A cache miss (for any
/// reason, including a cache-layer failure — see <see cref="IPolicyValueCache"/>'s
/// own doc comment) always falls through to <paramref name="inner"/>; a
/// database failure there still propagates and still becomes
/// <c>PolicyEngineUnavailableException</c> exactly as before Checkpoint 6 —
/// this decorator changes nothing about that contract, it only ever adds a
/// faster path when the answer is already known.
///
/// <paramref name="inner"/> is typed as the interface (not the concrete
/// <see cref="PolicyValueResolver"/>) so this class can be unit-tested with a
/// fake — <c>ConfigurationModuleExtensions</c> resolves the real one via a
/// keyed DI registration, since both it and this decorator share the same
/// public <see cref="IPolicyValueResolver"/> contract.
/// </summary>
internal sealed class CachedPolicyValueResolver : IPolicyValueResolver
{
    private readonly IPolicyValueResolver _inner;
    private readonly IPolicyValueCache _cache;

    public CachedPolicyValueResolver(IPolicyValueResolver inner, IPolicyValueCache cache)
    {
        _inner = inner;
        _cache = cache;
    }

    public async Task<PolicyValueResolution> ResolveAsync(
        Guid tenantId, string policyCode, Guid? propertyId, CancellationToken cancellationToken)
    {
        var cached = await _cache.TryGetAsync(tenantId, policyCode, propertyId, cancellationToken);
        if (cached is not null)
            return cached;

        var resolution = await _inner.ResolveAsync(tenantId, policyCode, propertyId, cancellationToken);
        await _cache.SetAsync(tenantId, policyCode, propertyId, resolution, cancellationToken);

        return resolution;
    }
}
