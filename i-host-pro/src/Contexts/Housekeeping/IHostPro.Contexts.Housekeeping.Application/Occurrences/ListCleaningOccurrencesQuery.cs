using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

/// <summary>
/// Self-service occurrence listing for a single own Cleaning (Fase 6,
/// Incremento 2A) — <see cref="HousekeeperUserId"/> always comes from the
/// caller's own authenticated identity.
/// </summary>
public sealed record ListCleaningOccurrencesQuery(
    Guid CleaningId, Guid HousekeeperUserId) : IQuery<IReadOnlyList<CleaningOccurrenceResult>>;
