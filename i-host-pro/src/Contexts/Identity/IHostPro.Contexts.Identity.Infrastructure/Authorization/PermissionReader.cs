using IHostPro.Contexts.Identity.Application.Authorization;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Authorization;

/// <inheritdoc cref="IPermissionReader"/>
/// <remarks>
/// Reads exclusively from the persisted catalog (<c>roles</c> joined through
/// <c>role_permissions</c> to <c>permissions</c>) — never from
/// <c>IdentityCatalogSeed</c>'s in-memory list, which exists only to produce
/// the migration's seed data, not to be queried at runtime (Incremento 3,
/// Checkpoint 2, approved design). None of the three tables is tenant-owned
/// (Architecture Principles §7: catalog tables never receive RLS), so this
/// never opens a tenant-aware transaction and never touches
/// <c>ITenantContext</c> — a plain <c>AsNoTracking</c> read against the
/// already-request-scoped <see cref="IdentityDbContext"/>.
///
/// <see cref="RolePermissionCache"/> is consulted per role first; only roles
/// missing or expired in the cache reach PostgreSQL, in a single query
/// covering every such role. A role absent from the catalog, or with zero
/// granted permissions, is cached as an empty set — otherwise every request
/// for that role would re-query PostgreSQL, defeating the cache for exactly
/// the roles least likely to change. Any exception from the database call is
/// never caught here: it propagates to the caller unmodified, so a transient
/// PostgreSQL failure can never be mistaken for "this role has no
/// permissions" (Incremento 3, Checkpoint 2, approved design).
/// </remarks>
public sealed class PermissionReader : IPermissionReader
{
    private readonly IdentityDbContext _dbContext;
    private readonly RolePermissionCache _cache;

    public PermissionReader(IdentityDbContext dbContext, RolePermissionCache cache)
    {
        _dbContext = dbContext;
        _cache = cache;
    }

    public async Task<IReadOnlyCollection<string>> GetPermissionCodesAsync(
        IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        var distinctRoleCodes = (roleCodes ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctRoleCodes.Length == 0)
            return [];

        var result = new HashSet<string>(StringComparer.Ordinal);
        var uncachedRoleCodes = new List<string>();

        foreach (var roleCode in distinctRoleCodes)
        {
            if (_cache.TryGet(roleCode, out var cachedPermissionCodes))
                result.UnionWith(cachedPermissionCodes);
            else
                uncachedRoleCodes.Add(roleCode);
        }

        if (uncachedRoleCodes.Count > 0)
        {
            var loadedByRole = await LoadFromDatabaseGroupedByRoleAsync(uncachedRoleCodes, cancellationToken);

            foreach (var roleCode in uncachedRoleCodes)
            {
                var permissionCodes = loadedByRole.TryGetValue(roleCode, out var codes)
                    ? codes
                    : Array.Empty<string>();

                _cache.Set(roleCode, permissionCodes);
                result.UnionWith(permissionCodes);
            }
        }

        return result.OrderBy(code => code, StringComparer.Ordinal).ToArray();
    }

    private async Task<Dictionary<string, string[]>> LoadFromDatabaseGroupedByRoleAsync(
        IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        var pairs = await _dbContext.Roles.AsNoTracking()
            .Where(role => roleCodes.Contains(role.Id))
            .Join(_dbContext.RolePermissions.AsNoTracking(), role => role.Id, rp => rp.RoleCode, (role, rp) => rp)
            .Join(_dbContext.Permissions.AsNoTracking(), rp => rp.PermissionCode, permission => permission.Id,
                (rp, permission) => new { rp.RoleCode, PermissionCode = permission.Id })
            .ToArrayAsync(cancellationToken);

        return pairs
            .GroupBy(pair => pair.RoleCode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.PermissionCode).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }
}
