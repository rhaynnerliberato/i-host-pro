namespace IHostPro.Contexts.Housekeeping.Contracts;

/// <summary>
/// The single, minimal synchronous query port Guest Operations may use to
/// evaluate an Early Check-in request's cleaning readiness (Fase 10,
/// Checkpoint 3; ADR-024 amendment — synchronous exception #8, mirroring
/// <c>Reservations.Contracts.IReservationGuestContactReader</c>'s own shape,
/// ADR-019). The FIRST synchronous exception this Bounded Context ever
/// grants — <c>Housekeeping.Contracts</c> previously published only
/// Integration Events and the cross-context command
/// <c>CreateCleaningForReservation</c>. Guest Operations' decision must
/// reflect the CURRENT cleaning state — an eventually-consistent local
/// projection could approve an early check-in against an already-stale
/// readiness signal. Implemented ONLY in
/// <c>Housekeeping.Infrastructure</c> — a consumer may reference this
/// contract, never <c>Housekeeping.Domain</c>/<c>Application</c>/
/// <c>Infrastructure</c>, and never <c>HousekeepingDbContext</c>/the
/// <c>housekeeping</c> schema directly.
/// </summary>
public interface ICleaningReadinessReader
{
    /// <summary>
    /// True only when a <c>Cleaning</c> linked to <paramref name="reservationId"/>
    /// exists AND its own <c>Status</c> is <c>Completed</c> — false both when
    /// no such Cleaning exists yet and when one exists but is not yet
    /// Completed (both cases mean the property is not ready; this method
    /// deliberately does not distinguish them further, mirroring
    /// <c>IReservationGuestContactReader</c>'s own "not found" convention of
    /// collapsing absence into a single, unambiguous negative answer).
    /// </summary>
    Task<bool> IsCleaningCompletedAsync(
        Guid tenantId,
        Guid reservationId,
        CancellationToken cancellationToken);
}
