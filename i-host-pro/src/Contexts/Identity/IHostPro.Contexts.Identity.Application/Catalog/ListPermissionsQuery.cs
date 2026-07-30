using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Identity.Application.Catalog;

/// <summary>
/// Lists the platform's fixed permission catalog (Incremento 3, Checkpoint 3;
/// <c>GET /api/v1/permissions</c>). Parameterless — there is no filter,
/// search or pagination for this small, fixed catalog. Read-only by
/// construction (<see cref="IQuery{TResponse}"/> implies
/// <c>IReadOnlyRequest</c>), so the tenant-aware pipeline behavior opens a
/// <c>READ ONLY</c> transaction for it automatically.
/// </summary>
public sealed record ListPermissionsQuery : IQuery<IReadOnlyCollection<CatalogPermission>>;
