using System.Text.Json;
using System.Text.Json.Serialization;
using IHostPro.Contexts.Configuration.Infrastructure.Resolution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.Contexts.Configuration.Infrastructure.Caching;

/// <inheritdoc cref="IPolicyValueCache"/>
/// <remarks>
/// Key format: <c>ihostpro:{tenantId:N}:policy-cache:{policyCode}:{generation}:{propertyId:N|"_"}</c>.
/// <c>generation</c> is a separate, per-(tenantId, policyCode) counter key
/// (<c>...:{policyCode}:gen</c>) — <see cref="InvalidateAsync"/> is a single
/// <c>INCR</c> on that counter, never a key-by-key delete/SCAN: every
/// subsequently-computed cache key for that (tenant, policyCode) pair
/// automatically becomes a fresh miss, while entries from the previous
/// generation are simply never addressed again and expire on their own TTL.
/// This gives immediate, whole-namespace invalidation (§6: "invalidação
/// imediata depois de commit bem-sucedido") without needing to enumerate
/// every Property that might inherit a Tenant-level value.
///
/// <see cref="TryGetAsync"/>/<see cref="SetAsync"/> cache <see cref="PolicyValueResolution"/>
/// verbatim, including <c>Found = false</c> — a cache HIT with
/// <c>Found = false</c> is "cached NotConfigured" (§6: "diferenciar cache de
/// Resolved e NotConfigured"), distinct from a cache MISS (<c>null</c>).
///
/// Every Redis operation is wrapped in a broad-but-deliberate catch, exactly
/// like <c>RedisSessionRevocationCache</c> — but the DEGRADATION TARGET
/// differs on purpose: session revocation degrades to a business-safe
/// default ("not revoked"); a policy value has no safe default to guess
/// (official decision 4 explicitly forbids an optimistic/hardcoded value), so
/// this cache degrades instead to "nothing cached" — the caller
/// (<see cref="CachedPolicyValueResolver"/>) then falls through to
/// PostgreSQL, which remains authoritative and can still answer correctly
/// even while Redis is down. <see cref="InvalidateAsync"/> is the one
/// exception: it does NOT swallow failures, since a failed invalidation
/// could otherwise leave a stale value cached with no bound other than TTL —
/// letting the exception propagate lets Wolverine's own message-level
/// retry/circuit-breaker handle it, with the configured TTL still providing
/// a hard ceiling on staleness regardless.
///
/// Checkpoint 7 homologação (Fase 5), real defect found and fixed: this class
/// used to be <c>internal</c>, matching <see cref="IPolicyValueCache"/>'s own
/// accessibility. That was invisible to every automated test (each one
/// resolves <see cref="IPolicyCacheInvalidator"/>/<see cref="IPolicyValueCache"/>
/// through normal DI in a host built within this same assembly's test
/// project) but broke the one real consumer that matters: Wolverine's code
/// generator, building the handler chain for a real
/// <c>PolicyUpdatedCacheInvalidation</c> in <c>IHostPro.Worker</c>
/// (a different assembly), refuses to inline-construct a non-public concrete
/// type and throws <c>Wolverine.Configuration.InvalidServiceLocationException</c>
/// — confirmed by direct observation, the first time this dependency chain
/// was ever exercised end-to-end. The class itself is now public — its two
/// implemented interfaces already carry the real, intended accessibility
/// boundary (<see cref="IPolicyValueCache"/> stays internal;
/// <see cref="IPolicyCacheInvalidator"/> stays public) — and
/// <see cref="IPolicyValueCache"/>'s two members are implemented explicitly
/// below so neither becomes part of this public class's own public surface
/// (which would otherwise force <see cref="PolicyValueResolution"/>, an
/// internal type, into a public method signature — CS0051).
/// </remarks>
public sealed class RedisPolicyValueCache : IPolicyValueCache, IPolicyCacheInvalidator
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly IOptions<PolicyCacheOptions> _options;
    private readonly ILogger<RedisPolicyValueCache> _logger;

    public RedisPolicyValueCache(
        IConnectionMultiplexer connectionMultiplexer, IOptions<PolicyCacheOptions> options, ILogger<RedisPolicyValueCache> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _options = options;
        _logger = logger;
    }

    async Task<PolicyValueResolution?> IPolicyValueCache.TryGetAsync(
        Guid tenantId, string policyCode, Guid? propertyId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            var generation = await CurrentGenerationAsync(database, tenantId, policyCode);
            var key = BuildValueKey(tenantId, policyCode, generation, propertyId);

            var raw = await database.StringGetAsync(key);
            if (raw.IsNullOrEmpty)
                return null;

            return JsonSerializer.Deserialize<PolicyValueResolution>((string)raw!, SerializerOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to read the policy cache for tenant {TenantId}, policy {PolicyCode} — falling back to PostgreSQL (fail-closed: never a fabricated value).",
                tenantId, policyCode);
            return null;
        }
    }

    async Task IPolicyValueCache.SetAsync(
        Guid tenantId, string policyCode, Guid? propertyId, PolicyValueResolution resolution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            var generation = await CurrentGenerationAsync(database, tenantId, policyCode);
            var key = BuildValueKey(tenantId, policyCode, generation, propertyId);
            var payload = JsonSerializer.Serialize(resolution, SerializerOptions);

            await database.StringSetAsync(key, payload, _options.Value.TimeToLive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to write the policy cache for tenant {TenantId}, policy {PolicyCode} — the resolved value is still returned to the caller, just not cached.",
                tenantId, policyCode);
        }
    }

    public async Task InvalidateAsync(Guid tenantId, string policyCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = _connectionMultiplexer.GetDatabase();
        await database.StringIncrementAsync(BuildGenerationKey(tenantId, policyCode));
    }

    private static async Task<long> CurrentGenerationAsync(IDatabase database, Guid tenantId, string policyCode)
    {
        var value = await database.StringGetAsync(BuildGenerationKey(tenantId, policyCode));
        return value.IsNullOrEmpty ? 0 : (long)value;
    }

    private static RedisKey BuildGenerationKey(Guid tenantId, string policyCode) =>
        $"ihostpro:{tenantId:N}:policy-cache:{policyCode}:gen";

    private static RedisKey BuildValueKey(Guid tenantId, string policyCode, long generation, Guid? propertyId) =>
        $"ihostpro:{tenantId:N}:policy-cache:{policyCode}:{generation}:{propertyId?.ToString("N") ?? "_"}";
}
