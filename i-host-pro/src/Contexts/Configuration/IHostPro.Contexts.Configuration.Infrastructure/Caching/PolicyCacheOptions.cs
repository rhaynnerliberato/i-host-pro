namespace IHostPro.Contexts.Configuration.Infrastructure.Caching;

/// <summary>
/// Redis connection/behavior configuration for <see cref="RedisPolicyValueCache"/>
/// (Fase 5, Incremento 1, Checkpoint 6, §6: "timeout configurável"). Bound
/// and validated by <c>AddConfigurationPolicyCache</c>, called from both
/// <c>IHostPro.Api</c>'s composition root (the read path, via
/// <see cref="Resolution.CachedPolicyValueResolver"/>) and
/// <c>IHostPro.Worker</c>'s (the invalidation path, via the
/// <c>PolicyUpdated</c> consumer) — both must point at the same physical
/// Redis so an invalidation from Worker is actually visible to Api's reads.
///
/// <see cref="ConnectTimeout"/>/<see cref="OperationTimeout"/>/<see cref="ConnectRetry"/>
/// mirror <c>SessionRevocationCacheOptions</c>'s own rationale exactly
/// (StackExchange.Redis's own defaults would otherwise block far longer than
/// the 50ms p95 resolution goal, official decision 7, tolerates) — tightened
/// defaults here for the same reason, not a new one.
/// </summary>
public sealed class PolicyCacheOptions
{
    public const string SectionName = "Configuration:PolicyCache";

    /// <summary>StackExchange.Redis connection string (e.g. <c>"localhost:6379"</c>) — never a URI.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Applied to both StackExchange.Redis's SyncTimeout and AsyncTimeout — every operation this cache performs is async.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public int ConnectRetry { get; set; } = 1;

    /// <summary>How long a cached resolution (Resolved or NotConfigured alike) stays valid before a read must go back to PostgreSQL — an engineering knob, not a business rule (§6 requires it be configurable, not any specific value).</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromSeconds(30);
}
