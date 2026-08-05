using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Lists reservations of the caller's tenant, paginated, with optional
/// filters (Fase 3, Incremento 1 plan, item 9) — mirrors
/// <c>ListPropertiesQuery</c>. All filter parameters are optional;
/// <see cref="Status"/> is the stable lowercase code (<c>"confirmed"</c>/
/// <c>"cancelled"</c>), never the raw enum member name.
/// <see cref="From"/>/<see cref="To"/> return reservations whose interval
/// intersects the supplied window — never a separate calendar endpoint this
/// increment.
/// </summary>
public sealed record ListReservationsQuery(
    Guid? PropertyId,
    string? Status,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? Page,
    int? PageSize) : IQuery<PagedResult<ReservationSummaryResult>>;
