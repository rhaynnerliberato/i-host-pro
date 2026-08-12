using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

/// <summary>
/// Self-service occurrence registration (Fase 6, Incremento 2A) —
/// <see cref="ActorId"/> is always the caller's own authenticated user id,
/// also the housekeeper identity the ABAC check compares against
/// <c>Cleaning.AssignedHousekeeperUserId</c>. <see cref="Type"/> is one of
/// <c>OccurrenceTypeCodeMapper</c>'s stable codes.
/// </summary>
public sealed record RegisterCleaningOccurrenceCommand(
    Guid TenantId, Guid ActorId, Guid CleaningId, string Type, string? Description) : ICommand<CleaningOccurrenceResult>;
