using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Reads the current cleaning status for a Reservation (Fase 11, Checkpoint
/// 3 — AI Agent's own <c>GetCleaningStatus</c> Read Tool, Exception #3).
/// <see cref="ReservationId"/> is always backend-derived by the caller, never
/// model-supplied.
///
/// A Reservation may have more than one <c>Cleaning</c> row (only the
/// automated flow is uniqueness-guarded — see
/// <c>ICleaningReader.ExistsAutomatedForReservationAsync</c>'s own doc
/// comment; manual cleanings for the same Reservation are unconstrained).
/// When more than one exists, the tie-break is the same deterministic rule
/// already approved for this exact class of problem (Fase 11, Checkpoint 3
/// mandate item 16, Payments' own multi-charge tie-break): most recent by
/// <c>CreatedAtUtc DESC</c>, then <c>Id DESC</c> — never "best status" or any
/// other status-based priority.
/// </summary>
public sealed record GetCleaningStatusByReservationQuery(Guid ReservationId) : IQuery<CleaningStatusResult>;
