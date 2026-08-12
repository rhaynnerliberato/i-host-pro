using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

/// <summary>
/// Self-service checklist read for a single own Cleaning (Fase 6, Incremento
/// 2A) — <see cref="HousekeeperUserId"/> always comes from the caller's own
/// authenticated identity.
/// </summary>
public sealed record GetOwnCleaningChecklistQuery(
    Guid CleaningId, Guid HousekeeperUserId) : IQuery<IReadOnlyList<CleaningChecklistItemResult>>;
