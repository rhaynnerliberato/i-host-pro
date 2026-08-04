using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Lists condominiums of the caller's tenant, paginated (Checkpoint 2 plan,
/// item 4). <see cref="Page"/>/<see cref="PageSize"/> are nullable —
/// <c>null</c> means "use the default" (page 1, size 20; max 100 — fixed by
/// this checkpoint's approved spec, not configurable). No tenant parameter:
/// resolved the normal way, from the JWT claim, via
/// <c>TenantTransactionBehavior</c>. No search/filter yet (Checkpoint 2 plan,
/// item 4: "não adicionar busca ou filtros ainda").
/// </summary>
public sealed record ListCondominiumsQuery(int? Page, int? PageSize) : IQuery<PagedResult<CondominiumSummaryResult>>;
