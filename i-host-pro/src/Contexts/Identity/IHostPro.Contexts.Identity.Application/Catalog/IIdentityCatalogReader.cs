namespace IHostPro.Contexts.Identity.Application.Catalog;

/// <summary>
/// Reads the platform's fixed Role/Permission catalog for administrative
/// listing (Incremento 3, Checkpoint 3) — <c>GET /api/v1/roles</c> and
/// <c>GET /api/v1/permissions</c>. Framework-neutral — no ASP.NET Core, EF
/// Core or Infrastructure type appears in this contract.
///
/// Deliberately distinct from <c>IPermissionReader</c>
/// (<c>Identity.Application.Authorization</c>): that abstraction resolves
/// which permission codes a set of role codes grants, for the authorization
/// engine, and may cache its answer (Checkpoint 2). This one lists the whole
/// catalog for an administrative client and always reflects the persisted
/// state of PostgreSQL directly — no caching (Incremento 3, Checkpoint 3,
/// approved design).
/// </summary>
public interface IIdentityCatalogReader
{
    /// <summary>
    /// Every role in the catalog, ordered by <see cref="CatalogRole.Code"/>
    /// using ordinal comparison. A role with no granted permission still
    /// appears, with an empty <see cref="CatalogRole.PermissionCodes"/> —
    /// never omitted.
    /// </summary>
    Task<IReadOnlyCollection<CatalogRole>> ListRolesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every permission in the catalog, ordered by
    /// <see cref="CatalogPermission.Code"/> using ordinal comparison.
    /// </summary>
    Task<IReadOnlyCollection<CatalogPermission>> ListPermissionsAsync(CancellationToken cancellationToken);
}
