using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Self-service detail read (Fase 6, Incremento 2A) — <see cref="HousekeeperUserId"/>
/// always comes from the caller's own authenticated identity. A cleaning
/// that exists but is not assigned to the caller is indistinguishable from
/// one that does not exist at all (§4 ABAC rule — fail closed without
/// revealing existence).
/// </summary>
public sealed record GetOwnCleaningDetailQuery(Guid CleaningId, Guid HousekeeperUserId) : IQuery<CleaningResult>;
