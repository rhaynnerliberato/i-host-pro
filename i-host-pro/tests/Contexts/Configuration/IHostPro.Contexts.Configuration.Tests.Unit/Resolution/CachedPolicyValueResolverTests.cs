using FluentAssertions;
using IHostPro.Contexts.Configuration.Infrastructure.Resolution;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Resolution;

public class CachedPolicyValueResolverTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task A_cache_hit_short_circuits_and_never_calls_the_inner_resolver()
    {
        var cachedResolution = new PolicyValueResolution(true, """{"allowed":true}""", ResolvedScopeKind.Tenant, 1);
        var cache = FakePolicyValueCache.WithHit(cachedResolution);
        var inner = FakePolicyValueResolver.Returning(new PolicyValueResolution(false, null, null, null));
        var resolver = new CachedPolicyValueResolver(inner, cache);

        var result = await resolver.ResolveAsync(TenantId, "EARLY_CHECKIN", null, CancellationToken.None);

        result.Should().Be(cachedResolution);
        inner.CallCount.Should().Be(0, "a cache hit must never fall through to PostgreSQL");
    }

    [Fact]
    public async Task A_cache_miss_falls_through_to_the_inner_resolver_and_populates_the_cache()
    {
        var resolved = new PolicyValueResolution(true, """{"allowed":true}""", ResolvedScopeKind.Property, 3);
        var cache = FakePolicyValueCache.Empty();
        var inner = FakePolicyValueResolver.Returning(resolved);
        var resolver = new CachedPolicyValueResolver(inner, cache);

        var result = await resolver.ResolveAsync(TenantId, "LATE_CHECKOUT", Guid.NewGuid(), CancellationToken.None);

        result.Should().Be(resolved);
        inner.CallCount.Should().Be(1);
        cache.SetValues.Should().ContainSingle().Which.Should().Be(resolved);
    }

    [Fact]
    public async Task NotConfigured_is_cached_and_returned_just_like_a_Resolved_value()
    {
        var notConfigured = new PolicyValueResolution(false, null, null, null);
        var cache = FakePolicyValueCache.Empty();
        var inner = FakePolicyValueResolver.Returning(notConfigured);
        var resolver = new CachedPolicyValueResolver(inner, cache);

        var result = await resolver.ResolveAsync(TenantId, "EARLY_CHECKIN", null, CancellationToken.None);

        result.Found.Should().BeFalse();
        cache.SetValues.Should().ContainSingle().Which.Found.Should().BeFalse();
    }

    // A cache-layer failure (Redis unreachable, timeout, etc.) is not tested
    // here: IPolicyValueCache's own contract requires every implementation to
    // never throw (see its doc comment) — a failure must already have
    // degraded to "nothing cached" (TryGetAsync returning null) before this
    // decorator ever sees it. That degradation is RedisPolicyValueCache's own
    // responsibility, verified against a real, genuinely unreachable Redis in
    // the integration suite (RedisPolicyValueCacheTests) — a fake that
    // violates the contract by throwing would only prove this decorator
    // tolerates a buggy IPolicyValueCache implementation, not a real
    // property of the system. From this decorator's own point of view, a
    // degraded cache and a genuine miss are indistinguishable — both are
    // already covered by "A_cache_miss_falls_through_to_the_inner_resolver_and_populates_the_cache".

    [Fact]
    public async Task A_database_failure_in_the_inner_resolver_still_propagates_unchanged()
    {
        var dbFailure = new InvalidOperationException("PostgreSQL connection lost");
        var cache = FakePolicyValueCache.Empty();
        var inner = FakePolicyValueResolver.Throwing(dbFailure);
        var resolver = new CachedPolicyValueResolver(inner, cache);

        var act = () => resolver.ResolveAsync(TenantId, "EARLY_CHECKIN", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(dbFailure.Message, "this decorator only ever adds a faster path — a real database failure must still surface exactly as before Checkpoint 6");
    }
}
