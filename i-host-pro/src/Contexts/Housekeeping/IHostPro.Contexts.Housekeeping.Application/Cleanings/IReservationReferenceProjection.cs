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
    /// Ensures a local reference row exists for <paramref name="reservationId"/>
    /// — inserts a non-cancelled tombstone if absent, a no-op (NEVER
    /// touching <c>IsCancelled</c>) if a row already exists. Fase 8,
    /// Checkpoint 1.1: exists so the cross-context command
    /// <c>CreateCleaningForReservation</c> can safely materialize its own
    /// reference even when it legitimately arrives before Housekeeping's own
    /// <c>ReservationCreated</c> reaction has run — the command was born
    /// from the same real <c>ReservationCreated</c> event, so this never
    /// invents business data, only the minimal (tenantId, reservationId)
    /// identity already known to both.
    ///
    /// MUST be called from within an already-open ambient tenant-aware
    /// transaction — typically immediately after
    /// <see cref="IReservationCancellationGuard.AcquireLockAsync"/> for the
    /// SAME <paramref name="reservationId"/>, which is what makes the
    /// insert-or-no-op below race-free against a concurrent
    /// <c>ReservationCreated</c>/<c>ReservationCancelled</c> reaction doing
    /// the exact same thing for the same reservation. Never opens its own
    /// transaction, unlike <see cref="ExistsAsync"/>.
    /// </summary>
    Task EnsureExistsAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// Deterministic cancellation check (Fase 8, Checkpoint 1.1 — corrects
    /// the former best-effort guard rejected at CP1 corrective review): safe
    /// and race-free ONLY when the caller already holds
    /// <see cref="IReservationCancellationGuard"/>'s lock for the same
    /// <paramref name="reservationId"/> — under that lock, no concurrently
    /// racing <c>ReservationCancelled</c> reaction can still be in flight,
    /// so this always observes the fully settled, monotonic state. Never
    /// opens its own transaction, unlike <see cref="ExistsAsync"/> — must be
    /// called from within the same already-open ambient transaction the
    /// lock was acquired in.
    /// </summary>
    Task<bool> IsCancelledAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken);
}
