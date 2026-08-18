namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// The real serialization point protecting a single Reservation's local
/// reference (<c>IReservationReferenceProjection</c>) against concurrent
/// mutation by the three flows that can race for it (Fase 8, Checkpoint 1.1
/// — corrective homologation of ADR-018's cancellation-race blocker):
/// <c>ReservationCreated</c>'s own projection reaction, <c>ReservationCancelled</c>'s
/// projection-and-auto-cancel reaction, and the cross-context command
/// <c>CreateCleaningForReservation</c>. Mirrors
/// <c>Reservations.Infrastructure.ReservationConflictGuard.AcquirePropertyLockAsync</c>'s
/// own <c>pg_advisory_xact_lock</c> pattern exactly — a genuine PostgreSQL
/// primitive, not an in-process guard, so it correctly serializes access even
/// though the three callers above are resolved from entirely separate
/// <c>IServiceScopeFactory</c> child scopes (ADR-015/016), each with its own
/// <c>HousekeepingDbContext</c> instance and connection.
///
/// MUST be called as the very FIRST statement inside an already-open,
/// tenant-aware write transaction (i.e., from within
/// <c>IHousekeepingTransactionExecutor.ExecuteAsync</c>'s operation delegate)
/// — the lock is transaction-scoped (<c>_xact_</c>), released automatically
/// at COMMIT/ROLLBACK, never needing an explicit unlock call. Deliberately
/// does NOT require the local reference row to already exist: the lock key
/// is derived only from <c>(tenantId, reservationId)</c>, so it can be
/// acquired before the row is ever created — which is exactly what lets a
/// <c>CreateCleaningForReservation</c> command safely materialize the
/// reference itself when it legitimately arrives before Housekeeping's own
/// <c>ReservationCreated</c> projection has (see
/// <see cref="IReservationReferenceProjection.EnsureExistsAsync"/>).
/// </summary>
public interface IReservationCancellationGuard
{
    Task AcquireLockAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken);
}
