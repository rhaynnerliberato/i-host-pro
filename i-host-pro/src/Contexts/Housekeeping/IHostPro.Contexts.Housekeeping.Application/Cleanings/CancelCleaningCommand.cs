using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// <c>Pending</c>/<c>Assigned</c> → <c>Cancelled</c>, terminal (Fase 6,
/// Incremento 1) — Documento 06 only documents <c>Designada → Cancelada</c>
/// explicitly; cancellation from <c>Pending</c> is additionally allowed as
/// the natural, lowest-risk extension (see <c>Cleaning.Cancel</c>'s own doc
/// comment). Never valid once cleaning work has started
/// (<c>Started</c>/<c>InInspection</c> onward) — no documented transition
/// exists.
/// </summary>
public sealed record CancelCleaningCommand(Guid TenantId, Guid ActorId, Guid CleaningId) : ICommand<CleaningResult>;
