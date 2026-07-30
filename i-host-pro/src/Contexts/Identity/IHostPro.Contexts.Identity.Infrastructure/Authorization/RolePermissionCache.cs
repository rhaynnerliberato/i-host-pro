using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Authorization;

/// <summary>
/// In-memory cache of a single canonical role code's granted permission
/// codes (Incremento 3, Checkpoint 2) — never a cache keyed by a combination
/// of roles, which would grow unbounded as the number of distinct role sets
/// in use grows; the platform's role catalog itself is small and fixed
/// (Documento 09 §4: seven roles), so caching per role bounds the cache size
/// by that same small, fixed number.
///
/// Registered as a Singleton so the cache is actually shared across
/// requests — <see cref="PermissionReader"/> (Scoped, one instance per
/// request) is the only caller and injects this same singleton instance.
///
/// Only ever populated by <see cref="Set"/> with a result <see cref="PermissionReader"/>
/// already successfully read from PostgreSQL — this type has no knowledge of
/// where a value came from and cannot itself distinguish "the database says
/// this role has no permissions" from a transient failure; it is the
/// caller's responsibility to call <see cref="Set"/> only after a successful
/// read (never inside a catch block, never with a default/fallback value).
/// </summary>
public sealed class RolePermissionCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly PermissionCacheOptions _options;

    public RolePermissionCache(TimeProvider timeProvider, IOptions<PermissionCacheOptions> options)
    {
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    /// <summary>
    /// True only when <paramref name="roleCode"/> has a cached entry that has
    /// not yet reached its expiry — an expired entry is treated identically
    /// to a missing one (the stale value is never returned), forcing the
    /// caller back to <see cref="PermissionReader"/>'s real PostgreSQL read.
    /// </summary>
    public bool TryGet(string roleCode, out IReadOnlyCollection<string> permissionCodes)
    {
        if (_entries.TryGetValue(roleCode, out var entry) && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            permissionCodes = entry.PermissionCodes;
            return true;
        }

        permissionCodes = [];
        return false;
    }

    public void Set(string roleCode, IReadOnlyCollection<string> permissionCodes) =>
        _entries[roleCode] = new Entry(permissionCodes, _timeProvider.GetUtcNow().Add(_options.Lifetime));

    private readonly record struct Entry(IReadOnlyCollection<string> PermissionCodes, DateTimeOffset ExpiresAt);
}
