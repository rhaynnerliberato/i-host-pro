using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Lists properties of the caller's tenant, paginated (Checkpoint 3 plan,
/// item 9) — mirrors <c>ListCondominiumsQuery</c>. <see cref="Page"/>/
/// <see cref="PageSize"/> are nullable — <c>null</c> means "use the default"
/// (page 1, size 20; max 100). No tenant parameter: resolved from the JWT
/// claim via <c>TenantTransactionBehavior</c>. No search/filter yet
/// (Checkpoint 3 plan, item 9).
/// </summary>
public sealed record ListPropertiesQuery(int? Page, int? PageSize) : IQuery<PagedResult<PropertySummaryResult>>;
