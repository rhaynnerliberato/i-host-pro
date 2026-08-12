using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Lists cleanings of the caller's tenant, paginated, with optional filters
/// (Fase 6, Incremento 1) — mirrors <c>ListReservationsQuery</c>. All filter
/// parameters are optional; <see cref="Status"/> is one of
/// <c>CleaningStatusCodeMapper</c>'s stable codes.
/// </summary>
public sealed record ListCleaningsQuery(
    string? Status,
    Guid? PropertyId,
    Guid? AssignedHousekeeperUserId,
    int? Page,
    int? PageSize) : IQuery<PagedResult<CleaningSummaryResult>>;
