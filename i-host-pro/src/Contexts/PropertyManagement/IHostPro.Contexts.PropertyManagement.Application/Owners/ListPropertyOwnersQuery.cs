using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// Lists the owners linked to a single property, paginated (Checkpoint 5
/// plan, item 9). <see cref="Page"/>/<see cref="PageSize"/> are nullable —
/// <c>null</c> means "use the default" (page 1, size 20; max 100).
/// </summary>
public sealed record ListPropertyOwnersQuery(Guid PropertyId, int? Page, int? PageSize) : IQuery<PagedResult<PropertyOwnerResult>>;
