using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Self-service <c>Assigned</c> → <c>InTransit</c> (Fase 6, Incremento 2A).
/// <see cref="ActorId"/> is always the caller's own authenticated user id —
/// also the housekeeper identity the ABAC check in the handler compares
/// against <c>Cleaning.AssignedHousekeeperUserId</c>.
/// </summary>
public sealed record MarkOwnCleaningInTransitCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
