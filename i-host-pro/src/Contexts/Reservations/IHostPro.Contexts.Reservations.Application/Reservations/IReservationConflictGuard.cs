namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Guards against two concurrent Create/Update commands confirming
/// overlapping reservations for the same Property (Fase 3, Incremento 1
/// plan, item 7). Both members MUST be called from within an already-open
/// write transaction (i.e., from inside the <c>operation</c> delegate passed
/// to <see cref="ICreateReservationExecutor"/>/<see cref="IUpdateReservationExecutor"/>)
/// — <see cref="AcquirePropertyLockAsync"/>'s PostgreSQL transaction-scoped
/// advisory lock is released automatically at COMMIT/ROLLBACK, and calling
/// it outside a transaction would leak the lock until the connection itself
/// closes.
/// </summary>
public interface IReservationConflictGuard
{
    /// <summary>
    /// Serializes every concurrent Create/Update targeting the SAME
    /// (<paramref name="tenantId"/>, <paramref name="propertyId"/>) pair —
    /// the second caller blocks here until the first one's transaction
    /// commits or rolls back, so the conflict check that follows always
    /// observes every reservation the first caller confirmed. PostgreSQL
    /// <c>pg_advisory_xact_lock</c> (public API, requires no extension) —
    /// never an in-memory lock, since this must hold across separate
    /// connections/processes.
    /// </summary>
    Task AcquirePropertyLockAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken);

    /// <summary>
    /// True when a <c>Confirmed</c> reservation already exists for
    /// <paramref name="propertyId"/> whose interval overlaps
    /// [<paramref name="checkInAt"/>, <paramref name="checkOutAt"/>) — start
    /// inclusive, end exclusive, so a reservation may start exactly at
    /// another's check-out instant (Fase 3, Incremento 1 plan, item 7).
    /// <paramref name="excludeReservationId"/> excludes the reservation being
    /// updated from its own conflict check (PATCH); <c>null</c> for Create.
    /// </summary>
    Task<bool> HasConflictingReservationAsync(
        Guid tenantId, Guid propertyId, DateTimeOffset checkInAt, DateTimeOffset checkOutAt,
        Guid? excludeReservationId, CancellationToken cancellationToken);
}
