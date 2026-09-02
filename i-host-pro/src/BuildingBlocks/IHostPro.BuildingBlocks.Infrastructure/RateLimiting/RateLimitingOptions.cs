namespace IHostPro.BuildingBlocks.Infrastructure.RateLimiting;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting) — externally
/// configured, centralized rate limiting, backed by Redis (ADR-006 already
/// names Redis as the intended store for this). Bound from
/// <see cref="SectionName"/> and shared by both <c>IHostPro.Api</c> (HTTP
/// policies) and <c>IHostPro.Worker</c> (the AI cost-guard policy, applied at
/// the real Wolverine orchestration boundary — never an HTTP endpoint).
///
/// No policy in <see cref="Policies"/> ships with a production-grade
/// threshold — every default here is a conservative dev/homologation value.
/// Final production numbers depend on real pilot data and are explicitly
/// NOT decided by this checkpoint (registered as
/// <c>ProductionRateLimitThresholdsRequired=true</c> in the CP3 homologation
/// document).
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    public RateLimitingRedisOptions Redis { get; set; } = new();

    /// <summary>Keyed by policy name (e.g. <c>"Authentication"</c>, <c>"Webhook"</c>, <c>"TenantApi"</c>, <c>"AdminApi"</c>, <c>"AiExpensiveOperation"</c>) — never hardcoded per call site.</summary>
    public Dictionary<string, RateLimitPolicyOptions> Policies { get; set; } = new();
}

/// <summary>Mirrors <c>PolicyCacheOptions</c>'s own connection/timeout shape exactly — a separate physical connection/options section, never sharing Configuration &amp; Policy's own <c>IConnectionMultiplexer</c> registration, since the two are independent concerns with independent lifecycles.</summary>
public sealed class RateLimitingRedisOptions
{
    /// <summary>StackExchange.Redis connection string (e.g. <c>"localhost:6379"</c>) — never a URI.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Applied to both StackExchange.Redis's SyncTimeout and AsyncTimeout.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public int ConnectRetry { get; set; } = 1;
}

/// <summary>One named policy's fixed-window shape. <see cref="PermitLimit"/> requests are allowed per <see cref="Window"/>, per partition (see each call site for its own partition key — never a global counter shared across tenants/IPs/accounts).</summary>
public sealed class RateLimitPolicyOptions
{
    public int PermitLimit { get; set; } = 100;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    public RateLimitFailureMode FailureMode { get; set; } = RateLimitFailureMode.FailOpen;
}
