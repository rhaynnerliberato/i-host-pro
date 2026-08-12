using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary><c>Started</c> → <c>InInspection</c> (Fase 6, Incremento 1).</summary>
public sealed record StartCleaningInspectionCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
