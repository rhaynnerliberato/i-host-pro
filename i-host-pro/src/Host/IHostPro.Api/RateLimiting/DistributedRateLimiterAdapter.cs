using System.Threading.RateLimiting;
using IHostPro.BuildingBlocks.Infrastructure.RateLimiting;

namespace IHostPro.Api.RateLimiting;

/// <summary>
/// Fase 12, Checkpoint 3 — the small, deterministic adapter the Decision Gate
/// allowed ("se um custom RateLimiter for necessário: manter implementação
/// pequena, determinística, testável"): wraps <see cref="IDistributedRateLimiter"/>
/// (Redis-backed, shared with <c>IHostPro.Worker</c>'s AI cost-guard policy)
/// behind the abstract <see cref="RateLimiter"/> base type
/// <c>Microsoft.AspNetCore.RateLimiting</c>'s middleware expects — never a
/// second limiting algorithm, purely a bridge. One instance per (policy,
/// partition key) pair, created on demand by <see cref="RateLimitPartition.Get{TKey}"/>'s
/// factory.
/// </summary>
internal sealed class DistributedRateLimiterAdapter : RateLimiter
{
    private readonly IDistributedRateLimiter _limiter;
    private readonly string _policyName;
    private readonly string _partitionKey;

    public DistributedRateLimiterAdapter(IDistributedRateLimiter limiter, string policyName, string partitionKey)
    {
        _limiter = limiter;
        _policyName = policyName;
        _partitionKey = partitionKey;
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
        ToLease(_limiter.CheckAsync(_policyName, _partitionKey, CancellationToken.None).GetAwaiter().GetResult());

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        ToLease(await _limiter.CheckAsync(_policyName, _partitionKey, cancellationToken));

    private static RateLimitLease ToLease(RateLimitDecision decision) =>
        decision.Allowed ? AllowedLease.Instance : new DeniedLease(decision.RetryAfter);

    private sealed class AllowedLease : RateLimitLease
    {
        public static readonly AllowedLease Instance = new();
        public override bool IsAcquired => true;
        public override IEnumerable<string> MetadataNames => [];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class DeniedLease : RateLimitLease
    {
        private readonly TimeSpan? _retryAfter;
        public DeniedLease(TimeSpan? retryAfter) => _retryAfter = retryAfter;
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames => _retryAfter is null ? [] : [MetadataName.RetryAfter.Name];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_retryAfter is { } retryAfter && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }
            metadata = null;
            return false;
        }
    }
}
