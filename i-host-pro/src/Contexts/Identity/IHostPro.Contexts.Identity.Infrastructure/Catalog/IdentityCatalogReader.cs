using IHostPro.Contexts.Identity.Application.Catalog;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Infrastructure.Catalog;

/// <inheritdoc cref="IIdentityCatalogReader"/>
/// <remarks>
/// Reads exclusively from the persisted catalog (<c>roles</c>,
/// <c>role_permissions</c>, <c>permissions</c>) via <see cref="IdentityDbContext"/>
/// — never from <c>IdentityCatalogSeed</c>'s in-memory list, which exists
/// only to produce the migration's seed data (Incremento 3, Checkpoint 3,
/// approved design). None of the three tables is tenant-owned (Architecture
/// Principles §7: catalog tables never receive RLS), so this never opens a
/// tenant-aware transaction of its own and never touches
/// <c>ITenantContext</c> directly — the tenant-aware <c>READ ONLY</c>
/// transaction each query already runs inside (via the closed
/// <c>TenantTransactionBehavior&lt;,&gt;</c> registration) is only there
/// because the request itself is authenticated and tenant-scoped, not
/// because these three tables need it.
///
/// Deliberately uncached, unlike <c>PermissionReader</c> (Checkpoint 2): each
/// call reads PostgreSQL directly, so an administrative listing always
/// reflects the current persisted state (Incremento 3, Checkpoint 3,
/// approved design — no cache introduced here).
///
/// Ordering is computed in memory with <see cref="StringComparer.Ordinal"/>
/// after materializing the (small, fixed-size) catalog, rather than trusting
/// PostgreSQL's default column collation to match .NET's ordinal semantics —
/// the two are not guaranteed identical for every possible code.
/// </remarks>
public sealed class IdentityCatalogReader : IIdentityCatalogReader
{
    private readonly IdentityDbContext _dbContext;

    public IdentityCatalogReader(IdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyCollection<CatalogRole>> ListRolesAsync(CancellationToken cancellationToken)
    {
        var roles = await _dbContext.Roles.AsNoTracking().ToListAsync(cancellationToken);
        var rolePermissions = await _dbContext.RolePermissions.AsNoTracking().ToListAsync(cancellationToken);

        var permissionCodesByRole = rolePermissions
            .GroupBy(rolePermission => rolePermission.RoleCode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyCollection<string>)group
                    .Select(rolePermission => rolePermission.PermissionCode)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        return roles
            .OrderBy(role => role.Id, StringComparer.Ordinal)
            .Select(role => new CatalogRole(
                role.Id,
                role.DisplayName,
                permissionCodesByRole.TryGetValue(role.Id, out var permissionCodes)
                    ? permissionCodes
                    : Array.Empty<string>()))
            .ToArray();
    }

    public async Task<IReadOnlyCollection<CatalogPermission>> ListPermissionsAsync(CancellationToken cancellationToken)
    {
        var permissions = await _dbContext.Permissions.AsNoTracking().ToListAsync(cancellationToken);

        return permissions
            .OrderBy(permission => permission.Id, StringComparer.Ordinal)
            .Select(permission => new CatalogPermission(
                permission.Id, permission.Resource, permission.Action, permission.Scope))
            .ToArray();
    }
}
