using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// <c>Started</c> → <c>Interrupted</c> (Fase 6, Incremento 1) — Documento 06
/// documents this entry transition explicitly; it documents no transition
/// back, so none exists this increment (registered as a gap in the Fase 6
/// homologation document, same treatment as Reopen).
/// </summary>
public sealed record MarkCleaningInterruptedCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
