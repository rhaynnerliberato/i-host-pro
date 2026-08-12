using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>Self-service <c>Started</c> → <c>InInspection</c> (Fase 6, Incremento 2A).</summary>
public sealed record StartOwnCleaningInspectionCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
