using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IHostPro.Contexts.Identity.Infrastructure.Caching;

/// <inheritdoc cref="ISessionRevocationCache"/>
/// <remarks>
/// Key format: <c>ihostpro:{tenantId:N}:session-revoked:{sessionId:N}</c>.
/// The stored value is a fixed, non-sensitive presence marker — no user
/// data, token, hash, or timestamp derived from anything secret; a future
/// JWT Bearer validator only needs to know the key EXISTS, never a value.
///
/// TTL is <see cref="JwtOptions.AccessTokenLifetime"/> + <see cref="JwtOptions.ClockSkew"/>
/// — the maximum time an already-issued access token for the revoked
/// session could still pass signature/expiry validation, so the cache entry
/// outlives every JWT that could still reference that session.
///
/// Every Redis operation is wrapped in a broad-but-deliberate catch: this
/// cache is documented (<see cref="ISessionRevocationCache"/>) to NEVER
/// throw for a genuine cache failure — failing to write/read this
/// best-effort optimization must never fail Logout, undo a PostgreSQL
/// revocation that already committed, or reject an otherwise-valid request
/// (Incremento 2 plan, Etapa 12/13). The one exception is
/// <see cref="OperationCanceledException"/>: that means the CALLER's
/// request was cancelled, not that Redis failed, so it is deliberately
/// excluded from the catch and left to propagate normally. Only structured
/// telemetry is recorded on an actual cache failure.
/// </remarks>
public sealed class RedisSessionRevocationCache : ISessionRevocationCache
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly IOptions<JwtOptions> _jwtOptions;
    private readonly ILogger<RedisSessionRevocationCache> _logger;

    public RedisSessionRevocationCache(
        IConnectionMultiplexer connectionMultiplexer, IOptions<JwtOptions> jwtOptions, ILogger<RedisSessionRevocationCache> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _jwtOptions = jwtOptions;
        _logger = logger;
    }

    public async Task MarkRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            var key = BuildKey(tenantId, sessionId);
            var ttl = _jwtOptions.Value.AccessTokenLifetime + _jwtOptions.Value.ClockSkew;

            await database.StringSetAsync(key, RevokedMarker, ttl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to write the session revocation cache entry for tenant {TenantId} — PostgreSQL remains the source of truth, Logout is not affected.",
                tenantId);
        }
    }

    public async Task<bool> IsRevokedAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            var key = BuildKey(tenantId, sessionId);

            return await database.KeyExistsAsync(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to check the session revocation cache for tenant {TenantId} — treating as not revoked (fail-open); token cryptographic validity remains authoritative.",
                tenantId);
            return false;
        }
    }

    private const string RevokedMarker = "1";

    internal static string BuildKey(Guid tenantId, Guid sessionId) =>
        $"ihostpro:{tenantId:N}:session-revoked:{sessionId:N}";
}
