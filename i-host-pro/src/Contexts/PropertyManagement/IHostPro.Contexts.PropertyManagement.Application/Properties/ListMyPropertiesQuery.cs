using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Lists the authenticated owner's own properties, paginated (Checkpoint 5
/// plan, item 10). <see cref="OwnerUserId"/> comes exclusively from the
/// validated JWT claims — never accepted from query, route or body.
/// <see cref="Page"/>/<see cref="PageSize"/> are nullable — <c>null</c> means
/// "use the default" (page 1, size 20; max 100). Includes every status.
/// </summary>
public sealed record ListMyPropertiesQuery(Guid OwnerUserId, int? Page, int? PageSize) : IQuery<PagedResult<PropertySummaryResult>>;
