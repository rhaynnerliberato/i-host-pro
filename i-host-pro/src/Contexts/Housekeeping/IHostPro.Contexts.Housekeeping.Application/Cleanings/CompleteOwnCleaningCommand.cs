using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>Self-service <c>InInspection</c> → <c>Completed</c>, terminal (Fase 6, Incremento 2A).</summary>
public sealed record CompleteOwnCleaningCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
