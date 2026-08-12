using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// <c>Started</c> → <c>WaitingHelp</c> (Fase 6, Incremento 1) — same
/// documented-entry/undocumented-return treatment as
/// <c>MarkCleaningInterruptedCommand</c>.
/// </summary>
public sealed record MarkCleaningWaitingHelpCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
