using IHostPro.Contexts.Configuration.Infrastructure.Caching;
using IHostPro.Contexts.Configuration.Infrastructure.Resolution;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Resolution;

internal sealed class FakePolicyValueCache : IPolicyValueCache
{
    private readonly PolicyValueResolution? _cachedValue;

    public int GetCallCount { get; private set; }

    public List<PolicyValueResolution> SetValues { get; } = [];

    private FakePolicyValueCache(PolicyValueResolution? cachedValue) => _cachedValue = cachedValue;

    /// <summary>A cache miss: <c>TryGetAsync</c> returns <c>null</c>.</summary>
    public static FakePolicyValueCache Empty() => new(null);

    public static FakePolicyValueCache WithHit(PolicyValueResolution cachedValue) => new(cachedValue);

    public Task<PolicyValueResolution?> TryGetAsync(Guid tenantId, string policyCode, Guid? propertyId, CancellationToken cancellationToken)
    {
        GetCallCount++;
        return Task.FromResult(_cachedValue);
    }

    public Task SetAsync(Guid tenantId, string policyCode, Guid? propertyId, PolicyValueResolution resolution, CancellationToken cancellationToken)
    {
        SetValues.Add(resolution);
        return Task.CompletedTask;
    }
}
