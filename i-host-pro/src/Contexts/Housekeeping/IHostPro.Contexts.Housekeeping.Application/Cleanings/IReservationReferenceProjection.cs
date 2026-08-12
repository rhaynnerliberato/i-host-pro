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
}
