using IHostPro.Contexts.Identity.Application;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace IHostPro.Contexts.Identity.Infrastructure.Caching;

/// <inheritdoc cref="ITenantAccessStateCache"/>
/// <remarks>
/// Key format: <c>ihostpro:{tenantId:N}:tenant-access-state</c>, a Redis hash
/// with two fields: <c>status</c> (<c>"Suspended"</c>/<c>"Active"</c>) and
/// <c>validAfterUtc</c> (Unix seconds — the reactivation cutover instant).
/// Deliberately has NO TTL: unlike a session-revocation marker (which only
/// needs to outlive the access token it protects against —
/// <see cref="RedisSessionRevocationCache"/>), a suspension must remain
/// enforced indefinitely, until an explicit reactivation, and a reactivation's
/// cutover instant must remain remembered forever after (an access token can
/// be refreshed indefinitely, so "old enough to predate reactivation" can
/// never be allowed to expire out of this cache on its own).
///
/// No entry for a tenant means "never suspended" — allowed by construction,
/// so a tenant that has never been suspended incurs no write at provisioning
/// time.
///
/// Fails CLOSED on any Redis error (unlike <see cref="RedisSessionRevocationCache"/>'s
/// fail-open): this cache is the only enforcement point for an already-issued
/// credential (see <see cref="ITenantAccessStateCache"/>'s own remarks), so a
/// failure to determine a tenant's access state must deny, mirroring the
/// "Authentication" rate-limit policy's own FailClosed degradation
/// (<c>RedisFixedWindowRateLimiter</c>) rather than
/// <see cref="RedisSessionRevocationCache"/>'s fail-open one. The one
/// exception is <see cref="OperationCanceledException"/>: that means the
/// CALLER's request was cancelled, not that Redis failed, so it is
/// deliberately excluded from the catch and left to propagate normally.
/// </remarks>
public sealed class RedisTenantAccessStateCache : ITenantAccessStateCache
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ILogger<RedisTenantAccessStateCache> _logger;

    public RedisTenantAccessStateCache(IConnectionMultiplexer connectionMultiplexer, ILogger<RedisTenantAccessStateCache> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _logger = logger;
    }

    public async Task MarkSuspendedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = _connectionMultiplexer.GetDatabase();
        await database.HashSetAsync(BuildKey(tenantId), [new HashEntry(StatusField, SuspendedStatus)]);
    }

    public async Task MarkReactivatedAsync(Guid tenantId, DateTimeOffset reactivatedAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = _connectionMultiplexer.GetDatabase();
        await database.HashSetAsync(BuildKey(tenantId),
        [
            new HashEntry(StatusField, ActiveStatus),
            new HashEntry(ValidAfterField, reactivatedAtUtc.ToUnixTimeSeconds()),
        ]);
    }

    public async Task<bool> IsAccessAllowedAsync(Guid tenantId, DateTimeOffset credentialIssuedAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var database = _connectionMultiplexer.GetDatabase();
            var entries = await database.HashGetAllAsync(BuildKey(tenantId));
            if (entries.Length == 0)
                return true; // Never suspended — nothing to enforce.

            var status = entries.FirstOrDefault(e => e.Name == StatusField).Value;
            if (status == SuspendedStatus)
                return false;

            var validAfter = entries.FirstOrDefault(e => e.Name == ValidAfterField).Value;
            if (validAfter.IsNullOrEmpty)
                return true; // Active, never reactivated after a suspension — no cutover to enforce.

            var validAfterUtc = DateTimeOffset.FromUnixTimeSeconds((long)validAfter);
            return credentialIssuedAtUtc >= validAfterUtc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to check the tenant access-state cache for tenant {TenantId} — denying access (fail-closed); this is the only enforcement point for an already-issued credential.",
                tenantId);
            return false;
        }
    }

    private const string StatusField = "status";
    private const string ValidAfterField = "validAfterUtc";
    private const string SuspendedStatus = "Suspended";
    private const string ActiveStatus = "Active";

    internal static string BuildKey(Guid tenantId) => $"ihostpro:{tenantId:N}:tenant-access-state";
}
