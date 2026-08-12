using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>Self-service <c>Started</c> → <c>WaitingHelp</c> (Fase 6, Incremento 2A).</summary>
public sealed record MarkOwnCleaningWaitingHelpCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
