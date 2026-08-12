using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Self-service "Minhas Faxinas" listing (Fase 6, Incremento 2A) —
/// <see cref="HousekeeperUserId"/> always comes from the caller's own
/// authenticated identity (never client-supplied), enforcing the ABAC rule
/// that a housekeeper only ever sees cleanings assigned to themselves, on
/// top of the tenant scoping the Global Query Filter already applies. No
/// <c>PropertyId</c>/<c>AssignedHousekeeperUserId</c> filter exists here —
/// unlike <see cref="ListCleaningsQuery"/>, the caller cannot choose whose
/// cleanings to see.
/// </summary>
public sealed record ListOwnCleaningsQuery(
    Guid HousekeeperUserId,
    string? Status,
    int? Page,
    int? PageSize) : IQuery<PagedResult<CleaningSummaryResult>>;
