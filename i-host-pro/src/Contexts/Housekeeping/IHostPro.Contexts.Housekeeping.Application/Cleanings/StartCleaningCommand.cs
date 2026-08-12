using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Marks cleaning work as begun — <c>Assigned</c> → <c>Started</c> (Fase 6,
/// Incremento 1; the optional <c>InTransit</c> step is skipped by design
/// this increment).
/// </summary>
public sealed record StartCleaningCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
