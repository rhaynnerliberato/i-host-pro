using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Self-service delay report (Fase 6, Incremento 2A) — Documento 06
/// documents no dedicated "Atrasada" state (Checkpoint 0 matrix, Fase 6 doc
/// §21.3), so unlike every other command in this namespace this one never
/// calls a <see cref="Domain.Cleaning"/> transition method; it only audits
/// and publishes <c>CleaningDelayed</c>. Rejected only when the cleaning is
/// already terminal (<c>Completed</c>/<c>Cancelled</c>) — reporting a delay
/// on already-finished work is meaningless, not an invented business rule.
/// </summary>
public sealed record ReportOwnCleaningDelayCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
