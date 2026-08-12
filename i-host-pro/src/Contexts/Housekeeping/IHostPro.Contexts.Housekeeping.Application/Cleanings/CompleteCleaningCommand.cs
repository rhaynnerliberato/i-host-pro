using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary><c>InInspection</c> → <c>Completed</c>, terminal (Fase 6, Incremento 1).</summary>
public sealed record CompleteCleaningCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
