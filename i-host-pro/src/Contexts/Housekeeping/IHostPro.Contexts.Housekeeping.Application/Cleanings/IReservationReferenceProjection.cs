namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Read port over this context's own local, tenant-aware projection of
/// Reservations' <c>ReservationCreated</c>/<c>ReservationCancelled</c>
/// events (Checkpoint 0/3 gate) — used ONLY to validate that a
/// <c>reservationId</c> supplied to <c>CreateCleaningCommand</c> refers to a
/// real reservation of this tenant, NEVER to derive <c>PropertyId</c>
/// (approved decision: <c>ReservationUpdated</c> never republishes a
/// changed <c>property_id</c>, so deriving it from this projection would
/// risk silent staleness — the caller always supplies <c>PropertyId</c>
/// explicitly).
/// </summary>
public interface IReservationReferenceProjection
{
    Task<bool> ExistsAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort cancellation guard for the Workflow → Housekeeping
    /// <c>CreateCleaningForReservation</c> flow (Fase 8, Checkpoint 1 —
    /// ADR-018) — returns <c>true</c> only when this context's own
    /// <c>ReservationCancelled</c> reaction has already run for
    /// <paramref name="reservationId"/>. NEVER a consistency guarantee: the
    /// independent, unordered RabbitMQ delivery of <c>ReservationCreated</c>
    /// and <c>ReservationCancelled</c> means this can still return
    /// <c>false</c> for a Reservation that was, in fact, already cancelled —
    /// accepted risk, documented in ADR-018. Returns <c>false</c> (never
    /// throws) when no projection row exists yet for
    /// <paramref name="reservationId"/>.
    /// </summary>
    Task<bool> IsCancelledAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken);
}
