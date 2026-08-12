using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Self-service <c>Assigned</c>/<c>InTransit</c> → <c>Started</c> (Fase 6,
/// Incremento 2A). <see cref="ActorId"/> is always the caller's own
/// authenticated user id.
/// </summary>
public sealed record StartOwnCleaningCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
